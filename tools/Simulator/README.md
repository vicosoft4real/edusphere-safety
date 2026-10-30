# Simulator (test-only synthetic footage)

Generates deterministic synthetic camera footage for the edge pipeline proof.
NOT shipped to schools. Not part of any deployment package.

## Usage

```sh
dotnet run --project tools/Simulator -- --scenario walker-crossing-zone --out /tmp/sim
dotnet run --project tools/Simulator -- --list
dotnet run --project tools/Simulator -- --scenario dropout-gap --out /tmp/gap --skip-mux
```

Options: `--scenario`, `--out`, `--seed` (default 7), `--frames` (default 40),
`--fps` (default 5), `--width` (default 640), `--height` (default 480),
`--ffmpeg` (default `ffmpeg`), `--skip-mux` (PNGs + manifest only).

Output layout:

```text
<out>/png/f0000.png …   rendered frames (dropout-gap omits indices 10–14)
<out>/manifest.json      ground-truth manifest (schema v1, below)
<out>/sim.mp4            H.264 MP4 muxed as
  ffmpeg -y -framerate N -i f%04d.png -c:v libx264 -pix_fmt yuv420p \
    -movflags +faststart sim.mp4
```

(The `-y` is the only deviation from the canonical argv: reruns must never block
on an overwrite prompt. Dropout gaps are staged under sequential names first, so
the image2 pattern still applies.)

## Scenarios

| Name | Content |
|---|---|
| `empty` | Corridor only, no persons (negative control). |
| `walker-crossing-zone` | One person walking left to right through the corridor-center zone. Pipeline proof scenario. |
| `loiterer` | One person standing inside the zone (dwell). |
| `group` | Three persons crossing (two left to right, one right to left). |
| `bidirectional-boundary` | One person crossing x=0.5 left to right, then back. |
| `dropout-gap` | Walker with sequence indices 10–14 missing: no PNGs, no manifest entries. Numbering jumps 9 to 15. |

Rendering: static corridor background (wall/floor + seeded speckle), one filled
rect per person (fixed palette by person order), and a font-free frame-counter
burn-in (16-block binary index, top-left). No fonts, no text shaping: output is
byte-deterministic per (scenario, seed, frames, fps, size).

## Manifest schema (`edusphere-sim-manifest/1`)

```json
{
  "format": "edusphere-sim-manifest/1",
  "scenario": "walker-crossing-zone",
  "seed": 7, "width": 640, "height": 480, "fps": 5,
  "frames": [
    { "frameIndex": 0, "boxes": [{ "x": 0.1, "y": 0.55, "w": 0.12, "h": 0.25 }] }
  ]
}
```

Boxes are normalized 0..1 and MAY extend off-screen (the walker enters/exits);
consumers clamp to the visible frame exactly as a real detector only emits
visible boxes. Same (scenario, seed, frames, fps, size) input yields a
byte-identical manifest; the SHA256 printed at render time is the CI
determinism gate.

## Fidelity boundary (what this proves — and what it does not)

Pixels are NEVER compared: the replay detector ignores decoded frame bytes and
serves manifest boxes in decode order (Nth decoded frame → Nth manifest entry),
so a passing pipeline run proves the decode → track → rules → outbox plumbing
carries real frames end to end, NOT that any visual detector works. Detection
quality (precision/recall, lighting, occlusion) is out of scope until the ONNX
adapter (Plan 2 Task 6) runs on connected CI.

Determinism scope is MANIFEST-ONLY: the byte-identical guarantee covers
`manifest.json` for fixed (scenario, seed, frames, fps, size). Rendered PNGs and
the muxed MP4 may differ across SkiaSharp/FFmpeg versions and platforms — their
bytes are never asserted, only frame count, dimensions, and timestamps.

## Consented-footage swap

Replace synthetic input with real consented footage by dropping in an MP4 plus a
hand-annotated manifest in the schema above. Entries replay in MANIFEST ORDER
against decoded frames in order (Nth decoded frame → Nth entry), so keep manifest
frames in capture order (`frameIndex` ascending) for attribution to line up —
no code changes are needed, point `--manifest` at the annotation file and
`--source` at the MP4.
