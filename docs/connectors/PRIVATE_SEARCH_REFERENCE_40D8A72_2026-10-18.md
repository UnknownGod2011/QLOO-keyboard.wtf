# Private search/reference candidate — 18 October 2026

Installed and running; **rendered-browser/manual verdict remains pending**.
This report separates source, native provider, Live, package and visible-page
evidence. Earlier success flags do not supersede the failures retained below.

## Frozen identity

- Pre-fix repository HEAD: `64cb18f75cba1650c8edbfa985589e6a86f2469a` (documentation
  over installed application `fb0a06728b7f67096c4b02d81b65b42d94574d99`).
- Application source: `40d8a722e77dc59bc4f7b7ac3692145b67c43b97`.
- Application tree: `630037dd54025eb6b9ab1f4c3bb4b5e10aef6860`.
- Checkpoint: `checkpoint/connectors-search-reference-final-20261018`.
- Rollback: `fb0a06728b7f67096c4b02d81b65b42d94574d99`, archived manifest
  `461590FE6A6010ABBD90D10CC8F405211E73D8BC71A371858763C46EB3EB56E2`.
  Earlier d245ac4/96eff50 archives remain preserved.
- New archived manifest: `D150CA256ECB150A75C5624E704BED8A984150D60175D2AD50FE7C1AA524D155`.
- Installer: `8FE2FD2D82A5CF09C1509FBE85C1C067C293E0FF1B1BCBAD5B293E7032B26BE8`.
- Installed EXE: `0D847FEEED162618ECDB65986E0077DFCB1FD551A56E67EBBBC1AAA1DCBDD990`.
- Published/installed extracted DLL:
  `DA33618B690F2AD45FE72888605C3F66FAFC41694744FFDA7C662EE6D8B32D97`.
- Source test/Live DLL:
  `0D66DCEBB290EB1AA4E174EF736DF5AF45C3247C16CBCCC88454BF838EE64398`.
  The packaged DLL is separately extracted, matched to publish and exercised.
- Public EXE remains:
  `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.

Only one private build and one installation ran. All 24 installed payload files
match the archive. No public release or backend deployment occurred.

## Verified causes and corrections

1. Physical history showed Gmail search URLs and invalid `#thread/<id>` passing
   through generic URL dispatch. Native account-qualified `#all/<API thread>`
   construction was unchanged across the reviewed candidates. Qualified/named
   opening language had bypassed the retained-reference owner. The connector
   parser/resolver now handles those selectors using verified retained metadata,
   preserves presentation modifiers and rejects unmatched/ambiguous objects.
   Sender selection checks actual From metadata; a To-only participant is not
   silently treated as the sender. No provider ID or model URL is guessed.
2. Courteous and “by <person>” discovery missed native person preparation.
   Generic normalization now enters the existing bounded person lookup without
   requiring optional structured reasoning or inventing an address.
3. A complete empty simple inferred topic could lose refinement to Personal429.
   One native alternative using the human's words precedes optional synthesis.
   Explicit filters, sender/date/account/literal scopes keep the existing planner;
   candidate retrieval is not proof of semantic match.
4. Actual Live Annika→reported-topic requests exposed a fresh-read classifier
   bypass, then an incomplete recovery handoff. Resolving an empty ready task
   allowed Live to abandon the operation and deny access. Simple reported sender/
   topic recovery now executes one human-derived native search using the existing
   validator/executor, account/session/identity/permission fences and pending-reply
   behavior. The stale proposed ID/query/cursor is discarded. Complex scopes keep
   the planner. Execution authority is not revived.

All six changed runtime files are inside `Services/Connectors`. All 196 tracked
application files outside that directory are unchanged from the fb0a067 baseline.
There are no changes to ordinary routing, Live/audio, BA/tab execution, Smart
Writing, translation/UI, memory, selected text, screen, transports or billing.

## Execution evidence

Ignored evidence root: `.artifacts/connectors/search-exact-recurrence-20261018/`.

- Native Gmail found Lea, Riccardo, Annika, the described humanoid internship,
  Daisy and Nanda. Lea/Riccardo each needed one native query and zero structured
  reasoning. Annika has two distinct internship conversations; a subject-specific
  selector selects the humanoid conversation while a bare ambiguous sender stays
  ambiguous. Current Daisy results were multiple messages in one retained thread;
  this is not new real-provider proof of two distinct Daisy tabs.
- Actual Personal Live Lea→qualified exact-open→third history-details worked.
  Exact opening required zero additional backend calls. The third read retrieved
  actual bodies under fresh authority without another search, surviving actual429.
  Tool work was about1.87s; full generated spoken turn46.87s. These are distinct
  measurements, not an assertion that all response latency has one cause.
