# EduSphere Safety Plan 2 — Edge Pipeline + Simulator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the school-side pipeline — frame ingest, person detection, tracking, deterministic zone/boundary/dwell rules, encrypted evidence buffer, offline spool, and cloud upload — proven end-to-end on synthetic footage through the real decode/track/rule code.

**Architecture:** New `EduSphere.Safety.Edge` classlib (no IO in geometry/rules/tracker; process boundary only at FFmpeg spawn, file/buffer IO, HttpClient) + thin `EduSphere.Safety.EdgeAgent` host (Plan 4 wires deployment). Detector behind `IPersonDetector`; ONNX adapter compiles and benchmarks on connected CI (package absent from sandbox cache). Frame sources behind `IFrameSource` (FFmpeg file input verified here; RTSP is the same argv path with a different URL, CI-gated).

**Tech Stack:** .NET 10, FFmpeg 8.1.2 CLI (argv-only, restricted protocols), SkiaSharp (synthetic frames; pin a cached version — `ls ~/.nuget/packages/skiasharp` first), System.Text.Json, AES-GCM buffer envelopes. No OpenCV, no ONNX package in-sandbox.

**Later plans (not this file):** Plan 3 Next.js, Plan 4 hardening/deploy/Chrome (owns recipient engine, cursor pagination, web auth, full ONVIF media-service, GPU sizing).

**Sandbox execution notes (same as Plan 1, plus):** socket bind() blocked — no RTSP listen, no UDP send, no server run; `dotnet test` cannot execute (prove via compile + /tmp probes, `dotnet exec`, `UseAppHost=false`); FFmpeg file/pipe decode WORKS (no sockets); `postgres --single` validation pattern from Plan 1 still applies if needed (not needed here — no DB). New pins must exist in `~/.nuget/packages` — confirm with `ls` before pinning; ONNX Runtime is NOT cached, so the adapter file is CI-gated (see Task 6).

---

## File structure

- Create: `src/EduSphere.Safety.Edge/Frames.cs` (Frame, Detection, Track, TrackedPoint, frame clock)
- Create: `src/EduSphere.Safety.Edge/IDetector.cs` (`IPersonDetector`, `IFrameSource`), `FakeDetector` (test-only, in test project)
- Create: `src/EduSphere.Safety.Edge/Tracker.cs` (IoU ByteTrack-lite, grace, stable ids)
- Create: `src/EduSphere.Safety.Edge/Geometry.cs` (polygon zone, directed segment, foot point, hysteresis)
- Create: `src/EduSphere.Safety.Edge/Rules.cs` (rule config versions A–D + health, evaluation over track histories)
- Create: `src/EduSphere.Safety.Edge/Schedules.cs` (IANA tz, overnight, DST, holidays/exceptions, pool sessions, cooldowns, pauses)
- Create: `src/EduSphere.Safety.Edge/EvidenceBuffer.cs` (segment manifest, AES-GCM envelope, bounded eviction)
- Create: `src/EduSphere.Safety.Edge/EdgeClients.cs` (upload with stable edge IDs + backoff, heartbeat, config pull, device-key header)
- Create: `src/EduSphere.Safety.Edge/FfmpegFrameSource.cs` (argv-only spawn, rawvideo decode, frozen-frame + disconnect detection)
- Create: `src/EduSphere.Safety.Edge/OnvifDiscovery.cs` (WS-Discovery probe builder + response parser; socket send CI-gated)
- Create: `src/EduSphere.Safety.EdgeAgent/Program.cs` (thin host wiring pipeline stages; deployable in Plan 4)
- Create: `tools/Simulator/` (SkiaSharp synthetic scene → PNG frames → FFmpeg MP4; scripted ground-truth manifest)
- Test: `tests/EduSphere.Safety.Tests/Edge*Tests.cs` (unit: tracker, geometry, rules, schedules incl. DSTangkauan; integration-style via probes: decode→track→rules→outbox JSON)

---

### Task 1: Frame/detection models + pipeline interfaces (TDD)

**Files:** Create `Frames.cs`, `IDetector.cs` in new `src/EduSphere.Safety.Edge` (net10.0, Nullable, TreatWarningsAsErrors, no package refs); test project references Edge.

