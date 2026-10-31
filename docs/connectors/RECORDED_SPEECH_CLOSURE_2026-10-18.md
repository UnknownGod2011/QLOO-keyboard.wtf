# Recorded speech regression closure — 18 October 2026

This report supersedes earlier readiness claims for the reproduced spoken follow-up defect. Application checkpoint: **46c4d177ba6a7824ce2c28d841ee7460004f306c**. The private candidate is installed and running. Browser rendering and physical acoustic quality remain separate, unverified acceptance items; this is not a claim that every reported symptom is resolved.

## A. Confirmed causes and remaining uncertainty

The installed 40d8a72 recording reproduced an input/output ownership race. A new spoken open request began, Gemini sent a legitimate interruption, then the preceding answer completed. That old output completion cleared the new input's connector activity flags before its recognized transcript and ACTIVITY_END arrived. The connector admission hook was skipped: no new opening proposal or URL dispatch occurred, and Live produced a generic denial. It was not a failed Gmail request or an inactive-task backend receipt in that reproduction.

A second ordering case split the new transcript across the old answer's completion. The shared conversation transcript lost the prefix, so the controller's exact human-instruction binding could reject the suffix. The final correction preserves a bounded per-input transcript and re-observes the complete already-accepted input through the existing ordered callback before execution. It never revives completed execution or write authority.

Legitimate provider interruption still clears old queued speech. In the corrected recording, old partial speech completed before the new recognized human identity; one owned answer followed each accepted open/details request. No old-human answer appeared after the newer human input was recognized.

The cause of the user's acoustic cracking is **not established**. VAD, microphone capture, formats, playback, transport and listening-state code were not changed. Earlier physical logs showed three provider interruptions clearing queued audio but cannot distinguish intentional speech, noise or echo. Captured recap PCM is substantive. A virtual timing check found no supply gap in the recap; a factual-answer capture has one approximately 882ms apparent gap, but its timestamps are harness buffer-drain times and cannot establish a provider stall or hardware underrun. No audio correction is justified from that alone. The naive first timing calculation double-counted this gap; both receipts are retained and the reviewed calculation supersedes it.

Some console windows were traced to our diagnostic shell launches: Codex PID16964 spawned pwsh24948/14524 and conhost8516/17528 at matching timestamps. Application launch/native-host/update/test-helper code matches the earlier candidates, and no app restart loop was found. Diagnostics now use hidden, redirected child processes or the existing hidden Windows launch mechanism. Errors remain logged. This establishes one cause, not the cause of every historical flash; no normal terminal was closed and no new service/startup dependency was added.

## B. Historical comparison

The full pre-fix GeminiLive source blob ea6f8112e8154acf556d59baee8c97a2dff6829b is identical at 96eff50, d245ac4, fb0a067 and 40d8a72. VoiceCaptureService, microphone-control and Personal Live transport blobs also match. The speech-boundary flags originated in f355a4d; the recent search-only candidate did not introduce the underlying race. Earlier passing tests did not exercise this recorded ordering.

The older 2f5ea15 Live implementation differs by the later anchored explicit-ending recognition. That earlier ending correction was preserved. Translation and its dynamic UI were inspected against the earlier versions; the selected-text service remains identical, including the 50,000-character admission limit. No translation request or UI behavior was changed in this pass.

## C. Exact production correction

Only src/Services/GeminiLiveConversationService.cs changed against installed 40d8a72: **27 additions, 5 deletions**. The other 215 tracked source files are unchanged.

- HandleServerMessage: retain connector input activity across an older output completion; collect the bounded activity transcript; consume/reset it at ACTIVITY_END.
- ObserveFinalConnectorInput: bind an already accepted full transcript through the existing ordered observer before starting its existing read/open operation.
- ProviderSocketDiagnostics: one bounded per-activity StringBuilder, using the existing transcript limit and spacing rules.

