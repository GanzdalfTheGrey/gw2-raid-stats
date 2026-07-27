# Session Summary — Update Spec

Implemented. This document records the resolved behaviour; see
`HtcmSessionSummaryService` (aggregation) and `HtcmSessionSummaryNotificationHandler`
(Discord rendering).

## Collapsed / Expanded (HTCM only)

The HTCM summary posts **collapsed**: a single header embed — pulls / best phase / best HP,
squad DPS for all four groups (Timecaster, Giants, Saltspray, Dragons), the ✨ Highlights
board, and the 💀 Wall of Shame (single worst per category) — with a **`📊 Full breakdown`**
button. There is no MVP podium.

Clicking the button replies **ephemerally to the clicker** with the per-player detail
tables (burst, dragons, orbs & rips) and a full Wall of Shame breakdown (every category's
ranking, not just the worst). It's stateless: the button id carries the session date
(`htcm:expand:{yyyy-MM-dd}`), so `HtcmSummaryInteractionHandler` rebuilds the summary from
scratch — the button keeps working across bot restarts. The regular (non-HTCM) session
summary is unaffected.

## Posting Behavior

1. **Trigger:** Summaries are still posted from the same button as before
   (`POST /api/admin/discord/post-session-summary`).
2. **HTCM progress summary:** If there are HTCM logs *and no HTCM clear (kill)*, post an **HTCM progress summary**.
3. **Regular session summary:** If there are logs from other/normal bosses, post a **regular session summary**.
4. **Both can post:** If both conditions are met, post **two summaries** (one HTCM progress summary + one regular session summary).
5. **No double-counting:** when the HTCM progress summary posts, those pulls are excluded
   from the regular summary's encounter list, MVPs and Wall of Shame. If HTCM *was* killed,
   it stays in the regular summary like any other boss and no HTCM summary posts.

Only guild members (included players) appear in the HTCM summary's tables, matching
leaderboard behaviour.

---

## HTCM Summary

### Per Player

**Total Damage** with DPS in parentheses — reported as `avg (dps)` per player for
Timecaster, Giants and Saltspray. The average is per pull, over only the pulls where the
player did damage in that group (a pull spent dead is left out rather than dragging the
average down). Each group's table ends with a **squad total vs its target** (✅/❌),
repeating the header line so it can be checked without scrolling.

**Squad DPS targets** (shown in the header next to tonight's figure):

| Group | Target |
|---|---|
| Timecaster | 175k |
| Giants | 310k |
| Saltspray | 170k |
| Dragons | *(no target — shows all-time average instead)* |

**Giants per-player targets and cookies/shames.** Only Giants carries a per-player DPS
target, by profession then role:

| Build | Target |
|---|---|
| Virtuoso | 35k |
| Vindicator | 45k |
| Boon DPS (`dps_quick`/`dps_alac`) | 30k |
| Pure DPS | 55k |
| Healer (`heal_quick`/`heal_alac`) | *(exempt — no target, not shamed for low burst)* |

Profession (elite spec) wins over role, so a portal-running Virtuoso is judged on 35k, not
the pure-DPS 55k. The Giants table shows `avg (dps) | target`. A player whose Giants
**session average** clears target + 10k is a **cookie**; target − 10k or worse is a
**shame**. The header names the single biggest cookie (→ Doing It Right) and biggest miss
(→ Wall of Shame) with their average and margin, and the expanded **Cookies & Shames**
section lists them all (see below).

### Cookies & Shames

A dedicated embed in the expanded (ephemeral) view, one **fixed-width table per burst
group** (aligned like the burst/dragon tables, so it reads at a glance), one row per
targeted player: session-avg DPS, its `status` vs target (cookie ≥10k over / spec within
10k / shame ≥10k under), then how many of tonight's pulls landed in each bucket
(`ck`/`sp`/`sh` = cookie/spec/shame). Rendered in a code block, so no emoji (they break
monospace alignment). It is **group-agnostic**: any burst group with target rows renders, so
once Timecaster / Saltspray gain per-player targets they appear here with no further change.
Currently only Giants has per-player targets.

**Combined bosses — Jormag, Kralk, Morde, Zhaitan, Soo**

- Report **Total Damage** *and* **DPS**. The rendered table shows
  `dmg avg | dmg top | dps avg`; best-ever figures are computed but not displayed.
- This is collapsed into **one number** for the combined group — one value for Total Damage and one value for DPS (not per-boss).
- **Primordus is deliberately excluded**: its arena heavily favours 1200-range builds, so
  including it would rank players by class rather than by performance.
- DPS uses a per-player denominator (only the phases that player has rows in), so someone
  who missed pulls isn't diluted by their duration.

**Total Orb Pushes**

- Cumulative for the session. The best-ever session total is computed but not displayed.
- Counted from EI's `Orb Push` mechanic. EI emits one event per channel tick (~350ms while
  pushing), so a 1s ICD (`MechanicIcdHelper`) collapses a continuous push into one
  occurrence; without it the metric measures time-on-orb rather than pushes.