- [x] **Step 1: Write failing tests** — `Frame` carries camera id, frame number, capture timestamp (event time), width/height; `Detection` has normalized box (x,y,w,h in 0..1), confidence, class id==person only; foot point = bottom-center; `IFrameSource` yields frames with monotonic numbers; `IPersonDetector.DetectAsync(frame, ct)` returns person detections above threshold (fake: scripted per-frame boxes).
- [x] **Step 2: Run to verify they fail** (compile fails; `dotnet test` cannot execute here — record CI-only, verify via compile + /tmp probe per sandbox notes).
- [x] **Step 3: Implement minimal models** — immutable records; normalized coords validated 0..1; timestamps: capture/event time on Frame, never wall-clock at rule time. Detector confidence stored as signal (doc comment: NOT a danger probability).
- [x] **Step 4: Verify** — Edge + tests compile 0/0; /tmp probe constructs frames/detections and asserts foot-point math.

---

### Task 2: IoU tracker with grace and stable IDs (TDD)

**Files:** Create `src/EduSphere.Safety.Edge/Tracker.cs`; test `EdgeTrackerTests.cs`.

- [x] **Step 1: Write failing tests** — same box across frames keeps one id; two boxes keep distinct ids; brief disappearance (< grace frames) keeps id on return; disappearance ≥ grace retires id (re-entry = new id); IoU threshold constant documented (e.g. 0.3); id counter never reuses retired ids within a run.
- [x] **Step 2: Run to verify they fail.**
- [x] **Step 3: Implement ByteTrack-lite** — greedy IoU matching (no Kalman; document why: deterministic, CPU-cheap, sufficient for zone/dwell semantics), per-track miss counter, grace retire, monotonic id allocation. No dependencies.
- [x] **Step 4: Verify** — compile 0/0; /tmp probe replays scripted box sequences (occlusion gap, group split) and prints id histories; assert grace boundary (kept at grace-1, retired at grace).

---

### Task 3: Geometry + rule engine + schedules (TDD)

**Files:** Create `Geometry.cs`, `Rules.cs`, `Schedules.cs`; tests `EdgeGeometryTests.cs`, `EdgeRulesTests.cs`, `EdgeScheduleTests.cs`.

Geometry (pure):
- Normalized polygon zones; point-in-polygon (ray casting, edge-inclusive rule documented: boundary counts as INSIDE for entry, OUTSIDE for exit = hysteresis band); directed boundary segments (crossing = foot-point segment intersects + direction sign matches); which point decides = foot point (documented).
- Resolution/aspect change: all geometry normalized 0..1, so stream changes cannot shift zones; camera-movement invalidates active tracks (documented: tracks reset on resolution/fov change signal).

Rules (deterministic over track histories, never single-frame):
- A zone-intrusion: foot point inside zone for ≥ persistence frames AND schedule says closed.
- B boundary-crossing: sustained directed crossing across ≥2 consecutive frames.
- C dwell: continuous presence ≥ dwell duration (seconds, evaluated on event timestamps); exit or grace-expiry resets; re-entry starts over.
- D pool-zone: like A against pool polygon + authorised-session check.
- E health: produced by ingest layer (offline/frozen/analysis-failure), not the rule engine — engine exposes `LastHealthyFrameAge` for it.
- Suppression: per-rule cooldown after firing; temporary pause with expiry; rule-version change during an active event: re-evaluate from scratch under the new version (documented; old partial state discarded, new event links supersedes id).
- Every fired event records: rule version, model version, detection/event/ingest timestamps separately, track id, foot-point trail.

Pinned semantics (Task 3 quality review — normative):
- Presence follows the TRACKER: a track in ActiveTracks counts as present even on grace-held stale boxes (occlusion bridges dwell/persistence); a track absent from ActiveTracks drops its episode (reset). Task 5 MUST pass ActiveTracks through unfiltered; document on Evaluate.
- Track carries last-matched `Confidence` + `LastDetectionTimeUtc` (set on every Match); Task 5 builds ScoredTrack from those. Below-threshold ⇒ skip as if absent (episode resets); threshold jitter can reset episodes — detector debounce is the documented mitigation, not engine logic.
- Boundary re-arm: an opposite-direction crossing CLEARS the fired latch (re-arm); refire still subject to cooldown. Re-arm requires the crossing itself, not a side==0 frame.
- Schedule-open frames FREEZE episode state (no accumulate, no lapse, no fire); evaluation resumes on close. No instant stale fire on close.
- Multi-rule same-frame fire order: sorted by rule id (deterministic).
- Event timestamps must be non-decreasing per caller contract (undefined otherwise — documented, not guarded).
- IANA timezone database required (Linux/macOS CI); noted on schedule tests.

