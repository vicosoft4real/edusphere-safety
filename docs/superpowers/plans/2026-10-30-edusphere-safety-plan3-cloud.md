# Plan 3: Cloud Ingestion, Evidence, and Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Durable edge-to-cloud ingestion, versioned configuration distribution, S3-compatible evidence with authorized access, recipient resolution with durable escalation, and retention — all multi-tenant, TDD, no new NuGet packages.

**Architecture:** Edge authenticates per-device (`X-Device-Key`, existing `DeviceAuth`); staff use OIDC `tid` claim + membership (existing `ResolveTenantAsync`). New Dapper repositories behind new store interfaces; migrations `002`–`006` (single-line statements, `IF NOT EXISTS`, `postgres --single` compatible, same as `001_foundation.sql`). Outbox + leased-worker pattern reused for escalation and retention. Evidence upload goes direct-to-S3 via presigned PUT; download is API-mediated (authz + audit).

**Tech Stack:** .NET 10, Npgsql/Dapper, FluentValidation, VicoMediator-style commands, raw-HttpClient S3 REST + hand-rolled SigV4 (BCL `HMACSHA256` only — no AWS SDK: not in the offline cache), filesystem-backed dev store, SMTP/pickup email (existing `IEmailSender`).

**Sandbox execution notes (same as Plans 1–2):** socket `bind()` blocked; `dotnet test` on the test project cannot restore offline — prove via per-project `dotnet build` (`/p:UseAppHost=false` for Exe projects), Roslyn `csc` compile of new test files, `/tmp` behavior probes with `dotnet exec`, WebApplicationFactory tests compile-clean and run on connected CI; migrations proven via `initdb` + `postgres --single`; MinIO/Docker/Testcontainers are CI-only.

---

## File structure

- Create: `src/EduSphere.Safety.Infrastructure/Migrations/002_devices.sql` (enrolment codes; device heartbeat/version columns).
- Create: `src/EduSphere.Safety.Infrastructure/Migrations/003_cameras.sql` (connection JSONB, last-test columns on `cameras`).
- Create: `src/EduSphere.Safety.Infrastructure/Migrations/004_config.sql` (`rule_bundles`, `notification_policies`, tenant config version counter).
- Create: `src/EduSphere.Safety.Infrastructure/Migrations/005_evidence.sql` (`evidence_manifests`, `evidence_access_log`).
- Create: `src/EduSphere.Safety.Infrastructure/Migrations/006_notifications.sql` (`delivery_attempts`, `escalation_schedules`).
- Create: `src/EduSphere.Safety.Application/EnrolDevice.cs` (`CreateEnrolCodeCommand` staff-side, `EnrolDeviceCommand` device-side, validators, handlers).
- Modify: `src/EduSphere.Safety.Application/IDeviceStore.cs` (add `CreateEnrolCodeAsync`, `ConsumeEnrolCodeAsync`, `RegisterDeviceAsync`, `RotateSecretAsync`, `SetRevokedAsync`, `ListAsync`, `RecordHeartbeatAsync`, `GetConfigVersionsAsync`).
- Create: `src/EduSphere.Safety.Application/CameraOnboarding.cs` (camera CRUD commands + `ReportCameraTestCommand`; URL-shape validation delegates to `EduSphere.Safety.Edge.FfmpegFrameSource.ValidateSourceUrl`).
- Create: `src/EduSphere.Safety.Application/ICameraStore.cs` (CRUD + test-report + tenant-scoped list).
- Create: `src/EduSphere.Safety.Application/ConfigDistribution.cs` (`PublishBundleCommand`, `SetPolicyCommand`, `GetActiveConfigQuery` returning versioned snapshot + ETag).
- Create: `src/EduSphere.Safety.Application/IConfigStore.cs` (bundle/policy CRUD + atomic tenant version bump).
- Create: `src/EduSphere.Safety.Application/Evidence.cs` (`RegisterManifestCommand`, `MintUploadUrlCommand`, policy: key layout `tenant/campus/camera/date/edgeEventId.ext`, never secret).
- Create: `src/EduSphere.Safety.Application/IEvidenceStore.cs` (storage seam) + `src/EduSphere.Safety.Infrastructure/S3EvidenceStore.cs` (SigV4, path-style) + `src/EduSphere.Safety.Infrastructure/FileEvidenceStore.cs` (dev).
- Create: `src/EduSphere.Safety.Application/Notifications.cs` (`ResolveRecipients` pure function, `ScheduleEscalationCommand`, `RecordDeliveryAttemptCommand`; states accepted/delivered/failed/unknown).
- Create: `src/EduSphere.Safety.Application/INotificationStore.cs` (attempts append/list, escalation schedule CRUD + leased claim).
- Create: `src/EduSphere.Safety.Api/DeviceEndpoints.cs` (enrol-code create, enrol, rotate, revoke, list, heartbeat, config pull).
- Create: `src/EduSphere.Safety.Api/CameraEndpoints.cs` (CRUD, test-report intake, diagnostics view).
- Create: `src/EduSphere.Safety.Api/ConfigEndpoints.cs` (bundle/policy CRUD staff-side).
- Create: `src/EduSphere.Safety.Api/EvidenceEndpoints.cs` (manifest intake, upload-URL mint, mediated download with audit).
- Create: `src/EduSphere.Safety.Worker/EscalationDispatcher.cs` (leased loop: recheck-ack → send → schedule-next).
- Create: `src/EduSphere.Safety.Worker/RetentionJob.cs` (leased daily: delete expired objects + rows).
- Test: `tests/EduSphere.Safety.Tests/Plan3*.cs` (`Plan3DeviceTests`, `Plan3CameraTests`, `Plan3ConfigTests`, `Plan3EvidenceTests`, `Plan3NotificationTests`, `Plan3ApiTests` via `SafetyApiFactory` fakes).

