-- Per-user opt-in for event reminder DMs. Global (one row per Discord user) rather
-- than per-guild to keep the UX simple: just a yes/no toggle, off by default. The
-- lead time itself is currently a fixed 30 minutes in EventReminderProcessor; a
-- per-event lead-time column on the events table is a Phase 2 enhancement.
CREATE TABLE IF NOT EXISTS event_reminder_preferences (
    discord_user_id BIGINT PRIMARY KEY,
    enabled BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at TIMESTAMPTZ NOT NULL
);
