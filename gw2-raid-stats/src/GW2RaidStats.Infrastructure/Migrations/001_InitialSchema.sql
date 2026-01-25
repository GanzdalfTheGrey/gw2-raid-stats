-- GW2 Raid Stats - Initial Schema
-- Migration: 001_InitialSchema

-- Players table
CREATE TABLE players (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_name VARCHAR(50) NOT NULL UNIQUE,
    display_name VARCHAR(100),
    first_seen TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Encounters table
CREATE TABLE encounters (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    trigger_id INT NOT NULL,
    boss_name VARCHAR(100) NOT NULL,
    wing INT,
    is_cm BOOLEAN NOT NULL DEFAULT FALSE,
    is_legendary_cm BOOLEAN NOT NULL DEFAULT FALSE,
    success BOOLEAN NOT NULL,
    duration_ms INT NOT NULL,
    encounter_time TIMESTAMPTZ NOT NULL,
    recorded_by VARCHAR(50),
    log_url VARCHAR(500),
    json_hash VARCHAR(64) UNIQUE,
    icon_url VARCHAR(500),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Player encounter stats (join table with stats)
CREATE TABLE player_encounters (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    encounter_id UUID NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
    character_name VARCHAR(100) NOT NULL,
    profession VARCHAR(50) NOT NULL,
    squad_group INT,

    -- DPS stats
    dps INT NOT NULL,
    damage BIGINT NOT NULL,
    power_dps INT,
    condi_dps INT,
    breakbar_damage DECIMAL(10,2),

    -- Defense stats
    deaths INT NOT NULL DEFAULT 0,
    death_duration_ms INT DEFAULT 0,
    downs INT NOT NULL DEFAULT 0,
    down_duration_ms INT DEFAULT 0,
    damage_taken BIGINT DEFAULT 0,

    -- Support stats
    resurrects INT DEFAULT 0,
    condi_cleanse INT DEFAULT 0,
    boon_strips INT DEFAULT 0,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE(player_id, encounter_id)
);

-- Mechanics summary (for fun stats)
CREATE TABLE mechanic_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    encounter_id UUID NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
    player_id UUID REFERENCES players(id) ON DELETE CASCADE,
    mechanic_name VARCHAR(100) NOT NULL,
    mechanic_full_name VARCHAR(200),
    description TEXT,
    event_time_ms INT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for common queries
CREATE INDEX idx_encounters_boss ON encounters(boss_name);
CREATE INDEX idx_encounters_time ON encounters(encounter_time);
CREATE INDEX idx_encounters_success ON encounters(success);
CREATE INDEX idx_encounters_wing ON encounters(wing);
CREATE INDEX idx_encounters_cm ON encounters(is_cm);

CREATE INDEX idx_player_encounters_player ON player_encounters(player_id);
CREATE INDEX idx_player_encounters_encounter ON player_encounters(encounter_id);
CREATE INDEX idx_player_encounters_profession ON player_encounters(profession);

CREATE INDEX idx_mechanic_events_encounter ON mechanic_events(encounter_id);
CREATE INDEX idx_mechanic_events_player ON mechanic_events(player_id);
CREATE INDEX idx_mechanic_events_name ON mechanic_events(mechanic_name);
