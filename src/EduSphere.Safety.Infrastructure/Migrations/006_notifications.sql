-- 006_notifications.sql — delivery-attempt ledger, escalation schedules.
-- NOTE: each statement is on a single physical line so the file also applies
-- cleanly via `postgres --single`, which executes input line by line.
CREATE TABLE IF NOT EXISTS delivery_attempts (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id TEXT NOT NULL, incident_id UUID NOT NULL, channel TEXT NOT NULL DEFAULT 'email', level INTEGER NOT NULL DEFAULT 0, recipient TEXT NOT NULL, state TEXT NOT NULL CHECK (state IN ('accepted','delivered','failed','unknown')), detail TEXT NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL DEFAULT now(), FOREIGN KEY (tenant_id, incident_id) REFERENCES incidents (tenant_id, id) ON DELETE CASCADE);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_attempt_dedup ON delivery_attempts (tenant_id, incident_id, channel, level, recipient) WHERE state IN ('accepted','delivered');
CREATE TABLE IF NOT EXISTS escalation_schedules (tenant_id TEXT NOT NULL, incident_id UUID NOT NULL, level INTEGER NOT NULL, due_at TIMESTAMPTZ NOT NULL, locked_until TIMESTAMPTZ NULL, attempts INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (tenant_id, incident_id, level), FOREIGN KEY (tenant_id, incident_id) REFERENCES incidents (tenant_id, id) ON DELETE CASCADE);
CREATE INDEX IF NOT EXISTS ix_escalation_due ON escalation_schedules (due_at, locked_until);
INSERT INTO schema_migrations (version) VALUES ('006') ON CONFLICT DO NOTHING;
