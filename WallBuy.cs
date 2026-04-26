using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;

namespace SEMIDEAD;

/// <summary>
/// Spawns three weapon-purchase stations each gameplay level.
/// Gun pool: Shotgun ($500), Shockwave/Ray Gun ($1000), Laser/Photon Blaster ($1000).
///
/// Each station spawns a physical display gun (Photon room object) visible to all clients.
/// Host detects player proximity, fires insufficient-funds TTS (rate-limited 15 s), or
/// starts a 5-second countdown purchase. Purchased gun spawns at the buyer's feet.
///
/// Display guns that drift > 2 units from their spawn position are destroyed and respawned
/// every 2 s (handles client grabs). Host-side grabDisableTimer prevents the host from
/// accidentally picking up display guns.
/// </summary>
public class WallBuy : MonoBehaviour
{
    public static WallBuy? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private const float BuyRadius      = 3f;
    private const float PlayerCooldown = 15f;
    private const int   StationCount   = 3;

    private class Station
    {
        public readonly Vector3    Position;
        public readonly Item       GunItem;
        public readonly int        Cost;
        public readonly string     DisplayName;
        public          GameObject? DisplayGun;

        public Station(Vector3 pos, Item gun)
        {
            Position    = pos;
            GunItem     = gun;
            Cost        = ResolveCost(gun);
            DisplayName = ResolveDisplayName(gun);
        }

        private static int ResolveCost(Item gun)
        {
            string n = gun.itemName;
            if (n.IndexOf("Shockwave", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Laser",     System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 1000;
            return 500;
        }

        private static string ResolveDisplayName(Item gun)
        {
            string n = gun.itemName;
            if (n.IndexOf("Shockwave", System.StringComparison.OrdinalIgnoreCase) >= 0) return "RAY GUN";
            if (n.IndexOf("Laser",     System.StringComparison.OrdinalIgnoreCase) >= 0) return "PHOTON BLASTER";
            return "SHOTGUN";
        }
    }

    private readonly List<Station>                   _stations          = new();
    private readonly Dictionary<PlayerAvatar, float> _purchaseCooldowns = new();
    private readonly Dictionary<PlayerAvatar, float> _insFundsCooldowns = new();
    private readonly Dictionary<PlayerAvatar, int>   _lastNearStation   = new();
    private readonly HashSet<int>                    _activeCountdowns  = new();

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
        StopAllCoroutines();

        if (SemiFunc.IsMasterClientOrSingleplayer())
            foreach (var s in _stations)
                if (s.DisplayGun != null) DestroyDisplayGun(s.DisplayGun);

        _stations.Clear();
        _purchaseCooldowns.Clear();
        _insFundsCooldowns.Clear();
        _lastNearStation.Clear();
        _activeCountdowns.Clear();
        WaveHUD.ClearBuyPrompt();

        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!IsGameplayLevel()) return;

        StartCoroutine(PlaceStationsAfterLoad());
    }

    // ---------------------------------------------------------------------------
    // Station placement
    // ---------------------------------------------------------------------------

