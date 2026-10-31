# Connector PC checkpoint

This is a separate **Connector Staging Candidate**, not replacement of the accepted
public keyboard.wtf 1.4.21. The normal public download, account, profile, startup,
Helper registration and updater remain protected. Promotion to the real website/
public desktop follows this acceptance, not merely successful compilation.

Latest prepared runtime: **`7e9aebc`**, not yet installed. Exact-source regression
403 focused / 4,528 full passes and private 24/24 package hashes are verified.
Use [the 10 October PC-ready checkpoint](PC_READY_2026-10-10.md) for the actual
installer, safe read-only tests, proof limits and remaining gates. Current
installed staging runtime is **`7a1f6f7`**, held backend **`a25d094` / `00028-boh`**;
its installed checks are in [current status](CURRENT_CONNECTOR_STATUS.md).
No public app/profile/Helper replacement and no repeat of the protected self-send
or Form-create tests. Installation is intentionally left for the owner later.

## Earlier installed checkpoint — df5cdf1

Earlier installed staging runtime: **`df5cdf1`**, held backend **`4664440`**, with
24/24 package hashes and 10/10 actual Settings checks. Combined natural Gmail
execution now has three verified searches and a count, independently corroborated
by authenticated task GET and cloud telemetry. Actual popup screenshot shows the
Gmail logo, blue connector orb and friendly search text. In-flight bare Cancel
reached server Cancelled before a provider execute result published; subsequent
arithmetic had zero connector calls. Final sessions are idle.
Semantic/audible answer acceptance and a failing alternate count wording remain
open: execution success is not universal answer accuracy. Prior Doc/Calendar/
Drive/YouTube/Form/GitHub proof remains distinct. Public installation/profile/
startup/Helper unchanged. A source-only wording fix additionally accepts
“Cancel that connector task”; it is not silently relabelled as installed.
Details: [current status](CURRENT_CONNECTOR_STATUS.md).
Exact evidence and remaining gates: [9 October installed acceptance](INSTALLED_CONNECTOR_ACCEPTANCE_2026-10-09.md).

Earlier installed staging runtime: **`3f02a9e`**, with 24/24 package hashes verified,
PersonalOnly, the current authoritative 12-row catalog and actual natural task
execution/cleanup. Exact installer, EXE, rollback and unresolved acceptance are in
[the 9 October reliability checkpoint](GMAIL_RELIABILITY_2026-10-09.md).
The 8 October details below remain historical evidence, not the current binary.

## Installed checkpoint on 8 October 2026

Built from clean commit `3ba1e14b4f479b3173343ba96f330d2f60eacc5d` and installed
silently into its distinct directory, **without launching** alongside the public GUI.
Installer exit 0. All 24 packaged files independently match installed hashes;
separate uninstall identity verified. Public EXE, startup and native-host registration
are unchanged. The running public app's existing paid session file changed during
this interval, consistent with normal token refresh but not independently attributed;
this is not a claim that every active-profile byte stayed frozen.
Root profile files were backed up before installation and no profile was deleted.

Protected installer archive:
`dist/connector-candidate-archives/3ba1e14-4dafe7d5/keyboard-wtf-connectors-staging-candidate-setup.exe`.
Installer SHA256 `4DAFE7D5D5931729F345B8AF57E07B39780E590522EF83726F59C43F1C073828`.
Installed EXE SHA256 `8DB458814BFAD79B283EBF0AC992313936C7440F2D05464FAE974B121F11DA4F`.
Signature status NotSigned; no public download/feed/tag changed.
The compiler reported non-commercial mode; commercial compiler licensing should
be confirmed before a subsequent public installer promotion, not asserted cleared.

The owner-authorized credential document was read only in memory. Existing Personal
validator confirmed text-model eligibility; the existing CurrentUser DPAPI vault
configured **only the new candidate profile as PersonalOnly**. One credential was
enabled; no rotation/pool redesign, production key mutation or paid balance transfer.
The new tooling required PowerShell reflection argument unwrapping; this did not
require any runtime product change and failed preparation did not overwrite a store.

Real Personal Live canary reused the actual transport, credential lease and exact
three broker declarations. Setup 960ms; correct Gmail `connector_resolve` proposal
1,453ms; one text turn, no executed connector tools, no microphone/player/Managed
requests. Early diagnostic-harness failures before input are retained; the corrected
harness accepts JSON in Text/Binary frames just as the existing production receiver
already does. **This is not installed microphone/popup or authenticated connector
E2E acceptance.** The probe deliberately discards audio/content and executes nothing.

The user closed the public app; the candidate is now the only running GUI and a
fresh staging Google desktop sign-in completed. Actual installed Settings confirms
PersonalOnly, signed-in credential ownership, the 12-entry authoritative catalog
and **one Google account shared across four connected capability rows**. Cold
`#connectors` navigation loaded automatically in 1,569ms without manual refresh.
The first script sampled the reset placeholder too early; this was a test-readiness
error, **not a cold-load product regression**. The revised installed check requires
actual catalog readiness rather than any nonempty status text.

