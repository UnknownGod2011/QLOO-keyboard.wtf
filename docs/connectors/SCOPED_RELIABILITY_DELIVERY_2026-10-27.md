# Scoped reliability delivery — 27 October 2026

Installed private source: **1d745d2eb4770447c33d82395c7e000340a8987b**.
Immediate rollback: **ae2428d**; f25011b and all older archives preserved.
This is a private reliability update, not public-release approval.

## What changed and why

| Production file / method | Confirmed fault and isolated correction |
| --- | --- |
| VoiceOverlayForm.RefreshState | Camera/tool Done could dismiss the view after 3.2 seconds while Live continued. Honor the existing valid Live Mute/Unmute owner for dismissal only, independent of button visibility. Ending ownership still resumes dismissal. No phase, camera, focus or speech changes. |
| WhisperRecognitionService.LoadModel/RecognizeFromWavAsync/Dispose; new WhisperModelLifetime | A factory could free the native context while a processor borrowed it. Retire generations until their last processor finishes, and await processor disposal on cancellation. Shutdown does not synchronously wait. Model/library/recognition parameters and Live playback are unchanged. |
| IntentMemoryService.SaveLocked; LearnedMappingService.SaveLocked | Failed atomic publication could leave the exact staging leaf. Finally performs best-effort deletion of that leaf through the verified handle helper; saved destination and original failure are preserved. |
| New OwnedTemporaryFileCleanup | Exclusive handle deletion validates exact final path, ordinary single-link leaf, age and optional size/SHA. Linked, busy, changed, readonly and unknown files remain. No recursive deletion or retry service. |
| CalendarDestination.SendAsync plus retention helpers | Future local ICS exports use a dedicated marked directory. Failed writes/launches clean their owned output; successful shell consumers retain it. Bounded opportunistic expiry after seven days; legacy/unmarked files untouched. This does not change the native Google Calendar connector. |
| SecureUpdateService verified-download hook; new UpdateCacheRetention | Future verified downloads receive ownership receipts. Expiry after 30 days protects requested/running versions and recent alternatives; at most two package hash attempts per invocation. Existing unmarked downloads and rollback archives are excluded. |

Independent commits: b06bf01 (test flavor), d08a8bc (popup), d507f4b (Whisper),
61d63c8/76fb1e0/a56b1e9/dfe8332 (owned retention), 81d0d4d (fixture correction).
No production changes followed the frozen package.

The pinned [Whisper factory](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperFactory.cs)
and [processor](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperProcessor.cs)
confirm the borrowed-context/async-disposal boundary. This demonstrated source
fault is independent of the historical crash; that event remains unattributed.

## Evidence and failures retained

- Four photo-Done popup cases failed against the exact original installed
  ae2428d DLL and passed on corrected source. Initial copied-harness runtime
  omission aborted before assertions; that failed attempt is retained.
- Source gate: **245 passed, 0 failed, 0 skipped**. First run had 243 passes
  and two new fixture failures: Windows rejected an exclusively held destination
  with UnauthorizedAccessException instead of IOException. Tests now allow only
  those two filesystem rejection categories; original saved-byte equality and
  staging-cleanup assertions remain unchanged. Original failed TRX retained.
- Extracted frozen-package DLL: **same 245 named cases passed**, no skipped cases.
- Actual native Whisper recognition and in-flight cancellation/shutdown passed
  both source and extracted-package assemblies using the existing model/recording.
  Package run: 483 recognized characters, 9,975 ms inference, shutdown returned
  in 0 ms; native worker cleanup preceded context release. No device/provider use.
  These are local Whisper measurements, not ordinary-command or Live latency.
  Earlier isolated native fixture layout failures are retained as harness errors.
- Installed photo/Photos path: captured saved output, completed device queue,
  Listening resumed, no remaining task. Second bounded session additionally
  closed the newly opened Photos window and retained the same Live session.
  Native snapshots before/after/delayed-after close show visible=true,
  TopMost=true, cloaked=0, onCurrentDesktop=1. User confirmed:
  **“rn it didn't disappear in this test.”**

