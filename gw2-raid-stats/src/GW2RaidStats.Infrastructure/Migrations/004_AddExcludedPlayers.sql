-- Migration: Add excluded_players table for filtering pugs from leaderboards
-- Excluded players won't claim leaderboard spots, and show as "Pug" in boon supports

CREATE TABLE IF NOT EXISTS excluded_players (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_name VARCHAR(255) NOT NULL UNIQUE,
    reason VARCHAR(500),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_excluded_players_account ON excluded_players(account_name);
