-- Re-add excluded_players table for manually overriding the auto-include logic.
--
-- Migration 005 dropped the original excluded_players table when the feature
-- moved to an inclusion-only model. We're re-adding it now with new semantics:
-- a player listed here is removed from the included set even if they pass the
-- auto-include encounter threshold. Useful for inactive members who used to
-- play but haven't shown up in over a year.
--
-- include_set = (manually_included ∪ auto_included_by_threshold) − excluded_players

CREATE TABLE IF NOT EXISTS excluded_players (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_name VARCHAR(255) NOT NULL UNIQUE,
    reason VARCHAR(500),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_excluded_players_account ON excluded_players(account_name);
