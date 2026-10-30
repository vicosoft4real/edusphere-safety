# Edge detector benchmark — freeze procedure (Plan 2, Task 6)

Status: **procedure frozen, no measurements yet.** No accuracy, latency, or capacity
claim in this repo may cite detector performance until the `measured` columns below
are filled on connected CI. Model choice is NOT frozen: candidates are YOLOX-S and
NanoDet-Plus (both Apache-2.0). Ultralytics weights are excluded (AGPL) — see
§ License provenance.

## 1. Hardware spec capture (record before every run)

Record: CPU model (`lscpu` / `sysctl`), core/thread count, RAM, OS + kernel, .NET SDK
(`dotnet --info`), ONNX Runtime version (restored pin), `IntraOpNumThreads` used,
FFmpeg version, camera input resolution(s). One row per machine in the results table;
numbers from different machines are NOT comparable.

## 2. Fixed run parameters

- Resolutions: 640×480 and 1280×720 inputs (adapter letterboxes to 640×640).
- Sample rates: full-rate decode with detection on every 1st, 2nd, and 4th frame
  (three operating points: detect-every-N for N ∈ {1, 2, 4}); tracker + rules run
  on every decoded frame in all cases.
- Confidence thresholds swept: {0.3, 0.5, 0.7}; NMS IoU fixed at 0.45
  (`NmsIouThreshold` in `OnnxPersonDetector`); max 100 detections/frame.
- Preprocessing freeze params (record the frozen values alongside the weights
  provenance in §6): resampling is OUR bilinear (`ImageSampling` in Edge — no
  SkiaSharp anywhere in the detection path); letterbox pad is 114 gray; the
  `pixelDivisor` is a REQUIRED `OnnxPersonDetector` ctor parameter with no silent
  default (use 1.0 for raw 0..255 YOLOX exports, 255.0 for 0..1 exports — record
  which). Interpolation parity against the export's own preprocessing is re-verified
  at freeze time.
- Each configuration runs 3 repetitions; report median + min/max.

## 3. Dataset mix (required coverage before freezing a model)

| Slice | Content | Min. footage |
|---|---|---|
| Daylight corridor | single walker, zone crossing | 10 min |
| Darkness / low-light | same script, night lighting | 10 min |
| Occlusion | columns / door frames briefly hiding walkers | 5 min |
| Groups | 2–5 persons, splits and merges | 10 min |
| Reflections | glass doors / wet floor | 5 min |
| Reconnects | camera dropout gap (frozen + EOF), then resume | 5 min |

Simulator scenes (`tools/Simulator`) cover the scripted equivalents; consented real
footage may be swapped in as drop-in MP4 + hand-annotated manifest (see the Simulator
README boundary: pixels are never compared, detection is manifest-keyed).
Ground truth = per-frame person boxes in the manifest format.

## 4. Metrics (event-level and frame-level are SEPARATE — never mix them)

- **Event-level** (primary, what schools experience): precision/recall of fired rule
  events (zone-intrusion / boundary / dwell) against scripted ground-truth events,
  counted through the REAL pipeline (decode → detect → track → rules → outbox).
- **Frame-level** (diagnostic only): frame mAP@0.5 of raw detector boxes vs manifest
  boxes. Reported alongside, never substituted for event metrics.
- **False alarms per camera-hour**, broken down by slice (§3).
- **End-to-end latency** (capture → event JSON): p50 and p99 per operating point.
- Detector-only inference ms/frame (p50/p99) as a secondary diagnostic.

Freeze bar (all must hold on the SAME weights + config before a model becomes the default):
event recall ≥ 0.95 on daylight/groups, false alarms ≤ 1 per camera-hour per slice,
p99 E2E latency within the Plan 4 deployment budget (budget itself is out of scope here).

## 5. Results (EMPTY until measured on connected CI)

| Model | Hw | In-res | Every-N | Conf | Event-P | Event-R | FA/cam-hr | Frame-mAP | E2E p50 | E2E p99 |
|---|---|---|---|---|---|---|---|---|---|---|
| YOLOX-S | — | — | — | — | — | — | — | — | — | — |
| NanoDet-Plus | — | — | — | — | — | — | — | — | — | — |

## 6. License provenance record

| Field | YOLOX-S (candidate) | NanoDet-Plus (candidate) |
|---|---|---|
| Source repo | https://github.com/Megvii-BaseDetection/YOLOX | https://github.com/RangiLyu/nanodet |
| License | Apache-2.0 | Apache-2.0 |
| Weights URL | _placeholder — fill at download time_ | _placeholder — fill at download time_ |
| Weights SHA256 | _to be filled at download; verified at adapter startup_ | _to be filled at download; verified at adapter startup_ |
| Pinned version | _to be filled at download (repo tag/commit)_ | _to be filled at download_ |

**Ultralytics exclusion rationale:** Ultralytics publishes model weights under AGPL-3.0
terms, which would impose copyleft obligations on the school-side deployment bundle.
EduSphere ships proprietary school software; AGPL weights are therefore excluded from
candidacy regardless of accuracy. Only permissively-licensed (Apache-2.0 / MIT) weights
with recorded provenance may be frozen.

**No downloads were performed for this task** (sandbox has no network): the weights
URLs and SHA256 values above are filled on connected CI at download time, and the
adapter refuses to start on checksum mismatch.

## 7. CI commands (connected runner only)

```sh
# Re-include the gated project, restore, build:
dotnet sln EduSphere.Safety.slnx add src/EduSphere.Safety.Edge.Onnx/EduSphere.Safety.Edge.Onnx.csproj
dotnet restore --source <connected-feed>
dotnet build src/EduSphere.Safety.Edge.Onnx -c Release

# Download + record weights provenance (fills §6), then run the benchmark matrix (§2–§4):
# <benchmark harness command — owned by the CI run, results pasted into §5>
```

Sandbox verification for this task: `Letterbox` + `Nms` + `ImageSampling` +
`YoloxOutputParser` compile 0/0 with committed xunit tests (`EdgePreprocessTests`,
`EdgeParserTests`) exercised via a `/tmp` probe matrix; the adapter was reviewed by
reading against the OR 1.x API list in `src/EduSphere.Safety.Edge.Onnx/README.md`.
Adapter build + this benchmark run are CONNECTED-CI-ONLY (plan acceptance).
