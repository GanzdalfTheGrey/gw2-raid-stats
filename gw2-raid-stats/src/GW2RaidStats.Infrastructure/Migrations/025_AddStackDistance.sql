-- Per-player average distance to the squad's centroid, from EI's statsAll.stackDist
-- (GW2 units; lower = tighter stacking). NULL = not captured.
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS stack_distance DECIMAL;
