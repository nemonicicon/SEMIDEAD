# SEMIDEAD Changelog

## [Unreleased]

---

## [0.3.0] - 2026-04-02
### Fixed
- **Photon connectivity bug** — `ShotgunExplosionPatch.RegisterListener()` was calling `PhotonNetwork.AddCallbackTarget()` in `SEMIDEAD.Awake()` before Photon initialized, breaking region/lobby loading every session. Moved registration to `RunManagerPatch.ChangeLevel_Postfix()` with a `PhotonNetwork.IsConnected` guard. Confirmed working across multiple full sessions.

### Added
- **AnnouncerSystem SUICIDE misattribution fix** — Added `PlayerTumbleOverrideEnemyHurtPatch`: when enemies (Oogly, Spinny, etc.) grab+throw a player, `OverrideEnemyHurt` fires and now calls `RecordEnemyHit(avatar)` so tumble deaths are attributed to enemies instead of SUICIDE. Built; pending full playtest verification.

---

## [0.2.0] - 2026-04-01
### Fixed
- **CharacterSystem ViewID=0 bug** — `_assignments`, `_oneShotUsed`, and `_cooldownUntil` were keyed by `PhotonView.ViewID` (int), which reads as 0 at level-load time when `AssignCharacters` runs. Re-keyed all three dictionaries by `player.playerName` (string). TTS speeches confirmed working in multiplayer playtests.
- **WaveManager Update() guard** — Changed `if (_waveNumber == 0 && !IsGameplayLevel()) return;` to `if (!IsGameplayLevel()) return;`. The `_waveNumber == 0` condition caused the wave timer to keep ticking in shop/lobby after any wave had started.

### Added
- **MysteryBox** — Confirmed working in multiplayer for host and clients. Visual, E-key interaction (host) / 1.5s dwell (clients), weapon spawn, and teddy bear teleport all functional.
- **WallBuy** — Confirmed working for host and clients.
- **MAG 60 burst fire** — 3-round burst at 0.08s interval; 30-round magazine via `ItemBattery.Start` postfix.
- **Shotgun explosion** — Triggers on bullet hit (size 0.4f, 30 player dmg, 80 enemy dmg). Host spawns with damage; clients receive visual via Photon RaiseEvent (code 43) + `IOnEventCallback`.
- **Ray Gun damage** — `ShootBulletRPC` postfix sets `enemyDamage` to 200.
- **Weapon renames** — Handgun renamed to "MAG 60", Shockwave renamed to "RAY GUN" at level load.
- **Grenade removal** — Grenades removed from all round loadouts.

### Loadouts (as of this version)
- Round 1: Ray Gun + MAG 60 + Sword
- Round 2: Ray Gun + MAG 60 + Shotgun + Sword
- Round 3: Ray Gun + Photon Blaster + Sword
- Round 4: Ray Gun + Tranq + Frying Pan + Sword
- Round 5+: Ray Gun + MAG 60 + Shotgun + Sword

---

## [0.1.0] - Initial
### Added
- WaveManager state machine (GracePeriod → WaveActive → Intermission)
- WaveSpawner with Photon MP and SP instantiation
- WaveHUD on-screen display
- WaveEnemyTag marker component
- Kill tracking and SURPLUS rewards ($50 slow / $100 fast)
- PowerUpManager + PowerUpOrb (MaxAmmo, DoubleTap, InstaKill, etc.)
- MonkeyBomb (attractor + explosion)
- StartingPistol (pistol at level start)
- ReviveSystem (downed player revival)
- CharacterSystem (per-character TTS kill quotes, wave-start lines)
- AnnouncerSystem (world TTS: wave start, first blood, multi-kill, spree, betrayal, suicide, game over)
- EnemyHealthPatch (InstaKill prefix + DeathImpulseRPC fallback)
- RunManagerPatch (ChangeLevel hook, lazy-init singletons)
- ShopFilter
