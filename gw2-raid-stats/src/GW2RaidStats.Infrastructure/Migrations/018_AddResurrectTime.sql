-- Add resurrect_time column to track total time spent resurrecting allies
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS resurrect_time DECIMAL DEFAULT 0;
