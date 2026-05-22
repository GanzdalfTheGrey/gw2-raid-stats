# Changelog

All notable changes to GW2 Raid Stats will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Stack distance tracking** — each player's average distance to the squad's centroid (from EI's `statsAll.stackDist`; lower = tighter stacking), captured into a new `player_encounters.stack_distance` column (migration 025). Surfaced in three places:
  - **Boon Uptime panel** — a "Dist" column per sub / per player, coloured against the encounter's squad-average distance (green at or below, amber within +10%, red beyond).
  - **Boss detail** — a squad-average Distance chip on the Squad Boon Uptime card.
  - **Player Profile** — an "Avg stack distance" line in the Boon Coverage card with a guild-average comparison.
  - **Boss quirk notes**: Deimos, Qadim, and Qadim the Peerless show a note that a kite/pylon role is intentionally off the stack, so a high distance / low uptime there isn't misread.
  - Backfill historical encounters via **Admin → Manage Logs → Rescan**.

## [1.15.2] - 2026-05-13

### Added
- **Tier-2 boon tracking** (Might, Fury, Regeneration, Protection, Swiftness) — alongside Quickness and Alacrity:
  - Captured per-player from EI's `buffUptimesActive` into five new `player_encounters` columns (migration 024). Might is average stacks (0-25); the rest are percentage uptime. Backfill historical encounters via **Admin → Manage Logs → Rescan**.
  - The session **"Session Stats" tab is renamed "Boon Uptime"** and rebuilt as per-encounter **heatmap cards**: each encounter card has its own column labels (no sticky-header needed), with color-tinted cells for all 7 boons per sub. A per-card **Subs / Players** toggle switches that encounter between sub-average rows and full per-player rows (grouped under their sub). A red-outlined cell marks the "one bad apple" case — sub average is fine but exactly one member is in the red. Each sub's booner line names the Quickness and Alacrity providers, their elite spec, and tags which one was the healer. The tab finishes with a whole-session **Session average**.
  - **Boss detail** gets a **Squad Boon Uptime** card — whole-squad average of all 7 boons, following the same All Time / This Patch range as the Top DPS toggle.
  - Color thresholds: % boons green > 90 / yellow > 80 / red ≤ 80; Might green ≥ 20 / yellow ≥ 15 / red below.
- **Player Availability page** (Admin → Raid Planning → Availability): admin-locked grid of active members with Monday / Tuesday raid-night availability — green (available), yellow (maybe — one day a week, either), red (not available) — plus a free-text note per player. Changes auto-save. Migration 023.
- **Boss detail page** Top DPS list now has an **All Time / This Patch** toggle, so you can see the current-patch top 5 separately from the all-time top 5. Defaults to This Patch. Overall boss stats and recent encounters stay all-time.
- **Leaderboards** boss names are now links — clicking one opens that boss's detail page.

## [1.15.1] - 2026-05-13

### Changed
- **Upload Logs**: the "Legacy Import (Pre-parsed JSON)" section is hidden — raw `.zevtc` upload is the standard path now. The code is retained behind a flag (`_showLegacyImport`) if it's ever needed again.
- **Nav menu reorganized**: the catch-all "Admin" group is split into three purpose-built groups under a non-interactive **Admin** section header — **Raid Planning** (Squad Builder, Mechanic Roles), **Log Management** (Upload Logs, Manage Logs), and **Configuration** (Manage Data, Manage Achievements, Recap Fun Stats). Top-level "Logs" renamed to **Log Search** to disambiguate it from Log Management.
- **Admin → Manage Logs → Rescan** now shows a real progress bar (X / Y encounters, percent, running updated/unchanged/error counts) instead of just an indeterminate spinner. The status endpoint reports per-encounter progress as the rescan runs.

### Fixed
- **Kodan Brothers** and **Old Lion's Court** now record combined DPS across all bosses. Both were only counting the first target — one Kodan brother, and just the red Prototype at OLC — so DPS read low. They're now flagged as multi-target encounters (like Twin Largos). The Rescan button recomputes DPS for multi-target encounters, so historical logs get corrected without re-import (run **Admin → Manage Logs → Rescan**).

## [1.15.0] - 2026-05-13

### Added
- **Past Sessions browser** (`/sessions`): new top-level page lists the last 30 raid sessions in a left-pane picker; selecting one shows that session's Logs + Session Stats tabs (same content as the Home page's Previous Session panel, just any session). Linked from the main nav as "Past Sessions".
- **Boon Coverage report**:
  - **Home → Previous Session** now has two tabs: **Logs** (existing encounter table) and **Session Stats** (new). Session Stats shows per-encounter Quickness and Alacrity uptime broken out by sub-group, plus the names of the boon generators. Cells are colored green > 90%, yellow > 80%, red ≤ 80%. A bold **Session avg** footer row at the bottom of the table summarises each sub × boon column across the whole session.
  - **Player Profile** boon coverage now excludes encounters under 30 seconds (usually aborted pulls or res-back attempts that skew averages).
  - **Player Profile** boon coverage headline now shows **guild-average comparison** next to each player number (Q-Gen, Q-Self, A-Gen, A-Self). A green ▲ / red ▼ / gray ↔ indicator + delta tells you at a glance whether you're above or below the guild's mean. Guild average uses included members only (no pugs). **Per-boss table now shows the same per-boss guild average + delta in each cell** — surfaces "I'm fine overall but specifically bad on Deimos" patterns.
  - **Player Profile** boon coverage now has a **30d / 90d / All time** range toggle (defaults to 90d). The whole card — headlines, breakdowns, guild comparison, and trend charts — respects the same range so nothing's inconsistent across sections.
  - **Player Profile** per-boss table now groups by canonical boss identity (trigger ID) so EI's occasional split-log artifacts like "Cardinal Adina 1" merge with "Cardinal Adina" instead of showing as separate rows. Session view encounter labels use the canonical name too.
  - **Player Profile** trend chart fixed: previously plotted both Quickness and Alacrity on one chart and forced null buckets to 0, producing a noisy graph with phantom drops. Now renders two separate charts (one per boon) side-by-side, each only including weeks where that boon's slice has actual data. X-axis labels thinned to ~6 visible. Each chart gets a header with avg + first-to-last trend arrow.
  - **Player Profile** gets a new **Boon Coverage** card that splits each player's history two ways: **Generation** (the sub's avg uptime on encounters where they were tagged as the booner — "how well did your booning hold up?") and **Self** (their own uptime on encounters where someone else booned — "did you stay in range?"). The breakdowns live in **As Generator** / **On Myself** sub-tabs so each side gets full width; per-boss and per-profession × role tables now show the slice-specific encounter count next to each cell (so you can tell 78% from 4 fights apart from 78% from 40), and rows with zero encounters in the active slice are hidden. Each tab includes a weekly trend chart at the top — two lines (Quickness / Alacrity) showing how that slice's uptime has moved over time, so a recent dip jumps out.
