# Connector count/coverage checkpoint — 10 October 2026

**PRIVATE ENGINEERING CHECKPOINT; NOT PUBLIC OR ALL-PROVIDER READY.**

Installed private desktop is now `7a1f6f7`, pinned to `a25d094` / `00028-boh`.
Previous `df5cdf1` / `4664440` remains a protected full rollback package.
Reviewed backend `a25d094` / `00028-boh` is separately deployed with **zero traffic**;
its health and a real authenticated product count returned successfully.
Accepted staging traffic stays on `00016-vic`. Public app, website,
download, billing, Helper, audio and selection paths have not been changed.

## Proven causes and narrow source correction

- A single natural mailbox-count task has no mixed read plan. Its settled-answer
  path previously retired a search-only task without flagging the missing total.
  Both premature explicit completion and the ordinary answer boundary now check
  that owner's verified count or exhaustive, decodable same-query search pages.
  The answer boundary still ends authority and clears activity; unfinished work
  never remains routable by later unrelated speech.
- Exact zero counts and marked partial/lower-bound counts remain valid evidence.
  Search samples/page sizes, other tasks' counts, malformed counts and partial or
  undecodable pages do not become totals. Category correctness and final arithmetic
  still require independent answer review; this is not a semantic classifier.
- Existing count rejection adds a finite reason, and operation receipts expose a
  small task-scoped evidence status. No new global Live prompt, model planner,
  query rewrite, provider retry, persistent mail memory or authority layer.
- Count intent excludes counts of words, attachments or addresses; quoted search
  content and explicit negation remain data, not mailbox-total instructions.
  Ambiguous single quotes are masked conservatively. Bounded quote-parser
  regression checks cover repeated malformed openers, not just normal prose.
- GitHub descriptions/coverage disclose the existing fixed `state=open` filter
  and possible pull requests. Endpoint, arguments, continuation, budgets and writes
  remain unchanged. It is deployed only in held `00028-boh`, not accepted traffic.
- Verified partial thread/message content now produces one existing visual
  coverage notice when task authority ends. More search matches alone do not
  imply that the requested latest message is incomplete. Cancelled, unverified
  and complete-content reads do not produce the notice; no new speech is added.
- Independent review reproduced an existing final-serialization gap: an inner
  complete eight-message thread lost body text in the outer search budget while
  both content flags remained complete. The connector normalizer now compares
  retained nested content after cleaning, propagating actual loss into its
  existing flags. Small coverage flags precede external identities/previews. Node, text,
  wire, provider-call and account/ownership limits remain unchanged. Metadata-only
  trimming does not mark an unchanged body partial. This backend fix is in held
  `00028-boh`; original runtime bindings/traffic match independently retained
  deployment receipts. Matching installed execution is now proven below; this
  does not imply complete natural-answer fidelity.

## Real product evidence — preserve failures

Every run below uses the compiled product controller/dispatcher, authenticated
keyboard.wtf backend and existing provider adapter. No direct provider script is
counted as acceptance. Personal/API only; microphone/playback not exercised.

| Run | Actual result |
| --- | --- |
| `NaturalDevpostV2-2337eab4…` | One empty inferred search; no verified count. New incomplete notice once and UI/owner cleanup. First/last transcript 13.194/14.849 s. Four product HTTP requests, four capabilities, no extra planner or writes. |
| `NaturalDevpostV2-091545c6…` | After short receipt guidance, still no verified count. Incomplete notice once and cleanup. First/last transcript 12.933/16.297 s. Guidance alone did **not** fix natural count reliability. |
| `NaturalComplexDetails-26a889db…` | Three searches plus one verified complete count of **36**, seven product HTTP requests, four capabilities, no extra planner/write. First/last transcript 28.406/44.951 s. One thread was truncated; transcript did not disclose partial coverage. Execution passes, full answer fidelity does not. |
| `NaturalDaisy-d327b6fd…` | Fresh natural thread summary executed through four capabilities/four product HTTP requests; first/last transcript 15.034/20.920 s. Verified partial thread produced exactly one new visual notice and task UI cleared. The transcript itself still omitted a qualifier; full semantic fidelity is not claimed. |
| `NaturalDevpostV2-1830bcf0…` | Fresh alternate count wording verified **36** and the answer used that number. First/last transcript 18.003/19.118 s, five HTTP requests/four capabilities, no extra planner/write/start speech, UI cleared. No query/evidence policy changed between the preserved failures and this success: planning remains intermittent. |
| `NaturalComplexDetails-b55b28fe…` | Live setup succeeded, then no connector/tool/answer arrived; provider socket closed 1008 after 157.432 s. Zero connector HTTP/write/microphone calls. This is not connector acceptance, not the earlier 1011, and the sanitized reason does not establish a local cause. |
| `CountVerified-eca59de3…` | Matching compiled desktop controller/dispatcher against held `a25d094` returned verified **36**, one page, complete. Four product HTTP requests/four capabilities, 6.301 s total, no AI call/write, activity cleared. This exact-query product execution is not natural-model acceptance. |
| `NaturalComplexDetails-dc7b4f5d…` | New held backend: three verified searches and a complete count of **36**, seven HTTP requests/four capabilities. First/last text 30.728/42.317 s; UI clears and one visual partial-content notice. The transcript still omitted a partial qualifier; execution passes but full answer fidelity is not certified. No extra planner/write/start speech/microphone input. |
| `NaturalDevpostV2-2da9e75f…` | Fresh compiled product dispatcher against held `a25d094`: one search and five message reads completed, but **no verified count**. Two count proposals lacked inferred-category evidence: first `inspect_category_first`, then `missing_sender_filter`. Both proposals were blocked before count dispatch; the model continued message reads instead of correcting the query. First/last text 43.371/49.815 s; nine product HTTP requests/four capabilities, no extra planner/write/start narration. One incomplete notice, activity cleared. This is a preserved natural-task failure, not successful count acceptance or a 1011 failure. |