---
---

### Task 1: Device lifecycle — enrolment, rotation, revocation (TDD)

**Files:** Create `002_devices.sql`, `EnrolDevice.cs`; modify `IDeviceStore.cs`; implement in `DeviceStore.cs`; test `Plan3DeviceTests.cs` (+ `Plan3ApiTests.cs` enrol/rotate/revoke cases).

Migration `002_devices.sql` (append after the migrations-table line pattern of 001; every statement one line):

```sql
CREATE TABLE IF NOT EXISTS enrolment_codes (code_hash TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, campus_id TEXT NULL, expires_at TIMESTAMPTZ NOT NULL, used_at TIMESTAMPTZ NULL, created_by TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS ix_enrolment_codes_tenant ON enrolment_codes (tenant_id, expires_at);
ALTER TABLE devices ADD COLUMN IF NOT EXISTS campus_id TEXT NULL;
ALTER TABLE devices ADD COLUMN IF NOT EXISTS software_version TEXT NOT NULL DEFAULT '';
ALTER TABLE devices ADD COLUMN IF NOT EXISTS model_version TEXT NOT NULL DEFAULT '';
ALTER TABLE devices ADD COLUMN IF NOT EXISTS config_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE devices ADD COLUMN IF NOT EXISTS last_heartbeat_at TIMESTAMPTZ NULL;
```

Rules (normative): enrol code = 128-bit crypto-random, shown once, stored as SHA256 hex; TTL 15 minutes; single-use (`used_at` set in the SAME transaction that inserts the device — `UPDATE ... WHERE used_at IS NULL`, 0 rows = gone). Device secret = 256-bit crypto-random Base64URL, stored as SHA256 hex (existing `DeviceAuth.HashSecret`); returned in plaintext exactly once at enrol/rotate. Rotation is immediate (old secret dies in the same `UPDATE`); revocation sets `revoked=true` (existing 401 path enforces). Staff endpoints require OIDC `tid` + membership; enrol endpoint is unauthenticated-but-code-gated with rate limiting noted as CI-hardening.

- [x] **Step 1: Write failing tests** — code expiry enforced; double-consume of one code fails; rotated secret invalidates the old one; revoked device gets 401 on ingest (Api-level, fake stores).
- [x] **Step 2: Run to verify they fail** (compile fails; `dotnet test` CI-only — record, verify via compile + probe pattern below).
- [x] **Step 3: Implement** commands/handlers/Dapper SQL per above; endpoints `POST /v1/devices/enrol-codes`, `POST /v1/devices/enrol`, `POST /v1/devices/rotate` (device auth), `POST /v1/devices/revoke`, `GET /v1/devices`.
- [x] **Step 4: Verify** — Api + Application + Infrastructure compile 0/0; migration applies via `postgres --single`; probe exercises enrol → ingest → rotate → old-secret-401 → revoke → 401.
- [x] **Step 5: Commit.**

### Task 2: Camera onboarding APIs + edge-reported connectivity (TDD)

**Files:** Create `003_cameras.sql`, `CameraOnboarding.cs`, `ICameraStore.cs`, `CameraEndpoints.cs`; Api references Edge project for `FfmpegFrameSource.ValidateSourceUrl` (dependency-free classlib; no new package).

Migration:

```sql
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS connection JSONB NOT NULL DEFAULT '{}';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS last_test_ok BOOLEAN NULL;
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS last_test_detail TEXT NOT NULL DEFAULT '';
ALTER TABLE cameras ADD COLUMN IF NOT EXISTS last_test_at TIMESTAMPTZ NULL;
```

Rules: `connection` holds ONLY non-secret descriptors (`{mode, host, port, path}` — never userinfo/password; validator rejects with `validation.failed` otherwise). Staff CRUD scoped `(tenant_id, id)`; `device_id` assignment must belong to the same tenant (FK already enforces). `POST /v1/cameras/{id}/test-report` is device-authed: camera must belong to the device tenant, else 404 (indistinguishable). Diagnostics `GET /v1/cameras` returns last-test fields; never emits credentials (there are none stored).

- [x] **Step 1: Write failing tests** — secret-bearing connection rejected; cross-tenant device assignment rejected; test-report from another tenant's device is 404; diagnostics contain no userinfo.
- [x] **Step 2: Run to verify they fail.**
- [x] **Step 3: Implement** store + endpoints.
- [x] **Step 4: Verify** — compile 0/0; migration applies; probe: create → bad-URL rejected → report ok → diagnostics show ok.
- [x] **Step 5: Commit.**

### Task 3: Versioned config — bundles, policies, edge pull (TDD)

**Files:** Create `004_config.sql`, `ConfigDistribution.cs`, `IConfigStore.cs`, `ConfigEndpoints.cs` (staff) + device pull in `DeviceEndpoints.cs`.

Migration:

```sql
CREATE TABLE IF NOT EXISTS rule_bundles (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, version INTEGER NOT NULL, document JSONB NOT NULL, created_by TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, version));
CREATE TABLE IF NOT EXISTS notification_policies (tenant_id TEXT NOT NULL REFERENCES tenants (id) ON DELETE CASCADE, id TEXT NOT NULL, levels JSONB NOT NULL, recipients JSONB NOT NULL, created_by TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, id));
CREATE TABLE IF NOT EXISTS tenant_config_state (tenant_id TEXT PRIMARY KEY REFERENCES tenants (id) ON DELETE CASCADE, bundle_version INTEGER NOT NULL DEFAULT 0);
```

Rules: publish validates the bundle by loading it through the edge `RuleBundleLoader` semantics (valid shape; unknown members ignored per Task 5 test) and bumps `tenant_config_state.bundle_version` in the SAME transaction (insert bundle + upsert counter). Edge pull `GET /v1/devices/config` (device auth) serves the active bundle + policy snapshot with `ETag: W/"{bundleVersion}"`; `If-None-Match` match → 304. Heartbeat response (Task 6) compares device `config_version` to this counter for the stale flag.

- [x] **Step 1: Write failing tests** — malformed bundle rejected; publish bumps version exactly once; pull with matching ETag is 304; pull content equals published snapshot. (`Plan3ConfigTests`: dangling/duplicate/malformed RED first; `Plan3ConfigApiTests`: HTTP-level ETag/304 cycle for CI.)
- [x] **Step 2: Run to verify they fail.** — RED observed: dangling-schedule test failed (test bug fixed: replacement hit both declaration and reference); new API tests fail in-sandbox only on the WebApplicationFactory `deps.json` gate (same as all ApiTests; logic proven via direct-drive probe).
- [x] **Step 3: Implement.** — `ConfigDistribution` (cloud shape validation; semantics edge-side), `ConfigStore` (seed + `FOR UPDATE` + insert + counter in one tx), staff `ConfigEndpoints`, edge pull `GET /v1/devices/config` with `ETag: W/"{version}"` + 304, migration `004_config.sql`.
- [x] **Step 4: Verify** — src builds 0 warnings 0 errors; tests Roslyn-compile clean; direct-drive probe 41/41 PASS incl. publish → pull full (`W/"1"`) → pull 304 → publish → pull new ETag (`W/"2"`), malformed/dangling/duplicate 400s, 403/401/404s, policy round-trip; migration chain verified statically (001 owns `tenants` + `schema_migrations`; 004 single-line statements); live `postgres --single` apply is CI-gated (PG binaries refuse this sandbox: unknown uid 501). Full suite 425 passed; 44 failed = all env-gated (WebApplicationFactory/Testcontainers), 0 product failures. Edge control 364/364.
- [ ] **Step 5: Commit.**

### Task 4: Evidence — S3 seam, manifests, presigned URLs (TDD)

**Files:** Create `005_evidence.sql`, `Evidence.cs`, `IEvidenceStore.cs`, `S3EvidenceStore.cs`, `FileEvidenceStore.cs`, `EvidenceEndpoints.cs`; test `Plan3EvidenceTests.cs`.