## Account/navigation checkpoint

The candidate's Account & Billing shows `monthly_test` with 700 staging credits.
100% means **remaining**, not used. Those TEST records are not evidence that a
cancelled real subscription renewed. Existing at-cycle-end cancellation preserves
paid-cycle allowance; no billing formula, provider policy or production account was
changed. Real production cancellation state is not certified from this screenshot.

Manage Account correctly targets the separate staging website, but its account page
was missing from the deployment. The staging generator now includes a clearly
TEST-labelled derivative of the accepted renderer, existing staging Firebase/API,
local account navigation and no purchase controls. Original public account HTML/JS
remain unchanged. Existing cancellation/deletion/device operations still target only
the authenticated staging backend. No new backend checkout policy was introduced.
Actual hosted `/account` returns 200; signed-out auth controls/TEST notice verified,
zero page errors. Local fixture verifies signed-in rendering, zero purchase controls
even when payments are enabled in fixture data, light/dark and 320px layout.

A separate real Settings-navigation-source fixture proved sidebar `pushState`
emitted no `hashchange`, so visiting Connectors with a settled identity or revisiting
after a same-account server change could omit refresh. Smallest source fix: one
connector-link-only listener reuses existing guarded `panel.focus()` and is removed
on page destruction. No history override, extra polling, identity changes or core
Settings/router edits. Two fixture groups pass (exactly one lookup per entry/re-entry,
none after destruction). **This narrow source fix is not yet in installed 3ba1e14.**
94 focused candidate/.NET checks, 15 existing rendered functional checks, three
staging-account fixture groups and four hosted groups passed. Source secret scan
passed. No new model, Managed, purchase or connector content execution occurred in
this account/navigation verification.

## Exact package boundary

- Build: `pwsh -NoProfile -File scripts/build-windows-release.ps1
  -ConnectorStagingCandidate -SkipSiteHashUpdate` after a clean source commit.
- Installer: `dist/installer/keyboard-wtf-connectors-staging-candidate-setup.exe`.
- Install directory: `%LOCALAPPDATA%\Programs\keyboard.wtf Connectors Staging Candidate`.
- Profile: `%LOCALAPPDATA%\keyboard.wtf-connectors-staging-candidate`.
- AppId: `0F5D5DB4-9C8B-458F-8928-4F2275099417`.
- Backend: isolated `keyboard-wtf-connectors-staging` in `keyboard-wtf-staging`.
- Website: <https://keyboard-wtf-connectors-staging.vercel.app/settings/connectors>.
- Compiled configuration cannot be redirected by runtime environment settings.
- Candidate cannot alter normal startup/Helper registration or install public updates.
- Self-contained .NET runtime/native dependencies are packaged by the existing
  installer; no Python, Node, development folders or JSON editing required.
- Root LICENSE/NOTICES accompany bundled assets. Installer remains unsigned.
- Fresh staging authentication is required. Never copy production desktop sessions,
  subscription entitlements or paid balances into staging. Google sign-in using the
  same account as the accepted staging connector resolves the same staging UID.

## Safe installed verification order

1. Snapshot normal installed EXE/hash, startup/native-host registration and profile
   hashes. Protect source/package rollback and do not uninstall the public app.
2. Install candidate into its distinct directory without launching while the public
   GUI runs. Verify installed executable/package hashes and separate uninstall entry.
3. Stop Jarvis. Temporarily disable published Helper 2.1.28, leave private Helper off,
   and exit the normal app from its tray. Do not force-kill an active session.
4. Launch **keyboard.wtf Connectors Staging Candidate**. Confirm staging label,
   separate profile, actual active shortcut and Settings listener. Only one GUI runs.
5. Sign in through the existing desktop Google flow to the same keyboard.wtf staging
   account used for Google authorization. This is not another identity system.
6. Open Settings → Connectors. The accepted Google connection should appear only
   after authoritative provider reconciliation. Search locally, inspect account label
   and connected/available grouping, then open Manage all connectors.
7. Re-enable the SAME Store Helper 2.1.28 and refresh a harmless HTTPS tab only after
   the candidate's existing pipe is ready. No extension change/repackaging required.
8. Choose the existing Personal or Managed AI mode through Settings. Confirm legitimate
   credentials/entitlement in that isolated profile; no fake credit grants, production
   balances, purchases or forged Firebase sessions. Do not promise a Managed session
   when the staging account has no spendable test credits.

## Natural task checks

Start one session with the active shortcut shown in Settings. No special exact phrase
is required. Read requests are intentionally first; avoid private content in screenshots.

