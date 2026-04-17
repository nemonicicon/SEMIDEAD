using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SEMIDEAD;

/// <summary>
/// Broadcast announcements spoken simultaneously by all players.
///
/// Triggers:
///   Wave start    — "WAVE 1", "WAVE 2", etc.
///   First Blood   — first enemy kill of the level, once only
///   Multi-kill    — rapid kills within 2.5s window (team-wide):
///                     Double Kill → Triple Kill → Overkill → Monster Kill
///                   Each tier fires once per chain; chain resets on break.
///   Killing spree — cumulative match-wide enemy kills (NOT reset per wave):
///                     5 → Killing Spree, 10 → Rampage, 15 → Dominating, 20 → Godlike
///   Betrayal      — player kills another player ("BETRAYAL"/"TEAM KILL"/"TRAITOR")
///                   Only fires on confirmed kill, not on any hit.
///   Suicide       — player dies from fall/environment (no enemy or player attacker)
///
/// Kill attribution uses two trackers updated before death fires:
///   _recentPlayerHits — last player-shot victim, from HurtCollider.onImpactPlayer
///   _recentEnemyHits  — last enemy hit on each player, from PlayerHealth.Hurt(enemyIndex)
/// On PlayerDeathHead.Trigger, whichever tracker has a live entry within 5s wins.
/// </summary>
public class AnnouncerSystem : MonoBehaviour
{
    public static AnnouncerSystem? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private const float MultiKillWindow = 2.5f;
    private const float AttackerWindow  = 5f;   // how long a hit stays "live" for attribution

    private static readonly (int threshold, string text)[] SpreeThresholds =
    {
        ( 5, "KILLING SPREE"),
        (10, "RAMPAGE"),
        (15, "DOMINATING"),
        (20, "GODLIKE"),
        (50, "HOLY SHIT!"),
        (60, "UNSTOPPABLE!"),
        (70, "EXTRACT ALREADY DAMMIT"),
    };

    private static readonly string[] BetrayalLines  = { "BETRAYAL", "TEAM KILL", "TRAITOR" };
    private static readonly string[] LastStandLines  = { "LAST PLAYER STANDING", "DOWN TO ONE!", "LONE SURVIVOR!" };

    // ---------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------

    // Per-level only
    private bool              _firstBloodFired;
    private int               _matchKillCount;             // cumulative across all waves
    private readonly HashSet<int> _spreeAnnouncedThresholds = new();

    // Multi-kill chain (reset per wave)
    private int               _multiKillCount;
    private float             _lastKillTime;
    private readonly HashSet<int> _chainAnnounced = new();

    // Per-wave kill counts for MVP announcement
    private readonly Dictionary<string, int> _waveKillCounts = new();

    // Kill attribution — static so patches can write to them without an instance reference
    private static readonly Dictionary<PlayerAvatar, (PlayerAvatar attacker, float hitTime)>
        _recentPlayerHits = new();
    private static readonly Dictionary<PlayerAvatar, float>
        _recentEnemyHits = new();

    // Current shooter — recorded from ShootBulletRPC for betrayal detection
    private static PlayerAvatar? _currentShooter;
    private static float         _currentShooterTime;

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

    public static AnnouncerSystem Create()
    {
        var go = new GameObject("SEMIDEAD_AnnouncerSystem");
        return go.AddComponent<AnnouncerSystem>();
    }

    // ---------------------------------------------------------------------------
    // Level setup — full reset.
    // ---------------------------------------------------------------------------

    public void OnLevelSetup()
    {
        _firstBloodFired = false;
        _matchKillCount  = 0;
        _multiKillCount  = 0;
        _lastKillTime    = 0f;
        _spreeAnnouncedThresholds.Clear();
        _chainAnnounced.Clear();
        _waveKillCounts.Clear();
        _recentPlayerHits.Clear();
        _recentEnemyHits.Clear();
        _currentShooter     = null;
        _currentShooterTime = 0f;
    }

