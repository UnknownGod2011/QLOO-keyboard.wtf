# Calendar mode correction and Photos popup findings — 27 October 2026

## Delivered state

Installed private candidate: **4a1f4d059f0ff8714148e4c365643fb1e4be1bb2**.
Rollback: **1d745d2**, with ae2428d and all earlier recorded archives preserved.
This document follows the tested source; it does not represent another build.

- All **24 installed payloads** match the frozen archive. Physical installed-file
  paths were checked, rather than trusting the redirected profile view.
- EXE SHA256: `0A965E4DD09B270C5F94CBEDD3D735BFC408900B84010522D72F2F104C5C03A5`.
- Embedded DLL SHA256: `7E786885E3B3A6891695C05D2FDFC1C93FB1C88EBB2429F66928C57D097C0F6C`.
- Exactly one private GUI, PID13548 at verification; public-path PID12888 is the
  existing Chrome native host, not a competing public GUI.
- PersonalOnly, AutoExecute, Helper connection and five registered hotkeys verified.
  The existing Jarvis/Cancel fallback keys remain Ctrl+Alt+J / Ctrl+Alt+Escape.
- Handoff compared **29 protected profile files** without configuration/auth changes.
  The final check reverified **58 protected archive manifests** and the exact
  1d745d2 rollback installer. Public executable bytes remain unchanged.

## Confirmed Calendar causes and minimal corrections

1. **Calendar alone overrode AutoExecute.** `JarvisAutomationService.ShouldRequestPermission`
   explicitly forced confirmation for calendar_create even when the saved mode was
   AutoExecute. Its routine permission metadata also declared confirmation mandatory.
   Commit **e785d88** removes only those Calendar exceptions. AlwaysAsk, account/task/
   session authorization, approved-payload identity and duplicate-write protection remain.
2. **Date-only reminders lacked an executable all-day representation.** The existing
   Calendar contract accepted offset timestamps only. Commit **ce8bc87** extends
   `ConnectorOperations.Plan` and `MatchesCalendarEvent`, and the existing Calendar
   argument descriptions in `ConnectorRegistry`, to accept validated date pairs with
   an exclusive later end. Existing timed creation remains intact. Readback rejects
   mixed date/dateTime representations and verifies exact content and no attendees.
3. **Native reminder discovery was insufficiently explicit.** Commit **4a1f4d0** changes
   one existing `connector_resolve` description to distinguish dated Calendar reminders
   from relative countdown timers. It adds no tool, model call or global routing layer.

Exactly four production files changed from 1d745d2:

- `src/Services/JarvisAutomationService.cs`: the two permission methods above.
- `src/Services/GeminiLiveConversationService.cs`: connector_resolve description only.
- `staging/backend/KeyboardWtf.Staging.Api/Connectors/ConnectorOperations.cs`: Calendar
  planning and provider readback only.
- `staging/backend/KeyboardWtf.Staging.Api/Connectors/ConnectorRegistry.cs`: Calendar
  argument descriptions only.

No Gmail search, retained reference, exact opening, tab executor/safety, speech/VAD/
playback/interruption, translation/chunking/UI/clipboard, camera, popup, goodbye,
authentication, settings or hotkey implementation changed. This is a diff boundary,
not a claim that every unchanged feature was physically retested.

## Focused checks and retained failures

- New AutoExecute regression against the exact old installed DLL: **failed as expected**
  at the extra confirmation; current mode tests pass.
- Final directly affected client gate: **122/122 passed**, zero skipped.
- The **same 122 case names passed against the extracted packaged DLL**.
- New all-day cases against the previous backend: **3 expected failures**.
  Corrected backend gate: **56/56 passed**, zero skipped, including existing timed writes.
- An earlier broader focused run was **548 pass / 1 fail**. Its unchanged Gmail regex
  hit its existing 50ms timeout. The identical isolated case subsequently passed on
  both baseline and current code. The original failure remains, with no timeout or
  assertion weakened and no Gmail code changed. It is not reported as an all-pass run.
- No full multi-thousand-test rerun was needed.

## Real installed application and provider outcome

The guarded user-run handoff installed the frozen package once. One request was sent
through the actual installed app's Personal Gemini Live text route, without specifying
a connector: “Set a reminder for November 19, 2026 saying Jarvis Calendar Reliability Test.”
Gemini selected connector_resolve then connector_execute. No confirmation remained.

Exactly **one** event was created in the approved primary Calendar:

