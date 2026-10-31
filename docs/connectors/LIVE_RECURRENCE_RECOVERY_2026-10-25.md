# Live microphone recurrence: scoped recovery

## Installed baseline and real failures

Installed private `4fb5948dd3f8beba59a1eec2db2c8707861261c3` remains protected in
`dist/connector-candidate-archives/4fb5948-reviewed`, alongside 7618450,
018b835 and d0290be. Its EXE SHA-256 is
`A6CDBAFC8AD29F01624704E85D238B259256358CED3CE7CF04892D4C5FC12758`.
The user's failed microphone acceptance supersedes the earlier passing text
sequence. It is not an accepted release. No installed files changed during
this investigation.

Evidence lives under `.artifacts/connectors/desktop-recurrence-20261025/`.
The normal-shell readback `physical-live-20261025-042945.json` captures actual
GUI session `4cd8e26d3c3049f0b2acf56cf30e9a67`; ordinary tool-side AppData was
stale. One tab-closing turn generated no tool, a later turn successfully closed
the exact rightmost three tabs, and later browser proposals lacked a successful
action receipt. The log does not identify every rejected predicate or preserve
the spoken words. These are distinct observations, not one proved failure.

## Confirmed search gap

Provider traces show the failed microphone session searched `ricardo`/`Ricardo`
as free text. Both searches returned two unrelated conversations: a newsletter
and an old question-paper email. Metadata-only hydration succeeded; neither
observed From header identifies the requested person. The successful text test
used correctly spelled Riccardo. It therefore did not reproduce this case.

The direct native Live path bypassed the participant preparation already used
by `ConnectorReadReasoner`. Nonempty body matches prevented empty-sender
recovery. This is a shared execution-path gap, not evidence that the installed
binary was wrong. The preparer exists in the older 40d8a72 search implementation;
restoring the entire older component would lose later verified behavior.

Changes are limited to:

- `ConnectorMailSearchArguments.Normalize`: reuse preparation for an identical,
  unqualified human person name; preserve literal/operator/date/topic queries,
  structured constraints and model spelling refinements.
- `ConnectorPersonSearch.PrepareQuery`: isolate one already-valid exact opening
  clause for search preparation only. It grants no action authority, changes no
  task goal and does not enable the headers-only completion shortcut.
- `JarvisConnectorController.ExecuteAsync`: supply the existing originating
  request to that adapter.
- `ConnectorMailSenderRecovery`: allow the preparer's canonical single-person
  participant query to use the existing one-query sender spelling recovery.
  Results require observed matching From headers; recipient spelling coverage
  remains explicitly partial. No new model call, account scope or retry loop.

## Confirmed tab readiness mismatch

`focus-physical-20261025-081508.json` independently records Chrome in the real
foreground with a matching Helper title, a connected Helper and
`activeTabFresh=false`. Current status identifies the missing page content
script. The extension defines that flag from DOM/content-script readiness,
whereas exact activation/closing uses its existing service-worker tabs API.
The same content readiness requirement exists in d245ac4; unchanged code did
not prove correct behavior on a tab without a content script.

`JarvisAutomationService.CanResolveForegroundBrowserTabReference` now also
accepts a recent, trusted, version-matched, single Helper session for these
native tab operations. Real foreground process/title, window handle continuity,
session continuity, native exact-ID resolution and distinct requested targets
remain required. The executor, extension, DOM automation and selection capture
are untouched. A true non-Chrome foreground still refuses. This does not claim
to explain the earlier no-tool turn.

## Focused verification and limits

Original source with faithful provider query echo: **6 failed / 1 passed**
(`valid-native-baseline-v2.trx`). Corrected focused gate: **170 passed**
(`native-preparation-and-authority-after-v2.trx`). Covers body-only rejection,
same-session ownership, spelling recovery during reasoning-provider failure,
compound requests, literal/date/topic preservation, missing DOM readiness,
stale/future/untrusted Helper authority and real foreground changes.

Earlier test setup failures remain preserved: mismatched fake queryUsed,
baseline compilation against new test-only API calls, and the first candidate
regex exceeding .NET's symbolic automaton budget. None is an installed-app
success or a physical microphone result. Their corrected reproductions are
separate receipts.

The focused Release ConnectorStagingCandidate gate passed **1,133/1,133**
with no failures/skips in 3m42s (`focused-release.trx`). This is the one wider
affected-path gate, not a full application suite. Warm local argument preparation
measured 0.0682ms median / 1.3039ms maximum over 30 samples; that measurement
excludes model/provider/network and playback time. Ordinary command routing is
unchanged and performs none of this connector preparation.

Package and installed acceptance are pending. Speech, microphone/VAD,
playback, prompts, model selection, translation, camera/overlay, goodbye,
settings and authentication production files remain unchanged from 4fb5948.
Reuse prior translation HTTP503 recovery and capacity evidence; do not label
large selected-text clipboard output newly verified without its real outcome.
