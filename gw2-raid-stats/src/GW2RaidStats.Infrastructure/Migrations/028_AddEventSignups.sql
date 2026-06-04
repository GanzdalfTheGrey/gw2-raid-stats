-- One signup row per (event, discord user). slot_id references a key inside the
-- event's role_slots_json (null when the event has no roles defined). player_id is
-- populated from discord_user_links when the user has linked their GW2 account.
-- ON DELETE CASCADE so deleting an event tears down its signups.
CREATE TABLE IF NOT EXISTS event_signups (
    id UUID PRIMARY KEY,
    event_id UUID NOT NULL REFERENCES events(id) ON DELETE CASCADE,
    discord_user_id BIGINT NOT NULL,
    player_id UUID,
    slot_id TEXT,
    status TEXT NOT NULL DEFAULT 'Accepted',
    signed_up_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    UNIQUE (event_id, discord_user_id)
);

CREATE INDEX IF NOT EXISTS idx_event_signups_event ON event_signups(event_id);