Migration:

```sql
CREATE TABLE IF NOT EXISTS evidence_manifests (tenant_id TEXT NOT NULL, edge_event_id TEXT NOT NULL, incident_id UUID NULL, object_key TEXT NOT NULL, kind TEXT NOT NULL CHECK (kind IN ('clip','snapshot')), bytes BIGINT NOT NULL DEFAULT 0, sha256 TEXT NOT NULL DEFAULT '', uploaded_at TIMESTAMPTZ NULL, expires_at TIMESTAMPTZ NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), PRIMARY KEY (tenant_id, edge_event_id, kind), FOREIGN KEY (tenant_id, incident_id) REFERENCES incidents (tenant_id, id) ON DELETE SET NULL);
CREATE INDEX IF NOT EXISTS ix_evidence_expires ON evidence_manifests (expires_at) WHERE expires_at IS NOT NULL;
CREATE TABLE IF NOT EXISTS evidence_access_log (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id TEXT NOT NULL, manifest_tenant TEXT NOT NULL, object_key TEXT NOT NULL, actor_id TEXT NOT NULL, accessed_at TIMESTAMPTZ NOT NULL DEFAULT now());
```

Rules (normative): object key layout `{tenant}/{campus}/{camera}/{yyyy-MM-dd}/{edgeEventId}.{clip.mp4|jpg}` — tenant-scoped, non-guessable access ONLY via signature, never via key secrecy. Flow: edge `POST /v1/evidence/manifest` (device auth) registers intent → cloud returns presigned PUT URL (15-min expiry) → edge uploads direct → edge `POST /v1/evidence/manifest/complete` marks `uploaded_at`. Staff playback `GET /v1/evidence/{edgeEventId}/{kind}` (OIDC, tenant member, camera campus in scope) returns 302 to a presigned GET (5-min) AND writes `evidence_access_log` in the same request. `IEvidenceStore`: `MintPutUrlAsync`, `MintGetUrlAsync`, `DeleteAsync`, `ExistsAsync`. `S3EvidenceStore` signs SigV4 by hand (`HMACSHA256` chain, path-style for MinIO); `FileEvidenceStore` (dev default) stores under `Storage:FileRoot`, PUT URLs point at an Api upload endpoint (same auth), GET URLs at the mediated download. No SDK, no new packages. Incident creation NEVER waits for upload (Plan 1 already satisfies this).

- [ ] **Step 1: Write failing tests** — SigV4 test-vector (AWS documented `get-vanilla` style vector) matches byte-for-byte; manifest for another tenant's edgeEventId is 404; download writes an audit row; expired presigned URL rejected (clock skew note: 5-min grace documented).
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** seam + both stores + endpoints + config (`Storage:Endpoint/Bucket/Region/AccessKey/SecretKey/ForcePathStyle/FileRoot`, validated at startup, fail-fast like email).
- [ ] **Step 4: Verify** — compile 0/0; migration applies; probe against FileEvidenceStore: manifest → upload → complete → download (audit row present); SigV4 vector test green.
- [ ] **Step 5: Commit.**

### Task 5: Notifications — recipients, escalation, delivery states (TDD)

**Files:** Create `006_notifications.sql`, `Notifications.cs`, `INotificationStore.cs`; create `EscalationDispatcher.cs`; extend outbox payload handling in Worker `Program.cs`; test `Plan3NotificationTests.cs`.

Migration:

```sql
CREATE TABLE IF NOT EXISTS delivery_attempts (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id TEXT NOT NULL, incident_id UUID NOT NULL, channel TEXT NOT NULL DEFAULT 'email', level INTEGER NOT NULL DEFAULT 0, recipient TEXT NOT NULL, state TEXT NOT NULL CHECK (state IN ('accepted','delivered','failed','unknown')), detail TEXT NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL DEFAULT now(), FOREIGN KEY (tenant_id, incident_id) REFERENCES incidents (tenant_id, id) ON DELETE CASCADE);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_attempt_dedup ON delivery_attempts (tenant_id, incident_id, channel, level, recipient) WHERE state IN ('accepted','delivered');
CREATE TABLE IF NOT EXISTS escalation_schedules (tenant_id TEXT NOT NULL, incident_id UUID NOT NULL, level INTEGER NOT NULL, due_at TIMESTAMPTZ NOT NULL, locked_until TIMESTAMPTZ NULL, attempts INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (tenant_id, incident_id, level), FOREIGN KEY (tenant_id, incident_id) REFERENCES incidents (tenant_id, id) ON DELETE CASCADE);
CREATE INDEX IF NOT EXISTS ix_escalation_due ON escalation_schedules (due_at, locked_until);
```

