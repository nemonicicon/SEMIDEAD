using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace SEMIDEAD.Patches;

/// <summary>
/// Triggers a small explosion at the bullet's hit point each time the shotgun
/// (Item Gun Shotgun) lands a shot.
///
/// Network model:
///   Master client  — ShootBulletRPC Postfix fires (master-only guard in original).
///                    Spawns explosion locally with full damage + visual.
///                    Raises Photon custom event (code 43) to all other clients.
///   Other clients  — IOnEventCallback listener receives the event and spawns a
///                    visual-only explosion (no HurtCollider damage) locally.
///
/// The explosion prefab ("Effects/Part Prefab Explosion") is a vanilla Resources
/// asset used by grenades; it is not a Photon room object.
/// </summary>
[HarmonyPatch(typeof(ItemGun), "ShootBulletRPC")]
static class ShotgunExplosionPatch
{
    private const byte  PhotonEventCode = 43;
    private const float ExplosionSize   = 0.4f;   // medium-small — grenade uses 1.2f
    private const int   PlayerDamage    = 30;
    private const int   EnemyDamage     = 80;

    // ---------------------------------------------------------------------------
    // Photon event listener — installed once, on all clients.
    // ---------------------------------------------------------------------------

    private static EventListener? _listener;

    internal static void RegisterListener()
    {
        if (_listener != null) return;
        // Do not register before Photon is connected — calling AddCallbackTarget
        // pre-connection breaks region/lobby loading. ChangeLevel_Postfix calls this
        // on every level load, so it will register on the first gameplay level when
        // Photon is definitely connected.
        if (!PhotonNetwork.IsConnected) return;
        _listener = new EventListener();
        PhotonNetwork.AddCallbackTarget(_listener);
    }

    private class EventListener : IOnEventCallback
    {
        public void OnEvent(EventData ev)
        {
            if (ev.Code != PhotonEventCode) return;
            if (SemiFunc.IsMasterClientOrSingleplayer()) return; // host already spawned it

            var pos = (Vector3)ev.CustomData;
            SpawnExplosion(pos, 0, 0, visualOnly: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Harmony postfix — runs on master client only (ShootBulletRPC has MasterOnlyRPC guard)
    // ---------------------------------------------------------------------------

    [HarmonyPostfix]
    private static void Postfix(ItemGun __instance, Vector3 _endPosition, bool _hit)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!_hit) return;
        if (!__instance.gameObject.name.StartsWith("Item Gun Shotgun")) return;

        SpawnExplosion(_endPosition, PlayerDamage, EnemyDamage, visualOnly: false);

        if (SemiFunc.IsMultiplayer())
            BroadcastExplosion(_endPosition);
    }

    private static void BroadcastExplosion(Vector3 pos)
    {
        // Pass Vector3 directly — PUN2 registers Vector3 as a natively serialisable type.
        PhotonNetwork.RaiseEvent(
            PhotonEventCode,
            pos,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
    }

    // ---------------------------------------------------------------------------
    // Explosion spawning
    // ---------------------------------------------------------------------------

    private static void SpawnExplosion(Vector3 pos, int playerDmg, int enemyDmg, bool visualOnly)
    {
        var prefab = Resources.Load<GameObject>("Effects/Part Prefab Explosion");
        if (prefab == null)
        {
            SEMIDEAD.Logger.LogWarning("[ShotgunExplosion] 'Effects/Part Prefab Explosion' not found in Resources.");
            return;
        }

        var go  = Object.Instantiate(prefab, pos, Quaternion.identity);
        var exp = go.GetComponent<ParticlePrefabExplosion>();
        if (exp == null) return;

        exp.explosionSize        = ExplosionSize;
        exp.explosionDamage      = playerDmg;
        exp.explosionDamageEnemy = enemyDmg;
        exp.onlyParticleEffect   = visualOnly;
    }
}
