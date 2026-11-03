# EduSphere Safety — judge's guide

**A school camera event should lead to a reviewable staff response.** EduSphere
Safety connects a video event pipeline to tenant-scoped incident management,
evidence review, local Meta Llama briefings, and a recorded human response.

Built during the Meta Global AI Developer Hackathon, October 29–November 3, 2026.
Theme: **Build for Your World**. Application code: MIT. This is a prototype with
working components and explicit simulation boundaries.

## The problem and who benefits

Schools can have cameras while still lacking a consistent process for reviewing
events, contacting staff, assigning responsibility, and recording what happened
after an alert. Footage alone does not establish who reviewed an event or whether
anyone responded. The intended users are school safety officers, campus managers,
and other authorized staff working under their school's established procedures.

EduSphere adds an incident workflow around the cameras: configured rules produce
events, staff review the available evidence, and a versioned state machine records
acknowledgement, investigation and resolution. Notification and escalation services
support that workflow. The aim is to reduce manual handoff and make responsibility
visible. No claimed reduction in harm, response time, or false alarms has been
measured in a school deployment yet.

## What the three-minute demonstration proves

1. **A real application is running.** The Next.js console uses the actual .NET API
   and PostgreSQL repositories. The recording signs in through the development
   login and opens real data, rather than a static mockup.
2. **Rules are configurable.** Staff can edit a normalized polygon, a confidence
   threshold, persistence and cooldown settings, then publish a numbered bundle.
   Schedules are part of the rule document. The demonstration uses a closed-window
   schedule so the scripted test can run at any time.
3. **The edge pipeline evaluates an event.** The project's simulator generates
   60 frames of a person-shaped rectangle crossing a restricted corridor zone.
   FFmpeg decodes the resulting MP4. Manifest-replayed detections pass through the
   actual tracker and rule engine. The run emits one event and queues it in the
   persistent outbox.
4. **Ingestion is idempotent.** An explicit demo handoff script submits the
   generated event to the real device-authenticated API, then retries it. Both
   requests return the same incident ID. The script adapts the edge outbox's
   slash-separated ID to the cloud evidence service's 64-character identifier
   contract using SHA-256.
5. **Evidence is authorized and reviewable.** The harness uploads the supplied
   synthetic clip and snapshot using evidence manifests and hash-checked upload.
   Staff open the clip from the incident through a short-lived mediated link.
   Playback access is logged. The web proxy was checked to return bytes identical
   to the original uploaded MP4.
6. **Meta Llama runs inside the staff workflow.** The officer requests a briefing
   for the actual incident. A local Llama 3.2 model returns a structured summary,
   review checklist and explicit unknowns. The video shortens the middle of the
   inference wait and labels that edit; it makes no inference-latency claim.
7. **Staff close the loop.** The officer acknowledges, adds an investigation note,
   investigates and resolves the incident. The API checks the current version;
   activity records preserve the actor and notes. Acknowledgement cancels pending
   escalation.
8. **Operations are visible.** The console exposes notification configuration,
   evidence access history, device heartbeat/configuration state and camera test
   status. A camera without a connectivity test is explicitly shown as unmonitored.

## Why Meta's model is part of the solution

The model turns compact, technical event metadata into a staff-facing draft and
review checklist. This is deliberately separate from the deterministic event rules
and the incident state machine. An LLM can help explain a record; it should not be
the authority that decides whether an incident occurred or who is responsible.

The running integration uses **Meta Llama 3.2 3B**, served locally through Ollama.
The server retrieves the incident through the tenant-authorized API **before**
calling the model. It supplies only event type, current state, campus/camera
identifiers, version and whether an evidence reference exists. No images or video
are sent to the model. The model endpoint and model name are server configuration,
not fields controlled by a browser request.

