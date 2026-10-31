# Real Gmail product-path checkpoint — 8 October 2026

**NOT READY for public connector promotion.** Stable public 1.4.21 is unchanged.
Starting source: `0572962b91cc3610508dd15ba97871358faa8549`, branch
`codex/connectors-foundation`. Protected tag:
`checkpoint/connectors-before-real-gmail-20261008`.
The original `3ba1e14` candidate/package remains protected. The separate connector
candidate was updated to desktop source `90a71f5203e2c0914e82aa53eb735e4421d956b3`
after its full source gate. All 24 installed files match the package. Backend-only
query guidance follows that desktop checkpoint; it is not a public app update.

## Real authenticated checks

The test path loads the actual candidate assembly, its protected staging desktop
session, `PaidAccountService.ConnectorRequestAsync` and `JarvisConnectorController`.
It reaches keyboard.wtf's authenticated connector API, UID-owned task/gateway and
provider adapter. These are **not direct Gmail/Composio scripts**. Headless controller
status is not a physical popup or microphone acceptance claim.

| Requested case | Independently verified product result |
| --- | --- |
| Codex event | Search and two identified reads succeeded; updated event time and registration status checked. Private answer belongs in the user conversation, not source/logs. |
| Daisy conversation | Search, message and latest-thread reads succeeded; recent transfer discussion and latest replies verified. Truncated thread bodies are explicitly marked; individual message references remain available. |
| Devpost winning notifications | **36 distinct messages**, final observed-template query, four pages 10+10+10+6, no remaining page token, three distributed message/body samples. Count is emails, not distinct competitions/prizes. Broad `resultSizeEstimate=201` was not used as a count. |
| One authorized self-email | Exactly one send dispatch; independent product search found one Sent message with the exact requested subject/body/recipient. No second send. The original operation remains truthfully `outcome_unknown`; no journal was rewritten. |
| Explicit cancel | Real in-flight read cancelled; server task Cancelled, UI status cleared, no completion notice or write afterward. Original safe cancellation receipt retained. |
| Calendar / Drive | Both authorized product read paths completed with verified empty results for their bounded queries. This proves access, not nonempty-content quality. |

Mailbox contents, account identities, tokens and message/provider IDs are excluded
from this tracked report. The ignored self-send fence must be preserved: **never
rerun the self-send test**. Its initial unknown result is retained alongside the
independent delivery verification, not relabelled as an initially successful send.

## Demonstrated causes and narrow changes

1. Search omitted pagination and exposed only an estimated count. Add a validated
   optional page token and bounded 1–100 page size. Return actual page item count
   and continuation facts, not the provider estimate as total evidence. Category
   semantics still require sample inspection and a justified final query.
2. MIME alternative text+HTML duplicated content, consuming the bounded thread
   budget before latest replies. Prefer plain text when present; HTML remains the
   fallback. Return recent thread messages newest-first and bounded references
   before bodies. Keep all existing size/depth/privacy limits.
3. Gmail rewrote the supplied RFC Message-ID. For an acknowledged send, independently
   verify its actual resource ID, Sent label, exact recipient, subject and plain
   body. Missing acknowledgement still requires deterministic reconciliation;
   uncertainty never authorizes another send. Mismatch/lost-ack tests fail closed.
4. Installed natural Gmail request chose Browser Automation. Legacy instructions
   described inbox work as browser-only and all email actions as draft-only. Replace
   only that guidance with connector-first routing and the existing fallback when
   unavailable. Keep legacy draft tools draft-only and existing write confirmation.
5. The global lifecycle rule prohibited tool calls even for a connector event whose
   required next step was result retrieval. Scope its exception to connector results;
   translation and browser lifecycle behavior retain their no-tool rule.
6. A 500ms handoff converted nearly every measured 1–3s read into a separate Live
   turn. Test a connector-only 5s inline ceiling: completed work returns immediately,
   genuine slow work/ordinary proposal interruption still retains its owned worker.
   This is not a minimum delay or a change to microphone/playback/BA/translation.
7. Add content-free resolve telemetry for deterministic registry/capability selection
   and authoritative account lookup. No normal non-connector path is instrumented.
8. A measured natural count required eight model/tool exchanges for 36 IDs with
   the old default page size of five. Raise only the bounded ID-search default to
   50; explicit limits still win, bodies are fetched separately, and pagination
   remains mandatory when a continuation token exists. Measure staging afterward.
