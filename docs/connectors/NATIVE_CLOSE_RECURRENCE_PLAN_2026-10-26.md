# Native close recurrence: analysis and minimal plan — 26 October 2026

**Initial read-only checkpoint; authorized implementation recorded below.**
At the analysis checkpoint installed source was f25011b, actual EXE
EAC0D4A75113748D320A2FE111E959FC07822D3DAC943D526B97311944C8D4CF.
The user accepts the new dynamic UI and existing tabs/translation/connectors:
freeze those paths. The latest instruction is to plan/analyze before action.

## What the real run establishes

The installed Settings action-history readback records successful opens using
**AppPaths**: Paint -> Paint and Teams -> Microsoft Teams. The previous bounded
source acceptance validated the other trusted catalog source, **StartApps**.
NativeWindowCloser.SelectInstalledApp currently excludes every source except
StartApps. A pure fixture invoking the exact extracted installed DLL, using
the observed AppPaths source and actual registered Paint executable, returns
zero matches. Thus the current correction does not cover an actual successful
opening identity. Why this session lacked a matching StartApps entry is not
established; do not assume timeout/cache/permissions or add retries.

The existing close handler already enumerates open windows. On a zero-match
result it returns windows=targets, which is empty. A separate list operation
provides current process names and exact hexadecimal IDs. That helps Gemini
reformulate the same request, explaining the observed benefit of listing.
The post-list receipt records close mspaint successful. The earlier failed
proposal's exact app_name/window_id is not available from action history;
name variation, stale ID and omitted tool remain distinct hypotheses.

Teams also demonstrates a separate verification boundary: the close ms-teams
receipt is false, while the original HWND2493300 is still owned by the original
ms-teams PID39364 but IsWindowVisible=false, owner0 and absent from the normal
window list. Current CloseAsync checks surviving handle/PID/thread/class only,
so this state is always counted as remaining. This is a post-attempt snapshot,
not an event-time trace; it does not prove every earlier Teams failure had this
cause. It establishes the verifier's mismatch for tray-style hidden windows.

Actual registered AppPaths for Paint and Teams point directly to their installed
WindowsApps executables, not command scripts or generic launchers. A generic
exact image-path binding is therefore available without new name-specific rules.

## Proposed isolated corrections (analysis checkpoint)

1. Keep existing successful process/title selection untouched. On a miss, reuse
   the same cached trusted app catalog used for opening. Accept exact display
   identity from StartApps or registered executable identity from AppPaths,
   validating the existing trusted `.exe` path and exact running image path.
   Preserve raw-name matching, exact-label priority, ambiguity, explicit HWND
   filtering, browser exclusion and snapshot handle/PID/thread/class checks.
   Do not use fuzzy destructive matching, strip arbitrary prefixes, invent app
   aliases, restart apps or retry catalog discovery. Only equivalent OS identity
   evidence may be deduplicated; same display name alone is insufficient.
2. Keep WM_CLOSE and cancellation unchanged. Verify the requested **window**
   outcome separately from process exit. After a dispatched close, a bounded
   stable hidden state may count as window closure only when no visible owned
   Save/confirmation dialog exists. Inspect the owner chain; normal enumeration
   deliberately excludes owned windows and cannot prove absence of a prompt.
   Minimized or other-desktop-cloaked windows remain open. Never target recycled
   handles or force-kill a process. Say window closed/background app may remain,
   not process exited. A visible target or owned prompt remains pending.

Both corrections should be independently reversible. No extra Gemini call or
new reasoning loop is required for successful name resolution. The automatic
native snapshot already exists; the user need not manually request a list.
Only if identity genuinely remains unavailable should a recoverable structured
receipt expose relevant current candidates; do not remove exact-ID guards or
convert ambiguity into an arbitrary close.

## Focused verification before any promotion

- Before/after AppPaths-only catalog and actual-image binding, plus an unfamiliar
  app with different display/process names. StartApps and existing working
  process/title closes must remain unchanged.
