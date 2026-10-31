# Reliability, popup, retention and public-beta audit — 27 October 2026

## Scope and current state

This was a read-only production-code review, not a new stabilization build.
No production/test/prompt/configuration edits, test-suite runs, builds, provider
model calls, cloud writes, publication or website updates were performed. The
operational exceptions explicitly requested by the user were a normal switch
back to the already-installed private app and removal of obsolete Desktop CMD
launchers after verified recoverable copies. No user outputs or caches deleted.

- Installed private source: `ae2428d77a7ec3a521e85a0a4ab63c6c9a4d481c`.
- EXE SHA256: `5286891CF9175D056B559719746CD711A01F33C68F22C60B22B5375461B77EC8`.
- Embedded DLL SHA256: `EFD2F3AB3B90A45A9846F2BB9E864BFFBBF8AC2C48046A9BB59CC73BA13255D0`.
- Reviewed source HEAD before documentation: `252d523`; production tree matches
  ae2428d. The existing uncommitted26October read-only report was preserved.
- Immediate rollback: f25011b; all older protected manifests/packages retained.
- Public readiness: **not yet established**. A tested private build is not the
  public release artifact or proof of every provider/physical outcome.

## Interrupted launch reconciled

Fresh inventory initially found public GUI15756 and its Chrome native host6072,
not the private GUI. CIM confirms the GUI's parent was that native host. The
interrupted26October attempt had briefly produced both GUIs, so it was not
reported as a successful private-only start.

The tool's private-profile file identity resolves into the Codex package's
LocalCache, while the public lock resolves to the actual normal profile. This
makes a tool-launched private-profile handoff unreliable. The earlier public
takeover remains failed runtime evidence; profile redirection alone does not
prove the exact reason that attempt's public lease was ineffective. No guard bypass, alternate-host workaround,
force stop, registration edit or installer was used to recover.

The user ran the reviewed normal-host recovery once. It validated installed
package bytes, physical lock ownership, the exact idle public PID/start time,
normal exit, a physical public lock lease, settled GUI inventory and private
profile ownership. It completed at **01:00:23IST,27October**, starting private
GUI6480. Fresh inventory confirms one private GUI and one public-path native
host10624, not two GUIs; Settings has the private banner and Helper is connected.

Saved hotkeys remain Q/X, but current Windows registrations use the existing
fallbacks **Jarvis Ctrl+Alt+J / Cancel Ctrl+Alt+Escape** because Q/X registration
was unavailable. The audit did not change these settings. Five registered
shortcuts must not be described as proof that all requested chords were obtained.
The conflicting owner has not been identified; no unrelated process was stopped.

## Popup: concrete lifecycle finding

The prior ended-session snapshot only established that a hidden popup still had
TopMost and was on the current desktop. It did not resolve the active-session
report. This review found the following actual code interaction:

1. `CommandRegistry.cs:1377` publishes Executing before the camera tool.
2. Successful capture publishes global Done at `WebcamCaptureService.cs:65`,
   including when invoked from an active Gemini Live conversation.
3. The tool-receipt observer records waiting but does not replace that phase.
4. Generic status publication at `GeminiLiveConversationService.cs:1425`
   requires Listening, so it does not replace the camera's Done phase.
5. `VoiceOverlayForm.cs:328–345` hides Idle/aged terminal phases without owned
   long-task/capture work. A Live Mute/Unmute action deliberately does not count
   as capture ownership. Terminal dismissal occurs after3.2seconds.
6. A later provider reply, subsequent tool or settled provider turn can restore
   Speaking/Executing/Listening and make the popup visible again.

**Confirmed source-reachable issue:** a post-photo reply/next-action delay longer
than3.2seconds can hide the popup while Live remains active. This does not require
camera failure, lost TopMost or a desktop switch. An existing test explicitly
codifies dismissal despite a continuing Live session:
`PopupQuickActionTests.Task_owner_keeps_popup_visible_then_clearing_it_dismisses_despite_live_action`.
It is a lifecycle-policy mismatch with the user's persistence expectation.

**Not proven:** that exact state transition caused the historical Photos-close
incident. No incident-time native visibility/cloaking/status trace is available.
Camera and overlay files are identical across fb0a067,d0290be and ae2428d, so this
is not established as a new camera-code regression introduced by app closing.

Other important distinctions:

- The native hidden owner is standard WinForms taskbar-owner behavior for
  ShowInTaskbar=false, not evidence Photos owns Jarvis. The popup is created at
  startup and reused. Its TopMost/NOACTIVATE invariants remain present.