9. A repeat natural trial invented an overly narrow exact phrase and reported no
   matches. An empty-search receipt now explicitly scopes that evidence to the
   actual query and asks the model to broaden inferred descriptive wording while
   preserving user-supplied exact queries. No automatic query rewrite/retry, extra
   model call or mailbox-specific hardcoding is introduced. The failed trial stays
   recorded; natural search fidelity still requires repeat acceptance.

The Live instruction changed by only 41 characters net before the inline policy;
no provider toolkit catalog or new planner is added to its permanent prompt.

## Performance — measured tiers, not invented stage splits

| Controller product case | Resolve/account/capabilities | Provider execution + verification | Total |
| --- | ---: | ---: | ---: |
| Codex | 1,252ms | search 1,395ms; reads 1,295/1,370ms | 5,915ms |
| Daisy | 739ms | search 1,223ms; read 2,400ms; thread 1,383ms | 6,133ms |
| Devpost | 1,003ms | four searches 1,248–1,650ms; samples 1,655–2,342ms | 13,143ms |
| Devpost bounded 50-ID page | 942ms | one search 1,290ms; three samples 1,320/1,617/2,379ms | 7,959ms |

Three read capabilities, four only for the explicitly authorized send. No extra
model planner, Managed generation, purchase or connector-model credit charge.
These controller totals exclude voice generation/playback. Resolve timings above
combine authenticated transport, account lookup, registry selection and task storage;
they must not be presented as separately measured stages.

Separate staging revision 00008-22s server telemetry measured registry/capability
selection at 0–3ms, authoritative connection lookup at 422–671ms, total resolve
at 471–797ms, and successful Gmail provider execution at 473–1,115ms. These are
server measurements, not a claimed microphone-to-speaker latency breakdown.

30,000 non-connector broker classifications took 2.1915ms with zero connector HTTP
requests, schemas or model calls. A full-62-tool natural arithmetic trial answered
four with zero connector requests/capabilities. Neither measures the complete
physical microphone-to-speaker hot path, or proves all model decisions always correct.
Full-tool natural Open Spotify / Close Chrome trials selected the existing local
tools in 2,137ms / 1,927ms with zero connector HTTP requests or capabilities. Tests
stopped at the proposal; they did not open an app or close the user's browser.

Installed actual Settings: 12 catalog rows, four connected Google capability rows
sharing one account, no page errors. Original automatic catalog response about
1.4s; updated-candidate restart check 2.7s including page/bootstrap/account setup.

## Natural Live findings — do not overclaim

Full current Jarvis instruction and all 62 declarations were tested with the actual
controller/authenticated backend. No external write was allowed in these trials.

- Gmail resolves naturally after the narrow routing correction. Daisy's actual
  thread was read and accurately summarized. Activity states cleared.
- One full-tool natural trial returned the correct Devpost **36**, traversing
  eight pages (5+5+5+5+5+5+5+1) until no continuation remained. Total 32,668ms
  includes model generation and a five-second quiet observation. Three read
  capabilities, no extra planner. Earlier wrong totals remain recorded; one pass
  is not an always-reliable guarantee. Category bodies were independently sampled
  through the product path, not read by the model during this particular trial.
- Repeat trials invented overly narrow quoted phrases. One falsely concluded
  zero; the initial empty-result hint made the next answer query-specific but it
  still asked permission instead of continuing a broader read. The hint was
  narrowed to explicitly continue an already-authorized read while preserving
  user-supplied exact queries. **Repeat natural fidelity remains a release gate.**
- Final revision 00011-8r9 trial did automatically broaden the empty inferred
  search, then read five messages from a broad 39-result category. It did not
  recognize/refine to the actual winning-notification template or answer the exact
  count. **This is NOT a natural counting pass.** Total 53,069ms including generation
  and quiet observation; three capabilities, ten connector HTTP requests, zero
  writes/extra AI calls. Searching Gmail / Reading email states were sampled while
  real work ran and cleared afterward. Do not hardcode this mailbox or infer
  semantic correctness from a successfully executed search.
- The asynchronous handoff sometimes caused start narration/internal lifecycle
  text to appear in diagnostic model output. The inline-ceiling experiment reduces
  unnecessary handoff but is not yet a complete physical-output certification.
- Latest inline-path Daisy trial accurately summarized the thread without a start
  lecture or internal lifecycle echo. Total 40,508ms includes the long generated
  answer and five-second quiet observation; resolve/search/thread were 741/1,270/
  1,459ms. This does not claim 40 seconds of connector provider execution.
