-- Add boon generation columns to player_encounters
-- These track how much quickness/alacrity each player generated for the squad

ALTER TABLE player_encounters
ADD COLUMN IF NOT EXISTS quickness_generation DECIMAL(10,2),
ADD COLUMN IF NOT EXISTS alacrity_generation DECIMAL(10,2);

-- Index for leaderboard queries filtering by boon providers
CREATE INDEX IF NOT EXISTS idx_player_encounters_quickness ON player_encounters(quickness_generation)
WHERE quickness_generation > 0;

CREATE INDEX IF NOT EXISTS idx_player_encounters_alacrity ON player_encounters(alacrity_generation)
WHERE alacrity_generation > 0;
