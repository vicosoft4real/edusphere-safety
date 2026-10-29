# EduSphere Safety — Approved Design Spec

Date: 2026-10-29. Branch: `feature/edusphere-safety-design` (based on `develop`).
Status: APPROVED design (§1–§6). Implementation plan not yet written.
Tracking issue: not published — no GitHub remote configured.

## Decisions locked during brainstorming

- External channel: email first (one configured SMTP adapter) + in-app; other channels later behind adapters.
- OIDC: genuine OIDC boundary (issuer/audience/`tid` claim validation); local stub provider for dev only, never a trusted header.
- Edge: x86 CPU appliance sized on the simulator; GPU only if benchmarks justify it.
- Pool sessions: manual session calendar in UI for the pilot.
- Evidence storage: S3-compatible primary (MinIO local); Cloudinary optional later adapter, not a replacement.
- Local simulation: full loop (simulator → edge → API/worker → web) runs in compose without cloud or cameras.

## 1. Scope

In scope: zone intrusion outside hours (A), directed boundary crossing (B), dwell/loitering (C), pool-zone entry outside authorised session (D), camera/edge health events (E); incident lifecycle New → Acknowledged → Investigating → Resolved / Dismissed with reason; assignment, notes, audit, ETag concurrency, paginated history, false-alarm feedback, escalation; snapshot + clip attached asynchronously, never gating incident creation or first notification.

Out of scope (not built, not claimed): face recognition, student identification, emotion detection, disciplinary automation, general anomaly detection, drowning detection, parent notification by default, live remote viewing, LLM video summaries gating alerts. Unaccompanied-child-near-pool stays a separately gated research flag; ordinary person detection is never labelled child/supervision recognition.

Assistive-system disclaimer (UI + docs): detection and notification cannot guarantee prevention of injury and do not replace supervision or physical controls.

## 2. Architecture (§1 approved)

Solution `EduSphere.Safety`: `Shared` (Result/error primitives, DTOs, pagination envelope), `Core` (entities, value objects, invariants, domain interfaces; no IO), `Application` (custom mediator `ICommand<T>`/`IQuery<T>` commands/queries/handlers, FluentValidation), `Infrastructure` (Dapper + Npgsql, SQL migrations, outbox, S3 storage port + adapters, email adapter), `Api` (endpoint modules, OIDC JWT, SignalR, OpenAPI, composition root), `Worker` (leased jobs: outbox dispatch, escalation sweeps, retention), `Edge` (.NET worker: RTSP/ONVIF ingest, FFmpeg, ONNX detector, tracker, rule engine, encrypted buffer, offline spool). Web: Next.js App Router + TS + Tailwind + TanStack Query + RHF/Zod + generated client. Compose: api, worker, web, Postgres, Redis, MinIO, edge, simulator. No MediatR, no EF Core; Serilog + OpenTelemetry; inward dependencies only.

## 3. Tenancy, trust, hardening (§1 approved)

Zones: school LAN, edge→cloud outbound TLS only, cloud control plane, browser. No inbound school ports, no camera port forwarding, cloud never dials LAN. Tenant from verified OIDC claims + `tenant_members`; device auth separate (per-device identity, 15-min single-use enrolment, rotation, revocation). Session cookies (httpOnly, SameSite=Lax); no bearer tokens or camera credentials in the browser. Camera credentials live on the edge, masked in logs. All tenant rows carry `tenant_id` with composite FKs; RLS defence-in-depth only (`SET LOCAL app.tenant`, non-bypass role, jobs set it per item). Evidence: private bucket, unguessable keys, presigned GET ≤15 min after reauthorisation, every mint/view audited. FFmpeg: argv arrays only, restricted protocol allowlist (no `file`/`concat`), metadata-endpoint/loopback/redirect validation, RTSP hosts checked against explicit local policy.

## 4. Event semantics (§2 approved)

Zone entry keyed on tracked foot point (box bottom-center) with entry/exit hysteresis and tracking-loss grace period. Boundary crossing needs sustained directed foot-point crossing across frames. Dwell accumulates continuous presence; resets on exit or grace expiry. Evaluation uses detection timestamps; ingestion/notification times stored separately. Schedules use the school IANA timezone (overnight + DST handled); holidays, exceptions, authorised pool sessions, cooldowns, expiring pauses suppress/delay. Each event persists rule + model versions. Confidence is a signal, never a danger probability.

## 5. Data and API (§3 approved)

Tables: tenants, campuses, devices/identities, cameras, zones/rule versions, schedules/sessions, events, incidents + activity/acknowledgements, evidence manifests, notification policies/delivery attempts, outbox, jobs, heartbeats, audit. UTC + schedule timezone; JSONB for versioned metadata, relational columns for constraints. API: enrolment, heartbeat/config versions, idempotent event ingest (stable edge IDs), incident state machine with ETags, presigned evidence after reauthorisation, paginated history, escalation policies, retention settings, authenticated SignalR updates.

## 6. Edge pipeline (§4 approved)

Simulator RTSP (synthetic/consented footage) drives the real ingest: FFmpeg decode, pinned ONNX person detector (checksum + version; Apache-2.0 candidates YOLOX-S / NanoDet-Plus benchmarked on x86 CPU before freezing; no AGPL weights), tracker for stable IDs, deterministic rule engine on versioned config, bounded encrypted rolling buffer + offline spool with documented eviction. Events carry stable edge IDs; metadata, snapshot, and clip upload asynchronously with backoff/jitter; cloud ingest idempotent. Health events: offline, frozen feed, analysis failure. Offline schools are never shown as monitored; delayed syncs labelled delayed.

## 7. Reliability (§5 approved)

Incident creation + first notification never wait for upload. Atomic cloud persist (event + incident + outbox, one transaction); at-least-once, idempotent IDs, durable retries, dead-letter inspection. Delivery states: accepted / delivered / failed / unknown; unconfigured channels never reported operational. Escalation on durable schedules, ack rechecked before firing, ack/escalation race handled. No exactly-once promise on external sends.

## 8. Deployment + pilot (§6 approved)

Local: compose runs api, worker, web, Postgres, Redis, MinIO, edge, simulator. Cloud: Render stateless services + managed Postgres/Redis + S3-compatible storage; inference stays on edge unless benchmarks demand otherwise. Rollout: migrations + compatible API → storage/worker config → feature-disabled deploy → edge enrolment → controlled camera/rules → staged validation → pilot enablement → monitoring/rollback checks. Pilot acceptance (targets, measured later): real-pipeline incident from staged event, email received, evidence review + ack, escalation on unacknowledged, cross-tenant refusal, duplicate-free offline recovery, visible camera failure, full retention deletion, Chrome-validated flow. Never production-ready on simulation alone.

## 9. Open items needing school/operator input

Real camera + credentials, SMTP provider credentials, OIDC production provider choice, annotated/consented test footage, pool timetable source confirmation (manual calendar assumed), hardware sizing validation from measured resolution/codec/sample-rate/latency, GitHub remote for tracking issue + PR CI, data-ownership/notice/retention jurisdiction review (no legal claims made).

## 10. Build order

Vertical slice first (simulated RTSP → detection → zone rule → cloud incident → email → ack), then harden: tenancy negatives, outbox/escalation recovery, DST/overnight schedules, retention, diagnostics, Chrome validation.

