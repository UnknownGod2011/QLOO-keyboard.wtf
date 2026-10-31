# Private stabilization delivery — 21 October 2026

## Installed identity

One controlled installation completed at **20 October 20:07:35 UTC / 21 October 01:37:35 IST**. Private **1.4.21**, source **5b61b99be2302f315b4ef1ae3c945e1e78df5e07**, GUI PID **9844** at handoff. No rebuild was performed during final desktop acceptance. The public application was not installed, stopped or changed.

| Artifact | SHA-256 |
| --- | --- |
| Installed EXE | `6CBB75932836AD1D068E91018F075A2681B052A39ACBE2C1FF29E611F2266C96` |
| Installed embedded DLL, independently extracted | `83796E52ADF2C12104DD0E5F763F035E04063BF0C988351368BBCEF5B339FFB8` |
| Installer | `6A3AF16037EBF6D70CB5E3E6CD7FBA34CA4F3E919523784743C8E28F820B08F3` |

Archive: `dist/connector-candidate-archives/5b61b99-reviewed/`. Immediate rollback: **cdd33d3**, preserved in `dist/connector-candidate-archives/cdd33d3-reviewed/`. Rejected **a817bfd** is not the rollback.

All **24 payloads** matched; **29 protected profile files** and their inventory matched before/after installation, including session-token files. PersonalOnly, the same five registered hotkeys (including the pre-existing Jarvis/Cancel fallbacks), one intended private GUI, Helper 2.1.21 and current-tab availability passed runtime checks. The public executable, its existing single-instance lock identity/bytes/timestamps, startup and Helper registration stayed intact. Native-host processes were left alone.

## Confirmed corrections

1. **Spoken request accumulation — 0e8050f.** `GeminiLiveConversationService` final connector admission previously used the complete accumulated input in fb0a067. The 46c4d17 interruption-race correction changed this to the latest activity only, losing the earlier clause of naturally paused commands. The correction uses the accumulated request when it still ends in the current activity, retaining the activity-local fallback when an older reply has already committed the shared buffer. Two split-activity reproductions failed before the correction and passed afterward. The earlier reply-completion race remains covered.
2. **Retained demonstratives — cb27137.** `ConnectorFollowupIntent.MailObject` accepts bare `this` and `that` alongside `it`. The prior combined details/open parser rejected these objects; this gap existed in older comparison builds too. Verified resource/account authorization and fresh task ownership remain unchanged. No sender names or full user sentences were added to production rules.
3. **Translation admission — 4ef5637.** `SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent` accepts bare `this` as an existing selection reference. Two natural capture/translate/copy proposals reproduced capture-only behavior before and passed after. The translation worker, clipboard verification, dynamic progress UI and 50,000-character admission limit are preserved. This proves a matching defect class; the original failed translation's full transcript was not logged.

Private diagnostic support in **fcb2d02** exposes the existing recording adapter in Connector builds as well as Personal builds, using the real desktop session and microphone callback clock. The original bounded queue utility is restored; public builds exclude it. Its authenticated entry refuses active human sessions and Managed mode. Normal sessions do not activate recording substitution. Conditional guards changed in `GeminiLiveConversationService`, `KeyboardWtfApp`, `WebSettingsService` and their `PrivateRecordingAudit` partials. No normal microphone, VAD, PCM format, playback engine, tab executor or permanent prompt/tool definitions changed for this delivery.

## Verification and preserved failures

- Reused focused source gate: **326 passed, zero failed**. Additional private recording queue contract: **6 passed**. Earlier red split-speech and translation tests remain saved. No 6,000-test rerun.
- Frozen published EXE executed as the actual desktop GUI before installation, with its actual registry, native Gmail, Personal Live, WaveIn clock, WaveOut, account/session authority and Chrome Helper. Installed EXE/DLL bytes match that exact package.
- **Pilot 1:** recorded Daisy search finished speaking, then the observer's eight-second metadata HTTP deadline failed. The completed answer and failed observer receipt are preserved.
- **Pilot 2:** recorded search, then the original combined spoken request with 450 ms inserted silence. Gemini transcribed the complete 103-character request, a fresh native read succeeded, direct verified URL navigation occurred, and the user reported that it worked. Gemini emitted one voice activity in this replay, so it is not additional proof of two-activity ASR. The private recording endpoint then hit its 30-second diagnostic deadline and stopped its owned session before final completion; do not call the entire script a pass or attribute that diagnostic stop to a new playback-engine fault.
- **Pilot 3:** recorded search followed by the user's exact combined text request through the actual desktop's supported text-turn route. Both replies completed, every provider audio chunk reached playback (75/75 and 63/63), the playback buffer drained, and the UI returned to Listening with no long-task owner. No inactive-task error or audio overflow occurred. This run has one recorded turn and one typed turn; it is not a wholly spoken replay.
- Pilot 3's URL assertion failed because Gmail represented the verified hexadecimal native ID as an escaped decimal `#thread-f:` ID. Exact unsigned conversion proves identity equivalence; the original assertion remains failed and a separate reconciliation receipt records the error. Helper was disconnected/stale at that particular metadata sample, so the trace alone is not rendered-page acceptance. The user separately confirmed: **“the current session worked and the tab opened.”**
- Later factual/reopen checks in the interrupted pilot scripts did not execute. Existing installed-DLL multi-turn/fresh-read/reply-ownership evidence and focused regression tests are reused; do not claim a newly completed four- or six-turn desktop script.
- There were no production changes, rebuilds or installations between these pilot attempts. Each failed pilot restored cdd33d3 normally; the final corrected package was installed once after evidence reconciliation and the user's visible-outcome confirmation.

