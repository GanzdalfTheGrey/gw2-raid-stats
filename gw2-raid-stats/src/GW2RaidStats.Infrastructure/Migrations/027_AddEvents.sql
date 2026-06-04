-- Discord bot events: scheduled raid nights or one-off events that members sign up
-- for via Discord buttons. role_slots_json is null when the event has no roles
-- defined (Accept / Reserve only); otherwise it stores the role-slot list
-- (RoleSlot record from Core.Events). message_id + channel_id are populated when
-- the event is first posted to Discord so the bot can find it for live re-renders.
CREATE TABLE IF NOT EXISTS events (
    id UUID PRIMARY KEY,
    template_id UUID,
    guild_id BIGINT NOT NULL,
    channel_id BIGINT,
    message_id BIGINT,
    title TEXT NOT NULL,
    description TEXT,
    scheduled_at TIMESTAMPTZ NOT NULL,
    timezone TEXT NOT NULL DEFAULT 'UTC',
    status TEXT NOT NULL DEFAULT 'Scheduled',
    role_slots_json TEXT,
    reminder_sent_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_events_scheduled ON events(scheduled_at);
CREATE INDEX IF NOT EXISTS idx_events_guild ON events(guild_id);
