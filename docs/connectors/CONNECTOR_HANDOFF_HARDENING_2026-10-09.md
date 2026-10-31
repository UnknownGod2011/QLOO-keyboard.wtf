# Connector handoff hardening — 9 October 2026

Status: **source verified in part; not all-provider or public acceptance**.
Starting/install rollback: `6402e74`, protected by
`checkpoint/connectors-private-6402e74-20261009` and its full private package.
Public executable/download, public website and accepted staging traffic remain
unchanged. Personal/API diagnostics only; no Managed credits or new external writes.

## Small causal corrections

1. **Fresh observations:** same-turn `connector_control result/status` previously
   reused a cached successful `executing` observation. Only those two read-only
   controls bypass the attempt cache. Execute, resolve, cancel, complete and
   change-account deduplication remain unchanged. Two before-fix failures and
   seven effect-dedup passes are retained; corrected focused gate: 354/354.
2. **Invalid references:** malformed task/operation/account references previously
   escaped as `ArgumentException`, producing only a generic dispatcher error.
   Control now reports `invalid_reference`, its finite field name and
   `this_proposal_dispatched=false`. No current reference is substituted or
   disclosed; well-formed foreign/ended references still fail authority checks.
   Eleven before-fix failures and one preserved authority pass are retained.
   Corrected focused gate: 327/327, before the three extra wrong-type cases.
3. **GitHub issue overviews:** long bodies consumed the existing result budget
   before later issue titles. Normalize useful metadata for the entire page first,
   then share the unchanged text/wire allowance among bounded body excerpts.
   Per-body truncation is explicit. Original and intermediate Unicode failures
   are retained. 192 focused connector and 981 full non-emulator backend checks pass.
