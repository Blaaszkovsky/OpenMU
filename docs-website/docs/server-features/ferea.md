---
title: Ferea on the extended client
sidebar_label: Ferea
description: Ferea hunting grounds and the configurable Lord of Ferea encounter.
---

# Ferea on the extended client

The Season 6 configuration includes Ferea as network map 112 with World113 terrain.
The warp costs 50,000 Zen, requires level 400, and arrives near 236,152.
The isolated Baski prototype (900) remains near 215,165. Ordinary Knights (730),
Archers (731), and Fighters (732) keep their hunting spawns.

## Lord of Ferea encounter

Step into the entrance trigger at 110–114,58–62 (center 112,60) while open.
The server moves you to 46,122 inside the arena (bounds 20–51,107–140).
The arena can also be reached along its eastern corridor.

1. Kill the Knight at 28,120, Archer at 32,120, and Fighter at 30,118.
   The Crystal Orb (735) at 30,120 rejects all damage while any guardian lives.
2. Destroy the Orb. Entry closes and Lord (734) appears at 30,120. Only living
   characters already inside participate. Late arrivals and returning characters
   cannot damage encounter monsters and are moved outside on the next update.
3. At 75%, 50%, and 25% HP the Lord stops taking damage. Large hits cannot skip
   gates; each gate happens once even if the Lord heals above an earlier threshold.
4. Roar drains nearby participants' Mana and AG. General (733) appears at 38,120,
   the Lord returns to his throne, and three reinforcements join. The Lord is
   invulnerable and regenerates until the General dies. Then resume damaging him.
5. Defeat the Lord after the final General. All surviving encounter monsters are
   removed. Victorious participants remain inside the throne room; newcomers
   cannot join during cooldown. By default, one Jewel of Bless, Soul or Chaos is
   randomly dropped at the Lord's position. Only surviving participants may pick up
   this reward, even after the usual ten-second pickup priority expires. Other
   encounter monsters give no items or Zen.

Pick up the victory item before logging out. Ground-drop ownership belongs to the
winning player session; logging back in creates a new session which cannot claim
the exclusive item still on the ground. Items already picked up follow normal
inventory persistence.

The Lord attacks normally, periodically uses an area attack, and places delayed
mines under selected participants. The blue warning gives three seconds to move.
Damage is checked at the original placement point. Existing combat views are used;
custom ground circles and retail skill animations are outside this implementation.

No living participants for 60 continuous seconds resets the battle with fresh
Orb and guardians. A 30-minute limit also resets it. Victory waits a random 12–24
hours. Cooldowns are in memory; server restarts reopen the arena. Death or leaving
revokes participation for that battle, including attempts to return by summon.

## Installation and configuration

Fresh Season 6 databases include the waves and an active **Ferea encounter** plugin.
Existing installations apply **Add Ferea encounter**. Its dependency is **Add Ferea
monsters** version 4, which depends on **Add Ferea map**. The update fills missing
wave spawns, feature configuration and a missing Lord victory jewel table, preserving existing operator settings and
remaining idempotent. Restart the server after installation or configuration edits.
A running encounter retains its original plugin configuration.

Plugin settings control bounds, entry/arrival/ejection points, wave numbers,
HP gates, regeneration, attack ranges, timers and cooldowns. Monster definitions
control ordinary combat stats. Wave spawns control positions, quantities and
optional `MaximumHealthOverride`. Invalid settings, quantities or terrain fail
closed with a server log entry.

`VictoryItemDropCount` controls the number of item rolls (default 1; 0 disables
items). Each roll selects from the Lord monster definition's `DropItemGroups`
using their normal probabilities; edit those groups to choose items or chances.
Global hunting drop tables do not affect victory rewards. Zen reward groups are
not supported. Existing reward tables are preserved when applying update version 2.

Version 3 uses a 300,000 HP Lord, a 100,000 HP General and regeneration of 0.05%
maximum HP per Roar victim per second. The 30-minute battle timer includes all
General phases. These defaults target a solo level-400 character in this Season 6
adaptation; equipment, attack rate, movement and party size still affect completion.
The optional upgrade replaces only the previous exact defaults (1,000,000 Lord HP,
250,000 General HP and 0.25% regeneration). An operator value identical to those old
defaults is indistinguishable and will also change when opting into this upgrade.
Other values, reward settings and spawn health overrides are preserved. Later
reapplications preserve operator edits and repair missing HP attributes.

