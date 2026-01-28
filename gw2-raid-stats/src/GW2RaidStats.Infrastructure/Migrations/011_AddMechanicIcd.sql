-- Add ICD (Internal Cooldown) to mechanic_events for multi-hit grouping
ALTER TABLE mechanic_events ADD COLUMN IF NOT EXISTS icd_ms INT NOT NULL DEFAULT 0;
