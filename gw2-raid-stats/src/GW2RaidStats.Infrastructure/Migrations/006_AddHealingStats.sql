-- Migration: Add healing stats to player_encounters for role classification
-- Healing stats help classify players as DPS, Boon DPS, or Heal Boon

ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS healing INTEGER DEFAULT 0;
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS healing_power_healing INTEGER DEFAULT 0;
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS hps INTEGER DEFAULT 0;
