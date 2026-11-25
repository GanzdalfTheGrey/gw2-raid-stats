-- Whether each phase's main target(s) died, so the HTCM prog page can tell a cleared
-- burst window (e.g. Void Goliath killed) from a wipe mid-phase. Null on rows
-- imported before this migration until Admin → Manage Logs → Rescan fills it in.
ALTER TABLE encounter_phase_stats
    ADD COLUMN IF NOT EXISTS completed BOOLEAN;
