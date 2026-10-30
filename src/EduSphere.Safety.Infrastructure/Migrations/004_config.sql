-- 004_config.sql — versioned rule bundles, notification policies, config counter.
-- NOTE: each statement is on a single physical line so the file also applies
-- cleanly via `postgres --single`, which executes input line by line.
CREATE TABLE IF NOT EXISTS rule_bundles (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, version INTEGER NOT NULL, document JSONB NOT NULL, created_by TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, version));
CREATE TABLE IF NOT EXISTS notification_policies (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, id TEXT NOT NULL, levels JSONB NOT NULL, recipients JSONB NOT NULL, created_by TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, id));
CREATE TABLE IF NOT EXISTS tenant_config_state (tenant_id TEXT PRIMARY KEY REFERENCES tenants (id) ON DELETE CASCADE, bundle_version INTEGER NOT NULL DEFAULT 0);
INSERT INTO schema_migrations (version) VALUES ('004') ON CONFLICT DO NOTHING;
