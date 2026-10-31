# Native connector contract — 20 October 2026

## Scope and baseline

Private version 1.4.21 / source 28e5489 is the preserved installation rollback. This checkpoint changes only six connector source files. Audio, microphone/VAD, playback, Live response boundaries, goodbye, translation and its 50,000-character admission limit, tab selection/execution, Browser Automation, authentication, settings, hotkeys, permanent prompt and tool declarations are unchanged from that baseline. The staging backend remains tree 8663bfda66ed96d9eded3540dd475bd41cf61aff, revision 00033-tip.

## Confirmed causes and corrections

1. **Duplicate read ownership.** Live had already interpreted a request and selected a native operation, but ExecuteOwnedAsync launched a second bounded planner. Real Annika retrieval succeeded before that planner returned 429; a recorded Daisy detail turn made eight reads and then received 503. Live now receives the selected native result directly for all connectors. The non-Live bounded reader remains supported. Observed message bodies in a retained thread can be requested natively under the same current human owner; thread membership, account, permission and cancellation checks remain mandatory.
2. **Provider syntax versus absence.** Gemini generated unsupported Gmail date:/on: operators. An empty response was treated as evidence of no mail. Inferred syntax is now validated before dispatch and rejected with a correctable same-task receipt. Gmail capabilities also advertise optional sender/receivedOn constraints, normalized into existing provider arguments. Literal human queries remain opaque. Frozen request clock/timezone accompany the task metadata. No natural-language date parser or extra model call was added.
3. **Completed-task routing.** A stale model task ID plus a new search was rejected by an additional phrase classifier. For registered native search capabilities, fresh authorization now comes from the new human turn and current account/session, not another affirmative language classifier. The stale proposal never executes. The legacy handwritten query generator is bypassed on this native path.
4. **Bare retained opens.** Generic open/show references and terminal punctuation could bypass the existing verified opener. The same retained provider/account/resource boundary now accepts those references. No model URL gains authority and the existing browser launcher is unchanged.
5. **Completion dependency.** Cached follow-up reuse required a secondary-planner settled flag. A completed, verified native thread receipt now satisfies the same boundary without synthetic planner state.

Final compatibility review caught one account-ambiguity regression in the new recovery code: counting only completed candidates could ignore another current account. Restored the prior guard over all current accounts before packaging. Both native and compatibility tests cover that denial.

Production files: ConnectorFollowupIntent.cs, ConnectorMailDateScope.cs, ConnectorMailSearchArguments.cs (new pure adapter), ConnectorReadReasoner.cs (request clock metadata only), ConnectorReadScope.cs (inferred syntax validation), JarvisConnectorController.cs.

## Evidence retained

Root: .artifacts/connectors/voice-followup-recurrence-20261018/live-search-recurrence-20261020

- installed-nl-search-v1 and native-date-contrast-v1 establish the original query/planner failures. Native contrast is not original-language acceptance.
- Query/contract/fresh-read red gates and intermediate failures remain. Final relevant scoped gates: 101, 793, 45, 104, 38 and 478 passes at their respective source checkpoints; do not add these into a unique-test count.
- native-recorded-six-turn-v1: actual saved Daisy recording, normal controller dependencies, same session; details took 82.1 seconds including speech, eight native calls and one secondary planner (real 503).
- native-recorded-six-turn-v2: same recording and six-turn sequence; zero secondary calls throughout, details 31.1 seconds including speech, three identical verified Daisy URL captures and UI cleanup after all turns. Independent offline ASR confirms the complete long recap reached device capture. This proves device-delivered content, not room acoustics or browser rendering.
- native-varied-acceptance-v1: original natural-language inputs interpreted by Gemini; six sender/topic searches each used one native search and zero secondary calls. Two bare/repeated opens selected the verified Daisy URL. Its semantic-verdict.json explicitly preserves mixed-topic recap/factual ambiguity and a wider-than-exact date window. This is not a full semantic pass.
- final-connector-compatibility.trx: 2,577/2,637 passed; original 60 failures retained. Fifty-nine expected the old secondary-reader behavior on Live mode; one exposed the account guard. Legacy tests now explicitly select the preserved compatibility reader, while native combined-open/cancellation/browser-failure cases are tested separately. No assertions or deadlines were relaxed.
- final-compatibility-focused.trx: 102/104 passed. Two legacy-timing test adaptations still expected the old direct-reader input semantics; final-native-input.trx: 4/4 passed, proving prompt native handoff, unchanged dispatched arguments, truthful rejection of inapplicable planner guidance, cancellation and zero secondary calls. Other successful broad cases are reused; no clean single-run broad-suite claim.

## Interpretation limits, deliberately not patched

The original possessive phrase Find Daisy Edwards' email sometimes causes a provider no-tool refusal, while Find the email from Daisy Edwards and the saved recording succeed. Two small prompt experiments did not solve it and were fully reverted. No phrase-specific name/subject rule was introduced.

In a mixed six-person session, a whole-conversation recap referred to the Jarvis chat, and a later last-commitment answer chose an earlier Daisy commitment. The exact single-conversation replay passed; universal semantic correctness is not claimed. The latest user explicitly requests finishing without more date parsing or overengineering. These limits are reported rather than concealed.

## Delivery checkpoint

Source frozen for one private build after focused gates. Packaging, exact installed payload verification and bounded installed replay are recorded separately under native-contract-delivery-20261020. Preserve 28e5489 and its older rollback archives. Browser rendering remains unobservable while local browser use is disabled; prior human confirmation is retained separately.
