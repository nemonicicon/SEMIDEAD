# REPO — Technical Context Reference
### For use with Claude Code and future SEMIDEAD development
### Last updated: v1.2 — Phase 1 live testing pass

---

## Overview

This document captures confirmed internal details about R.E.P.O.'s codebase discovered via:
- dnSpy decompilation of Assembly-CSharp.dll (R.E.P.O. v0.3.2)
- Runtime log analysis during SEMIDEAD development
- Soda_Perks mod source code (SadTocino, confirmed working patterns)
- REPOLib modding wiki (repomods.com)

All findings confirmed against R.E.P.O. v0.3.2.

---

## Networking Architecture

- **Engine:** Unity 2022.3.67 with Photon PUN (Photon Unity Networking)
- **Authority model:** Master client authoritative — most game logic runs on host only
- **Master client check:** `SemiFunc.IsMasterClientOrSingleplayer()` — always guard state-changing code with this
- **RPC pattern:** Methods decorated with `[PunRPC]` are called via `photonView.RPC("MethodName", RpcTarget.All, ...)`
- **Singleplayer fallback:** Most RPC methods have a direct call path: `if (!GameManager.Multiplayer()) { MethodRPC(...); return; }`
- **REPOLib Network Events:** Cleaner alternative to raw Photon RPCs for mod-to-mod communication:

```csharp
// Create once in Awake:
public static NetworkedEvent MyEvent = new NetworkedEvent("EventName", HandleEvent);

// Raise:
MyEvent.RaiseEvent(data, NetworkingEvents.RaiseAll, SendOptions.SendReliable);
MyEvent.RaiseEvent(data, NetworkingEvents.RaiseOthers, SendOptions.SendReliable);
MyEvent.RaiseEvent(data, NetworkingEvents.RaiseMasterClient, SendOptions.SendReliable);
```

---

## Key Singleton Instances

| Class | Access | Purpose |
|-------|--------|---------|
| `StatsManager` | `StatsManager.instance` | Currency, player stats, save/load |
| `RunManager` | `RunManager.instance` | Level flow, lives, progression |
| `EnemyDirector` | `EnemyDirector.instance` | Enemy pool, spawn timing, difficulty |
| `GameDirector` | `GameDirector.instance` | Player list, game state |
| `LevelGenerator` | `LevelGenerator.Instance` | Map generation, enemy parent transform |
| `PunManager` | `PunManager.instance` | Photon stat sync |
| `AssetManager` | `AssetManager.instance` | Prefab/asset references |
| `PhysGrabber` | `PhysGrabber.instance` | Local player grabber |
| `PlayerAvatar` | `PlayerAvatar.instance` | Local player avatar |

---

## Currency (SURPLUS) System

### Storage
- Currency stored in `StatsManager.instance.runStats["currency"]`
- `runStats` is `Dictionary<string, int>` — **shared team pool, not per-player**

### Read
```csharp
int current = StatsManager.instance.GetRunStatCurrency();
// or directly:
int current = StatsManager.instance.runStats["currency"];
```

### Write (host only)
```csharp
if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
int newValue = StatsManager.instance.GetRunStatCurrency() + amount;
StatsManager.instance.runStats["currency"] = newValue;
PunManager.instance.UpdateStat("runStats", "currency", newValue);
```

### Other runStats keys
| Key | Purpose |
|-----|---------|
| `"currency"` | Current SURPLUS balance |
| `"level"` | Levels completed |
| `"lives"` | Remaining lives |
| `"totalHaul"` | Total SURPLUS ever extracted |
| `"chargingStationCharge"` | Charging station state |

---

## Player Stats — Confirmed Field Names
### Source: Soda_Perks mod (SadTocino) — proven working in multiplayer

These field names are confirmed correct via working mod code. Use reflection to access private fields.

### PlayerController
| Field | Type | Access | Notes |
|-------|------|--------|-------|
| `SprintSpeed` | float | public | Direct access: `pc.SprintSpeed` |
| `EnergySprintDrain` | float | public | Direct access: `pc.EnergySprintDrain` |

