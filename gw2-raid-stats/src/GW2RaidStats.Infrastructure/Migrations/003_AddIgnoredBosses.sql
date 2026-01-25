-- Migration: Add ignored_bosses table for filtering stats
-- This allows admins to exclude certain bosses (e.g., prog sessions) from overall stats

CREATE TABLE IF NOT EXISTS ignored_bosses (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    trigger_id INT NOT NULL,
    boss_name VARCHAR(255) NOT NULL,
    is_cm BOOLEAN NOT NULL DEFAULT false,
    reason VARCHAR(500),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(trigger_id, is_cm)
);

CREATE INDEX IF NOT EXISTS idx_ignored_bosses_trigger ON ignored_bosses(trigger_id, is_cm);
