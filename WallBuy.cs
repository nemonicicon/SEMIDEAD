using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;

namespace SEMIDEAD;

/// <summary>
/// Spawns weapon purchase zones at random NavMesh-valid LevelPoints each gameplay level.
///
/// Flow:
///   - OnLevelSetup() places StationCount zones after the level generates.
///   - Each zone picks a random gun from itemDictionary (same pool as MysteryBox).
///   - Host polls all player positions each frame.
///   - Player entering a zone sees a WaveHUD prompt showing the gun name + cost.
///   - After DwellTime seconds in the zone, SURPLUS is deducted and the gun spawns
///     at the player's feet. PlayerCooldown prevents instant re-purchase.
///
/// Host-only: proximity detection and SURPLUS deduction are authoritative on the host.
/// WaveHUD buy prompt is only visible to the host player.
/// Gun spawning uses PhotonNetwork.InstantiateRoomObject so all clients see it.
/// </summary>
public class WallBuy : MonoBehaviour
{
    public static WallBuy? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private const int   GunCost       = 500;
    private const float BuyRadius     = 3f;
    private const float DwellTime     = 2f;
    private const float PlayerCooldown = 10f;
    private const int   StationCount  = 3;

    private readonly struct Station
    {
        public readonly Vector3 Position;
        public readonly Item    GunItem;
        public Station(Vector3 pos, Item gun) { Position = pos; GunItem = gun; }
    }

    private readonly List<Station>                       _stations   = new();
    private readonly Dictionary<PlayerAvatar, float>     _dwellTimers = new();
    private readonly Dictionary<PlayerAvatar, float>     _cooldowns   = new();
    private bool _stationsSynced = false;

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

    public static WallBuy Create()
    {
        var go = new GameObject("SEMIDEAD_WallBuy");
        return go.AddComponent<WallBuy>();
    }

    // ---------------------------------------------------------------------------
    // Level setup — called from RunManagerPatch.ChangeLevel_Postfix
    // ---------------------------------------------------------------------------

    public void OnLevelSetup()
    {
        _stations.Clear();
        _dwellTimers.Clear();
        _cooldowns.Clear();
        _stationsSynced = false;
        WaveHUD.ClearBuyPrompt();

        // Wall buy temporarily disabled — uncomment below to re-enable.
        return;

        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!IsGameplayLevel()) return;

