# Demo media attribution

The updated SAM 3.1 demo includes a 12-second excerpt of real pedestrian footage
from OpenCV's `samples/data/vtest.avi`:

- Source: https://github.com/opencv/opencv/blob/4.x/samples/data/vtest.avi
- Downloaded source SHA-256: `45cddc9490be69345cbdab64ca583be65987e864ca408038e648db99e10516cf`
- Extracted 12-second clip SHA-256: `206ac2675a2018fc02814b448c51c73230410d90b6a95f08fbde3aa3d76b342d`
- Upstream license file: https://github.com/opencv/opencv/blob/4.x/LICENSE
- A copy of the upstream Apache 2.0 license is included in
  [licenses/OpenCV-APACHE2.txt](licenses/OpenCV-APACHE2.txt).

Modifications: a 12-second crop and MP4 conversion; measured SAM 3.1 box/ID/trail
overlays; source and timing labels; scaling and placement within the narrated
demo. A smaller transcoded copy of the same recording is used for in-app evidence
playback. The tracking preview uses actual saved API output, not scripted boxes.

The sample is not footage from the entrant's network or a deployed school.
Object IDs are scoped to the model response and do not identify people. No child
classification, injury, intent, or actual supervision determination is shown.

The software code is MIT-licensed. The upstream sample footage retains its upstream
terms; the project does not claim ownership of it or relicense it under MIT.
Application captures show the running local project. Presentation graphics and
the narration script were created for this entry. English narration is synthesized.