Rules (normative): on `incident.created` the Api ALSO inserts a level-0 immediate send (existing outbox `incident.created` email stays the pilot default) and the FIRST escalation row (level 1, `due_at = detected + policy.levels[0].afterSeconds`). `ResolveRecipients(rule, policy)` is pure: rule recipients ∪ level recipients, deduped. `EscalationDispatcher` (leased claim like `OutboxDispatcher`, 60s lease): claim due → **recheck state in the same transaction** (`SELECT state ... FOR UPDATE`; if not `New` → delete schedule, no send — this closes the ack-vs-escalation race) → send via `IEmailSender` → append `delivery_attempts` (accepted on send success, failed on exception, unknown on cancellation) → insert next-level row or delete. Crash between send and next-row insert can duplicate one email: acceptable, documented (at-least-once; dedup index keeps the ledger clean). SMTP has no delivery callbacks: `delivered` state is reserved for future callback channels and NEVER reported for SMTP (never claim operational what is unconfigured). Policy with zero levels = no escalation (explicit, not an error).

- [ ] **Step 1: Write failing tests** — ack before due cancels silently (no send); send failure appends `failed` and reschedules with backoff; second level fires after its own delay; SMTP send never records `delivered`.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** — compile 0/0; migration applies; probe with pickup-directory email: create → level-1 due → ack → no send; create → no ack → two-level send sequence in pickup dir + attempts ledger.
- [ ] **Step 5: Commit.**

### Task 6: Heartbeat intake + stale-config flagging (TDD)

**Files:** Extend `DeviceEndpoints.cs` (`POST /v1/devices/heartbeat`), `IDeviceStore.RecordHeartbeatAsync`, device list DTO; test in `Plan3ApiTests.cs`.

Rules: device-authed heartbeat `{softwareVersion, modelVersion, configVersion, queueDepth, diskUsedBytes}` updates the `002` columns + `last_heartbeat_at` (single `UPDATE`). Response: `{serverConfigVersion, configStale: device < server, serverTimeUtc}` — the agent uses `serverTimeUtc` for clock-skew tracking (brief §7). Device list (staff) surfaces versions + `last_heartbeat_at` + stale flag; offline threshold is a presentation rule (device silent > N minutes = degraded, never "monitored"). Validation reuses the heartbeat shape rules (finite age ≥ 0 etc., same codes as edge `HeartbeatClient.Build`).

- [ ] **Step 1: Write failing tests** — heartbeat updates versions/seen-at; stale=true when behind; future configVersion is not stale; validation rejects non-finite age.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** — compile 0/0; probe: heartbeat → list shows versions → publish bundle → heartbeat stale=true.
- [ ] **Step 5: Commit.**

### Task 7: Retention, audit view, vertical-slice proof (TDD)

**Files:** Create `RetentionJob.cs` (Worker); add `GET /v1/evidence/access-log` (staff, tenant-scoped) to `EvidenceEndpoints.cs`; test `Plan3ApiTests.cs` retention cases.

Rules: retention periods are Worker options (`Retention:ClipDays/SnapshotDays/AuditDays`, defaults 30/90/365) per deployment; per-tenant configuration is a later plan and is documented as such. `RetentionJob` (leased, daily): for expired manifests → `IEvidenceStore.DeleteAsync` object → delete manifest row; NEVER deletes `incident`/`incident_activity` rows (metadata outlives media; `incident_id` FK is `ON DELETE SET NULL` the other way). Access-log rows older than audit retention are deleted; the job logs counts. Vertical slice (manual + recorded): simulator edge event → ingest → incident + email (pickup dir) → staff ack via PATCH → escalation cancelled; evidence manifest → upload → mediated download with audit row. Record commands + outputs in the task report.

- [ ] **Step 1: Write failing tests** — expired clip object deleted + row gone, incident row survives; unexpired untouched; access-log view is tenant-scoped.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** — compile 0/0; full vertical slice recorded; `docs/` operator notes updated (env vars: `Storage:*`, `Retention:*`, enrol TTL).
- [ ] **Step 5: Commit.**

---

## Plan 3 acceptance

Device enrol → ingest → rotate → revoke cycle proven; camera CRUD with secret-free descriptors; bundle publish → versioned edge pull (304 support); evidence manifest → direct upload → audited mediated download; unacknowledged incident escalates per policy with ack-race safety; expired media deleted with ledger intact; all new migrations apply on PG 14 via `postgres --single`; Api/Application/Infrastructure/Worker compile 0/0; new tests compile clean and pass on connected CI; no new packages; no accuracy claims; no production-secret values in repo.
