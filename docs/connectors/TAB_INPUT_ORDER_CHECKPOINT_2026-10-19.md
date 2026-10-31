# 19 October continuation: early tab proposal ordering (not ready for installation)

Current authoritative worktree is keyboard.wtf-connectors, branch codex/connectors-foundation, base HEAD 0ded695e2cba09f08b1b83d654fbb7b0cbcd0dae. Production before this continuation is 83eb981. Installed private candidate is still 0ded695; EXE C9922D73361C50515E2670E0DAEE3C362C7E5E63D8BD8D216763E591B8B79C01, extracted DLL BD5654F28E433D606B9BDB8EBFBD46888AA58F85D6FE8B2298ACC02D3DD87D4D. Rollback f082c5c and its reviewed archive remain preserved. No installer or new private GUI was run in this continuation.

## New confirmed reproduction

The real saved first-two recording in package-stability-20261019/real-helper-first-two-20261018-v4 passed the actual foreground Chrome check. Provider frame 5 proposed browser_action close tabs with last_tab_count=2 at 09:11:21.890 UTC, before frame 6 delivered the clear first-two transcript at 09:11:21.904. The executor ran before its serialized observer at 09:11:21.928. The strict harness correctly rejected this unauthorized edge, closing nothing. This is distinct from v2/v3 foreground failures (ChatGPT/Explorer); those guards remain intact.

Two unmodified-production replay tests reproduced early action execution. Evidence: early-tab-order-red.trx (2 failed, 0 passed). Original production files saved under tab-order-correction.

Working change scopes only early Personal browser-close proposals: reserve their dispatcher position until the current input has been observed. Complete input releases immediately; absent input receives a bounded no-close failure, cancellation stays owned, and a newer spoken activity invalidates the earlier unbound proposal. Existing first/last stable-ID resolution and close execution remain unchanged. No playback/VAD/microphone/translation/connector changes.

Latest user also requested second-and-third tab sets. A small whole-request ordinal parser delegates once to the existing resolve-all-before-mutation mechanism, preserving permission and foreground checks. No new permanent tool or prompt.

## Gates

- tab-order-ordinal-v1: 452 passed / 67 failed. New ordinal regex exceeded .NET NonBacktracking automata limit; all original failures retained. Build overlapped the agent's microguard write, so this is not frozen-source evidence.
- tab-order-ordinal-v2: 497 passed / 24 failed. Early-input tests and prior controls passed; new parser still exceeded that regex engine's size limit. No deployment.
- tab-order-ordinal-v3: 521 passed / 0 failed / 0 skipped in 27 seconds; terminal at 09:45:49 UTC. Release DLL SHA256 0738FCE28A299E8F0D83C1DE0CF7CD320ABF72C1299B2648D0B05D643BF50238. Source and assembly hashes frozen in tab-order-correction/source-tested-v3.json.

## Completed installed checks and remaining blockers

- Installed six-turn Gmail replay completed useful answers, four identical exact Gmail URL dispatches, UI cleanup, no inactive-task errors, and actual WaveOut consumption. First open nevertheless took 97.06 seconds: ~85.67 seconds waiting for the next provider websocket receive. Local lifecycle notification took 16 ms; later receipt 5 ms. This remains a responsiveness blocker, not a pass.
- Device samples showed no internal quiet gap longer than 1 second in emitted speech; this does not prove room acoustics. Earlier captured recap was manually confirmed clear and complete.
- Installed core-v3 greeting and explicit ending lifecycle passed, with zero connector work; end activity-to-socket-close ~97.8 ms. Playback disabled in this lifecycle check.
- CUA verified the installed retained destination visibly rendered the correct Daisy conversation, not Inbox/search/blank. This was navigation of the captured URL, not a full Jarvis launch end-to-end.
- Corrected-source recorded first-two v6 and last-two v1 passed actual foreground, provider, Helper and stable-ID closure. Independent CUA tab inventory confirmed C/D remain after first-two and A/B remain after last-two; browser-outcome.json saved in each run. No microphone or playback in these tab checks; not speech evidence. First-two v5 failed before input because login token expired, then one authorized same-session refresh enabled v6.
- Second/third v1 did not execute: diagnostic launcher text replacement corrupted PowerShell syntax before the run directory. Original script and failure retained. Corrected launcher parser passed; v2 now running with unchanged safety checks.
- Public native host naturally relaunched public GUI after each isolated bridge test. Do not force-stop it or alter native host registration. Revalidate exact GUI identity and idle state before a normal-exit handoff.
- New source corrections require focused safety review, real recorded browser tests, protected hot-path checks, then frozen build/installation. Existing package operators pin clean 0ded695 and MUST NOT be bypassed or reused for dirty source.
- Existing 97-second provider delay, physical multi-reopen, full-suite old regex timeout, and final startup/ownership acceptance remain open.

