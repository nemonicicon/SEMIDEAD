using System.Collections;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SEMIDEAD;

/// <summary>
/// Spawns a Toy Monkey valuable at a truck ItemVolume position each level.
/// Spawn piggybacks on PunManager.TruckPopulateItemVolumes — same timing and
/// Photon-ready window as the starting pistol.
///
/// After spawn, ItemToggle + ItemEquippable + ItemAttributes are added so the
/// monkey behaves like a proper inventory item (E to arm, hotbar-equippable).
/// ItemAttributesSafePatch guards ItemAttributes.Start() against the null item
/// reference that would otherwise crash since we have no Item ScriptableObject.
///
/// Pressing E while holding: ToyMonkeyTrapActivated() starts cymbals + 35f attract.
/// After AttractDuration (7s): EnemyHealth.Hurt(9999) in BlastRadius, explosion VFX,
/// camera shake, then the valuable is destroyed.
/// </summary>
public class MonkeyBomb : MonoBehaviour
{
    public static MonkeyBomb? Instance { get; private set; }
    internal static ManualLogSource Logger => SEMIDEAD.Logger;

    internal const string MonkeyResourcePath = "Valuables/02 Small/Valuable Manor Toy Monkey";
    internal const float  SpawnChance        = 1.00f;
    internal const float  AttractDuration    = 7f;
    internal const float  BlastRadius        = 8f;
    internal const int    BlastDamage        = 9999;

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

    public static MonkeyBomb Create()
    {
        var go = new GameObject("SEMIDEAD_MonkeyBomb");
        return go.AddComponent<MonkeyBomb>();
    }

    // Called from RunManagerPatch — spawn handled by MonkeyBombTruckSpawnPatch.
    // When leaving a gameplay level, destroy any surviving MonkeyBomb Photon objects
    // so their ItemAttributes.Update() doesn't NullRef in the Shop/Lobby scene.
    public void OnLevelSetup()
    {
        if (IsGameplayLevel()) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var activators = Object.FindObjectsOfType<MonkeyBombActivator>();
        foreach (var activator in activators)
        {
            if (activator == null) continue;
            Logger.LogInfo("[MonkeyBomb] Cleaning up surviving MonkeyBomb on level transition.");
            if (SemiFunc.IsMultiplayer())
                PhotonNetwork.Destroy(activator.gameObject);
            else
                Object.Destroy(activator.gameObject);
        }
    }

    internal static bool IsGameplayLevel()
    {
        var rm = RunManager.instance;
        return rm != null && rm.levelCurrent != null && rm.levels != null
               && rm.levels.Contains(rm.levelCurrent);
    }
}

// ---------------------------------------------------------------------------
// Component added at runtime to the spawned Toy Monkey.
// Handles E-key arm → countdown → explosion.
// ---------------------------------------------------------------------------
public class MonkeyBombActivator : MonoBehaviour
{
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private ItemToggle?    _toggle;
    private ToyMonkeyTrap? _trap;
    private bool           _activated;
    private bool           _detonated;
    private AudioSource?   _musicSource;
    private float          _investigateTimer;

    private const float InvestigateInterval = 0.25f;

    private void Start()
    {
        _toggle = GetComponent<ItemToggle>();
        _trap   = GetComponent<ToyMonkeyTrap>();

        if (_toggle != null)
            _toggle.onToggle.AddListener(OnToggled);
        else
            Logger.LogWarning("[MonkeyBomb] MonkeyBombActivator: no ItemToggle found.");

        // Load the boombox music clip and wire up a looping AudioSource on this object.
        var boomboxPrefab = Resources.Load<GameObject>("Valuables/04 Big/Valuable Museum Boombox");
        if (boomboxPrefab != null)
        {
            var vbx = boomboxPrefab.GetComponent<ValuableBoombox>();
            if (vbx?.soundBoomboxMusic?.Sounds?.Length > 0)
            {
                _musicSource              = gameObject.AddComponent<AudioSource>();
                _musicSource.clip         = vbx.soundBoomboxMusic.Sounds[0];
                _musicSource.loop         = true;
                _musicSource.volume       = 0.8f;
                _musicSource.spatialBlend = 1f;
                _musicSource.minDistance  = 5f;
                _musicSource.maxDistance  = 30f;
                _musicSource.playOnAwake  = false;
                Logger.LogInfo("[MonkeyBomb] Boombox music source ready.");
            }
            else
                Logger.LogWarning("[MonkeyBomb] soundBoomboxMusic has no clips on boombox prefab.");
        }
        else
            Logger.LogWarning("[MonkeyBomb] Boombox prefab not found — no music will play.");
    }

