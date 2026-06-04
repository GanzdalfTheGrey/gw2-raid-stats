-- Per-feature destination channels for the Discord bot. Both nullable; null means
-- "fall back to notification_channel_id" so existing single-channel guilds keep
-- working with no config changes.
ALTER TABLE discord_config ADD COLUMN IF NOT EXISTS squad_builder_channel_id BIGINT;
ALTER TABLE discord_config ADD COLUMN IF NOT EXISTS events_channel_id BIGINT;
