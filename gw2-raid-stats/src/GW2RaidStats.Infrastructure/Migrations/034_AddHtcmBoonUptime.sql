-- Adds per-phase Quickness and Alacrity uptime to player_encounter_phase_stats,
-- sourced from EI's buffUptimesActive "presence" field (% of phase active time the
-- boon was up) for buff IDs 1187 (Quickness) and 30328 (Alacrity). Stored for every
-- player, not just boon givers: the Most Valuable Proggers boon category scores a
-- giver on the average uptime *received by their subgroup*, which needs the
-- non-giver rows to average over.
--
-- After applying this migration, DELETE FROM player_encounter_phase_stats and
-- then run Admin → Manage Logs → Rescan so the importer repopulates every column
-- (RescanService skips encounters that already have phase-stat rows). Until that
-- backfill runs, the boon category scores 0 for everyone.
ALTER TABLE player_encounter_phase_stats
    ADD COLUMN IF NOT EXISTS quickness_uptime_pct DECIMAL,
    ADD COLUMN IF NOT EXISTS alacrity_uptime_pct DECIMAL;