```csharp
PlayerController pc = FindLocalPlayerComponent<PlayerController>();
float originalSpeed = pc.SprintSpeed;
pc.SprintSpeed = originalSpeed * 1.5f;       // Speed Cola pattern
pc.EnergySprintDrain = 0f;                    // Infinite stamina pattern
```

### PlayerHealth
| Field | Type | Access | Notes |
|-------|------|--------|-------|
| `maxHealth` | int | private | Use reflection (see below) |
| `health` | int | private | Use reflection (see below) |

### PhysGrabber
| Field | Type | Access | Notes |
|-------|------|--------|-------|
| `grabStrength` | float | private | Use reflection (see below) |
| `grabRange` | float | private | Use reflection (see below) |

### Reflection Pattern (from Soda_Perks — proven working)
```csharp
private void SetField(object target, string fieldName, object value)
{
    FieldInfo field = target.GetType().GetField(fieldName, 
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    field?.SetValue(target, value);
}

private T GetField<T>(object target, string fieldName)
{
    FieldInfo field = target.GetType().GetField(fieldName, 
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    return field != null ? (T)field.GetValue(target) : default(T);
}

// Usage:
int maxHP = GetField<int>(playerHealth, "maxHealth");
SetField(playerHealth, "maxHealth", 500);  // Juggernog pattern
SetField(playerHealth, "health", 500);
```

### Finding Player Components (from Soda_Perks — proven working in multiplayer)
```csharp
// For local player — use PlayerAvatar.instance or Camera.main proximity
// For specific player by Photon actor number:
T[] all = Object.FindObjectsOfType<T>();
foreach (T component in all)
{
    PhotonView pv = component.GetComponent<PhotonView>() 
                 ?? component.GetComponentInParent<PhotonView>();
    if (pv != null && pv.OwnerActorNr == targetActorNumber)
        return component;
}
```

---

## Item System

### Confirmed Resource Paths
| Item | Resource Path | Notes |
|------|--------------|-------|
| Handgun | `Items/Item Gun Handgun` | Confirmed via `item.prefab.ResourcePath` |

### Item ScriptableObject Fields
```csharp
public class Item : ScriptableObject
{
    public string itemName;           // "Gun" for handgun
    public string description;
    public SemiFunc.itemType itemType;
    public PrefabRef prefab;          // ← USE THIS for resource path
    public bool physicalItem;
    // ...
}
```

### Getting Item Resource Path (correct pattern)
```csharp
Item item = StatsManager.instance.itemDictionary["Item Gun Handgun"];
string resourcePath = item.prefab.ResourcePath;  // "Items/Item Gun Handgun"
```

### Spawning Items
```csharp
// Multiplayer:
PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, position, rotation);

// Singleplayer:
GameObject prefab = Resources.Load<GameObject>(item.prefab.ResourcePath);
Object.Instantiate(prefab, position, rotation);
```

### All Gun Item Names (confirmed from itemDictionary at runtime)
- `"Item Gun Handgun"`
- `"Item Gun Laser"`
- `"Item Gun Shockwave"`
- `"Item Gun Shotgun"`
- `"Item Gun Stun"`
- `"Item Gun Tranq"`

---

## Enemy System

### Key Classes
| Class | Role |
|-------|------|
| `EnemySetup` | ScriptableObject — defines enemy type, prefab refs, difficulty, rarity |
| `EnemyParent` | MonoBehaviour — manages spawn/despawn lifecycle |
| `EnemyHealth` | MonoBehaviour — handles damage, death events |
| `EnemyDirector` | Singleton — manages all spawned enemies, difficulty pools |
| `Enemy` | MonoBehaviour — AI state machine |

### Enemy Difficulty Pools
```csharp
EnemyDirector.instance.enemiesDifficulty1  // Slowest/easiest
EnemyDirector.instance.enemiesDifficulty2  // Medium  
EnemyDirector.instance.enemiesDifficulty3  // Fastest/hardest
```

