# Demo production sources

The updated SAM demo combines actual application screenshots, real OpenCV
pedestrian footage with actual Meta SAM 3.1 API track overlays, original graphics
and synthetic English narration. The older `record-demo.cjs` / `prepare-demo.py`
path documents the archived synthetic replay demo, not the current video.

1. Launch the API, PostgreSQL, worker, web console and local Llama using the root
   README. Follow `docs/real-footage.md` for the verified SAM source/results setup.
2. Run `python3 submission/source/prepare-sam-demo.py`. It reuses the existing
   source-bound SAM result without making a model request; creates a fresh device
   and recorded-sample camera descriptor; runs rules; ingests one event twice;
   and uploads a transcoded playback copy plus snapshot. No physical camera is
   represented as connected. Runtime credentials are saved only in ignored files.
3. Run `SAM_DEMO=1 python3 submission/source/verify-demo.py` for access, playback
   and real local Llama checks. This calls local Ollama, not hosted Meta inference.
4. Capture the actual web workflow: overview, published rules, the selected SAM
   incident, evidence, local Llama result, acknowledgement, investigation,
   resolution, notification policy and diagnostics. The current captures were
   made through the authorized browser UI. `scenes.json` lists exact image names
   and durations. The Llama wait is shortened and labeled. Do not capture API keys,
   model account pages or runtime credential files.
5. Install `edge-tts` in an isolated environment and run `narrate.py`; only the
   written narration is sent to the speech service. macOS Daniel is a fallback.
6. With Pillow, FFmpeg and FFprobe installed, run
   `python3 submission/source/render-video.py`. `RENDER_ONLY` can select scenes.

`scenes.json` is the editable 180-second timing source. The output is
`submission/edusphere-safety-sam31-demo-3min.mp4` (2:59), plus SRT, narration,
poster and storyboard. The SAM preview plays 12 seconds at normal speed then
holds the last frame. The renderer uses macOS Avenir Next and Menlo fonts.
A fresh New incident is required to recapture the response workflow.

`.demo-runtime/`, `submission/assets/`, recordings, render segments, generated
MP4s, speech cache and Python environments are ignored. The public source contains
no keys or raw SAM response; store `MODEL_API_KEY` only in the ignored mode-600
`.demo-runtime/meta-api.env`, never on a command line. See the media attribution
file for the public sample's origin and upstream terms.