- **Boon self-uptime capture** (Quickness + Alacrity): the importer now extracts each player's "Phase active duration" uptime from EI's `buffUptimesActive` and stores it in two new columns on `player_encounters` (`quickness_self_uptime`, `alacrity_self_uptime`). Migration 022. Distinct from boon *generation* (what you gave the squad) — this is what you had on yourself. Used by the upcoming Boon Coverage report to (a) compute sub-group average uptime as the Generation metric for booners, and (b) flag positioning issues via the Self metric for non-booners. Historical encounters backfill via **Admin → Manage Logs → Rescan**.

### Changed
- **Squad Builder walkthrough**: page now surfaces a step-by-step hint banner (1: pick players, 2: pick bosses, 3: randomize) and pulses the relevant input on each step. The Random Bosses menu activator is a labeled button (not just a dice icon), and the Players / Pug DPS fields are aligned on the same row.
- **Squad Builder randomizer**: now picks a random attempt among all those tied for the best score instead of keeping the first encountered. Removes a bias where flexible players (Can across many roles) were getting locked into the same slot every build because attempts 1-19 contributed nothing once attempt 0 hit optimal.
- **Squad Builder heal flavors**: each sub's heal (Alac vs Quick) is now randomized per attempt instead of hardcoded to AlacHeal-on-Sub1 + QuickHeal-on-Sub2. The squad can now come back as 2× AlacHeal + 2× Quick boon-DPS, 2× QuickHeal + 2× Alac boon-DPS, or mixed — whichever scores best. Lock-aware: an AlacHeal/Quick-boon-DPS lock pins that sub to Alac-heal flavor; QuickHeal/Alac-boon-DPS pins to Quick-heal.
- **Squad Builder random bosses**: roll now interleaves wings instead of sorting by wing order (largest-bucket-first scheduling, random tie-break) so adjacent picks aren't the same wing when possible. The Count field's +/- arrows no longer close the popup.

