# Session Summary — Update Spec

Implemented. This document records the resolved behaviour; see
`HtcmSessionSummaryService` (aggregation) and `HtcmSessionSummaryNotificationHandler`
(Discord rendering).

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

**Total Damage** (not DPS) — reported as `Average | Top | Max` for each of:

| Metric | Average | Top | Max |
|---|---|---|---|
| Timecaster | Avg of the night | Best of the night | Best ever (all sessions) |
| Giants | Avg of the night | Best of the night | Best ever (all sessions) |
| Saltspray | Avg of the night | Best of the night | Best ever (all sessions) |

- **Average** = average across the night
- **Top** = best single result of the night
- **Max** = best ever, across all sessions

Average and Top are computed per pull, over only the pulls where the player did damage in
that phase group — a pull where they were dead the whole phase is left out rather than
dragging the average to zero. `*` in the rendered table marks a metric where tonight set
the all-time max.

Each burst column also carries its DPS in parentheses — `930k (39k)` — giving a read on
how long that burst window ran. The DPS shown against Top and Max is that specific pull's
DPS, not a separate DPS maximum.

**Combined bosses — Jormag, Kralk, Morde, Zhaitan, Soo**

- Report **Total Damage** *and* **DPS** as `Average | Top | Max`. Damage and DPS are
  independent series here, so "best-ever DPS" is its own figure rather than the DPS of
  whichever pull did the most damage. The rendered table shows `dmg avg | dmg top |
  dps avg | dps max`.
- This is collapsed into **one number** for the combined group — one value for Total Damage and one value for DPS (not per-boss).
- **Primordus is deliberately excluded**: its arena heavily favours 1200-range builds, so
  including it would rank players by class rather than by performance.
- DPS uses a per-player denominator (only the phases that player has rows in), so someone
  who missed pulls isn't diluted by their duration.

**Total Orb Pushes**

- `Cumulative (this session) | Max (best-ever session)`
- Counted from EI's `Orb Push` mechanic. EI emits one event per channel tick (~350ms while
  pushing), so a 1s ICD (`MechanicIcdHelper`) collapses a continuous push into one
  occurrence; without it the metric measures time-on-orb rather than pushes.

**Boon Rips**

- `Average | Top | Max`
- Per pull, from `player_encounters.boon_strips` (full-fight basis). **Max is the best-ever
  single pull**, the same basis as Average and Top.

---

### Individual Awards

**MVDPS** — awarded for the best metric across the categories above.

Weighted in priority order, summing to 100:

1. Burst (Total Damage) — weight 37.5
2. DPS — weight 37.5
3. Orb pushes — weight 12.5
4. Boon rips — weight 12.5

The weights are fixed by three constraints — burst == dps, orbs == rips, and
burst == 3 × orbs — which with a total of 100 give `2b + 2o = 100`, `b = 3o`, so
`o = 12.5` and `b = 37.5`.

Scoring is a **weighted sum of each player's share of the session leader** in each
category: the category leader earns the full weight, a player at half the leader's value
earns half. This self-calibrates across comps instead of relying on fixed
damage-per-rip conversion constants. A category with no data contributes nothing.

---

### Shame Awards

- **Debilitated** — pass/fail per pull: the count of pulls where the player carried
  Debilitated into the Giants window at all, regardless of uptime or stack count. Uses the
  same phase set as the prog page's Phase Insights column (Giants main phases plus their
  breakbars, where EI records the buff separately).
- **Chomped** — if any. Counted from EI's `Jaws.H` (Primordus Jaws) mechanic.

Both are gated on the guild's Wall of Shame toggle.

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