### EnemySetup Prefab Reference
```csharp
string resourcePath = setup.spawnObjects[0].ResourcePath;
// PrefabRef.ResourcePath is the string for PhotonNetwork
// PrefabRef.Prefab loads via Resources.Load internally
```

### Known Enemy Variants
- `Enemy - Duck` — standard, works correctly
- `Enemy - Thin Man` — standard, works correctly (EnemyOnScreen patch required)
- `Enemy - Elsa` — standard, works correctly
- `Enemy - Tricycle` — standard, works correctly
- `Enemy - Slow Mouth` — standard, works correctly
- `Enemy - Birthday Boy` — standard, works correctly
- `Enemy - Tick` — standard, works correctly
- `Enemy - Ceiling Eye` — standard, works correctly
- `Enemy - Gnome Director` — **BROKEN** — EnemyParent not on root, different spawn structure. Filter from wave pool.

### Spawning an Enemy (Multiplayer)
```csharp
GameObject go = PhotonNetwork.InstantiateRoomObject(
    setup.spawnObjects[0].ResourcePath, 
    spawnPosition, 
    Quaternion.identity
);
EnemyParent enemyParent = go.GetComponentInChildren<EnemyParent>(); // Use InChildren!
EnemyDirector.instance.FirstSpawnPointAdd(enemyParent);
enemyParent.DespawnedTimerSet(0f);
```

### Enemy Death Events
```csharp
// Primary — EnemyParent.health is not publicized; use GetComponentInChildren instead:
EnemyHealth? health = enemy.GetComponentInChildren<EnemyHealth>();
if (health != null)
    health.onDeath.AddListener(() => { /* handle death */ });

// EnemyHealth death flow:
// Hurt() → Death() → DeathRPC() → DeathImpulseRPC() ← fires onDeath here
```

### EnemyHealth.Hurt() Signature
```csharp
public void Hurt(int _damage, Vector3 _hurtDirection)
// NO attacker parameter — cannot detect who dealt damage
// NO headshot bool — headshot detection not possible via this path
```

### EnemyOnScreen — KeyNotFoundException Fix
```csharp
// Wave-spawned enemies cause KeyNotFoundException in EnemyOnScreen.GetOnScreen
// because they bypass R.E.P.O.'s normal enemy registration.
// Fix: Harmony Finalizer on GetOnScreen to catch and swallow KeyNotFoundException:
[HarmonyPatch(typeof(EnemyOnScreen), nameof(EnemyOnScreen.GetOnScreen))]
static class EnemyOnScreenPatch
{
    static Exception Finalizer(Exception __exception, ref bool __result)
    {
        if (__exception is KeyNotFoundException)
        {
            __result = false;
            return null; // Swallow — treat enemy as off-screen
        }
        return __exception; // Re-throw everything else
    }
}
```

---

## Level System

### Level Load Hook
```csharp
// Confirmed: RunManager.ChangeLevel drives all transitions
// Hook with: [HarmonyPatch(typeof(RunManager), "ChangeLevel")]

// Check if current level is a gameplay level (not lobby/shop/arena):
bool isGameplay = RunManager.instance.levels.Contains(RunManager.instance.levelCurrent);
// NOTE: levelLobby, levelShop, levelArena are NOT in the levels list
```

### Level Points (Spawn Positions)
```csharp
List<LevelPoint> all = SemiFunc.LevelPointsGetAll()
    .Where(x => !x.Truck).ToList();

LevelPoint furthest = SemiFunc.LevelPointGetFurthestFromPlayer(origin, minDistance);
List<LevelPoint> startRoom = SemiFunc.LevelPointsGetInStartRoom();
List<LevelPoint> playerRooms = SemiFunc.LevelPointsGetInPlayerRooms();
```

### NavMesh Snapping
```csharp
// After selecting a LevelPoint, snap Y to actual ground:
Vector3 spawnPos = levelPoint.transform.position;
if (NavMesh.SamplePosition(spawnPos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
    spawnPos = hit.position;
// else use spawnPos.y directly from LevelPoint
```

### Level Generation
```csharp
LevelGenerator.Instance.Generated   // bool — true when map fully built
LevelGenerator.Instance.EnemyParent // Transform — parent for enemy objects
```