Optional WCoinC rewards are disabled by default. Set `VictoryWCoinCChance` from
0 to 1 and `VictoryWCoinCMinimum`/`VictoryWCoinCMaximum` to an inclusive amount
range. Each living participant receives an independent roll. Awards are serialized
with the player's saves, immediately saved, and capped at the account balance limit.
Dead, departed and late-arriving characters receive no victory rewards. Failure
or timeout gives no reward. Repeated encounter updates cannot award the same victory
twice. The encounter and its cooldown are in memory: there is no durable reward
queue or replay after a server crash. A restart clears cooldown and permits another
encounter, so keep WCoinC disabled where repeated restarts could be exploited.
Check server logs for failed reward saves, including saves returning false.

| Wave | Purpose | Monsters |
| --- | --- | --- |
| 20 | Crystal Orb | 735 × 1 |
| 21 | Guardians | 730, 731, 732 × 1 |
| 22 | Lord | 734 × 1 |
| 23 | General | 733 × 1 |
| 24 | Reinforcements per phase | 730, 731, 732 × 1 |

Orb, Lord and General waves require exactly one monster each. Event waves use
`OnceAtWaveStart`; the ordinary map initializer must not spawn them. Encounter
spawns use `NullDropGenerator`: maximum item drops zero alone would not prevent Zen.
Only encounter victory explicitly rolls the Lord's reward groups.
Native IDs 730–735 are sent directly to MuMain, without the 700–706 remapping.

## Reference facts and implementation choices

[Webzen's Season 11 announcement](https://muonline.webzen.com/en/events/season11-part1)
confirms Ferea, its Knights and Lord. Its detailed Ferea guide returned 404 when
checked on 21 October 2026; it does not establish phase thresholds or timings.

The public [MU Online Fanz Ferea guide](https://muonlinefanz.com/tools/maps/data/mapdb/Ferea.php)
describes Orb activation, closed entry, abandonment, Roar, General protection and
healing. These are reference descriptions, not source code. Public guides disagree
about coordinates, balance and later-season mechanics.

The 75/50/25% gates, three guardians, reinforcement quantities, arena placement on
shipped walkable terrain, 30-minute limit and regeneration of 0.05% maximum HP per
Roar victim per second (zero if nobody was hit) are editable OpenMU choices,
not verified Webzen formulas. This is a Season 6 balance adaptation without elemental
combat. No decompiled server source was used.

## Runtime acceptance test without persistence changes

Use a disposable server or an attached .NET debugger. Obtain the live encounter via
`gameContext.FeaturePlugIns.GetPlugIn<FereaEncounterPlugIn>().GetEncounter(gameContext)`.
Use `GetNpcsInRange` to find live guardians/Orb and set their instance `Health` to 1.
The Orb must still reject damage until every guardian dies. Kill by attacking;
do not remove or dispose the Orb, because its death activates the encounter.

Once Lord appears, pause execution and apply an instance-only maximum-health element:

```csharp
var lord = encounter.Lord;
var healthElement = new SimpleElement(
    10000 - lord.Attributes[Stats.MaximumHealth], AggregateType.AddRaw);
lord.Attributes.AddElement(healthElement, Stats.MaximumHealth);
lord.Health = 10000;
```

This changes only the live attribute holder, not definitions or spawn areas. Gates
become 7500/5000/2500 HP. Set each newly summoned General's instance `Health` to 1.
Verify Lord immunity (normal, reflected, poison and bleed), regeneration while the
General lives, all three phases, late entry, player death, victory, the configured
item reward and no Zen,
and cleanup of every reinforcement. Restore the live Lord with:

```csharp
lord.Attributes.RemoveElement(healthElement, Stats.MaximumHealth);
lord.Health = (int)lord.Attributes[Stats.MaximumHealth];
```

Resetting the encounter disposes instance overrides. Restarting clears its cooldown
and recreates normal configured monsters. No persistence save is involved. For reset,
leave/disconnect all participants, wait 60 seconds, and reenter for fresh Orb/guards.
For cooldown timing, use the automated controlled-clock tests or a separate disposable
configuration; do not shorten the operator's production cooldown.
