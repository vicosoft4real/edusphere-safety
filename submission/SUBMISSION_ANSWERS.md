# Cerebral Valley submission answers

Prepared against the actual Meta Global AI Developer Hackathon submission form on
November 3, 2026. Indie Developer is confirmed by the entrant. The completed form
awaits the entrant's publishing permission and final submission.

## Team name

EduSphere Safety

## Team members

Victor Oluwayemi is currently listed on the submission form. Add any other actual
contributors before submission; do not omit contributors.

## Track

**Indie Developer**, confirmed by the entrant.

## Problem statement

Build for Your World

## Project description

EduSphere Safety turns school camera events into an accountable staff response.
Schools can have footage without a consistent process for reviewing an event,
contacting the right staff member and recording what happened next.

Built during this hackathon, the prototype combines a .NET edge video pipeline,
scheduled zone rules, tenant-scoped incident management, evidence review, local
Meta Llama briefings and staff acknowledgement, investigation and resolution.
Versioned configuration controls rules; idempotent ingestion suppresses duplicate
incidents; short-lived evidence links and access logs support review; background
workers handle notification, escalation and retention.

The three-minute demo shows the actual web app and PostgreSQL-backed API. Synthetic
footage passes through the real FFmpeg decoder, tracker and rules engine. An
explicit handoff submits the resulting event to the API, retries it to demonstrate
duplicate suppression and uploads a synthetic clip. Staff review the clip, request
a real local Llama briefing and complete the recorded response workflow.

All 509 existing .NET tests pass, including PostgreSQL integration tests. Web type
checking, lint and production build also pass. This is a prototype: field accuracy
benchmarks, automatic edge/cloud delivery, live-camera clip extraction and
production sign-in remain next steps. Email is demonstrated through local pickup.
Detection assists staff and does not replace supervision.

## Public GitHub repository

https://github.com/vicosoft4real/edusphere-safety

## Three-minute demo video

https://www.youtube.com/watch?v=3fFxLrLHKA8

Uploaded to Victor Oluwayemi's channel as **Unlisted**, with the entrant's approval.
YouTube playback and Unlisted visibility were verified. A fallback watch page at
https://vicosoft4real.github.io/edusphere-safety/ includes an inline player and a
direct MP4 download from the public `hackathon-demo` release.
The final video is 179.89 seconds (under three minutes),
1920×1080 H.264/AAC, with burned-in English captions and a separate SRT.

## Describe your use of Meta products to build the submitted project

We integrated Meta Llama 3.2 3B into the incident review workflow, running locally
through Ollama. The officer clicks Generate briefing on an actual incident. The
server first resolves tenant membership and incident access through the real API,
then sends only event metadata to the local model: event type, state, campus and
camera identifiers, version and whether an evidence reference exists.

Llama returns schema-constrained JSON containing a concise summary, a human review
checklist and explicit unknowns. Zod validates the response shape. The UI shows
model attribution, identifies the metadata-only basis, labels the text as an
AI-generated draft and warns if the incident version has changed. A failed model
call produces an honest unavailable state. The model cannot change an incident,
send notifications or control cameras.

No footage is sent to the model. The demo includes real local inference, with the
middle of its wait shortened in the edit and explicitly labeled. The existing
hackathon commit history is authored under Muse Code; the demonstrated runtime
Meta integration is Llama. We make no claim that YOLOX is a Meta model, or that we
used hosted Meta APIs or partner credits that were not exercised.

## Why are Meta's models important to your solution?

The event pipeline produces technical metadata, while staff need a concise record
and a practical review checklist. Meta Llama bridges that gap inside the existing
incident workflow. Its open weights let us demonstrate local inference without
sending camera footage to a remote model service or requiring a cloud inference
credential.

We keep deterministic rules and human decisions separate from language generation.
Llama helps draft the explanation and surfaces unknown visual context and intent;
it does not declare wrongdoing or decide the response. This architecture makes the
model useful while keeping staff responsible for verifying its output. Local
inference is an option in this prototype, not a claim of production certification.

## Feedback from experience using partner technologies

The demonstrated inference path uses local Ollama with Meta Llama 3.2. We did not
use Baseten, Modal or Fireworks inference for the recorded build and cannot report
hands-on performance for those services. Structured JSON output made integration
with the typed web app straightforward. Local CPU inference had a visible wait;
we show that state honestly and shorten it only in the labeled video edit.
Prompt refinement was needed to distinguish a rule event from confirmed
unauthorized access, so every briefing remains a staff-reviewed draft.

## Questions that require the entrant's own answers

The form asks likelihood of using/recommending Meta Model API, overall opinion of
it, recommendation of Muse Code, biggest obstacle, comparison with other models,
model-selection priorities, switching barriers and production concerns. Those are
personal experience surveys. The entrant should answer them directly; the project
does not establish their opinions.

## Final consent

The form requires permission for Meta and Cerebral Valley to use, publish, share
and promote the submitted materials. The entrant must review and accept that
permission and perform the final submission.

Deadline: **November 4, 2026, 9:00 PM EST**, equivalent to **November 5 at 3:00 AM
in Lagos (WAT)**. Sources: the participant announcement email and the
[official participant guide](https://cerebralvalley.ai/e/meta-ai-developer-hackathon/details).
