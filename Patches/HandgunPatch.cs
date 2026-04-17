using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SEMIDEAD.Patches;

/// <summary>
/// Two modifications to the handgun (Item Gun Handgun):
///
/// 1. 3-round burst — Postfix on ItemGun.Shoot(). When a handgun fires its first
///    shot normally, a coroutine fires 2 more shots at 0.08s intervals.
///    A static HashSet<ItemGun> guards against the postfix re-triggering on its
///    own burst shots.
///
/// 2. 30-round magazine — Postfix on ItemBattery.Start() sets batteryBars = 30.
///    Note: batteryBars > 100 breaks the formula (100/batteryBars = 0 in integer math),
///    so 100 is the practical maximum without patching the drain logic.
/// </summary>
[HarmonyPatch]
static class HandgunPatch
{
    // ---------------------------------------------------------------------------
    // 3-round burst
    // ---------------------------------------------------------------------------

    private static readonly HashSet<ItemGun> _burstActive = new();
    private const int   BurstSize  = 3;     // total shots per trigger pull (including first)
    private const float BurstDelay = 0.08f; // seconds between burst shots

    [HarmonyPostfix, HarmonyPatch(typeof(ItemGun), "Shoot")]
    private static void Shoot_Postfix(ItemGun __instance)
    {
        // Don't re-trigger on our own burst shots.
        if (_burstActive.Contains(__instance)) return;

        // Only apply to handguns. instanceName may not be set yet on first spawn,
        // so fall back to the game object name which is always "Item Gun Handgun(Clone)".
        var attrs = __instance.GetComponent<ItemAttributes>();
        bool isHandgun = attrs != null
            ? attrs.instanceName.StartsWith(StartingPistol.HandgunName)
            : __instance.gameObject.name.StartsWith("Item Gun Handgun");

        if (!isHandgun) return;

        _burstActive.Add(__instance);
        __instance.StartCoroutine(BurstCoroutine(__instance));
    }

    private static IEnumerator BurstCoroutine(ItemGun gun)
    {
        for (int i = 1; i < BurstSize; i++) // first shot already fired by the original call
        {
            yield return new WaitForSeconds(BurstDelay);
            if (gun == null) break;
            gun.Shoot();
        }
        if (gun != null) _burstActive.Remove(gun);
    }

    // ---------------------------------------------------------------------------
    // Damage logger — reads and logs the handgun's default enemyDamage on first shot.
    // Remove once we know the value and want to set it explicitly.
    // ---------------------------------------------------------------------------

    private static bool _damageLogged = false;

    [HarmonyPostfix, HarmonyPatch(typeof(ItemGun), "ShootBulletRPC")]
    private static void ShootBulletRPC_Postfix(ItemGun __instance)
    {
        if (_damageLogged) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!__instance.gameObject.name.StartsWith("Item Gun Handgun")) return;

        SEMIDEAD.Logger.LogInfo($"[HandgunPatch] MAG 60 enemyDamage = {__instance.hurtCollider?.enemyDamage.ToString() ?? "hurtCollider is null"}");
        _damageLogged = true;
    }

    // ---------------------------------------------------------------------------
    // 30-round magazine
    // ---------------------------------------------------------------------------

    [HarmonyPostfix, HarmonyPatch(typeof(ItemBattery), "Start")]
    private static void BatteryStart_Postfix(ItemBattery __instance)
    {
        if (!__instance.gameObject.name.StartsWith("Item Gun Handgun")) return;

        __instance.batteryBars    = 30;
        __instance.batteryLifeInt = 30;
        // batteryLife is already 100f from Start() — no change needed.
    }
}