4. **Empty inferred subject searches:** two natural count-only repeats returned
   empty searches and never obtained a verified total. Sanitized query shapes
   show a sender filter and multi-word subject filter, not their exact wording.
   The existing single empty-topic fallback now also handles a simple positive
   `subject:"multi word phrase"` with separate subject-scoped alternatives.
   Sender/date/negative filters remain exact; explicit/default queries,
   continuations, existing results and Boolean/unsafe phrases never broaden.
   Broader matches remain `topicMatchVerified=false`, not a count/category claim.
   Eight pre-fix failures and 63 preserved passes are retained. Final full
   non-emulator backend gate: **1,004 passed, zero failed/skipped** (13 s).
   Gmail supports subject-scoped search and brace alternatives; API and web
   matching are not identical. [Official Gmail search guidance](https://developers.google.com/workspace/gmail/api/guides/filtering).

Runtime edits are restricted to connector control, the connector read-observation
predicate, issue-result normalization and bounded inferred read refinement. No model/global prompt, transport,
microphone, playback, BA engine, selection/translation, screen, billing or Helper
change. The original one-email and one-Form fences remain protected.

## Real product-path evidence and limits

The new ignored diagnostic can use the compiled `LiveToolCallDispatcher` and its
actual guard instead of merely serial controller calls. It uses the existing
full Personal context (19,509 characters / 62 tools), authenticated product backend
and real gateway/provider. It records control-reference shapes, never references
or private message bodies. It is not physical microphone or audible-answer proof.

- Combined event/thread/count: four Gmail read capabilities; all three goal
  receipts have evidence; verified 36-message count; partial thread coverage
  disclosed. First transcript 27.843 s, last 40.922 s; seven product HTTP requests,
  no extra planner/writes, Gmail activity owner and terminal cleanup verified.
  Receipt: `receipt-NaturalComplexDetails-56a514fd3d664394b839c09c868c2658.json`.
- A count-only repeat did **not** obtain a verified count: two empty searches and
  one locally rejected count proposal. Preserve this failure; an answered turn
  or completed search task does not prove the requested mailbox total.
  Receipt: `receipt-NaturalDevpost-28fead2f4070463c93ba2487ffe67b94.json`.
- The earlier installed combined failure (two resolves plus malformed control,
  no reads) remains preserved. Its exact invalid field was not captured; do not
  attribute it to 1011, billing or claim these source tests close it.
- The earlier installed summary's independently authenticated status GET now
  verifies one completed operation. The first DNS-failed verifier receipt remains.

## UI and performance

Fresh browser checks: 15 functional fixture groups, four light/dark contrast groups,
two Settings navigation groups and four hosted signed-out account/theme groups
pass, with no page/console errors. Names, text, hover/active/disabled controls are
readable; 320 px layouts do not overflow. Nine native provider marks and activity
owner/terminal/late-response behaviour have renderer/state tests. No unnecessary
UI redesign. Real installed popup visual and audible acceptance remain separate.

Existing hot-path measurement: 30,000 classifications in 2.7519 ms with zero
connector HTTP, discovery or extra AI calls. Ordinary installed arithmetic also
made no connector request. This does not measure whole spoken-answer latency.

## Remaining release gates

- Installed complex execution is now independently verified; semantic answer and
  count-only fidelity must still pass reliably. Alternate source count wording
  failed earlier and passes a later 19.007 s repeat without a count-code change.
- Real disconnect/reconnect/revocation, account-switch and multi-device acceptance
  require a safely restorable OAuth login; mocks/UID tests are not real acceptance.
- Slack/Discord staging custom OAuth, Meet availability, Google public consent and
  Reddit/LinkedIn branding remain external gates in `STAGING_AUTH_CONFIGURATION.md`.
- Current Drive is name/metadata search; Docs/Forms creates are blank resources;
  Discord is normal-user account/server reads. Unsupported actions stay unavailable.
  Do not describe this bounded registry as full parity with a major app's tool set.
- Independent Personal Live 1011/speech-cutoff remains open. No production 3.8
  switch or stability claim from short successful connector turns.

Full desktop gate after private route binding: **4,352 passed, zero failed/skipped**
(3m48s), including all three additional wrong-operation-type cases. Secret scan
and whitespace checks pass.

## Held deployment and real read verification

Backend source `4664440838c56ef30c1d4ec5feebded813f670ca`, backend tree
`503649248076cb7ff2d4b1feac9ed59fbac80f48`, is frozen and deployed to held
`00027-xid` / `gs-4664440`. Accepted staging traffic remains 100% `00016-vic`.
Before/after runtime digests prove unchanged environment/secret references,
service account, concurrency and timeout; no public deployment or promotion.
The private build alone now binds this exact held route; public configuration is
untouched. Receipts remain ignored under `.artifacts/connectors/deployments/`.

- Empty inferred subject search: authenticated keyboard.wtf controller/gateway
  read verified the single refinement and 41 candidate messages, no semantic
  category/count claim. Four capabilities, no model or write, 2.498 s read plus
  verification (cold resolution 3.500 s); Gmail activity and terminal cleanup pass.
- Existing GitHub issue page: product repository read supplied the exact repository
  reference, then issue read verified two issues, both complete overview metadata
  and body excerpts; two capabilities, no model/write, 1.566 s issue read plus
  verification, GitHub activity and cleanup pass. These actual bodies were short;
  long/Unicode-body fairness remains the deterministic test evidence, not a claimed
  real long-body reproduction.

Those two probes used the source-compiled product against the exact held backend,
not direct Composio scripts. Subsequent matching installation and installed
combined/count/Gmail/cancel checks are now recorded in
[current status](CURRENT_CONNECTOR_STATUS.md) and
[installed acceptance](INSTALLED_CONNECTOR_ACCEPTANCE_2026-10-09.md).

## Cancellation wording follow-up — source only

An installed request “Cancel that connector task” was submitted while the provider
read was running, but was not recognized by the immediate local matcher. The model
cancelled later, after the read had returned. Bare “Cancel” used the existing local
path and cancelled the in-flight read immediately; independent authenticated GET
confirmed Cancelled, with no execute completion in the native trace and no later
connector activity during arithmetic.

The sole runtime follow-up adds optional `connector` before `task` in that anchored
matcher. Same single-owner and competing translation/BA checks remain unchanged;
questions, negation, quoted content, compound requests and subscription cancellation
remain excluded. Before: four new failures, seven preserved passes. After: 292
focused checks and 4,362 full desktop checks pass, zero failures/skips. Source
`db080ea`, unchanged backend tree. No Live/audio/prompt/backend change, and this follow-up is not yet
installed. Original delayed-cancel and successful bare-cancel receipts remain.