Schedules:
- School IANA timezone via TimeZoneInfo; overnight windows (e.g. 22:00–06:00); DST spring-forward/fall-back pinned tests (use a tz with DST, e.g. America/New_York: 2026-03-08 02:30 does not exist; 2026-11-01 01:30 occurs twice); holidays/exceptions list; authorised pool sessions (manual calendar entries: pool id, start/end); evaluation uses EVENT timestamps.

- [x] **Steps 1–2: Failing tests first** — one test per behavior above (≥20 tests): entry/exit hysteresis, directed vs wrong-direction crossing, dwell reset on exit, overnight wrap, DST gap/ambiguity, holiday suppression, session-authorized pool entry silent, cooldown dedup, pause expiry, version-change restart.
- [x] **Step 3: Implement** — pure functions, no clock (pass timestamps in), no IO.
- [x] **Step 4: Verify** — compile 0/0; /tmp probe runs the full event matrix and prints PASS/FAIL per case.

---

### Task 4: Evidence buffer/spool + cloud clients (TDD)

**Files:** Create `EvidenceBuffer.cs`, `EdgeClients.cs`; tests `EdgeBufferTests.cs`, `EdgeClientTests.cs` (HttpClient via fake `HttpMessageHandler` — no sockets needed).

- Buffer: rolling segments (configurable seconds each) with manifest (segment id, start/end event-time, SHA256); AES-GCM envelope per segment (key from host config, never logged); BOUNDED total size with explicit eviction policy (oldest sealed segments first; document what is lost at capacity: oldest evidence beyond the bound is unrecoverable); crash-safe manifest rewrite (write-temp + rename).
- Offline spool: pending-upload queue persisted as JSONL (event metadata + clip refs + stable edge IDs + attempt counts); survives edge restart (file-backed); uploader drains oldest-first with backoff+jitter; delayed events labelled delayed on sync.
- Cloud clients: upload (event metadata + snapshot/clip bytes, device-key header `X-Device-Key: id.secret`, stable edge IDs for idempotency, retryable-vs-fatal status mapping); heartbeat (device id, software/detector/config versions, queue depth, frame age, disk use); config pull (versioned rule bundle, stale-config detection via `config_version` echo in heartbeats). Health: frozen-frame detector (identical frame hash N in a row WITH an open stream) and disconnect detector live in the ingest layer and emit E-events through the same outbox path.
- [x] **Steps 1–2: Failing tests** — eviction drops oldest first and manifest stays consistent; restart reloads spool and resumes; retryable (429/503) vs fatal (400/401/403/404) mapping; heartbeat payload contains all versions + queue depth; stale config flagged when server version > local.
- [x] **Step 3: Implement.**
- [x] **Step 4: Verify** — compile 0/0; /tmp probe fills buffer past capacity and asserts eviction order; fake-handler upload client asserts retry/backoff behavior and delayed labelling.

Hardening (Task 4 quality review — normative):
- Spool load skips corrupt lines (count exposed via `SkippedCorruptLines`), never bricks restart; corrupt manifest.json is quarantined (`manifest.corrupt-<utc>` rename) and the buffer starts empty with a `ManifestWasCorrupt` flag; recovery procedure documented.
- Segment pinning: `Pin(id)`/`Unpin(id)`; eviction skips pinned segments; Task 5 wiring contract — enqueue pins, successful upload unpins. `GetSegment` on missing id throws KeyNotFoundException (Task 5 translates to spool-drop + ops note, no infinite retry).
- Upload bodies use `application/json` Content-Type (asserted in tests).
- `FrameHealthMonitor` is thread-safe (internal lock). `NoteFrame(sig, streamOpen:false, …)` does NOT refresh the idle clock — callers must stop calling when down and rely on QueryIdle; documented. `NoteProcessExit` is edge-triggered by contract (documented call-once per exit; repeated calls re-emit — caller must edge-trigger).
- `BackoffDelay` guards attempt >= 0; heartbeat builder validates (queueDepth/disk >= 0, finite age); shared static JsonSerializerOptions; EnsureSuccessStatusCode behavior documented.
- Tests added: nonce uniqueness + tamper ⇒ CryptographicException; corrupt-line reopen; unknown ids; duplicate enqueue rejection; frozen-once-per-streak; eviction deletes the file; key defensive copy; Content-Type assert; pinning blocks eviction.