## [1.14.0] - 2026-05-05

### Added
- **Trends dashboard** (Player Profile → Trends): per-player progression chart for any boss in any role (DPS / Boon DPS). Plots your kills over time with the **guild median** and **guild record** overlaid, plus your personal best, a "last 5 vs prior 5" trend arrow, and a "X to beat (N%)" note showing how close you are to the record. Boss picker is a type-search autocomplete; kill counts on each option reflect the active range. Range toggle: **This Patch** (default) / **Last 90d** / **All Time**.
- **Squad Builder random boss roll**: 🎲 button next to the boss picker rolls N random bosses with a **multi-select wing filter** (pick any combination, empty = all wings) and replace-vs-append.

### Changed
- **GW2 Elite Insights parser** bumped from v3.18.1.0 to v3.22.0.0. Notable: Mesmer/Mirage shatter clone double-counting fixed (was inflating their DPS), Gorseval subphases restored, Greer phase detection fixed on long splits, multi-target detection fixed on Kaineng Overlook + Arkk. Applies to new imports.

## [1.13.1] - 2026-05-05

### Added
- **Patch DPS records**: bot now toots when someone breaks the current patch's DPS or Boon DPS record on a boss (separate from all-time records). Teal "📯 *TOOT* New Patch Record!" embed.

### Changed
- **Session summary** now lists **every record broken** in the session — kill time, DPS, and Boon DPS — grouped into their own sections: ⏱️ Kill Time Records, 📯 TOOT 📯 DPS Records, and 🛡️ Boon DPS Records. Patch records show alongside all-time records with a *(patch)* tag. Was previously capped at 3 mixed together.

## [1.13.0] - 2026-04-30

