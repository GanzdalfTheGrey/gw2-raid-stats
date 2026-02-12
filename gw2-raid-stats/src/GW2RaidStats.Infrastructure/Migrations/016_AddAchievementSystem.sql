-- Achievement System
-- Adds tables for tracking personal and guild achievements

-- Player achievements (earned achievements)
CREATE TABLE IF NOT EXISTS player_achievements (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    achievement_code VARCHAR(50) NOT NULL,
    achieved_at TIMESTAMPTZ NOT NULL,
    context JSONB,  -- Achievement-specific data (encounter_id, partner, spec, etc.)
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(player_id, achievement_code)
);

CREATE INDEX IF NOT EXISTS idx_player_achievements_player ON player_achievements(player_id);
CREATE INDEX IF NOT EXISTS idx_player_achievements_code ON player_achievements(achievement_code);

-- Guild achievements (collective accomplishments)
CREATE TABLE IF NOT EXISTS guild_achievements (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    achievement_code VARCHAR(50) NOT NULL UNIQUE,
    achieved_at TIMESTAMPTZ NOT NULL,
    context JSONB,  -- Achievement-specific data
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Add role column to player_encounters for Wing Master tracking
-- Roles: heal_alac, heal_quick, dps_alac, dps_quick, pure_dps
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS role VARCHAR(20);

-- Index for role-based queries
CREATE INDEX IF NOT EXISTS idx_player_encounters_role ON player_encounters(role) WHERE role IS NOT NULL;