---

### Task 5: FFmpeg source + SkiaSharp simulator + pipeline test (TDD)

**Files:** Create `FfmpegFrameSource.cs`, `tools/Simulator/*`, `EdgeAgent/Program.cs` (thin wiring only); test `EdgePipelineTests.cs` (mostly /tmp-probe executed, committed tests where socket-free).

- `FfmpegFrameSource`: argv-only spawn (`ffmpeg -hide_banner -loglevel error -rtsp_transport tcp -i <url> -f rawvideo -pix_fmt rgb24 -s WxH -r N -` for RTSP; file input variant for simulator: `-re -i file.mp4 ...`); protocol allowlist enforced on the URL BEFORE spawn (rtsp/http/file only per source type; reject `concat`, pipes, metadata IPs); stdout rawvideo parsing to Frames with capture timestamps; stderr drained to logs (redacted); no shell interpolation (ProcessStartInfo ArgumentList); process crash ⇒ E-event + supervised restart with backoff; frozen detection (frame-hash repeats) and EOF/disconnect ⇒ E-event; resolution change mid-stream ⇒ track-reset signal + re-log geometry basis. VERIFIED HERE with file input (same binary, same argv shape, same parser): decode a simulator MP4 and assert frame count/dimensions/timestamps.
- Simulator (`tools/Simulator`, NOT shipped to schools): SkiaSharp scene renderer — deterministic scripted scenes (empty corridor; single walker crossing a zone; loiterer; group; boundary crosser both directions; camera dropout gap) drawn as filled person-rectangles on static backgrounds + ground-truth manifest (per-frame boxes). Frames → PNG → `ffmpeg -framerate N -i f%04d.png -c:v libx264 -pix_fmt yuv420p sim.mp4`. Consented-real-footage swap-in documented (drop-in MP4 + hand-annotated manifest). The simulator drives the REAL decode→detect(fake-scripted-from-ground-truth)→track→rules→outbox path.
- Pipeline test: file MP4 → FfmpegFrameSource → scripted detector (replays ground-truth manifest as detections) → real Tracker → real Rules (zone stampede scenario) → outbox JSONL; assert the staged intrusion produces exactly one event with rule/model versions and three timestamps. This is the in-sandbox vertical slice proof.
- [x] **Steps 1–2: Tests/probe scripts first.**
- [x] **Step 3: Implement.**
- [x] **Step 4: Verify** — compile 0/0; pipeline probe output asserts single-intrusion-single-event; RTSP transport itself is CI-gated (same argv path, different URL scheme — note it).

Detector seam (Task 5 quality review — normative for Task 6):
- `Frame` carries `Pixels: ReadOnlyMemory<byte>` in documented `Rgb24` row-major layout (length == W*H*3); `FfmpegFrameSource` retains the decoded buffer (still hashes it for health); `IPersonDetector` receives pixels via Frame (signature unchanged).
- Replay detector maps Nth decoded frame → Nth manifest entry (order-based, robust to mux renumbering and dropout gaps); assumes no mid-stream frame drops in file mode (documented).
- FFmpeg hardening: `-protocol_whitelist file` (file mode) / `rtsp,rtp,tcp,udp` (RTSP mode) before `-i`; redact ALL `://…@` spans in logs; argv credential exposure documented with least-privilege guidance; start-failure disposes; bounded WaitForExit in Dispose.
- Bundle strictness: non-empty ruleId, version>=1, threshold finite 0..1, persistence>=1, dwell/cooldown/exitMargin>=0, polygon>=3 normalized vertices, direction in {-1,0,1}, scheduleId/poolId must resolve, duplicate scheduleId rejected; malformed dates/manifest wrapped as InvalidDataException.
- Tests: golden manifest hash pinned per scenario; README fidelity boundary (pixels never compared; detection is manifest-keyed); ffmpeg-absent passes with a note (CI must have ffmpeg); allowlist edge tests (IPv6, userinfo, encoded hosts, double-dispose).

