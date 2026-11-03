# EduSphere Safety

Turn school camera events into an accountable staff response. **Built with Meta Llama and SAM 3.1.**

EduSphere Safety combines a school-side video pipeline, scheduled safety rules,
tenant-scoped incident management, evidence review, local Llama incident briefings,
and staff acknowledgement, investigation and resolution.

Built for the **Meta Global AI Developer Hackathon 2026**, under **Build for Your
World**. This is a working prototype, with explicit simulation and deployment
boundaries. It does not replace supervision or physical safety measures.

- [Watch the three-minute demo on YouTube](https://www.youtube.com/watch?v=lX3EbPyyavw)
- [Narration and captions](submission/NARRATION.md)
- [Detailed judge's guide](submission/JUDGES_GUIDE.md)
- [Submission answers](submission/SUBMISSION_ANSWERS.md)
- [Architecture and original design](docs/superpowers/specs/2026-10-29-edusphere-safety-design.md)
- [Operator configuration](docs/operator-notes.md)
- [Real-footage verification and hosted SAM 3.1 adapter](docs/real-footage.md)

[![EduSphere Safety demo](submission/demo-poster.jpg)](https://www.youtube.com/watch?v=lX3EbPyyavw)

## What runs

```text
School camera / test footage
  → SAM 3.1 saved tracks / FFmpeg + detector + tracker → scheduled rules → edge outbox
  → explicit demo handoff → .NET API → PostgreSQL incidents + notification worker
  → Next.js staff console → evidence review → acknowledge → investigate → resolve
                          ↘ local Meta Llama 3.2 → briefing + checklist + unknowns
```

The updated demo uses **Meta SAM 3.1 API** on a real 12-second pedestrian
recording from OpenCV. Its 120 frames produced 8 response-scoped object IDs;
the edge agent preserved those IDs and generated 6 demonstration-zone events.
Saved results are bound to the source SHA-256 and FPS. This is a public recorded
sample, not a connected local CCTV feed or an accuracy benchmark. See
[commands and limits](docs/real-footage.md), [measured results](submission/sam-validation.json)
and [media attribution](submission/THIRD_PARTY_MEDIA.md).

The demo explicitly hands one generated event to the real API, verifies duplicate
suppression, uploads a transcoded playback copy of the same recording and completes
the staff workflow. Automatic edge delivery and live-camera clip extraction remain
next steps. YOLOX ONNX and deterministic manifest replay remain alternative adapters.

For production, critical configured zones such as a pool area could escalate to
the responsible supervisor by phone, SMS or a site alarm, using the site's
supervision schedule. Those integrations are planned; current notification
verification uses local email pickup. No child-age, crawling or intent classifier
is demonstrated, and the system does not replace supervision.

## Meta model integration

The incident page's **Generate briefing** button calls a server-side route that
first checks the caller's tenant membership and access to the incident using the
real API, then requests a structured response from **Meta Llama 3.2 3B** running
locally in Ollama. The model receives event metadata, not camera images, video,
credentials, or staff identity. Its output contains a summary, human review
checklist, and explicit unknowns. Staff review the draft and retain every action.

```sh
ollama pull llama3.2
ollama serve # only if the Ollama app/server is not already running
```

Configure the web service with `OLLAMA_URL` (default `http://127.0.0.1:11434`)
and `LLAMA_MODEL` (default `llama3.2:latest`). No Meta API key is required for this
local open-weight integration. Model weights are downloaded separately and remain
under the [Llama 3.2 Community License](https://github.com/meta-llama/llama-models/blob/main/models/llama3_2/LICENSE).
The repository's MIT license does not relicense those weights.

## Run the local demonstration

Prerequisites: .NET 10 SDK, Node.js 22 LTS (22.13+) or 24 LTS, PostgreSQL with `psql`/`createdb`, FFmpeg,
Python 3, and Ollama with Llama 3.2. Local PostgreSQL must allow your OS user to
create and connect to a dedicated demo database. The recorded run used local
PostgreSQL 14; the deployment compose file targets PostgreSQL 17.

```sh
dotnet restore EduSphere.Safety.slnx
dotnet build EduSphere.Safety.slnx --no-restore
cd web && npm ci && cd ..
createdb -h 127.0.0.1 -U "$USER" edusphere_submission_demo
python3 submission/source/start-demo.py
python3 submission/source/prepare-demo.py setup
python3 submission/source/prepare-demo.py ingest
```

Open **http://127.0.0.1:3000**, use school `demo-school` and staff
`safety-officer`. All keys, logs, local evidence and email pickup files stay under
the ignored `.demo-runtime/` directory. The demo does not send email to real
recipients. It registers an example camera descriptor rather than claiming a
physical camera is connected.

`setup` enrolls a new device, creates a rule bundle and runs the synthetic pipeline.
`ingest` uses the event's stable ID to demonstrate duplicate suppression and uploads
the supplied synthetic clip. The demo handoff hashes the edge host's slash-separated
event ID into the cloud evidence API's 64-character identifier format. That mapping
is explicit in the harness; automatic edge/cloud transport remains future work.

For the updated SAM recording, follow [the real-footage setup](docs/real-footage.md)
to obtain source-bound SAM results, then run:

```sh
python3 submission/source/prepare-sam-demo.py
SAM_DEMO=1 python3 submission/source/verify-demo.py
```

The helper reuses saved results without another model call and prepares a fresh
incident. Hosted SAM requires a private server-side API key and uploads the clip;
local Llama requires no Meta API key. Never commit runtime credentials.

The local login is deliberately a development stub. For deployment, configure
authentication, object storage, notifications and secrets using the operator notes.
Production single sign-on wiring in the web console is still pending.

## Validate

```sh
dotnet test tests/EduSphere.Safety.Tests
cd web && npm run typecheck && npm run lint && npm run build
```

The .NET integration tests require Docker; the video decode test requires FFmpeg.
**Measured on November 3, 2026: 518 .NET tests and 6 Python adapter tests passed.**
The submission also checks the live web/API flow, exact uploaded video bytes,
anonymous and cross-tenant refusals, real local Llama inference, and persisted
staff transitions. See [validation notes](submission/VALIDATION.md).

Do not run `next build` in the same output directory as an active `next dev`
server. Stop the development server first, or build in a separate checkout.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/EduSphere.Safety.Edge*` | Decoder, detector adapter, tracker, rules, local buffer and edge host |
| `src/EduSphere.Safety.Api` | Device authentication and staff-facing incident/evidence/configuration APIs |
| `src/EduSphere.Safety.Application` | Commands, validation, incident workflow and interfaces |
| `src/EduSphere.Safety.Infrastructure` | PostgreSQL repositories, migrations and evidence storage |
| `src/EduSphere.Safety.Worker` | Notification dispatch, escalation and retention |
| `web` | Next.js staff console and local Meta Llama briefing integration |
| `tools/Simulator` | Deterministic synthetic footage and ground truth |
| `tests` | Existing domain, pipeline, API and PostgreSQL tests |
| `submission` | Judge explanation, answers, video script and reproducible production sources |

MIT-licensed application code. Third-party libraries and models retain their own
licenses. YOLOX candidate provenance is in [docs/weights.md](docs/weights.md).