---

## Player System

### Getting Players
```csharp
List<PlayerAvatar> players = SemiFunc.PlayerGetList();
List<PlayerAvatar> players = GameDirector.instance.PlayerList;
```

### Player Identity
```csharp
playerAvatar.steamID      // string key used in StatsManager dictionaries
playerAvatar.playerName   // display name
playerAvatar.isDisabled   // true when dead
```

---

## Shop System

### Removing Weapons from Truck Shop
```csharp
// Harmony patch the shop's item population method
// Filter items by itemType — SemiFunc.itemType enum contains weapon types
// Single patch, only needs to run on host
```

### Item Type Enum (SemiFunc.itemType)
Used to identify and filter item categories. Patch shop population and check this field.

| Value | Meaning | Confirmed |
|-------|---------|-----------|
| `SemiFunc.itemType.gun` | All gun/weapon items | ✅ confirmed at runtime |

```csharp
// Filter itemDictionary to guns only:
foreach (Item item in StatsManager.instance.itemDictionary.Values)
    if (item.itemType == SemiFunc.itemType.gun) pool.Add(item);
```

---

## Useful SemiFunc Helper Methods

```csharp
SemiFunc.IsMasterClientOrSingleplayer()    // host authority check
SemiFunc.IsMultiplayer()                   // multiplayer check
SemiFunc.PlayerGetList()                   // List<PlayerAvatar>
SemiFunc.LevelPointsGetAll()              // all LevelPoints
SemiFunc.LevelPointsGetInStartRoom()      // spawn room points
SemiFunc.LevelPointsGetInPlayerRooms()    // near-player points
SemiFunc.LevelPointGetFurthestFromPlayer(origin, minDist)
SemiFunc.RunIsShop()                      // truck/shop phase
SemiFunc.RunIsLobby()                     // lobby/truck
SemiFunc.RunIsArena()                     // arena (death)
SemiFunc.IsMainMenu()
SemiFunc.StatSyncAll()                    // sync all stats
SemiFunc.StatSetRunLevel(level)
SemiFunc.EnemyInvestigate(pos, radius)
SemiFunc.RunGetDifficultyMultiplier1()
```

---

## REPOLib API Summary

### What REPOLib IS useful for
- Registering **custom** enemies, items, valuables from asset bundles
- Network events between mods (`NetworkedEvent`)
- Audio mixer group fixing for custom sounds
- Debug commands

### What REPOLib is NOT for
- Spawning existing game enemies at runtime (use PhotonNetwork directly)
- Modifying existing item/enemy behavior (use Harmony patches)

### Using REPOLib Network Events (recommended over raw Photon RPCs)
```csharp
// In plugin Awake:
public static NetworkedEvent PowerUpActivated = 
    new NetworkedEvent("SEMIDEAD.PowerUpActivated", OnPowerUpReceived);

// Raise from host to all clients:
PowerUpActivated.RaiseEvent(
    (int)powerUpType,
    NetworkingEvents.RaiseAll, 
    SendOptions.SendReliable
);

// Handler runs on all clients including host:
private static void OnPowerUpReceived(EventData data)
{
    int type = (int)data.CustomData;
    // apply effect locally
}
```

---

## Ecosystem — Compatible Mods

### Soda_Perks (SadTocino)
**Thunderstore:** `SadTocino-Soda_Perks`
**What it does:** CoD Zombies-style perk vending machines (Speed Cola, Juggernog, Double Tap, Quick Revive) and a Mystery Box that dispenses perk sodas.

**Compatibility with SEMIDEAD:**
- Fully compatible — separate systems, no conflicts
- SEMIDEAD's Mystery Box dispenses **weapons**
- Soda_Perks Mystery Box dispenses **perk sodas**
- Players can run both mods simultaneously for the full experience
- List as **recommended companion mod** on SEMIDEAD's Thunderstore page

