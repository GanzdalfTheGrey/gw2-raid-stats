-- Per-player self-uptime % for Quickness and Alacrity (from EI buffUptimesActive).
-- Distinct from quickness_generation / alacrity_generation: those measure what the
-- player generated for the squad; these measure what the player had on themselves.
-- Used by the Boon Coverage report to compute sub-group averages (Generation metric)
-- and to spot positioning issues (Self metric) when the player wasn't the booner.
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS quickness_self_uptime DECIMAL;
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS alacrity_self_uptime DECIMAL;
