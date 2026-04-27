using HarmonyLib;
using UnityEngine;

namespace SEMIDEAD.Patches;

/// <summary>
/// Patches on EnemyHealth:
///
/// 1. InstaKill_Prefix — forces damage to 9999 when PowerUpManager.InstaKillActive.
///
/// 2. DeathRPC_Prefix — sets spawnValuable=false on every enemy before DeathRPC runs,
///    suppressing native soul orb drops globally. Belt-and-suspenders alongside
///    WaveSpawner (sets it at spawn time) and EnemyParentDespawnPatch (sets it before Despawn).
///
/// 3. DeathRPC_Postfix — fires CharacterSystem and AnnouncerSystem kill tracking.
///
/// 4. DeathImpulseRPC_Postfix — fallback death hook for wave enemies.
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

    // Suppress native soul orb drop before any orb-spawn logic in DeathRPC runs.
    [HarmonyPrefix, HarmonyPatch("DeathRPC")]
    private static void DeathRPC_Prefix(EnemyHealth __instance)
    {
        __instance.spawnValuable = false;
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