- Actual final-source Annika→reported humanoid-topic v4 passed with one answer per
  turn. The new human-derived search ran, unrelated Robotics Manipulation headers
  were not reused as topic results, and actual Personal429 retained native results.
  Full generated turns19.19s/22.69s; zero Managed credits and no microphone.
- Installed extracted-DLL sanity: real Daisy search, fresh retained thread read,
  seven actual body excerpts with coverage limits, combined details/open receipt,
  one exact account/thread URL dispatch, no follow-up search, three repeated model
  proposals sharing the receipt with zero extra requests/openings. One injected
  reasoning503 preserved native content. Activity cleared. Browser was inert.
- Packaged ordinary Gmail/Instagram/Discord/multi-site/arithmetic cases: zero
  connector backend/account/provider/reasoning calls. Gmail local dispatch median
  9.8663ms, observation+dispatch19.349ms. Saved Discord workflow remains its
  existing roughly195ms dispatch path. No normal-path optimization was introduced.
- Permanent payload unchanged: 19,509 prompt characters,62 tools/30,887 JSON chars.
  Prompt hash `D094925C7CAE6336CE013FF4515C832279A36095D83D4314533E196C4B3D5E95`;
  tools hash `F1D89E81F3201FADC6EEED0CD2E40898C3A2D15B40DA019B21EBE3E4BB1F35FF`.

## Regression gate and retained failures

One final full desktop run: **6,161 passed / 3 failed / 0 skipped / 6,164 total**.
All failures were short RegexMatchTimeoutException in unchanged helpers.
One unchanged-binary control covered their three methods: **9/9 passed**.
Original TRX and control are retained; this is not an all-pass original-run claim.
Held backend tree `8663bfda66ed96d9eded3540dd475bd41cf61aff` /00033-tip is unchanged;
its existing452/452 gate was retained, with no unnecessary backend rerun/deploy.

Focused .NET8 validation and actual Live failures are documented in
`SEARCH_REFERENCE_REPAIR_2026-10-18.md`. In particular, v1's optimistic harness flag
was rejected by semantic review, and v3's ready-task handoff denial was corrected
before v4 and packaging. A large regex automaton was corrected before acceptance.
Eight new fixtures initially omitted a capability present in real Gmail; the
corrected fixture/recovery subset passed31/31. The final broad includes all cases.

## Installed runtime and preservation

Final private GUI12964, Settings8090, PersonalOnly/revision14, all five saved
hotkeys registered; Helper connected and current-tab automation available.
The public native-host process can remain visible; it is not a second GUI.

The strict install comparison flagged an automatic public session-token refresh
when Chrome started the public GUI during the install gap. That original failure
was retained and reviewed against its successful refresh log/token write time
(2.4ms correlation). Another automatic GUI start won the hotkeys during handoff;
the runtime check failed truthfully. Normal public exit plus the app's existing
private restart API restored one private GUI, five shortcuts and Helper control.
No force kill, reinstall, hotkey reset, startup/registration edit or native-host
termination was used.

Final active metadata comparison covers29 profile files. Two normal session-token
refreshes were separately correlated to successful logs (public and private);
credentials were not opened/restored/replaced by the review. All other hashes,
settings, mappings, Personal preferences, public/other executables, startup and
Helper remain unchanged. The failed first timestamp-review script is retained;
its culture-sensitive metadata parsing was corrected in a new review receipt.

## Deliberate limits and final manual verdict

- Chrome tool navigation still returns “Local browser use is disabled in Work
  settings.” Native URL dispatch is not rendered-page proof. A pending human
  question asks for the new candidate's Daisy exact-tab plus details outcome.
  **Final rendered-thread/whole-workflow acceptance remains unverified.**
- Reopen-three currently verifies shortcut dispatch, not three observed restored
  tabs. The human's one-tab outcome remains unresolved and low priority. No tab
  execution/BA code was changed in this pass. Stable-ID closing and bounded
  uncertainty handling are preserved; no blind retry closes a different tab.
- The05:47 connector_resolve HttpRequestException had no retained status or inner
  cause. It cannot be attributed to Composio, DNS, OAuth, quota or a code regression.
  Existing native initial-GET retries and reasoning fallback are bounded; generic
  transport failures are not blindly replayed and recovery is not called perfect.
- Translation source/backend/UI match the reviewed public/working versions. Input
  cap remains50,000 chars; the physical32,740-char translation/copy succeeded.
  No translation change was made. Honoring an explicit full translation without
  asking full-versus-summary is a possible separate improvement, not implemented.
- No new physical-microphone test was performed in this pass. Existing recorded
  audio/normal lifecycle evidence is retained; the protected audio/Live files are
  unchanged. Actual new Live checks used text through the product dispatcher.

No further code, broad rerun or packaging is justified while awaiting the visible
manual verdict. Readiness beyond the pending rendered-browser proof is not claimed.
