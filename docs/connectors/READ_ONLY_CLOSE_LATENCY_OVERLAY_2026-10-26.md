# Read-only close latency and overlay investigation — 26 October 2026

No production code, tests, configuration, model/prompt, app installation, app
ownership or window state changed. No new provider calls, controlled app closes,
camera captures or repeated regression suites were run. Installed candidate is
still ae2428d; rollback f25011b and older packages remain preserved. Installed
EXE SHA256 remains5286891CF9175D056B559719746CD711A01F33C68F22C60B22B5375461B77EC8.

The user confirms actual closes worked, initially felt slow and later became
quick. This establishes functional improvement but does not establish the cause
of latency. The following findings keep current snapshots and earlier incident
evidence separate.

## Responsiveness

**Confirmed application path:** the installed-app catalog already exists and
is shared with app opening. `AppResolverService.Discover` caches a nonempty
catalog for10minutes. Cold discovery synchronously invokes hidden Get-StartApps
(six-second process wait), then scans Start menu, registered AppPaths and known
executables. Six seconds is not an overall-operation deadline. It also holds
the existing cache lock while discovering.

The resolver itself is identical in d0290be, f25011b and ae2428d. The f25011b
native-name fallback introduced a call to this existing discovery only when the
old native process/title lookup misses. Paint can enter it because Paint and
mspaint differ; Chrome/Notepad generally match their processes directly. The
ae2428d identity correction does not add another discovery call. Existing saved
read-only measurements show5095.6ms cold versus2.4ms cached, not the latency of
this user's latest command. The catalog is not recreated for every Live session.

**Confirmed latest receipts:** actual installed history contains successful
Paint, Teams, Chrome and Notepad closes. At19:31:06IST, the Paint and Teams close
receipts are234ms apart. Those are consecutive completion timestamps, not
microphone-to-action timing. The history also preserves one early Teams failure
at19:26:17IST followed by later successful closes; its specific failure cause
cannot be recovered from receipt flags alone and is not silently treated as a
pass. No post-install virtual-desktop action was present in the retained history.

**Resource samples:** the machine has7979MiB usable RAM. Initial available memory
was881–938MiB; six later samples over24seconds showed971–984MiB. CPU was4–19%,
disk queue0, and page input was0 in five samples and128pages/sec in one. Jarvis
working set was105.5–105.6MiB, private committed bytes1577.7–1577.8MiB. Private
commit is not physical RAM usage and is not proof of a leak. Low memory headroom
can contribute to variability; these later samples do not prove a historical
stall. There was no duplicate private GUI (PID15932); the public-path process
was Chrome's native host, not a public GUI.

**Unproven:** how much of the user's slow runs came from model/tool choice,
speech endpointing, cold discovery, app response or transient machine pressure.
The current action history has no start times. Tool-side profile logging remains
redirected to the old24October file, so it was not presented as fresh. Existing
discovery stage logs are compiled under PERSONAL_API_CANDIDATE, not this
CONNECTOR_STAGING_CANDIDATE flavor. Fresh logs alone therefore would not provide
all stage timings. No false claim that PC lag alone caused the slowdown.

## Popup background/disappearance

The actual native popup HWND300A92 was inspected without input or activation:

- WS_EX_TOPMOST, WS_EX_TOOLWINDOW and WS_EX_NOACTIVATE remain set.
- Native visibility is false; it is not minimized or DWM-cloaked.
- Public IVirtualDesktopManager reports it on the current desktop.
- Its rectangle intersects the monitor; it is not currently offscreen.
- Its owner is a hidden window in the same private Jarvis process, not Photos.
- Redacted child-label inspection reports an ended-session status and the
  start-hotkey hint; raw label contents and private speech were not retained.

This establishes that the currently inspected popup is **hidden after an ended
session**, rather than a visible popup whose topmost bit was lost. It does not
reproduce or resolve the user's earlier active-session disappearance.

`VoiceOverlayForm.cs` has the identical Git blob
b9a6f4d44028c6bcdd2515e6f8879eabbc1106f9 in fb0a067, d0290be, f25011b and ae2428d.
It sets TopMost in the constructor and WS_EX_TOPMOST/TOOLWINDOW/NOACTIVATE on
every created native handle. It deliberately calls Hide on Idle with no owned
work, or after3.2seconds in Done/Cancelled/Error with no owned work. Its primary
monitor placement is unchanged. The Live completion path normally restores
Listening. App close does not itself publish Done on success.

The new native closer cannot select this popup through ordinary enumeration:
the popup has a nonzero owner and no title, and both conditions exclude it.
Its new image/visibility checks only read OS state; they do not change z-order
or virtual-desktop placement. No new direct hide/move path was introduced.

Still unresolved: whether the intermittent active-session case is a terminal/idle
state dismissal, virtual-desktop cloaking, another topmost surface, delayed UI
refresh or some other transition. It needs incident-time visibility/owner/style/
cloaking/desktop plus status evidence; the current ended-session snapshot cannot
substitute for that. No TopMost polling, forced activation, hidden-owner change,
virtual-desktop workaround or source patch is justified by this snapshot.

Microsoft's Win32 documentation distinguishes topmost z-order from visibility
and explains that inactive virtual desktops hide their windows:
[SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos),
[IVirtualDesktopManager](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager),
[DWM attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute).

Read-only evidence:
`.artifacts/connectors/stabilization-plan-20261026/latency-overlay-readonly/`:
action-history.json, resource-samples.json, window-snapshot.json and
hidden-overlay-label-categories.json. CUA window inventory showed only ChatGPT;
no desktop input, app launch, foreground change or clipboard operation occurred.

## Disposition

Keep the accepted native-close correction and working app behavior unchanged.
Cold discovery is a real potential multi-second application-side path; machine
pressure is a plausible additional contributor, not a confirmed sole cause.
The historical active-session popup incident remains unresolved, with current
native topmost/desktop/ended-state evidence now recorded. No new build or
installation is warranted for this read-only request.
