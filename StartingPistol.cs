using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SEMIDEAD;

/// <summary>
/// Gives each player a round-specific loadout at the start of every gameplay level.
///
/// Rounds are determined by RunManager.levelsCompleted:
///   0  (Round 1): Ray Gun + Handgun + Sword
///   1  (Round 2): Ray Gun + Shotgun + Handgun + Sword
///   2  (Round 3): Laser + Laser (pulse/photon) + Grenade + Sword
///   3  (Round 4): Tranq + Frying Pan + Grenade + Sword
///   4+ (Round 5+): Handgun + Shotgun + Grenade + Sword
///
/// Item names are ScriptableObject asset names — keys in StatsManager.itemDictionary.
/// On the first level load the full gun+melee item list is dumped to the log so item
/// names can be confirmed or corrected without a dnSpy session.
///
/// Respawn pistol: spawns a handgun at head-extraction respawn (mid-level).
/// </summary>
public class StartingPistol : MonoBehaviour
{
    public static StartingPistol? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    // ---------------------------------------------------------------------------
    // Item name constants — keys in StatsManager.itemDictionary.
    // Run the game once and check the log for "[StartingPistol] ITEMS:" if any
    // of these fail; the dump will show the real key names.
    // ---------------------------------------------------------------------------

    internal const string HandgunName       = "Item Gun Handgun";       // confirmed
    internal const string ShotgunName      = "Item Gun Shotgun";       // likely
    internal const string LaserName         = "Item Gun Laser";         // Photon Blaster (confirmed from dump)
    internal const string ShockwaveName     = "Item Gun Shockwave";     // Pulse Pistol (confirmed from dump)
    internal const string TranqName         = "Item Gun Tranq";         // Tranq Gun (confirmed from dump)
    internal const string GrenadeName       = "Item Grenade Explosive"; // confirmed
    internal const string SwordName         = "Item Melee Sword";       // confirmed from dump
    internal const string FryingPanName     = "Item Melee Frying Pan";  // confirmed from dump

    private const float RespawnOffset = 0.5f;
    private static bool _itemsDumped = false;

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

    public static StartingPistol Create()
    {
        var go = new GameObject("SEMIDEAD_StartingPistol");
        return go.AddComponent<StartingPistol>();
    }

    public void OnLevelSetup() { }

    // ---------------------------------------------------------------------------
    // Respawn — called from PlayerAvatarRespawnPatch.
    // Spawns a handgun at the player's feet after head-extraction respawn.
    // ---------------------------------------------------------------------------

    public static void OnPlayerRespawned(PlayerAvatar player)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var dict = StatsManager.instance?.itemDictionary;
        if (dict == null || !dict.ContainsKey(HandgunName))
        {
            Logger.LogWarning($"[StartingPistol] '{HandgunName}' not found in itemDictionary.");
            return;
        }

        Vector3 pos = player.transform.position + Vector3.up * RespawnOffset;
        SpawnItem(dict[HandgunName], pos);
        Logger.LogInfo($"[StartingPistol] Respawn pistol spawned for {player.playerName}.");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static void SpawnItem(Item item, Vector3 pos)
    {
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, pos, Quaternion.identity);
        else
        {
            var prefab = item.prefab.Prefab;
            if (prefab != null)
                Object.Instantiate(prefab, pos, Quaternion.identity);
            else
                Logger.LogWarning($"[StartingPistol] prefab.Prefab null for '{item.itemName}'.");
        }
    }

    /// <summary>
    /// Injects an Item into the truck's purchasedItems list (spawns it in the truck).
    /// Returns false and logs a warning if the name isn't in the dictionary.
    /// </summary>
    internal static bool InjectItem(Dictionary<string, Item> dict, string itemName)
    {
        if (!dict.ContainsKey(itemName))
        {
            Logger.LogWarning($"[StartingPistol] Item '{itemName}' not found in itemDictionary — skipping.");
            return false;
        }
        ItemManager.instance.purchasedItems.Add(dict[itemName]);
        return true;
    }

    /// <summary>
    /// Dumps all gun and melee item names to the log once per session so asset names
    /// can be confirmed. Gated by _itemsDumped so it only fires once.
    /// </summary>
    internal static void DumpItemNames(Dictionary<string, Item> dict)
    {
        if (_itemsDumped) return;
        _itemsDumped = true;

        var guns   = new System.Text.StringBuilder();
        var melees = new System.Text.StringBuilder();


        foreach (var kv in dict)
        {
            if (kv.Value == null) continue;
            if (kv.Value.itemType == SemiFunc.itemType.gun)
                guns.Append($"  \"{kv.Key}\" ({kv.Value.itemName})\n");
            else if (kv.Value.itemType == SemiFunc.itemType.melee)
                melees.Append($"  \"{kv.Key}\" ({kv.Value.itemName})\n");
        }

        Logger.LogInfo($"[StartingPistol] ITEMS — Guns:\n{guns}Melee:\n{melees}");
    }
}