    // ---------------------------------------------------------------------------
    // Wave start — resets multi-kill chain only. Spree runs match-wide.
    // ---------------------------------------------------------------------------

    public void OnWaveStart(int waveNumber)
    {
        _multiKillCount = 0;
        _chainAnnounced.Clear();
        _waveKillCounts.Clear();
        SpeakAllPlayers($"WAVE {waveNumber}");
    }

    // ---------------------------------------------------------------------------
    // Enemy killed — called from EnemyHealthPatch on every enemy death.
    // ---------------------------------------------------------------------------

    public void OnEnemyKilled(Vector3 position)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        // Track per-wave kills for MVP (nearest player gets credit).
        PlayerAvatar? killer = GetNearestLivingPlayer(position);
        if (killer != null)
        {
            _waveKillCounts.TryGetValue(killer.playerName, out int prev);
            _waveKillCounts[killer.playerName] = prev + 1;
        }

        // First blood: fires once with player name.
        if (!_firstBloodFired)
        {
            _firstBloodFired = true;
            string killerName = killer?.playerName ?? "UNKNOWN";
            SpeakAllPlayers($"{killerName} drew first blood!");
            _lastKillTime   = Time.time;
            _multiKillCount = 1;
            _chainAnnounced.Clear();
            _matchKillCount++;
            CheckSpree();
            return;
        }

        _matchKillCount++;

        // Multi-kill chain.
        float now = Time.time;
        if (now - _lastKillTime <= MultiKillWindow)
        {
            _multiKillCount++;
        }
        else
        {
            _multiKillCount = 1;
            _chainAnnounced.Clear();
        }
        _lastKillTime = now;

        int level = Mathf.Min(_multiKillCount, 8);
        if (level >= 2 && !_chainAnnounced.Contains(level))
        {
            _chainAnnounced.Add(level);
            string multiText = level switch
            {
                2 => "DOUBLE KILL",
                3 => "TRIPLE KILL",
                4 => "MULTI KILL",
                5 => "MEGA KILL",
                6 => "ULTRA KILL",
                7 => "MONSTER KILL",
                _ => "LUDICROUS KILL",  // 8+
            };
            SpeakAllPlayers(multiText);
        }