### Important popup limitation

The user first reported **“Popup still disappears”** on the corrected installation.
That failed report is not erased by the later pass. The subsequent hidden-window
snapshot was taken after the microphone session had ended: physical log shows
Smart Writing hotkey at 02:21:51, client cancellation and normal Live shutdown;
the hidden snapshot was at 02:23:25. It cannot establish active-session hiding.
The latest controlled photo/open/close run and user observation passed, but the
earlier intermittent disappearance is **not fully attributed or universally fixed**.

No speculative native Show/TopMost loop, owner detachment, camera modification,
desktop move or conversation-ending patch was added. The hidden same-process
WinForms taskbar owner is normal. Ordinary owner hiding does not itself hide
owned windows; changing ownership is not justified by this snapshot.
See [Microsoft window visibility](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#window-visibility)
and [pinned WinForms Form](https://github.com/dotnet/winforms/blob/v8.0.31/src/System.Windows.Forms/src/System/Windows/Forms/Form.cs#L6521).

## Installation and regression boundaries

- All **24 installed payload hashes** match the frozen archive.
- EXE SHA256: 55C583781310D3180E160C16F7EE34B793EA5E3A00758A2F757B1960E3C35060.
- Embedded DLL SHA256: D0CC0E81308E87385039559D8342C37F78F858A5556D707113D20C7F718109B1.
- One private GUI18076 plus native host13956, not a second public GUI.
- 29 protected profile files compared; authentication/session files, settings,
  startup and Helper registration unchanged. PersonalOnly and Helper verified.
- 57 previous rollback manifests unchanged. ae2428d archive remains usable.
- Existing requested Q/X hotkey conflict persists. Current registered fallback
  remains Jarvis Ctrl+Alt+J, Cancel Ctrl+Alt+Escape; no settings reset or unrelated
  process termination.
- Gmail/search/retained follow-ups, native exact opening, tab executor, browser
  extension, translation/chunking/progress/clipboard, camera, Live speech/VAD/
  playback/prompts/model, goodbye and app resolver source unchanged from ae2428d.
  Existing acceptance retained; no claim these were all physically retested here.
- No added model/provider call or ordinary-command routing stage. Retention work
  occurs only at its owned save/export/update boundaries.
- Old ae2428d Desktop starter retired after hash-verified recoverable copy.
  Current update/evidence wrappers were retained while their user consoles might
  still be open. Underlying scripts and archives remain.

## Deliberately unresolved

1. **Virtual-desktop following:** not established; only one desktop was available.
   Own toolwindow Guid.Empty association and supported same-desktop S_OK do not
   prove cross-desktop behavior. No undocumented pinning or desktop manipulation.
2. **Historical 0xc0000005:** old PID/module/build attribution lacks WER/dump.
   Native lifetime correction is verified but is not a crash-cause claim.
3. **Cold app discovery:** unchanged. Equivalent 209-ID native COM catalog saved
   about 572 ms (1,697 vs 2,269 ms) but lacked the external process timeout and
   had label differences. That does not justify replacing the accepted resolver.
   RAM pressure is a possible contributor, not established incident causation.
4. **Old/unmarked leftovers:** not swept and physical profile not certified clean.
   New owned policies prevent the demonstrated forward-retention gaps safely.
5. **Public beta:** OAuth verification, code signing, real public artifact and
   clean install/update/uninstall, privacy disclosures, production provider
   acceptance, publishing/download consistency and support/rollback remain gates.
   Nothing published; no scopes expanded, backend deployed or cloud event sent.

Evidence:
.artifacts/connectors/stabilization-plan-20261027/scoped-fixes/
includes original failures, native results, exact package checks, guarded handoff,
protected comparisons, physical readback, window snapshots and delivery-status.json.
