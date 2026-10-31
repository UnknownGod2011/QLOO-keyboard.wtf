# Source-only recovery after failed 68af242 microphone acceptance

## Status and boundaries

The user's normal microphone session failed. **Installed 68af242 is not accepted.**
Its passing earlier text sequence and 398 packaged checks do not supersede that
failure. No additional package was built or installed during this correction.
68af242 and all earlier rollback archives remain preserved. The current installed
EXE still matches its archive:
`50685031A0C265A7F13D889617AA5763623F7729C3DE5D5B2E89CF69E54BF77A`.

New corrections are separate, reversible local commits:

- `b4ffa90`: first native Gmail person lookup preparation.
- `926234c`: explicit spoken ordinal-tab forms and routing tests.

## Fresh physical evidence

`.artifacts/connectors/desktop-recurrence-20261025/physical-live-20261025-090214.json`
is the normal-shell readback of actual installed GUI PID 8368. Its microphone
session is `b8507de24c04477c8a2cba3d75b324a5` at 14:25–14:26 IST.

- Turn 2: 62 input characters, two browser proposals, both explicitly rejected
  by the complete positional-request parser **before Chrome was contacted**.
  That is not a connection, foreground or executor failure.
- Turn 3: 30 input characters and two browser proposals. Tool arguments and
  response text are absent. The user's expired-reference report is consistent
  with the separate retained-handle close path, but the exact invocation is
  not recovered from the log.
- Turn 4: the exact last-two pre-route succeeded. The executor is not generally
  broken in this session.
- No provider interruption or overlapping old response is evident across those
  turns. Speech/VAD/boundary changes are unjustified.
- The Gmail turn's actual native requests were `sent to:Ricardo`, `to:Ricardo`,
  then free-text `Ricardo`. The final search returned the same two unrelated
  body-mention messages as the earlier failure. The user's original intent was
  sender discovery, and those messages are not sender evidence.

The log deliberately retains transcript lengths, not the speech text. Its
62-character tab request and 131-character Gmail request **cannot be recovered
verbatim**. No phrase is invented or claimed as the exact failed transcript.

## Gmail correction

The 68af242 adapter reused the existing person-query preparation only when the
model's proposed query exactly repeated a quoted name in that prepared query.
A model spelling difference or recipient-direction guess bypassed it. Native
body matches then prevented empty-sender recovery.

`ConnectorMailSearchArguments.Normalize` now reuses the already-proven human
person query for the **first** native lookup; it no longer requires an identical
model proposal. `JarvisConnectorController.ExecuteAsync` supplies that request
only before a search exists in the task and outside planned multiple read goals.
Later model refinements are not overwritten. The original human request still
owns topic/date/literal/account and read/open authorization. The existing
preparer declines non-simple constraints; structured fields and literal queries
retain their established path.

No new QueryForPreparation helper, query grammar, spelling algorithm, provider
call, model call or retry was introduced. Existing From-header verification and
retained exact-thread references remain unchanged.

## Tab correction

The existing whole-request parser declined demonstrably unambiguous explicit
left-position forms: per-item tab nouns, numeric position lists, and “from the
left.” If the parser declined without its rejection fence, a model's stale
target_ids could reach the unrelated retained-tab-handle path.

`JarvisAutomationService.TryGetOrdinalBrowserTabCloseReferences` accepts these
forms using the existing bounded position resolver. Its rejection fence is
aligned. Whole-request anchoring, a required tab noun, distinct positions 1–9,
and rejection of mixed actions, negation and right-relative ambiguity remain.
The exact-ID executor, Helper/extension, target-window/session identity,
foreground checks and genuine stale-handle rejection are unchanged.

These are proved grammar/routing gaps. Missing raw speech means they cannot
honestly be called the exact recovered cause of every reported tab failure.

## Focused evidence

Evidence root: `.artifacts/connectors/desktop-recurrence-20261025/`.

- `remaining-live-route-before.trx`: **7 failed / 10 passed** on 68af242.
- `first-native-person-after.trx`: 39 passed.
- `remaining-live-route-after.trx`: 91 directly affected checks passed.
- `bounded-spoken-route-final.trx`: **57 passed, 0 failed/skipped**, 15 seconds.

The final bounded gate exercises normal `ObserveJarvisTranscript` plus the real
ordered `LiveToolCallDispatcher`, two stale/contradictory browser proposals,
consecutive ordinal/first-two requests, genuine foreground failures and genuinely
stale references. Gmail starts from a wrong recipient proposal, goes through the
real CommandRegistry/controller and existing verified sender recovery, then
opens the same observed thread on three subsequent turns without new searches
or reasoning calls. Provider results in this gate are controlled fixtures, not
new live Gmail acceptance. Initial tool queries are intentionally incorrect,
not prepared ideal queries. Literal, local-date and later-refinement controls
also pass. No 398/1,133/full-suite repetition.