## Timing and scope

The completed Pilot 3 search used a 649 ms resolve and 2,261 ms native execution HTTP header wait; the combined follow-up used 578 ms resolve and 1,419 ms execution. Combined direct URL dispatch preceded first audio by about **0.99 s**; that reply completed approximately **18.07 s** after its first audio. The full search stage took **46.64 s**, including recorded input, provider interpretation, speech and observation; the combined stage took **23.23 s**. These are complete stage measurements, not claims of two-second end-to-end searches. Provider and observation timing varied between runs.

Previous ordinary-command evidence remains **4.371 ms local dispatch / 9.343 ms observation plus dispatch median, zero connector/account/model/provider work**. That ordinary executor is unchanged from cdd33d3; no new same-machine latency benchmark was run. No added planning/reasoning stage or retry was introduced.

## Remaining checks and limitations

| Area | Status at handoff |
| --- | --- |
| Full spoken request / previous interruption race | Fixed and verified by red/green focused tests; actual recorded combined desktop route executed; natural room/microphone split still has narrower evidence than simulated split events. |
| Details plus exact opening | Actual desktop completion and human tab-opening confirmation; native identity verified. Later same-session questions reuse previous evidence. |
| Speech / activity cleanup | Completed desktop replies and drained playback; user heard current run working. No universal acoustic guarantee. Private diagnostic timeout preserved separately. |
| Translation | Corrected admission tests and prior real translation/clipboard evidence; final foreground-selection/UI check requested from the user after installation. |
| Positional tabs / browser | Executor and safety checks unchanged from restored baseline; prior human passes preserved. Helper connected at final startup. No new destructive tab test this turn. Reopen-three remains unverified/lower priority. |
| Screen analysis | Prior local capture/encoding under 300 ms; provider request stalled 24.29 s, and another returned HTTP 503. No demonstrated new local fault. Final normal-use check requested. |
| Camera | Prior execution took 16.27 s. Camera code/dependencies match fb0a067; existing evidence does not isolate native open/read, device contention or resources. No speculative camera change. |
| Overlay | Same `TopMost=true`/window styles as fb0a067 and cdd33d3. Photos/app-switch incident remains unobserved. Cross-desktop visibility is separate from topmost ordering; no unverified positioning change. |
| Protected behavior | Playback/microphone engine, conversation ending, tab executor, translation engine/UI, camera, overlay, settings and hotkeys preserved by source and installation comparisons. Unchanged source alone is not a physical pass. |

Windows distinguishes [topmost z-order](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) from [virtual-desktop membership](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ivirtualdesktopmanager-iswindowoncurrentvirtualdesktop). That distinction is not proof of the user's Photos incident cause. Supported desktop observation currently fails initialization with `failed to write kernel assets ... os error 3`; no substitute Windows UI automation was used to bypass that limitation.

## Exact continuation evidence

Evidence base: `.artifacts/connectors/voice-followup-recurrence-20261018/`.

- `speech-segment-recurrence-20261021/`: original red/green gates, recordings/provenance, all three desktop pilot receipts/logs, translation dispatch tests.
- `speech-segment-final-delivery-20261021/desktop-smoke-reconciled.json`: identity conversion and human confirmation; does not overwrite failed receipts.
- `speech-segment-final-delivery-20261021/speechfinal20261021-guarded-handoff-v3.json`: final one-install/runtime/public-preservation proof.
- `speech-segment-final-delivery-20261021/speechfinal20261021-installation-boundaries.json`: 29 protected file comparisons.
- `speech-segment-final-delivery-20261021/installed-current-recurrence-5b61b99/extraction-proof.json`: actual installed DLL identity.

Await only the requested normal-use translation, screen, camera and overlay feedback. Correlate its timestamps with the installed app log. Do not rebuild, rerun broad gates or alter stable code without a newly demonstrated defect. This is a delivered private correction with explicit remaining verification limits, not universal stabilization or public-release acceptance.