// ---------------------------------------------------------------------------
// Injects round-specific loadout into the truck item system every gameplay level.
// Fires in Prefix of TruckPopulateItemVolumes — same window as MonkeyBomb spawn.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PunManager), "TruckPopulateItemVolumes")]
static class TruckPopulateItemVolumesPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var rm = RunManager.instance;
        if (rm == null || rm.levelCurrent == null || rm.levels == null
            || !rm.levels.Contains(rm.levelCurrent))
            return;

        var dict = StatsManager.instance?.itemDictionary;
        if (dict == null) return;

        // Dump item names once so asset names can be confirmed.
        StartingPistol.DumpItemNames(dict);

        // Rename Shockwave to "RAY GUN" every level (idempotent).
        if (dict.TryGetValue(StartingPistol.ShockwaveName, out Item rayGunItem))
            rayGunItem.itemName = "RAY GUN";

        // Rename Handgun to "MAG 60" every level (idempotent).
        if (dict.TryGetValue(StartingPistol.HandgunName, out Item handgunItem))
            handgunItem.itemName = "MAG 60";

        int round = rm.levelsCompleted; // 0 = round 1, 1 = round 2, etc.
        int players = Mathf.Max(1, SemiFunc.PlayerGetList()?.Count ?? 1);

        // Build item list for this round.
        // Each entry is (itemName, countPerPlayer).
        var items = new List<(string name, int count)>();

        switch (round)
        {
            case 0: // Round 1
                items.Add((StartingPistol.ShockwaveName, 1));
                items.Add((StartingPistol.HandgunName,   1));
                items.Add((StartingPistol.SwordName,     1));
                break;
            case 1: // Round 2
                items.Add((StartingPistol.ShockwaveName, 1));
                items.Add((StartingPistol.ShotgunName,   1));
                items.Add((StartingPistol.HandgunName,   1));
                items.Add((StartingPistol.SwordName,     1));
                break;
            case 2: // Round 3
                items.Add((StartingPistol.ShotgunName,  1));
                items.Add((StartingPistol.HandgunName,  1));
                items.Add((StartingPistol.ShockwaveName, 1));
                break;
            case 3: // Round 4 — 2 Ray Guns per player
                items.Add((StartingPistol.ShockwaveName, 2));
                break;
            case 4: // Round 5 — 3 Shotguns per player
                items.Add((StartingPistol.ShotgunName, 3));
                break;
            default: // Round 6+
                items.Add((StartingPistol.ShockwaveName, 1));
                items.Add((StartingPistol.HandgunName,   1));
                items.Add((StartingPistol.ShotgunName,   1));
                break;
        }

        // Inject items per player.
        int injected = 0;
        foreach (var (itemName, count) in items)
        {
            for (int i = 0; i < players * count; i++)
            {
                if (StartingPistol.InjectItem(dict, itemName))
                    injected++;
            }
        }

        SEMIDEAD.Logger.LogInfo(
            $"[StartingPistol] Round {round + 1}: injected {injected} item(s) for {players} player(s).");
    }
}

// ---------------------------------------------------------------------------
// Sets Ray Gun bullet damage to 150 on the host.
// ShootBulletRPC already has a MasterOnlyRPC guard, so the bullet and its
// HurtCollider are only instantiated on the master client — this postfix
// therefore affects every player's shots regardless of who fired.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(ItemGun), "ShootBulletRPC")]
static class RayGunDamagePatch
{
    [HarmonyPostfix]
    private static void Postfix(ItemGun __instance)
    {
        var attrs = __instance.GetComponent<ItemAttributes>();
        if (attrs == null || string.IsNullOrEmpty(attrs.instanceName)) return;
        if (!attrs.instanceName.StartsWith(StartingPistol.ShockwaveName)) return;

        var hurtCollider = HarmonyLib.Traverse.Create(__instance)
            .Field("hurtCollider").GetValue<HurtCollider>();
        if (hurtCollider != null)
            hurtCollider.enemyDamage = 150;
    }
}

// ---------------------------------------------------------------------------
// Hooks player respawn after head extraction.
// ReviveRPC confirmed at PlayerAvatar.cs line 1356.
// _revivedByTruck == false → mid-level head-extraction respawn → spawn pistol.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PlayerAvatar))]
static class PlayerAvatarRespawnPatch
{
    [HarmonyPostfix, HarmonyPatch("ReviveRPC")]
    private static void ReviveRPC_Postfix(PlayerAvatar __instance, bool _revivedByTruck)
    {
        if (_revivedByTruck) return;
        StartingPistol.OnPlayerRespawned(__instance);
    }
}
