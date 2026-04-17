using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;

namespace SEMIDEAD;

/// <summary>
/// Spawns R.E.P.O. monsters in wave counts from level edges.
/// Called by WaveManager. Reports back via OnWaveEnemyDied() → WaveManager.OnWaveCleared().
///
/// Correct spawn flow (confirmed from LevelGenerator.EnemySpawn decompile):
///   1. PhotonNetwork.InstantiateRoomObject — creates EnemyParent on all clients.
///   2. enemy.SetupDone = true — CRITICAL. Without this EnemyParent.Setup() loops
///      forever waiting, the rigidbody stays kinematic, and Logic() never starts.
///   3. enemy.Enemy.EnemyTeleported(pos) — warps NavMesh agent to spawn position.
///   4. EnemyDirector.FirstSpawnPointAdd — registers with spawn point system.
///   5. Setup() completes within 0.1 s, starts Logic() which despawns then
///      re-spawns the enemy after its 2–5 s DespawnedTimer.
///
/// Death detection: subscribes to EnemyHealth.onDeath UnityEvent on each spawned enemy.
/// Fallback: EnemyHealthPatch patches DeathImpulseRPC if the event proves unreliable.
/// </summary>
public class WaveSpawner : MonoBehaviour
{
    public static WaveSpawner? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    // All living wave enemies this round. Cleared at the start of each wave.
    private readonly HashSet<EnemyParent> _waveEnemies = new();
    // Spawn positions used this wave — used to enforce minimum spacing between enemies.
    private readonly List<Vector3> _recentSpawnPositions = new();
    private int  _waveNumber;
    private bool _waveActive;

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

    public static WaveSpawner Create()
    {
        var go = new GameObject("SEMIDEAD_WaveSpawner");
        return go.AddComponent<WaveSpawner>(); // Awake() calls DontDestroyOnLoad
    }

    // ---------------------------------------------------------------------------
    // Public API — called by WaveManager
    // ---------------------------------------------------------------------------

    public void SpawnWave(int waveNumber, int count, bool elsaWave)
    {
        _waveNumber = waveNumber;
        _waveEnemies.Clear();
        _recentSpawnPositions.Clear();
        _waveActive = true;
        Logger.LogInfo($"[WaveSpawner] Starting spawn — wave {waveNumber}, {count} enemies, elsa={elsaWave}.");
        StartCoroutine(SpawnRoutine(waveNumber, count, elsaWave));
    }

    /// <summary>
    /// Forces all remaining wave enemies to investigate the nearest player position.
    /// Used by wave timeout — enemies rush players instead of being killed instantly.
    /// No SURPLUS rewards are awarded; the wave continues until enemies are killed normally.
    /// </summary>
    public void RushEnemiesAtPlayers()
    {
        if (!_waveActive) return;
        Logger.LogInfo("[WaveSpawner] RushEnemiesAtPlayers — forcing all wave enemies toward players.");

        Vector3 targetPos = GetPlayerCentroid();
        int rushed = 0;
        foreach (EnemyParent ep in _waveEnemies)
        {
            if (ep == null || !ep.Spawned || ep.Enemy == null) continue;
            Enemy enemy = ep.Enemy;

            // Mirror MonkeyBomb: break out of Chase so Investigate can take over.
            if (enemy.CurrentState == EnemyState.Chase ||
                enemy.CurrentState == EnemyState.ChaseBegin)
                enemy.CurrentState = EnemyState.Roaming;

            if (enemy.HasStateInvestigate)
            {
                enemy.StateInvestigate.Set(targetPos, false);
                rushed++;
            }
        }
        Logger.LogInfo($"[WaveSpawner] Rushed {rushed}/{_waveEnemies.Count} enemies toward {targetPos}.");
    }

    /// <summary>
    /// Kills all remaining wave enemies silently — no SURPLUS rewards, no orb drops.
    /// Called 20s after the rush timeout if the wave is still active.
    /// Clears _waveEnemies first so OnWaveEnemyDied is a no-op when each enemy's
    /// onDeath event fires, then triggers OnWaveCleared directly.
    /// </summary>
    public void SilentKillAll()
    {
        if (!_waveActive) return;
        Logger.LogInfo($"[WaveSpawner] SilentKillAll — clearing {_waveEnemies.Count} enemies (no rewards).");

        var toKill = new List<EnemyParent>(_waveEnemies);
        _waveEnemies.Clear();
        _waveActive = false;

        foreach (EnemyParent ep in toKill)
        {
            if (ep == null) continue;
            EnemyHealth? eh = ep.GetComponentInChildren<EnemyHealth>();
            if (eh != null) eh.Hurt(9999, Vector3.up);
        }

        WaveManager.Instance?.OnWaveCleared();
    }

