# Pilot rollout, operations, and acceptance

## Rollout order

1. **Database.** Apply `src/EduSphere.Safety.Infrastructure/Migrations/001`–`006`
   in order (`psql -f` or `postgres --single`; files are idempotent
   `IF NOT EXISTS` with single-line statements, tracked in
   `schema_migrations`). All additive — safe under the running build.
2. **Object storage + Worker config.** Create the bucket (MinIO/S3), set
   `Storage:*`, `Email:*`, `Notification:To`, `Retention:*` per
   [operator notes](operator-notes.md).
3. **Deploy cloud.** `docker compose up -d postgres minio api worker web`
   (builds: `docker/api.Dockerfile`, `docker/worker.Dockerfile`,
   `docker/web.Dockerfile`). Confirm Api `/health` (if exposed) and Worker
   startup validation passes (fails fast on bad mail/recipient config).
4. **Edge enrolment.** Create an enrolment code in the console
   (Devices → Create code, 15-min single-use), enter it on the appliance,
   confirm the device appears with a fresh heartbeat.
5. **Cameras + rules.** Add cameras (manual RTSP or ONVIF), check the edge
   connectivity report per camera, draw zones, publish bundle v1, confirm the
   device pulls it (config no longer stale on Devices).
6. **Staged validation.** Walk a test subject through a zone out of hours;
   expect incident → email → ack → escalation cancelled. Check evidence
   playback and the access-log entry.
7. **Pilot enablement.** Configure real recipients + escalation policy;
   brief staff that this is assistive (see disclaimer below).
8. **Monitoring + rollback.** Watch Worker logs (outbox, escalation,
   retention counts), device heartbeat ages, and dead-letter endpoint.

## Rollback

- Cloud: redeploy the previous image tags; migrations are additive so old
  binaries keep working. Do not roll back migrations.
- Edge: keep the previous agent image tag; config pull is versioned, and a
  rule change discards only in-progress episodes.
- Evidence objects are immutable once uploaded; retention deletes only
  expired media, never incident records.

## Backup / restore

- PostgreSQL: `pg_dump` on the standard schedule; media lives in object
  storage — back up the bucket with versioning/lifecycle per school policy.
  Note: backups extend effective retention; align bucket lifecycle with
  `Retention:*` and the school's data agreement.
- Restore: restore DB then bucket; manifest rows reference object keys, so a
  DB-only restore shows evidence as missing until the bucket matches.

## Dependency inventory (runtime)

- .NET 10 ASP.NET Core / Worker; PostgreSQL 17; S3-compatible storage
  (MinIO for local); SMTP relay or pickup dir; Next.js 15 console;
  FFmpeg on the edge appliance; ONNX Runtime + detector weights (pinned,
  benchmarked — see `docs/edge-benchmark.md`).
- No Kafka, Kubernetes, vector DB, or extra microservices. No Redis yet:
  single Api replica assumed; multi-replica SignalR needs a backplane (later).

## Cost sketch (explicit assumptions)

Assumes one school, ≤16 cameras at 5 fps sub-streams, edge appliance owned
by the school, cloud on a small Render/VM tier: Postgres managed (~1 vCPU),
object storage for 30-day clips (~500 GB with motion-gated capture),
egress dominated by staff playback (not 24/7 streaming — inference stays on
the edge). Measure before committing: footage retention × bitrate sets the
storage bill, not camera count alone.

## Assistance disclaimer (show in staff briefings)

Detection and notification cannot guarantee prevention of injury and do not
replace supervision and physical safety controls. Confidence scores are
thresholds, not danger probabilities. Pool supervision, face recognition,
and drowning detection are explicitly out of scope.

## Controlled-pilot acceptance (Definition of Done) — status

| Criterion | Status |
| --- | --- |
| Real camera connected; staged event → incident via real pipeline | Pending field (simulation only) |
| External notification reaches its destination | Proven in-slice (pickup-dir SMTP path); provider delivery pending creds |
| Staff review evidence + acknowledge | Proven in-slice (probe) / UI built, Chrome walkthrough pending |
| Escalation on unacknowledged | Proven (probe L0–L2 + ledger) |
| Cross-tenant isolation | Proven (negative tests) |
| Offline recovery without duplicates | Proven (idempotent ingest + delayed sync) |
| Camera failure visible | Proven (heartbeat/test-age surfacing) |
| Retention removes expired evidence | Proven (probe + job) |
| Measured performance documented | Partial (`docs/edge-benchmark.md`); field footage pending |
| CI green on latest commit | Pending connected CI run |
| Sandbox-runnable unit suite (direct reflection runner — vstest needs TCP, TestServer/CreateBuilder hang here) | **Measured 2026-10-30: 449 pass, 0 product failures.** 58 factory/Testcontainers tests skipped (CI-gated); 1 fail is SkiaSharp native lib absent in sandbox (`Pipeline_WalkerMp4` decode test; passes where native deps exist). Covers edge pipeline, domain, handlers, dispatchers, retention, rate-limit policy. |
| Full flow validated in Chrome | Pending (no browser automation in this environment) |
| Deployment + operator docs | This file + operator notes + compose |

Nothing above is claimed production-ready on simulation alone.
