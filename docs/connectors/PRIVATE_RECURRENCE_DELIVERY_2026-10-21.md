# Private recurrence delivery — 21 October 2026

## Superseding human verdict and rollback

**REJECTED.** The actual microphone/browser check failed: Jarvis reported the task finished and could not open the thread. The user also reported unreliable tab behavior. The logs show the positional guard refusing two proposals (`wholeRequestOwned=false`, `noTabClosed=true`) while the Helper remained connected; they do not establish an executor regression or justify weakening the guard. The passing replay below used complete typed follow-ups and did not cover the observed split speech activities.

One controlled rollback restored **cdd33d3**, GUI PID 21032 at 21 October 00:45:38 IST. EXE SHA-256 `78B29E8003AB80014D83DFEBA88DB664CD9BD67081130FC0745C94E484AF66B8`, DLL `B713A95F0ADB5780CFEB0D6F8005CAF27BC3152CE50CB69F799AC779723A6D75`. All 24 payloads and 29 protected profile files matched. PersonalOnly, five hotkeys and connected Helper confirmed. Source and rejected a817bfd archive retained. This restores the prior baseline, not a claim its connector defect is repaired.

New speech-segmentation regression and focused correction are recorded at the top of CURRENT_CONNECTOR_STATUS.md. The rest of this document preserves the original delivery evidence and limitations; it is **not current release acceptance**.

## Current verdict

Private **1.4.21 / a817bfd** is installed and running, containing correction **cb27137**. One guarded installation completed at 00:34 IST. The previous **cdd33d3** package remains the immediate rollback. This is a targeted connector repair, not a claim that all reported desktop incidents are resolved or that public promotion is ready.

The original failures occurred on the expected cdd33d3 installation. They were not caused by a different GUI version running. The second executable was Chrome's native host.

## Confirmed defect and smallest correction

The exact human request was: “give me more details on that and open the thread in a new tab.”

`ConnectorFollowupIntent.MailObject` accepted `it` but required a mail-object noun after `that` or `this`. The compound read/open parser consequently rejected the read clause. A proposed `connector_execute` then reached a completed task and returned `terminal_task`; the companion Browser Automation proposal was rejected as unnecessary.

This grammar also existed in **fb0a067** and **28e5489**. The earlier successful replay used a more explicit reference and missed the defect. The correction adds bare `that` and `this` beside `it` in the existing reference grammar. Verified account/resource identity, fresh read authorization, exact URL construction, cancellation and write permissions remain unchanged.

**Only production file changed from cdd33d3:** `src/Services/Connectors/ConnectorFollowupIntent.cs` (one constant and its explanatory comment). Regression cases were added to `ConnectorCombinedRetainedOperationTests.cs` and `ConnectorConversationalFollowupIntentTests.cs`. No permanent prompt, tool catalog, backend deployment, speech, tab, translation, camera, overlay, hotkey or authentication implementation changed.

Before correction: **3 failed / 12 passed**, including the exact sentence and two related references. After correction: **297 passed / 0 failed** in the focused compound, reference, admission and recovery tests. Original failures remain preserved. Earlier broad evidence is reused, not relabeled as a fresh clean broad run.

## Installed-assembly acceptance

The actual installed executable was extracted and its DLL verified against the archived package and the earlier real-provider test. One bounded same-session replay then used that installed DLL, the saved Daisy recording, the exact combined request above, and “What did she say most recently?”

Gemini generated the tool proposals; no corrected search query was supplied. Real Gmail and the product WaveOut device were used. Follow-up inputs were submitted text, not microphone recordings.

| Turn | Native reads / secondary reasoning | Reply complete / device drained | Observed result |
| --- | --- | --- | --- |
| Recorded Daisy search | 1 / 0 | 28.55 / 28.88 s | Correct conversation and summary |
| Exact combined details/open request | 1 / 0 | 27.25 / 27.58 s | Full details and one validated retained destination |
| Most recent message | 0 / 0 | 11.45 / 11.77 s | Correct answer from retained information |

These totals include spoken output. Search resolve/read/finish took **0.831 / 2.870 / 0.105 s**; details resolve/read/finish took **0.571 / 1.666 / 0.139 s**. No additional reasoning call. A companion `browser_action` proposal collected the existing retained-open receipt in **1.37 ms**, without a second destination or Browser Automation task.

All three UI owners cleared. There were no inactive-task errors, unexpected provider interruptions, missing playback sinks or buffer-overflow events in this replay. Provider/device audio counts matched for each turn. Independent offline recognition of the actual WaveOut-consumed combined recap confirmed its complete middle and final sentence; it was not just generated text or unconsumed PCM.

