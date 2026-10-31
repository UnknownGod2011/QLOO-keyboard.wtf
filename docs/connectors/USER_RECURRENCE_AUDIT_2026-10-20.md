# User recurrence audit — 20 October 2026

**21 October update:** the isolated connector correction is installed as **a817bfd** and its installed-assembly replay passed. Translation clipboard intent was confirmed; a real translation/clipboard control passed its product workflow, but the original incident is not reproduced. See [PRIVATE_RECURRENCE_DELIVERY_2026-10-21.md](PRIVATE_RECURRENCE_DELIVERY_2026-10-21.md) for current results, test-harness corrections, protected installation and remaining limits. The audit below preserves the original findings.

This supersedes the earlier completion wording for the newly reported failures. Passing the previous replay did not establish that all natural follow-ups or unrelated desktop operations worked.

## Installed identity

The physical read-only snapshot at 13:55:50 UTC confirms private GUI PID 23228, started 13:25:14 UTC, running **1.4.21 / cdd33d3**. EXE SHA-256: `78B29E8003AB80014D83DFEBA88DB664CD9BD67081130FC0745C94E484AF66B8`, matching the reviewed package. PID 2192 is Chrome's native host, not a second/public GUI. Helper/current-tab connected; five registered hotkeys present. Public EXE remains `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.

No installation, app restart, configuration change, camera capture or browser mutation occurred during this audit. Existing rollback archives remain preserved. Local correction **cb27137 is not installed**.

## Connector root cause and correction

At 19:09 IST a successful native read completed its execution authority. The next `connector_execute` was rejected with `terminal_task state=completed`; a co-proposed Browser Automation call was separately rejected as unnecessary. This was a real application failure, not a wrong installed version.

User-supplied follow-up: “give me more details on that and open the thread in a new tab.”

`ConnectorFollowupIntent.MailObject` accepted `it`, but required an explicit mail-object noun after `that`/`this`. The read clause therefore failed the existing compound-operation admission, and its proposed read reached terminal-task rejection. The same grammar exists in **fb0a067**, **28e5489** and **cdd33d3**: an older unhandled case, not a new cdd33d3 difference.

The only production change adds bare `that` and `this` alongside `it` in that reference grammar. Existing validated account/resource selection, fresh read authority, permissions, URLs, provider queries and cancellation remain intact. No email names or complete command sentences are hardcoded. No speech, translation, tab, camera, overlay or permanent-prompt change.

Before correction: **3 failed / 12 passed**, including the exact sentence and two related demonstrative-reference cases. After correction: **297 passed / 0 failed** in focused compound-operation, conversational-reference, admission and generic-recovery tests. Negatives cover explicit selected text, other resources/accounts and writes. Original failures are preserved.

## Real-provider evidence

One same-session replay used the saved Daisy recording through the product receive/dispatcher path, then the user's exact natural-language text and a later factual follow-up. Gemini created the tool proposals; no corrected search query was supplied. Real Gmail and actual product WaveOut were used, with passive device-read capture.

Built DLL SHA-256: `F34028A96516555A30184EEBD306E579BDC58F07C0D8258215954A8D9EFD2BDC`. Built from 32f2dbe plus the exact production change subsequently committed as cb27137. This is built-assembly evidence, not new installed-build evidence.

| Request | Native reads / extra reasoning | Reply complete / device drained | Result |
| --- | --- | --- | --- |
| Recorded Daisy search | 1 / 0 | 35.46 / 35.79 s | Correct conversation summary |
| Exact combined details/open sentence | 1 / 0 | 28.52 / 28.85 s | Details; one validated exact-thread destination |
| What did she say most recently? | 0 / 0 | 13.10 / 13.42 s | Answer from retained evidence |

These totals include speech. Native search tool elapsed 3.28 s; combined proposal collected its owned result in 1.23 s; final retained receipt took 1.86 ms. UI ownership cleared after every turn; protected settings/mappings/Personal-key file hashes stayed unchanged.

The destination matches the previously user-confirmed Daisy conversation. The launcher captured validated dispatch without opening a browser: **Chrome rendering was not observed**. Offline transcription of WaveOut-consumed bytes confirms the complete combined recap through its final sentence. That does not establish speaker acoustics or live microphone behavior. Follow-up inputs were text, not new recordings.

## Other reported behavior: read-only findings

| Issue | Evidence | Verdict |
| --- | --- | --- |
| Photograph delay | `take_photo` proposal 19:07:48.801; result 19:08:05.072: **16.27 s inside execution**. Webcam service, capture method and dependency declarations unchanged from fb0a067. Existing native driver/open/read calls have no per-stage timing. | Delay confirmed; driver/resource cause unproven. No new photo or code change. |
| Screen analysis | Capture/encoding under 300 ms. First Personal image request waited **24.29 s for headers** before provider tool cancellation; next request returned **HTTP 503** after 3.01 s. Screen guidance implementation unchanged. | Provider wait/error confirmed, not evidence of a slower capture implementation. No retry changes. |
| Translation | Selection repeatedly captured: **6,667 characters**, **182–199 ms**. Full source retained; separate model-facing context bounded. No translation-start event; progress later proposed `connector_control`. | Failed route confirmed; not the 50,000-character admission limit. Clipboard wording pending. No translation change. |
| Overlay behind windows | Source identical to fb0a067/28e5489; `TopMost=true` and `WS_EX_TOPMOST` still present. No incident-time z-order trace. | Physical failure reported, cause unverified. No positioning change. |

The unchanged translation background operation requires translation plus clipboard copy; translate-only uses the existing conversational route. “Local reference is lost” is not a local error literal. This does not prove that the observed failure was harmless or that the model had sufficient context.

Later snapshot: approximately 1,032 MiB free RAM and 41% CPU load. This cannot establish load at the earlier photograph.

## Continuation and evidence

All focused tests, real replay and offline transcription are terminal. Installed cdd33d3 remains unchanged; cb27137 is a local correction. Public readiness is superseded by this recurrence.

Next: resolve the pending translation/clipboard wording; reproduce only that route if needed. Do not alter camera/overlay/audio/tabs speculatively. For private delivery, package the frozen correction with the existing guarded handoff, preserve cdd33d3 rollback, compare installed/package hashes and protected files, and retain physical verification limits. Do not repeat the broad suite or claim universal regression freedom.

Evidence root: `.artifacts/connectors/voice-followup-recurrence-20261018/user-recurrence-20261020-135550/`.

- `snapshot.json`, `last-hour.txt`, original private log tail.
- `red.trx`, `green.trx`, logs and terminal receipts.
- `live-exact-followup/acceptance.json`, actual proposals/receipts, device/UI metadata, `tested-build.json`.
- `live-exact-followup/offline-combined/whisper-transcription-private.json`.
# 21 October continuation — rejected human check overrides replay

The a817bfd human check failed (finished-task refusal and no exact opening). Restored cdd33d3 with 24 payload/29 protected-file matches; private GUI 21032, PersonalOnly, all five hotkeys and Helper verified. A new two-activity spoken-request reproduction fails on unchanged code and passes after restoring accumulated-request admission with the older interruption-race fallback intact (181 focused checks). No tab executor, translation engine, camera or overlay changes. See CURRENT_CONNECTOR_STATUS.md for authoritative current state and preserved evidence.

