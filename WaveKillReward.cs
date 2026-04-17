using BepInEx.Logging;
using UnityEngine;

namespace SEMIDEAD;

/// <summary>
/// Awards SURPLUS to the shared team pool when a wave enemy dies.
///
/// Kill rewards (tunable — will move to host config in Phase 3):
///   $50   standard kill  (difficulty1)
///   $100  fast kill      (difficulty2 / difficulty3)
///   +$150 last-enemy-of-wave bonus
///
/// Headshot bonus dropped — EnemyHealth.Hurt(int, Vector3) carries no attacker
/// or hit-location data, so headshots are not detectable from this system.
///
/// SURPLUS is a shared team pool. Awards go to the pool via SemiFunc.StatSetRunCurrency.
/// Caller must be master client or singleplayer — guarded by SemiFunc.IsMasterClientOrSingleplayer().
/// </summary>
public static class WaveKillReward
{
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    // Reward amounts — will be moved to host config in Phase 3.
    private const int RewardStandard = 3;
    private const int RewardFast     = 5;
    private const int BonusLastEnemy = 8;

    /// <summary>
    /// Called by WaveSpawner.OnWaveEnemyDied() before the enemy is removed from
    /// _waveEnemies, so remainingAfterThisKill is accurate for the last-kill bonus.
    /// </summary>
    public static void OnWaveEnemyKilled(EnemyParent enemy, int remainingAfterThisKill)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        WaveKillTracker? tracker = enemy.GetComponent<WaveKillTracker>();
        if (tracker == null)
        {
            Logger.LogWarning($"[WaveKillReward] No WaveKillTracker on {enemy.name} — skipping reward.");
            return;
        }

        int reward = tracker.IsFastVariant ? RewardFast : RewardStandard;

        if (remainingAfterThisKill == 0)
        {
            reward += BonusLastEnemy;
            Logger.LogInfo($"[WaveKillReward] Last enemy of wave — +${BonusLastEnemy} bonus.");
        }

        Logger.LogInfo($"[WaveKillReward] +${reward} SURPLUS ({enemy.name}, fast={tracker.IsFastVariant})");
        AwardSurplus(reward);
    }

    // ---------------------------------------------------------------------------
    // SURPLUS award — shared team pool.
    //
    // Confirmed: StatsManager.instance.runStats["currency"] is the shared pool.
    // Read via GetRunStatCurrency(), write directly to the dictionary, then sync
    // to all clients via PunManager so the UI updates across the network.
    // ---------------------------------------------------------------------------
    private static void AwardSurplus(int amount)
    {
        if (PowerUpManager.DoublePointsActive)
        {
            amount *= 2;
            Logger.LogInfo($"[WaveKillReward] Double Points active — award doubled to ${amount}.");
        }

        StatsManager stats = StatsManager.instance;
        int newValue = stats.GetRunStatCurrency() + amount;
        stats.runStats["currency"] = newValue;
        PunManager.instance.UpdateStat("runStats", "currency", newValue);
    }
}

// ---------------------------------------------------------------------------
// Marker component added to wave enemies by WaveSpawner.
// Carries the difficulty tier flag so WaveKillReward knows which reward to give.
// ---------------------------------------------------------------------------
public class WaveKillTracker : MonoBehaviour
{
    /// <summary>True if this enemy came from difficulty2 or difficulty3 — awards $100 instead of $50.</summary>
    public bool IsFastVariant { get; set; }
}
