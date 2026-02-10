-- Track app version for patch notes notifications
CREATE TABLE IF NOT EXISTS app_version (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version VARCHAR(50) NOT NULL,
    broadcast_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Index for quick version lookups
CREATE INDEX IF NOT EXISTS idx_app_version_version ON app_version(version);
CREATE INDEX IF NOT EXISTS idx_app_version_broadcast_at ON app_version(broadcast_at DESC);