- Competing installed identities, stale/denied executable path, browser title,
  explicit wrong ID and recycled HWND remain rejected.
- Visible close, normal hidden/tray close, minimized/cloaked window and hidden
  parent with visible owned Save dialog produce truthful separate outcomes.
- One same-session real open -> spoken name close, without a preceding user
  window-list request. Keep physical, native and fixture evidence separate.
- Preserve dynamic UI, translation, tab executor, speech, authentication and
  current performance; no full-suite or unrelated repair is warranted.

## Other requested analysis

**Popup:** VoiceOverlayForm is unchanged across fb0a067,d0290be and f25011b.
It sets TopMost and recreates handles with WS_EX_TOPMOST/TOOLWINDOW/NOACTIVATE.
It intentionally hides on idle without owned work, and3.2seconds after terminal
state. No event-time visibility/style/cloaking/rectangle/z-order/phase evidence
identifies the reported disappearance. A normal log-viewer attempt was blocked
by target geometry, then its file picker was dismissed; no file or setting was
changed. Do not add TopMost polling, focus stealing or virtual-desktop machinery.
No overlay correction is justified yet.

**Goodbye:** The existing standalone matcher explicitly accepts "end the
conversation now". The matcher/end handler/one-second authoritative transcript
grace/shutdown path match d0290be. In Personal Live, incremental ASR does not
end the session by itself: Gemini must propose end_conversation, followed by the
local whole-request guard. Missing tool choice or late/incomplete transcription
can prevent termination; normal connector permissions do not control this tool.
Existing tests supply the proposal, so do not prove live model selection. The
precise latest refusal is unclassified. Per user instruction, change nothing.
If a future trace proves omitted tool choice, the smallest candidate is reusing
the existing matcher/shutdown only after complete finalized standalone input,
with negated/quoted/compound safeguards, never an interim farewell prefix.

Operational evidence is under
`.artifacts/connectors/stabilization-plan-20261026/f25011b-live-close-analysis/`:
fresh action history, exact installed-DLL AppPaths reproduction, registry readback
and current Teams native-window snapshot. No new collection script was requested
from the user and no new app closing was performed during this analysis.

## Authorized implementation and focused evidence

The user's subsequent "fix it" authorized these two isolated corrections:

- `34130b1`: `NativeWindowCloser.SelectInstalledApp` now accepts validated,
  existing trusted AppPaths executables and matches their exact normalized
  running image path using query-limited process access. Exact catalog labels
  take priority across sources. Existing StartApps selection, ambiguity and
  AUMID-read failure remain authoritative; no switch to a different path after
  an identity failure. No new aliases, title substrings, AI calls or discovery
  retries. The ordinary successful process/title path is unchanged.
- `7e4f60d`: `Read` records native WS_VISIBLE. `CloseAsync` accepts a retained
  hidden HWND only after successful close dispatch and 120ms continuously
  hidden without a visible owned window. A visible/prompt state resets that
  observation. The native check walks the full owner chain, including untitled
  owned dialogs excluded from ordinary window enumeration. WM_CLOSE, snapshot
  identity, cancellation, visible/minimized/cloaked windows and the existing
  overall two-second deadline remain intact. Processes are never force-killed.

Only `src/Services/NativeWindowCloser.cs` changed in production. Separate
commits make each correction reversible. No dynamic UI, speech, translation,
connector, tab, browser execution, camera, overlay or goodbye source changed.

Focused source evidence, all retained under `native-close-correction/`:

- Identity plus existing resolver/window tests: 57 passed, zero failed/skipped.
- Corrected completion plus identity/window tests: 55 passed, zero failed/skipped.
- Actual Win32 disposable-window tests: five passed, zero failed/skipped.
  Real WM_CLOSE verified destruction and tray hiding; direct, indirect and
  delayed untitled owned prompts correctly prevented success. Fixtures remained
  nonactivating, alpha-zero and offscreen and touched no user-owned app window.
