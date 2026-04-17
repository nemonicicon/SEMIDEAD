using System.Collections;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SEMIDEAD;

public enum PowerUpType { InstaKill, Nuke, MaxAmmo, DoublePoints }

/// <summary>
/// Manages all active power-up states. Spawns PowerUpOrbs on wave enemy death.
/// All effect logic runs host-side.
///
/// Power-up effects:
///   InstaKill    — 30 s. EnemyHealthPatch forces EnemyHealth.Hurt damage = 9999.
///   Nuke         — Instant. Calls Hurt(9999) on every active WaveEnemyTag enemy.
///   MaxAmmo      — Instant. Restores ItemBattery charge on all live items.
///   DoublePoints — 30 s. WaveKillReward multiplies SURPLUS by 2.
/// </summary>
public class PowerUpManager : MonoBehaviour
{
    public static PowerUpManager? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    public static bool InstaKillActive    { get; private set; }
    public static bool DoublePointsActive { get; private set; }

    private const float TimedEffectDuration = 30f;
    private const float OrbDropChance       = 0.10f;

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public static PowerUpManager Create()
    {
        var go = new GameObject("SEMIDEAD_PowerUpManager");
        return go.AddComponent<PowerUpManager>();
    }

    // ---------------------------------------------------------------------------
    // Color / size helpers
    // ---------------------------------------------------------------------------

    internal static Color OrbColor(PowerUpType t) => t switch
    {
        PowerUpType.InstaKill    => new Color(1f, 0.25f, 0.25f),
        PowerUpType.Nuke         => new Color(1f, 0.5f,  0f),
        PowerUpType.MaxAmmo      => new Color(0.2f, 1f,  0.2f),
        PowerUpType.DoublePoints => new Color(1f, 0.85f, 0f),
        _                        => Color.white,
    };

    // ---------------------------------------------------------------------------
    // Orb drop — spawns the game's native enemy valuable (soul orb) via Photon so
    // all clients see it. Size varies by power-up type. Native drops are suppressed
    // in WaveSpawner so orbs only appear on this 10% roll.
    // ---------------------------------------------------------------------------

    public static void TryDropOrb(Vector3 deathPos)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        float roll = Random.value;
        if (roll > OrbDropChance)
        {
            Logger.LogInfo($"[PowerUpManager] Orb roll {roll:F2} > {OrbDropChance} — no drop.");
            return;
        }

        if (Instance == null) { Logger.LogWarning("[PowerUpManager] Instance null — skipping orb."); return; }

        var assetMgr = AssetManager.instance;
        if (assetMgr == null)
        {
            Logger.LogWarning("[PowerUpManager] AssetManager null — skipping orb drop.");
            return;
        }

        PowerUpType type = (PowerUpType)Random.Range(0, 4);

        // Map power-up type to orb size so clients see a visual difference.
        GameObject? prefab = type switch
        {
            PowerUpType.Nuke      => assetMgr.enemyValuableBig,
            PowerUpType.InstaKill => assetMgr.enemyValuableMedium,
            _                     => assetMgr.enemyValuableSmall,
        };

        if (prefab == null)
        {
            Logger.LogWarning("[PowerUpManager] Enemy valuable prefab null — skipping orb drop.");
            return;
        }

        string path     = "Valuables/" + prefab.name;
        Vector3 spawnPos = deathPos + Vector3.up * 0.5f;
        Logger.LogInfo($"[PowerUpManager] Dropping {type} orb — path '{path}', pos {spawnPos}.");

        GameObject? go;
        if (SemiFunc.IsMultiplayer())
            go = PhotonNetwork.InstantiateRoomObject(path, spawnPos, Quaternion.identity);
        else
            go = Object.Instantiate(prefab, spawnPos, Quaternion.identity);

        if (go == null) { Logger.LogWarning($"[PowerUpManager] Orb spawn returned null (path='{path}')."); return; }