    /// <summary>
    /// Safety fallback — clears the enemy set without firing rewards.
    /// </summary>
    public void ForceClearAll()
    {
        _waveEnemies.Clear();
        _waveActive = false;
    }

    /// <summary>
    /// Called either by the onDeath UnityEvent subscription (primary)
    /// or by EnemyHealthPatch.DeathImpulseRPC postfix (fallback).
    /// </summary>
    public void OnWaveEnemyDied(EnemyParent enemy)
    {
        if (!_waveActive) return;
        if (!_waveEnemies.Contains(enemy)) return;

        // Capture position before the enemy object is destroyed.
        Vector3 deathPos = enemy.transform.position;

        // Award SURPLUS before removing — remaining count passed so last-kill bonus works.
        int remainingAfter = _waveEnemies.Count - 1;
        WaveKillReward.OnWaveEnemyKilled(enemy, remainingAfter);

        // Roll for power-up orb drop.
        PowerUpManager.TryDropOrb(deathPos);

        _waveEnemies.Remove(enemy);

        // Remove from EnemyDirector.enemiesSpawned to keep the director list clean.
        EnemyDirector.instance?.enemiesSpawned.Remove(enemy);

        Logger.LogInfo($"[WaveSpawner] Wave enemy died — {_waveEnemies.Count} remaining.");

        if (_waveEnemies.Count == 0)
        {
            _waveActive = false;
            Logger.LogInfo($"[WaveSpawner] All wave {_waveNumber} enemies dead.");
            WaveManager.Instance?.OnWaveCleared();
        }
    }

    // ---------------------------------------------------------------------------
    // Spawn coroutine
    // ---------------------------------------------------------------------------