        CheckSpree();
    }

    public void AnnounceWaveMVP()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (_waveKillCounts.Count == 0) return;

        string? mvp = null;
        int maxKills = 0;
        foreach (var kv in _waveKillCounts)
            if (kv.Value > maxKills) { maxKills = kv.Value; mvp = kv.Key; }

        if (mvp != null)
        {
            SpeakAllPlayers($"{mvp} led the slaughter!");
            Logger.LogInfo($"[AnnouncerSystem] Wave MVP: {mvp} with {maxKills} kills.");
        }

        _waveKillCounts.Clear();
    }

    private void CheckSpree()
    {
        foreach (var (threshold, text) in SpreeThresholds)
        {
            if (_matchKillCount >= threshold && !_spreeAnnouncedThresholds.Contains(threshold))
            {
                _spreeAnnouncedThresholds.Add(threshold);
                SpeakAllPlayers(text);
                return;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Kill attribution — called from patches, static so no instance needed.
    // ---------------------------------------------------------------------------

    /// <summary>Record that a player's bullet hit another player.</summary>
    internal static void RecordPlayerHit(PlayerAvatar victim, PlayerAvatar attacker)
    {
        _recentPlayerHits[victim] = (attacker, Time.time);
    }

    /// <summary>Record that an enemy damaged a player (enemyIndex != -1).</summary>
    internal static void RecordEnemyHit(PlayerAvatar victim)
    {
        _recentEnemyHits[victim] = Time.time;
    }

    /// <summary>Record the player currently firing a gun (for betrayal attribution).</summary>
    internal static void RecordCurrentShooter(PlayerAvatar attacker)
    {
        _currentShooter     = attacker;
        _currentShooterTime = Time.time;
    }

    /// <summary>Returns the current shooter if within the attribution window, else null.</summary>
    internal static PlayerAvatar? GetCurrentShooter(float window) =>
        _currentShooter != null && Time.time - _currentShooterTime <= window
            ? _currentShooter : null;

    private static PlayerAvatar? GetNearestLivingPlayer(Vector3 pos)
    {
        var players = SemiFunc.PlayerGetList();
        if (players == null) return null;

        PlayerAvatar? nearest  = null;
        float         bestDist = float.MaxValue;
        foreach (var p in players)
        {
            if (p == null || p.isDisabled) continue;
            float d = (p.transform.position - pos).sqrMagnitude;
            if (d < bestDist) { bestDist = d; nearest = p; }
        }
        return nearest;
    }

    /// <summary>
    /// Called when a player's death head spawns. Checks attribution within
    /// AttackerWindow to decide between BETRAYAL, silence (enemy kill), or SUICIDE.
    /// </summary>
    internal static void OnPlayerDied(PlayerAvatar player)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        bool killedByPlayer = _recentPlayerHits.TryGetValue(player, out var ph)
                              && Time.time - ph.hitTime <= AttackerWindow;
        bool killedByEnemy  = _recentEnemyHits.TryGetValue(player, out float et)
                              && Time.time - et <= AttackerWindow;

        _recentPlayerHits.Remove(player);
        _recentEnemyHits.Remove(player);

        // Always announce the player going down.
        SpeakAllPlayers($"{player.playerName} is down!");

        if (killedByPlayer && SemiFunc.IsMultiplayer())
        {
            SpeakAllPlayers(BetrayalLines[Random.Range(0, BetrayalLines.Length)]);
            Logger.LogInfo($"[AnnouncerSystem] Team kill — {player.playerName} killed by {ph.attacker?.playerName}.");
        }
        else if (!killedByEnemy && !killedByPlayer)
        {
            SpeakAllPlayers("SUICIDE");
            Logger.LogInfo($"[AnnouncerSystem] Suicide — {player.playerName}.");
        }

        // Count remaining alive players (explicitly exclude the dying player in case
        // isDisabled hasn't been set yet when this postfix fires).
        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        int aliveCount = 0;
        foreach (var p in players)
            if (p != null && p != player && !p.isDisabled) aliveCount++;

        if (aliveCount == 0)
            SpeakAllPlayers("GAME OVER, MAN! GAME OVER!");
        else if (aliveCount == 1)
            SpeakAllPlayers(LastStandLines[Random.Range(0, LastStandLines.Length)]);
    }

    // ---------------------------------------------------------------------------
    // SpeakAllPlayers — every player says the text simultaneously.
    // ---------------------------------------------------------------------------

    internal static void SpeakAllPlayers(string text)
    {
        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        foreach (PlayerAvatar player in players)
        {
            if (player == null) continue;

            if (!SemiFunc.IsMultiplayer())
                player.voiceChat?.ttsVoice?.TTSSpeakNow(text, false);
            else
            {
                var pv = player.GetComponent<PhotonView>();
                if (pv != null)
                    pv.RPC("ChatMessageSendRPC", RpcTarget.All, new object[] { text, false });
            }
        }

        Logger.LogInfo($"[AnnouncerSystem] >> {text}");
    }
}

// ---------------------------------------------------------------------------
// Betrayal detection — two-patch approach.
//
// Root cause of old approach failure: HurtCollider.PlayerHurt has a
//   `if (GameManager.Multiplayer() && !_player.photonView.IsMine) return;`
// guard, so onImpactPlayer only fires on the VICTIM's machine, never on
// the master client. The old listener-on-onImpactPlayer approach was always
// a no-op on the host.
//
// New approach:
//   1. ShootBulletRPC Postfix records the current shooter on master client.
//   2. HurtCollider.PlayerHurt Prefix intercepts the call on master client
//      BEFORE the IsMine check, capturing (victim, attacker) for attribution.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(ItemGun), "ShootBulletRPC")]
static class TeamKillDetectionPatch
{
    [HarmonyPostfix]
    private static void Postfix(ItemGun __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var physGrab = __instance.GetComponent<PhysGrabObject>();
        if (physGrab?.playerGrabbing == null || physGrab.playerGrabbing.Count == 0) return;

        PlayerAvatar? attacker = physGrab.playerGrabbing[0].playerAvatar;
        if (attacker == null) return;

        AnnouncerSystem.RecordCurrentShooter(attacker);
    }
}

[HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
static class HurtColliderPlayerHurtPatch
{
    private const float ShooterWindow = 0.5f; // max seconds between shoot and hit landing

    // Prefix runs BEFORE the IsMine check inside PlayerHurt, so we capture
    // the victim on master client even though PlayerHurt would return early.
    [HarmonyPrefix]
    private static void Prefix(HurtCollider __instance, PlayerAvatar _player)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (__instance.enemyHost != null) return; // enemy attack, not a player gun
        if (_player == null) return;

        PlayerAvatar? shooter = AnnouncerSystem.GetCurrentShooter(ShooterWindow);
        if (shooter == null || shooter == _player) return; // no shooter or self-damage

        AnnouncerSystem.RecordPlayerHit(_player, shooter);
        SEMIDEAD.Logger.LogInfo($"[AnnouncerSystem] Potential team hit: {shooter.playerName} → {_player.playerName}");
    }
}

// ---------------------------------------------------------------------------
// Records enemy hits on players so we can distinguish enemy kills from falls.
//
// Three patches are needed:
//   PlayerHealth.Hurt — fires on the OWNER's machine (singleplayer + host's own hits).
//   PlayerHealth.HurtOtherRPC — fires on the MASTER CLIENT for ALL players in MP.
//     Enemies call HurtOther() on master; it sends HurtOtherRPC to all clients.
//     We patch the RPC to record enemy hits on non-host players.
//   PlayerTumble.OverrideEnemyHurt — fires on the MASTER CLIENT when any enemy
//     grabs/throws a player. The tumble damage path calls Hurt with enemyIndex=-1
//     (no enemy attribution), so the above two patches miss it. Recording at the
//     point of the throw covers the entire subsequent tumble/impact sequence.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PlayerHealth), "Hurt")]
static class PlayerHealthHurtPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerHealth __instance, int enemyIndex)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (enemyIndex == -1) return;

        var avatar = Traverse.Create(__instance).Field("playerAvatar").GetValue<PlayerAvatar>();
        if (avatar != null)
            AnnouncerSystem.RecordEnemyHit(avatar);
    }
}

