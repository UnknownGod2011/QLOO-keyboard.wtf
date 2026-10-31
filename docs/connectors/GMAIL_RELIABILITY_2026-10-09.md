# Gmail reliability and larger-query checkpoint — 9 October 2026

Status: **INSTALLED STAGING CHECKPOINT — GMAIL BASIC PATH VERIFIED; COMPLEX-ANSWER ACCEPTANCE STILL OPEN. NOT A PUBLIC RELEASE.**

Starting source: `123f832c6a609427132b6f01eb118a929ea79d41`, protected by
`checkpoint/connectors-before-search-20261009`. The installed staging desktop was
the accepted `8a4cccc` package at investigation start; the exact `3f02a9e` installation
is recorded below. Staging backend initially remained revision `00013-gjh` / `c4144f9`.
Previous receipts, rollback packages,
the public build/site/backend and the one-self-email fence are preserved.

## Proven boundaries and narrow changes

- Natural-query failure is distinct from execution/authentication: earlier genuine
  product reads succeeded, but the model sometimes invented an exact quoted phrase
  or counted a narrower category. More global Live prompt text is not the fix.
- Add one generic empty-topic refinement inside the existing read executor. The
  desktop derives topic/exact intent from the originating real user turn; only a
  supporting backend receives that field. Explicit quotes, Gmail operators and
  literal queries are conservatively preserved. No sender/date/negative filters
  are relaxed, no nonempty query is changed and no writes are retried.
- Add bounded internal `mail_count` pagination to avoid spending a separate model
  exchange on every page. Four read capabilities (five only when a write is
  authorized), unchanged three permanent brokers/62 Live tools. At most 5,000
  messages per read window, deduplication, compact references and explicit partial
  evidence; no promise of unbounded/atomic mailbox counting.
- A large encoded body was silently dropped and marked complete. The new causal
  test reproduced that failure; return a bounded excerpt with incomplete evidence.
- Adversarial authorization failures after a first page also exposed a raw result
  remaining in the failed receipt. Discard it unless the operation normalized and
  completed successfully. Preserve independently verified write-journal outcomes.

Changed runtime scope: `JarvisConnectorController`, connector registry/task gateway,
existing mail normalization and one mail-read helper. No changes to provider OAuth,
UID/account ownership, confirmation, billing/prices, Live/system instruction,
audio/VAD/STT/playback, selection/translation, screen, BA, Smart Writing, Helper,
hotkeys/startup, saved mappings/workflows, public artifacts or permanent memory.

## Evidence tiers

- Initial source receipt `connector-search-scale-before-fix.trx`: three causal
  checks fail before implementation (topic refinement, aggregate counting,
  silently discarded oversized body). Preserve this receipt.
- First after-run: 97 passed/3 failed; old capability-count/description assertions
  needed to reflect the reviewed new capability. No failing result is hidden.
- Adversarial after-run: 118 passed/2 failed; authorization failures retained the
  raw first-page result. This directly caused the result-discard correction.
- Final focused backend: **124 passed, zero failed**. Fourteen Firestore-emulator
  checks skipped without a configured emulator; they are not counted as passes.
- Desktop origin/owner/confirmation focused regression: **74/74 passed**.
- Full desktop candidate regression: **4,148/4,148 passed**, zero failures/skips,
  3m43s. Receipt `connector-search-full-desktop.trx`.
- Full backend unit/non-emulator regression: **913/913 passed**, zero failures/skips,
  17s. Receipt `connector-search-full-backend.trx`.
- Real local Firestore emulator persistence/identity/claims regression:
  **14/14 passed**, zero failures/skips, 11s. Receipt
  `connector-search-firestore-local.trx`. The first emulator attempts failed due
  to launcher/Java selection, before any tests; the existing installed Java 21
  runtime was reused without changing PATH or customer prerequisites.
- Tracked/untracked source secret scan and diff checks passed. Real authenticated
  product-path reads, repeated natural query quality, installed acceptance and
  actual latency remain to be recorded below.

Source tests do not certify real provider availability, physical audio/popup,
OAuth consent, account switching, disconnect/reconnect or public promotion.

## Real product-path findings after the first narrow fix

The `5ac7b7f` backend was built from an exact clean Git archive, health-checked with
no traffic, then promoted only on the isolated connector staging service as
revision `00014-zah`. Runtime/secret bindings were independently unchanged;
`00013-gjh` remains recoverable. The matching private installer and package are
archived under `dist/connector-candidate-archives/5ac7b7f-b559ef03`; they were not
installed or published.

- Authenticated product `mail_count` returned **36** winning-style notifications,
  one page, complete; controller/backend round trip 3.701s including resolve/finish.