**Boon Rips**

- `Average | Top`, per pull, from `player_encounters.boon_strips` (full-fight basis). The
  best-ever single pull is computed but not displayed.

---

### Individual Awards

The **Most Valuable Proggers** scored podium has been removed — the glance layer is now the
Highlights board plus the Wall of Shame.

The **boon-uptime score** survives because it ranks the 🎵 **Boons** highlight (the giver
with the best night). It applies only to players whose `PlayerEncounter.Role` marks them a
quickness or alacrity giver (`dps_quick`/`heal_quick` → Quickness, `dps_alac`/`heal_alac` →
Alacrity; role read per encounter so a mid-session build swap scores on what was played).

It is scored on the uptime the giver's **subgroup received** — not the giver's own — so a
scrapper self-quickening while their group sits at 40% earns nothing. Allocation is 5 points
each for Timecaster / Giants / Saltspray and 15 for the combined dragons (30 total). Uptime
at or above **95%** earns the full allocation; below it it ramps linearly. Each group is
averaged across the pulls the giver was in before the four are summed, so more pulls can't
inflate the total.

Boon uptime requires `player_encounter_phase_stats.quickness_uptime_pct` /
`alacrity_uptime_pct` (migration 034). Sessions imported before that migration score 0 in
the category until **Admin → Manage Logs → Rescan** backfills them.

---

### Highlights Board

A scannable good-play / bad-play board in the header embed — the glance layer, so the
little things people do right and wrong stand out without opening the app. Each callout
names the single leader in its category; a category with no data is dropped.

**✨ Doing It Right** (always shown)

| Callout | Source |
|---|---|
| 🔥 Burst | highest combined session-avg burst damage |
| 🐉 Dragon DPS | highest combined-dragon session-avg DPS |
| 🎵 Boons | best boon giver by uptime points (subgroup-received) |
| 🔵 Orbs | most orb pushes |
| 🚑 Medic | most resurrects (`PlayerEncounter.Resurrects`) |
| 🌀 Rips | highest session-avg boon strips |
| 💥 CC | most total breakbar damage (`PlayerEncounter.BreakbarDamage`) |
| 🍪 Giants | biggest Giants over-target performer (see Giants targets above) |

### Shame Awards

- **First Death** — the count of pulls where the player was the first to die. Taken from
  the same per-pull first-death the prog page shows.
- **Debilitated** — pass/fail per pull: the count of pulls where the player carried
  Debilitated into the Giants window at all (it can only be carried in from a Mordremoth
  shockwave; nothing applies it during Giants). Counted as the distinct pulls that show a
  Debilitated readout on the prog page's phase breakdown — any Giants-window phase (main OR
  breakbar) with non-zero phase-relative uptime — so the bot count matches the page exactly.
  (Previously it was derived from the average-uptime pull set, which required a main-phase
  row and undercounted pulls whose debil landed only on a breakbar.)
- **Chomped** — if any. Counted from EI's `Jaws.H` (Primordus Jaws) mechanic.
- **Shockwaved** — most `ShckWv.H` (Mordremoth Shockwave) hits.
- **Bad Reds** — most `Red.B` (Red Bait) targets caught by a **non-healer**. Reds are meant
  for healers to take, so this is role-gated: healer = `PlayerEncounter.Role` of `heal_quick`
  or `heal_alac`, read per encounter so a build swap is respected. A healer catching reds is
  not shamed.
- **Giants Miss** — biggest Giants under-target performer (see Giants targets above): the
  player whose session-average Giants DPS fell furthest below their target (by at least 10k).

Each header line names the single worst in its category, or **"Multiple (N)"** when several
players tie for the top count (rather than picking one arbitrarily). The expanded view lists
the full per-category ranking.

All are gated on the guild's Wall of Shame toggle.

---

## Discord Limits

Discord allows 6000 characters summed across all embeds in one message, 4096 per
description, 1024 per field value, and 25 fields per embed. Ten players across six metrics
would exceed the field cap, so player tables are rendered as fixed-width code blocks inside
embed descriptions. A full ten-player roster comes to roughly 3,600 characters across the
four embeds; oversized rosters split across a second message, and per-table row caps
report what was dropped rather than truncating silently.

The summary is split across four embeds — header, burst, dragons, orbs & rips — because
stacking differently-shaped code blocks in one description reads as mush; an embed
boundary gives each table a visual break. Account-name discriminators (`.1234`) are
stripped in the tables: they consumed column width and forced real names into mid-word
truncation.

---

## Data Dependencies

- `player_encounter_phase_stats` (migrations 032 + 033) backs every damage/DPS and
  Debilitated figure. Sessions imported before 032 have no rows, so their burst and dragon
  sections render empty and "best-ever" values are understated until
  **Admin → Manage Logs → Rescan** backfills them (`RescanService` skips encounters that
  already have rows, so a rescan on fresh imports is a cheap no-op).
- Mechanic events (`Orb Push`, `Jaws.H`) are imported unfiltered by `LogImportService`, so
  historical logs already carry them — no rescan needed for those two.
