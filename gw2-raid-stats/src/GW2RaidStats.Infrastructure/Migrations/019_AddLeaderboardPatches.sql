-- Add leaderboard patches table for tracking balance patch reset dates
CREATE TABLE IF NOT EXISTS leaderboard_patches (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(255) NOT NULL,
    start_date TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_leaderboard_patches_start_date ON leaderboard_patches(start_date DESC);
