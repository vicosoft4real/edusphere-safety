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
This adapter is not yet wired into the incident dashboard or rule-event ingestion.
It handles recorded clips, not continuous camera streaming or cross-clip identity.

Set `MODEL_API_KEY` in the process environment or in the git-ignored file
`.demo-runtime/meta-api.env`. Never put keys in source, argv, screenshots or chat.
The directory is already ignored. Limit local file access to its owner.

```sh
python3 tools/sam_tracking.py --video /absolute/path/cctv.mp4 \
  --out .demo-runtime/sam-results
```

The command uploads video to Meta and incurs API usage; verify your credits and
spending limit before running it. No API key is configured in the prepared local
environment, so real SAM inference has not yet been verified. Unit tests cover the
documented request/response boundary only. An API failure produces an error rather
than invented fallback tracks. The client has no automatic retries.

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

This sample is not footage from the user's network. Its input, per-frame measurements
and annotated preview remain in ignored `.demo-runtime/real-footage/`. The existing
submitted hackathon video remains a synthetic/replay demonstration; this verification
does not retroactively change its footage or claims.
