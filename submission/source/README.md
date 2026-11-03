# Demo production sources

The release MP4 is built from real browser recordings, deterministic synthetic
footage, original presentation graphics and synthetic English narration.

1. Follow the root README to build and launch the project.
2. Run `prepare-demo.py setup` once, then `prepare-demo.py ingest`.
3. Run `verify-demo.py` to check the real boundary, including local Llama.
4. Install Playwright and its video recorder, or set `PLAYWRIGHT_MODULE` in
   `record-demo.cjs` to your installed module. The recorded build used the installed
   Google Chrome channel. Run `node submission/source/record-demo.cjs`.
5. Install `edge-tts` in an isolated Python environment and run `narrate.py` with
   that interpreter. It sends only the written narration to the speech service.
   On macOS the script falls back to the local Daniel voice if necessary.
6. Install Pillow for your system Python; ensure FFmpeg and FFprobe are in PATH.
   Run `python3 submission/source/render-video.py`.

`scenes.json` is the editable 180-second script and timing source. The renderer
generates the video, a matching `.srt`, a narration document, a poster, and an
autoplay storyboard HTML file. Graphic fonts use macOS Avenir Next and Menlo;
change those paths for another OS.

The source recordings last longer than the final shots to permit clean trimming.
The Llama shot retains the actual request click and generated result, while
omitting the middle of the inference wait. The on-screen label discloses this.
Synthetic footage uses manifest-replayed detections; it does not present the
simulator's rectangles as neural-network detections.

`SKIP_EXISTING=1` skips already recorded scenes. `RECORD_ONLY=llama` records just
the model scene. Re-running the response scene requires a fresh New incident,
because the real state machine does not reverse a Resolved incident.

The `.demo-runtime/` directory contains local demo credentials and is ignored.
`submission/assets/`, `recordings/`, `render/` and the Python virtual environment
are also ignored. No model weights or local camera footage are shipped with the
repository. The synthetic footage generator is the existing project Simulator.
