-- Add Healing Power stat column (character attribute, always available from EI)
-- This is different from the healing extension stats which require arcdps extension

ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS healing_power_stat INT NOT NULL DEFAULT 0;

-- Create index for healer queries (players with 1000+ healing power are likely healers)
CREATE INDEX IF NOT EXISTS idx_player_encounters_healing_power_stat ON player_encounters(healing_power_stat) WHERE healing_power_stat >= 1000;