The route constrains the response to a JSON schema, validates its shape with Zod,
and returns an unavailable state rather than a fabricated briefing if inference
fails. Its prompt distinguishes a rule event from confirmed unauthorized access
and explicitly preserves unknown visual context and intent. The UI labels the
result as an AI-generated draft and warns when the incident version changes.
Schema validation does not prove factual accuracy; staff must check the draft.
The model has no tool that changes state, contacts anyone or operates a camera.

Local open-weight inference gives this prototype a deployment option where event
metadata stays on the host running the model. This is a property of the recorded
local configuration, not a certification about every future deployment. The
existing development history records Muse Code as commit author; the runtime Meta
model feature is directly demonstrated with Llama. We do not claim use of Muse
Spark, SAM, partner inference services or other tools that were not exercised.

## Engineering depth

- **Edge processing:** FFmpeg frame ingestion, checksum-verified ONNX adapter,
  preprocessing, non-maximum suppression, tracked identities, normalized geometry,
  persistence, cooldowns, schedules, health-event handling, encrypted evidence
  buffer and persistent event spool. The replay demo exercises the decoder,
  tracker, rules and outbox, while the real detector's field accuracy remains
  unmeasured.
- **Cloud and data:** .NET 10 API, application commands and domain state machine,
  PostgreSQL repositories and additive migrations, tenant-scoped queries and
  membership checks, separate device credentials, optimistic concurrency,
  idempotent event ingestion and a notification outbox.
- **Evidence lifecycle:** manifest size/hash checks, file and S3-compatible storage
  adapters, short-lived playback links, access logs and retention jobs. Incident
  metadata is retained when expired media is removed.
- **Operations:** background dispatch, escalation schedules and cancellation,
  delivery-attempt ledger, local SMTP pickup support, device heartbeat and stale
  configuration visibility, plus operator and rollout documentation.
- **Web and model boundary:** httpOnly session cookies, same-origin API forwarding,
  actual binary evidence delivery, tenant checks before inference, structured local
  Llama output and staff-controlled decisions.

## Validation and limitations

On November 3, the existing .NET solution built with **zero warnings and zero
errors**. All **509 existing tests passed**, including PostgreSQL integration
tests, with no failures or skips. The web type check, lint and isolated production
build pass. Additional live checks exercise denied anonymous requests, denied
cross-tenant and nonmember briefing requests, byte-identical evidence playback,
real local Llama output and the UI's persisted response workflow. Details are in
[VALIDATION.md](VALIDATION.md).

The prototype does **not** yet prove real-camera detector accuracy, false alarms
per camera-hour, inference throughput or end-to-end field latency. The current edge
host does not automatically drain its outbox into the cloud; that connection is
explicitly supplied by the demo harness. Its buffered clip payload is still a
descriptor, so the demo uploads the simulator's source MP4 rather than claiming
automatic live-camera clip extraction. Email is demonstrated through local pickup,
not external provider delivery. Production web SSO and a controlled school pilot
remain next steps. A local CCTV discovery attempt did not return an ONVIF response;
no physical camera is represented as connected in the recorded synthetic demo.

This is assistive incident software. It does not identify people, infer intent,
perform facial recognition, diagnose danger or replace supervision.

## What was created during the hackathon

The repository's first commit is dated **October 29, 2026**. The history tracks the
foundation, edge pipeline, device lifecycle, camera configuration, versioned rules,
evidence service, worker escalation/retention and web console during October 29–30,
followed by detector input/provenance refinements. November 3 adds the runtime
Llama briefing, corrects web/API integration defects, verifies the live flow and
produces the submission assets. Those contributions are visible in the public Git
history. Third-party model weights, libraries and runtimes are identified as
dependencies rather than original work.

## Next practical milestone

Connect an authorized camera, evaluate consented annotated footage, measure
event-level accuracy and latency, complete automatic edge delivery and real clip
extraction, integrate production identity, and run a supervised pilot with staff.
Those measurements should determine deployment limits before any production or
safety-performance claim.
