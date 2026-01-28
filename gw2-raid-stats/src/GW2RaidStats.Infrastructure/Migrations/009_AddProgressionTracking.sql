-- Add progression tracking columns for HTCM and other prog bosses
ALTER TABLE encounters ADD COLUMN IF NOT EXISTS furthest_phase VARCHAR(100);
ALTER TABLE encounters ADD COLUMN IF NOT EXISTS furthest_phase_index INT;
ALTER TABLE encounters ADD COLUMN IF NOT EXISTS boss_health_percent_remaining DECIMAL(5,2);
