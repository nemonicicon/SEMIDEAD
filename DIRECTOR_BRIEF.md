# SEMIDEAD — Director Brief
*Paste this at the start of every new directing session. Maintained by Claude Code.*
*Last updated: 2026-04-17 — git/GitHub set up; spawn overhaul; orb visual fix; hold revive; staggered wave-1 speeches; player name announcements; betrayal fix; extraction TTS*

---

## What This Is

**SEMIDEAD** is a BepInEx 5 host-only mod for **R.E.P.O.** (Unity, Photon PUN multiplayer).
It layers CoD Zombies-style wave mechanics on top of R.E.P.O.'s existing gameplay.
- You (Jacob) direct the work via Claude.ai. Claude Code does the implementation in `C:\Users\jacob\SEMIDEAD\`.
- **Host-only**: all game logic runs on the master client. Photon RPCs sync state to other players where needed.
- No REPOLib — raw Photon PUN is used throughout.
- Build: `dotnet build` in the project root. Target: `netstandard2.1`.
- **Multiplayer confirmed working**. Jacob plays this with friends regularly online.

---

## Project Structure

```
SEMIDEAD/
├── SEMIDEAD.cs               — BepInEx plugin entry point. Initializes logger, runs Harmony patches
├── WaveManager.cs            — Wave state machine: GracePeriod (15s) → WaveActive (150s timeout) → Intermission (15s)
│                               Announces waves via ambience RPC. Fires MaxAmmo on wave clear.
├── WaveSpawner.cs            — Spawns enemies each wave. Sets spawnValuable=false to suppress native orb drops.
│                               SP: Resources.Load+Instantiate; MP: PhotonNetwork.InstantiateRoomObject
│                               Calls PowerUpManager.TryDropOrb() on each wave enemy kill.
├── WaveEnemyTag.cs           — Empty MonoBehaviour marker on all wave-spawned enemies
├── WaveHUD.cs                — TextMeshPro HUD overlay (host-only). Wave number, kill counter, buy prompts,
│                               power-up activation/expiry banners
├── WaveKillReward.cs         — SURPLUS per kill. Doubles when DoublePointsActive. (WaveKillTracker component)
├── WallBuy.cs                — 3 weapon zones/level at random NavMesh LevelPoints. 2s dwell, $500, random gun.
│                               Host-authoritative proximity polling. Room-prop sync for client HUD position.
├── MysteryBox.cs             — One box/level at random non-truck LevelPoint. Proximity → $500 roll:
│                               80% random gun, 20% Teddy Bear (teleport). Animal crate visual.
├── MonkeyBomb.cs             — ToyMonkeyTrap-based monkey bomb. MonkeyBombActivator arms on pickup.
│                               ToyMonkeyTrapSetupPatch adds activator to all clients via InstantiationData.
├── PowerUpManager.cs         — Power-up state. TryDropOrb() 10% drop using AssetManager.enemyValuableSmall/
│                               Medium/Big. InstaKill/Nuke/MaxAmmo/DoublePoints effects.
│                               SpeakAllPlayers announces power-up name to all players on pickup.
├── PowerUpOrb.cs             — Host-side proximity pickup (2.5f, 30s lifetime). Calls ActivatePowerUp() on contact.
├── StartingPistol.cs         — Round-based loadout injected into truck item volumes (see table below).
│                               Respawn patch re-grants handgun mid-level.
├── CharacterSystem.cs        — Per-player CoD Zombies character (Richtofen/Nikolai/Dempsey/Takeo).
│                               TTS speech triggers: kills, power-ups, wave 1 start, revive, monkey bomb.
│                               API: TriggerSpeech(player) / TriggerSpeechNearestTo(pos) / TriggerSpeechForOneRandom
├── AnnouncerSystem.cs        — World-level TTS via SpeakAllPlayers (ChatMessageSendRPC MP / TTSSpeakNow SP).
│                               Wave start, First Blood, multi-kill chain, spree milestones (match-wide),
│                               Betrayal/Team Kill/Traitor (confirmed kill only), Suicide, Last Standing, Game Over.
│                               Three Harmony patches: TeamKillDetection, PlayerHealthHurt, PlayerDeathAnnouncer.
├── ReviveSystem.cs           — Pickup revive (grab death head ≤30s) + wave-start revive for all dead players.
│                               CharacterSystem speech trigger on revive.
├── ShopFilter.cs             — DISABLED. [HarmonyPatch] commented out. Would remove guns from truck shop.
├── ExamplePlayerControllerPatch.cs — Unused BepInEx template. Does nothing.
├── Patches/
│   ├── RunManagerPatch.cs    — ChangeLevel postfix: recreates all singletons if null, calls OnLevelSetup() on each.
│   │                           Master-client guard: wave logic only runs on host.
│   ├── EnemyHealthPatch.cs   — InstaKill_Prefix (force 9999 dmg) + DeathImpulseRPC_Postfix (fallback death hook).
│   │                           Also fires CharacterSystem.OnEnemyKilled + AnnouncerSystem.OnEnemyKilled.
│   └── EnemyOnScreenPatch.cs — Finalizer swallows KeyNotFoundException for wave enemies not in director dictionary.
└── RepoDecompiled/           — R.E.P.O. decompiled source, reference only (excluded from build)
```

---

## System Status

| System | Status | Notes |
|---|---|---|
| Wave cycle | ✅ Confirmed | GracePeriod→WaveActive→Intermission |
| Enemy spawning | ✅ Confirmed | Player-scaled count, per-player 15u distance |
| Post-spawn investigate | ⚠️ Unconfirmed | Coroutine → enemy.Spawned → EnemyDirector.SetInvestigate |
| Elsa waves | ⚠️ Unconfirmed | Every 5th wave; "FETCH ME THEIR SOULS!" TTS 2s before spawn |
| WaveKillReward | ✅ Confirmed | SURPLUS per kill |
| StartingPistol | ✅ Confirmed | Round-based weapon injection working |
| WallBuy | ✅ Confirmed | Disabled intentionally (guns in shop is fine) |
| MysteryBox | ✅ Confirmed | Working in MP for host and clients |
| EnemyOnScreen fix | ✅ Confirmed | No more KeyNotFoundException |
| Soul orb suppression | ✅ Confirmed | spawnValuable=false |
| CharacterSystem | ✅ Confirmed | Wave-1 speeches staggered 10/18/26/32s from wave start |
| AnnouncerSystem | ⚠️ Unconfirmed | Player names (first blood, down, MVP) + betrayal fix unplaytested |
| ReviveSystem | ✅ Built | 3-second hold revive + wave-start revive — needs playtest |
| PowerUpOrb visual | ⚠️ Unconfirmed | New sphere+light approach (events 44/45) — needs playtest |
| PowerUpOrb pickup | ✅ Confirmed | Proximity working |
| MonkeyBomb client | ⚠️ Unconfirmed | ToyMonkeyTrapSetupPatch — not playtested |
| MysteryBox visual | ⚠️ Unconfirmed | Animal crate path may be wrong |
| Betrayal detection | ⚠️ Unconfirmed | New HurtCollider.PlayerHurt Prefix — needs playtest |
| Extraction TTS | ⚠️ Unconfirmed | "TEAM DARKSTAR HAS DONE IT AGAIN!" on gameplay→shop — needs playtest |
| WallBuy HUD (clients) | ⚠️ Known gap | Buy prompt host-only; needs client mod to fix |
| MysteryBox E-key | ⚠️ Known gap | Proximity-only; no keypress |
| DoublePoints orb | ⚠️ Visual gap | Same sphere color system as other orbs — distinct colors now |

---

## Round-Specific Loadouts (StartingPistol)

| Round | Items given |
|---|---|
| 1 | Handgun + Grenade + Sword |
| 2 | Handgun + Shotgun + Grenade + Sword |
| 3 | Laser + Laser Shockwave + Grenade + Sword |
| 4 | Tranq + Frying Pan + Grenade + Sword |
| 5+ | Handgun + Shotgun + Grenade + Sword |

Ray Gun (Shockwave) given every round. RayGunDamagePatch sets it to 200 damage.

---

## Announcer System

**SpeakAllPlayers(text)**: all players say `text` simultaneously.
- MP: `pv.RPC("ChatMessageSendRPC", RpcTarget.All, text, false)` per player
- SP: `player.voiceChat?.ttsVoice?.TTSSpeakNow(text, false)`

**Kill attribution**: `_recentPlayerHits` (HurtCollider.onImpactPlayer) + `_recentEnemyHits` (PlayerHealth.Hurt enemyIndex) resolved on PlayerDeathHead.Trigger within 5s.

**Multi-kill** (resets per wave): 2=DOUBLE KILL, 3=TRIPLE KILL, 4=MULTI KILL, 5=MEGA KILL, 6=ULTRA KILL, 7=MONSTER KILL, 8+=LUDICROUS KILL

**Spree** (match-wide): 5=KILLING SPREE, 10=RAMPAGE, 15=DOMINATING, 20=GODLIKE, 50=HOLY SHIT!, 60=UNSTOPPABLE!, 70=EXTRACT ALREADY DAMMIT

---

## Confirmed R.E.P.O. APIs

### Currency / SURPLUS
```csharp
int bal = StatsManager.instance.GetRunStatCurrency();
StatsManager.instance.runStats["currency"] = newVal;
PunManager.instance.UpdateStat("runStats", "currency", newVal);
```

### Enemy spawning flow
```csharp
// MP:
GameObject go = PhotonNetwork.InstantiateRoomObject(resourcePath, pos, Quaternion.identity);
// SP:
var prefab = Resources.Load<GameObject>(resourcePath);
GameObject go = Object.Instantiate(prefab, pos, Quaternion.identity);