        StartCoroutine(PlaceStationsAfterLoad());
    }

    private IEnumerator PlaceStationsAfterLoad()
    {
        while (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            yield return null;
        if (!IsGameplayLevel()) yield break;

        // LevelPoints are populated asynchronously after Generated becomes true.
        // Poll until at least 2 non-truck points exist or 12s elapses.
        float waited = 0f;
        List<LevelPoint> all;
        do
        {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
            all = SemiFunc.LevelPointsGetAll();
        }
        while (IsGameplayLevel() && waited < 12f && NonTruckCount(all) < 2);

        if (!IsGameplayLevel()) yield break;

        // Build gun pool.
        var guns = new List<Item>();
        var dict = StatsManager.instance?.itemDictionary;
        if (dict != null)
            foreach (Item entry in dict.Values)
                if (entry.itemType == SemiFunc.itemType.gun) guns.Add(entry);

        if (guns.Count == 0)
        {
            Logger.LogWarning("[WallBuy] No gun items in itemDictionary — no stations placed.");
            yield break;
        }

        if (all == null || all.Count == 0)
        {
            Logger.LogWarning("[WallBuy] No LevelPoints found after polling.");
            yield break;
        }

        var candidates = new List<LevelPoint>(all.Count);
        foreach (LevelPoint pt in all)
            if (!pt.Truck) candidates.Add(pt);

        Logger.LogInfo($"[WallBuy] LevelPoints: {all.Count} total, {candidates.Count} non-truck (after {waited:F1}s).");

        if (candidates.Count == 0) candidates = all;

        // Fisher-Yates shuffle for random selection.
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        // Collect occupied positions to enforce minimum spacing.
        // Starts with the MysteryBox so wall buy stations don't spawn on top of it.
        const float MinSpacingSq = 25f; // 5 unit minimum (reduced from 10 — levels may be compact)
        var occupied = new List<Vector3>();
        if (MysteryBox.ActiveBoxPosition.HasValue)
        {
            occupied.Add(MysteryBox.ActiveBoxPosition.Value);
            Logger.LogInfo($"[WallBuy] MysteryBox occupied slot at {MysteryBox.ActiveBoxPosition.Value}.");
        }

        int placed = 0;
        int tried  = 0;
        foreach (LevelPoint pt in candidates)
        {
            if (placed >= StationCount) break;
            tried++;
            Vector3 pos = pt.transform.position;

            // Snap Y to NavMesh if possible; fall back to raw LevelPoint position.
            bool navHit = NavMesh.SamplePosition(pos, out NavMeshHit hit, 10f, NavMesh.AllAreas);
            if (navHit) pos = hit.position;

            // Skip if too close to MysteryBox or another station.
            bool tooClose = false;
            foreach (Vector3 occ in occupied)
            {
                float dSq = (pos - occ).sqrMagnitude;
                if (dSq < MinSpacingSq) { tooClose = true; break; }
            }

            Logger.LogInfo($"[WallBuy] Candidate {tried}: pos={pos} navHit={navHit} tooClose={tooClose}");

            if (tooClose) continue;

            occupied.Add(pos);
            Item gun = guns[Random.Range(0, guns.Count)];
            _stations.Add(new Station(pos, gun));
            Logger.LogInfo($"[WallBuy] Station {placed + 1}: {gun.itemName} at {pos}");
            placed++;
        }

        Logger.LogInfo($"[WallBuy] {placed} station(s) placed (tried {tried} candidate(s)).");

        if (SemiFunc.IsMultiplayer())
            SyncStationsToRoomProperties();
    }

    // ---------------------------------------------------------------------------
    // Per-frame proximity check (host-only)
    // ---------------------------------------------------------------------------

    private void Update()
    {
        if (SemiFunc.IsMasterClientOrSingleplayer())
            HostUpdate();
        else if (SemiFunc.IsMultiplayer())
            ClientHudUpdate();
    }

    // Full host logic: proximity detection, dwell timers, purchase, HUD.
    private void HostUpdate()
    {
        if (_stations.Count == 0) return;

        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        PlayerAvatar? promptPlayer    = null;
        int           promptStation   = -1;
        float         promptDwell     = 0f;

        foreach (PlayerAvatar player in players)
        {
            if (player == null || player.isDisabled) continue;

            int nearStation = GetNearestStation(player.transform.position);

            if (nearStation < 0)
            {
                _dwellTimers.Remove(player);
                continue;
            }

            // Skip if on cooldown.
            if (_cooldowns.TryGetValue(player, out float coolUntil) && Time.time < coolUntil)
                continue;

            if (!_dwellTimers.TryGetValue(player, out float dwell))
                dwell = 0f;
            dwell += Time.deltaTime;
            _dwellTimers[player] = dwell;

            // Track whichever player has been dwelling longest for the HUD prompt.
            if (dwell > promptDwell)
            {
                promptDwell   = dwell;
                promptPlayer  = player;
                promptStation = nearStation;
            }

            if (dwell >= DwellTime)
            {
                _dwellTimers.Remove(player);
                _cooldowns[player] = Time.time + PlayerCooldown;
                Purchase(player, nearStation);
            }
        }

        // Update WaveHUD buy prompt for whichever player is deepest in a zone.
        if (promptPlayer != null && promptStation >= 0 && promptStation < _stations.Count)
        {
            Station s = _stations[promptStation];
            float remaining = Mathf.Max(0f, DwellTime - promptDwell);
            WaveHUD.ShowBuyPrompt(
                $"WALL BUY: {s.GunItem.itemName.ToUpper()}  —  ${GunCost}  ({Mathf.CeilToInt(remaining)}s)");
        }
        else
        {
            WaveHUD.ClearBuyPrompt();
        }
    }

    // Non-host client: load station positions from room properties (once per level),
    // then show/clear the buy prompt based on the local player's position.
    // Purchase logic stays host-only; this is HUD display only.
    private void ClientHudUpdate()
    {
        if (!_stationsSynced)
            TryLoadStationsFromRoomProperties();

        if (_stations.Count == 0) return;

        // Find the local player (the one owned by this Photon client).
        PlayerAvatar? localPlayer = null;
        var players = SemiFunc.PlayerGetList();
        if (players == null) return;
        foreach (PlayerAvatar p in players)
        {
            if (p == null) continue;
            var pv = p.GetComponent<PhotonView>();
            if (pv != null && pv.IsMine) { localPlayer = p; break; }
        }

        if (localPlayer == null || localPlayer.isDisabled)
        {
            WaveHUD.ClearBuyPrompt();
            return;
        }

        int nearStation = GetNearestStation(localPlayer.transform.position);
        if (nearStation < 0)
        {
            WaveHUD.ClearBuyPrompt();
            return;
        }

        Station s = _stations[nearStation];
        WaveHUD.ShowBuyPrompt($"WALL BUY: {s.GunItem.itemName.ToUpper()}  —  ${GunCost}");
    }

    // Serialise station positions + gun names into a Photon room property
    // so non-host clients can load them for HUD display.
    private void SyncStationsToRoomProperties()
    {
        var parts = new System.Collections.Generic.List<string>(_stations.Count);
        foreach (Station s in _stations)
            parts.Add($"{s.Position.x:F3}|{s.Position.y:F3}|{s.Position.z:F3}|{s.GunItem.itemName}");

        string data = string.Join(";", parts);
        PhotonNetwork.CurrentRoom?.SetCustomProperties(
            new ExitGames.Client.Photon.Hashtable { { "WBStations", data } });
        Logger.LogInfo($"[WallBuy] Synced {_stations.Count} station(s) to room properties.");
    }

    // Called on non-host clients each Update() until stations are loaded.
    private void TryLoadStationsFromRoomProperties()
    {
        var room = PhotonNetwork.CurrentRoom;
        if (room == null) return;
        if (!room.CustomProperties.TryGetValue("WBStations", out object val)) return;
        if (val is not string data || string.IsNullOrEmpty(data)) return;

        var dict = StatsManager.instance?.itemDictionary;
        if (dict == null) return;

        _stations.Clear();
        foreach (string entry in data.Split(';'))
        {
            // Format: "x|y|z|Gun Name" — LastIndexOf handles spaces in gun names.
            int lastPipe = entry.LastIndexOf('|');
            if (lastPipe < 0) continue;
            string coordPart = entry.Substring(0, lastPipe);
            string gunName   = entry.Substring(lastPipe + 1);

            string[] coords = coordPart.Split('|');
            if (coords.Length < 3) continue;
            if (!float.TryParse(coords[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x)) continue;
            if (!float.TryParse(coords[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float y)) continue;
            if (!float.TryParse(coords[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float z)) continue;

            Item? gun = null;
            foreach (Item item in dict.Values)
                if (item.itemName == gunName) { gun = item; break; }
            if (gun == null) continue;

            _stations.Add(new Station(new Vector3(x, y, z), gun));
        }

        _stationsSynced = true;
        Logger.LogInfo($"[WallBuy] Client loaded {_stations.Count} station(s) from room properties.");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private int GetNearestStation(Vector3 pos)
    {
        float radiusSq = BuyRadius * BuyRadius;
        for (int i = 0; i < _stations.Count; i++)
            if ((_stations[i].Position - pos).sqrMagnitude <= radiusSq) return i;
        return -1;
    }

    private void Purchase(PlayerAvatar player, int stationIndex)
    {
        if (stationIndex >= _stations.Count) return;
        Station s = _stations[stationIndex];

        var stats = StatsManager.instance;
        if (stats == null) return;

        int balance = stats.GetRunStatCurrency();
        if (balance < GunCost)
        {
            Logger.LogInfo($"[WallBuy] {player.playerName} can't afford {s.GunItem.itemName} (${balance}/${GunCost}).");
            WaveHUD.ShowBuyPrompt($"NOT ENOUGH SURPLUS — ${GunCost} required");
            return;
        }

        int newBalance = balance - GunCost;
        stats.runStats["currency"] = newBalance;
        PunManager.instance.UpdateStat("runStats", "currency", newBalance);

        Vector3 spawnPos = player.transform.position + Vector3.up * 0.5f;
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.InstantiateRoomObject(s.GunItem.prefab.ResourcePath, spawnPos, Quaternion.identity);
        else
        {
            var prefab = s.GunItem.prefab.Prefab;
            if (prefab != null)
                Object.Instantiate(prefab, spawnPos, Quaternion.identity);
            else
                Logger.LogWarning($"[WallBuy] prefab.Prefab null for '{s.GunItem.itemName}'.");
        }

        Logger.LogInfo($"[WallBuy] {player.playerName} bought {s.GunItem.itemName} — ${newBalance} remaining.");
        WaveHUD.ShowBuyPrompt($"PURCHASED: {s.GunItem.itemName.ToUpper()}");
    }

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
