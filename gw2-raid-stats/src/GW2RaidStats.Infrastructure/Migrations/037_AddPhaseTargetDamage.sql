-- Per-player damage to each phase's own targets (EI dpsTargets — the HTML report's
-- "Target" column). The existing damage column is everything hit during the phase
-- window, which overstates phases fought alongside other enemies (the Soo-Won
-- champions). Null on older rows until Admin → Manage Logs → Rescan fills it in.
ALTER TABLE player_encounter_phase_stats
    ADD COLUMN IF NOT EXISTS target_damage BIGINT;
