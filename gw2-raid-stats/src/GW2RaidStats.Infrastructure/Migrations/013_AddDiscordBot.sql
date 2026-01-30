-- Discord bot configuration per server
CREATE TABLE discord_config (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    guild_id BIGINT NOT NULL UNIQUE,
    guild_name VARCHAR(100),
    notification_channel_id BIGINT,
    notifications_enabled BOOLEAN NOT NULL DEFAULT false,
    wall_of_shame_enabled BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Link Discord users to GW2 player accounts
CREATE TABLE discord_user_links (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    discord_user_id BIGINT NOT NULL UNIQUE,
    player_id UUID NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    personal_best_dms_enabled BOOLEAN NOT NULL DEFAULT true,
    wall_of_shame_opted_in BOOLEAN NOT NULL DEFAULT false,
    linked_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Notification queue for cross-process communication
CREATE TABLE notification_queue (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    notification_type VARCHAR(50) NOT NULL,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at TIMESTAMPTZ
);

-- Index for efficient polling of unprocessed notifications
CREATE INDEX idx_notification_queue_unprocessed
ON notification_queue (created_at)
WHERE processed_at IS NULL;

-- Index for looking up user links
CREATE INDEX idx_discord_user_links_player
ON discord_user_links (player_id);
