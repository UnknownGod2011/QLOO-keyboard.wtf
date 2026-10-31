# Capacity correction and failed live opening — 23 October 2026

This is **not release acceptance**. The installed private application remains
018b835 (1.4.21), EXE SHA256
`5E450DCE93180794F7BB2F939DA6BEDB7E3C77A6423B13EAF7B341E891E2E6B3`.
The archived 018b835 package and d0290be rollback remain protected. No installer,
public deployment, setting, credential, prompt, model or hotkey changed in this continuation.

## Current release blocker

The user reports that both "Can you open that in a new tab?" and
"Can you open that email thread in a new tab?" failed after Daisy details.
The retained opener already recognizes these forms; adding their words to a
parser is not a justified correction. No connector production code has changed
in this continuation. Backend task completion and retained resource lifetime
already have distinct implementations; keeping write/execution tasks alive
indefinitely would not establish the cause or be a safe repair.

A bounded replay ran through the actual installed GUI's existing recording API
at approximately **18:59–19:00 IST**, logical session
`1ee0f7542a0c417f91cbbd3661e28d4e`. The recorded initial request was transcribed,
connector activity was observed, the device queue reached 114,300 bytes and
drained, and the UI returned to Listening after 17.12 seconds. **Those facts do
not verify the answer content.** The next recording request hit the endpoint's
30-second deadline and returned HTTP 400; that private diagnostic endpoint stops
its owned session on such a failure. The user independently heard speech cut off
and a generic language-model refusal during this test. This is a failed sequence.
It does not prove that the refusal, provider interruption and endpoint timeout
have one cause. No opening or complete spoken recap is accepted from this replay.

Evidence is under
`.artifacts/connectors/voice-followup-recurrence-20261018/reliability-fixes-20261023/current-opening-reproduction/`.
Original failures remain intact. The successful source tests cannot override them.

The existing ordinary file-read view exposes an old October 11 profile log.
A prior desktop-side readback helper was rejected by automatic review as
`blocked by policy`, without a more specific reason. It has not been retried by
another launching mechanism. Supported Settings endpoints expose current action
history and Helper state, but not the provider/dispatcher event log. The next
needed evidence is the **current physical private log for 18:59–19:00**, obtained
by an allowed route or supplied by the user. No new wording/time recollection is needed.

24 October continuation: the user supplied an October 9 excerpt and asked for
another automated attempt. Direct reads of the candidate log and its available
generations still end on October 11; no current rotated generation is visible.
The candidate path and running process remain correct. A fresh supported Settings
read confirms the Helper connected and all five hotkeys present; its action
history exposes no operation after 18:35. The public-path process is a Chrome
native-host invocation. No denied desktop launch was retried through another route.
An entire current log attachment is sufficient; the user need not select lines.

## Isolated source correction, not installed

Frozen local source checkpoint: **ec2dd6fad380704566f519be6600fb43597d143b**.
This has not been packaged, installed, pushed or publicly deployed.

The 50,000-character *whole-selection* ceiling is confirmed in capture, Helper
fallback, source retention, translation admission and verified-copy retention.
It predates this candidate and is present in fb0a067 and d0290be; Git history first
exposes it in the imported c80e7a2 release snapshot. This does not identify its
original author or prove how the user's earlier page was captured.

Changes:

- `JarvisAutomationService`: keep full selected/copy source instead of rejecting
  or discarding it at 50,000 characters. An incomplete capture returns its usable
  text and explicit coverage metadata, rather than an empty refusal.
- Helper `service-worker.js`: return the actual DOM selection, preserving the
  existing focus/tab/account/session ownership and native transport checks.
- `CommandRegistry`: admit and retain complete large sources; a genuinely partial
  capture can be translated as the captured portion, with accurate coverage.
- `SelectedTextTranslationOperation`: translate sequential ordered parts through
  the existing delegate and UI owner. 50,000 characters is now a **per-request
  partition size**, compatible with the unchanged Managed server endpoint, not a
  document admission ceiling. Preserve paragraphs, CRLF and surrogate pairs.
  Ordinary sources fitting one part still make one generation request.
  Word boundaries are preferred for long paragraphs without newlines. Source
  separators join translated parts, and whitespace-only parts are preserved
  locally without an invalid empty provider request.
  A known Personal MAX_TOKENS result may split that part once; generic network,
  quota, authorization and provider errors do not trigger retries. Only completed
  parts are combined; clipboard publication occurs once after all parts finish.
  Completed partial progress remains in the operation in memory on later failure;
  there is not yet a new UI/export action for that partial text.
- `PersonalGeminiHttpTransport`: remove the separate selected-source character
  ceiling, retaining the existing serialized request/response envelopes and
  complete-output validation. Translation still uses the existing 65,536-token
  output setting. No provider/model upgrade or permanent prompt change.

Verification:

- `capacity-before.trx`: 21 failures preserved. These include both intended
  over-limit reproductions and old tests using a translation request without a
  bound Live owner; do not label every failure a new production defect.
- `capacity-after-v2.trx`: **321 passed**, including large Unicode/ordered source,
  placeholder/dependency protection, one verified copy, short legitimate output,
  cancellation, owned UI cleanup and preserving completed progress after failure.
- Old no-Live source-recall fixtures now exercise source recall; explicit
  translate/copy cases in such fixtures assert the existing missing-owner failure.
  The bound translation-dispatch fixture tests the exact user request and all
  source parts through the Personal HTTP boundary, rather than weakening ownership.
- Neighbor gate: 232 passed and one obsolete assertion expected 50,001 characters
  to be rejected. Its replacement checks two provider parts, one copy and the
  same update-work owner; the original failing receipt is retained.
- `capacity-update-owner-after.trx`: all **21** update-work checks passed after
  replacing that obsolete size contract.
- A narrow read-only review found new partitioning edge cases before packaging:
  a whitespace-only final part failed, and a long paragraph could split a word
  or gain an artificial paragraph break. The failing reproduction is preserved
  in `capacity-boundaries-before.trx` (**3 failed / 1 passed**). The corrected
  partitioner plus dispatch/update-owner checks in `capacity-boundaries-after.trx`
  passed **201 / 201**. These counts overlap earlier gates; do not add them into
  a distinct-test total. No physical or provider acceptance follows from them.
- Partial completed output remains in memory, but there is no new public
  retrieval action. Failure wording reports completed character counts without
  promising conversational recovery that is not implemented.
- Helper lifecycle/direct-tab tests passed (simulated browser API). No physical
  tab result is inferred from this test.
- Real large-page translation, physical speech and rendered Gmail acceptance on
  a corrected installed candidate remain **unverified**. No candidate was installed.

## Exact next actions

1. Obtain the current desktop event log for the witnessed failed replay; correlate
   provider interruption/device-stop, connector admission/result, lifecycle notice,
   socket recovery and the recording timeout. Do not suppress the refusal or guess
   a new phrase rule. Keep normal microphone/VAD/playback behavior unchanged.
2. Resolve only a confirmed shared opening/delivery defect; demonstrate it against
   the preserved failing sequence before considering private installation.
3. Review large *generic Live analysis* near actual context/transport limits:
   translation is partitioned, but generic analysis has no new paging or token
   preflight in this patch. Do not claim unlimited full-document analysis.
4. When critical gates pass, perform one controlled private package/installation
   and real acceptance with d0290be rollback protected. No public publication.
5. Gmail sending remains later work. The native catalog has `mail_send`, distinct
   from legacy draft tools, but account scopes and spoken confirmation/send
   idempotency have not been accepted in this continuation. No message was sent.

Goal remains incomplete. Do not mark this checkpoint or its source tests release-ready.