**Proven field names from source (use in SEMIDEAD power up system):**
```csharp
// PlayerController — direct access
pc.SprintSpeed          // float
pc.EnergySprintDrain    // float

// PlayerHealth — via reflection ("health", "maxHealth")
// PhysGrabber — via reflection ("grabStrength", "grabRange")
```

**Soda_Perks uses reflection pattern for private fields — same pattern SEMIDEAD should use for power ups.**

---

## SEMIDEAD-Specific Notes

### Wave Monster Identification
- `WaveEnemyTag` component added to all wave-spawned enemies
- Distinguishes wave enemies from native R.E.P.O. spawns (chaos mode)
- Added synchronously after `PhotonNetwork.InstantiateRoomObject` — no race condition

### SURPLUS Award Pattern
```csharp
if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
int newValue = StatsManager.instance.GetRunStatCurrency() + amount;
StatsManager.instance.runStats["currency"] = newValue;
PunManager.instance.UpdateStat("runStats", "currency", newValue);
```

### Death Detection Pattern (dual path, idempotent)
```csharp
// Primary — subscribe on spawn (Health field not publicized — use GetComponentInChildren)
EnemyHealth? h = enemy.GetComponentInChildren<EnemyHealth>();
if (h != null) h.onDeath.AddListener(() => WaveSpawner.OnWaveEnemyDied(enemy));

// Fallback — Harmony Finalizer on DeathImpulseRPC
// HashSet.Remove returns false on second call — double-fire safe
```

### Gameplay Level Detection
```csharp
// CORRECT — checks actual gameplay level list
bool isGameplay = RunManager.instance.levels.Contains(
    RunManager.instance.levelCurrent);

// INCORRECT — RunIsLobby/RunIsShop/RunIsArena proved unreliable
// for this purpose
```

### Lazy Singleton Pattern (required for SEMIDEAD components)
```csharp
// DontDestroyOnLoad components can be destroyed on scene transitions.
// Always use lazy-init in patches.
// IMPORTANT: DontDestroyOnLoad must be called from inside Awake(), NOT from the
// patch — calling it before AddComponent() has no effect on the resulting instance.
if (WaveManager.Instance == null)
{
    var go = new GameObject("SEMIDEAD_WaveManager");
    go.AddComponent<WaveManager>(); // Awake() calls DontDestroyOnLoad
}
WaveManager.Instance?.ResetForNewLevel();
```

### Pistol Resource Path
```csharp
// CONFIRMED:
const string PistolResourcePath = "Items/Item Gun Handgun";

// Spawn at player feet:
Item item = StatsManager.instance.itemDictionary["Item Gun Handgun"];
PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, 
    player.transform.position + Vector3.up * 0.5f, Quaternion.identity);
```

---

## Harmony Patch Templates

```csharp
// Standard Postfix:
[HarmonyPatch(typeof(TargetClass), nameof(TargetClass.TargetMethod))]
public class MyPatch
{
    static void Postfix(TargetClass __instance) { }
}

// Finalizer (exception handling):
[HarmonyPatch(typeof(TargetClass), nameof(TargetClass.TargetMethod))]
public class MyPatch
{
    static Exception Finalizer(Exception __exception, ref ReturnType __result)
    {
        if (__exception is SpecificException)
        {
            __result = safeDefault;
            return null; // swallow
        }
        return __exception; // re-throw
    }
}
```

---

## dnSpy Navigation Guide

- **Assembly to open:** `REPO_Data/Managed/Assembly-CSharp.dll`
- **Search:** Edit → Search Assemblies (Ctrl+Shift+K)
- **Type search:** change dropdown to "Type", search class name
- **String search:** change dropdown to "String", search for known string values
- **Most game classes:** in `{no namespace}`
- **Token numbers** (e.g. `// Token: 0x020000B6`): ignore these, they are metadata
- **`internal` fields:** accessible via reflection or publicizer

### Fastest lookups
- To find resource paths: String search for `"Items/"` or `"Enemies/"`
- To find method names: Type search for the class, then read method list
- To find field names: Type search, scroll to bottom of class for field declarations

---

*SEMIDEAD Technical Context v1.1*
*Maintained alongside active development*
