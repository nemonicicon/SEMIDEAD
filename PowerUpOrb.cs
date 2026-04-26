using Photon.Pun;
using UnityEngine;

namespace SEMIDEAD;

/// <summary>
/// Host-side pickup logic attached to a Photon room object (enemy valuable orb prefab).
/// Detects when a player walks within PickupRadius and activates the power-up.
/// PhotonNetwork.Destroy removes the room object on all clients simultaneously.
/// </summary>
public class PowerUpOrb : MonoBehaviour
{
    public PowerUpType Type { get; set; }

    private const float PickupRadius = 2.5f;
    private const float Lifetime     = 30f;

    private float _remaining;
    private bool  _loggedStart;

    private void Awake() => _remaining = Lifetime;

    private void Update()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        if (!_loggedStart)
        {
            _loggedStart = true;
            SEMIDEAD.Logger.LogInfo($"[PowerUpOrb] {Type} orb active at {transform.position}. Lifetime={Lifetime}s.");
        }

        _remaining -= Time.deltaTime;
        if (_remaining <= 0f)
        {
            SEMIDEAD.Logger.LogInfo($"[PowerUpOrb] {Type} orb expired.");
            DestroyOrb();
            return;
        }

        var players = SemiFunc.PlayerGetList();
        if (players == null || players.Count == 0)
        {
            SEMIDEAD.Logger.LogWarning("[PowerUpOrb] PlayerGetList returned empty — cannot check proximity.");
            return;
        }

        foreach (PlayerAvatar player in players)
        {
            if (player == null || player.isDisabled) continue;
            float distSq = (player.transform.position - transform.position).sqrMagnitude;
            if (distSq <= PickupRadius * PickupRadius)
            {
                SEMIDEAD.Logger.LogInfo($"[PowerUpOrb] {player.playerName} picked up {Type} (dist={Mathf.Sqrt(distSq):F1}f).");
                PowerUpManager.Instance?.ActivatePowerUp(Type, player);
                DestroyOrb();
                return;
            }
        }
    }

    private void DestroyOrb()
    {
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.Destroy(gameObject);
        else
            Destroy(gameObject);
    }
}
