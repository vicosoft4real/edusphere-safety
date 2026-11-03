# EduSphere Safety · three-minute narration

Synthetic English narration. All app footage is recorded from the running project.

### 00:00:00–00:00:12 · THE PROBLEM

A camera can record an incident without getting help to the right person. EduSphere Safety connects school camera events to a staff response, with evidence and a clear record of who acted.

### 00:00:12–00:00:24 · HOW IT WORKS

The edge agent decodes video, tracks people, and applies scheduled rules. A cloud API stores incidents. The staff console combines evidence review, local Meta Llama briefings, and a recorded response workflow.

### 00:00:24–00:00:38 · 01 / THE RUNNING APP

This is the running application with a real PostgreSQL database. We sign in as the school safety officer. The overview shows enrolled devices and open incidents, while diagnostics identifies camera coverage gaps.

### 00:00:38–00:00:54 · 02 / DEFINE THE RULE

A school defines a restricted zone and an after-hours schedule. This editor stores the polygon, confidence threshold, persistence, and cooldown in a versioned bundle. Persistence requires multiple frames, so one isolated detection does not trigger this rule.

### 00:00:54–00:01:12 · 03 / RUN THE EDGE PIPELINE

This reproducible demo uses synthetic footage and replayed detections. The real FFmpeg decoder, tracker, and rule engine process sixty frames and write one zone event to the persistent outbox. This verifies pipeline behavior, not detector accuracy.

### 00:01:12–00:01:26 · 04 / CREATE AN INCIDENT

A demo handoff sends the edge outbox event to the real API. Retrying the same event returns the same incident identifier. Staff open that single incident from the inbox.

### 00:01:26–00:01:44 · 05 / REVIEW THE EVIDENCE

We upload the synthetic clip through the evidence API and open it from the incident. Playback is authorized through a short-lived link, and access is logged. This supplied test clip gives staff context; automatic live-camera clip extraction remains future work.

### 00:01:44–00:02:06 · 06 / META LLAMA IN THE WORKFLOW

Now we ask Meta Llama to draft an incident briefing. It runs locally and receives only authorized event metadata, never camera footage. The structured result gives staff a summary, review steps, and explicit unknowns. It cannot change the incident or take action. Staff verify the draft against the evidence.

### 00:02:06–00:02:24 · 07 / CLOSE THE LOOP

The officer acknowledges the incident, adds an investigation note, and resolves it. Each transition checks the current version to prevent conflicting updates. The database records the actor and activity. Acknowledgement also cancels pending escalation, connecting the alert to an accountable staff response.

### 00:02:24–00:02:38 · 08 / OPERATE RESPONSIBLY

Notification policy sets escalation recipients and delays. The worker writes email to a local pickup directory in this demo. Diagnostics shows evidence access and untested cameras, making monitoring gaps visible.

### 00:02:38–00:02:52 · WHAT IS VERIFIED

All five hundred and nine existing tests pass, including PostgreSQL integration tests. Next come real-camera validation, measured detector benchmarks, automatic edge delivery, and production sign-in integration.

### 00:02:52–00:03:00 · EDUSPHERE SAFETY

EduSphere Safety makes the response part of the record. It assists school staff; it never replaces supervision.
