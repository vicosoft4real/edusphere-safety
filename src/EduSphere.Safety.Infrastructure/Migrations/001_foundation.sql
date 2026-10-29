-- 001_foundation.sql — EduSphere Safety foundation schema.
-- Idempotent-friendly: every object uses IF NOT EXISTS, and 001 is recorded in
-- schema_migrations exactly once (INSERT ... ON CONFLICT DO NOTHING).
-- NOTE: no RLS policies in 001. The repository sets `app.tenant` per
-- transaction (SET LOCAL) as a defense-in-depth hook for future policies.
-- NOTE: each statement is on a single physical line so the file also applies
-- cleanly via `postgres --single`, which executes input line by line.
CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS tenants (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS tenant_members (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, user_id TEXT NOT NULL, role TEXT NOT NULL DEFAULT 'member', created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, user_id));
CREATE TABLE IF NOT EXISTS devices (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, id TEXT NOT NULL, key_hash TEXT NOT NULL, revoked BOOLEAN NOT NULL DEFAULT FALSE, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, id));
CREATE TABLE IF NOT EXISTS cameras (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, id TEXT NOT NULL, campus_id TEXT NOT NULL, device_id TEXT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, id), FOREIGN KEY (tenant_id, device_id) REFERENCES devices (tenant_id, id) ON DELETE SET NULL);
CREATE TABLE IF NOT EXISTS incidents (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, id UUID NOT NULL, campus_id TEXT NOT NULL, event_type TEXT NOT NULL, camera_id TEXT NOT NULL, edge_event_id TEXT NULL, state TEXT NOT NULL CHECK (state IN ('New','Acknowledged','Investigating','Resolved','Dismissed')), version INTEGER NOT NULL DEFAULT 0, detected_at TIMESTAMPTZ NOT NULL DEFAULT now(), rule_version TEXT NOT NULL DEFAULT '', model_version TEXT NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, id));
-- Partial unique: NULL edge_event_ids never collide with each other.
CREATE UNIQUE INDEX IF NOT EXISTS ux_incidents_tenant_edge ON incidents (tenant_id, edge_event_id) WHERE edge_event_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_incidents_tenant_state_created ON incidents (tenant_id, state, created_at);
CREATE TABLE IF NOT EXISTS incident_activity (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id TEXT NOT NULL, incident_id UUID NOT NULL, actor_id TEXT NOT NULL, reason TEXT NULL, occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(), FOREIGN KEY (tenant_id, incident_id) REFERENCES incidents (tenant_id, id) ON DELETE CASCADE);
CREATE INDEX IF NOT EXISTS ix_incident_activity_tenant_incident ON incident_activity (tenant_id, incident_id);
CREATE TABLE IF NOT EXISTS outbox (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id TEXT NOT NULL, type TEXT NOT NULL, payload JSONB NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), locked_until TIMESTAMPTZ NULL, attempts INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS ix_outbox_due ON outbox (locked_until, created_at);
INSERT INTO schema_migrations (version) VALUES ('001') ON CONFLICT DO NOTHING;