**Limits:** the launcher captured the verified destination without navigating Chrome. Rendered Gmail, physical speaker acoustics and live microphone timing were not observed in this replay. Browser observation was unavailable (the tool's kernel-assets error on 21 October; earlier attempts reported local browser access disabled). A narrow final human check is pending. Existing normal-command timing evidence is reused because that route is unchanged; it was not rebenchmarked.

## Translation, camera, screen and overlay

| Reported issue | Evidence and current status |
| --- | --- |
| Translation captured text but no dynamic job | Incident logs show 6,667 characters captured in 182–199 ms and no translation-start event. User confirmed clipboard intent. The exact full spoken sentence remains unavailable; the original failure is **not reproduced or resolved**. It was below the unchanged 50,000-character admission limit. |
| Translation control | The unchanged engine was exercised through real Gemini tool selection, the product dispatcher, translation provider, clipboard read-back, UI activity and lifecycle speech. A complete isolated 10,077-character selection produced 10,562 French characters, all 60 sections, and the translated start/end boundaries. One provider call, 26.584 s; clipboard verified and restored; UI owner cleared. Selection was supplied by the harness, not captured from Devpost. |
| Failed translation test assertion | The harness incorrectly required literal English `START`/`END`; the output translated these to `DÉBUT`/`FIN` while preserving their identifiers and all sections. Original failure and separate reviewed outcome are retained. No product change or repeated provider call was used to conceal it. |
| Photograph delay | Confirmed **16.27 s inside capture execution**. Webcam source is byte-identical to fb0a067 and cdd33d3. Existing logs cannot separate native driver/open/read delay from resource contention. No camera code or new camera capture. |
| Screen analysis | Capture/encoding under 300 ms. First Personal request waited **24.29 s** before provider tool cancellation; the next returned **HTTP 503** after 3.01 s. Screen guidance source is identical to fb0a067. Provider wait/error is confirmed; why the interruption occurred is not established. |
| Overlay behind windows | Overlay source is identical to fb0a067 and cdd33d3; topmost flags remain. No incident-time z-order trace, so the reported physical failure remains **unverified**, not fixed. |

Source object IDs for the overlay, webcam, screen guidance, translation operation and microphone control are recorded in `protected-source-comparison.json`; all match fb0a067 and cdd33d3. This does not prove every runtime interaction is unchanged.

## Test-environment corrections

The translation harness refreshed the same account session the running GUI held. Its successful rotation was followed by repeated GUI HTTP 409 refresh conflicts. This was a test-induced runtime problem, not evidence of a new production authentication change. It is recorded rather than hidden.

The final replay uses an isolated token-store path, reads an app-owned fresh access snapshot, and explicitly rejects test-side refresh requests. During the controlled handoff, the new GUI loaded the persisted session and successfully refreshed it (HTTP 200), then fetched account/readiness successfully. Final replay preserved settings, mappings, Personal-key files **and the session-token file**. No sign-out, account switch or credential editing was performed.

The first package-contract check also failed because the harness expected a Git SHA in `AssemblyInformationalVersion`. The project explicitly sets `IncludeSourceRevisionInInformationalVersion=false`; version `1.4.21` is intentional. All behavioral checks had passed. The corrected check binds the package manifest, extracted DLL hash and version; its result and the original failed assertion are both preserved. No product rebuild was needed.

All helper launches in this continuation use hidden process creation and saved error/terminal receipts. No new service, startup hook, process manager or public-app restart was introduced.

## Installation and rollback

- Installed package source: **a817bfdccd5dcb0270e686836c5bad1bd89e35b6**; production fix **cb27137**.
- Running private GUI: **PID 26480**, started 21 October 00:34:07 IST; one GUI, with a separate Chrome native host.
- EXE SHA-256: `38741C77F27BD0636EDEB8388136A5A48B0D187745510060E8F01A8A5FD42F13`.
- Installed/tested DLL SHA-256: `F34028A96516555A30184EEBD306E579BDC58F07C0D8258215954A8D9EFD2BDC`.
- Installer SHA-256: `1890ACFE0389A35E7E6B24F19C23CC3823C01BABCD1C05C9AB99063C1865C4E2`.
- Immediate rollback: `dist/connector-candidate-archives/cdd33d3-reviewed`; earlier archives preserved.
- **24 payload files verified; 29 protected profile files unchanged** across installation. Public EXE remains `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.
- PersonalOnly and all five saved hotkeys verified. Helper connected, version 2.1.21. Current-tab content automation was unavailable at this snapshot; no new browser/tab success claim.
- Public GUI lease acquired read-only during handoff and identity/hash/metadata preserved. No public GUI/native host stopped, no registration change, no forced termination.
- Backend remains staging `00033-tip`; public promotion remains gated separately.

## Evidence and next action

Evidence root: `.artifacts/connectors/voice-followup-recurrence-20261018/recurrence-final-delivery-20261020/`.

Key receipts: `package-contract/` original failed assertion, `package-contract-v2/` corrected hash-bound check, `archived-a817bfd/extraction-proof.json`, `installed-current-recurrence-a817bfd/extraction-proof.json`, `recurrencefinal20261020-installation-boundaries.json`, `recurrencefinal20261020-guarded-handoff-v3.json`, `installed-exact-followup/acceptance.json`, device/proposal/UI traces, and `installed-exact-followup/offline-combined/whisper-transcription-private.json`.

Original failure/reproduction and translation control remain under `user-recurrence-20261020-135550/`. Final physical runtime snapshot: `user-recurrence-20261020-190604/`.

All build/install/provider/offline-review processes are terminal. No further installation or broad test run is planned. Await the narrow actual-browser/live-microphone combined-follow-up check. Translation's original routing incident, camera cause and overlay z-order incident remain explicitly unresolved; do not add speculative corrections or assert global readiness.