- No virtual-desktop following/pinning is implemented. TopMost controls stacking;
  it does not promise the same HWND on every virtual desktop. Desktop-cloaked
  visibility is separate from the demonstrated terminal-state dismissal.
- Normal native app-close enumeration excludes owned/untitled popup windows;
  the new closer does not select or move this overlay.

**Smallest future correction to consider:** change only the view's auto-dismiss
decision to honor the already-existing active Live owner, preserving dismissal
after session end. Do not mutate voice phases, camera capture, focus, speech,
topmost polling or virtual desktops. This intentionally changes the old dismissal
expectation; it needs focused active/ended/muted/task/error lifecycle checks and
a bounded real photo -> Photos open/close observation before promotion. No such
change was applied in this audit.

Microsoft references checked:
[window visibility/ownership](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features),
[WinForms taskbar-owner implementation](https://raw.githubusercontent.com/dotnet/winforms/v8.0.21/src/System.Windows.Forms/src/System/Windows/Forms/Form.cs).

## Performance and crash findings

The existing installed-app catalog is cached for10minutes. A cold fallback can
be slow: historical isolated measurements recorded5.1seconds cold versus2.4ms
cached. Paint can need display-name resolution; Chrome/Notepad usually match
their processes directly. ae2428d adds no model call or discovery pass relative
to f25011b. Do not replace this with another installed-app catalog architecture.

The user's successful-then-slow-then-quick behavior and recent low available
RAM support variability, but do not establish the latest slow run's exact
cause. Current sampling reached roughly504MiB available of7979MiB RAM. Earlier
six samples had971–984MiB, CPU4–19% and disk queue0. Private commit is not resident
RAM or proof of a leak. No processes/settings were changed for optimization.

An additional Windows .NET Runtime1026 event at22:54:21IST26October records
keyboard.wtf.exe terminating with unhandled code0xc0000005 (PID17024). Nearby
public log entries show a Smart Writing provider-segment failure, Whisper fallback,
listener shutdown and restart. The event has no module/stack/installed-path
attribution, so neither the ae2428d build nor Whisper is declared the cause.
This is an unresolved crash lead, not a proven Gemini outage or RAM diagnosis.
No camera/audio/native-runtime patch is justified without that attribution.

## Temporary data and retention

| Area | Confirmed behavior | Limits / follow-up |
| --- | --- | --- |
| Selection / translation | Source/results retained in memory; cancellation prevents clipboard publication; session ending disposes translation and clears selection/history | Managed objects released, not cryptographically erased; valid partial/result state intentionally lives until replacement/end |
| Screen analysis | Bitmap/MemoryStream ownership disposed, session screen snapshot reset | Not proof every external provider deletes its copies |
| Recording / Live | Memory/device disposal and session transcript/context reset | Forced termination can bypass normal cleanup |
| Piper output | Unique temp WAV deleted in finally | Crash/forced-exit orphan still possible |
| Connector context | Account invalidation cancels work/references; ResetAsync clears task/result/open state and awaits worker unwinding; expiry pruning | No native mail-content disk cache found in reviewed path; backend/provider retention is separate |
| Logs | Exact operational log plus two5MiB generations, protected from reparse paths | Diagnostics/evidence are separate and must be excluded from publication |
| Photos / voice notes | Saved as user outputs | Must not be treated as garbage |
| Model downloads | Normal partial/staging cleanup, checksums/bounds; recovery originals sometimes retained | Recovery bytes are not safe blanket-deletion candidates |

Source-level gaps requiring a small future retention policy:

- `CalendarDestination.cs:26–29` writes a unique temporary ICS and shell-opens
  it without a later cleanup owner. This is the legacy local-calendar destination,
  not the native Google Calendar connector. Immediate deletion would race the
  external app; actual accumulation was not proved.
- `SecureUpdateService.cs:80–130` cleans failed .part downloads, but retains
  successful per-version installers without eviction. Public updater cache could
  grow over versions; not a demonstrated private-app leak.
- Crash/forced-exit orphan recovery for unique WAV/download/staging files is not
  general. Any later cleanup must target exact app-owned paths, respect active
  operations and preserve rollback/recovery bytes.
- A failed save can leave one fixed .tmp for intent memory, learned mappings or
  session storage. This is bounded per-file residue rather than an unbounded
  GUID leak; do not delete authentication state speculatively.

Tool-visible temp inventory found no matching ICS/TTS WAV and no update-cache
directories, but physical-path identity for that inventory was not established.
Given known profile redirection, **the real PC is not certified free of leftovers**.
No temporary-file contents, emails or audio were exported or deleted.

## Public-release gates and fresh static checks

**Not public ready yet.** The main remaining gates are:

1. Runtime lifecycle issues: photo-related popup persistence, public/private
   handoff/startup expectations, requested hotkey conflicts and crash attribution.
   Intermittent speech/goodbye reports remain distinct from proven fixes.
2. Google OAuth: last owner confirmation20October said review was not submitted;
   current console/granted-scope status was not inspected. Confirm OAuth-owning
   project, least-required scopes, consent/domains/privacy, justifications and
   demo before submission. Applicable restricted-data processing assessment must
   be resolved; do not give every connector broader scopes by default.
3. Signing: current EXE and archived installer are Authenticode NotSigned.
4. Genuine public artifact: the available package is private connector staging.
   Build the reviewed public flavor with its independent inventory policy, then
   verify actual diagnostic exclusion, clean install/upgrade/uninstall, default
   startup/hotkey/Helper ownership and update/rollback in isolation.
5. Production backend/account/connector/quota/billing acceptance: private staging
   success is not current production proof.
6. Privacy: local site/privacy.html lacks connector/Composio data-flow disclosures.
   The live policy was not freshly verified. Reconcile actual local/backend/
   provider retention, disconnection/deletion and Personal/Managed processing.
7. Publishing: origin is a local protected checkout, not GitHub. Review/push/backup
   and public deployment are separate authorized actions. Website/download/feed
   hashes and versions must describe the same signed public artifact. No publishing
   was performed or authorized by this audit.
8. Support: explicit beta limitations, provider-failure recovery, incident evidence
   collection and a compatible public rollback/update procedure are still required.

Fresh checks performed without building/testing:

- Tracked/untracked source secret scan passed (ignored private evidence excluded).
- NuGet advisory query, including transitive references, returned no vulnerable
  packages. The existing asset inventory hash remained unchanged. This is not a
  complete native-library/security assessment. Initial unsupported CLI flag was
  rejected; that original output is preserved rather than hidden.
- Private package manifest has24files and no test assemblies/testhost/xUnit/TRX/
  source/PDB payload. **Deleting tests would not speed up installed Jarvis.** Keep
  coverage; select focused suites and archive developer output separately.
- Exact installed-DLL metadata confirms private recording adapters are present in
  this intentionally private staging artifact, with authentication guards.
  PublicDiagnosticExclusionTests incorrectly groups connector staging with public
  production while asserting their absence. This is a source/test expectation
  mismatch, not an executed failing test or proven public leak. Preserve the test;
  correct the build-flavor expectation separately and verify the final real public
  DLL. The69-case native-close gate never certified this public boundary.

Google reference:
[restricted-scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification).

## Existing acceptance retained, not rerun or overstated

| Feature | Evidence retained / current disposition |
| --- | --- |
| Native app closing |69exact-package cases incl5real Win32 cases; user confirms closes work; variable latency investigated, no new change |
| Positional tabs/browser | Previous real/helper/user acceptance retained; user explicitly froze it; no new executor/extension changes |
| Gmail discovery/exact open/follow-ups | Previous source/installed/provider/rendered/user records retained with their version boundaries; no claim every phrasing/provider outcome passed anew |
| Translation/chunking/clipboard/dynamic UI | Previous large-source markers, clipboard checks and user-confirmed working UI retained; no modifications |
| Speech/camera/conversation | User confirmed recent clear speech/fast picture; older intermittent interruption and goodbye omissions not declared universally fixed |
| Calendar | Prior native-create/provider/rendered result preserved; no extra event created in this review |
| GitHub advanced connector capabilities | Not freshly exercised; Gmail success does not certify every connector capability |

## What to do next, conservatively

Prioritize the demonstrated popup lifetime conflict and runtime ownership clarity,
then classify the crash before blaming providers or removing safeguards. Plan
small owner-scoped retention for legacy ICS/update cache after listing actual
physical candidates. Keep tests and accepted subsystems. Do not warm caches,
unload models, trim prompts, change audio/termination or migrate virtual desktops
without a separate measured reason and focused regression boundary.

Three obsolete Desktop launchers were removed at the user's explicit request
after exact hash checks and recoverable copies. Current Jarvis Start Latest.cmd
was retained. Rollback packages, underlying scripts, tests and original evidence
remain intact.

Evidence root:
`.artifacts/connectors/stabilization-plan-20261027/read-only-readiness/`.
The saved normal-host resume receipt, current-runtime.json, crash-window.json,
dependency-advisories*.json, secret-scan-receipt.json, diagnostic metadata and launcher
cleanup receipt are separate proof tiers. No universal end-to-end or public
release acceptance is claimed by this audit.
