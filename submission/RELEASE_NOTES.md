# Updated SAM 3.1 demo

[Watch the Unlisted 2:59 video](https://www.youtube.com/watch?v=lX3EbPyyavw). The previous `hackathon-demo` release
contains the archived synthetic replay cut; use the new YouTube link for judging.

The revised cut shows actual hosted SAM 3.1 tracks on a 12-second real OpenCV
recording, six edge demonstration-zone events, explicit ingestion of one event,
duplicate suppression, authorized playback of a transcoded copy, real local
Llama output, and persisted acknowledgement, investigation and resolution.
Production supervisor calls, SMS and site alarms for critical configured zones,
including pool areas, are explained as planned integrations. Current notification
verification uses local pickup.

Validation: 518 .NET tests, six Python adapter tests and ten live boundary checks
passed. The final H.264/AAC MP4 is 179.887696 seconds and passed full decode.
See [validation](VALIDATION.md), [judge guide](JUDGES_GUIDE.md),
[media metadata](media-validation.json) and [attribution](THIRD_PARTY_MEDIA.md).
This public recorded sample is not local CCTV or a field accuracy benchmark.