Next: inspect v3 terminal results; correct only failures with causal evidence; run isolated corrected-assembly recorded first/last tests with unchanged foreground guard and inspect actual surviving tab IDs; retain all failed receipts. Continue the wider stabilization Goal. Do not declare ready.

## Typed-input compatibility (later continuation)

Second/third v3 reached real Personal Live but closed nothing: the new early-ASR guard refused a typed request that has no ASR. The harness also omitted KeyboardWtfApp's normal ObserveSubmittedJarvisText call; this will be corrected in the diagnostic only. Actual loopback sender regressions independently reproduced the guard failure (tab-typed-input-red-v1: 2 failed, 2 passed). A small explicit-text marker at the existing send boundary now bypasses the ASR wait only for submitted human text; lifecycle notices cannot grant it, and new speech / reply boundaries reset it. No fake ASR or audio changes. Expanded tab-order-ordinal-v4 gate running, host28188. New source snapshot must replace v3 pins only after terminal PASS.

Independent 97-second review confirms no frames arrived for85.667s, UI already clear, local notice16.17ms and receipt5ms. No evidence supports another playback correction. Prepared raw-two-turn-latency-v1 harness (compiled only), maximum180s, same saved recordings, actual device/raw frame capture, no real browser mutation. It has not run.

## Current pinned source v4

Expanded gate:560 passed /0 failed /0 skipped, finished10:12:23 UTC. Production DLL EE0F5CA92D4C96A02542A4A6F33C19F1A7295157D4A933114146FF67352CB148. Snapshot tab-order-correction/source-tested-v4.json. Personal text-tab loopback regressions pass and lifecycle notices/new speech still cannot authorize old closes. Ordinary Open Gmail local dispatch7.1931ms median,total16.1987ms,0connector/account/model/provider calls; permanentprompt19509/toolcount62 and both hashes unchanged.

Ordinal physical v4 failed actual foreground (ChatGPT) beforeinput; noaction. v5 separately gated, awaiting/just released at10:15:55UTC. Preserve this as unverified until actual Helper receipts and fresh Chrome inventory agree.

raw-receive-diagnostic-v3 built isolated successfully; adds only existing raw metadata callback, never install. Diagnostic DLL BE46D8ACDDAC34A183A54A9D23E9E5E183DCBCD12FAEB99A667C281ADB9FC33A;source-proof.json records source equivalence. Prepared two-turn run is pinned to this diagnostic plus560-test gate.

## Additional pre-installation safety edge

A read-only callback review found that generic dispatcher Observe also receives an empty transcript for old reply-completion callbacks. New test tab-completion-order-red-v1 failed before correction: that non-input callback released the waiting close. Minimal correction binds only nonempty transcript observations to the pending input; empty completion callbacks retain the existing ordered queue. v5 focused gate now running. Nothing installed.

raw-two-turn-latency-v1 finished47.36s, search37.00s including speech, recorded open7.09s including acknowledgment; UIclear,0Managed,0protectedchanges,exactretaineddispatch1. Important: lifecycle notice returnedfalse because provider followed normal connector_control route. This fast run does not reproduce or resolve the intermittent early-notice97s delay. Metadata+device evidence saved; speaker acoustics not inferred.


## 10:40 UTC continuation — frozen tab source and real ordinal success

Source commits727252c (early input ownership) and f9608b9 (ordinal selection) are independently revertible. Focused v5 gate561/561passed; DLL DB359CAE332C0A4104524E8167D53A851C060D8B849EB622E9B60DF5EA27356F; current pin source-tested-v5.json. Empty old-completion observations cannot release an early close. No production file is dirty.

Real Helper second-third v6 passed with actual foreground Chrome. The model proposed indices2/3, both stableIDs resolved before mutation, then C/Bclosed. Independent Chrome API inspection confirmed A1674838381/D1674838471remain in window1674838393. All earlier guard failures preserved. No microphone/playback,0connector/Managedcalls; typed command through actual desktop observers, not recorded speech. Public GUI normal-exited through reviewed endpoint with existing lock lease; no force-stop or registration mutation.

Repeat-open diagnostic v1 FAILED: first open56.15s with no provider frames for about50s after early lifecycle send; second open canceled at180s session limit, no speech returned. Exact dispatch completed and UIclear. No protected file changes. This supersedes any interpretation that the single fast two-turn run resolved the delay. A counterfactual-only replay raw-recorded-wait-baseline-v1 uses the same frozen diagnostic DLL and recordings but sets announcement OwnsCurrentReply=false to exercise fb0a067 original wait branch. No production edits. It may overlap full serial gate, so not a clean latency benchmark. Broad frozen v5 NoBuild gate tab-stability-full-v1 runningPID17044. Installed private remains0ded695; do not package until current blockers evaluated.
