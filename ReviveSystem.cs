using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SEMIDEAD;

/// <summary>
/// Two revive mechanics for wave mode:
///
///   1. Pickup revive — a teammate grabbing the death head within 30 seconds
///      immediately revives the downed player.
///      After the 30-second window the pickup window closes; the player must wait
///      for the next wave-start revive.
///
///   2. Wave-start revive — OnWaveStart() is called from WaveManager.StartWave().
///      Every player whose isDisabled flag is still set is revived before enemies
///      spawn, so the full squad enters each wave alive.
///
/// Host-only: all Revive() calls are issued on master client only.
/// PlayerAvatarRespawnPatch (StartingPistol.cs) fires on ReviveRPC and drops a
/// handgun at the revived player's position in both revive paths.
/// </summary>
public class ReviveSystem : MonoBehaviour
{
    public static ReviveSystem? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private const float PickupReviveWindow = 30f;

    /// <summary>Maps a dead player to the Time.time value when they died.</summary>
    private readonly Dictionary<PlayerAvatar, float> _deathTimes = new();

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

    public static ReviveSystem Create()
    {
        var go = new GameObject("SEMIDEAD_ReviveSystem");
        return go.AddComponent<ReviveSystem>();
    }

    // ---------------------------------------------------------------------------
    // Called from PlayerDeathHeadTriggerPatch when a player's death head spawns.
    // ---------------------------------------------------------------------------

    public void OnPlayerDied(PlayerDeathHead head)
    {
        if (head?.playerAvatar == null) return;
        _deathTimes[head.playerAvatar] = Time.time;
        Logger.LogInfo($"[ReviveSystem] {head.playerAvatar.playerName} down — {PickupReviveWindow:F0}s pickup window open.");
    }

    // ---------------------------------------------------------------------------
    // Per-frame: check whether any dead player's head is being grabbed.
    // ---------------------------------------------------------------------------

    private void Update()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        foreach (var kv in _deathTimes.ToList())
        {
            PlayerAvatar player = kv.Key;
            float deathTime     = kv.Value;

            // Already revived or destroyed — clean up.
            if (player == null)
            {
                _deathTimes.Remove(kv.Key);
                continue;
            }
            if (!player.isDisabled)
            {
                _deathTimes.Remove(player);
                continue;
            }

            // Window closed — no more pickup revive until next wave start.
            if (Time.time - deathTime > PickupReviveWindow) continue;

            var head = player.playerDeathHead;
            if (head?.physGrabObject == null) continue;

            if (head.physGrabObject.playerGrabbing.Count > 0)
            {
                Logger.LogInfo($"[ReviveSystem] Pickup revive — {player.playerName}.");
                _deathTimes.Remove(player);
                player.Revive(false);
                CharacterSystem.Instance?.TriggerSpeech(player, SpeechTrigger.Revived);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Wave-start: revive every player who is still dead.
    // Called from WaveManager.StartWave() before enemies are spawned.
    // ---------------------------------------------------------------------------

    public void OnWaveStart()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        int revived = 0;
        foreach (PlayerAvatar player in players)
        {
            if (player == null || !player.isDisabled) continue;
            player.Revive(false);
            CharacterSystem.Instance?.TriggerSpeech(player, SpeechTrigger.Revived);
            revived++;
        }

        _deathTimes.Clear();

        if (revived > 0)
            Logger.LogInfo($"[ReviveSystem] Wave start — revived {revived} dead player(s).");
    }

    // ---------------------------------------------------------------------------
    // Level reset — clear stale death tracking from the previous level.
    // ---------------------------------------------------------------------------

    public void OnLevelSetup()
    {
        _deathTimes.Clear();
    }
}

// ---------------------------------------------------------------------------
// Hooks PlayerDeathHead.Trigger() — fires on master client only (the method
// has its own IsMasterClient guard, but we also guard here for clarity).
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PlayerDeathHead), "Trigger")]
static class PlayerDeathHeadTriggerPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerDeathHead __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        ReviveSystem.Instance?.OnPlayerDied(__instance);
    }
}
