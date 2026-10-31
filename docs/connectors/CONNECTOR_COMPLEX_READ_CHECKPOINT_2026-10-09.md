# Complex Gmail reads: current engineering checkpoint

Status: **NOT READY FOR PUBLIC PROMOTION.** This report supersedes the older
source snapshots, not their preserved receipts. No production deployment,
installer replacement, customer billing change or audio rewrite was performed.

## Source and deployment boundaries

- Connector worktree: `keyboard.wtf-connectors`, `codex/connectors-foundation`.
- Verified runtime logic: `30f4db2` (search cursor/query/goal binding); matching
  candidate configuration: `4dfe25355005ab5ed83e8bd4e7effcdfd8d55f5e`. This
  candidate-only route pin matches the same reviewed held backend; no service
  traffic or production URL is changed.
- Installed accepted staging desktop remains `3f02a9e`; staging accepted traffic
  remains revision `00016-vic`. Neither is the newly tested runtime.
- Real source probes use the reviewed held `gs-b986706` staging route, with no
  traffic promotion. The backend tree is unchanged.
- New candidate packaging is compile-pinned to the reviewed `gs-b986706` route,
  independently rechecked as revision `00024-cal` at zero traffic. The accepted
  installed app still uses its old configuration. Packaging and installed proof
  remain distinct; nothing is silently installed or promoted by this source pin.
- Rollbacks, original rejected receipts and the one-send email fence are retained.

## Narrow changes since the activity checkpoint

| Commit | Change and reason |
| --- | --- |
| `5153592` | Bound mixed mail reads/counts to literal user request parts and goal-scoped evidence; prevent omission or cross-topic borrowing. No extra planner. |
| `44eee41`, `4baf336` | Resolve a user-named sender alias and bare subject phrase only from that goal's observed headers; preserve exact/body queries and reject ambiguous senders. |
| `81d9882` | Keep partial thread/page coverage explicit through final settlement; a separately verified count remains exact. |
| `a5407ae` | For an eligible requested count proposal, inspect six headers through the existing owned read executor, then count only if proven. No guessed sender, unrelated category, extra AI call or write. |
| `30f4db2` | Bind search pagination to a verified cursor from the same goal and its exact actual `queryUsed`. A changed query starts without a cursor; an invalid continuation is not evidence of absent mail. |

The packaging-only integration changes `PaidStagingConfiguration.cs` inside
`KEYBOARD_WTF_CONNECTOR_STAGING_CANDIDATE` and its two pinning tests. Production,
legacy runtime configuration, website, profile and registration owners are
unchanged. Before packaging, the prior publish/installer were backed up to ignored
`.artifacts/connectors/packaging/before-matched-20261009`; prior accepted archives
and the installed `3f02a9e` are preserved.

Latest runtime files are `JarvisConnectorController.cs`,
`ConnectorMailReadPlan.cs` and the two optional broker argument declarations in
`GeminiLiveConversationService.cs`, with focused tests. Earlier native activity
files/resources remain as documented in the activity checkpoint. No global Live
instruction expansion, transport/model switch, playback/VAD change, selection or
translation change, new customer billing route, permanent content store or direct
provider shortcut was introduced.

One owner retains at most 16 operation receipts. Count discovery uses that same
owner, account, executor, cancellation and deduplication path. Ordinary speech
interruption does not cancel an already authorized owned read. Explicit cancel,
account/session change and shutdown invalidate its authority. Reads do not create
external write permission; the original write journal/fence remains unchanged.

## Evidence and qualifications

- Real path: compiled product controller → authenticated `PaidAccountService` →
  keyboard.wtf backend → normalized connector executor/provider adapter.
- Natural diagnostics use Personal/API mode, the unchanged full 19,509-character
  setup and 62 permanent tools; Gmail resolves four task-scoped capabilities.
  Zero extra planning model calls and zero sends in this pass. This is text-driven
  Live verification, not physical microphone/heard playback acceptance.
- Exact `81d9882`: **4,266 desktop tests passed**. Latest `30f4db2`: **213 focused
  tests and 4,279 full candidate tests passed**, zero skips/failures, full duration
  **4m11s**. Receipt: `connector-complex-read-full-30f4db2-20261009.trx`.
- Two cursor-test fixtures initially advanced to a second goal before supplying
  first-goal content. Corrected the fixtures, not the task-order guard. Original
  failing TRX is retained beside the corrected passing receipt.
- Candidate configuration pin: **228 focused checks passed**, zero failures or
  skips, including auth/configuration isolation and controller/overlay boundaries.
  Full matching-candidate regression: **4,279 passed**, zero skips/failures,
  **4m48s**, `connector-matched-candidate-full-20261009.trx`. Packaging and installed
  acceptance remain separate gates.
