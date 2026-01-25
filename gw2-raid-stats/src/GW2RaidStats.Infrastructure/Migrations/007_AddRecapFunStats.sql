-- Migration: Add recap fun stats table for configurable mechanic-based achievements

CREATE TABLE IF NOT EXISTS recap_fun_stats (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    mechanic_name VARCHAR(255) NOT NULL,
    display_title VARCHAR(255) NOT NULL,
    description TEXT,
    is_positive BOOLEAN NOT NULL DEFAULT false,
    display_order INT NOT NULL DEFAULT 0,
    is_enabled BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_recap_fun_stats_mechanic UNIQUE (mechanic_name)
);

-- Index for enabled stats ordered by display order
CREATE INDEX IF NOT EXISTS idx_recap_fun_stats_enabled_order ON recap_fun_stats (is_enabled, display_order);