    private IEnumerator SpawnRoutine(int waveNumber, int count, bool elsaWave)
    {
        const float spawnInterval = 0.75f;

        for (int i = 0; i < count; i++)
        {
            TrySpawnOne(waveNumber, elsaWave);
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    private void TrySpawnOne(int waveNumber, bool elsaWave)
    {
        EnemySetup? setup;
        bool isFastVariant;

        if (elsaWave)
        {
            if (!PickElsaSetup(out setup) || setup == null)
            {
                // Elsa not found — fall back to normal spawn.
                if (!PickEnemySetup(waveNumber, out setup, out isFastVariant) || setup == null)
                {
                    Logger.LogWarning("[WaveSpawner] PickElsaSetup and fallback both failed.");
                    return;
                }
            }
            isFastVariant = true; // Elsa counts as fast variant ($100 reward)
        }
        else if (!PickEnemySetup(waveNumber, out setup, out isFastVariant) || setup == null)
        {
            Logger.LogWarning("[WaveSpawner] PickEnemySetup returned null — is EnemyDirector ready?");
            return;
        }

        Vector3 spawnPos = GetEdgeSpawnPosition(_recentSpawnPositions);

        if (NavMesh.SamplePosition(spawnPos, out NavMeshHit navCheck, 2f, NavMesh.AllAreas))
            spawnPos = navCheck.position;
        else
            Logger.LogWarning($"[WaveSpawner] NavMesh snap failed at {spawnPos} — using raw LevelPoint Y.");
        _recentSpawnPositions.Add(spawnPos);

        Logger.LogInfo($"[WaveSpawner] Spawning {setup.name} at {spawnPos} (Y={spawnPos.y:F3})");

        EnemyParent? enemy = SpawnEnemy(setup, spawnPos);
        if (enemy == null)
        {
            string resourcePath = (setup.spawnObjects != null && setup.spawnObjects.Count > 0)
                ? setup.spawnObjects[0].ResourcePath : "NO_PREFAB";
            Logger.LogWarning($"[WaveSpawner] Spawn failed — enemy: '{setup.name}', path: '{resourcePath}'.");
            return;
        }

        // -----------------------------------------------------------------------
        // THE critical step — matches LevelGenerator.EnemySpawn() exactly.
        // EnemyParent.Setup() loops on (!SetupDone) until this is set.
        // Without it: rigidbody stays kinematic, Logic() never starts, enemy is dead.
        // -----------------------------------------------------------------------
        enemy.SetupDone = true;

        // Warp NavMesh agent and sync position to all clients (matches native spawn flow).
        enemy.Enemy.EnemyTeleported(spawnPos);

        // Register with EnemyDirector's spawn point system (sets firstSpawnPointUsed).
        EnemyDirector.instance.FirstSpawnPointAdd(enemy);

        // Setup() will complete within ~0.1 s, then start Logic().
        // Logic() despawns the enemy, sets DespawnedTimer = 2–5 s, then re-spawns it.
        // No manual Spawn() call needed — Logic() handles the full lifecycle.
        Logger.LogInfo($"[WaveSpawner] {setup.name} initialized — will appear in ~2-5s.");

        // Mark as wave enemy and attach kill tracker.
        enemy.gameObject.AddComponent<WaveEnemyTag>();
        var tracker = enemy.gameObject.AddComponent<WaveKillTracker>();
        tracker.IsFastVariant = isFastVariant;

        // Primary death hook — subscribe to onDeath UnityEvent directly.
        EnemyHealth? enemyHealth = enemy.GetComponentInChildren<EnemyHealth>();
        if (enemyHealth != null)
        {
            // Suppress native enemy valuable (soul orb) drops — PowerUpManager controls orb spawning.
            enemyHealth.spawnValuable = false;

            EnemyParent captured = enemy;
            enemyHealth.onDeath.AddListener(() => OnWaveEnemyDied(captured));
        }
        else
        {
            Logger.LogWarning($"[WaveSpawner] No EnemyHealth on {enemy.name} — relying on DeathImpulseRPC fallback.");
        }

        _waveEnemies.Add(enemy);
        Logger.LogInfo($"[WaveSpawner] {setup.name} registered (fast={isFastVariant}). Wave total: {_waveEnemies.Count}");

        // Once the enemy actually spawns (2-5s), direct it toward the player centroid.
        StartCoroutine(InvestigateOnSpawn(enemy));
    }

    // ---------------------------------------------------------------------------
    // Post-spawn investigate — waits until the enemy actually appears, then
    // directs it toward the player centroid so it pathfinds in immediately.
    // ---------------------------------------------------------------------------

    private IEnumerator InvestigateOnSpawn(EnemyParent enemy)
    {
        float timeout = 12f;
        while (timeout > 0f)
        {
            if (enemy == null) yield break; // enemy was destroyed
            if (enemy.Spawned && enemy.Enemy != null) break;
            yield return new WaitForSeconds(0.5f);
            timeout -= 0.5f;
        }

        if (enemy == null || !enemy.Spawned || enemy.Enemy == null)
        {
            Logger.LogWarning($"[WaveSpawner] InvestigateOnSpawn timed out for {enemy?.name}.");
            yield break;
        }

        Vector3 centroid = GetPlayerCentroid();
        EnemyDirector.instance?.SetInvestigate(centroid, float.MaxValue, false);
        Logger.LogInfo($"[WaveSpawner] {enemy.name} directed toward centroid {centroid}.");
    }

    // ---------------------------------------------------------------------------
    // Enemy type selection via EnemyDirector difficulty lists
    //
    // Wave 1  → enemiesDifficulty1 (slow/basic)
    // Wave 2  → 70% difficulty1, 30% difficulty2 (mixed)
    // Wave 3+ → 30% difficulty1, 40% difficulty2, 30% difficulty3 (fast/aggressive)
    // ---------------------------------------------------------------------------

    // Finds the Elsa enemy setup from any difficulty pool.
    private static bool PickElsaSetup(out EnemySetup? setup)
    {
        setup = null;
        EnemyDirector? director = EnemyDirector.instance;
        if (director == null) return false;

        var candidates = new List<EnemySetup>();
        foreach (var pool in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
        {
            if (pool == null) continue;
            foreach (var s in pool)
            {
                if (s?.spawnObjects != null && s.spawnObjects.Count > 0 &&
                    s.spawnObjects[0].ResourcePath.Contains("Elsa"))
                    candidates.Add(s);
            }
        }

        if (candidates.Count == 0)
        {
            SEMIDEAD.Logger.LogWarning("[WaveSpawner] No Elsa EnemySetup found in any difficulty pool.");
            return false;
        }

        setup = candidates[Random.Range(0, candidates.Count)];
        SEMIDEAD.Logger.LogInfo($"[WaveSpawner] Elsa setup selected: {setup.name}");
        return true;
    }

    // isFastVariant = true for difficulty2/3 enemies → $100 kill reward instead of $50.
    private static bool PickEnemySetup(int wave, out EnemySetup? setup, out bool isFastVariant)
    {
        setup = null;
        isFastVariant = false;

        EnemyDirector? director = EnemyDirector.instance;
        if (director == null)
        {
            SEMIDEAD.Logger.LogWarning("[WaveSpawner] EnemyDirector.instance is null.");
            return false;
        }

        float roll = Random.value;
        List<EnemySetup> pool;

        if (wave == 1)
        {
            pool = director.enemiesDifficulty1;
            isFastVariant = false;
        }
        else if (wave == 2)
        {
            if (roll < 0.70f) { pool = director.enemiesDifficulty1; isFastVariant = false; }
            else              { pool = director.enemiesDifficulty2; isFastVariant = true;  }
        }
        else
        {
            if      (roll < 0.30f) { pool = director.enemiesDifficulty1; isFastVariant = false; }
            else if (roll < 0.70f) { pool = director.enemiesDifficulty2; isFastVariant = true;  }
            else                   { pool = director.enemiesDifficulty3; isFastVariant = true;  }
        }

        if (pool == null || pool.Count == 0)
        {
            SEMIDEAD.Logger.LogWarning("[WaveSpawner] Selected difficulty pool is empty.");
            return false;
        }

        // Filter out Director-type enemies (e.g. GnomeDirector) — they are level
        // management entities, not combat enemies, and crash when wave-spawned.
        // Filter out Ceiling Eye — attaches to ceilings, incompatible with wave spawn flow.
        var filtered = new List<EnemySetup>(pool.Count);
        foreach (var s in pool)
        {
            if (s?.spawnObjects != null && s.spawnObjects.Count > 0 &&
                !s.spawnObjects[0].ResourcePath.Contains("Director") &&
                !s.spawnObjects[0].ResourcePath.Contains("Ceiling Eye"))
                filtered.Add(s);
        }

        if (filtered.Count == 0)
        {
            SEMIDEAD.Logger.LogWarning("[WaveSpawner] Pool is empty after filtering Director enemies.");
            return false;
        }

        setup = filtered[Random.Range(0, filtered.Count)];
        return true;
    }

    // ---------------------------------------------------------------------------
    // Spawn point selection — picks randomly from edge LevelPoints (far from players)
    // ---------------------------------------------------------------------------

    private const float EdgeMinDistSq      = 225f; // 15 units
    private const float SpawnSpacingDistSq = 9f;   // 3 units

    private static Vector3 GetEdgeSpawnPosition(List<Vector3> recentPositions)
    {
        List<LevelPoint> all = SemiFunc.LevelPointsGetAll();
        if (all == null || all.Count == 0)
        {
            SEMIDEAD.Logger.LogWarning("[WaveSpawner] No LevelPoints found — using world origin.");
            return Vector3.zero;
        }

        var players    = SemiFunc.PlayerGetList();
        var edgePoints = new List<LevelPoint>(all.Count);
        foreach (var pt in all)
        {
            bool farFromAll = true;
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p == null) continue;
                    if ((pt.transform.position - p.transform.position).sqrMagnitude < EdgeMinDistSq)
                    {
                        farFromAll = false;
                        break;
                    }
                }
            }
            if (farFromAll) edgePoints.Add(pt);
        }

        List<LevelPoint> pool = edgePoints.Count > 0 ? edgePoints : all;

        if (recentPositions.Count > 0)
        {
            var spaced = new List<LevelPoint>(pool.Count);
            foreach (LevelPoint pt in pool)
            {
                bool tooClose = false;
                foreach (Vector3 used in recentPositions)
                {
                    if ((pt.transform.position - used).sqrMagnitude < SpawnSpacingDistSq)
                    { tooClose = true; break; }
                }
                if (!tooClose) spaced.Add(pt);
            }
            if (spaced.Count > 0) pool = spaced;
        }

        return pool[Random.Range(0, pool.Count)].transform.position;
    }

