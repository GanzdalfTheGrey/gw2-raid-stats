-- Per-player raid-night availability, edited on the admin Player Availability page.
-- One row per player. monday_status / tuesday_status: 0 = unavailable (red),
-- 1 = maybe / one-day-a-week (yellow), 2 = available (green); NULL = not set.
CREATE TABLE IF NOT EXISTS player_availability (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    monday_status SMALLINT,
    tuesday_status SMALLINT,
    note TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_player_availability_player
    ON player_availability(player_id);
