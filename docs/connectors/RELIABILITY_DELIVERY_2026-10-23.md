# Private reliability candidate — 23 October 2026

Installed private **1.4.21 / `018b835431f60271d7e7bae8dcd95035a5ea0213`**. Immediate rollback **`d0290be`**, with **`872cef7`** also preserved. **This is a partial acceptance, not a public-ready or fully stable verdict.** The two authorized reliability corrections are implemented and verified at source/package boundaries; actual desktop translation succeeded, but the complete recorded Gmail sequence did not pass.

## Confirmed defects and isolated corrections

| Correction | Production boundary and rationale |
| --- | --- |
| `b6d92de`: translation authority | `CommandRegistry.CompleteJarvisUserTurn` retains an explicit translate-and-copy request across a tool-only completion, releasing it on a real answer, new request or session reset. The user's exact sentence failed in two unchanged-code reproductions. Without the request, a later copy could take the ordinary-copy path and publish model narration. This proves the defect class, not the exact event order of the earlier unlogged utterance. |
| `b6d92de`: dependent capture/copy | `LiveToolCallDispatcher.RunAsync` carries execution-local provenance for a capture whose result has not yet been returned to the model. `CommandRegistry` cannot treat that capture as justification for dependent output proposed in the same batch. The existing owned translation operation remains responsible for generation, verified copying and completion. No-session translation proposals fail truthfully. Ordinary literal copying and legitimate short translation remain covered. |
| `44060c9`: connector exception cleanup | `JarvisConnectorController.ResolveAsync` reports classification timeout accurately; `ExecuteOwnedAsync` includes setup/classification inside its existing cleanup boundary. Faulted completion/reset workers are observed, their update lease/UI ownership is released, and failures cannot become successful announcements. Read and uncertain-write outcomes remain distinct. No search, retained-reference, exact-opening or authorization redesign. |
| `018b835`: playback evidence | `GeminiLiveConversationService.StartPlayback` / `StopPlayback` log original device/session identity, expected versus unexpected stopping and error type. This is observation only, not a speech/VAD/playback correction. |

Only these four production files differ from `d0290be`. The translation operation/engine, 50,000-character limit, dynamic UI implementation, tab executor/safety checks, camera, overlay, goodbye, prompt/tool definitions, provider model and configuration are unchanged. Historical `fb0a067` comparisons were reused: its translation worker differs from `d0290be` only in the already approved bare-`this` admission correction; this delivery does not revert later approved speech/tab fixes.

## Before/after and package evidence

Evidence root: `.artifacts/connectors/voice-followup-recurrence-20261018/reliability-fixes-20261023/`.

- Exact-request boundary reproduction: **2 failed before**; corrected cases included in **232/232** initial focused checks and the final **820/820** selected regression gate.
- Exact extracted package assembly: **174/174** focused checks. Tests cover translation admission/dependency, short and ordinary copying, failure cleanup, cancellation/authorization, retained follow-ups, reply ownership and protected browser behavior.
- Original compile/setup failures and the earlier **6-failure/8-pass** exploratory run remain saved. Two exploratory variants were outside the exact contract and were removed; do not claim six original product failures were all repaired.
- Independent source review found no production blocker. Secret scan passed; no full 6,000-test rerun or public deployment occurred.

## Actual installed desktop results

The private recording endpoint drove the running installed GUI, its normal tool dispatch, real providers and playback device. Translation used the exact typed user sentence with a visibly selected harmless 142-character Notepad fixture. Gmail and interruption controls used existing recordings through the real microphone callback clock. Room microphone publication was deliberately disabled in this diagnostic; this is not an acoustic echo test.

