# Weights provenance (pilot freeze)

Status: **pilot candidate, NOT the production freeze.** The full benchmark
procedure in `docs/edge-benchmark.md` (§2–§5: sweeps, dataset mix, precision/
recall, latency distributions) is still required before any production claim.
What is recorded here is provenance + checksum + input contract only.

## Frozen candidate: `models/yolox_s_416x416.onnx`

- Architecture: YOLOX-S (decoupled head, strides 8/16/32), COCO 80 classes.
- Source weights: Megvii YOLOX (`Megvii-BaseDetection/YOLOX`), Apache-2.0.
- This file: ONNX conversion from PINTO_model_zoo `132_YOLOX`
  (`resources_s.tar.gz` via `download_s.sh`), conversion scripts MIT, source
  model subject to the provider (Apache-2.0) license per that repo's LICENSE.
- Bundle URL: `https://s3.ap-northeast-2.wasabisys.com/pinto-model-zoo/132_YOLOX/resources_s.tar.gz`
- File in bundle: `yolox_s_416x416/yolox_s_416x416.onnx` (35,858,647 bytes).
- SHA256: `5515a6d732986dea8c9d1241dc5464c3eb161048e8fd959314755e5b5496c299`
- Verified session metadata (ONNX Runtime 1.22.0, observed, not claimed):
  input `images` float32 `[1,3,416,416]`; output `output` float32 `[1,3549,85]`
  (3549 = 52²+26²+13² anchors; raw pre-NMS rows: cx,cy,w,h + objectness +
  80 class scores — matches `YoloxOutputParser`).
- Preprocessing contract: letterbox to 416×416 square, pad 114 gray, raw
  0..255 pixels (`--pixel-divisor 1.0`), NCHW float32. No mean/std shift.
- Run: `edgeagent --model models/yolox_s_416x416.onnx --model-sha256 <above>
  --pixel-divisor 1.0 --input-size 416 [...]` (checksum enforced at startup).

## Rejected alternative in the same bundle

- `yolox_s_HxW.onnx` (SHA256 `41b2a9f21da5bb9f3014030119971da3bb3585a974ba7dbef03ef78b8905cfb1`,
  dynamic spatial axes): works but dynamic shapes complicate latency
  characterization; static 416 preferred for the pilot freeze.