- Headless wire trials do not verify installed sound, popup placement or microphone.
- Updated installed Jarvis text-entry test selected connector_resolve directly,
  performed two real connector executions, completed the task, published all 49
  received audio parts to playback with zero missing sinks, and ended normally.
  It did not call BA or disconnect with 1011 in that bounded run. Heard sound,
  answer-content fidelity and physical popup placement were not independently
  inspected. Native Computer Use inspection could not initialize its kernel.

## Reliability remains separate

The isolated 3.8 trial accepted the full tool setup but did not establish an
improvement: one run failed after a search; another closed with **1011** shortly
after setup before connector execution. Production/current model was not switched.
No fair reliability percentage, cost comparison, interruption or ten-minute rollover
acceptance is claimed. Keep the earlier 1011 comparison report and failed trials.

Installed speech-phase evidence previously showed received audio parts reaching
the playback queue, with explicit provider interruption clearing queued audio. This
does not prove whether interruption was intentional speech, echo or room noise.
No speculative VAD/STT/playback rewrite was made. Personal 1011 and speech cutoff
remain open, independent from the demonstrated backend Gmail reads/delivery.

## Gates / rollback

Receipts: ignored `.artifacts/connectors/tests/gmail-real-read/` and diagnostics.
Keep failed emulator transaction-lock and loopback-handshake receipts; successful
isolated rechecks do not erase those failures. Full desktop 4,118 passed before the
inline policy/new test. Final-source desktop regression: **4,119/4,119 passed**,
zero skips. Final pagination-default backend connector unit/Firestore tests:
**91/91 passed**, zero skips. These are not a claim that every backend test passed;
the separate older full-backend Luma contention failure remains documented.
After the empty-result guidance: 78/78 unit tests passed. The combined unit/database
run passed 91/92 with a repeated emulator transaction-lock timeout in the existing
concurrent-connect fixture; unchanged isolated database class passed 14/14. Keep
both receipts. The final continuation wording rebuilt and passed 78/78 unit tests.

Public connector promotion still needs installed
popup/heard-output proof, repeat natural search fidelity, real disconnect/reconnect and another authenticated
identity/device check, physical AI billing correlation, Google public OAuth clearance
and representative additional-provider consent/branding. Google Super currently
shares Gmail/Calendar/Drive/Docs; Forms/YouTube need their own scope grants. GitHub
is configured but not end-user verified. Slack/Discord external steps are documented
in `STAGING_AUTH_CONFIGURATION.md`; Meet is undiscovered; Reddit/LinkedIn remain
Coming Soon. Do not let these external gates block independent Google work.

Staging revisions 00004-g2p, 00005-hnk, 00006-qd4, 00007-xp9 and 00008-22s remain rollback
receipts. No production release, public download/feed, Helper, billing formula,
credential ownership or existing saved-data migration was changed.

Desktop installer archive: `dist/connector-candidate-archives/90a71f5-349c37fe/`.
Installer SHA256 `349C37FEB59B43F8070CE697C7625DCD034F8CF6FEEFDF51A086632D2A623A44`.
Installed EXE SHA256 `A6CE7C57317F8E0E7FD135DC2260F9F7204EC8F01F654B2DD336680F842C3EAF`.
Public EXE/startup/native-host registration independently remain unchanged. Existing
profile and prior package protected; no forced process termination, profile transfer
or uninstall. Installer log records success/no reboot. A diagnostic receipt-writing
error occurred afterward; independent full-package verification passed without
rerunning the installer. Compiler remains unsigned/non-commercial-mode: not public
commercial packaging clearance.

## Remaining provider and product gates

| Area | Actual status |
| --- | --- |
| Gmail / Calendar / Drive | Authenticated product reads passed; Gmail send delivered once; natural count refinement not yet reliable. |
| Docs | Shared Google authorization/capability mapping; no real document task acceptance in this pass. |
| GitHub | Custom staging config exists; owner login/consent requested before real reads. |
| Forms / YouTube | Separate custom configs; additional real scope grants and product checks required. |
| Slack / Discord | Staging custom OAuth fields externally required after both-project discovery; exact configuration steps documented. No bot or fabricated staging reuse. |
| Meet | Existing purported configuration not found; not enabled on an invented ID. |
| Reddit / LinkedIn | Coming Soon; managed consent branding does not satisfy production branding requirement. |
| Identity / safety | Unit/database UID, ownership, multiple accounts, cancel/late-response, unknown-write fences passed in their documented tiers; real account switching/device/reconnect still pending. |
| UI | 15 rendered fixtures, 2 navigation fixtures, 4 hosted theme/mobile groups, 10 updated installed Settings checks passed; native popup/heard-output still not certified. |
| Billing | No connector-model debit or extra AI planner; Personal ownership observed. Physical Managed AI/connector correlation not certified. |
| Production | Stable public release unchanged; no connector production promotion. |

