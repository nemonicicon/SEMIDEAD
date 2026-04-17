using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;

namespace SEMIDEAD;

/// <summary>
/// One Mystery Box per gameplay level at a random non-truck LevelPoint.
///
/// Interaction model (host-only mod constraint):
///   Host player   — E key detected via SemiFunc.InputDown(InputKey.Interact).
///   Remote players — auto-trigger after RemoteDwellTime seconds in range
///                    (can't read client input without a client mod).
///
/// Flow:
///   1. PlaceBoxAfterLoad() — waits for LevelGenerator, picks position, syncs to room props.
///   2. Update() — proximity check all players; E-key for host player, dwell for remotes.
///   3. ActivateSequence() — deducts $500, shows 3s countdown to all clients, then:
///        80% → PickRandomGun() → SpawnWeaponAboveBox() → drops to floor via physics.
///        20% → TeleportBox() → new random non-truck position.
/// </summary>
public class MysteryBox : MonoBehaviour
{
    public static MysteryBox? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private const int   Cost            = 500;
    private const float InteractRadius  = 2.5f;
    private const float TeddyBearChance = 0.20f;
    private const float RemoteDwellTime = 1.5f; // auto-trigger for non-host players

    private Vector3    _boxPos;
    private bool       _active = false;
    private bool       _locked = false; // true during 3-second sequence
    private GameObject? _boxVisual;     // spawned at _boxPos so players can see the box

    /// <summary>Current box world position — used by WallBuy to avoid co-location.</summary>
    public static Vector3? ActiveBoxPosition =>
        (Instance != null && Instance._active) ? Instance._boxPos : (Vector3?)null;

    // Dwell timers for remote players (no E-key detection possible on their machines).
    private readonly Dictionary<PlayerAvatar, float> _dwellTimers = new();

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

    public static MysteryBox Create()
    {
        var go = new GameObject("SEMIDEAD_MysteryBox");
        return go.AddComponent<MysteryBox>();
    }

    // ---------------------------------------------------------------------------
    // Level setup — called from RunManagerPatch.ChangeLevel_Postfix
    // ---------------------------------------------------------------------------