- Real `4dfe253` default-configuration product-path count independently verified
  **36**, complete one-page count, clear activity, zero sends/extra AI calls.
  No diagnostic backend override: the receipt records the actual compile-pinned
  held URL. Receipt `receipt-CountVerified-3c5fa4193ecc4bb790cb85a28c05a629.json`.
  This is an integration check, not a new latency benchmark while tests ran.

Real independent reads verified the latest Codex event update (10 October,
10:30–14:00 IST, ATLAS SkillTech University, Mumbai), a bounded latest Daisy
conversation with no pending action in those messages, and **36** Devpost winner
notifications matching the observed sender/subject pattern. The older 17:00 event
ending was superseded. These facts do not establish full historical thread
coverage or reliable combined-query acceptance. Private ticket links and message
bodies are not stored in this report or the sanitized receipts.

The authorized self-email already has independent delivery proof: **one copy**.
Its original operation journal remains truthfully `outcome_unknown`; delivery
verification did not rewrite that journal. **No new send or retry occurred.**

### Preserved natural-request failures

| Runtime | Observation | Assessment |
| --- | --- | --- |
| `4baf336` | Combined answer retrieved correct facts/count; one detailed answer overstated complete coverage. | Reject complete-coverage claim; caused `81d9882`. |
| `81d9882` detailed | Reads/count succeeded and partial scope was disclosed; Live ended with 1008 before a reliable final completion. | Not a complete natural-session pass; not retrospectively 1011. |
| `81d9882` natural | Bad inferred query prevented a verified count; answer requested a retry. | Combined task failed despite working backend reads. |
| `a5407ae` natural | Model repeated an unstructured query, reused a mismatched search cursor and claimed no matching mail without a count. | Failed; strict evidence remained enforced. Cursor defect caused `30f4db2`. |
| `30f4db2` natural | All three goals reached verified evidence; correct count 36 and partial thread disclosure. First transcript **127.745s**, total **157.724s**. | Qualified answer success, unacceptable latency; no invalid cursor appeared, so this run does not prove guard causality or consistent acceptance. |

Representative measurements: the `4baf336` natural run took **43.298s** total,
including generated audio/drain; first transcript **19.136s**. Resolution **1.391s**,
three authenticated executions **3.617 / 2.531 / 3.431s**, count **1.283s**.
Its detailed run took **60.241s**, but failed coverage truthfulness. The failed
`a5407ae` run took **55.213s**, seven HTTP requests, four capabilities and zero
extra AI/write calls. These are not a before/after speed claim. Backend routing,
provider work and verification are measured together; their separate timings
were not instrumented and are not invented.

The latest natural run had eight authenticated HTTP requests: resolution
**2.339s**, five executions **3.223 / 2.700 / 1.321 / 2.606 / 1.252s**, cleanup
**0.096 / 0.087s**; about **13.624s** total. UI included real Searching/Counting
Gmail states and cleared. Four capabilities, zero extra AI calls and zero sends.
Most elapsed time was outside those backend requests; a timeline probe is needed
to distinguish local publication from Live provider turn wait. No speculative
content reduction or audio change follows from this aggregate measurement.
Receipt: `receipt-NaturalComplex-8dd7437102d540388df4673d8f6080a1.json`, exact
clean source and assembly hash captured before dispatch.

A timing-only repeat of the same request on the same runtime retrieved all three
goals, count **36** and explicit partial disclosure: first transcript **23.825s**,
last **31.190s**, total **59.444s** including generated audio/drain. Seven HTTP
requests; resolution **2.742s**, executions **2.671 / 2.573 / 4.037 / 1.243s**,
cleanup **0.088 / 0.079s**. Tool publication took **0–10ms**; the 37,683-byte thread
receipt took **10ms**. Subsequent provider tool proposals arrived **0.730–0.875s**
after prior tool responses; final first transcript arrived **1.178s** after the
count response. No sustained local publication stall appeared in this run.
This does not retrospectively prove the source of the previous uninstrumented
104-second difference. It does rule out claiming that response serialization
necessarily takes tens of seconds or that evidence must blindly be discarded.
Receipt: `receipt-NaturalComplex-485f0fe52cd94bf5a2385fe87d7ce015.json`; source dirty
only for documentation, runtime assembly unchanged. The diagnostic timeline
stores timestamps and payload lengths, not mail bodies, queries or credentials.

The detailed natural request then returned correct event date/time/location and
registration evidence, a bounded latest-pending summary, **36** notifications and
two observed subject/date examples, with explicit partial disclosure. First
transcript **18.191s**, last **31.874s**, total **75.298s** including generated
audio/drain. Resolution **1.112s**, executions **2.999 / 3.134 / 2.231 / 1.192s**;
four capabilities, zero extra model calls/writes. Two resolve proposals reused
one existing task (one HTTP resolve); no duplicated state owner. Tool publication
**0–9ms**, provider next-tool gaps **0.741–1.492s**. Receipt:
`receipt-NaturalComplexDetails-b76cd4fcc7eb49c9a33b6fe2fad01690.json`.