Final staging backend: revision `keyboard-wtf-connectors-staging-00011-8r9`,
100% staging traffic, source `74135a7d6338e37fe40f2e25acdb494b8813ab7c`.
Revisions 00009-4qp / 00010-hrt also retained. Hosted signed-out route checks
19/19 and hosted light/dark/mobile groups 4/4 passed after deployment. No real
OAuth is inferred from these browser checks. GitHub human consent is the next
external step; natural topic/category search remains an independent internal gate.
Do not introduce a new planner, change Live model/audio, or stack global prompt
rules merely to make a single mailbox-specific benchmark pass.

## Bounded headers experiment after the installed checkpoint

The repeated category trial exposed an evidence gap: `mail_search` returned only
opaque IDs, forcing serial full-message reads before the model could inspect even
a subject. The follow-up changes only the existing Gmail read boundary: a small
distributed headers-only sample on the first page (default six, maximum eight,
three concurrent reads, four-second optional deadline). Pagination and explicit
`metadataLimit=0` count queries do not add metadata requests. No permanent Live
prompt, desktop/audio, model, billing, OAuth configuration or provider adapter
change is included. This is not a Devpost-specific query rewrite or new planner.

Reviewed backend connector unit checks: **97/97 passed**, including sample bounds,
ID/thread matching, body exclusion, opt-out, continuation-page behavior, partial
metadata, authorization failure, cancellation, deadline, malformed pages, content-
free journaling and per-operation provider request counts. The earlier initial
93/93 receipt is retained. Real natural-query improvement and latency are **not
yet established**; source tests alone do not clear this gate. Installed candidate
and stable public artifacts remain unchanged while the staging experiment is
evaluated. Rollback: `checkpoint/connectors-installed-gmail-20261008` (`f4880a6`).

## Argument repair and repeat product checks

The headers experiment is deployed only to staging revision
`keyboard-wtf-connectors-staging-00012-bpd`, source
`61434afcf1a6c103a4530f365b6fad5a36fa7435`. A real product search returned six
verified distributed header samples without bodies. Its broad query had 39 hits;
that is **not** the winning-notification count. Full search/controller time was
3,065ms, resolve 3,506ms cold, total 7,031ms. Three capabilities, no extra model
planner, no writes. The first combined unit/database run exposed a cancellation
race at the metadata semaphore: a waiter could acquire after cancellation and
start another read. Recheck the token immediately after acquisition. After this
one-line correction, connector unit/Firestore checks passed **111/111**, zero
skips. Preserve the initial 110/111 receipt and the subsequent successful receipt.

A separate real natural trial generated malformed `arguments_json`. Strict parsing
correctly rejected it, but the generic dispatcher exception incorrectly implied
that even an undispatched read could not continue. The connector controller now
returns a precise `invalid_arguments`, `this_proposal_dispatched=false` receipt
before acquiring a worker/claim or touching the backend. The model may correct the
same authorized read. Writes still need the existing permission owner and durable
write fence; parsing is not relaxed and earlier write fences are not cleared.
Cancelled/expired task authority is still checked before argument processing.

- Controlled real product repair: malformed proposal made **zero HTTP requests**;
  a corrected read in the same task found the event messages. Total 3,604ms,
  resolve 1,373ms, search 1,785ms; no write, new permission or extra model call.
- Natural current-model Daisy trial accurately summarized the latest transfer
  thread. Resolve/search/thread 738/3,053/1,433ms; total 44,842ms includes generated
  speech and the diagnostic quiet window. Activity cleared.
- First natural current-model count trial after repair corrected malformed
  arguments and answered the independently verified **36**. Resolve/search/control
  843/4,120/105ms; total 19,706ms. Three capabilities, three HTTP requests, zero
  extra model calls/writes. The malformed proposal itself did not dispatch.
- The immediate repeat invented the quoted phrase `"Woo hoo winning"`, got zero
  matches and asked whether to search something else rather than broadening the
  inferred query. Resolve/search/control 1,145/1,503/110ms; total 19,532ms.
  **Repeat semantic fidelity is still NOT PASS.** Successful transport or the
  earlier correct count must not hide this failure. No mailbox-specific rewrite,
  extra planner or additional global prompt was added to chase the benchmark.
