# SEMIDEAD
### R.E.P.O. Mod — Design Document v1.0
### Status: Phase 1 LIVE — Phase 2 in planning

---

## What Is SEMIDEAD

A host-only BepInEx mod for R.E.P.O. that layers CoD Zombies-style wave mechanics on top of the existing game using R.E.P.O.'s own monster roster. No custom characters. No custom AI. Existing monsters spawn in escalating waves on a timer, alongside the game's normal monster behavior. Players must meet the quota while surviving wave pressure.

The mod is designed to shine at **20+ player lobbies** where the fixed wave sizes give the group a numerical advantage — the challenge comes from coordination and protecting fragile items, not being outnumbered.

**Host installs the mod. Clients install nothing.**

---

## Companion Mod — Soda_Perks

SEMIDEAD is designed to be **compatible with and complementary to** [Soda_Perks by SadTocino](https://thunderstore.io/c/repo/p/SadTocino/Soda_Perks/).

Soda_Perks adds CoD Zombies-style perk vending machines (Speed Cola, Juggernog, Double Tap, Quick Revive) and its own Mystery Box for perks.

| Feature | SEMIDEAD | Soda_Perks |
|---------|----------|------------|
| Wave system | ✅ | ❌ |
| Kill rewards | ✅ | ❌ |
| Mystery Box (weapons) | ✅ | ❌ |
| Mystery Box (perks) | ❌ | ✅ |
| Perk vending machines | ❌ | ✅ |
| Starting pistol | ✅ | ❌ |
| Power ups | ✅ (Phase 2) | ❌ |
| Monkey Bomb | ✅ (Phase 2) | ❌ |

**Recommended setup:** Install both mods for the full CoD Zombies experience. They do not conflict.

---

## Core Design Philosophy

> "Each level is a CoD Zombies map with a heist objective on top."

Not a CoD Zombies port — a mod that takes the **feeling** of CoD Zombies and grafts it onto R.E.P.O.'s existing chaos. The heist objective and extraction mechanics are what make SEMIDEAD unique.

---

## Session Loop

```
LEVEL LOADS
  → Pistol spawns at feet of each player (first level only + on respawn)
  → Mystery Box placed at random floor location
  → Wave counter resets to Wave 1
    ↓
15 second grace period (scout, haul items, locate Mystery Box)
    ↓
[Between-round jingle plays]
    ↓
WAVE 1 BEGINS
  → Monsters spawn from level edges in wave counts
  → Random monsters drop power up orbs on death (Phase 2)
    ↓
Last monster of wave dies → [Jingle plays]
    ↓
15 second intermission (haul items, use Mystery Box, regroup)
    ↓
WAVE 2 — escalating counts
    ↓
[Repeat]
    ↓
QUOTA MET → Extract to truck
    ↓
TRUCK PHASE
  → Stat upgrades only (speed, strength, stamina, health)
  → NO weapons sold here
    ↓
NEXT LEVEL — everything resets
```

---

## Failure States

### Total Party Wipe
All players die with no revivable heads remaining. Standard R.E.P.O. game over.

### Soft Failure (Organic)
No explicit "you lose" for items. If enough items are destroyed and quota becomes unreachable, waves keep escalating regardless. The team eventually gets overwhelmed. This mirrors classic CoD Zombies — you don't lose by running out of items, you lose by eventually being overwhelmed.

---

## Death & Respawn

### On Death
- Standard R.E.P.O. Semibot death — robot explodes, head drops, all items drop
- No mod intervention needed

### Respawn
- Head extracted at extraction point → player respawns
- On respawn: pistol spawns on ground at player's feet
- Player picks it up manually — one second interaction

### Starting Pistol Rules
- First level only: pistol spawns at feet on level load
- Subsequent levels: no pistol on level load (carry what you have)
- On respawn from head extraction: pistol spawns at feet on any level
- Accumulated pistols from dead players persist as floor items — expected behavior, not a problem

---

## Level Transition Rules

### What SEMIDEAD Resets
| Element | Behavior |
|---------|----------|
| Wave counter | Resets to Wave 1 |
| Mystery Box location | New random position on new map |
| Power up orbs (Phase 2) | Despawn with level |

### What R.E.P.O. Handles Normally
| Element | Behavior |
|---------|----------|
| Player SURPLUS | Carries forward |
| Player weapons | Carry forward — drop on death as normal |
| Player stat upgrades | Permanent |
| Player health | Standard R.E.P.O. behavior |
| Quota progression | Higher each level |
| Monkey Bombs in inventory | Carry forward as normal items |

---

## Economy

### Two-Tier System
```
SURPLUS earned from:
  → Monster kills (in-round)
  → Item extractions (in-round)

SURPLUS spent on:
  IN-ROUND (under pressure):
    → Mystery Box ($500 per roll)
    → [Phase 2] Monkey Bomb (rare level spawn — free)
  
  TRUCK PHASE (safe):
    → Stat upgrades only
    → NO weapons ever sold here
```

### Kill Rewards (configurable)
| Action | SURPLUS |
|--------|---------|
| Monster hit (non-kill) | $10 |
| Monster kill (standard) | $50 |
| Monster kill (fast variant) | $100 |
| Last monster of wave bonus | +$150 |

---

## Wave System

### Wave Composition (Fixed Size)
| Wave | Monster Count | Notes |
|------|--------------|-------|
| 1 | 6 | Slow/basic monsters weighted |
| 2 | 10 | Mixed types |
| 3 | 14 | Mixed + fast variants |
| 4+ | 14 + 2 per wave | Cap by playtesting |

### Known Working Enemy Types
Duck, Thin Man, Elsa, Tricycle, Slow Mouth, Birthday Boy, Tick, Ceiling Eye

### Filtered Enemy Types
- **Gnome Director** — removed from spawn pool (different structure, EnemyParent not on root)

### Timing
| Phase | Duration |
|-------|----------|
| Grace period | 15 seconds (configurable) |
| Intermission | 15 seconds |
| Wave active | Until all wave monsters dead |

### Spawn Behavior
- All LevelPoints ≥15 units (sqrMagnitude) from player centroid collected as edge candidates
- One candidate picked at random per enemy — each gets a unique position
- NavMesh.SamplePosition snaps Y to ground; falls back to raw LevelPoint Y on failure
- Run alongside existing R.E.P.O. monsters (chaos mode)
- Wave counter resets each new level

---

## Mystery Box

### The Sole Weapon Source
No wall-buys. No shop weapons. Mystery Box is the only way to get weapons beyond the starting pistol and teammate drops.

| Detail | Value |
|--------|-------|
| Spawn | One per level, random floor location |
| Excluded zones | Spawn room, extraction room |
| Cost per use | $500 SURPLUS |
| Reward | Random weapon from R.E.P.O.'s weapon pool |
| Teddy Bear chance | ~20% per use |
| On Teddy Bear | Box teleports to new random location |
| Level end | Despawns, resets next level |

### Weapon Scarcity Design Note
With Mystery Box as the only weapon source and a 20% failure rate, weapons are genuinely scarce. Retrieving a fallen teammate's dropped weapon is worth risking your life for. This is intentional.

---

## Phase 2 Systems

### Power Ups
Random orbs dropped by killed wave monsters (~10% chance). Picked up by any player, effect applies to **entire team**.

| Power Up | Color | Effect | Duration |
|----------|-------|--------|----------|
| Insta-Kill | Red | All kills one-hit | 30 sec |
| Nuke | Green | All wave monsters instantly killed, SURPLUS split | Instant |
| Max Ammo | Yellow | Full ammo restore for all players | Instant |
| Double Points | Blue | All SURPLUS earnings doubled | 30 sec |

**Implementation note:** Use REPOLib `NetworkedEvent` to sync power up activation to all clients. This avoids raw Photon RPC complexity.

**Player stat modification pattern** (from Soda_Perks, confirmed working):
```csharp
// Speed boost — direct field access:
pc.SprintSpeed = originalSpeed * multiplier;
pc.EnergySprintDrain = 0f;

// Health modification — reflection:
SetField(playerHealth, "maxHealth", newValue);
SetField(playerHealth, "health", newValue);
```

### Monkey Bomb
R.E.P.O. already contains a **cymbal monkey with drum asset**. No custom model needed. Cymbal audio serves as natural attraction sound.

| Detail | Value |
|--------|-------|
| Model | R.E.P.O.'s existing cymbal monkey asset |
| Acquisition | One spawns per player each level — placed in truck ItemVolume slots |
| Throw | Standard R.E.P.O. throw mechanic |
| Effect | Attracts all nearby entities toward it |
| Fuse | 5 seconds |
| Detonation | AoE damage via R.E.P.O. explosion system |
| Inventory | Carries between levels as normal item |

---

## Phase 3 Systems (Polish & Release)

### Wave HUD
- Wave number display
- Intermission banner ("INTERMISSION — 15s")
- Active power up icon + countdown
- Optional kill counter

### Between-Round Jingle
- 2D audio, heard by all players equally
- Original composition or royalty-free — cannot use actual CoD audio
- Triggers 15 second intermission

### Audio Assets Needed
- `between_round_jingle.wav`
- `mysterybox_reward.wav`
- `mysterybox_teddybear.wav`
- `mysterybox_teleport.wav`
- `powerup_drop.wav`
- `powerup_pickup.wav`

### Host Config File
All tunable values exposed for host to adjust:
- Grace period duration
- Wave counts per wave
- Kill reward amounts
- Mystery Box cost and Teddy Bear rate
- Power up drop chance
- Power up durations

---

## Architecture

```
SEMIDEAD
├── WaveManager.cs            ← Timer, wave defs, intermission, jingle trigger
├── WaveSpawner.cs            ← Spawns R.E.P.O. monsters from level edges
├── WaveKillReward.cs         ← SURPLUS on wave monster kill
├── WaveEnemyTag.cs           ← Marker component on wave enemies
├── StartingPistol.cs         ← Pistol at feet on first level + respawn
├── MysteryBox.cs             ← Placement, reward, Teddy Bear, teleport
├── PowerUpManager.cs         ← Drop chance, orb spawn, effects [Phase 2]
├── PowerUpOrb.cs             ← Trigger, pickup, team broadcast [Phase 2]
├── MonkeyBomb.cs             ← Cymbal monkey, NavMesh override [Phase 2]
├── ShopFilter.cs             ← Removes weapons from truck shop
├── WaveHUD.cs                ← All HUD elements [Phase 3]
├── Patches/
│   ├── RunManagerPatch.cs    ← Hooks ChangeLevel, lazy-init singletons
│   └── EnemyOnScreenPatch.cs ← Swallows KeyNotFoundException
└── Assets/Audio/             ← Sound files [Phase 3]
```

---

## Development Roadmap

### Phase 1 — Core Loop ✅ COMPLETE
- [x] Wave timer, grace period, intermission
- [x] Monster spawning from level edges (random edge point selection)
- [x] SURPLUS kill rewards
- [x] Starting pistol
- [x] Shop filter (weapons removed from truck)
- [x] Lobby menu scene filter (grace period reset on non-gameplay level load)
- [x] EnemyOnScreen crash fix
- [x] Gnome Director filtered from spawn pool

### Phase 1 — Polish Remaining
- [ ] Enemy Y position — some still spawning at Y=0 on certain levels
- [ ] Wave 2+ testing
- [ ] Confirm `PlayerAvatar` respawn method name (`ReviveRPC` is unconfirmed — verify in dnSpy)

### Phase 2 — Items & Economy
- [ ] `MysteryBox.cs` — placement, reward, Teddy Bear, teleport
- [ ] `PowerUpManager.cs` + `PowerUpOrb.cs`
- [ ] `MonkeyBomb.cs`

### Phase 3 — Polish & Release
- [ ] `WaveHUD.cs`
- [ ] Between-round jingle + all audio
- [ ] Host config file
- [ ] Balance testing with 20+ player lobby
- [ ] Thunderstore release as **SEMIDEAD**
- [ ] List Soda_Perks as recommended companion mod

---

## Resolved Design Decisions

| Decision | Answer |
|----------|--------|
| Mod name | SEMIDEAD |
| Host-only | Yes |
| Monster type | R.E.P.O.'s existing monsters |
| Custom models | None |
| Wall-buy stations | Cut — Mystery Box only |
| Perk machines | Not built — use Soda_Perks companion mod |
| Mystery Box | Weapons only — Teddy Bear teleport included |
| Monkey Bomb model | R.E.P.O.'s existing cymbal monkey |
| Monkey Bomb acquisition | One per player per level, spawned via truck ItemVolume slots |
| Starting pistol | Spawns at feet — first level + on respawn |
| Weapons in truck shop | Removed |
| Stat upgrades in truck | Yes — unchanged |
| Wave scaling | Fixed wave sizes |
| Player death | Standard R.E.P.O. Semibot death |
| Monster coexistence | Chaos mode |
| Breather rounds | No — 15s intermission only |
| Between-wave pause | 15 seconds with jingle |
| Power ups | Insta-Kill, Nuke, Max Ammo, Double Points [Phase 2] |
| Lobby/shop/arena filtering | Uses `RunManager.instance.levels.Contains()` |
| Soda_Perks relationship | Separate but compatible — recommended companion |

---

*SEMIDEAD Design Document v1.0*
*Authored with Claude — active development*