    private void OnDestroy()
    {
        if (_toggle != null)
            _toggle.onToggle.RemoveListener(OnToggled);
        _musicSource?.Stop();
    }

    private void Update()
    {
        if (_detonated) return;
        if (!_activated) return;

        _investigateTimer -= Time.deltaTime;
        if (_investigateTimer <= 0f)
        {
            _investigateTimer = InvestigateInterval;
            PullAllEnemiesToMonkey();
        }
    }

    // Breaks enemies out of active Chase/ChaseBegin (which SetInvestigate ignores)
    // then redirects every enemy toward the monkey position.
    private void PullAllEnemiesToMonkey()
    {
        var director = EnemyDirector.instance;
        if (director == null) return;

        Vector3 monkeyPos = transform.position;

        foreach (EnemyParent ep in director.enemiesSpawned)
        {
            if (ep == null || !ep.Spawned || ep.Enemy == null) continue;
            Enemy enemy = ep.Enemy;

            // EnemyStateInvestigate.Set() skips enemies in Chase / ChaseBegin.
            // Force them back to Roaming so the redirect below can reach them.
            if (enemy.CurrentState == EnemyState.Chase ||
                enemy.CurrentState == EnemyState.ChaseBegin)
            {
                enemy.CurrentState = EnemyState.Roaming;
            }

            // Prevent the enemy from re-acquiring a player target until our next
            // pull call refreshes the timer (InvestigateInterval * 2 > InvestigateInterval).
            enemy.DisableChase(InvestigateInterval * 2f);

            // Direct the enemy to walk toward the monkey.
            if (enemy.HasStateInvestigate)
                enemy.StateInvestigate.Set(monkeyPos, false);
        }
    }

    private void Arm()
    {
        if (_activated) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        _activated = true;

        // Make cymbals audible anywhere — SpatialBlend 1f (3D) attenuates with distance
        // so even high volume values go quiet from across a room. Setting it to 0f
        // makes it a 2D sound heard at full volume everywhere on the map.
        if (_trap != null)
        {
            _trap.cymbal.Volume       *= 1.75f;
            _trap.cymbal.SpatialBlend  = 0f;
        }

        Logger.LogInfo("[MonkeyBomb] Armed — 7s fuse started.");

        // TrapActivateSyncRPC is the vanilla networked activation path on Trap.
        // It sets trapStart = true on all clients; ToyMonkeyTrap.Update() then calls
        // ToyMonkeyTrapActivated() locally — giving every client the animation,
        // mechanical loop, and cymbal sounds without a client mod.
        var photonView = GetComponent<PhotonView>();
        if (SemiFunc.IsMultiplayer() && photonView != null)
            photonView.RPC("TrapActivateSyncRPC", RpcTarget.All);
        else
            _trap?.ToyMonkeyTrapActivated();

        _musicSource?.Play();
        StartCoroutine(FuseCoroutine());
    }

    private void OnToggled()
    {
        if (_toggle == null || !_toggle.toggleState) return;
        if (SemiFunc.IsMasterClientOrSingleplayer())
        {
            Arm();
        }
        else
        {
            // Client: ask the master to arm via RPC.
            var pv = GetComponent<PhotonView>();
            if (pv != null) pv.RPC("ArmBombRPC", RpcTarget.MasterClient);
        }
    }

    [PunRPC]
    public void ArmBombRPC()
    {
        Arm();
    }

    private IEnumerator FuseCoroutine()
    {
        yield return new WaitForSeconds(MonkeyBomb.AttractDuration);
        if (_detonated) yield break;
        _detonated = true;
        Detonate();
    }