- One isolated 3.8 trial answered 36 in 17,147ms (resolve/search/control
  969/3,430/182ms), using the same 62-tool setup. Earlier 3.8 1011 failures remain;
  this is not a stability, cost, interruption or model-switch clearance.
- Full connector-candidate desktop regression after argument repair:
  **4,127/4,127 passed**, zero skips. The first new cancellation test expected a
  returned failure although existing cancelled-task behavior throws; its assertion
  was corrected to preserve that guard. Initial 96/97 receipt remains.
- Broker-only classification of 30,000 unrelated tool names took 4.291ms with
  zero connector requests, capabilities or AI calls. This is not complete voice
  latency proof.

These are authenticated keyboard.wtf controller/backend tests, not direct provider
acceptance. Headless audio generation does not certify physical microphone,
heard playback or popup placement. The installed desktop is still `90a71f5` until
a separately recorded exact-source package/update succeeds. Public artifacts,
Helper, credential ownership, billing formulas, Live/audio and selection behavior
remain untouched. The platform is **NOT READY** while natural-query fidelity and
the remaining real OAuth/identity/device/provider/physical acceptance gates remain.

## Exact installed checkpoint and task-owner follow-up

The clean `c4144f9d9239ed8e1e5ee4ee6ceb768093307f73` desktop was subsequently
packaged and installed in the separate Connector Staging Candidate directory.
Installer archive: `dist/connector-candidate-archives/c4144f9-cf3b11d3/`.
Installer SHA256 `CF3B11D316BD16EA0BAFD093EDBC8FD0A2291069823ED653690FB2E98BC9CC15`;
installed EXE SHA256 `6919AECEA5C81AAE46E1E0DEA6A13F527A71EDEDDABA150AFB3512906506219D`.
All 24 packaged files matched independently. Prior package/profile and source tag
`checkpoint/connectors-installed-c4144f9-20261008` are protected. The public EXE,
startup and native-host registration were unchanged. Backend staging revision
`keyboard-wtf-connectors-staging-00013-gjh` uses the same `c4144f9` source, with
100% staging traffic; previous revisions remain rollback receipts.

Final-revision product argument repair found two event messages: resolve/search/
controls 1,110/2,148/134/130ms, total 3,704ms. The malformed proposal dispatched
zero HTTP requests; three capabilities, no extra AI call or write. Real cancel
completed in 3,428ms and cleared activity, with no completion or write after
cancellation. These traversed the authenticated keyboard.wtf product path.

Installed Settings: **10/10 checks**, 12 compact rows, four verified shared Google
capabilities, signed-in PersonalOnly, no console/page errors. First automatic
catalog load including page bootstrap took 4,057ms after restart. Hosted checks
after backend deployment: **19/19 signed-out route checks**, **4/4 light/dark/mobile
groups**, no console/page errors. Neither tier proves real OAuth consent.

An installed natural Codex request reached `connector_resolve` and one verified
`connector_execute`: backend/client resolve 586/4ms, execute 2,846/7ms. All 51
received audio parts reached the playback queue, no missing sink or 1011 in this
bounded run. The following arithmetic request invoked no connector tool; all ten
audio parts were queued. A connector catalog request during the arithmetic answer
cannot be counted as zero global HTTP overhead without separating Settings refresh
from Jarvis routing. Goodbye ended normally; the allowed-state endpoint independently
reported idle. Native screen inspection was unavailable, so physical heard-answer,
content correctness and popup placement are **not** certified by these logs.

### Proven narrow owner gap

The installed answer omitted `connector_control complete`; the local task remained
ready after the spoken answer. A causal existing-registry test reproduced that a
later bare cancel still targeted this finished task. A second source check exposed
the same cleanup gap when confirmation/result retrieval occurred on a later real
human turn. Before fixing that boundary, **three of four new tests failed**; retain
`connector-answer-owner-before-fix.trx` and
`connector-answer-confirmation-before-fix.trx`.

The desktop-only fix joins the existing ordered answer observer to the existing
connector owner. End authority only after all steps settled and their actual
results were delivered, with no pending confirmation. Capture the delivery turn
before waiting; match its completed answer without changing the originating
identity, write fence, cancellation token owner or server policies. Unread/in-flight
background work and pending confirmation survive; stale cached controls cannot
republish completed results. No Live/system prompt, audio/VAD/STT/playback, screen,
translation, billing, Helper, OAuth/provider or website change is included.