At checkpoint `3e756b0`, only three production files differed from 68af242:
`ConnectorMailSearchArguments.cs`, `JarvisConnectorController.cs`, and
`JarvisAutomationService.cs`. Translation/large-source handling, speech,
interruptions, prompts, camera/overlay, goodbye, hotkeys, authentication,
Personal settings, native tab execution and ordinary routing are unchanged.

## Unresolved acceptance blocker

**Real installed behavior is not verified for the new source.** 68af242 remains
installed and failed human acceptance. The absent original transcript prevents
proving that these corrected grammar/preparation paths are the exact paths used
by every failed spoken turn. This source gate does not observe actual microphone
recognition, rendered browser mutation or physical sound. No further speculative
installation is authorized by a passing fixture test. Keep the source checkpoint
and both failures visible until a bounded real-provider/desktop acceptance can
exercise the corrected assembly and resolve this distinction.

## Continuation: real-provider compound-open and completion failures

The user's normal Windows launcher ran the corrected assembly with real Personal
Gemini Live, native Gmail, the production dispatcher and WaveOut device. Inputs
were original natural-language text, not microphone audio or prepared provider
queries. No installation, GUI restart, test-side token refresh or public change
occurred. Protected profile files remained identical.

The original run is preserved at
`.artifacts/connectors/voice-followup-recurrence-20261018/sender-recurrence-20261021/source-3e756b0-live`.
It recovered the misspelled sender through verified From metadata, but **failed
the initial compound opening**. A later explicit opening succeeded. Its harness
`failed=false` only meant the sequence finished; it was not acceptance.

`2fa22d6` corrects two connected handoffs:

- `CommandRegistry` passes an already-proposed `open_url` identity to
  `TryOpenCurrentReadResultAsync`. When incomplete coverage prevents automatic
  unique selection, that method reuses the existing proposed-mail validator.
  Only a single, observed native thread in the current authorized result can be
  selected. Unknown IDs, conflicting accounts, named/all selectors and absence
  of a verified selector retain their rejection. Native identity builds the
  destination; the proposed URL is not dispatched.
- `RetainResult` preserves an already selected thread when completion rebuilds
  **identical native evidence** under the same provider/account/request/goal and
  logical session. Changed evidence invalidates the carried selection. Only the
  selected identity is carried, not old coverage, content or execution authority.

`compound-partial-open-before.trx` reproduced the real rejection: 2 failures,
6 passes. The intermediate run caught lost selection on ready-task completion
and one separate regex timeout. The final directly affected gate passed
**166/166**. One intermediate assertion also incorrectly counted the legitimate
server task-completion request as a repeated read; that failed result remains
preserved. The corrected assertion explicitly permits that one completion.

The next real run, preserved as `source-2fa22d6-live`, opened the verified thread
on the initial request, with one dispatch, two native searches (initial plus
bounded spelling recovery), zero separate reasoning calls and clear activity.
Chrome's actual window title matched the intended conversation. Full AX/URL
observation timed out, so that title is not full rendered-page proof.

The details read returned HTTP 200. Gemini completed its reply with all 121
audio chunks passed to playback and zero missing sinks. **The sequence still
failed:** optional retained-fact extraction threw a 50 ms regex timeout on a
16,000-character body during `RetainResult`. Completion was aborted before
task/UI cleanup and the completed-answer callback. The actual regex exception
and body hash/length, without the body in diagnostic metadata, are preserved in
`regex-first-chance.json`. This is a local completion defect, not a provider 503
or evidence of a broken playback engine. The regex itself dates to `e9925651`;
the evidence does not attribute its introduction to today's corrections.

`a242409` contains only this exception boundary: `ConnectorSessionResult.Create`
catches `RegexMatchTimeoutException` from optional `ExtractFacts`, omits those
derived excerpts and marks cached content partial, while preserving verified
native identities and other evidence. No regex timeout, dispatcher, audio,
model, prompt or provider retry was changed. **42/42** focused fact, lifecycle,
opening and thread-coverage checks pass, including long content and unchanged
selection behavior. The next bounded real-provider check is pending; no new
candidate has been installed or accepted at that source-test boundary.

The same three-turn real-provider check on `a242409` subsequently passed in
77.91 seconds including full spoken replies/device drain. Initial request:
one verified thread opening and two searches (empty exact spelling, bounded
native recovery). Details: one fresh thread read and no opening. Final open:
the same account/thread, one opening, zero native reads. No separate structured
reasoning call ran; all activity cleared and protected files stayed identical.
The user confirmed clear speech and the correct thread in the source checks.
This confirms observed sound/page outcomes for those checks, while normal
microphone recognition and the yet-to-be-built installed candidate remain
separate acceptance gates. Raw failures and intermediate assertions remain
preserved. Final evidence: `source-a242409-live/acceptance.json`.

The local ordinary-command probe on `a242409` measured Open Gmail dispatch
median **10.43 ms**, total observe/dispatch/completion median **25.51 ms** over
12 warmed inert samples, with zero connector calls, account lookups, model calls
or provider calls. This is not network, browser-rendering or speech latency.
Machine load differed from older measurements; these figures do not establish
a causal speed improvement or regression.
