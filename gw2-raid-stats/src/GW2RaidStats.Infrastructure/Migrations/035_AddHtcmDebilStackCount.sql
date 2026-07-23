-- Adds the integer stacks-gained side of Debilitated tracking to
-- player_encounter_phase_stats. EI emits one "Debilitated" mechanic event per
-- stack applied; the importer counts the events whose timestamp falls inside
-- each phase window, giving a clean per-player per-phase stack count to show
-- instead of the fractional average-stack figure.
--
-- After applying this migration, DELETE FROM player_encounter_phase_stats and
-- then run Admin → Manage Logs → Rescan so the importer repopulates the rows
-- with the new column (RescanService skips encounters that already have
-- phase-stat rows).
ALTER TABLE player_encounter_phase_stats
    ADD COLUMN IF NOT EXISTS debilitated_stacks INT;
