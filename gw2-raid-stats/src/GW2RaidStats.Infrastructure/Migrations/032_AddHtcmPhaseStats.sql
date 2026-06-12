-- Per-player per-phase stats for HTCM (trigger 43488) only — long fights with many
-- phases, where coordinating burst on specific phase targets (Time Caster, Giants,
-- Saltspray) and tracking phase-specific deaths/debilitated time matters. Bounded
-- storage: ~12 phases × 10 players per pull ≈ 120 rows per pull.
--
-- dead_at_phase_start uses EI's deadCombatTimes (timestamp pairs of player deaths)
-- so it's exact, not heuristic. debilitated_uptime_pct is from buffUptimesActive
-- for buff ID 67972; nullable so phases without buff data show as no measurement.
CREATE TABLE IF NOT EXISTS player_encounter_phase_stats (
    id UUID PRIMARY KEY,
    encounter_id UUID NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
    player_id UUID NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    phase_index INT NOT NULL,
    phase_name TEXT NOT NULL,
    dps INT NOT NULL,
    damage BIGINT NOT NULL,
    dead_count INT NOT NULL DEFAULT 0,
    down_count INT NOT NULL DEFAULT 0,
    dead_duration_ms INT NOT NULL DEFAULT 0,
    down_duration_ms INT NOT NULL DEFAULT 0,
    dead_at_phase_start BOOLEAN NOT NULL DEFAULT FALSE,
    debilitated_uptime_pct DECIMAL,
    created_at TIMESTAMPTZ NOT NULL,
    UNIQUE (encounter_id, player_id, phase_index)
);

CREATE INDEX IF NOT EXISTS idx_pepstats_encounter ON player_encounter_phase_stats(encounter_id);
CREATE INDEX IF NOT EXISTS idx_pepstats_player ON player_encounter_phase_stats(player_id);
