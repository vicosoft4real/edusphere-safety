# Saved hackathon submission

Updated November 3, 2026, and verified after reloading the saved entry.

- Team: EduSphere Safety; Victor Oluwayemi (`vicomeg`).
- Track: Indie Developer. Theme: Build for Your World.
- Public repository: https://github.com/vicosoft4real/edusphere-safety
- Unlisted demo: https://www.youtube.com/watch?v=lX3EbPyyavw
- Saved entry: https://cerebralvalley.ai/e/meta-ai-developer-hackathon/hackathon/a5dffe54-06b2-42dd-974e-723991f68a85/edit
- Existing accepted materials grant and survey answers were preserved.

## Project description

EduSphere Safety connects real recorded-video events to an accountable staff response. Meta SAM 3.1 tracks people through a clip; the .NET edge agent validates the source SHA-256 and frame rate, preserves the model's object IDs, and evaluates versioned zone rules. A tenant-scoped API and PostgreSQL back the Next.js staff console, evidence review, local Meta Llama briefing, and acknowledgement, investigation and resolution.

The updated demo uses a 12-second real pedestrian recording from OpenCV, with actual SAM API output: 120 processed frames, eight response-scoped track IDs and six events for a demonstration zone. We select one event, explicitly hand it to the API, verify duplicate suppression, upload a transcoded playback copy of the same recording, and record the staff workflow. This is a public recorded sample, not a connected local CCTV feed or an accuracy benchmark.

Production roadmap: critical configured zones, including pool-safety zones governed by a supervision schedule, could escalate urgently to the responsible supervisor through phone, SMS or a site alarm. Current delivery verification uses local email pickup. All 518 .NET tests and six Python adapter tests pass. Continuous camera operation, field accuracy, automatic edge delivery and production sign-in remain next steps.

## Use of Meta products

Meta SAM 3.1 on Meta Model API performs actual video tracking. We submit the real MP4 with the concept prompt "person" and request confidence and compact mask output. The verified request processed 120 frames; SAM returned tracks on all 120 frames with eight object IDs across the clip. The preview draws returned boxes, IDs and movement trails. Encoded masks are retained; the demo does not claim decoded mask rendering or person identification.

The edge agent binds saved SAM results to the exact source SHA-256 and FPS, uses explicit frame indices, and maps SAM IDs one-to-one into its positive-ID rule state without IoU reassignment. Missing frames clear presence/trails. The real rule engine produced six demonstration-zone events; one was ingested into the incident API with duplicate suppression and evidence playback.

Meta Llama 3.2 3B runs locally through Ollama to draft a summary, review checklist and unknowns from tenant-authorized incident metadata only. It receives no video. The UI labels its result as a staff-reviewed draft and flags stale incident versions. The model cannot change state or contact people. Hosted SAM receives the uploaded clip; local Llama receives metadata. The existing development history records Muse Code as commit author. YOLOX remains an alternative non-Meta adapter.

## Why Meta models matter

SAM 3.1 supplies object tracks directly from real video, making the Meta model part of the event-generation path rather than only an explanatory feature. Stable response-scoped IDs and explicit frame markers let our rule engine evaluate a person's movement within a configured zone. Source and frame-rate checks prevent accidentally applying results to another recording. We do not assume identity persists through full occlusion or across separate API responses.

Local open-weight Llama makes technical event metadata readable to a school officer through a review checklist and explicit unknowns. Deterministic rules and staff retain control of escalation and incident decisions. For critical areas such as pools, the intended production benefit is to get the responsible supervisor's attention sooner through an integrated alert channel; that benefit still needs field validation.

## Partner feedback

We exercised hosted Meta SAM 3.1 directly and local Ollama with Meta Llama 3.2. SAM's explicit frame markers and response-scoped object IDs were useful; preserving source FPS and converting inclusive box corners correctly matter. The single verified SAM request reported 120 video frames, corresponding to $0.024 at the published rate and covered by existing free credits. No accuracy comparison or production latency claim is made. Local Llama required prompt refinement to avoid copying an example location, reinforcing the need for staff verification. We did not exercise Baseten, Modal or Fireworks inference and cannot report their hands-on performance.

## Demo media

The MP4 is 179.887696 seconds, 1920×1080, H.264/AAC, with burned English captions.
See [media validation](media-validation.json), [narration](NARRATION.md),
[third-party source attribution](THIRD_PARTY_MEDIA.md) and [judge guide](JUDGES_GUIDE.md).
