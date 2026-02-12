# GW2 Raid Stats - Achievement System

This document outlines the proposed achievement system for GW2 Raid Stats. Achievements are designed to be lighthearted, fun, and encourage players to try new things.

## Overview

- **Personal Achievements**: Tied to individual players, displayed on player profiles
- **Guild Achievements**: Collective accomplishments for the entire guild
- **Progress Tracking**: Shows progress toward incomplete achievements (e.g., "50/100 kills")
- **Discord Notifications**: Announces when players/guild unlock achievements
- **Retroactive Awards**: Existing data will be scanned to award achievements (without Discord spam)

---

## Personal Achievements (30 total)

### Wing Master (8 achievements)
Complete each boss in a wing on all 5 roles: Heal Alac, Heal Quick, DPS Alac, DPS Quick, and Pure DPS.

| Achievement | Description |
|-------------|-------------|
| **Wing 1 Master** | All roles on Vale Guardian, Gorseval, Sabetha |
| **Wing 2 Master** | All roles on Slothasor, Matthias |
| **Wing 3 Master** | All roles on Keep Construct, Xera |
| **Wing 4 Master** | All roles on Cairn, Mursaat Overseer, Samarog, Deimos |
| **Wing 5 Master** | All roles on Soulless Horror, Dhuum |
| **Wing 6 Master** | All roles on Conjured Amalgamate, Twin Largos, Qadim |
| **Wing 7 Master** | All roles on Cardinal Adina, Cardinal Sabir, Qadim the Peerless |
| **Wing 8 Master** | All roles on all Wing 8 bosses |

*Role Detection: Healer = HealingPowerStat >= 1 AND (QuickGen >= 10% OR AlacGen >= 10%)*

---

### Completion (4 achievements)
Progress through raid content.

| Achievement | Description |
|-------------|-------------|
| **Completion** | Kill every boss in Wings 1-7 |
| **Legendary Raider** | Kill every CM boss in Wings 1-7 |
| **Wing 8 Clear** | Complete all Wing 8 bosses |
| **Wing 8 CM Clear** | Complete all Wing 8 CMs |

---

### Performance (4 achievements)
Exceptional individual performance in encounters.

| Achievement | Description |
|-------------|-------------|
| **The Carry** | Deal 25%+ of your squad's total DPS in a successful kill |
| **Immortal** | Complete 10 consecutive kills without dying |
| **Clutch Player** | Survive a successful kill where 5+ squadmates died |
| **Speed Demon** | Participate in a guild record kill time |

---

### Records (1 achievement)
Recognition for setting records.

| Achievement | Description |
|-------------|-------------|
| **Former Champion** | Held a guild DPS record at any point in time |

---

### Spec Diversity (4 achievements)
Encouraging players to try different builds and classes.

| Achievement | Description |
|-------------|-------------|
| **Versatile** | Complete a boss on 10 different elite specs |
| **Jack of All Trades** | Complete a boss on 20 different elite specs |
| **Class Completionist** | Complete a boss on every elite spec for a single profession (excluding core) |
| **Master of One** | Complete 100 kills on the same elite spec |

*Note: Core specs are excluded from Class Completionist*

---

### Support Recognition (3 achievements)
Celebrating the unsung heroes.

| Achievement | Description |
|-------------|-------------|
| **Guardian Angel** | Have the most resurrects in a successful kill (5+ times) |
| **CC Champion** | Deal the most breakbar damage in a successful kill (10+ times) |
| **The Enabler** | Have the highest boon DPS in a successful kill (25+ times) |

---

### Dedication (2 achievements)
Showing up consistently.

| Achievement | Description |
|-------------|-------------|
| **The Regular** | Participate in 25 raid sessions |
| **Dedicated** | Participate in 50 raid sessions |

---

### Personal Growth (1 achievement)
Improving over time.

| Achievement | Description |
|-------------|-------------|
| **Keeping Up** | Beat your personal DPS best on a single boss 5+ times |

---

### Social (3 achievements)
Playing with friends.

| Achievement | Description |
|-------------|-------------|
| **Dynamic Duo** | Complete 50 bosses with the same squadmate |
| **Trio** | Complete 25 bosses with the same two squadmates |
| **Guild Pride** | Be part of a successful kill with only guild members (no pugs) |

---

## Guild Achievements (14 total)

### Class/Composition Challenges (7 achievements)
Creative squad compositions.

| Achievement | Description |
|-------------|-------------|
| **One Trick Guild** | Complete a boss with all 10 players on the same profession |
| **Class Wing Clear** | Complete an entire wing with all players on the same profession |
| **Heavy Metal** | Complete a boss with only heavy armor classes (Warrior, Guardian, Revenant) |
| **Cloth Squad** | Complete a boss with only light armor classes (Elementalist, Necromancer, Mesmer) |
| **Leather Lovers** | Complete a boss with only medium armor classes (Thief, Ranger, Engineer) |
| **No Duplicates** | Complete a boss with 10 different elite specs (no repeats) |
| **Rainbow Squad** | Complete a boss with at least one of each profession (9 classes represented) |

---

### Performance Challenges (3 achievements)
Squad-wide excellence.

| Achievement | Description |
|-------------|-------------|
| **Flawless Wing** | Complete a full wing with 0 squad deaths (one achievement per wing) |
| **Untouchable** | Complete a boss with 0 downs across the entire squad |
| **Photo Finish** | Kill a boss in the final 10 seconds before enrage |

*Note: Flawless Wing requires all bosses in a wing to be cleared in a single session with zero total deaths*

---

### Fun/Rare Moments (4 achievements)
Memorable guild moments.

| Achievement | Description |
|-------------|-------------|
| **Bench Warmers** | Complete a boss with 7 or fewer players |
| **Synchronized** | 3+ players set a personal best DPS in the same encounter |
| **Record Breakers** | 2+ players break guild DPS records in the same encounter |
| **The Comeback** | Kill a boss after wiping 5+ times on it in the same session |

---

## Implementation Notes

### Display
- **Player Profile**: Shows top 5 rarest achievements earned
- **Achievements Page**: Full list of all achievements with progress tracking
- **Progress Format**: "50/100 kills" style for incomplete achievements

### Discord Integration
- Achievements announced in Discord when unlocked
- Retroactive awards from existing data will NOT trigger notifications

### Role Classification (for Wing Master)
Players are classified into roles based on:
```
Healer: HealingPowerStat >= 1 AND (QuicknessGen >= 10% OR AlacracityGen >= 10%)

Heal Alac:  Healer + AlacracityGen >= 10%
Heal Quick: Healer + QuicknessGen >= 10%
DPS Alac:   !Healer + AlacracityGen >= 10%
DPS Quick:  !Healer + QuicknessGen >= 10%
Pure DPS:   !Healer + AlacracityGen < 10% + QuicknessGen < 10%
```

### Retroactive Processing
- Will utilize existing "Rescan JSON" functionality to backfill role data
- One-time scan of all historical data to award achievements
- Future imports will check achievements incrementally

---

## Summary

| Category | Count |
|----------|-------|
| Personal - Wing Master | 8 |
| Personal - Completion | 4 |
| Personal - Performance | 4 |
| Personal - Records | 1 |
| Personal - Spec Diversity | 4 |
| Personal - Support | 3 |
| Personal - Dedication | 2 |
| Personal - Growth | 1 |
| Personal - Social | 3 |
| **Personal Total** | **30** |
| Guild - Composition | 7 |
| Guild - Performance | 3 |
| Guild - Fun/Rare | 4 |
| **Guild Total** | **14** |
| **Grand Total** | **44** |