    private IEnumerator PlaceStationsAfterLoad()
    {
        while (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            yield return null;
        if (!IsGameplayLevel()) yield break;

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

        // Build allowed gun pool.
        var guns = new List<Item>();
        var dict = StatsManager.instance?.itemDictionary;
        if (dict != null)
            foreach (Item entry in dict.Values)
                if (IsAllowedGun(entry)) guns.Add(entry);

        if (guns.Count == 0)
        {
            Logger.LogWarning("[WallBuy] No allowed guns found in itemDictionary — no stations placed.");
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
        if (candidates.Count == 0) candidates = all;

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        const float MinSpacingSq = 25f;
        var occupied = new List<Vector3>();
        if (MysteryBox.ActiveBoxPosition.HasValue)
            occupied.Add(MysteryBox.ActiveBoxPosition.Value);

        // Assign one gun type per station, no duplicate display names when possible.
        var usedNames = new HashSet<string>();

        int placed = 0;
        int tried  = 0;
        foreach (LevelPoint pt in candidates)
        {
            if (placed >= StationCount) break;
            tried++;
            Vector3 pos = pt.transform.position;

            if (NavMesh.SamplePosition(pos, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                pos = hit.position;

            bool tooClose = false;
            foreach (Vector3 occ in occupied)
                if ((pos - occ).sqrMagnitude < MinSpacingSq) { tooClose = true; break; }

            Logger.LogInfo($"[WallBuy] Candidate {tried}: pos={pos} tooClose={tooClose}");
            if (tooClose) continue;

            occupied.Add(pos);

            // Prefer a gun whose display name hasn't been used yet.
            Item gun = PickGun(guns, usedNames);
            usedNames.Add(new Station(pos, gun).DisplayName);

            var station = new Station(pos, gun);
            _stations.Add(station);
            Logger.LogInfo($"[WallBuy] Station {placed + 1}: {station.DisplayName} (${station.Cost}) at {pos}");
            placed++;
        }

        Logger.LogInfo($"[WallBuy] {placed} station(s) placed (tried {tried} candidate(s)).");

        for (int i = 0; i < _stations.Count; i++)
        {
            SpawnDisplayGun(i);
            StartCoroutine(DisplayGunRespawnCheck(i));
        }
    }

    private static Item PickGun(List<Item> guns, HashSet<string> usedNames)
    {
        foreach (Item g in guns)
        {
            string dn = new Station(Vector3.zero, g).DisplayName;
            if (!usedNames.Contains(dn)) return g;
        }
        return guns[Random.Range(0, guns.Count)];
    }

    // ---------------------------------------------------------------------------
    // Per-frame update (host-only)
    // ---------------------------------------------------------------------------

    private void Update()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        HostUpdate();
    }

    private void HostUpdate()
    {
        if (_stations.Count == 0) return;

        // Prevent host from grabbing display guns.
        foreach (var s in _stations)
        {
            if (s.DisplayGun == null) continue;
            foreach (var pgo in s.DisplayGun.GetComponentsInChildren<PhysGrabObject>())
                Traverse.Create(pgo).Field("grabDisableTimer").SetValue(0.5f);
        }

        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        PlayerAvatar? promptPlayer  = null;
        int           promptStation = -1;

        foreach (PlayerAvatar player in players)
        {
            if (player == null || player.isDisabled)
            {
                if (player != null) _lastNearStation.Remove(player);
                continue;
            }

            int nearStation = GetNearestStation(player.transform.position);
            _lastNearStation.TryGetValue(player, out int prevStation);
            bool justEntered = nearStation >= 0 && nearStation != prevStation;
            _lastNearStation[player] = nearStation;

            if (nearStation < 0) continue;

            // Show prompt for first player near a station without an active countdown.
            if (promptPlayer == null && !_activeCountdowns.Contains(nearStation))
            {
                promptPlayer  = player;
                promptStation = nearStation;
            }

            // Skip if countdown already running on this station.
            if (_activeCountdowns.Contains(nearStation)) continue;

            // Skip if on purchase cooldown.
            if (_purchaseCooldowns.TryGetValue(player, out float coolUntil) && Time.time < coolUntil) continue;

            if (!justEntered) continue;

            Station s    = _stations[nearStation];
            int     bal  = StatsManager.instance?.GetRunStatCurrency() ?? 0;

            if (bal < s.Cost)
            {
                if (!_insFundsCooldowns.TryGetValue(player, out float iCool) || Time.time >= iCool)
                {
                    _insFundsCooldowns[player] = Time.time + 15f;
                    CharacterSystem.Instance?.TriggerSpeech(player, SpeechTrigger.WallBuyInsufficientFunds);
                    Logger.LogInfo($"[WallBuy] {player.playerName} insufficient funds (${bal}/${s.Cost}) for {s.DisplayName}.");
                }
            }
            else
            {
                _activeCountdowns.Add(nearStation);
                StartCoroutine(PurchaseCountdown(player, nearStation));
            }
        }

        if (promptPlayer != null && promptStation >= 0)
        {
            Station s = _stations[promptStation];
            WaveHUD.ShowBuyPrompt($"WALL BUY: {s.DisplayName}  —  ${s.Cost}");
        }
        else if (!AnyCountdownRunning())
        {
            WaveHUD.ClearBuyPrompt();
        }
    }

    private bool AnyCountdownRunning() => _activeCountdowns.Count > 0;

    // ---------------------------------------------------------------------------
    // Purchase countdown coroutine
    // ---------------------------------------------------------------------------

    private IEnumerator PurchaseCountdown(PlayerAvatar player, int stationIndex)
    {
        Station s = _stations[stationIndex];
        CharacterSystem.Instance?.TriggerSpeech(player, SpeechTrigger.WallBuyPurchase);
        Logger.LogInfo($"[WallBuy] {player.playerName} starting {s.DisplayName} countdown.");

        for (int i = 5; i >= 1; i--)
        {
            WaveHUD.ShowBuyPrompt($"BUYING {s.DisplayName}: ${s.Cost}  —  {i}...");
            yield return new WaitForSeconds(1f);

            // Cancel if player left the zone.
            if ((player.transform.position - s.Position).sqrMagnitude > BuyRadius * BuyRadius)
            {
                Logger.LogInfo($"[WallBuy] {player.playerName} left zone — countdown cancelled.");
                _activeCountdowns.Remove(stationIndex);
                WaveHUD.ClearBuyPrompt();
                yield break;
            }

            // Cancel if funds dropped (e.g. another purchase on same frame).
            if ((StatsManager.instance?.GetRunStatCurrency() ?? 0) < s.Cost)
            {
                Logger.LogInfo($"[WallBuy] {player.playerName} lost funds mid-countdown — cancelled.");
                _activeCountdowns.Remove(stationIndex);
                CharacterSystem.Instance?.TriggerSpeech(player, SpeechTrigger.WallBuyInsufficientFunds);
                WaveHUD.ClearBuyPrompt();
                yield break;
            }
        }

        _activeCountdowns.Remove(stationIndex);
        ExecutePurchase(player, stationIndex);
    }

    private void ExecutePurchase(PlayerAvatar player, int stationIndex)
    {
        if (stationIndex >= _stations.Count) return;
        Station s = _stations[stationIndex];

        var stats = StatsManager.instance;
        if (stats == null) return;

        int balance = stats.GetRunStatCurrency();
        if (balance < s.Cost)
        {
            Logger.LogWarning($"[WallBuy] ExecutePurchase final-check failed for {player.playerName}.");
            return;
        }

        int newBalance = balance - s.Cost;
        stats.runStats["currency"] = newBalance;
        PunManager.instance.UpdateStat("runStats", "currency", newBalance);

        Vector3 spawnPos = player.transform.position + Vector3.up * 0.5f;
        if (SemiFunc.IsMultiplayer())
        {
            PhotonNetwork.InstantiateRoomObject(s.GunItem.prefab.ResourcePath, spawnPos, Quaternion.identity);
        }
        else
        {
            var prefab = s.GunItem.prefab.Prefab;
            if (prefab != null)
                Object.Instantiate(prefab, spawnPos, Quaternion.identity);
            else
                Logger.LogWarning($"[WallBuy] prefab.Prefab null for '{s.GunItem.itemName}'.");
        }

        _purchaseCooldowns[player] = Time.time + PlayerCooldown;
        Logger.LogInfo($"[WallBuy] {player.playerName} bought {s.DisplayName} — ${newBalance} remaining.");
        WaveHUD.ShowBuyPrompt($"PURCHASED: {s.DisplayName}");
    }

    // ---------------------------------------------------------------------------
    // Display gun management
    // ---------------------------------------------------------------------------

    private void SpawnDisplayGun(int stationIndex)
    {
        if (stationIndex >= _stations.Count) return;
        Station s   = _stations[stationIndex];
        Vector3 pos = s.Position + Vector3.up * 1.2f;
        Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 15f);

        GameObject go;
        if (SemiFunc.IsMultiplayer())
        {
            go = PhotonNetwork.InstantiateRoomObject(s.GunItem.prefab.ResourcePath, pos, rot);
        }
        else
        {
            var prefab = s.GunItem.prefab.Prefab;
            if (prefab == null)
            {
                Logger.LogWarning($"[WallBuy] Display gun prefab null for station {stationIndex}.");
                return;
            }
            go = Object.Instantiate(prefab, pos, rot);
        }

        if (go == null)
        {
            Logger.LogWarning($"[WallBuy] Display gun spawn failed for station {stationIndex}.");
            return;
        }

        go.name = "SEMIDEAD_WallBuy_Display";
        s.DisplayGun = go;
        Logger.LogInfo($"[WallBuy] Display gun spawned for station {stationIndex} ({s.DisplayName}) at {pos}.");
    }

    private IEnumerator DisplayGunRespawnCheck(int stationIndex)
    {
        while (IsGameplayLevel())
        {
            yield return new WaitForSeconds(2f);
            if (stationIndex >= _stations.Count) yield break;

            Station s = _stations[stationIndex];
            if (s.DisplayGun == null)
            {
                SpawnDisplayGun(stationIndex);
                continue;
            }

            Vector3 target = s.Position + Vector3.up * 1.2f;
            if ((s.DisplayGun.transform.position - target).sqrMagnitude > 4f)
            {
                Logger.LogInfo($"[WallBuy] Display gun drifted — respawning station {stationIndex}.");
                DestroyDisplayGun(s.DisplayGun);
                s.DisplayGun = null;
                yield return new WaitForSeconds(0.1f);
                SpawnDisplayGun(stationIndex);
            }
        }
    }

    private static void DestroyDisplayGun(GameObject go)
    {
        if (go == null) return;
        if (SemiFunc.IsMultiplayer())
        {
            var pv = go.GetComponent<PhotonView>();
            if (pv != null) PhotonNetwork.Destroy(go);
            else            Object.Destroy(go);
        }
        else
        {
            Object.Destroy(go);
        }
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

    private static bool IsAllowedGun(Item item)
    {
        if (item.itemType != SemiFunc.itemType.gun) return false;
        string n = item.itemName;
        return n.IndexOf("Shotgun",   System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("Shockwave", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
               n.IndexOf("Laser",     System.StringComparison.OrdinalIgnoreCase) >= 0;
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
