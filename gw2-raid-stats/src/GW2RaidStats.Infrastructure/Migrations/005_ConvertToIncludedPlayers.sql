-- Migration: Convert excluded_players to included_players model
-- Instead of excluding pugs, we include guild members
-- Non-included players show as "Pug" and can't claim leaderboard spots

-- Drop the old excluded_players table
DROP TABLE IF EXISTS excluded_players;

-- Create included_players table for manually included players
CREATE TABLE IF NOT EXISTS included_players (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_name VARCHAR(255) NOT NULL UNIQUE,
    reason VARCHAR(500),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_included_players_account ON included_players(account_name);

-- Create settings table for configurable values
CREATE TABLE IF NOT EXISTS settings (
    key VARCHAR(100) PRIMARY KEY,
    value VARCHAR(500) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Default auto-include threshold (players with this many+ encounters are auto-included)
INSERT INTO settings (key, value) VALUES ('auto_include_threshold', '300')
ON CONFLICT (key) DO NOTHING;
