# SEMIDEAD

**Zombies wave survival mode for R.E.P.O.**

> Host installs the mod. Clients install nothing.

---

## What Is SEMIDEAD?

SEMIDEAD transforms every R.E.P.O. level into a wave survival map. Enemies spawn from the edges of the level while you still have to run the heist. Survive the waves, meet the quota, extract.

---

## Features

### Wave System
- Waves of R.E.P.O. enemies spawn from level edges on a timer
- 15-second grace period at level start to scout and prepare
- 30-second intermission between waves
- Escalating enemy counts — player-scaled at wave 6+
- Special **Elsa rounds** on wave 3, 8, 13... ("FETCH ME THEIR SOULS!")
- Wave timeout — enemies rush players if a wave drags on too long
- Max Ammo power-up awarded to all players at the end of every wave

### Starting Loadouts
Each level begins with a round-specific weapon set:

| Round | Weapons |
|---|---|
| 1 | RAY GUN + MAG 60 + Sword |
| 2 | RAY GUN + MAG 60 + Shotgun + Sword |
| 3 | Shotgun + MAG 60 + RAY GUN |
| 4 | 2× RAY GUN per player |
| 5 | 3× Shotguns per player |
| 6+ | RAY GUN + MAG 60 + Shotgun |

- **MAG 60** — zero recoil, 30-round mag
- **RAY GUN** — 150 damage per shot
- **Shotgun** — explosive rounds

### Economy
- Start each level with **$500 SURPLUS**
- Earn SURPLUS from kills:
  - $50 standard kill
  - $100 fast variant kill
  - +$150 last-kill-of-wave bonus
- Double Points power-up doubles all earnings for 30 seconds

### Wall Buy Stations
Three weapon purchase stations placed randomly each level:

| Weapon | Price |
|---|---|
| Shotgun | $500 |
| RAY GUN | $1000 |
| Photon Blaster | $1000 |

Stand near a station for 5 seconds to purchase. Insufficient funds trigger a character voice line.

### Mystery Box
- $500 per roll — random weapon from R.E.P.O.'s full weapon pool
- ~20% Teddy Bear chance — box teleports to a new location on the map

### Power Ups
Dropped by wave enemies on death (10% chance). Walk over to activate — effect applies to the entire team:

| Power Up | Effect |
|---|---|
| MAX AMMO | Full ammo restored for all players instantly |
| DOUBLE POINTS | 2× SURPLUS earnings for 30 seconds |
| INSTA KILL | One-hit kills for 30 seconds |
| NUKE | Instantly kills all remaining wave enemies |

### Monkey Bomb
- Attracts nearby enemies then explodes with AoE damage
- One spawns per player per level

### Revive System
- Hold near a downed teammate for 5 seconds to revive them
- All downed players are automatically revived at wave start

### Character System
Players are assigned one of four characters (Tank, Nikolai, Warrior, Doctor) and deliver kill quotes, wave-start speeches, and wall-buy reactions through R.E.P.O.'s TTS system.

### Announcer
- Multi-kill, killing spree, betrayal, last stand, and game over callouts
- Wave MVP announced at the end of each round

---

## Installation

**Host only — clients do not need to install this mod.**

1. Install [BepInEx](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/) if you haven't already
2. Install SEMIDEAD through your mod manager (r2modman / Gale)
3. Launch R.E.P.O. as host — SEMIDEAD activates automatically on gameplay levels

---

## Known Issues

- MonkeyBomb cannot be armed or placed in inventory by client players (host only)
- Mystery Box uses a placeholder visual (animal crate model)

---

## Links

- [GitHub](https://github.com/nemonicicon/SEMIDEAD)

---

*......SAM!.....*
