-- Remove ICD column since we use a hardcoded lookup table instead
ALTER TABLE mechanic_events DROP COLUMN IF EXISTS icd_ms;
