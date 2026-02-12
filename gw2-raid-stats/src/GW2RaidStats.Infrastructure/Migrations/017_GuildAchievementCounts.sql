-- Add count tracking to guild achievements
-- Allows tracking how many times each achievement has been completed

-- Add completion count (defaults to 1 for existing achievements)
ALTER TABLE guild_achievements ADD COLUMN IF NOT EXISTS completion_count INT NOT NULL DEFAULT 1;

-- Add last completion tracking (for showing the most recent log)
ALTER TABLE guild_achievements ADD COLUMN IF NOT EXISTS last_achieved_at TIMESTAMPTZ;
ALTER TABLE guild_achievements ADD COLUMN IF NOT EXISTS last_context JSONB;

-- For existing achievements, set last_achieved_at = achieved_at and last_context = context
UPDATE guild_achievements
SET last_achieved_at = achieved_at, last_context = context
WHERE last_achieved_at IS NULL;

-- Make last_achieved_at NOT NULL after populating
ALTER TABLE guild_achievements ALTER COLUMN last_achieved_at SET NOT NULL;
