# Submission validation — November 3, 2026

These are measured results from the working checkout used to record the video.
Earlier sandbox-era notes in `docs/` describe their original environment and are
not a substitute for this verification.

| Check | Result |
| --- | --- |
| `dotnet build EduSphere.Safety.slnx --no-restore -v minimal --disable-build-servers` | Passed; 0 warnings, 0 errors |
| Full `dotnet test tests/EduSphere.Safety.Tests --no-build` | 518 passed, 0 failed, 0 skipped; PostgreSQL Docker integration tests included |
| `npm run typecheck` | Passed |
| `npm run lint` | Passed |
| Isolated `npm run build` | Passed; all routes generated, including the new briefing endpoint |
| Real recorded SAM / edge run | 120 frames; tracks on 120; 8 response-scoped IDs; 6 demonstration-zone events |
| Python SAM adapter tests | 6 passed |
| Ingest retry | Same incident ID returned for both requests |
| Real recorded evidence manifest/upload | Transcoded playback copy and snapshot accepted by the real API |
| Evidence web proxy | HTTP 200 `video/mp4`; bytes identical to uploaded real-recording playback copy (SHA-256 `479d8a270434ef39697521ed890276390286912d19fd84c83053bec0e27117c2`) |
| Anonymous evidence and briefing | Both refused with HTTP 401 |
| Other-tenant briefing | Refused with HTTP 404 |
| Nonmember briefing | Refused with HTTP 403 |
| Real Meta Llama inference | HTTP 200; `llama3.2:latest`; structured summary, review steps and unknowns; correct recorded-sample location |
| Client-supplied tenant/prompt | Ignored; route retrieves authorized incident facts server-side |
| Staff UI workflow | Acknowledge → Investigating → Resolved completed through the real web proxy/API |
| Main web pages | Overview, devices, cameras, rules, policies, diagnostics and incidents loaded against the real API |
| Local email transport | Worker wrote `.eml` files to the local pickup directory; no external email sent |
| Physical CCTV discovery | No ONVIF responder found; no live-camera validation claimed |
| Final video | 179.89 seconds; 1920×1080, 30 fps, H.264/AAC; English captions; full decode check passed |
| Production package audit after patch | 0 vulnerabilities reported by `npm audit --omit=dev` |

The first production-build attempt ran against the active development output
directory and failed because Next.js development and production processes shared
`.next`. We moved production verification to an isolated copied web directory; the
complete isolated build then passed. The recording server was restarted with a
clean development cache before the successful walkthrough.

The production build reports the existing warning that the Next.js ESLint plugin
is not detected by its integrated lint step. The standalone lint and type checks
pass, and this warning does not stop the build.

GitHub flagged inherited dependency advisories after the first publication. We
updated Zod to the patched 4.6 line, overrode PostCSS to 8.5.32 and updated ESLint
within version 9. The lockfile is committed. Type checking, lint, the isolated
production build and the original nine live boundary checks passed after those changes.
The production dependency audit reports zero vulnerabilities. This does not claim
that the separate development-tool dependency tree has no advisories.

## Browser/API defects corrected while preparing the demo

- Development login now reads the API's actual `accessToken` response.
- The web catch-all proxy no longer doubles the `/v1` prefix.
- Evidence redirects are handled manually and kept relative, preserving the
  browser's original hostname and session cookie.
- The proxy forwards the response body as a stream instead of decoding binary
  evidence as text.
- Evidence references are URL-encoded in incident links.
- Overview coverage now counts expired heartbeats as degraded, consistent with
  the status label shown for each device.

## Model evaluation boundary

We exercised actual local Llama inference, rather than a stub. These requests are
smoke tests for the integration and access boundary, not a model quality benchmark.
The initial prompt produced an overconfident description of unauthorized access.
We tightened it to distinguish a rule event from confirmed wrongdoing and verified
the revised output. Model drafts can still be wrong and require staff review.

A later repeat inference returned HTTP 503 after the configured 120-second
timeout on the local CPU model server. The recorded successful calls and earlier
live validation remain valid; this also demonstrates the unavailable path.
Local model responsiveness is variable and requires production validation.

The video explicitly shortens the model wait. No benchmark for inference latency,
detector accuracy, camera capacity, false alarms or safety outcomes is claimed.

## Reproduce the extra live checks

After running the local demo setup and ingest commands:

```sh
python3 submission/source/verify-demo.py
```

For the updated SAM incident, run `SAM_DEMO=1 python3 submission/source/verify-demo.py`.
All ten live checks passed, including the location regression check. Database
verification confirmed Resolved at version 3, model `sam-3.1`, three staff activity
records and two uploaded evidence items. The briefing prompt was corrected after
an actual output copied an unrelated example location; the revised real output
uses the authorized incident location. Web lint and type checks passed afterward.
The earlier isolated production build predates this small prompt revision.

The new MP4 passed a complete FFmpeg decode check and was uploaded Unlisted to
YouTube at https://www.youtube.com/watch?v=lX3EbPyyavw. Watch playback reports 2:59.
The existing Cerebral Valley entry was updated and its new URL and explanations
verified after reload. No additional hosted SAM calls were made for this edit.
Runtime keys, raw logs, source media and captures remain in ignored local folders.
The source repository contains the script, captions, attribution and measured media
metadata, while the revised MP4 is distributed through the requested YouTube link.

## Publication secret check

Gitleaks found zero secrets in the proposed source export and all 27 reachable
commits. An exact comparison of the configured Meta key against source files and
323 historical Git blobs found zero matches. The key file remains ignored with
mode 600. Credentials and raw runtime assets are excluded from the commit.
See [redacted aggregate results](security-validation.json).
