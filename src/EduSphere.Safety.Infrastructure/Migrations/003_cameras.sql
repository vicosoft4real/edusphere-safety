-- 003_cameras.sql — camera descriptors and edge-reported connectivity.
-- Flat columns match the existing cameras style; no secret material is ever
-- stored here (connection holds only non-secret descriptors).
-- NOTE: each statement is on a single physical line so the file also applies
-- cleanly via `postgres --single`, which executes input line by line.
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS mode TEXT NOT NULL DEFAULT 'manual';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS host TEXT NOT NULL DEFAULT '';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS port INTEGER NOT NULL DEFAULT 554;
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS path TEXT NOT NULL DEFAULT '';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS last_test_ok BOOLEAN NULL;
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS last_test_detail TEXT NOT NULL DEFAULT '';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS last_test_at TIMESTAMPTZ NULL;
INSERT INTO schema_migrations (version) VALUES ('003') ON CONFLICT DO NOTHING;