    private static Vector3 GetPlayerCentroid()
    {
        var players = SemiFunc.PlayerGetList();
        if (players == null || players.Count == 0) return Vector3.zero;

        Vector3 sum = Vector3.zero;
        foreach (var p in players) sum += p.transform.position;
        return sum / players.Count;
    }

    // ---------------------------------------------------------------------------
    // Enemy instantiation
    // ---------------------------------------------------------------------------

    private static EnemyParent? SpawnEnemy(EnemySetup setup, Vector3 position)
    {
        if (setup.spawnObjects == null || setup.spawnObjects.Count == 0)
        {
            SEMIDEAD.Logger.LogWarning($"[WaveSpawner] EnemySetup '{setup.name}' has no spawnObjects.");
            return null;
        }

        string resourcePath = setup.spawnObjects[0].ResourcePath;

        GameObject? go;
        if (SemiFunc.IsMultiplayer())
        {
            go = PhotonNetwork.InstantiateRoomObject(resourcePath, position, Quaternion.identity);
        }
        else
        {
            var prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab == null)
            {
                SEMIDEAD.Logger.LogWarning($"[WaveSpawner] Resources.Load failed for '{resourcePath}'.");
                return null;
            }
            go = Object.Instantiate(prefab, position, Quaternion.identity);
        }

        if (go == null)
        {
            SEMIDEAD.Logger.LogWarning($"[WaveSpawner] Spawn returned null for '{resourcePath}'.");
            return null;
        }

        EnemyParent? enemy = go.GetComponent<EnemyParent>();
        if (enemy == null)
        {
            SEMIDEAD.Logger.LogWarning($"[WaveSpawner] No EnemyParent on spawned object '{go.name}'.");
            return null;
        }

        return enemy;
    }
}