- Broader Devpost mailbox query returned **604** messages, **two actual pages**,
  deduplicated and complete, 3.778s. This is not a winning-email count.
- Cloud telemetry independently measured registry resolution below 1ms, account
  lookup 461–719ms, exact-count execution 575–951ms and the two-page count 1,453ms.
  Client timing includes network/authentication/journal work and is not identical
  to provider execution timing.
- **Natural 3.1 still failed before the evidence guard**: it counted an uninspected
  inferred body-word query and answered 3. A three-part request also omitted event
  details and answered 3. These are answer-quality failures despite successful
  HTTP operations; their receipts are retained.
- A small controller-only count prerequisite now uses existing completed task
  receipts: descriptive category counts first require real nonempty sender/subject
  previews. Exact human-supplied Gmail queries retain the direct count path. No
  new cache, model/planner, global prompt or persistent message content.
- Two natural wordings on the revised local controller returned **36** after
  inspecting subject evidence, 21.923s/24.637s total including streamed generated
  audio and the diagnostic's five-second drain. They are not microphone/heard-audio
  proof and do not yet certify the final combined-request behavior.
- Existing metadata calls now also request a bounded 500-character Gmail snippet,
  not a full body, using the same HTTP request. It helps relevance selection; the
  relevant full message/thread must still be read for dates/pending actions.

One further causal test proved that ordinary punctuation in `Check my Gmail:`
was incorrectly classified as an exact operator. Recognize documented Gmail
operators instead of every word followed by a colon; exact quotes/queries and
sender/date/status operators remain conservative. The final controller suite
passes **83/83** including this case.

Further reviewed gates: full revised desktop 4,156 pass before the colon correction;
full revised backend 914 pass. Five new prerequisite tests fail causally before
implementation. The first backend after-run preserved one stale scope-wording
assertion failure; the final 914/914 gate passed after preserving the scope warning.
The final exact-source and installed evidence follows; the broader acceptance
limitations are not superseded by successful HTTP requests.

## Exact installed/deployed checkpoint

- Runtime source: `3f02a9e25de72d4841d7a0be92f5c1981f41d18a`, protected tag
  `checkpoint/connectors-gmail-evidence-installed-20261009`.
- Staging backend: `keyboard-wtf-connectors-staging-00016-vic`, 100% staging traffic;
  exact Git-archive build and no-traffic health check preceded promotion. Existing
  environment, secret bindings, service account, concurrency and timeout matched.
  Prior `00014-zah`/`00013-gjh` receipts remain recoverable. No public deployment.
- Installer archive: `dist/connector-candidate-archives/3f02a9e-230c4b0e`.
  SHA256 `230C4B0EADE309F215795BF1BD956EDFAE76FA05B40EAC2DA0F8DB95A11529BE`.
- Installed candidate EXE SHA256:
  `C15F5608F46E0EC4D438555F68798C25DE6A293AB2187FB7743EC4E289D00493`.
  All 24 package files match. The 23 non-EXE assets match the accepted owner-fix
  installation, including Helper/native resources. Old installation and profile
  backups are preserved; no forced close, profile deletion or public replacement.
