# Additive feedback and native app-close correction — 26 October 2026

The user accepts installed e177447 as the working baseline for connectors,
translation and tabs. Normal translation/copy/dynamic UI and tab closing were
confirmed directly. Tab clarification on unclear input is accepted behavior:
do not change its parser, executor, extension or safety guards.

## App-close cause and constrained correction

Opening uses the existing trusted Windows app catalog. Native closing first
matches a small process-name identity set, otherwise exact process name or
exact non-browser title. The actual installed DLL reproduces zero matches for
Paint/mspaint/Untitled - Paint and Teams/ms-teams/document titles. This source
is identical in d0290be and e177447: it is an existing selection gap, not a
confirmed newly introduced regression. Actual read-only process identity queries
return the same Paint/Teams application IDs advertised by Windows' app inventory.

The correction only runs after that existing selection finds nothing. It reuses
the cached installed StartApps catalog, prefers an exact display label, otherwise
requires one distinct identity matching a trailing whole display name. Raw name
normalization is used; fuzzy typo canonicalization and ranked-best guessing do
not authorize a destructive action. Each non-browser/non-Explorer candidate
must have that exact Windows process application ID and any supplied window ID.
Multiple matching windows retain the existing clarification/all-scope behavior.

No new Paint/Teams names or process mappings are added. No process is killed.
Normal WM_CLOSE, target handle/PID/thread/class revalidation, cancellation and
actual disappearance verification are unchanged. A tray app choosing to keep
running, or a save dialog retaining its window, is separate from finding the
correct target. The exact existing Teams window is not closed during readback.
Do not claim every app/exit mode works from these selection checks.

Microsoft documents that process AUMID reading needs only
[PROCESS_QUERY_LIMITED_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getapplicationusermodelid).
The fallback uses that right and fails closed on unavailable identity; it does
not need elevation or a background service. Ordinary commands and successful
existing close targets do not enter discovery. Cold discovery latency still
needs measurement; the existing catalog has a six-second subprocess deadline.

Independent review identified fuzzy-alias and catalog-ambiguity gaps in the
first draft; both were corrected before promotion, with added rejection tests.
The actual revised source binds the real Paint window and Teams window to
their exact OS identities, including the short Teams name: old lookup0matches,
new lookup1exact matching HWND in each case. Cold catalog discovery was5096ms,
cached discovery2.4ms; native binding was31/6/3ms. This is not a model/action
latency measurement. The existing fallback runs only on an old-selection miss.
Paint accepted a normal UI Close and its blank window disappeared; this is
separate from an installed Jarvis command. Existing Teams work remained open.

Initial focused gate:177passed, zero failures/skips. The combined380-case gate
first passed379 and failed one new assertion that read a current transcript
after normal completion cleared it. That assertion now checks committed context;
production was unchanged. Its rerun passed378, with two unchanged white-meter
UI cases failing after one15-second STA timeout and a subsequent shared-state
mismatch. Those same three theory cases pass isolated on both the archived
e177447 baseline and byte-identical current source DLL. All required current
case names now have passing evidence; no timeout/assertion was weakened, and
no single combined all-pass run is claimed. The timeout's trigger is unproven.
Both failed runs are preserved; the extracted-package gate must pass before
installation. Do not silently discard or reinterpret these failures.
The original installed-binary reproduction is retained as
`before-close-selection.json`, not replaced by the later source checks.

## Generic feedback boundaries

User requests concise feedback for slower ordinary tasks and selected-source
processing, with a reliable generic fallback. No extra AI call is warranted.
Existing Thinking/Executing already shows elapsed work, but a settled tool-only
completion can restore Listening before a later model reply. Merely changing
the capture label would not cover that interval.

The implementation at cc1c406 is a display-only observer, using the existing
one-second monitor and revision-bound Listening advisory. It must not acquire
a synthetic long-task owner, alter response-health rules, speech/turn completion,
tools/prompts, cancellation or retries. Existing translation/connector owners,
queued speech and health/error/reconnect status take precedence. A newer user
input or delivered output must invalidate the old visual caption. Status text
does not imply a successful action or expose private source content. Its2-second
grace uses the existing1-second Personal monitor, so feedback appears in about
2–3seconds. Current Managed behavior is unchanged. No new timer/AI call exists.
The existing actual receive-frame fixture covers selected-source receipt wait,
tool-only completion, new-input overlap and same-frame input/output/completion.
The app-identity correction is separately reversible at bba7562.

This source work is not installed acceptance. Keep e177447 and all rollback
packages unchanged until the exact package checks pass. Physical caption timing,
actual new-build Jarvis Paint/Teams closing, and tray-exit behavior remain
separate acceptance outcomes. No public deployment/publication is part of this work.

Evidence root:
`.artifacts/connectors/stabilization-plan-20261026/ui-and-native-close/`.

## Frozen package and installed handoff

Frozen source f25011ba18b0400eee2e6a33cebad508f12ee7d4 compiled once and its
exact offline-extracted private DLL passed all380focused cases, zero failures
or skips, in one packaged run. Original source failures above remain retained;
the passing packaged run does not erase them. Package/evidence root is
`.artifacts/connectors/stabilization-plan-20261026/candidate-f25011b/`.

The first normal Windows attempt stopped at the existing2GiB disk-headroom
guard before any app/installer mutation. Its console is preserved separately.
After the user freed space, the same package/launcher was reused. The one actual
installer handoff completed26October12:21:23Z. Installed EXE
EAC0D4A75113748D320A2FE111E959FC07822D3DAC943D526B97311944C8D4CF
and extracted installed DLL
450C2524395855D908B87DD2FE43BD59F26685CBFB66A44C4478DCF960A933EC
match the tested package. Manifest SHA256:
3D5A22DDBF7CA3A300CAD025E90BDF5DFE7B7EE7869B1937FEF4DA14DBF8B808.

Handoff receipt confirms one private GUI39920, PersonalOnly, five hotkeys and
Helper connectivity. Independent stopped snapshots compare29protected profile
files with zero changes, no token changes, unchanged public executable/startup/
Helper registration and no issues. The other public-path process is the Chrome
native host, not a second GUI. Current status readback again shows connected
Helper/bridge, a fresh tab and no multiple Helpers. No backend deployment,
provider write or public publication occurred. e177447 and all older rollbacks
remain intact.

This proves installation/configuration identity, not the remaining physical
acceptance. One short user check is pending for a slow task's caption/cleanup,
normal Jarvis closing of a blank Paint window, and Teams only when no work is
active. No repeat Gmail/tab/translation test was requested. Do not mark actual
Jarvis app-close or caption timing verified from the package gate alone.