No 1011 in these short runs proves neither long-session stability nor a speech-cutoff
fix. Original one-send email and one-create Form fences remain untouched.

## Gates and next step

Desktop final reviewed source: **360 focused / 4,434 full passed**, zero
failed/skipped. Preserved before-fix regressions include the real count-completion
gap, quoted/negated intent and two pagination-only false-warning cases.
Backend coverage fix: **108 focused passed**; two failing/one passing pre-fix
serialization cases are preserved. A further long-identity before-fix failure
proved coverage mode itself could be erased; its small flags now precede identities
too. **1,013 full non-emulator checks passed**, zero
failed/skipped. Held deployment and matching private installation are verified;
natural semantic acceptance remains separate.
Earlier Firestore full-suite failure remains recorded separately, not relabelled.
The first matched-route desktop run retained four failures from one stale expected
backend URL, not product behavior: 4,430 passed / four failed. The exact private
pin was corrected; 53 focused configuration/security checks pass. Its corrected
full run is a separate retained gate: **4,434 passed, zero failed/skipped, 3m58s**,
not an overwritten failure receipt. Independent GET-only verification confirms
the exact held source/revision, every previous rollback tag, original runtime
bindings and unchanged accepted traffic/public EXE. An earlier verifier used an
incorrect shorthand tag and returned false; its original receipt is retained,
not treated as a lost rollback or overwritten by the exact-tag verification.

The matched private 7a1f6f7 package is installed: 24/24 payload hashes and exact
26-file installed set verified, backed-up profile unchanged by installation,
public EXE/startup/native host unchanged. Installed complex Gmail independently
completed three searches plus a count; Docs and ordinary Gmail reads also completed.
New cancellation wording cancelled an actual in-flight read; late settlement could
not complete it and later arithmetic made no connector call. Both actual Settings
themes and hosted signed-out/mobile connector UI passed contrast/search/layout
checks. On the later owner-launched candidate, a fresh installed Gmail read was
independently verified and the owner confirmed its Gmail logo/status/blue-circle
popup. First audio was 7.851 s after owner ready; 39/39 received audio parts reached
playback. Following arithmetic made zero connector calls. This human-observed
popup proof is not a successful screenshot or full spoken semantic acceptance;
original capture/tool failures remain preserved. No speculative app/audio/launch
edit was made. Jarvis is stopped; the owner-launched private GUI remains open.

Keep source, installed, real execution and semantic-answer proof separate.
Natural count variations and truthful partial answers remain
gates; do not publish this as equivalent to an unrestricted major-app connector set.
OAuth lifecycle/account-switch/provider-consent gates remain in
[current status](CURRENT_CONNECTOR_STATUS.md) and
[staging configuration](STAGING_AUTH_CONFIGURATION.md).

Ignored original receipts remain under `.artifacts/connectors/diagnostics/` and
`.artifacts/connectors/tests/count-completion/`. No credentials, account identity,
private message/document bodies or unredacted query contents are stored here.
Five exact unused private rollback EXEs were reversibly compressed: hashes
unchanged, zero deletions, approximately 989 MB recovered. Original receipts and
installers remain protected. Initial source probes did not change microphone/output
settings. Installed testing subsequently applied only the owner's explicit
microphone-mute/output-2 preference after a read found different device settings;
fresh endpoint reads verify that preference. No application audio code changed.
