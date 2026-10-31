# Scoped reliability corrections — plan before implementation

Baseline: installed ae2428d, EXE5286891CF9175D056B559719746CD711A01F33C68F22C60B22B5375461B77EC8,
source HEAD93a120a (documentation only after the frozen code), one private GUI6480.
Keep ae2428d/f25011b and older archives immutable. No installer until focused
checks and any necessary native acceptance justify one frozen candidate.

## Independent changes and justification

1. **Popup lifetime:** camera success publishes globalDone; the existing view
   dismisses it after3.2s even with a continuing Live owner. Change only the
   view's dismissal predicate to honor its existing session-owned Mute/Unmute
   identity. Ending the session removes that identity and resumes normal hide.
   Do not change camera, voice phases, microphones, speech, goodbye, translations
   or long-task ownership. Existing tests asserting live-owner dismissal must
   be intentionally updated; ended/no-owner dismissal and task cleanup remain
   asserted. Reproduce the old behavior on ae2428d before claiming correction.
2. **Virtual desktops:** investigate the supported IVirtualDesktopManager route
   for Jarvis's own popup only. No undocumented pinning interfaces, registry
   writes, desktop creation/removal, foreign-window movement, focus activation
   or topmost polling. If supported own-window movement can be safely verified,
   use the existing UI timer with bounded/throttled observations only while
   work is visible, validating the foreground desktop and original target
   identity. Missing/denied/ambiguous identity must leave windows untouched.
   Do not implement an unverified desktop hack merely to mark this complete.
3. **Temporary retention:** reuse normal cleanup and exact ownership boundaries.
   Legacy ICS has an external asynchronous consumer, so never delete immediately.
   Retain current/recovery updater packages and active files. Any cleanup must
   stay within dedicated owned paths, exclude links/user outputs/rollback bytes,
   and be verified using disposable fixtures before touching a normal profile.
   First decide whether old unmarked crash leftovers can be recognized safely;
   uncertainty means preserve them. Fixed-name atomic-write failure cleanup is
   independent of broad orphan discovery. No general disk sweeper or service.
4. **Cold discovery latency:** preserve the proven native process/title path,
   exact app-ID/image binding and ambiguity guards. Reuse the existing trusted
   catalog mechanism, avoid adding a second competing catalog, extra AI calls
   or background manager. A change needs a measured cold-path benefit and
   equivalent destructive-target selection; otherwise report the limitation.
5. **Diagnostic test flavor:** distinguish canonical public production from
   intentionally private staging types. Preserve public absence assertions and
   positive private expectations per actual preprocessor conditions. Keep tests.
6. **Crash:** the old event identifies an unhandled termination, not its module
   or installed build. Read existing WER/event/log evidence for attribution;
   do not change audio/Whisper/native libraries, force a reproduction, enable
   global crash dumping or hide exceptions speculatively. If attribution remains
   unavailable, record the exact missing evidence instead of inventing a fix.

## Verification and promotion boundary

- Separate reversible commits and focused tests for each corrected behavior.
- Root coordinates shared build outputs; no parallel builds/test runners.
- Use existing quiet offscreen native fixtures for popup visibility, no focus
  stealing; keep mutable static UI tests serialized.
- Keep original failing reproductions, error receipts and any external failures.
- Verify owner/task/cancel/end invariants and unaffected exact native closes.
- Reuse accepted Gmail/tab/translation/speech evidence if code paths unchanged;
  run only the necessary directly affected regression boundary.
- One frozen package gate and protected profile/hash comparison if installation
  is justified. A passing fixture is not microphone or cross-desktop proof.
- No public/backend/website deployment, new connector capability or scope change.

## Implementation decisions and evidence (27 October)

The installed baseline remains ae2428d. Independent reversible corrections:

- b06bf01: test-only flavor expectations; connector staging intentionally contains
  the recording adapter, canonical public production must not contain it.
