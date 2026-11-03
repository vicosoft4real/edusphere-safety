# Submission validation — November 3, 2026

These are measured results from the working checkout used to record the video.
Earlier sandbox-era notes in `docs/` describe their original environment and are
not a substitute for this verification.

| Check | Result |
| --- | --- |
| `dotnet build EduSphere.Safety.slnx --no-restore -v minimal --disable-build-servers` | Passed; 0 warnings, 0 errors |
| Full `dotnet test tests/EduSphere.Safety.Tests --no-build` | 509 passed, 0 failed, 0 skipped; PostgreSQL Docker integration tests included |
| `npm run typecheck` | Passed |
| `npm run lint` | Passed |
| Isolated `npm run build` | Passed; all routes generated, including the new briefing endpoint |
| Synthetic simulator / edge run | 60 decoded frames, 1 zone event, 1 queued event |
| Ingest retry | Same incident ID returned for both requests |
| Synthetic evidence manifest/upload | Clip and snapshot accepted by the real API |
| Evidence web proxy | HTTP 200 `video/mp4`; bytes identical to original 15,443-byte synthetic clip |
| Anonymous evidence and briefing | Both refused with HTTP 401 |
| Other-tenant briefing | Refused with HTTP 404 |
| Nonmember briefing | Refused with HTTP 403 |
| Real Meta Llama inference | HTTP 200; `llama3.2:latest`; structured summary, review steps and unknowns |
| Client-supplied tenant/prompt | Ignored; route retrieves authorized incident facts server-side |
| Staff UI workflow | Acknowledge → Investigating → Resolved completed through the real web proxy/API |
| Main web pages | Overview, devices, cameras, rules, policies, diagnostics and incidents loaded against the real API |
| Local email transport | Worker wrote `.eml` files to the local pickup directory; no external email sent |
| Physical CCTV discovery | No ONVIF responder found; no live-camera validation claimed |

The first production-build attempt ran against the active development output
directory and failed because Next.js development and production processes shared
`.next`. We moved production verification to an isolated copied web directory; the
complete isolated build then passed. The recording server was restarted with a
clean development cache before the successful walkthrough.

The production build reports the existing warning that the Next.js ESLint plugin
is not detected by its integrated lint step. The standalone lint and type checks
pass, and this warning does not stop the build.

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

The video explicitly shortens the model wait. No benchmark for inference latency,
detector accuracy, camera capacity, false alarms or safety outcomes is claimed.

## Reproduce the extra live checks

After running the local demo setup and ingest commands:

```sh
python3 submission/source/verify-demo.py
```

The recording script also asserts exact evidence bytes, anonymous refusal, actual
Llama output in the UI and successful resolution. Runtime keys and raw logs remain
in ignored local folders. Generated media is published as release assets rather
than committed into the source tree.