Focused owner/confirmation tests after the delivery-turn fix: **70/70 passed**, zero
skips. This excludes the subsequently added completed-control test, which is included
in the first full owner regression: **4,141/4,141 passed**, zero skips. Final review
then added two adversarial receipt tests: cancellation/account switch after initial
task lookup could still return cached content. Both reproduced before the fix
(`connector-receipt-authority-before-fix.trx`). Recheck current authority under the
same result-delivery lock; reject without content if the task/account/session ended.
Final-source full connector-candidate regression including both adversarial cases:
**4,143/4,143 passed**, zero failed/skipped, 3m39s. Receipt:
`connector-delivery-owner-final-regression.trx`. The earlier 4,141 pass and all
causal failures are preserved, not overwritten. Source secret scan and diff checks
passed. No public promotion is authorized by these source tests.

An offline benchmark of the actual candidate test assembly ran 30,000 idle
answer-cleanup calls in **22.635ms** (0.0007545ms per call) and 30,000 unrelated
broker classifications in **0.4434ms**. Zero connector HTTP calls, announcements,
tool capabilities or model calls. This measures only those local hooks, not model
choice, Settings polling or whole microphone/speech latency. Package/installed
acceptance of this owner fix remains separate. Repeat semantic search fidelity,
real reconnect/identity/device and the external/physical gates above remain open.

### Installed owner-fix acceptance, 9 October

Desktop source/package checkpoint:
`8a4cccc6efdd6810131a4ddfc989613c3c13d1df`, source rollback tag
`checkpoint/connectors-task-owner-20261009`. The separate staging candidate was
updated once after backing up its fully stopped profile and complete previous
package. Installer exited successfully; all 24 installed files independently match
the reviewed package. All 23 non-executable resources are byte-identical to the
previous candidate. Protected Personal credential bytes, the public EXE and
startup/native-host registration remain unchanged. No production backend, website,
download/feed or Helper was changed; backend remains staging revision 00013-gjh.

- Installer archive: `dist/connector-candidate-archives/8a4cccc-91377d32/`.
- Installer SHA256: `91377D3214BAF4169AE1B05197FD49788EDDADFC9C114DCE5C81616E001E2A90`.
- Installed EXE SHA256: `8085ACBB4C6338F31894662883E44A192E17DC0232B1A8FAF49EAE9EA4542CAD`.
- Exact install/profile/package receipts:
  `.artifacts/connectors/pc-installation/8a4cccc-reviewed/`.
- Installed Settings: **10/10 passed**, 12 rows, four verified shared Google
  capabilities, same staging authentication and PersonalOnly, no page errors.
  Screenshot visually reviewed. Cold catalog including bootstrap: **6,646ms**;
  the startup log separately records token refresh headers **6,357ms** and catalog
  headers **1,157ms**. These overlap bootstrap and are not additive; this does not
  establish a local Settings regression or justify changing authentication.
- Real installed natural Gmail request reached resolve (**695ms headers + 9ms
  body**) and execute (**3,272 + 6ms**). First audio arrived **6.10s** after the
  Personal socket owner was established; the answer completed after **15.56s**.
  All 37 received audio parts reached playback; zero missing sink/interruption.
  The model did **not** invoke a completion tool. The existing ordered answer
  hook ended authority and the backend acknowledged `/complete` in **159ms**.
- Following arithmetic in the same session: zero proposed connector tools,
  normal answer, all ten received audio parts queued. A Settings catalog request
  (**842ms**) was also observed; do not confuse that with connector routing or
  claim zero global Settings network activity during an ordinary request.
- Goodbye ended normally after **257.15s**; allowed-state check confirmed idle.
  A separate authenticated **GET-only keyboard.wtf task-status** check afterward
  verified terminal `completed`, one operation, independently verified success.
  It made zero provider/model requests. Session cleanup did not overwrite the
  completed server task. No new email send or Managed credit use occurred.

These prove the installed task-owner fix and authenticated Gmail operation, not
physical microphone/heard-answer correctness or popup placement. No 1011 occurred
in this bounded session; intermittent Personal 1011/speech cutoff remain open.
Current source-only receipt tagging prevents later Settings runs overwriting an
earlier source's proof. Repeat natural category-search fidelity and the remaining
real OAuth/reconnect/device/provider/physical gates still prevent declaring the
connector platform READY for public promotion.
