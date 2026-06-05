-- Recurring event templates. Each template defines the day-of-week + time-of-day
-- at which it normally fires; events are spawned MANUALLY from the templates page
-- via the "Post next" button (no auto-scheduler in Phase 2 — that's a follow-up).
-- timezone is an IANA name (e.g. "America/Chicago") so DST handling is correct
-- when computing the next occurrence.
CREATE TABLE IF NOT EXISTS event_templates (
    id UUID PRIMARY KEY,
    guild_id BIGINT NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    day_of_week INT NOT NULL,           -- 0 = Sunday .. 6 = Saturday (matches DayOfWeek enum)
    time_of_day TEXT NOT NULL,          -- "HH:mm:ss" formatted TimeSpan
    timezone TEXT NOT NULL DEFAULT 'UTC',
    role_slots_json TEXT,
    active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_event_templates_active ON event_templates(active);