        var orb  = go.AddComponent<PowerUpOrb>();
        orb.Type = type;
        Logger.LogInfo($"[PowerUpManager] Orb spawned — {type}, PowerUpOrb component added.");
    }

    // ---------------------------------------------------------------------------
    // TTS — every player speaks the power-up name simultaneously.
    // This is the world-level announcement: all clients hear all players say
    // "MAX AMMO", "INSTA KILL", etc. through their own Photon Voice streams.
    // ---------------------------------------------------------------------------

    private static void SpeakAllPlayers(string label)
    {
        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        foreach (PlayerAvatar player in players)
        {
            if (player == null) continue;

            if (!SemiFunc.IsMultiplayer())
                player.voiceChat?.ttsVoice?.TTSSpeakNow(label, false);
            else
            {
                var pv = player.GetComponent<PhotonView>();
                if (pv != null)
                    pv.RPC("ChatMessageSendRPC", RpcTarget.All, new object[] { label, false });
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Activation
    // ---------------------------------------------------------------------------

    public void ActivatePowerUp(PowerUpType type, PlayerAvatar? triggeredBy = null)
    {
        Logger.LogInfo($"[PowerUpManager] Activating {type}.");
        SpeakAllPlayers(GetLabel(type));
        Color orbColor = OrbColor(type);
        switch (type)
        {
            case PowerUpType.InstaKill:
                Announcer.SendBigMessage(GetLabel(type), "", 1f, orbColor, Color.white);
                StartCoroutine(TimedEffect(type, TimedEffectDuration,
                    () => InstaKillActive = true, () => InstaKillActive = false));
                break;
            case PowerUpType.Nuke:
                Announcer.SendBigMessage("NUKE", "", 1f, Color.red, Color.white);
                ApplyNuke();
                WaveHUD.ShowPowerUpActivated(GetLabel(PowerUpType.Nuke), orbColor, 0f);
                break;
            case PowerUpType.MaxAmmo:
                Announcer.SendBigMessage(GetLabel(type), "", 1f, orbColor, Color.white);
                ApplyMaxAmmo();
                WaveHUD.ShowPowerUpActivated(GetLabel(PowerUpType.MaxAmmo), orbColor, 0f);
                break;
            case PowerUpType.DoublePoints:
                Announcer.SendBigMessage(GetLabel(type), "", 1f, orbColor, Color.white);
                StartCoroutine(TimedEffect(type, TimedEffectDuration,
                    () => DoublePointsActive = true, () => DoublePointsActive = false));
                break;
        }
    }

    private IEnumerator TimedEffect(PowerUpType type, float duration,
                                    System.Action activate, System.Action deactivate)
    {
        activate();
        WaveHUD.ShowPowerUpActivated(GetLabel(type), OrbColor(type), duration);
        Logger.LogInfo($"[PowerUpManager] {type} active for {duration}s.");
        yield return new WaitForSeconds(duration);
        deactivate();
        WaveHUD.ShowPowerUpExpired(GetLabel(type), OrbColor(type));
        Logger.LogInfo($"[PowerUpManager] {type} expired.");
    }

    // ---------------------------------------------------------------------------
    // Nuke
    // ---------------------------------------------------------------------------

    private static void ApplyNuke()
    {
        Logger.LogInfo("[PowerUpManager] NUKE — killing all wave enemies.");
        int killed = 0;
        foreach (var tag in Object.FindObjectsOfType<WaveEnemyTag>())
        {
            EnemyHealth? health = tag.GetComponentInChildren<EnemyHealth>();
            if (health != null) { health.Hurt(9999, Vector3.zero); killed++; }
        }
        Logger.LogInfo($"[PowerUpManager] Nuke hit {killed} wave enemies.");
    }

    // ---------------------------------------------------------------------------
    // Max Ammo
    // ---------------------------------------------------------------------------

    private static void ApplyMaxAmmo()
    {
        Logger.LogInfo("[PowerUpManager] MAX AMMO — restoring all ItemBattery components.");
        int count = 0;
        foreach (ItemBattery battery in Object.FindObjectsOfType<ItemBattery>())
        {
            // Skip batteries with no registered instanceName — SetBatteryLife calls
            // StatsManager.ItemUpdateStatBattery which throws ArgumentNullException if
            // instanceName is null (happens with dynamically-spawned items like MonkeyBomb
            // or WallBuy guns that haven't completed StatsManager registration).
            var attrs = battery.GetComponent<ItemAttributes>();
            if (attrs == null) continue;
            var name = Traverse.Create(attrs).Field("instanceName").GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;

            if (battery.batteryLife <= 0f) battery.batteryLife = 1f;
            battery.SetBatteryLife(100);
            count++;
        }
        Logger.LogInfo($"[PowerUpManager] Max Ammo restored {count} battery/batteries.");
    }

    // ---------------------------------------------------------------------------
    // Labels
    // ---------------------------------------------------------------------------

    private static string GetLabel(PowerUpType type) => type switch
    {
        PowerUpType.InstaKill    => "INSTA KILL",
        PowerUpType.Nuke         => "NUKE",
        PowerUpType.MaxAmmo      => "MAX AMMO",
        PowerUpType.DoublePoints => "DOUBLE POINTS",
        _                        => type.ToString().ToUpper(),
    };
}