EnemyParent enemy = go.GetComponent<EnemyParent>();
enemy.SetupDone = true;                           // CRITICAL — unlocks Setup() coroutine
enemy.Enemy.EnemyTeleported(pos);                 // warps NavMesh agent, syncs clients
EnemyDirector.instance.FirstSpawnPointAdd(enemy); // registers with spawn point system
```
- Do NOT call `enemy.Spawn()` manually — Logic() handles it after 2-5s timer.
- Do NOT add to `enemiesSpawned` — Setup() auto-adds. On death: `enemiesSpawned.Remove(enemy)`.
- Filter out: `ResourcePath.Contains("Director")` and `ResourcePath.Contains("Ceiling Eye")`

### Level readiness
```csharp
SemiFunc.LevelPointsGetAll()
SemiFunc.IsMasterClientOrSingleplayer()   // true if master OR not connected (singleplayer)
SemiFunc.IsMultiplayer()
SemiFunc.PlayerGetList()                  // List<PlayerAvatar>
SemiFunc.itemType.gun
RunManager.instance.levels.Contains(RunManager.instance.levelCurrent)  // is gameplay level
```

### Items / batteries
```csharp
StatsManager.instance.itemDictionary      // Dictionary<string, Item>
item.prefab.ResourcePath                  // Photon resource path
ItemBattery.SetBatteryLife(int)           // prime with battery.batteryLife = 1f if dead
```

### Spawning items in the truck
```csharp
// Inject into purchasedItems BEFORE TruckPopulateItemVolumes runs.
ItemManager.instance.purchasedItems.Add(dict["Item Gun Handgun"]);
```

### Valuables
```csharp
PhotonNetwork.InstantiateRoomObject("Valuables/[size]/[name]", pos, Quaternion.identity);
// Sizes: "01 Tiny" "02 Small" "03 Medium" "04 Big" "05 Wide" "06 Tall" "07 Very Tall"
```

### Shop
```csharp
// Postfix on ShopManager.ShopInitialize:
shop.potentialItems.RemoveAll(item => item.itemType == SemiFunc.itemType.gun);
```

### Announcer / UI
```csharp
Announcer.SendBigMessage(header, subtext, duration, headerColor, subtextColor);
Announcer.SendFocusText(text, bgColor, textColor, duration);
WaveHUD.ShowWaveStart(waveNumber, monsterCount);
WaveHUD.ShowWaveCleared(waveNumber, intermissionSecs);
WaveHUD.ShowPowerUpActivated(name, color, duration);  // 0f = until next power-up
```

---

## Wave Design

| Wave | Count | Enemy mix |
|---|---|---|
| 1 | 6 | 100% difficulty1 |
| 2 | 10 | 70% difficulty1, 30% difficulty2 |
| 3+ | 14+(n-4)×2 | 30% diff1, 40% diff2, 30% diff3 |

---

## Open Bugs / TODOs

- **PowerUpOrb pickup unverified** — check for `[PowerUpOrb]` log lines next session
- **WallBuy clients see no buy prompt** — WaveHUD is host-only; low priority UX gap
- **MysteryBox visual path unverified** — "Valuables/05 Wide/Valuable Manor Animal Crate" may be wrong
- **MonkeyBomb client activation unverified** — need client log showing components added
- **MonkeyBomb ItemToggle.onToggle init** — ToyMonkeyTrapSetupPatch creates new UnityEvent; may conflict with game's normal setup
- **No extraction blocking** — players can extract during an active wave
- **ShopFilter disabled** — guns appear in truck shop alongside wave system

---

## Phase Roadmap

- **Phase 1** ✅ — Wave spawning, kill rewards, death tracking
- **Phase 2** ✅ — StartingPistol, PowerUps, MysteryBox, MonkeyBomb, WallBuy, WaveHUD, CharacterSystem, AnnouncerSystem, ReviveSystem
- **Phase 3** 🔄 — Spawn overhaul ✅, orb visual ✅, hold revive ✅, player name announcements ✅, betrayal fix ✅, extraction TTS ✅. Remaining: playtest all new systems, verify Elsa wave, verify post-spawn investigate, verify betrayal detection.

---

## How to Direct Claude Code

Claude Code already has memory of this project. You don't need to paste code — just describe what to change and it will read the relevant files first. The decompiled R.E.P.O. source is at `RepoDecompiled/Assembly-CSharp/` if Claude Code needs to verify an API.

Common commands:
- "Rebuild" — Claude Code runs `dotnet build`
- "Read [FileName.cs]" — Claude Code reads and confirms current state
- "Fix [described bug]" — Claude Code reads affected files, applies fix, rebuilds