- d08a8bc: view-only persistence while the existing valid Live owner remains.
  Camera, voice phases, quick-action visibility preference and session termination
  are unchanged. Four new photo-Done cases failed against the exact installed
  ae2428d DLL, then passed against corrected source. Ending ownership still hides.
- d507f4b: a separate demonstrated Whisper context lifetime defect. Factory
  disposal previously could free an active processor's native context. The
  pinned Whisper.net 1.9.1 implementation confirms this borrowed-pointer contract
  and requires asynchronous processor disposal after cancellation. Ref-counted
  generation leases retire contexts after processors finish without blocking UI
  shutdown. Model parameters, library version, VAD and Live audio are unchanged.
  This is NOT attribution or proof of repair for the historical 0xc0000005 event.
- 61d63c8, 76fb1e0, a56b1e9, dfe8332: exact failed save staging, newly marked
  Calendar exports and future verified updater generations receive bounded owned
  cleanup. Exclusive verified leaf handles exclude links, busy/changed/unknown
  files. No recursive deletion, legacy adoption, background service or rollback
  archive deletion. Calendar shell consumers get seven days; update cache gets
  thirty days with requested/running and recent versions protected and at most
  two package hash attempts per invocation. Concurrent first exports are retained.
- 81d0d4d: two new filesystem fixtures now allow the actual Windows overwrite
  rejection categories IOException or UnauthorizedAccessException. Original saved
  bytes and staging removal assertions are unchanged. Original failed TRX stays.

Focused corrected-source run: **245 passed, 0 failed, 0 skipped**. Initial run
had 243 passes and two fixture exception-category failures; preserved separately.
The first old-DLL popup attempt aborted because the copied harness lacked the
Windows runtime folder; after restoring that same runtime dependency, the four
expected old-behavior failures reproduced. No assertion or timeout was weakened.
Production tree under test: e6cdb0ab86bca01c532feba165cef6f9092c3b0d.
Evidence: .artifacts/connectors/stabilization-plan-20261027/scoped-fixes/.

### Deliberately unchanged and remaining limitations

- No virtual-desktop production change. A disposable own nonactivating tool
  window reports Guid.Empty desktop association. Supported own-window movement
  to the already-current desktop returns S_OK but does not prove following a
  second desktop. Task View showed only Desktop 1. No desktop was created,
  deleted or moved, and no unsupported shell-pinning interface was used. The
  earlier inference that lack of explicit pinning proves the incident was a
  desktop gap is too strong; lifecycle hiding is confirmed, cross-desktop outcome
  still needs a real second-desktop observation.
- Cold discovery replacement rejected: identical 209 app IDs but native COM took
  1697 ms vs existing PowerShell 2269 ms, with label normalization differences and
  no equivalent six-second process timeout. A modest isolated saving does not
  justify a new blocking/destructive-resolution risk. Keep current cache and
  native fast path. Low memory can contribute but is not incident-time causation.
- Historical crash PID17024 has no matched module/path/WER dump. It remains
  unattributed. No playback, provider, Vosk, camera or permanent prompt change.
- No physical-profile leftover sweep. Legacy unmarked ICS, model recovery,
  TTS crash leftovers and developer/rollback artifacts remain protected.
- Existing saved hotkeys Q/X conflict on this PC; the running app currently uses
  J/Ctrl+Alt+Escape fallback. No unrelated owner was stopped or setting changed.
- Public readiness remains separate: OAuth review, signing, correct public
  artifact/inventory and clean install, privacy disclosures, production provider
  acceptance and release/support workflow. No public release is claimed.

### Next promotion boundary

Verify the small native recognition/cancel/dispose path in a standalone process,
then freeze and package once. Re-run this same directly affected focused filter
against the extracted packaged DLL and compare exact test names. Protect ae2428d
and all older archive hashes. Only then attempt the existing guarded private
handoff; a redirected physical-profile guard must stop rather than be bypassed.
Actual installed photo/Photos lifecycle and second-desktop visibility are separate
from quiet offscreen fixture proof and will be reported accordingly.