The focused regression Prior_reply_completion_cannot_erase_the_new_spoken_retained_read_activity covers input before/after old completion and split transcription, full human text/identity, a fresh single read and inert duplicate END. No delays, retries, permanent tools/prompts, access controls, task architecture or unrelated functionality were added or rewritten.

## D. Execution evidence

Evidence directory: .artifacts/connectors/voice-followup-recurrence-20261018.

| Check | Result and exact proof tier |
| --- | --- |
| Original installed recorded barge-in | FAILED, preserved in recorded-barge-installed-v3. |
| Initial uninterrupted replay | v1 failed because the harness rejected an extra address-bar proposal; corrected v2 passed. Neither replaces the barge failure. |
| Final focused gate | 296 passed, 0 failed. Original and intermediate ordering failures retained. |
| One broad desktop gate | 6,164 passed, 3 failed, 0 skipped; 6,167 total, 16m04s. Two explicit regex timeouts and one caught tool exception of unconfirmed type. |
| One unchanged-binary control | All 17 cases covering the three failed methods passed. No timeout/assertion was weakened and no production source changed. This does not rewrite the original broad result as all green. |
| Corrected source recordings | Actual Personal Live/Gmail v5 passed the full sequence; frozen DLL93288B5A2301BDA3DA875A7A1AB44CCB6AB9B897CBE8E13661F85A9CA956C7AF. |
| Final installed extracted bytes | Actual recorded Daisy -> spoken open during first reply PCM -> combined details/open -> factual follow-up passed in 117.639s. 17 backend requests, 13 native executions. |
| Provider rate limit | One actual Personal reasoning HTTP429, then one local cooldown. Native retrieval succeeded; no Managed fallback, charges or retry loop. |
| Open/follow-up continuity | Two exact retained URL dispatches, one per explicitly requested open. Barge open required no new backend/native/reasoning work. Combined request made a fresh body read and no new search. Factual question made zero new backend/native/reasoning/search/open calls. |
| Actual answers and PCM | Reviewed against native evidence. Detailed answer covered participants, chronology, renewal responsibility, account-code request, verification issue and confirmed transfer; it explicitly disclosed clipped/partial coverage. Captured details59.280625s and factual12.280625s PCM; not text-only proof. |
| Greeting/ending | Same installed bytes and saved audio: “Hi Jarvis, what's up?” received a normal greeting; the recorded explicit ending invoked one ending observer, cancelled the session and closed the socket. Zero connector/structured/Managed calls. |
| Full recap device rendering | PASS on final installed DLL:59.280625s recap,2,845,470PCMbytes supplied/drained,2,866,242device-position bytes advanced,zero queue-empty-while-feeding samples,no unexpected PlaybackStopped/error.61.156s diagnostic duration; microphone/network/settings untouched. Acoustic quality not measured. |
| Chrome body/new tab outcome | Unverified. Fresh inventory shows the matching Daisy conversation title. Both supported page reads timed out at Emulation.setFocusEmulationEnabled. No permission bypass, personal tab mutation or new rendered-navigation PASS claim. Earlier human confirmation of the exact destination is retained separately. |
| Browser/tab/translation regression protection | Source unchanged, relevant desktop cases passed. Current actual first/last tab outcomes could not be re-observed through the browser tool. Reopen-three remains an unchanged lower-priority limitation. |

The final installed recap truthfully said its ten message references contain excerpts/clipped content. This is useful verified history, not a claim that every byte or attachment in the conversation was read. Factual recall used that evidence without a new search.

## E. Performance

Local ordinary Gmail dispatch median: prior installed9.8663ms -> final packaged5.7259ms, twelve samples. Observation plus dispatch19.349ms ->13.0612ms. All ordinary benchmark cases performed zero connector backend/account/provider/reasoning work. Saved Discord workflow remains about198ms dispatch; no claim of network or physical browser timing is made by this isolated benchmark.