- Title: **Jarvis Calendar Reliability Test**.
- All-day start **2026-11-19**, exclusive end **2026-11-20**, no attendees.
- Status confirmed; organizer/account matched the intended signed-in account.
- Deterministic event ID: `kb5131dc4af0ff41fec296ac8e963c1255690b38aa50a6771fca125e5904346da`.
- Application create plus exact provider readback: **1,614ms**, two provider requests.
- Additional independent verification: **one read-only GET**, no create retry.
- Browser observation showed the actual event details heading and **Thursday,
  November 19** in Google Calendar. No browser edit/delete action was performed.

Measured from submission: native selection **2,613ms**; execute proposal **4,259ms**;
first audio **7,528ms**; provider turn completion **12,848ms**; playback queue observed
drained **13,885ms**. All 24 provider audio chunks reached the playback sink, none
missing. Task/activity cleared and the owned test session stopped. These are actual
installed/device-path measurements, not physical microphone or speaker-quality proof.
This correction adds no ordinary-command model call or routing stage; no new broad
performance benchmark was run.

The first acceptance script reported failure **after the operation completed** because
its post-check reparsed an already typed DateTime through a culture-sensitive string.
The original result is preserved. Independent reconciliation verifies the existing
session/history/queue outcome; neither the app request nor the event was rerun.
The first independent verifier likewise stopped before its GET because journal
resourceId was serialized JSON rather than a bare ID. Its corrected read-only check
passed. Neither diagnostic error is concealed or treated as a product failure.

## Private backend and rollback

Private Calendar revision: `keyboard-wtf-connectors-staging-00036-haj`, backend tree
`f7b45f894198f3507ef32fb675a1a45308de1faa`. Private tag `gs-q261013` and held test tag
`cal261027` point there. Default staging remains `00016-vic` at 100%; other tags,
runtime configuration, secrets, service account and OAuth scopes/grants were preserved.
Public production was not modified. Private backend rollback is the tag-only restore
of `gs-q261013` to `keyboard-wtf-connectors-staging-00034-yab`.

## Photos popup — unresolved; no speculative fix

The user's report of Jarvis appearing behind Photos remains valid but was not reproduced
as actual TopMost loss in this check. Earlier snapshots after normal session ending
cannot establish an active-conversation hide. A controlled active-session Photos opening
showed native visible=true, TopMost=true, current desktop and no DWM cloaking.

An initial EnumWindows rank interpretation was corrected: that enumeration cannot
establish visual stacking. The explicit GetWindow previous-window chain put the popup
above Photos. Point hit tests disagreed across the popup bounds, which does not establish
rendering or the cause. No claim about Label transparency or owner-window causation is
supported. The relevant popup code is otherwise unchanged from the older comparisons.

No owner detachment, focus stealing, TopMost polling, camera change or virtual-desktop
manipulation was added. A visible incident-time popup/Photos observation with the matching
session remains needed before a safe correction. No further user reproduction is requested
for this delivery. Cross-desktop following is still a separate unimplemented capability.

## Other connectors and remaining boundaries

Registry review is not certification of all provider grants or all capabilities. Gmail's
existing read/search/send subset is preserved. Calendar supports list plus timed/all-day
create; recurrence, update/delete, attendees and custom notification overrides are not
implemented. Drive supports name search/metadata; Docs and Forms read-by-ID and their
existing blank-create paths; YouTube channel/public-video search; GitHub its existing
read subset and issue-create; Slack search/history/post; Discord identity/server-list.
Meet, Reddit and LinkedIn remain coming soon. No scopes were widened.

The two obsolete Desktop launchers pinned to 1d745d2 were **not removed**: automatic
approval review rejected the cleanup before execution with “blocked by policy” and no
detailed reason. The current Calendar launcher, underlying scripts and all evidence remain.
Do not rerun its completed one-shot test. Public-beta OAuth, signing, clean installer
lifecycle, privacy/security and production acceptance gates remain separate unfinished work.

## Evidence

`.artifacts/connectors/stabilization-plan-20261027/calendar-popup-recovery/` contains
original before/after TRX files, package-check.json, handoff-result-v3.json, the original
installed-calendar-acceptance result, installed-acceptance-independent-reconciliation.json,
single-calendar-provider-verification-v2.json, rendered-calendar-verification.json,
installed-release-final-proof.json and the popup snapshots. Deployment records are under
`.artifacts/connectors/deployments/4a1f4d059f0ff8714148e4c365643fb1e4be1bb2-calendar-all-day/`.
