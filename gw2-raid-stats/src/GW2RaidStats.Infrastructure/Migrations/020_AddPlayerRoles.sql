-- Player role capability tracking (Phase 1 of role/squad-builder feature)
--
-- Two tables:
--   mechanic_roles: admin-editable catalog of boss-specific mechanic roles
--                   (e.g. Pylon Kite at Q2, Hand Kiter at Deimos).
--   player_role_capabilities: per-player status (Can/Cant/Maybe/WantToLearn)
--                             for each generic role and each mechanic role.
--
-- Generic roles are a closed enum in code (see GW2RaidStats.Core.Roles.GenericRole)
-- and are not stored as rows; player_role_capabilities references them by enum value.
--
-- slot_constraint enum (mirrors GW2RaidStats.Core.Roles.MechanicConstraint):
--   0 = Any
--   1 = PreferHealer
--   2 = PreferBoonDps
--   3 = PreferDps
--   4 = RequiresHealer
--   5 = RequiresBoonDps
--   6 = RequiresDps        (boon DPS or pure DPS)
--   7 = RequiresPureDps    (pure DPS only)
--
-- status enum (mirrors GW2RaidStats.Core.Roles.RoleCapabilityStatus):
--   0 = Cant, 1 = Maybe, 2 = Can, 3 = WantToLearn

CREATE TABLE IF NOT EXISTS mechanic_roles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    trigger_id INTEGER NOT NULL,
    boss_name VARCHAR(100) NOT NULL,
    name VARCHAR(100) NOT NULL,
    slot_constraint SMALLINT NOT NULL DEFAULT 0,
    min_count INTEGER NOT NULL DEFAULT 1,
    max_count INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT mechanic_roles_count_chk CHECK (min_count >= 1 AND max_count >= min_count),
    CONSTRAINT mechanic_roles_unique UNIQUE (trigger_id, name)
);

CREATE INDEX IF NOT EXISTS idx_mechanic_roles_trigger ON mechanic_roles(trigger_id);

