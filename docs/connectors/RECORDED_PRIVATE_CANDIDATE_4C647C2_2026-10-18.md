# Private recorded-regression delivery — 18 October 2026

The corrected **Connectors Staging Candidate is installed and running**, PID22564,
Settings port8090, PersonalOnly, all five saved hotkeys registered. Public1.4.21
remains installed/running and was not replaced. **Overall readiness remains NO**:
browser tooling still rejects local browser use, so actual disposable first/last
tab outcomes and fresh rendered Gmail navigation are unverified. Long spoken
completion time is reported below, not described as fast.

## Immutable identities

| Item | Verified identity |
| --- | --- |
| Installed/frozen source | `4c647c250660921263887670928608d600710a8a` |
| Application correction | `abee9f07c8dd9100561426119ce138e14934f895` |
| Source checkpoint tag | `checkpoint/connectors-cooldown-recorded-source-20261017` |
| Earlier installed candidate, archived | `5578f1eb4b486e6a7650bb185a3020cc61892253` |
| Working rollback, archived | `96eff5033ff93152d8169e8e45d5f8b50bcb9fa2` |
| Source tested DLL | `E2496B2152D997D58D09794003634DAD86D69A42F861E11AD9ACF306F1D68502` |
| Packaged/installed extracted DLL | `0D87AF324E3CE88EA02CE693811673B2C1A5CB66E5554E00B2FD4C4936DF9917` |
| Private installer SHA256 | `A616D8243EF4A455D51C57FD624DA5E1F64852AC37148FC4362C6FCD3C86C310` |
| Installed EXE SHA256 | `D9BCBEA363F357D5FB285D3B1E811D56D4D97E39E555AAA01160240BC545398F` |
| Public EXE SHA256, unchanged | `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5` |
| Accepted public desktop baseline | `1.4.21 / 07a57667055a1c47c7a1990ec0d45d7f20a0cd48` |

Archive manifest: `dist/connector-candidate-archives/4c647c2-reviewed/package-proof.json`,
SHA256 `DCB33132567C3B5655BDD6A1EA9615A500640DF1B82EBAA69EEE6DBAA90640BC`.
Embedded DLL was extracted offline from the installed single-file EXE and matched
the RID-specific published DLL. Non-RID source and packaged hashes differ; both
were tested separately. Later delivery documentation does not change these bytes.

## Verified cause and minimal correction

Actual packaged5578 Daisy acceptance found a distinct recovery bypass: initial
Personal reasoning429 succeeded through native fallback, then a fresh retained
thread read obtained five native results. Its synthesis hit the local vault's
generic `InvalidOperationException` cooldown rejection. That exception was not
the typed provider quota error handled by the reader, so it discarded the useful
result and prevented the combined open/details operation.

Only two production files changed since5578. `PersonalApiVault` now gives the
same two existing cooldown rejections an internal `InvalidOperationException`
subtype. `ConnectorReadReasoner` handles only that subtype, only when verified
native evidence exists and the operation is not cancelled. It uses existing
native recovery and content-scope filters. No new retry, key rotation, Managed
fallback, cooldown duration/storage, revocation rule or execution authority was
introduced. Revocation/corrupt-configuration controls still fail closed.

Earlier causes and corrections remain distinct: retained follow-ups must use a
new human-turn identity rather than text equality; retained result context is
independent of terminal execution authority; an initial partial result's outer
headers-only answer instruction contradicted its verified body-bearing content.
The previous controller correction aligns those scopes. These are demonstrated
defects, not proof that they explain every historic remote stall or denial.

The requested extra retained-content503 retry remains disabled only when native
body evidence is verified for the same thread/revision/scope. Current focused
gate includes13 retained-retry cases. Fresh searches and invalid/missing body
evidence retain their existing bounded policy. No broad exception swallowing.

## Actual saved-recording acceptance

Recordings were converted to PCM16k mono and sent through the existing Personal
Live setup/receive/tool-dispatch/audio-frame path. No microphone or room audio
was captured. Browser launch was deliberately inert; reported dispatch success
does not establish that a real browser rendered the conversation.