Real current-source negative checks also passed: invalid changed-query search
cursor rejected before HTTP, valid continuation verified (**4.446s**); in-flight
explicit cancellation verified server `cancelled`, clear UI, zero late completion
or writes (**3.315s**). Latest non-connector classifier: **30,000 checks in
2.5241ms**, zero connector HTTP/capabilities/model calls. All diagnostic owners
disposed. These checks do not certify production, microphone/playback or every
OAuth lifecycle/provider.

The non-connector classifier measured **30,000 checks in 2.6533ms**, with zero
connector HTTP, discovery, capabilities or extra model calls. This is classifier
overhead only, not end-to-end spoken latency. Native activity has local provider
icons, blue connector orb and motion-aware shine; source/render checks and real
state cleanup are distinct from physical popup/audio acceptance.

### Diagnostic authentication correction

Two apparent task-control 409s in the `81d9882` detailed trial were independently
identified in GET-only Cloud Run request logs as `/v1/sessions/refresh` conflicts,
not connector endpoint failures. The diagnostic had reused a refresh credential
owned by the running installed desktop. It now reloads the protected current
access token, pins the same session and fails closed if freshness/account changes
require a refresh. It does not persist/rotate credentials or change production
authentication. Subsequent cleanup requests succeeded. The original receipts
remain preserved; this harness correction is not a new installed auth claim.

## Remaining gates, in order

1. Installed validation of combined natural requests, exact category counts and
   truthful partial answers through Jarvis. Three consecutive source trials now
   have correct count/partial evidence, but one was excessively slow; this is not
   a guarantee of stable all-query/provider behavior.
2. Exact-source bounded regression, matching-backend candidate packaging and
   installed Settings/task/activity verification without changing the public app.
3. Real multi-account, OAuth disconnect/reconnect/revocation, account switching,
   restart/cross-device and provider branding acceptance. Do not disconnect the
   useful account merely to manufacture a test without a viable consent return.
4. GitHub/other-provider consent and meaningful reads/writes; Slack/Discord staging
   custom configuration is still external. Exact fields are in
   `STAGING_AUTH_CONFIGURATION.md`; no duplicate auth configs are created.
5. Broader stable release acceptance before production promotion. Personal Live
   1011 / occasional speech cutoff remains independent and open; 3.8 superiority
   is unproven. No speculative audio rewrite or model switch.

No owner input is required for the current combined-read engineering step.
Original/sanitized artifacts are under ignored `.artifacts/connectors/diagnostics`
and `.artifacts/connectors/tests/gmail-real-read`. Never resend the self-email.

Final read-only Settings check at this entry: HTTP 200, `nativeVoiceBusy=false`.
Installed/public EXE hashes still match the protected starting versions. No
diagnostic Live socket or microphone session is intentionally left running.

## Private package ready; installation confirmation pending

Historical packaging entry. Owner subsequently confirmed installation; see
[installed acceptance and discovered non-Gmail handoff defect](INSTALLED_CONNECTOR_ACCEPTANCE_2026-10-09.md).
Do not treat the pending status below as the current installed state.

Clean packaging source: `b83bc6be27e730d4eec29f09eebc21ee6153f01f`, differing from
tested runtime `4dfe253` only in checkpoint documentation. Protected tag:
`checkpoint/connectors-private-b83bc6b-20261009`.

- Archive: `dist/connector-candidate-archives/b83bc6b-90ecd703/`.
- Installer SHA256: `90ECD703F3E55C5068AE82BD2DDC5A0A1C379A31D6827640B2AD3990A461FD9C`.
- EXE SHA256: `ACD4F2A22A8BD364FE413ED317DCB0F6B5EBBE45A99EC378C29E2D5A06968D6D`.
- All 24 archive files match publish. Compared with accepted `3f02a9e`, only
  the EXE and icon-attribution `NOTICES.md` changed; other 22 files, including all
  bundled Helper 2.1.28/native audio/voice-config assets, are byte-identical.
- Published win-x64 configuration independently confirms the held staging URI,
  production=false and preserved registrations. All 12 embedded native provider
  icons match reviewed source bytes. No new Node/Python prerequisite was added.
- Stopped candidate profile backed up under ignored
  `.artifacts/connectors/pc-installation/b83bc6b-reviewed/profile-before`.
  Package-only receipt is there; it contains no credentials or mail bodies.

Owner confirmed apps stopped/Helper disabled; process check agreed. Installer
path supplied. **Not yet confirmed installed, not a public release, not all-provider
acceptance.** After confirmation: compare installed files/profile/public hashes,
verify native Settings, reconnect the same Store Helper and perform bounded task
acceptance. Do not promote production or resend the self-email.
