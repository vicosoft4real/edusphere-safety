-- 002_devices.sql — device enrolment and heartbeat columns.
-- Idempotent-friendly: every object uses IF NOT EXISTS, consistent with 001.
-- NOTE: each statement is on a single physical line so the file also applies
-- cleanly via `postgres --single`, which executes input line by line.
CREATE TABLE IF NOT EXISTS enrolment_codes (code_hash TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, campus_id TEXT NULL, expires_at TIMESTAMPTZ NOT NULL, used_at TIMESTAMPTZ NULL, created_by TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS ix_enrolment_codes_tenant ON enrolment_codes (tenant_id, expires_at);
ALTER TABLE devices ADD COLUMN IF NOT EXISTS campus_id TEXT NULL;
ALTER TABLE devices ADD COLUMN IF NOT EXISTS software_version TEXT NOT NULL DEFAULT '';
ALTER TABLE devices ADD COLUMN IF NOT EXISTS model_version TEXT NOT NULL DEFAULT '';
ALTER TABLE devices ADD COLUMN IF NOT EXISTS config_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE devices ADD COLUMN IF NOT EXISTS last_heartbeat_at TIMESTAMPTZ NULL;
INSERT INTO schema_migrations (version) VALUES ('002') ON CONFLICT DO NOTHING;