    private void Detonate()
    {
        _musicSource?.Stop();
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        Vector3 blastPos = transform.position;
        Logger.LogInfo($"[MonkeyBomb] Detonating at {blastPos} (radius {MonkeyBomb.BlastRadius}).");

        // Kill enemies in blast radius.
        int hits = 0;
        foreach (EnemyHealth eh in Object.FindObjectsOfType<EnemyHealth>())
        {
            if ((eh.transform.position - blastPos).sqrMagnitude <= MonkeyBomb.BlastRadius * MonkeyBomb.BlastRadius)
            {
                eh.Hurt(MonkeyBomb.BlastDamage, (eh.transform.position - blastPos).normalized);
                hits++;
            }
        }
        Logger.LogInfo($"[MonkeyBomb] Hit {hits} enemies.");
        CharacterSystem.Instance?.TriggerSpeechNearestTo(blastPos, SpeechTrigger.MonkeyBombExplosion);

        // Explosion VFX — ParticlePrefabExplosion.Start() auto-plays the particles
        // and reads explosionSize before it fires (set here in the same frame as Instantiate).
        var explosionPrefab = Resources.Load<GameObject>("Effects/Part Prefab Explosion");
        if (explosionPrefab != null)
        {
            var vfxGo = Object.Instantiate(explosionPrefab, blastPos, Quaternion.identity);
            var ppe = vfxGo.GetComponent<ParticlePrefabExplosion>();
            if (ppe != null)
            {
                ppe.explosionSize       = 2f;   // read by Start() next frame to scale particles
                ppe.onlyParticleEffect  = true;  // skip HurtCollider — we handled damage above
                ppe.SkipHurtColliderSetup = true;
            }
        }
        else
        {
            Logger.LogWarning("[MonkeyBomb] 'Effects/Part Prefab Explosion' not found — no VFX.");
        }

        // Camera shake.
        GameDirector.instance?.CameraImpact?.ShakeDistance(10f, 6f, 12f, blastPos, 0.2f);
        GameDirector.instance?.CameraShake?.ShakeDistance(5f, 6f, 12f, blastPos, 0.5f);

        // Destroy the valuable.
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.Destroy(gameObject);
        else
            Object.Destroy(gameObject);
    }
}

// ---------------------------------------------------------------------------
// Spawns the Toy Monkey at a truck ItemVolume position.
// Fires in the Prefix of TruckPopulateItemVolumes — ItemVolumes are truck floor
// slots (confirmed valid positions) and are destroyed at end of the method,
// so positions must be read here in the Prefix.
// After spawn: adds ItemToggle (E-key activation) and MonkeyBombActivator.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PunManager), "TruckPopulateItemVolumes")]
static class MonkeyBombTruckSpawnPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!MonkeyBomb.IsGameplayLevel()) return;
        if (Random.value > MonkeyBomb.SpawnChance) return;

        // ItemVolumes are destroyed at the end of this method — read positions now.
        ItemVolume[] volumes = Object.FindObjectsOfType<ItemVolume>();
        if (volumes.Length == 0)
        {
            MonkeyBomb.Logger.LogWarning("[MonkeyBomb] No ItemVolumes found — skipping spawn.");
            return;
        }

        // Spawn one monkey per player, using separate ItemVolume slots from the end
        // (the last slot(s) are furthest from the pistol slot at index 0).
        const int playerCount = 2;

        // Pre-fetch grenade icon once for all monkeys.
        UnityEngine.Sprite? grenadeIcon = null;
        var dict = StatsManager.instance?.itemDictionary;
        if (dict != null)
        {
            foreach (var kvp in dict)
            {
                if (!kvp.Key.Contains("Grenade")) continue;
                var gp = kvp.Value?.prefab?.Prefab;
                if (gp == null) continue;
                grenadeIcon = gp.GetComponent<ItemEquippable>()?.ItemIcon;
                if (grenadeIcon != null)
                {
                    MonkeyBomb.Logger.LogInfo($"[MonkeyBomb] Borrowed icon from '{kvp.Key}'.");
                    break;
                }
            }
        }

        // Only load singleplayer prefab once.
        GameObject? spPrefab = null;
        if (!SemiFunc.IsMultiplayer())
        {
            spPrefab = Resources.Load<GameObject>(MonkeyBomb.MonkeyResourcePath);
            if (spPrefab == null)
            {
                MonkeyBomb.Logger.LogWarning("[MonkeyBomb] Resources.Load failed.");
                return;
            }
        }

        for (int i = 0; i < playerCount; i++)
        {
            // Use slots from the end of the array; clamp to index 0 if fewer slots than players.
            int slotIndex = Mathf.Max(0, volumes.Length - 1 - i);
            Vector3 spawnPos = volumes[slotIndex].transform.position;
            MonkeyBomb.Logger.LogInfo($"[MonkeyBomb] Spawning monkey {i + 1}/{playerCount} at {spawnPos}.");

            GameObject? go;
            if (SemiFunc.IsMultiplayer())
                // Pass "MonkeyBomb" as instantiation data so every client's
                // ToyMonkeyTrapSetupPatch can add the interaction components.
                go = PhotonNetwork.InstantiateRoomObject(MonkeyBomb.MonkeyResourcePath, spawnPos,
                         Quaternion.identity, 0, new object[] { "MonkeyBomb" });
            else
                go = Object.Instantiate(spPrefab!, spawnPos, Quaternion.identity);

            if (go == null)
            {
                MonkeyBomb.Logger.LogWarning($"[MonkeyBomb] Spawn returned null for monkey {i + 1}.");
                continue;
            }

            var toggle = go.AddComponent<ItemToggle>();
            toggle.onToggle = new UnityEngine.Events.UnityEvent();
            var equippable = go.AddComponent<ItemEquippable>();
            go.AddComponent<ItemAttributes>();
            go.AddComponent<MonkeyBombActivator>();

            if (grenadeIcon != null)
                equippable.ItemIcon = grenadeIcon;
        }

        MonkeyBomb.Logger.LogInfo($"[MonkeyBomb] Spawned {playerCount} Toy Monkey(s).");
    }
}