Sourcev11 used normal Personal reasoning, no injected failure or receipt override.
All three turns passed: initial47.86s, combined38.40s, repeated exact-open26.21s.
Actual429 then typed local cooldown were captured. Same native thread/account,
one completed answer per input, no inactive-task/competing denial, clear activity,
no follow-up mailbox search and zero later-open backend/reasoning calls.

Installed-byte Daisyv2 completed these business assertions and actual answers:

| Input | Observable recorded result | Whole turn |
| --- | --- | --- |
| Find Daisy and give details | One useful answer from verified native content after real503 then429 | 52.56s |
| Open in new tab and give entire-conversation details | Fresh task; five native reads; no mailbox search; exact retained dispatch; one chronology/asks/commitments/status answer, explicitly partial10-excerpt coverage | 85.31s |
| Open it in a new tab | One same-thread dispatch; zero backend, search or reasoning work; one answer | 21.45s |

The original installed harness receipt **FAILED** after those assertions: its
final classifier permitted only Quota/cooldown and excluded the preceding typed
Provider503. Preserve that failure; it is not a product failure or a fully green
harness run. Separate offline review checks the original hashes, three answers,
verified native thread/message IDs, fresh task ID, both actual resolved-object
dispatch receipts, zero redundant work and exact installed identity. It accepts
only captured503/429/typed-cooldown errors and rejects unrelated errors. No Daisy
provider rerun or product edit was made to turn this into a pass. Internal
resolved-account count was not serialized after the failed final guard and was
not independently recounted; same destination account is observed in both URLs.
Healthy Personal structured synthesis remains unproven because of actual503/429.
Managed charges in both current Daisy runs: zero.

Both exact dispatches use the same account-qualified Gmail native thread URL
previously confirmed by the human as the correct Daisy conversation:
`https://mail.google.com/mail/?authuser=tanushshah2006%40gmail.com#all/1a09467590afcb15`.
Never promote Live's spoken "opened" to rendered-navigation evidence. Opaque
provider/UI fragments are not interchangeable with native thread IDs.

Current installed-byte core acceptance also passed: recorded greeting produced
one ordinary reply and kept the session alive; discussion about when to end did
not end it; recorded explicit ending produced one end observer invocation,
cancelled the session and closed the socket. Zero connector/backend/reasoning
calls, no Managed usage, clear activity. Hardware listening/playback remains
unverified by this replay.

## Regression, performance and protected boundaries

- Current affected focused gate: **272/272**, zero failures/skips. TRX SHA256
  `FE7960B467F0EE25257CD9DF4DA251F2DA53E601D408D0E727B81498165BAAEC`.
- Broad desktop gate on earlier application3dabb0f: **5,992/5,995**, three
  failures, zero skips. One explicit50ms regex timeout and two masked local
  exceptions with inner causes unproven. Same-binary NoBuild control **16/16**
  included every failed family and the initial-scope tests. Neither receipt is
  relabeled a new, completely green broad run onabee9f0. No duplicate broad loop.
- Backend tree unchanged `8663bfda66ed96d9eded3540dd475bd41cf61aff`, held revision
  `keyboard-wtf-connectors-staging-00033-tip`; inherited selected452/452 gate,
  no backend code/deployment change.
- Current installed local dispatch: Gmail5.52ms median, Instagram5.17ms,
  Discord194.71ms, multisite188.58ms. Same-host earlier controls already showed
  the Discord cost. No new ordinary routing regression identified. All five
  phrases, including arithmetic, made **zero connector calls/account lookups/
  provider/reasoning calls**. This tests local routing, not a complete spoken
  arithmetic or browser-navigation interaction.
- Prompt/tools unchanged from the approved private connector baseline:
  19,509 characters;62tools;30,887 JSON characters. Prompt hash
  `D094925C7CAE6336CE013FF4515C832279A36095D83D4314533E196C4B3D5E95`,
  tools hash `F1D89E81F3201FADC6EEED0CD2E40898C3A2D15B40DA019B21EBE3E4BB1F35FF`.
  Existing public-to-private connector delta remains; no new permanent payload.
