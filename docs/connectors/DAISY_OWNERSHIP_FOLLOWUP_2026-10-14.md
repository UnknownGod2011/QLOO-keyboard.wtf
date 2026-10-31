# Daisy response ownership and retained follow-ups — 14 October 2026

Rollback: `checkpoint/connectors-before-daisy-followup-20261014` points to
`bbcff17f752eb409b925cdd785d9049b750aff4d`. Its installed package remains archived
under `dist/connector-candidate-archives/bbcff17-reviewed`. This is a narrow
continuation of the restored Jarvis boundary, not a routing redesign.

## Reproduced failure and owners

The physical Daisy session recorded successful Gmail search, a five-second
background-start receipt, premature Live output, then two Personal reasoning
HTTP 503 responses. The later details request reused terminal task authority.
The exact-open parser did not accept the presentation suffix “in a new tab”.
The held backend independently confirms successful native retrieval; this was
not evidence that Gmail disconnected.

- `JarvisConnectorController` owns pending operations, delivery, activity,
  cancellation, temporary result context and fresh follow-up task authority.
- `GeminiLiveConversationService` publishes output. A narrow callback now holds
  only the matching connector turn's output until its pending receipt is
  collected. Other human turns retain the existing path. No artificial wait,
  audio parameter, transport, VAD, STT or model change was introduced.
- `ConnectorReadReasoner` preserves verified evidence after typed reasoning
  failures, including Managed admission/rate/unavailable responses. Existing
  retry, financial ownership, evidence and eight-read budgets remain enforced.
- `ConnectorSessionResult` retains compact references/facts and loaded-content
  capability metadata independently of terminal task authority.
- `ConnectorFollowupIntent` separates exact object references from presentation
  modifiers. It does not classify plain service opening as connector intent.

## Corrections

A started operation keeps response/UI ownership while its result is pending,
including a verified partial result after reasoning failure. Delivery or explicit
cancellation releases ownership. No terminal task is restored for execution.

An observed offer to read details binds a subsequent affirmative to its retained
result. A new same-account read task retrieves the unique observed thread;
multiple candidates require clarification. No old query or write approval is
replayed. Explicit full-thread requests have the same behavior. Missing or
clipped bodies are progressively read using observed message IDs within the
existing read budget; incomplete coverage remains partial.

An implicit affirmative/details request preserves an earlier headers-only
restriction. The existing explicit human body/content authorization is required
to relax it. Body-only Gmail capability subsets also project earlier content
after an accepted metadata restriction. Three causal failures reproduce these
permission boundaries before the guard; final receipts record their correction.

Exact retained object opening accepts “open it”, “pull it up”, “show me that
conversation” and new-tab/window modifiers. Generic provider-homepage proposals
cannot replace the retained object. The existing account-qualified Gmail URL
format was verified in a real browser against the user-supplied Daisy thread;
no mailbox-specific URL was added to product source.

## Evidence and limits

Receipts are under `.artifacts/connectors/daisy-followup-20261014/`.

- Causal pre-fix tests reproduce the pending-answer and exact-opening failures.
  The focused gate passed 1,322 checks. Subsequent narrow Managed-failure,
  progressive-body, ambiguity and generic-reference checks passed 32 and 29
  cases respectively. Final full-gate counts are in `final-gates.json`.
- Actual source Gmail search found the known Daisy browser thread among two
  candidates. A read of the known user-supplied thread returned ten messages
  with partial body coverage. “Yes” created a fresh task and used one thread
  read plus seven missing-body reads, then survived injected reasoning 503.
  Retained question/reply context and exact opening used zero further backend
  calls; activity cleared. This proves native/component orchestration, not
  natural Live selection, audible single-response behavior or a complete
  AI-generated conversation summary.
- Actual Gao followed by Winter retrieval found three and five candidates;
  fresh same-account execution did not replay the old query, read a real
  thread, retained follow-up context and dispatched an exact Gmail URL with
  clean activity. Three native executions, zero model calls, about 10.3 seconds.
- The exact Daisy API reference was opened in a separate browser tab and
  rendered the same thread as the user-supplied Gmail URL. No Inbox fallback.
- Sixty ordinary-command repetitions recorded zero connector backend calls,
  account lookups, provider calls and connector reasoning calls. These are
  local dispatch measurements, not microphone or provider latency claims.
- Real Personal synthesis returned HTTP 503 twice. One bounded Managed attempt
  was declined by the existing daily spending cap (HTTP 409, zero charged).
  Neither cap nor saved AI preference was changed. Full natural summary
  acceptance remains unproven; no quota/fallback loop was run.

Some preparation receipts record helper-only failures (wrong Debug DLL path or
an old-ID assertion after the helper had already captured the new task). They
are preserved; corrected helper runs did not require application changes.

## Protected boundaries and delivery

Ordinary routing, mappings, prompts/permanent tools, popup dimensions, Smart
Writing, Browser Automation, memory, selected text/screen analysis, audio
parameters and transports are unchanged by this pass. Live has only the
connector publication/offer-observer hook described above. Backend source/tree
is unchanged; its existing 438/438 emulator gate remains separate evidence.
Held `00031-vup` stays isolated; no default/public deployment occurred.

Final source, package, installed hashes, profile preservation and packaged
acceptance are recorded in `final-delivery.json` when available. A source test
or this document alone does not establish installation. Physical retest must
check one continuous Daisy operation/one answer, affirmative details, exact
new-tab opening, and “cancel the search” without late speech or stale activity.