[HarmonyPatch(typeof(PlayerHealth), "HurtOtherRPC")]
static class PlayerHealthHurtOtherRPCPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerHealth __instance, int enemyIndex)
    {
        // Only on master client — enemies run on master and call HurtOther(),
        // which sends HurtOtherRPC. This lets us track hits on non-host players.
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (enemyIndex == -1) return;

        var avatar = Traverse.Create(__instance).Field("playerAvatar").GetValue<PlayerAvatar>();
        if (avatar != null)
            AnnouncerSystem.RecordEnemyHit(avatar);
    }
}

// ---------------------------------------------------------------------------
// Records that an enemy grabbed/threw a player (tumble path).
// OverrideEnemyHurt fires right as the enemy initiates the throw, which is
// before any tumble-impact Hurt call (which uses enemyIndex=-1).
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.OverrideEnemyHurt))]
static class PlayerTumbleOverrideEnemyHurtPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerTumble __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var avatar = Traverse.Create(__instance).Field("playerAvatar").GetValue<PlayerAvatar>();
        if (avatar != null)
            AnnouncerSystem.RecordEnemyHit(avatar);
    }
}

// ---------------------------------------------------------------------------
// On death, resolves attribution and fires BETRAYAL or SUICIDE if applicable.
// Runs alongside ReviveSystem's existing patch on the same method.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PlayerDeathHead), "Trigger")]
static class PlayerDeathAnnouncerPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerDeathHead __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (__instance.playerAvatar == null) return;
        AnnouncerSystem.OnPlayerDied(__instance.playerAvatar);
    }
}