Final packaged open: recognized input boundary to verified receipt about95ms; first provider PCM about896ms. Combined details/open: boundary to completed read/open receipt6.856s; first PCM7.636s; completed answer66.924s after boundary, including59.281s of speech. Retained factual question: first PCM2.421s; stage15.983s including12.281s speech and zero connector calls. The source v5 factual first PCM was846ms: provider response timing varied; the unaccepted text recall path did not execute the new accepted-connector callback. No local dispatch regression or extra reasoning stage was measured.

These are separate dispatch, provider/tool, PCM and completion measurements. Captured PCM duration is not hardware playback completion. Device timing is reported by the dedicated render receipt below.

Permanent payload unchanged: prompt19,509 characters, 62 tools/30,887 JSON characters. PromptSHA D094925C7CAE6336CE013FF4515C832279A36095D83D4314533E196C4B3D5E95; toolsSHA F1D89E81F3201FADC6EEED0CD2E40898C3A2D15B40DA019B21EBE3E4BB1F35FF.

## F. Protected state and deployment

One private build and one private installation. All24 packaged files matched; installed embedded, published and extracted managed bytes agreed. Existing rollback archives were preserved. Public EXE remains2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5.

The existing public GUI lock was held Open/Read/None across normal private exit, installation, packaged tests and private startup. Its identity, zero length, hash and creation/write timestamps were unchanged after release. No public GUI/native host was stopped, no Helper registration/startup was changed, and no duplicate GUI took ownership. New private GUI24608 owns the correct profile lock. Fresh13:17IST readback confirms the same EXE, PersonalOnly revision17, all five saved active hotkeys, Helper/current-tab connection.

All29 protected profile files were unchanged during installation and isolated packaged tests. After normal GUI startup,28 remained byte-identical; only paid-staging-session.dat rotated. Physical metadata and the new GUI's successful refresh log prove the same session, with renewed expiry. No sign-in, mode/settings/key/hotkey/mapping change was made. Public startup, native-host registration and installed Store Helper manifests remain unchanged. No visible console window was present at post-start readback; that snapshot does not prove an absence of every transient historical window.

Backend source tree8663bfda66ed96d9eded3540dd475bd41cf61aff is unchanged. Existing452/452 backend evidence retained; no new backend gate/deployment or public publication.

## G. Installed identity, rollback and remaining limits

- Application source:46c4d177ba6a7824ce2c28d841ee7460004f306c, checkpoint/connectors-recorded-barge-boundary-20261018.
- Installed EXE:ECCB2F27A1A00264B6FECF361827BBFF80593C7D7A8CF92F2D6F7F13EDA0612D.
- Installed/published/extracted DLL:AC29006D13462EFEDE6015D795C854CD0004E5F990E62885227717B1F3FA9F26.
- Private installer:FE40B88F0EF58D60BF7E2D0770F13BFA8834D22A6B0DFAA04B711BB9A1FC449D.
- Archive manifest:6F72ABD935C2E9EEB66A0500C0E55EF4D1DA6D03ECB834496C3C0105917A21B1.
- Rollback:40d8a722e77dc59bc4f7b7ac3692145b67c43b97; preserved40d8a72-reviewed archive, manifestD150CA256ECB150A75C5624E704BED8A984150D60175D2AD50FE7C1AA524D155.

The reproduced connector input race is corrected and verified on installed bytes. Global acoustic-quality sign-off and a newly observed actual Chrome action remain open manual/external proof limits. No speculative audio/browser correction was made to conceal those limits. Do not rerun broad gates, rebuild or reinstall unchanged source because of older stale checkpoints below this report.

Primary receipts: package-final/final-gates.json, tests/desktop-final.trx, tests/desktop-final-control.trx, package-final/speech-boundary20261018-packaged-live.json, package-final/packaged-core-v1/acceptance.json, package-final/speech-boundary20261018-guarded-handoff.json, post-start-boundary-reviewed.json, runtime-final-1317/physical-state.json, browser-verification-final-1319.json, final-device-render/device-render-result.json.
