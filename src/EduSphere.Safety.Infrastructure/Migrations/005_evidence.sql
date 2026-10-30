-- 005_evidence.sql — evidence manifests, expiry index, access log.
-- NOTE: each statement is on a single physical line so the file also applies
-- cleanly via `postgres --single`, which executes input line by line.
CREATE TABLE IF NOT EXISTS evidence_manifests (tenant_id TEXT NOT NULL, edge_event_id TEXT NOT NULL, incident_id UUID NULL, object_key TEXT NOT NULL, kind TEXT NOT NULL CHECK (kind IN ('clip','snapshot')), bytes BIGINT NOT NULL DEFAULT 0, sha256 TEXT NOT NULL DEFAULT '', uploaded_at TIMESTAMPTZ NULL, expires_at TIMESTAMPTZ NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, edge_event_id, kind), FOREIGN KEY (tenant_id, incident_id) REFERENCES incidents (tenant_id, id) ON DELETE SET NULL);
CREATE INDEX IF NOT EXISTS ix_evidence_expires ON evidence_manifests (expires_at) WHERE expires_at IS NOT NULL;
CREATE TABLE IF NOT EXISTS evidence_access_log (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id TEXT NOT NULL, manifest_tenant TEXT NOT NULL, object_key TEXT NOT NULL, actor_id TEXT NOT NULL, accessed_at TIMESTAMPTZ NOT NULL DEFAULT now());
INSERT INTO schema_migrations (version) VALUES ('005') ON CONFLICT DO NOTHING;
