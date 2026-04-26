using HarmonyLib;
using UnityEngine;

namespace SEMIDEAD.Patches;

/// <summary>
/// Three patches on EnemyHealth:
///
/// 1. InstaKill_Prefix — forces damage to 9999 when PowerUpManager.InstaKillActive.
///    Runs on every enemy hit, so it affects both wave and native R.E.P.O. monsters.
///
/// 2. DeathRPC_Postfix — fires CharacterSystem and AnnouncerSystem kill tracking.
///    DeathRPC fires IMMEDIATELY when a kill is confirmed (before the freeze delay),
///    so rapid kills register within the multi-kill window correctly.
///    DeathRPC is master-only (SemiFunc.MasterOnlyRPC check inside), but our Postfix
///    runs on all clients; the master-client guard is applied here.
///
/// 3. DeathImpulseRPC_Postfix — fallback death hook for wave enemies.
///    Primary path: WaveSpawner subscribes to EnemyHealth.onDeath UnityEvent.
///    This fallback fires after the freeze delay if the event is missed.
///    Checks WaveEnemyTag so it is a no-op for non-wave enemies.
/// </summary>
[HarmonyPatch(typeof(EnemyParent), "Despawn")]
static class EnemyParentDespawnPatch
{
    [HarmonyPrefix]
    private static void Prefix(EnemyParent __instance)
    {
        if (__instance.Enemy?.Health != null)
            __instance.Enemy.Health.spawnValuable = false;
    }
}

[HarmonyPatch(typeof(EnemyHealth))]
static class EnemyHealthPatch
{
    [HarmonyPrefix, HarmonyPatch(nameof(EnemyHealth.Hurt))]
    private static void InstaKill_Prefix(ref int _damage)
    {
        if (PowerUpManager.InstaKillActive)
            _damage = 9999;
    }

    [HarmonyPostfix, HarmonyPatch("DeathRPC")]
    private static void DeathRPC_Postfix(EnemyHealth __instance)
    {
        // Fire kill quote and multi-kill announcer at the moment the kill is confirmed.
        // DeathRPC fires before the freeze delay, giving accurate timing for multi-kill chains.
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        Vector3 deathPos = __instance.transform.position;
        CharacterSystem.Instance?.OnEnemyKilled(deathPos);
        AnnouncerSystem.Instance?.OnEnemyKilled(deathPos);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(EnemyHealth.DeathImpulseRPC))]
    private static void DeathImpulseRPC_Postfix(EnemyHealth __instance)
    {
        // Ignore non-wave enemies.
        if (__instance.GetComponentInParent<WaveEnemyTag>() == null) return;

        // Only fire if the primary onDeath subscription hasn't already removed this enemy.
        EnemyParent? parent = __instance.GetComponentInParent<EnemyParent>();
        if (parent == null) return;

        SEMIDEAD.Logger.LogInfo($"[EnemyHealthPatch] DeathImpulseRPC fallback fired for {parent.name}");
        WaveSpawner.Instance?.OnWaveEnemyDied(parent);
    }
}
