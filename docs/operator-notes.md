# EduSphere Safety — operator notes

Local/dev and pilot deployment notes. No production secret values live here;
configure secrets via environment variables or the deployment secret store.

## Environment variables

Use `__` (double underscore) as the section separator, e.g.
`Storage__FileRoot`. Both the Api and the Worker read `Postgres` and
`Storage`; each service additionally reads its own sections below.

| Key | Service | Required | Default | Source / rotation | Validation |
| --- | --- | --- | --- | --- | --- |
| `Postgres__ConnectionString` | Api, Worker | Yes | `""` (fails) | Deployment secret store; rotate per DB policy | Fail-fast: Npgsql must connect |
| `Auth__Issuer` | Api | Prod yes | `""` | Genuine OIDC issuer for EduSphere | Fail-fast when `DevStubEnabled=false` |
| `Auth__Audience` | Api | Prod yes | `""` | EduSphere audience | Fail-fast when `DevStubEnabled=false` |
| `Auth__IssuerSigningKey` | Api | Prod yes | `""` | OIDC signing key | Fail-fast when `DevStubEnabled=false` |
| `Auth__Authority` | Api | Prod yes | `""` | OIDC authority | Fail-fast when `DevStubEnabled=false` |
| `Auth__DevStubEnabled` | Api | No | `false` | Local testing only; never enable in production | Enables `POST /v1/auth/dev-token` (HS256 test tokens) |
| `Storage__Backend` | Api, Worker | No | `File` | Deployment choice: `File` (dev/single-node) or `S3` | Must be `File` or `S3` (`StorageOptions.Validate`, fail-fast) |
| `Storage__FileRoot` | Api, Worker | File backend yes | `""` (fails) | Local path (dev) or mounted volume | Required when `Backend=File` |
| `Storage__TokenSecret` | Api, Worker | File backend yes | `""` (fails) | Random ≥32 bytes (UTF-8); rotate = invalidate outstanding presigned URLs | Must be ≥32 bytes (`StorageOptions.Validate`, fail-fast) |
| `Storage__Endpoint` | Api, Worker | S3 backend yes | `""` (fails) | S3-compatible endpoint (e.g. MinIO URL) | Required when `Backend=S3` |
| `Storage__Bucket` | Api, Worker | S3 backend yes | `""` (fails) | Bucket name | Required when `Backend=S3` |
| `Storage__Region` | Api, Worker | No | `us-east-1` | S3 region (SigV4 scope) | Used in hand-rolled signature |
| `Storage__AccessKey` | Api, Worker | S3 backend yes | `""` (fails) | Object-storage credential | Required when `Backend=S3`; rotate per storage policy |
| `Storage__SecretKey` | Api, Worker | S3 backend yes | `""` (fails) | Object-storage credential | Required when `Backend=S3`; rotate per storage policy |
| `Storage__ForcePathStyle` | Api, Worker | No | `true` | `true` for MinIO | SigV4 URL style |
| `Email__PickupDirectory` | Worker | Either pickup or SMTP | `""` | Local dir (dev default); empty means SMTP | Exactly one of `PickupDirectory`/`Host` must be set (`WorkerStartupValidator`, fail-fast) |
| `Email__Host` / `Email__Port` | Worker | Either pickup or SMTP | `""` / `25` | SMTP relay | Exactly one transport must be set |
| `Email__From` | Worker | No | `safety@localhost` | Sender address shown to staff | Used as SMTP envelope from |
| `Notification__To` | Worker | Yes | [] (fails) | Pilot recipient list (static; per-campus routing is a later plan) | Non-empty required (`WorkerStartupValidator`, fail-fast) |
| `Retention__ClipDays` | Worker | No | `30` | Deployment choice | Clamped to ≥1; per-tenant retention is a later plan |
| `Retention__SnapshotDays` | Worker | No | `90` | Deployment choice | Clamped to ≥1 |
| `Retention__AuditDays` | Worker | No | `365` | Deployment choice | Clamped to ≥1 |
| `Outbox__IntervalSeconds` | Worker | No | `5` | Deployment tuning | Dispatch loop cadence |
| `Outbox__BatchSize` | Worker | No | `25` | Deployment tuning | Claim batch size |
| `Outbox__LeaseSeconds` | Worker | No | `60` | Deployment tuning | Lease for claimed rows |

## Enrolment TTLs

- Enrolment code: single-use, 15 minutes (`EnrolmentPolicy.CodeTtlMinutes`),
  shown once, stored as SHA256 hex.
- Device secret: 256-bit crypto-random Base64URL, stored as SHA256 hex,
  returned in plaintext exactly once at enrol/rotate. Rotation is immediate.
- Evidence PUT URL: 15 minutes; playback GET URL: 5 minutes
  (`EvidencePolicy.PutUrlTtlMinutes` / `GetUrlTtlMinutes`).
- The manifest `expires_at` column is the **upload window**, not media
  retention. Media retention is computed by the Worker from `uploaded_at`
  plus `Retention:ClipDays` / `Retention:SnapshotDays`.

## Retention job

`RetentionJob` (Worker, daily, Postgres advisory lease for single-flight;
runs unleashed with a log line when no connection string is configured):

1. Uploaded manifests older than the kind cutoff → `DeleteAsync` object
   bytes → delete the manifest row (bounded 500-row passes).
2. Never-uploaded manifests past their upload window → same sweep.
3. Access-log rows older than `Retention:AuditDays` → purged (count logged).
4. Incident and incident-activity rows are NEVER deleted (metadata outlives
   media).

Staff can inspect recent access via `GET /v1/evidence/access-log`
(OIDC `tid` + membership; tenant-scoped).

## Vertical slice (recorded 2026-10-30, in-sandbox direct-drive probe)

`simulated edge event → POST /v1/ingest/events → incident (New) +
escalation scheduled → OutboxDispatcher → email in `Email:PickupDirectory`
→ PATCH ack (If-Match version) → EscalationDispatcher sends nothing,
schedule gone → manifest → complete → playback 302 + access-log row →
`GET /v1/evidence/access-log` shows it → `RetentionJob.RunOnceAsync`
deletes the expired object + row, the incident still reads back, the fresh
manifest is kept.` Full Postgres/SMTP/MinIO variants run on connected CI.