- Initial incompatible build-flavor invocation was rejected before compilation;
  its original log is preserved as `identity-buildflavor-guard.log`. Corrected
  invocation used only ConnectorStagingCandidate. No failing assertion was
  weakened or hidden.

These checks establish resolver policy and real native-window behavior. They
do not establish a user's microphone-driven Gemini tool selection. Packaging,
installed identity and physical acceptance must be recorded separately.

## Frozen package, installation and final comparison

Frozen candidate `ae2428d77a7ec3a521e85a0a4ab63c6c9a4d481c` was built once.
Its exact offline-extracted DLL passed all69focused resolver/native-close cases,
zero failures or skips, including the five real Win32 fixture cases (five-second
packaged gate). The original incompatible-flavor guard log remains preserved.
No unrelated full-suite rerun was performed.

The normal-host handoff completed26October2026 at19:21:36IST. One private GUI
PID15932 owns the installed app; the other public-path process is Chrome's
native host, not another GUI. Installed EXE and independently extracted DLL:

- EXE: `5286891CF9175D056B559719746CD711A01F33C68F22C60B22B5375461B77EC8`
- DLL: `EFD2F3AB3B90A45A9846F2BB9E864BFFBBF8AC2C48046A9BB59CC73BA13255D0`
- Manifest: `A0C915D2A80A1335F6F5F1261C697959ABDA4AAEB3ADFE322258D7431CEBF157`

The stable stopped before/after comparison found29protected profile files
unchanged, no token changes, preserved PersonalOnly and five hotkeys, connected
Helper, and unchanged public executable/startup/native-host registrations.
Rollback f25011b and every previously protected package remain intact. The
tool-shell preflight stopped safely on the old redirected startup log; no guard
was bypassed. The user then performed the one normal-host installation.

| Path | Previous f25011b | Corrected candidate | Necessity and boundary |
| --- | --- | --- | --- |
| Existing process/title close | Exact process/title snapshot | Unchanged | Previously successful behavior preserved |
| Registered name fallback | StartApps/AUMID only | Same authority, plus AppPaths-only exact image identity | Real open receipts used AppPaths, original DLL returned zero matches |
| Conflicting identity | Ambiguity or failed AUMID rejected | Unchanged; no path fallback from failed selected AUMID | Prevents silently selecting another app |
| Window completion | Surviving same HWND always pending | Dispatched stable hidden window may finish without process exit | Matches tray-window behavior; visible owned prompts still block |
| Browser, Explorer, snapshot checks | Existing exact protections | Unchanged | Native fallback never treats website titles as app authority |
| Dynamic UI, speech, translation, connectors, camera, overlay, goodbye | Accepted implementations | Byte-identical source | No justified correction to those paths in this task |

No new AI call, catalog-discovery retry, app alias, model/prompt change, service
or process-killing path was added. Normal destroyed-window verification keeps
its existing polling. Retained hidden windows require120ms of stable native
observation within the existing two-second budget. Real Teams AppPaths-only
selection was verified read-only against its actual registered/running image
(first reflected selection35.3ms, not a measurement of user speech latency).

The installed action history subsequently recorded `close Paint` success at
19:25:36IST. A separate native enumeration at19:26:41IST found zero visible Paint
windows. This is real desktop execution and native outcome evidence, separate
from the fixture tests. Final human confirmation of the Teams close experience
and spoken response remains pending; do not call all physical acceptance passed.

At the user's request, six obsolete Desktop launchers were removed only after
exact hash verification and recoverable copies in candidate evidence. Current
updater and two recovery/resume launchers were retained. No rollback package,
underlying script, original failure evidence or unrelated Desktop file was deleted.

Detailed frozen/package/installation/cleanup evidence:
`.artifacts/connectors/stabilization-plan-20261026/candidate-ae2428d/`.