    public void OnLevelSetup()
    {
        _active = false;
        _locked = false;
        _dwellTimers.Clear();
        WaveHUD.ClearBuyPrompt();
        DestroyVisual();

        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!IsGameplayLevel()) return;
        StartCoroutine(PlaceBoxAfterLoad());
    }

    private IEnumerator PlaceBoxAfterLoad()
    {
        while (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            yield return null;
        if (!IsGameplayLevel()) yield break;

        // Poll until ≥2 non-truck points exist or 12s elapses.
        float waited = 0f;
        List<LevelPoint>? allPts = null;
        do
        {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
            allPts = SemiFunc.LevelPointsGetAll();
        }
        while (IsGameplayLevel() && waited < 12f && NonTruckCount(allPts) < 2);

        Logger.LogInfo($"[MysteryBox] LevelPointsGetAll returned {allPts?.Count ?? 0} point(s) " +
                       $"({NonTruckCount(allPts)} non-truck) after {waited:F1}s.");

        if (!IsGameplayLevel()) yield break;

        Vector3? pos = PickBoxPosition(allPts);
        if (pos == null)
        {
            Logger.LogWarning("[MysteryBox] No valid position found — box not placed.");
            yield break;
        }

        _boxPos = pos.Value;
        _active = true;

        if (SemiFunc.IsMultiplayer())
            SyncToRoomProperties();

        SpawnVisual();
        Logger.LogInfo($"[MysteryBox] Box placed at {_boxPos}.");
    }

    // ---------------------------------------------------------------------------
    // Per-frame update
    // ---------------------------------------------------------------------------

    private void Update()
    {
        if (!_active || !SemiFunc.IsMasterClientOrSingleplayer() || _locked) return;

        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        bool anyNear      = false;
        bool hostNear     = false;
        bool canAfford    = (StatsManager.instance?.GetRunStatCurrency() ?? 0) >= Cost;

        foreach (PlayerAvatar player in players)
        {
            if (player == null || player.isDisabled) continue;

            bool near = (player.transform.position - _boxPos).sqrMagnitude
                        <= InteractRadius * InteractRadius;

            if (!near)
            {
                _dwellTimers.Remove(player);
                continue;
            }

            anyNear = true;

            // Identify host's local player.
            var pv = player.GetComponent<PhotonView>();
            bool isLocal = pv == null || pv.IsMine;

            if (isLocal)
            {
                hostNear = true;
                // E-key: single-frame press, no auto-trigger.
                if (SemiFunc.InputDown(InputKey.Interact))
                {
                    StartCoroutine(ActivateSequence(player));
                    return; // sequence now running — stop checking this frame
                }
            }
            else
            {
                // Remote player — accumulate dwell, auto-trigger when ready.
                if (!_dwellTimers.TryGetValue(player, out float dwell))
                    dwell = 0f;
                dwell += Time.deltaTime;
                _dwellTimers[player] = dwell;

                if (dwell >= RemoteDwellTime)
                {
                    _dwellTimers.Remove(player);
                    StartCoroutine(ActivateSequence(player));
                    return;
                }
            }
        }

        // WaveHUD prompt — visible to host only.
        if (anyNear)
        {
            string colTag = canAfford ? "<color=#00ff88>" : "<color=#ff4444>";
            string hint   = hostNear ? "Press E" : "Stand near to use";
            WaveHUD.ShowBuyPrompt($"{colTag}MYSTERY BOX  —  {hint}  —  ${Cost}</color>");
        }
        else
        {
            WaveHUD.ClearBuyPrompt();
        }
    }

    // ---------------------------------------------------------------------------
    // 3-second activation sequence
    // ---------------------------------------------------------------------------

    private IEnumerator ActivateSequence(PlayerAvatar player)
    {
        var stats = StatsManager.instance;
        if (stats == null) { _locked = false; yield break; }

        int balance = stats.GetRunStatCurrency();
        if (balance < Cost)
        {
            Logger.LogInfo($"[MysteryBox] {player.playerName} — not enough SURPLUS ({balance}/{Cost}).");
            WaveHUD.ShowBuyPrompt("<color=#ff4444>NOT ENOUGH SURPLUS</color>");
            yield break;
        }

        _locked = true;
        _dwellTimers.Clear(); // cancel any other pending remote triggers

        // Deduct cost immediately on activation.
        int newBalance = balance - Cost;
        stats.runStats["currency"] = newBalance;
        PunManager.instance.UpdateStat("runStats", "currency", newBalance);
        Logger.LogInfo($"[MysteryBox] {player.playerName} spent ${Cost}. Balance: ${newBalance}.");

        // 3-second anticipation — countdown visible to all clients via Announcer.
        WaveHUD.ShowBuyPrompt("<color=#00ccff>MYSTERY BOX SPINNING...</color>");
        Announcer.SendFocusText("MYSTERY BOX...", new Color(0f, 0.9f, 1f), Color.white, 3f);

        yield return new WaitForSeconds(3f);

        WaveHUD.ClearBuyPrompt();

        if (Random.value < TeddyBearChance)
        {
            Logger.LogInfo("[MysteryBox] Teddy Bear — teleporting box.");
            Announcer.SendBigMessage("TEDDY BEAR!", "", 1f, new Color(0.9f, 0.6f, 0.2f), Color.white);
            TeleportBox();
        }
        else
        {
            Item? item = PickRandomGun();
            if (item != null)
            {
                SpawnWeaponAboveBox(item);
                Announcer.SendFocusText(item.itemName.ToUpper(), Color.white, Color.cyan, 3f);
                Logger.LogInfo($"[MysteryBox] Spawned {item.itemName} at {_boxPos}.");
            }
        }

        _locked = false;
    }

    // ---------------------------------------------------------------------------
    // Gun selection and spawning
    // ---------------------------------------------------------------------------

    private static Item? PickRandomGun()
    {
        var dict = StatsManager.instance?.itemDictionary;
        if (dict == null) return null;

        var pool = new List<Item>();
        foreach (Item entry in dict.Values)
            if (entry.itemType == SemiFunc.itemType.gun) pool.Add(entry);

        if (pool.Count == 0)
        {
            Logger.LogWarning("[MysteryBox] Gun pool empty — no weapons in itemDictionary.");
            return null;
        }

        return pool[Random.Range(0, pool.Count)];
    }

    private void SpawnWeaponAboveBox(Item item)
    {
        // Spawn 1.5 units above box — drops to floor via physics.
        Vector3 spawnPos = _boxPos + Vector3.up * 1.5f;

        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, spawnPos, Quaternion.identity);
        else
        {
            var prefab = item.prefab.Prefab;
            if (prefab != null)
                Object.Instantiate(prefab, spawnPos, Quaternion.identity);
            else
                Logger.LogWarning($"[MysteryBox] prefab.Prefab null for '{item.itemName}'.");
        }
    }

    // ---------------------------------------------------------------------------
    // Teddy bear — teleport box to new random non-truck position
    // ---------------------------------------------------------------------------

    private void TeleportBox()
    {
        Vector3? newPos = PickBoxPosition(SemiFunc.LevelPointsGetAll());
        if (newPos == null)
        {
            Logger.LogWarning("[MysteryBox] TeleportBox — no valid position found.");
            return;
        }

        _boxPos = newPos.Value;

        if (SemiFunc.IsMultiplayer())
            SyncToRoomProperties();

        DestroyVisual();
        SpawnVisual();
        CharacterSystem.Instance?.TriggerSpeechForOneRandom(SpeechTrigger.TeddyBear);
        Logger.LogInfo($"[MysteryBox] Box teleported to {_boxPos}.");
    }

    // ---------------------------------------------------------------------------
    // Position selection — excludes truck LevelPoints, NavMesh-snaps Y (2f radius)
    // ---------------------------------------------------------------------------

    private static Vector3? PickBoxPosition(List<LevelPoint>? pts)
    {
        if (pts == null || pts.Count == 0) return null;

        var candidates = new List<LevelPoint>(pts.Count);
        foreach (LevelPoint pt in pts)
            if (!pt.Truck) candidates.Add(pt);
        if (candidates.Count == 0) candidates = pts;

        Vector3 pos = candidates[Random.Range(0, candidates.Count)].transform.position;

        if (NavMesh.SamplePosition(pos, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            pos = hit.position;
        else
            Logger.LogWarning($"[MysteryBox] NavMesh snap failed at {pos} — using raw LevelPoint Y.");

        return pos;
    }

    // ---------------------------------------------------------------------------
    // Room property sync — lets clients know box position
    // ---------------------------------------------------------------------------

    private void SyncToRoomProperties()
    {
        PhotonNetwork.CurrentRoom?.SetCustomProperties(
            new ExitGames.Client.Photon.Hashtable
            {
                { "MBPos", $"{_boxPos.x:F3}|{_boxPos.y:F3}|{_boxPos.z:F3}" }
            });
        Logger.LogInfo("[MysteryBox] Synced box position to room properties.");
    }

    // ---------------------------------------------------------------------------
    // Visual — a glowing orb spawned at _boxPos so players can locate the box.
    // Uses enemyValuableMedium (same path as power-up orbs — guaranteed valid).
    // Destroyed and re-spawned on Teddy Bear teleport.
    // ---------------------------------------------------------------------------

    private void SpawnVisual()
    {
        var assetMgr = AssetManager.instance;
        if (assetMgr?.enemyValuableMedium == null)
        {
            Logger.LogWarning("[MysteryBox] AssetManager.enemyValuableMedium null — box will be invisible.");
            return;
        }

        string path = "Valuables/" + assetMgr.enemyValuableMedium.name;
        Logger.LogInfo($"[MysteryBox] Spawning visual at {_boxPos} (path='{path}').");

        if (SemiFunc.IsMultiplayer())
            _boxVisual = PhotonNetwork.InstantiateRoomObject(path, _boxPos, Quaternion.identity);
        else
            _boxVisual = Object.Instantiate(assetMgr.enemyValuableMedium, _boxPos, Quaternion.identity);

        if (_boxVisual == null)
            Logger.LogWarning("[MysteryBox] Visual spawn returned null — box will be invisible.");
    }

    private void DestroyVisual()
    {
        if (_boxVisual == null) return;
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.Destroy(_boxVisual);
        else
            Object.Destroy(_boxVisual);
        _boxVisual = null;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static int NonTruckCount(List<LevelPoint>? pts)
    {
        if (pts == null) return 0;
        int n = 0;
        foreach (var p in pts) if (!p.Truck) n++;
        return n;
    }

    private static bool IsGameplayLevel()
    {
        var rm = RunManager.instance;
        return rm != null && rm.levelCurrent != null && rm.levels != null
               && rm.levels.Contains(rm.levelCurrent);
    }
}