| Check | Example / acceptance |
| --- | --- |
| Mail search/read | “Check my Gmail for my two most recent unread emails and summarize them.” Only relevant connected account/capabilities; search then bounded identified message reads; grounded answer. |
| Calendar | “What is on my Google Calendar tomorrow?” Accurate explicit time interval and no writes/invitations. |
| Drive | “Find files in my Google Drive with keyboard in the name.” Bounded matching names/metadata; do not claim binary contents were read. |
| Popup | Friendly Searching Gmail / Reading email / Checking Calendar / Looking through Drive; no vendor/tool internals. Long work stays visible only while owned; success/failure/cancel clears activity. |
| Ordinary request | Ask a normal question and open a harmless local app. No connector lookup/provider calls for unrelated requests. |
| Cancel | Start a real connector read then explicitly cancel; late result cannot publish completion/actions or revive task ownership. |
| Confirmed write | Only after choosing a harmless concrete destination: explicitly create one disposable Calendar event without attendees, approve existing confirmation, independently verify one provider object. Never replay an uncertain write. Remove only that exact approved fixture through normal authorized means. |
| Restart | End with goodbye, restart the candidate, sign-in state and same UID-owned connections remain; no another-user or stale Connected display. |
| Account switch | Another actual keyboard.wtf account immediately clears prior connections; switching back reconciles original state. |
| Disconnect/reconnect | Choose the exact test connection, verify real provider revocation and UI update, then fresh human OAuth consent and independent ACTIVE/adoption. This removes access across that Google family. |

Record timings for Settings load, authoritative state lookup, task resolve, provider
execution and final popup publication separately. Provider-adapter timings already
measured (Gmail 590ms, Calendar 656ms, Drive 1,443ms) are not total spoken latency.
Correlate safe task/operation/logical-session metadata; no content or credential logs.
Server-verified linkage to physical Managed AI usage remains an explicit separate gate.

## Rollback

End candidate Jarvis normally, disable Helper temporarily, exit candidate, launch the
unchanged normal public app and then re-enable the same Store Helper. Registration
and public profile do not require restoration because candidate never owns them.
Do not delete profiles or install one build over another. Keep exact package/hash
receipts in ignored `.artifacts/connectors/` and source rollback checkpoints intact.

## Still required before public connector promotion

Installed tasks/popup acceptance; actual authenticated backend tool reads; one verified
confirmed write; real disconnect/reconnect; account switching/restart/device identity;
physical AI/connector accounting correlation; broader accepted-core regression;
Google sensitive/restricted-scope approval; representative additional-provider consent
and branding. Slack/Discord/Meet/Reddit/LinkedIn remain explicitly gated where their
configuration/distribution/branding is not verified. Never show unavailable services
as connected or publish the staging environment as the production account backend.

## Installed follow-up: c4144f9

The separate candidate was updated to clean source `c4144f9` and independently
matched all 24 packaged files. Installer/profile/prior package receipts are under
ignored `.artifacts/connectors/pc-installation/c4144f9-reviewed/`; the archived
installer is `dist/connector-candidate-archives/c4144f9-cf3b11d3/`.
Source rollback: `checkpoint/connectors-installed-c4144f9-20261008`.
Ten actual Settings checks passed (compact rows, four provider-verified Google
capabilities, signed-in PersonalOnly, no page errors). Installed text entry reached
real connector resolve/execute; received audio was queued and goodbye ended
normally. This is not physical microphone/heard-output/popup acceptance.
That answer exposed missing terminal desktop authority when the model omitted
its final complete tool. See `REAL_GMAIL_CHECKPOINT_2026-10-08.md` for the causal
test, narrow existing-answer-boundary fix and exact source/install proof tiers.
Stable public artifacts and Helper 2.1.28 remain unchanged.

## Installed owner-fix checkpoint: 8a4cccc

The desktop-only completion fix is now installed in the same isolated candidate.
Clean source/package: `8a4cccc6efdd6810131a4ddfc989613c3c13d1df`; rollback tag
`checkpoint/connectors-task-owner-20261009`. Protected prior package/profile remain
under the recorded c4144f9 archive and local recovery backup. All 24 installed
files match; all 23 non-executable assets and protected credential bytes are
unchanged. Installer/public registration checks passed.

Run `node scripts/connectors/test-installed-settings.mjs 8a4cccc-reviewed` to put
proof in that source-specific receipt directory; the actual run passed all ten
checks. Do not rerun into an existing accepted receipt directory without choosing
a fresh reviewed source tag. Real installed Gmail execution subsequently passed;
without a model completion call, the ordered answer ended local authority and
the authenticated backend accepted completion. After goodbye, a separate GET-only
product status verified completed + one verified operation. Normal arithmetic
invoked no connector tool; native voice state was idle after goodbye.

This is not physical microphone/heard-output/popup or all-provider OAuth acceptance.
See the Gmail checkpoint for exact timings, cold authentication caveat, receipts,
remaining public promotion gates and independent 1011 status. Stable public source,
installed EXE, startup/Helper registration, download/feed and website are untouched.
