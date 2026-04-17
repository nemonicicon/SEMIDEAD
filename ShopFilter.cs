using HarmonyLib;
using BepInEx.Logging;

namespace SEMIDEAD;

/// <summary>
/// Removes guns from the truck shop after ShopManager builds its item pool.
///
/// Flow (confirmed via dnSpy):
///   ShopInitialize() → GetAllItemsFromStatsManager() → potentialItems populated.
///   Guns end up in potentialItems because they don't match upgrade/healthPack/crystal.
///   Postfix purges them after the list is built — no prefix or spawn interception needed.
///
/// potentialItems is public, so no reflection required.
/// ShopManager.instance is used — runs on host only (SEMIDEAD is host-only).
/// </summary>
// DISABLED: Shop filter inactive until Phase 2 testing.
// [HarmonyPatch(typeof(ShopManager), "ShopInitialize")]
static class ShopFilter
{
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    [HarmonyPostfix]
    private static void ShopInitialize_Postfix()
    {
        var shop = ShopManager.instance;
        if (shop?.potentialItems == null) return;

        int before = shop.potentialItems.Count;
        shop.potentialItems.RemoveAll(item => item.itemType == SemiFunc.itemType.gun);
        int removed = before - shop.potentialItems.Count;

        Logger.LogInfo($"[ShopFilter] Removed {removed} gun(s) from shop pool ({shop.potentialItems.Count} items remaining).");
    }
}
