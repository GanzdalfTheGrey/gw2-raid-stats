-- Adds the average-stack-count side of Debilitated tracking to
-- player_encounter_phase_stats. EI emits "uptime" and "presence" separately for
-- stacking buffs: uptime is the average stack count (0-5 for Debilitated) and
-- presence is the actual % of phase time the buff was up at any stack count.
-- We were previously storing the average-stack value into the uptime % column,
-- which under-reported the metric vs the EI HTML report.
--
-- After applying this migration, DELETE FROM player_encounter_phase_stats and
-- then run Admin → Manage Logs → Rescan so the importer repopulates both
-- columns with the correct meaning (RescanService skips encounters that
-- already have phase-stat rows).
ALTER TABLE player_encounter_phase_stats
    ADD COLUMN IF NOT EXISTS debilitated_avg_stacks DECIMAL;
