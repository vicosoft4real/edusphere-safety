# Real footage and SAM 3.1

The edge agent has an actual pixel-inference mode. Use `--model`, not `--manifest`:

```sh
dotnet run --project src/EduSphere.Safety.EdgeAgent -- \
  --source /absolute/path/cctv.mp4 --camera camera-1 \
  --rules /absolute/path/rules.json --out .demo-runtime/live-output \
  --model models/yolox_s_416x416.onnx \
  --model-sha256 5515a6d732986dea8c9d1241dc5464c3eb161048e8fd959314755e5b5496c299 \
  --pixel-divisor 1.0 --input-size 416 --model-output raw-grid \
  --width 768 --height 576 --fps 10
```

`--source` also accepts a reachable private-LAN RTSP URL. Camera discovery cannot
recover camera passwords or cross a router's isolated camera network.

The YOLOX mode runs decoded RGB pixels through ONNX, greedy IoU association and
the product's zone/dwell rules. Its IDs can fragment through overlap/occlusion;
they do not identify people. It requires field benchmarking before deployment.

## Hosted Meta SAM 3.1

`tools/sam_tracking.py` is a separate command-line adapter for Meta Model API.
It uploads an MP4 clip with the `person` prompt to `sam-3.1`. Its exported frame
numbers and object IDs come from SAM, with no extra IoU reassignment. It preserves
encoded masks and validates boxes; it does not decode or render mask rasters.
The edge agent's `--sam-results` mode feeds those model IDs into the existing rule
engine, with a source SHA-256 and FPS check before use. IDs are mapped one-to-one
from SAM's zero-based space to the core's positive ID space, without geometric
reassignment. Missing frames clear presence and reset trails rather than bridging
unseen movement. This handles recorded clips, not continuous camera streaming or
cross-clip identity. Dashboard ingestion still requires the explicit event handoff.

Set `MODEL_API_KEY` in the process environment or in the git-ignored file
`.demo-runtime/meta-api.env`. Never put keys in source, argv, screenshots or chat.
The directory is already ignored. Limit local file access to its owner.

```sh
python3 tools/sam_tracking.py --video /absolute/path/cctv.mp4 \
  --out .demo-runtime/sam-results
```

The command uploads video to Meta and incurs API usage; verify your credits and
spending limit before running it. A real SAM 3.1 call was verified on the public
sample described below. An API failure produces an error rather than invented
fallback tracks. The client has no automatic retries. If Python's default trust
store is missing, configure `SSL_CERT_FILE` to a trusted CA bundle (for example
`certifi.where()` from an installed certifi package); never disable verification.

Set `modelVersion` to `sam-3.1` in your rule bundle, then consume the exact same
video and the saved API result (this second command makes no API call):

```sh
dotnet run --project src/EduSphere.Safety.EdgeAgent -- \
  --source /absolute/path/cctv.mp4 --camera camera-1 \
  --rules /absolute/path/sam-rules.json --out .demo-runtime/sam-events \
  --sam-results .demo-runtime/sam-results --fps 10 --width 768 --height 576
```

`--fps` must match the source FPS in the SAM summary. Results for a different clip
are rejected; missing or malformed metadata is not silently treated as a replay.

For a bounding-box preview, install Pillow and use the measured output:

```sh
python3 tools/render_tracks.py --video /absolute/path/cctv.mp4 \
  --tracks .demo-runtime/sam-results/tracks.jsonl \
  --out .demo-runtime/sam-results/preview.mp4 --fps 10 \
  --title 'SAM 3.1 tracking' --source-label 'Recorded CCTV clip'
```

Set preview FPS to the exact source FPS recorded in `summary.json`. Boxes and
trajectories are rendered from the measured results; missing frames stay empty.
SAM IDs are scoped to a response. Do not assume persistence through complete
occlusion or across separate requests.

Primary references:
- https://dev.meta.ai/docs/sam/segmenting
- https://dev.meta.ai/docs/sam/reading-segmentation
- https://dev.meta.ai/docs/sam/client-libraries

## Current local verification

A 12-second crop of OpenCV's public real pedestrian sample, `samples/data/vtest.avi`,
was processed without a replay manifest. Source:
https://github.com/opencv/opencv/blob/4.x/samples/data/vtest.avi

The corrected pipeline measured 120 decoded/inferred frames, detections on all 120,
575 person detections in total, up to 7 detections per frame, 23 allocated track IDs
and 16 events for an explicitly configured demonstration zone. These are observed
sample results, not ground-truth accuracy scores. The 23 IDs are not a count of
unique people; fragmentation can retrigger events. The CPU probe processed about
4.58 frames/second, below the clip's 10 FPS playback rate.

A separate **real Meta SAM 3.1 API request** on those same 120 frames returned
tracks on all 120 frames and 8 response-scoped object IDs (up to 8 objects in a
frame). The API usage reported 120 `video_frames_processed`. At the documented
$0.20/1,000-frame rate, that request's listed usage cost is $0.024, covered by
existing free credits. The preview uses those returned boxes and IDs. These counts
are not a ground-truth accuracy comparison with YOLOX and do not establish identity
through full occlusion. No additional inference is needed to evaluate the saved
tracks against the demonstration zone.
The edge agent consumed this source-bound result, evaluated all 120 frames and
wrote 6 zone events with `modelVersion: sam-3.1`, then exited successfully. This
post-processing used the saved result and incurred no additional API request.

This sample is not footage from the user's network. Its input, per-frame measurements
and annotated preview remain in ignored `.demo-runtime/real-footage/`. The existing
submitted hackathon video remains a synthetic/replay demonstration; this verification
does not retroactively change its footage or claims.
