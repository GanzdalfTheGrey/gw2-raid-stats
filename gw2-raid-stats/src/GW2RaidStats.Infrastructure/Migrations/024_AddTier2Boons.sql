-- Per-player self-uptime for "tier 2" boons (from EI buffUptimesActive, "Phase active
-- duration" basis — same source as quickness/alacrity self-uptime).
-- might_avg_stacks is average stacks 0-25 (Might is an intensity boon); the rest are
-- percentage uptime 0-100 like quickness/alacrity. NULL = not captured.
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS might_avg_stacks DECIMAL;
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS fury_uptime DECIMAL;
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS regeneration_uptime DECIMAL;
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS protection_uptime DECIMAL;
ALTER TABLE player_encounters ADD COLUMN IF NOT EXISTS swiftness_uptime DECIMAL;