---

### Task 6: ONNX adapter (CI-gated) + detector benchmark procedure

**Files:** Create `src/EduSphere.Safety.Edge/OnnxPersonDetector.cs` (implements `IPersonDetector`); `docs/edge-benchmark.md` (procedure + results table, targets vs measured).

- Adapter written against the Microsoft.ML.OnnxRuntime 1.x API (session options: intra-op threads, CPU arena; preprocess: letterbox resize + normalize per model card; NMS on outputs; confidence threshold from rule config; model path + checksum verified at startup, mismatch ⇒ refuse to start). It CANNOT compile in-sandbox (package absent) — write carefully, mark `// CI-gated: requires Microsoft.ML.OnnxRuntime`, and the connected-CI build is the check. Keep ALL preprocessing numbers (input size, mean/std, NMS IoU) as named constants from the model card.
- Model choice is NOT frozen here: candidates are Apache-2.0 YOLOX-S and NanoDet-Plus (Ultralytics weights excluded — AGPL). `docs/edge-benchmark.md` defines the freeze procedure: fixed hardware (x86 CPU model), resolutions, sample rates, dataset mix (daylight/darkness/occlusion/groups/reflections), metrics (event-level precision/recall SEPARATE from frame mAP, false alarms per camera-hour, p50/p99 end-to-end latency), and a results table with `measured` columns left EMPTY until run. No accuracy claims until measured.
- License provenance record in the doc: candidate, repo URL, license, weights URL, SHA256 (filled at download time), pinned version.
- [x] **Steps 1–2: Tests first** — preprocessing unit tests must NOT depend on ONNX: factor pure `Letterbox.cs`/`Nms.cs` into the Edge project (fully tested here); the adapter itself is thin glue.
- [x] **Step 3: Implement adapter + benchmark doc.**
- [x] **Step 4: Verify** — Edge compiles 0/0; preprocessing tests green via probe; adapter + benchmark execution marked CONNECTED-CI-ONLY in the plan acceptance.

Preprocessing ownership (Task 6 quality review — normative):
- All pixel math lives in Edge as pure code and is tested HERE: `ImageSampling.LetterboxRgb24ToNchwFloat` (bilinear, gray pad, NCHW float) + `YoloxOutputParser` + `IModelOutputParser` (moved from Edge.Onnx — they depend on no OR types). Edge.Onnx keeps ONLY the thin OR glue (session, checksum, tensor fill from the float array). SkiaSharp is NOT used in the detection path (Simulator only); interpolation parity = our bilinear, recorded at freeze.
- `pixelDivisor` is a REQUIRED ctor parameter (no silent default); the freeze records divisor alongside pad/interpolation.
- Parser hardening: score-order top-K (not anchor-order truncation), skip non-finite anchors, clamp-then-drop degenerate, shape rejection, single-input/single-output validation with clear errors; weights hashed via stream.

---

### Task 7: ONVIF discovery probe (parser now, sockets on CI)

**Files:** Create `src/EduSphere.Safety.Edge/OnvifDiscovery.cs`; test `EdgeOnvifTests.cs`.

- Scope: WS-Discovery Probe (SOAP envelope builder with types `tdn:NetworkVideoTransmitter`) + response parser (endpoint XAddrs, scopes, device types) as PURE functions with captured-sample tests; UDP multicast send/receive + RTSP URL validation against local policy live in the same file but marked CI-gated (bind/send blocked here). Full media-service (GetStreamUri/GetCapabilities) and PTZ are Plan 4. Manual RTSP configuration remains the primary onboarding path.
- [x] **Steps 1–2: Tests first** — parse 2+ captured probe-match samples (incl. multi-XAddrs + scope lists); builder output schema-validated by string assertions.
- [x] **Step 3: Implement.**
- [x] **Step 4: Verify** — compile 0/0; parser probe green; socket path marked CI-only.

---

## Plan 2 acceptance

Scripted simulator intrusion → exactly one versioned event JSON through real decode/track/rules; dwell/boundary/pool/health behaviors green in matrix probe; buffer eviction order + restart resume proven; migration-free (no DB in edge); Edge compiles 0/0; ONNX adapter + benchmark + ONVIF sockets + RTSP-live run on connected CI; no accuracy or capacity claims beyond measured numbers.
