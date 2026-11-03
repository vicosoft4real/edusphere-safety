# EduSphere Safety · three-minute SAM demo narration

Synthetic English narration. Actual app captures and attributed OpenCV real footage with verified SAM API tracks. The 12-second tracking clip plays at normal speed, followed by a held frame. Local Llama inference wait is omitted between actual before/after captures.

### 00:00:00–00:00:10 · THE PROBLEM

A camera can record an event without getting help to the right person. EduSphere Safety connects real video tracking to evidence, staff review, and a recorded response.

### 00:00:10–00:00:24 · HOW IT WORKS

Meta SAM three point one tracks people in a recorded clip. The edge agent checks the source and applies zone rules. Our API stores incidents and evidence. Local Meta Llama drafts a briefing, while staff control the response.

### 00:00:24–00:00:38 · 01 / THE RUNNING APP

This is the running Next.js console backed by our dot net API and PostgreSQL. We sign in as the school safety officer. The overview shows devices, monitoring coverage, and incidents for this school.

### 00:00:38–00:00:50 · 02 / DEFINE THE RULE

The school configures a zone, schedule, confidence threshold, and persistence. This versioned bundle names SAM three point one. Multiple frames must support the configured rule before it produces an event.

### 00:00:50–00:01:12 · 03 / META SAM 3.1 ON REAL VIDEO

Here, Meta SAM three point one tracks real people in a twelve-second public OpenCV recording. These boxes and IDs came from an actual API call. All one hundred and twenty frames returned tracks, with eight IDs across the clip. Our edge agent checks the source and evaluates the tracks, producing six events for this demonstration zone.

### 00:01:12–00:01:26 · 04 / CREATE AN INCIDENT

We select one actual SAM zone event. An explicit demo handoff sends it to the API and retries it. Both requests return one incident. The record retains model version SAM three point one.

### 00:01:26–00:01:44 · 05 / REVIEW THE EVIDENCE

This is the same real clip, uploaded with the event through the evidence API. Playback uses an authorized, short-lived link, and access is logged. The recording gives staff context. Automatic live-camera clip extraction remains future work.

### 00:01:44–00:02:04 · 06 / META LLAMA IN THE WORKFLOW

Meta Llama drafts an incident briefing locally. It receives only authorized event metadata, with no video. The structured result gives staff a summary, review steps, and explicit unknowns. It cannot change the incident or take action. Staff verify the draft against the evidence.

### 00:02:04–00:02:22 · 07 / CLOSE THE LOOP

The officer acknowledges the incident, adds an investigation note, and resolves it. Each transition checks the current version to prevent conflicting updates. The database records the actor and activity. Acknowledgement cancels pending escalation, connecting this real video event to an accountable response.

### 00:02:22–00:02:38 · 08 / OPERATE RESPONSIBLY

In production, a configured pool-zone event could route an urgent alert to a supervisor by phone, SMS, or a site alarm. This demo verifies local email pickup, escalation settings, and the evidence access log.

### 00:02:38–00:02:52 · WHAT IS VERIFIED

Five hundred and eighteen dot net tests and six Python tests pass. This validates a recorded-clip workflow. Field accuracy, continuous cameras, automatic delivery, and production sign-in remain next steps.

### 00:02:52–00:03:00 · EDUSPHERE SAFETY

Built with Meta SAM and Llama, EduSphere Safety records the response. Staff remain in control.