### Added
- **Player Roles tracking** — mark which roles you can / can't / maybe / want to learn. Open your **Player Profile → Roles** to set your status across the 8 generic roles (Alac/Quick Heal, Alac/Quick DPS Power & Condi, pure DPS Power & Condi) plus boss-specific mechanic roles (pylon kite, hand kiter, tank slots, cannons, etc.).
- **Roles Matrix** (Stats → Roles Matrix) — heatmap view of who-can-do-what across the whole guild. Colored dots per cell (green Can, orange Maybe, red Can't, blue Want to Learn); click any cell to update. Open to all members so anyone can keep their own row current.
  - **Filter to role** dropdown collapses the matrix to a single column sorted by capability with a summary like "5 Can · 2 Maybe · 1 Learn · 0 Can't · 3 Unset" — answers "who can heal alac tonight?" in a glance.
- **Squad Builder** (Admin → Squad Builder) — pick your players, the night's bosses, and pug count, then randomize a 10-person comp.
  - Honors capabilities (uses Can players first, falls back to Maybe; ignores Can't and Want-to-Learn).
  - Respects per-mechanic constraints (Sabetha cannons require DPS, Deimos hand kiter is pure DPS, Adina tank is healer, etc.).
  - **Manual edit** any slot — swap a player, mark a DPS as PUG, or clear. **Lock** slots before clicking **Re-randomize unlocked** to keep the picks you like.
  - **Conflict reset** — if a mechanic can't be filled because the only capable players are stuck on incompatible base roles, click *Reset from <boss>* to re-solve from that boss onward. The earlier bosses keep their assignment; you'll see a "Roles change at <boss>" diff banner.
  - **Post to Discord** — sends the final composition as an embed to your notification channel with the squad, per-boss mechanics, mid-set swaps, and @-mentions for `/link`'d members.
- **Mechanic Role Catalog** (Admin → Mechanic Roles) — admin-editable list of boss-specific mechanic roles with slot constraints (Any / Prefer Healer / Requires DPS / Requires Pure DPS / etc.) and per-mechanic min/max counts (e.g., Qadim Lamp 2-3). Seeded with Wings 1-7; Wing 8 ready for you to populate.
- **Manual exclusion override** (Admin → Manage Data) — if a player passes the auto-include threshold but hasn't played in a year, click **Exclude** on their row. They drop out of leaderboards, the Roles Matrix, and the Squad Builder until you remove the exclusion.

### Changed
- Roles Matrix uses case-insensitive sorting for player names, and the filter bar wraps gracefully on narrower screens.

### Fixed
- Mechanic Role Catalog group headers now show the actual boss name instead of the literal word "Boss".
- Roles Matrix sticky player column no longer shows dots bleeding through during horizontal scroll on a hovered row.
- Squad Builder boss picker now shows all bosses (Cairn, Samarog, Sabir, Wing 8) even when they have no mechanic roles defined.

## [1.12.0] - 2026-04-13

### Added
- Leaderboard patch reset system: admin can add balance patch dates to reset the leaderboard
- Patch selector on leaderboards page (defaults to most recent patch, with "All Time" option)
- Admin Manage Data page section for adding/removing leaderboard patches

### Changed
- Wall of Shame: replaced "Most Deaths" with "Most First Deaths" (counts how many times a player was the first to die in an encounter)

## [1.11.3]  - 2026-03-10

### Added
- Live checking for "Thorn in My Side" and "Ring of Fire" achievements (now awarded immediately when criteria are met, not just during backfill)
- Manual guild achievement awarding on Admin > Manage Achievements page with custom date support
- Separate Discord notification section for announcing already-earned guild achievements

### Fixed
- Fixed "Thorn in My Side" and "Ring of Fire" achievements not detecting Matthias kills when using alternate trigger ID (16115)
- Fixed trigger ID normalization in expansion-themed achievement checking

## [1.11.2] - 2026-03-03

### Added
- Logs page now has Raids/Strikes toggle to filter by content type
- Strike boss list for filtering strike encounters
- 4 new guild achievements:
  - **Metal Bikini Brigade**: Complete Heavy Metal on all 8 wings
  - **Glass Cannons United**: Complete Cloth Squad on all 8 wings
  - **Cowhide Crusaders**: Complete Leather Lovers on all 8 wings
  - **Triple Threat**: Complete Heavy Metal, Cloth Squad, and Leather Lovers on the same wing

### Fixed
- First kill DPS record now only awards to top DPS player (not everyone in the squad)
- Achievement Discord notifications now display the achievement name correctly

## [1.11.1] - 2026-03-02

### Changed
- Heavy Metal, Cloth Squad, and Leather Lovers achievements are now wing-based instead of single-boss:
  - Must complete an entire wing with only that armor class
  - Must include at least one of each base profession (e.g., Heavy Metal requires at least one Guardian, Warrior, and Revenant)

### Fixed
- Top 5 DPS Discord notifications now show correct rankings when multiple players from the same encounter enter the leaderboard

## [1.11.0] - 2026-02-18

### Added
- 5 new personal achievements:
  - **The Crabs and The Bees**: Complete Guardian's Glade strike mission
  - **Cajun Seafood Boil**: Complete Guardian's Glade without being hit by Scalding Wave
  - **Witness Me**: Be the only one alive when the boss dies
  - **Ambulance**: Resurrect 5+ teammates in a single encounter
  - **The Sacrifice**: Die during Matthias sacrifice mechanic
- 9 new guild achievements:
  - **Full Clear**: Clear all 8 wings in a single session
  - **Musical Chairs achievements** (8): Complete a wing with different boon providers on each boss - no player can repeat the same boon role (heal alac, heal quick, dps alac, dps quick) across different bosses:
    - Wing 1: "Spirit Shuffle"
    - Wing 2: "Salvation Rotation"
    - Wing 3: "Stronghold Swap"
    - Wing 4: "Musical Chairs"
    - Wing 5: "Death's Dance"
    - Wing 6: "Mythwright Medley"
    - Wing 7: "Key Change"
    - Wing 8: "Balrior Ballet"
- **Spec History page** (`/players/{name}/specs`) - New dedicated page showing complete spec and role history:
  - All bosses with completed specs and roles (Heal Alac, DPS Quick, etc.)
  - Kill counts per spec/role combination
  - Accessible from Player Profile and achievement "View All Specs" links

### Changed
- "Most Rubs" MVP stat now shows total time spent resurrecting instead of count (e.g., "1m 23s" instead of "5")

### Fixed
- Search/Clear buttons on Mechanics page now wrap to new line below 1280px to prevent overflow
- HTCM HP% calculation now correctly shows progress relative to all 6 dragons (fixes incorrect values with GW2EI v3.18.1.0)
- Death counting in MVP/Shame stats now handles instant-kill mechanics correctly (first death always counts, deaths 5+ seconds before fight end count)

## [1.10.5] - 2026-02-17

### Changed
- Updated GW2 Elite Insights parser to v3.18.1.0
  - "Kela" renamed to "Guardian's Glade"
  - Additional mechanics now tracked in logs

## [1.10.4] - 2026-02-16

### Added
- 6 new guild achievements for creative squad compositions:
  - **Core Memory**: Complete an encounter with everyone on core classes (no elite specs)
  - **Core 2 Duo**: Complete an entire wing with everyone on core classes only
  - **Chaos Strat**: Complete a raid encounter (Wings 1-8) with 7+ players all in the same subgroup
  - **Chaos Dunk**: Complete an entire raid wing with 7+ players all in the same subgroup
  - **Thorn in My Side**: Complete Wings 1-4 on only Heart of Thorns specializations
  - **Ring of Fire**: Complete Wings 5-7 on only Path of Fire specializations
- Added expansion-based elite spec categorization (HoT, PoF, EoD, JW) for future achievement tracking
- 8 new personal "Shame" achievements for those memorable moments:
  - **Serial Downer**: Go downstate 5+ times in a single encounter without fully dying
  - **Backpack**: Die within the first minute of a boss and still clear
  - **Greedy**: Die to a boss under 10% HP
  - **Pacifist**: Do the least damage on a boss kill
  - **Fast Service Oil Change**: Be the first to step in oil on Deimos on a failed run
  - **Breakfast Special**: Get egged by Gorseval and die to Sabetha's flame wall in the same session
  - **Glass Cannon (Without the Cannon)**: Go down 3+ times while doing less DPS than a boon DPS
  - **Just GG Already**: Be the last one alive on a wipe for 5+ seconds
- Re-parse feature in Admin > Manage Logs to re-process logs with updated GW2EI parser

### Fixed
- Class Completionist achievement now shows progress even when incomplete (e.g., "2/4 Guardian specs on Vale Guardian")

## [1.10.3] - 2025-02-12

### Fixed
- Wing 8 CM Clear achievement fixed: Greer/Ura use NM ID with IsCM flag, Decima CM=26867

## [1.10.2] - 2025-02-12

### Added
- Mechanics page now shows which boss each mechanic comes from
- Mechanics page: Boss filter dropdown to filter mechanics by boss
- Boss names on Mechanics page link to their GW2 Wiki pages
- 9 new profession-specific guild achievements (one for each class):
  - Oops All Downstate (Elementalist), Shroud Squad (Necromancer), The Clone Wars (Mesmer)
  - Blue Man Group (Guardian), Box of Crayons (Warrior), Channel Surfers (Revenant)
  - Over-Engineered (Engineer), Nature Documentary (Ranger), Pickpocket Convention (Thief)

### Changed
- Logs page: Boss dropdown now filters by selected wing
- Logs page: Wing dropdown shows wing names (e.g., "Wing 1 - Spirit Vale")

### Fixed
- Logs page now displays properly on smaller screens
- Wing 3 boss trigger IDs corrected: KC=16235, Xera=16246 (were swapped/incorrect)

## [1.10.1] - 2025-02-12

### Fixed
- Mechanic Lookup page now displays properly on smaller screens
- Removed checkmark indicator from Personal Bests toggle on player profile

## [1.10.0] - 2025-02-12

### Added
- **Achievement System** - Track personal and guild accomplishments
  - 30 Personal Achievements across 9 categories:
    - **Wing Master** (8): Complete all bosses in a wing on every role
    - **Milestones** (4): First Kill, Veteran (50), Centurion (100), Dedicated (25 sessions)
    - **Completion** (4): Clear all bosses in Wings 1-7, Legendary Raider (all CMs), Wing 8 clears
    - **Performance** (4): The Carry (30%+ squad DPS), Immortal (10 deathless kills), Clutch Player, Speed Demon
    - **Records** (2): Record Holder, Former Champion
    - **Spec Diversity** (4): Versatile (10 specs on one boss), Jack of All Trades (20), Master of One (100 kills on one spec), Class Completionist (all 4 elite specs for a profession on one boss)
    - **Support** (3): Guardian Angel (most resurrects), CC Champion (most breakbar), The Enabler (highest boon DPS)
    - **Dedication** (2): Session Warrior (10+ kills in session), Keeping Up (beat personal best 5 times)
    - **Social** (3): Dynamic Duo (50 kills with same partner), Trio, Guild Pride (all guild members)
  - 14 Guild Achievements:
    - **Flawless Wings** (8): Clear entire wing with 0 deaths
    - **Composition** (6): One Trick Guild, Heavy Metal, Cloth Squad, Leather Lovers, No Duplicates, Rainbow Squad
    - **Performance** (4): Bench Warmers, Untouchable, The Comeback, Record Breakers
  - Card-based UI with icons, progress circles, and visual feedback
  - Progress tracking for incomplete achievements
  - Expandable details for Wing Master and Completion achievements
  - Achievement rarity display (X/Y players earned)
  - Links to encounter logs from achievements
- Admin panel for running achievement backfill

## [1.9.1] - 2025-02-10

### Fixed
- HTCM Prog best phase now uses canonical phase ordering (Zhaitan is correctly ranked higher than Void Giant phases)

## [1.9.0] - 2025-02-10

### Added
- Patch notes system with automatic Discord notifications on new versions
- `/patchnotes` command to view current or specific version notes
- `/versions` command to view version history

### Fixed
- Survivor award now correctly excludes /gg deaths (matches Wall of Shame logic)
- Logs view now shows most recent first by default
- HTCM Prog best phase now correctly correlates phase name with phase index
- Session summary header layout improvements for smaller screens

## [1.8.0] - 2025-02-04

### Added
- Session Summary MVP Stats
  - Top DPS (non-support players)
  - Best Boon DPS (support players)
  - Best CC (breakbar damage)
  - Most Resurrects
  - Survivor (fewest deaths)
- Wall of Shame Stats
  - Most Deaths, Most Downs, Least CC, Most Damage Taken
- Wipes now included in MVP stats calculations

## [1.7.2] - 2025-02-03

### Fixed
- Death tracking now excludes /gg (forfeit) deaths for more accurate stats

## [1.7.1] - 2025-02-01

### Fixed
- Rescan service no longer times out
- Background scan context issues
- JSON rescan file path (report.json not log.json)
- Uses built-in arcdps check for healing power

## [1.7.0] - 2025-02-01

### Added
- Healing Power stat tracking
- Healing DPS leaderboard
- ReScan JSON service for re-importing encounter data without re-parsing logs
- Top 5 "Tooter" notifications when records are broken
- Record notifications now show all players who broke the record

### Fixed
- Aetherblade Hideout and Harvest Temple DPS calculations
- Leaderboard now indicates if top DPS was on a boon build

## [1.6.1] - 2025-01-31

### Added
- New bot commands: `/leaderboard`, `/myrecords`, `/myboonrecords`, `/help`

## [1.6.0] - 2025-01-30

### Added
- Discord Bot integration
  - Session end summary notifications
  - New record notifications
  - HTCM progression milestone notifications
  - `/config notifications` - Set notification channel
  - `/config shame` - Toggle Wall of Shame
  - `/config status` - View bot configuration
  - `/link` - Link Discord to GW2 account

### Fixed
- Docker configuration for Discord bot

## [1.5.2] - 2025-01-29

### Fixed
- HTCM Prog UI responsiveness
- DateTime offset display issues
- Home screen date display

## [1.5.1] - 2025-01-28

### Fixed
- Mechanic ICD (internal cooldown) moved to in-memory for performance
- Mordremoth shockwave mechanic tracking
- HTCM HTML log viewer integration

## [1.5.0] - 2025-01-28

### Added
- HTCM (Harvest Temple CM) Progression Tracking
  - Session-by-session progress view
  - Best phase and HP tracking
  - Pull-by-pull breakdown
  - Player mechanics tracking per session
- Mechanics lookup and tracking system
- Damage taken stat in yearly recap
- Download raw .zevtc files from encounter pages

## [1.4.2] - 2025-01-26

### Added
- Delete logs from admin page
- Log links in leaderboard entries

## [1.4.1] - 2025-01-25

### Fixed
- Twin Largos encounter handling (multiple attempts)
- Yearly recap only shows for completed years
- Boss page now only shows guild members in DPS rankings

## [1.4.0] - 2025-01-25

### Added
- Log Search/Filter page with filters for boss, wing, CM, success, date range
- HTML Report Viewer (view reports like dps.report without leaving the app)
- Leaderboards UI with better groupings
- Cloudflare tunnel support for deployment

### Fixed
- Processing race condition

## [1.3.1] - 2025-01-25

### Fixed
- Dockerfile path issues
- PostgreSQL local storage configuration

## [1.3.0] - 2025-01-24

### Added
- Automatic .zevtc log parsing with GW2 Elite Insights
- Drop logs into queue folder for automatic processing
- Docker Compose for local builds

## [1.2.0] - 2025-01-22

### Added
- Personal Player Recap (Spotify Wrapped style yearly summary per player)

## [1.1.1] - 2025-01-22

### Added
- Admin panel with basic authentication
- Most recent session stats on home page
- Boss information pages

### Fixed
- Timestamp handling improvements
- Upload processing reliability

## [1.1.0] - 2025-01-22

### Added
- Yearly Guild Recap feature with fun statistics

## [1.0.0] - 2025-01-21

### Added
- Initial release
- Log import from Elite Insights JSON
- DPS Leaderboards per boss
- Player statistics and profiles
- Guild member filtering (exclude pugs from stats)
- Included players management
- MudBlazor-based responsive UI
- PostgreSQL database storage
- Docker deployment support