| Scenario | Actual result |
| --- | --- |
| Exact translation request | **Verified:** owned translation job, complete independently inspected French output (172 characters, all six source concepts), verified clipboard write, active translation UI state then cleanup. Corrected-observer run finished including speech/device drain in **12.23 s**; the translation job took **7.225 s**. Ten provider audio parts reached playback, queue rose then drained. Popup pixels and human acoustic quality were not observed. |
| First translation attempt | Translation/copy completed in **900 ms**, but spoken lifecycle delivery returned false and used the existing notification fallback after about **8 s**. Its first test observer also used a log reader that could deny append writes. Preserve the 100-second harness timeout; it was not 100 seconds of translation work. Do not infer the log-reader defect caused the missing spoken acknowledgement. |
| Ordinary reply | **Verified at device/state level:** 44 provider audio parts forwarded, nonzero queue then drained, Listening and no activity, **13.92 s including speech**. No unintended interruption in this control. |
| Recorded Daisy search and paused combined follow-up | **Failed:** provider transcribed both inputs, then completed each with no tool call, output text or audio. These were incorrectly marked `settled` by the original test's state-only interpretation of a phase line. They are explicitly rejected in `installed-acceptance-reviewed.json`. No application cause or connection to the new source delta is established. |
| Later factual question | Real native connector resolution/read returned successfully and a spoken response completed: **29.35 s including speech**, 73 audio parts forwarded, queue drained, task/UI cleared. This does not retroactively pass the two empty turns. |
| Later exact opening | One direct account-qualified URL dispatched for the previously verified Daisy thread `1a09467590afcb15`. The transient Helper URL encoded the same numeric thread ID; a later fresh readback reported the expected **DeveloperWeek Domain Roulette** conversation title and working content script. **Helper/navigation metadata verified; rendered Gmail/individual answer content not newly inspected.** No Browser Automation task was started. |
| Deliberate interruption | At 17:38:34.454 IST the provider interruption aligned with the replayed greeting and cleared **14,880 queued bytes**. Replacement reply produced 20 audio parts, drained and returned to Listening. No unexpected playback-device stop. The raw harness interruption count used the wrong log phrase; the reviewed receipt uses the actual event. |
| Task exception cleanup | **Verified by deterministic source and packaged-assembly fault injection**, not by injecting a fault into the user's desktop session. Ordinary installed task completion/UI cleanup was observed. |
| Tabs and other protected behavior | Reused prior physical/manual evidence plus focused source/package checks and unchanged executor/configuration. No new destructive tab test, camera capture, screen analysis or virtual-desktop mutation in this delivery. Do not advertise those as freshly physically retested. |

The raw second harness terminal says `ok=true`; **that is not release acceptance**. The authoritative reviewed receipt is `installed-acceptance-reviewed.json`, with `completeGatePassed=false`. Original results were retained unchanged.

The first failed operator did not restore the pre-test clipboard because it lost its in-memory backup on exit. The second operator restored its own starting clipboard correctly. No unrelated clipboard was deliberately overwritten during cleanup; nevertheless, the original pre-first-test clipboard was not recovered. The disposable Notepad fixture was left unsaved; existing user documents were not changed.

## Performance and preserved installation

Same-machine/same-physical-profile inert ordinary-command comparison: **Open Gmail median 32.95 ms (`d0290be`) vs 28.44 ms (`018b835`)**, 12 repetitions each, zero connector backend/account/model/provider calls. This measures local dispatch only, not spoken end-to-end response; it does not establish an overall speedup. The installed native read logged approximately **1.52 s resolve + 3.03 s execution**; no secondary reasoning dispatch was observed. Translation provider duration varied between runs and is reported separately from speech.

One guarded installation preserved **24 matching package files**, **29 unchanged profile files**, PersonalOnly, five registered hotkeys, public executable/startup/Helper registration and one private GUI. The other public-path process is Chrome's native host, not a second GUI. The new source remains frozen; later documentation does not imply another installed build.

| Payload | SHA-256 |
| --- | --- |
| Installed EXE | `5E450DCE93180794F7BB2F939DA6BEDB7E3C77A6423B13EAF7B341E891E2E6B3` |
| Extracted installed/package DLL | `70E04912339FC66784910A82643FCF088BB6D0159F65C0DF80509B411D52C27B` |
| Archived installer | `7D04D7A60DB4E30DE60B0D2144DC6982CF48FDE5E1C323B8CF824FB4BFFD08C9` |
| Package manifest | `C75681A91D9082BD57639E43D2005412BD47D7764A688D3CCDEEB982DE60A8BA` |

Shell AppData virtualization was independently identified during delivery. Protected installation/profile comparisons came from the reviewed physical desktop operator, not the redirected shell view. A later shell-based readback was blocked by automatic review; the needed action/Helper history was obtained safely from the existing read-only Settings endpoints instead. No browser-permission setting was changed.

## Remaining acceptance and next action

1. **Unresolved:** provider empty responses on two recorded spoken turns; one translation spoken completion used notification fallback. No evidence yet warrants changing core audio, VAD, prompt/model or adding retries. Do not call intermittent speech reliability solved.
2. **Unverified:** accidental speaker pickup, physical sound quality and fresh rendered Gmail outcome. The only useful final manual check is normal microphone translation/copy with visible progress, followed in the same session by Daisy search and the paused combined details/open request. Record success/failure and time; do not ask the user to repeat the already supplied wording for diagnosis.
3. Low-priority Photos/desktop overlay disappearance, multi-tab reopening and provider screen-analysis outages remain historical limitations, unchanged in this scope. No advanced GitHub expansion.
4. Keep `018b835` as the inspected private candidate and `d0290be` as immediate rollback; **do not promote publicly** or label the overall Goal complete. If a live failure recurs, correlate its timestamp with provider events before deciding on another change. No broad suite rerun or speculative rebuild is pending.

Public release work is separately gated by [PUBLIC_BETA_CHECKLIST_2026-10-23.md](PUBLIC_BETA_CHECKLIST_2026-10-23.md). No GitHub or website publication was performed.
