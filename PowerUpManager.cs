using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
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

    private const byte OrbSpawnEventCode   = 44;
    private const byte OrbDestroyEventCode = 45;

    private static int _orbIdCounter = 0;
    private static readonly Dictionary<int, GameObject> _clientOrbs = new();
    private static OrbVisualListener? _orbListener;

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
    // Orb visual sync — Photon events 44 (spawn) and 45 (destroy) keep a local
    // colored sphere visible on all clients. No room object needed; pickup logic
    // runs host-side only. Pattern mirrors ShotgunExplosionPatch (event 43).
    // ---------------------------------------------------------------------------

    public static void RegisterOrbListener()
    {
        if (_orbListener != null) return;
        if (!PhotonNetwork.IsConnected) return;
        _orbListener = new OrbVisualListener();
        PhotonNetwork.AddCallbackTarget(_orbListener);
    }

    internal static void RaiseOrbDestroy(int orbId)
    {
        if (!SemiFunc.IsMultiplayer()) return;
        PhotonNetwork.RaiseEvent(
            OrbDestroyEventCode,
            orbId,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
    }

    private class OrbVisualListener : IOnEventCallback
    {
        public void OnEvent(EventData ev)
        {
            if (SemiFunc.IsMasterClientOrSingleplayer()) return; // host handles its own visuals

            if (ev.Code == OrbSpawnEventCode)
            {
                var data   = (object[])ev.CustomData;
                int orbId  = (int)data[0];
                var pos    = (Vector3)data[1];
                var type   = (PowerUpType)(int)data[2];

                var go = new GameObject($"SEMIDEAD_PowerUpOrb_Client_{orbId}");
                go.transform.position = pos;
                AddOrbVisual(go, type);
                _clientOrbs[orbId] = go;
                Logger.LogInfo($"[PowerUpManager] Client orb {orbId} ({type}) visual spawned at {pos}.");
            }
            else if (ev.Code == OrbDestroyEventCode)
            {
                int orbId = (int)ev.CustomData;
                if (_clientOrbs.TryGetValue(orbId, out var go))
                {
                    Object.Destroy(go);
                    _clientOrbs.Remove(orbId);
                    Logger.LogInfo($"[PowerUpManager] Client orb {orbId} visual destroyed.");
                }
            }
        }
    }

    private static void AddOrbVisual(GameObject parent, PowerUpType type)
    {
        Color color = OrbColor(type);

        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.Destroy(sphere.GetComponent<Collider>());
        sphere.transform.SetParent(parent.transform);
        sphere.transform.localPosition = Vector3.zero;
        sphere.transform.localScale    = Vector3.one * 0.4f;
        var mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", color * 2f);
        sphere.GetComponent<MeshRenderer>().material = mat;

        var lightGo = new GameObject("OrbLight");
        lightGo.transform.SetParent(parent.transform);
        lightGo.transform.localPosition = Vector3.zero;
        var lt = lightGo.AddComponent<Light>();
        lt.type      = LightType.Point;
        lt.color     = color;
        lt.intensity = 2f;
        lt.range     = 4f;
    }

    // ---------------------------------------------------------------------------
    // Orb drop — creates a local host-side orb with visual, then broadcasts the
    // visual to clients via Photon event 44. No room object, no valuable prefab.
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

        PowerUpType type    = (PowerUpType)Random.Range(0, 4);
        int         orbId   = _orbIdCounter++;
        Vector3     spawnPos = deathPos + Vector3.up * 0.5f;

        Logger.LogInfo($"[PowerUpManager] Dropping {type} orb (id={orbId}) at {spawnPos}.");

        var go = new GameObject($"SEMIDEAD_PowerUpOrb_{orbId}");
        go.transform.position = spawnPos;
        AddOrbVisual(go, type);
        var orb = go.AddComponent<PowerUpOrb>();
        orb.Type  = type;
        orb.OrbId = orbId;

        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.RaiseEvent(
                OrbSpawnEventCode,
                new object[] { orbId, spawnPos, (int)type },
                new RaiseEventOptions { Receivers = ReceiverGroup.Others },
                SendOptions.SendReliable);

        Logger.LogInfo($"[PowerUpManager] Orb {orbId} ({type}) spawned.");
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