// ---------------------------------------------------------------------------
// ItemAttributes.Start() crashes when item == null because it reads
// this.item.itemName, this.item.itemType, etc. Our dynamically-added
// ItemAttributes has no Item ScriptableObject, so we intercept Start(),
// set safe defaults via Traverse, and skip the original.
// This only fires for our monkey bomb — all normal items have item != null.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(ItemAttributes), "Start")]
static class ItemAttributesSafePatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemAttributes __instance)
    {
        if (__instance.item != null) return true; // normal item — run original Start()

        // Our monkey bomb: set the minimum fields needed by ItemEquippable's equip flow.
        var t = Traverse.Create(__instance);
        t.Field("itemName").SetValue("Monkey Bomb");
        t.Field("instanceName").SetValue("MonkeyBomb");
        t.Field("physGrabObject").SetValue(__instance.GetComponent<PhysGrabObject>());
        t.Field("itemToggle").SetValue(__instance.GetComponent<ItemToggle>());
        t.Field("itemEquippable").SetValue(__instance.GetComponent<ItemEquippable>());

        MonkeyBomb.Logger.LogInfo("[MonkeyBomb] ItemAttributes.Start() — safe defaults applied.");
        return false; // skip original (avoids null-ref on this.item.*)
    }
}

// ---------------------------------------------------------------------------
// Runs on EVERY client when a ToyMonkeyTrap is instantiated via Photon.
// Reads the instantiation data set by MonkeyBombTruckSpawnPatch — if it says
// "MonkeyBomb", adds the interaction components so non-host clients can arm it.
// The host already has these components (added synchronously in the Prefix);
// the null check prevents duplicates.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(ToyMonkeyTrap), "Start")]
static class ToyMonkeyTrapSetupPatch
{
    [HarmonyPostfix]
    private static void Postfix(ToyMonkeyTrap __instance)
    {
        var pv            = __instance.GetComponent<PhotonView>();
        bool alreadySetup = __instance.GetComponent<MonkeyBombActivator>() != null;
        bool isMonkeyBomb = pv?.InstantiationData?.Length > 0
                            && pv.InstantiationData[0] as string == "MonkeyBomb";
        bool isMaster     = SemiFunc.IsMasterClientOrSingleplayer();

        // Diagnostic: fires on every client so we can confirm the patch reaches non-host clients.
        MonkeyBomb.Logger.LogInfo(
            $"[MonkeyBomb] ToyMonkeyTrap.Start() patch — isMaster={isMaster} isMonkeyBomb={isMonkeyBomb} alreadySetup={alreadySetup}");

        if (!isMonkeyBomb || alreadySetup) return;

        // Non-host client — add the same interaction components the host added.
        var toggle = __instance.gameObject.AddComponent<ItemToggle>();
        toggle.onToggle = new UnityEngine.Events.UnityEvent();
        __instance.gameObject.AddComponent<ItemEquippable>();
        __instance.gameObject.AddComponent<ItemAttributes>();
        __instance.gameObject.AddComponent<MonkeyBombActivator>();

        MonkeyBomb.Logger.LogInfo("[MonkeyBomb] Client-side MonkeyBomb components added.");
    }
}