- Combined connector read/open in sourcev11 was7.50s; current installed follow-up
  native retrieval finished in about8s and provider output began about1s after
  the receipt. Full recorded turn85.31s included long streamed output. No claim
  that end-to-end speech is fast, or that every delay is locally caused.
- Prior recorded audit compares public07a5766, pre-tab4be3d21, rollback2f5ea15,
  corrected96eff50 and subsequent source. Protected29-source/Helper boundary
  checks are preserved. This latest correction changes no Live, audio, VAD,
  transport, ending, tab, BA, Smart Writing, memory, selected-text or screen code.
  Cumulative approved connector/mute integration exists in shared files; do not
  claim their complete implementation is identical to public.
- Positional tabs: earlier bounded polite-prefix parser correction remains;
  existing stable-ID selection/execution and last-tab mechanism were not rewritten.
  Recorded ASR/parser checks do not prove actual tab closures. First/last/plural
  real-browser acceptance is still blocked by tool permission.
- Install comparison passed24payload files and29profile files; settings/mappings/
  Personal configuration, public executables, startup and Helper unchanged.
  After tests, the comparator retained a failure for one public session-token
  refresh. Explicit review correlates its write with the untouched running
  public app's successful automatic refresh within1.0493ms; no token plaintext
  was read, restored or replaced. Original comparison and null initial operator
  review are retained. A PowerShell parsing error in that initial review let
  the authorized exact-hash private GUI launch before the review completed;
  separate guarded review then confirmed the evidence and process. No extra
  launch, force-stop, source change or comparator-policy relaxation occurred.
- Physical runtime: PID22564, private8090 banner, PersonalOnly, all five saved
  hotkeys registered (JarvisCtrl+Alt+J); no Jarvis/microphone start requested.

## Earlier supporting real checks: preserved, not rerun on current installed bytes

Gao/Winter native candidate/read/reference regression exists on installed7300c9b;
GitHub authenticated MX--Cursivis/release/assets/retained recall on an earlier
recorded DLL; cancellation and Hello interruption through earlier actual
Personal Live; headers-only omitted-revision update through earlier real Live;
status interruption through08d6674/freeze96eff50. Their source/fixture protections
remain, but they are not fresh4c647c2 rendered/spoken passes.

Lea actual Personal Live/native Gmail with the separately authorized one-run
Managed reasoning test passed search/details/retained factual follow-up. Total
known charge2.00credits (planning-only harness0.48 plus successful1.52). Saved
PersonalOnly never changed. Later actual Personal failure-path Lea also passed
initial/cached follow-up with injected503/429 and zero repeat calls. No further
Managed test was used in this closure.

## Receipts and remaining boundary

Ignored evidence root: `.artifacts/connectors/recorded-regression-20261017/`.
Current package receipts are under `package-cooldown-final/`:

- `installation-boundaries-physical.json`, `installed-final-4c647c2/extraction-proof.json`.
- `packaged-protected-boundary.json`, `packaged-combined-native-sanity-final.json`.
- Original `packaged-recorded-daisy-v2/acceptance.json`, SHA
  `0B11A5A020C063494495953C86C629AC6B706A30257C567D2FE2649E7AFF177D`.
- `packaged-recorded-daisy-v2-offline-review.json`, SHA
  `A42F830F2949CBED81126C7768B717BCB871402D4F28509EC49EFD59C5F43F07`.
- `packaged-recorded-core-v1/acceptance.json`, SHA
  `2E1452A4ABCA40652EC6190159AB7442F37DF217FAFF3AB3B35C919ED242B6A7`.
- `post-packaged-boundaries-physical.json`, `post-packaged-token-review-final.json`.
- `start-private-final.json`, `final-runtime-first.json`.

Browser inventory was retried and again returned **Local browser use is disabled
in Work settings**. No alternate automation bypass was attempted. Source, native,
recording, dispatch, installed and human proof remain separate. The correct
private build is available, but **READY FOR FINAL MANUAL CONNECTOR TEST: NO**
until the outstanding actual browser/tab outcomes are evaluated. No public release.
