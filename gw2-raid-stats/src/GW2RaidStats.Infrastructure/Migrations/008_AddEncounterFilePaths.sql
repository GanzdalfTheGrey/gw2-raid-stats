-- Add file path columns to encounters for storing .zevtc, JSON, and HTML files
ALTER TABLE encounters ADD COLUMN IF NOT EXISTS files_path TEXT;
ALTER TABLE encounters ADD COLUMN IF NOT EXISTS original_filename TEXT;

-- files_path stores the relative path like "2025/01/{guid}"
-- Full paths are derived:
--   - {base}/encounters/{files_path}/log.zevtc
--   - {base}/encounters/{files_path}/report.json
--   - {base}/encounters/{files_path}/report.html

COMMENT ON COLUMN encounters.files_path IS 'Relative path to encounter files (e.g., 2025/01/{guid})';
COMMENT ON COLUMN encounters.original_filename IS 'Original uploaded filename (e.g., 20250115-213052.zevtc)';
