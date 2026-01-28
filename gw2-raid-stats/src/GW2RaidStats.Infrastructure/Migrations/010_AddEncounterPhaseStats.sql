-- Add per-phase stats tracking for progression analysis
CREATE TABLE IF NOT EXISTS encounter_phase_stats (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    encounter_id UUID NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
    phase_index INT NOT NULL,
    phase_name VARCHAR(100) NOT NULL,
    squad_dps INT NOT NULL,
    duration_ms INT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(encounter_id, phase_index)
);

CREATE INDEX IF NOT EXISTS idx_encounter_phase_stats_encounter_id ON encounter_phase_stats(encounter_id);