CREATE TABLE IF NOT EXISTS player_role_capabilities (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    generic_role SMALLINT,
    mechanic_role_id UUID REFERENCES mechanic_roles(id) ON DELETE CASCADE,
    status SMALLINT NOT NULL,
    notes TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT player_role_caps_one_role CHECK (
        (generic_role IS NOT NULL AND mechanic_role_id IS NULL) OR
        (generic_role IS NULL AND mechanic_role_id IS NOT NULL)
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_player_role_caps_generic
    ON player_role_capabilities(player_id, generic_role)
    WHERE generic_role IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_player_role_caps_mechanic
    ON player_role_capabilities(player_id, mechanic_role_id)
    WHERE mechanic_role_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_player_role_caps_player
    ON player_role_capabilities(player_id);

-- Seed the mechanic role catalog.
-- Constraint values in comments use the names from the enum above for readability.

-- Wing 1 - Spirit Vale
INSERT INTO mechanic_roles (trigger_id, boss_name, name, slot_constraint, min_count, max_count, sort_order) VALUES
    (15438, 'Vale Guardian', 'Tank',           4, 1, 1, 1),  -- RequiresHealer
    (15429, 'Gorseval',      'Tank',           4, 1, 1, 1),  -- RequiresHealer
    (15375, 'Sabetha',       'Cannons',        6, 2, 2, 1)   -- RequiresDps
ON CONFLICT (trigger_id, name) DO NOTHING;

-- Wing 2 - Salvation Pass
INSERT INTO mechanic_roles (trigger_id, boss_name, name, slot_constraint, min_count, max_count, sort_order) VALUES
    (16123, 'Slothasor', 'Mushrooms',      6, 4, 4, 1),  -- RequiresDps
    (16137, 'Matthias',  'Reflect',        0, 1, 1, 1),  -- Any
    (16137, 'Matthias',  'Backup Reflect', 0, 1, 1, 2)   -- Any
ON CONFLICT (trigger_id, name) DO NOTHING;

-- Wing 3 - Stronghold of the Faithful
INSERT INTO mechanic_roles (trigger_id, boss_name, name, slot_constraint, min_count, max_count, sort_order) VALUES
    (16235, 'Keep Construct', 'Tank',   0, 1, 1, 1),  -- Any
    (16235, 'Keep Construct', 'Pusher', 0, 1, 1, 2),  -- Any
    (16246, 'Xera',           'Tank',   0, 1, 1, 1)   -- Any
ON CONFLICT (trigger_id, name) DO NOTHING;

-- Wing 4 - Bastion of the Penitent
-- (Cairn and Samarog have no mechanic roles)
INSERT INTO mechanic_roles (trigger_id, boss_name, name, slot_constraint, min_count, max_count, sort_order) VALUES
    (17172, 'Mursaat Overseer', 'Claim',     1, 1, 1, 1),  -- PreferHealer
    (17172, 'Mursaat Overseer', 'Dispel',    0, 1, 1, 2),  -- Any
    (17172, 'Mursaat Overseer', 'Protect',   0, 1, 1, 3),  -- Any
    (17154, 'Deimos',           'Tank',      4, 1, 1, 1),  -- RequiresHealer
    (17154, 'Deimos',           'Hand Kiter',7, 1, 1, 2),  -- RequiresPureDps
    (17154, 'Deimos',           'Oil Kiter', 7, 1, 1, 3)   -- RequiresPureDps
ON CONFLICT (trigger_id, name) DO NOTHING;

-- Wing 5 - Hall of Chains
INSERT INTO mechanic_roles (trigger_id, boss_name, name, slot_constraint, min_count, max_count, sort_order) VALUES
    (19767, 'Soulless Horror', 'Tank',       4, 2, 2, 1),  -- RequiresHealer
    (19450, 'Dhuum',           'Tank + G3',  4, 1, 1, 1),  -- RequiresHealer
    (19450, 'Dhuum',           'Kiter + G2', 7, 1, 1, 2),  -- RequiresPureDps
    (19450, 'Dhuum',           'G1',         7, 1, 1, 3),  -- RequiresPureDps
    (19450, 'Dhuum',           'First G2',   7, 1, 1, 4)   -- RequiresPureDps
ON CONFLICT (trigger_id, name) DO NOTHING;

-- Wing 6 - Mythwright Gambit
INSERT INTO mechanic_roles (trigger_id, boss_name, name, slot_constraint, min_count, max_count, sort_order) VALUES
    (43974, 'Conjured Amalgamate', 'Sword',     0, 1, 1, 1),  -- Any (warn if both Sword+Shield are healers)
    (43974, 'Conjured Amalgamate', 'Shield',    0, 1, 1, 2),  -- Any
    (21105, 'Twin Largos',         'Tank',      0, 2, 2, 1),  -- Any
    (20934, 'Qadim',               'Kiter',     7, 1, 1, 1),  -- RequiresPureDps
    (20934, 'Qadim',               'Main Tank', 4, 1, 1, 2),  -- RequiresHealer
    (20934, 'Qadim',               'Matt Tank', 3, 1, 1, 3),  -- PreferDps
    (20934, 'Qadim',               'Lamp',      0, 2, 3, 4)   -- Any (min 2, optional 3rd)
ON CONFLICT (trigger_id, name) DO NOTHING;

-- Wing 7 - The Key of Ahdashim
-- (Sabir has no mechanic roles)
INSERT INTO mechanic_roles (trigger_id, boss_name, name, slot_constraint, min_count, max_count, sort_order) VALUES
    (22006, 'Cardinal Adina',      'Tank',         4, 1, 1, 1),  -- RequiresHealer
    (22000, 'Qadim the Peerless',  'Pylon Kiters', 6, 3, 3, 1),  -- RequiresDps
    (22000, 'Qadim the Peerless',  'Tank',         0, 1, 1, 2)   -- Any
ON CONFLICT (trigger_id, name) DO NOTHING;

-- Wing 8 - Mount Balrior: intentionally seeded empty; admins will populate via UI.