- Public EXE hash independently remains
  `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.
  Public startup/native-host registration, website/backend and billing are untouched.
- Actual installed Settings: **10/10 checks**, no page errors, automatic catalog
  load in 2.067s; 12 rows and four shared-Google connected capability rows.
  Protected credentials and **PersonalOnly/API mode** confirmed. Active shortcut
  `Ctrl+Alt+Q`; no Managed inference or purchase was used.

## Final gates and actual runtime evidence

- Exact runtime desktop: **4,157/4,157**, zero failures/skips.
  `connector-count-evidence-full-desktop-final.trx`.
- Backend non-emulator: **914/914**, zero failures/skips.
  `connector-count-evidence-backend-final.trx`.
- Final local database run initially **13/14**, with the unchanged concurrent
  connection-claim case failing a transaction lock timeout; isolated same-binary
  recheck also failed. Both receipts are retained. A fresh isolated emulator then
  passed **14/14**, no source/test deadline or transaction changes. This is not
  proof of hosted contention, nor justification to erase the failures. The owned
  emulator was stopped afterward; no cloud/customer documents were touched.
- Current exact-source authenticated counts independently rechecked: **36** matching
  winning notifications, one page, complete, 3.429s; broader sender query **604**
  messages, two real pages, complete, 3.518s. These timings include resolve, operation
  verification and cleanup, not Live speech. No estimate/first-page-only counting.
- Full-62-tool natural three-part question returned the correct brief event,
  conversation and 36-notification answers. Four task capabilities, no extra model
  planner; 19.059s first transcript / 44.259s total including generated audio and
  five-second diagnostic drain. It used previews, not full bodies: **brief-answer
  evidence only**, not comprehensive thread/detail acceptance.
- Installed Codex and Devpost turns completed and an authenticated GET independently
  verified one/two completed verified operations respectively. An installed Daisy
  search plus actual thread read also completed: first audio about 10.5s from diagnostic
  start (including Settings/session setup), about 8s after text-turn submission;
  all 72 provider audio chunks published with zero missing sinks.
  Ordered assistant-answer completion released authority; backend completion verified.
  This is not independently heard audio or inspected physical popup proof.
- Ordinary arithmetic immediately after Daisy used no connector tools/resolve/execute.
  Settings bootstrap/focus catalog GETs are separate management traffic, not connector
  routing from the arithmetic turn. Full-tool Open Spotify / Close Chrome chose
  existing local tools in 2.196s / 1.938s, zero connector HTTP/capabilities; these
  tests stop at proposal and do not open/close the user's applications.
- Idle hook benchmark: 30,000 completion hooks 17.6588ms (0.000589ms each), 30,000
  broker checks 0.5319ms; zero HTTP, schemas, announcements or AI. This measures the
  hook/classifier, not complete microphone-to-speaker performance.
- Real in-flight read cancellation passed: Cancelled, activity cleared, no completion
  notice/external write after cancellation. Source tests separately cover late results,
  stale owners, expired authority, account switches and unknown writes.
- The previously authorized self-email remains **one observed sent copy, zero duplicate
  copies**. Its original unknown journal outcome and independent delivery receipt are
  preserved. No resend was performed. Customer bodies are absent from tracked docs.

## Remaining proof — do not silently mark these PASS

1. **Detailed/complex answer fidelity:** a richer three-part wording completed searches
   and correctly counted 36, but used short previews rather than the requested full
   message/thread reads. Do not call it comprehensive reading or all-details proof.
   A preceding trial hit HTTP 409 before any operation claim and then the diagnostic's
   three-minute deadline. A GET of that exact original owned task found zero operations;
   its eventual expiry is not evidence of the earlier rejection's cause. The subsequent
   pass does not close this isolated failure. Capture sanitized rejection category and
   contemporaneous task state before cleanup; no speculative audio or billing fix.
2. **Real identity/OAuth lifecycle:** consent/disconnect/reconnect, two real accounts,
   account switching, restart and another device need actual owner interactions.
   Do not disconnect the currently useful Google connection without a viable consent
   return. Source/mock/emulator identity tests are not substitutes for these tiers.
3. **Other providers:** GitHub and additional Google services need actual consent/read
   acceptance. Slack/Discord custom staging credentials and external-workspace consent
   remain gated as documented in `STAGING_AUTH_CONFIGURATION.md`. Meet is unresolved;
   Reddit/LinkedIn remain Coming soon pending branding clearance. No blind duplicates.
4. **Release:** public Google scope verification, broader installed core acceptance,
   representative production provider proof and verified Managed physical-billing
   correlation remain separate gates. No stable public installer/site is promoted.
5. **Live reliability:** Personal 1011 and speech cutoff remain independent/open. No
   transport/VAD/STT/playback changes. Current bounded installed turns lost no playback
   sinks or sockets; this does not certify long-session microphone stability.

The 3.8 compatibility trial is **NOT PROVEN BETTER**. It accepted the full setup and
eventually answered 36, but used more HTTP exchanges (10 versus six in the earlier
3.1 combined case), took 28.513s to first transcript and 47.726s total, and recovered
from one normalized provider-unavailable read. A prior resolve rejection lacks a
conclusive cause. No fair stability/interruption/cost benchmark or production model
switch is claimed. Preserve all failed receipts.

The earlier natural-probe `verifiedCount=1` field on responses lacking an explicit
count was a PowerShell object-count artifact, not mailbox evidence. The diagnostic
now records a count only when the result contains that actual property; original
receipts remain unedited. Real operation counts and observed answers are the evidence.

All diagnostic Live sockets were disposed and installed Jarvis ended normally with
goodbye. The final allowed-state GET confirmed `nativeVoiceBusy=false`. A finite
same-chat continuation was scheduled for **9 October, 07:13 Asia/Calcutta**, using
API mode only; the earlier completed financial follow-up remains deleted.

Artifacts: ignored `.artifacts/connectors/tests/gmail-real-read/`,
`.artifacts/connectors/pc-installation/3f02a9e-reviewed/`, source-pinned deployment
receipts and unique content-free `diagnostics/receipt-*` / `installed-*` receipts.

## Next narrow content correction (9 October morning)

The richer real query above exposed a separate gap: a successful search preview is
not a full-message/thread read. The model sometimes ignored the available read
capability and answered details from previews. The correction remains inside the
existing owned Gmail search, not another agent, model planner or Live instruction.

- Optional `readMode` defaults to `preview`, preserving older backend clients.
  The desktop supplies a bounded read hint only if the actual backend schema
  supports it, using the frozen human request rather than model/email text.
  Clear read/details requests use `message`; conversation summaries use `thread`.
  Count-only searches, quoted search terms and explicit header-only requests remain
  previews. Ambiguous requests still have the existing explicit read tools.
- The first selected header GET is **replaced**, not supplemented, by the existing
  message/thread GET and normalizer. At most one content sample per search; original
  parallelism, optional four-second deadline, result/node/text limits, cancellation,
  Firebase/account ownership and operation metering are retained. All returned
  content remains external untrusted data and is absent from durable operation logs.
- Validate the selected message, parent thread and child thread identities. Missing,
  rate-limited, timed-out or bounded content is partial, not a completed comprehensive
  read. Authorization failure still fails closed. Page cursors/references remain
  available for further authorized reads.
- No global Live prompt, 62-tool catalog, audio/VAD/STT/playback, selection/translation,
  screen, BA, ordinary actions, billing/prices or public release changes.

Causal tests against an isolated exact `3f02a9e` archive: backend **8 failing / 35
passing**, desktop **9 failing / 84 passing**. Original receipts are retained.
New source gates: desktop **4,169/4,169**, backend non-emulator **932/932**, zero
failures/skips. The unchanged database layer's earlier 14/14 fresh-emulator proof
is retained, not falsely represented as a new run. Initial backend build and wording
assertion failures are preserved; the original safety wording was restored rather
than weakening its assertions. Secret scan and whitespace checks passed.

Current installed/deployed runtime above is still `3f02a9e` at this source checkpoint.
Real product content/answer latency, staging deployment and installation verification
for this next correction are **pending**. Do not call them PASS from unit tests.

### Rejected real content trial — preserved, not installed

`2b0888a` passed source gates and was deployed only to reviewed staging revision
`00018-fik`. Real multi-part reading did receive normalized content, but its inferred
whole-topic phrase query matched an indirect forwarded digest instead of the actual
event, yielding incorrect location/registration details. It also incorrectly claimed
no partial results despite a bounded thread. **Answer fidelity FAIL**, not acceptance.

Staging traffic was restored to **`00016-vic` / `3f02a9e`**. The installed candidate
remained `3f02a9e` throughout. The rejected package and original failed receipt are
preserved in ignored `dist/connector-rejected-archives/2b0888a-edf0d45d/` and the
diagnostics directory. No public release, new send or financial change occurred.
The initial `/health` route probe returned 404 (wrong route); subsequent actual
`/health/live`, `/health/ready` and catalog checks were 200. Hosted connector UI
checks passed 19/19 and theme checks 4/4; these are not real OAuth acceptance.

The next causal correction is strictly generic: for a model-inferred **whole quoted
topic** only, preserve all words as an AND query rather than accidentally requiring
an exact contiguous phrase. User literals, filters, Boolean/negative expressions,
controls and existing cursors remain unchanged; no extra search/model call is added.
An exact current source test failed on that boundary before the correction. Actual
query used remains explicit; matching is still not semantic proof.

The connector operation receipt also explicitly marks typed bounded/partial evidence
and forbids claiming comprehensive coverage. This is emitted only for an actual
partial connector result, not added to the global Live/system prompt. Next-source
provider/installed answer acceptance remains pending until actually exercised.

Next-source gates passed **4,171/4,171 desktop** and **939/939 non-emulator backend**,
zero failures/skips (`connector-topic-content-*-final.trx`). Query-boundary causal
receipt: one failing/two passing before correction; operator/control/cursor and
partial-receipt coverage added. No database source was changed. The lightweight
classification check still made zero HTTP/model/discovery calls for 30,000 ordinary
tool names; this is not whole-session audio latency proof.

## Finite 07:13 IST continuation — source reviewed, release held

See [the complete continuation report](CONNECTOR_CONTINUATION_2026-10-09.md)
for `b986706`/`cbcaa7d`, all subsequent causal and real-query failures, final
4,192/4,192 desktop and 939/939 backend gates, 36-notification/604-message counts,
actual cancellation, natural answer timing, installed/hosted UI and remaining
external gates. Reviewed `00024-cal` remains **zero traffic**; accepted staging
`00016-vic` and installed `3f02a9e` remain unchanged. Complex-answer fidelity is not
closed by one passing answer. No new public release or self-email was attempted.

The scheduled one-off continuation was deleted after verification, and the final
actual allowed-state GET confirmed Jarvis stopped. Preserve every earlier failed
receipt and the original send fence; do not recreate indefinite polling.
