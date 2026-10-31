# Private context-recovery delivery — 17 October 2026

The corrected **Connectors Staging Candidate is installed and running** for
manual testing. This delivery supersedes the earlier disk-blocked status.
No public release or backend deployment was made.

## Exact identities

- Packaged source: `96eff5033ff93152d8169e8e45d5f8b50bcb9fa2`.
- Application correction: `08d6674fdf593aec4f4a1884df0525c62e208e96`;
  packaged source adds documentation only, with identical source/test trees.
- Pre-fix source: `ba9e793e49134a8096d71f0e581cf8e7ba523bae`;
  tag `checkpoint/connectors-before-context-audit-20261016`.
- Installed rollback: `2f5ea15b3b2279738538d9b289759f41fc17dd4f`;
  protected `dist/connector-candidate-archives/2f5ea15-reviewed/` unchanged.
- Public1.4.21 source: `07a57667055a1c47c7a1990ec0d45d7f20a0cd48`.
- Held backend: `gs-q261013 / keyboard-wtf-connectors-staging-00033-tip`,
  source `00901f5b51619466c068295c986da624dea59109`, tree
  `8663bfda66ed96d9eded3540dd475bd41cf61aff`. No traffic/config changes.
- Installer SHA256:
  `51268648AE83DC099B1CBE6B593DD9171BE73D737056EC8AA4EE9FA0571DCBB2`.
- Installed EXE SHA256:
  `79D65F1DDBAB494AE83C43470D6B0E5F863E7A7F9CCE10A3DA024E0853D2B941`.
- Embedded/publish managed assembly SHA256:
  `5190DD8F6E9FFAEF18F43AB9A568CED8768C737C24C2FE3708B8B86294FEF9A6`.
- Source-gate managed assembly SHA256:
  `08B1893A9CF36AB7EFDFF44A96EE2A6260339801BCA185B7304E86CD94193153`.
  RID publish/informational identity differs; installed-byte tests below were
  run separately, not inferred from matching source alone.
- Private GUI27232, localhost8090, PersonalOnly/Gemini, all five hotkeys.
  No microphone or Jarvis session was started for the final runtime readback.

The package manifest is
`dist/connector-candidate-archives/96eff50-reviewed/package-proof.json`.
Delivery receipts are under ignored
`.artifacts/connectors/repeated-followup-audit-20261016/`.
This document's commit is a documentation-only successor, not a rebuilt app.

## Root causes and targeted changes

Read-only comparison preceded edits; see
[the forensic audit](REPEATED_FOLLOWUP_AUDIT_2026-10-16.md).
Physical logs disproved the assumption that every inactive follow-up came from
503: native search succeeded, then local terminal-task checks rejected later
reads within about10ms. Recovery was tied to selected Gmail phrases/task
authority even though retained results survived independently.

The shared retained-read boundary now selects verified session/account objects
independently of terminal execution. Facts reuse existing evidence; new reads
obtain fresh authority and pin observed object IDs. Historical capabilities are
intersected with current normalized read capabilities. Active, cancelled,
foreign-account, stale-scope and write authority keep their existing fences.
Ambiguous objects clarify; unpinnable read capabilities get fresh account-ready
authority without replaying stale queries or cursors. Metadata-only projection
continues to hide bodies, including in clarification receipts; explicit body
regrant can reuse already verified same-scope content. Idle/active impossible
recovery paths return before transcript/regex work.

A substantive question about one retained email conversation can correct a
stale model search proposal into a fresh exact thread read; explicit new search,
date, participant and body restrictions are preserved. Repeated proposals share
one owned receipt. Existing useful retained-body evidence skips the extra
reasoning retry; default new-search retry policy was not globally changed.

Natural reported-mail wording such as “I just received an email from Lea. Could
you look into it and give me the details?” now feeds the existing bounded person
search preparation. No person-specific aliases or subject guesses were added.

The public/pre-tab/previous ending guard all rejected the standalone phrase
“End the conversation now.” This was a pre-existing phrase gap, not a changed
audio parameter. Eight anchored whitelist lines recognize that explicit intent
and keep negated/quoted/future/compound instructions excluded.

The restored tab parser supported last-N and single positions but lacked a
first-N set. The narrow correction resolves explicit first/last-N into stable
Helper IDs before closing and overrides conflicting model target proposals.
The Helper, browser engine, foreground/key fallback and ordinary tab paths were
not rewritten. No personal tabs were closed during automated checks.

## Evidence and limits

- Final focused correction112/112; earlier focused779/779 and102/102 retained.
- Final broad desktop **5971 passed /5974 total,3 failed,0 skipped**.
  One unchanged-binary controlled gate passed13/13, including all three exact
  failures and adjacent status/ownership cases. Failures were a3s notice wait,
  an existing50ms regex timeout and a queued-read generic failure receipt whose
  underlying exception was not captured. Preserve the failed broad receipt;
  do not describe it as a single green5974/5974 run.
- Backend tree unchanged; selected452/452 reused. Firestore emulator cases
  remain separately accounted for, outside that selected gate.
