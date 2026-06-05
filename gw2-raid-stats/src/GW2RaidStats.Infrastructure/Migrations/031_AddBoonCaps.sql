-- Per-event toggle for the hardcoded raid boon caps (max 2 Heals, max 2 Boon DPS,
-- max 2 Quicks, max 2 Alacs derived from each slot's role + boon tags stored inside
-- role_slots_json — no schema change needed for the slot tags themselves since the
-- column is JSON). Existing events default to FALSE so signup behaviour is unchanged.
ALTER TABLE events ADD COLUMN IF NOT EXISTS enforce_boon_caps BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE event_templates ADD COLUMN IF NOT EXISTS enforce_boon_caps BOOLEAN NOT NULL DEFAULT FALSE;