- Actual Personal Live selected the tools for natural Daisy follow-up, spoken
  details and exact-open stages. A combined read/open interrupted by “What's
  taking so long?” passed on final source-gate bytes: one progress answer while
  UI stayed owned, then one useful answer, no contradictions, same account/thread,
  no repeated search, one exact inert dispatch, clean activity. One injected
  structured503; no actual Managed reasoning usage or microphone.
- Exact installed managed bytes: `installed-natural-followup-final.json`
  passed a completed-task natural question with one fresh thread read, seven
  actual body excerpts, no reused dead task, no repeated search, one simulated
  reasoning503 and clean UI;19.68s including initial lookup/native retrieval.
- Exact installed bytes: `packaged-combined-native-sanity-final.json` passed
  read+open, same account/thread, seven body excerpts, one account-qualified
  URL dispatch, three repeated proposals sharing the receipt, zero repeated
  backend calls/search/openings, one simulated503, clean UI;17.43s total.
  These are real native reads with inert browser callbacks, not actual Live
  speech or rendered-browser proof on the installed GUI.
- Separately, fresh Chrome loaded the native-ID/account-qualified Daisy URL
  and canonicalized it to the actual named conversation, not Inbox/search/blank.
  `daisy-browser-render-v1.json` records that source-neutral URL proof.
  Multiple-object opening and opaque-ID rejection are covered by source tests;
  this receipt is not fresh two-Daisy-thread browser proof.
- Native Lea request passed: observed named sender/subject and two body excerpts.
  Existing Gao/Winter, explicit cancellation, headers-only and generic provider
  fixtures remain green; older real Personal Live receipts are retained rather
  than relabeled as new installed runs. Unconfigured providers were not made
  authenticated by fixtures. Details explicitly disclose missing/clipped coverage;
  seven excerpts are not claimed to be the complete unbounded conversation.
- Actual generated spoken “End the conversation now” closed the session/socket
  once, with zero connector calls. Same ending method unchanged in final source.
- Tab/shortcut classes364/364, including first-two exact-ID selection and
  conflicting last-count/from-end/target proposals. Inert fixtures, not physical
  closing of a user's browser tabs.

## Protected behavior and performance

Exact installed-byte local measurements: Gmail9.56ms median, Instagram16.50ms,
Discord saved workflow191.23ms, multi-site190.28ms, arithmetic admission0.014ms,
12 repetitions each. Inert action dispatch; not speech-to-action/network timings.
Every ordinary case made ZERO connector backend, account, provider and extra
model calls and acquired no connector activity.

Permanent payload unchanged:19509 base prompt characters,62 tools,30887 tool
JSON characters; prompt SHA D094925C...D5E95 and tools SHA F1D89E81...1F35FF.
No audio format/VAD/STT/playback/model/transport, Smart Writing, BA engine,
memory, selected-text/screen, billing, popup dimensions or Helper changes in
this correction. Shared routing changes are only the explicitly requested
positional-tab authority fix; the eight ending lines are command classification.

The new uninstalled recovery hook initially had avoidable idle work; final guard
tests measured88.10ms→0.0163ms locally with zero snapshots/network/model calls.
Do not attribute all prior user latency to this source-only intermediate code.
Native multi-message retrieval still takes seconds, and Live audio completion
adds provider variability. No speculative audio/performance refactor was made.

## Installation preservation

One private build/archive/install, installer exit0,24 files matched, offline
embedded assembly matched publish bytes. Stopped before/after snapshots matched
all29 protected profile files exactly, plus public/other executable bytes,
startup, native-host registration and packaged/Store Helper2.1.28 manifests.
No settings/profile copying or hotkey reset. Public EXE SHA remains
`2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.

The later stable running readback still matches settings, mappings, Personal
configuration, device files, public binaries/startup/Helper. Two encrypted
session-token files renewed during normal authenticated execution. Public write
matches its successful refresh log; private write occurred during packaged
Auth refresh, whose frozen session is checked before every connector request.
The older combined harness's rejected AppLog filename limits direct private
refresh logging; this exception was reviewed, not silently called byte-identical.
No token was restored, copied or exposed to conceal renewal.

The public native host briefly relaunched its old GUI, capturing8090/hotkeys.
Only the hash-verified old GUI was closed gracefully; the private normal restart
then acquired8090 and all five shortcuts. Public native host/Helper remain alive
and unchanged. Runtime mode is PersonalOnly/Gemini; no policy change.

The disk blocker was resolved with measured phased packaging and only verified
generated EXE/installer duplicate cleanup after archiving. No rollback/archive,
installed/public/profile files or pagefile settings were deleted/changed.

## Manual handoff and stop

**READY FOR FINAL MANUAL CONNECTOR TEST.** No more source polishing, new providers,
repeated broad gates or public release work is authorized by this closure.
Useful physical checks: Daisy natural/combined follow-up with status interruption
and exact tab; Lea/Gao details; explicit cancel; end-now; and first-two closing
in a dedicated window of disposable tabs. These exercise microphone/UI/real
foreground integration beyond the automation proof above.
