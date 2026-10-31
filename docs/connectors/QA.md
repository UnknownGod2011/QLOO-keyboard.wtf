# Connector QA inventory

This is an isolated connector checkout. Test-double UI is not OAuth/provider proof.
No production website, account, installer or release has been changed by local UI
tests. A separate staging website/backend has been used. One real end-user
Google grant is ACTIVE/UID-bound/adopted and bounded provider reads succeeded.
Actual installed authenticated account/catalog synchronization is verified; these
do not alone certify Jarvis/backend tool execution, microphone/popup or write acceptance.
Current actual authenticated Gmail, installed execution/cleanup, exact-source gates
and unresolved complex-query/OAuth evidence are indexed in
[the 9 October checkpoint](GMAIL_RELIABILITY_2026-10-09.md). Preserve original failed
receipts; do not confuse source, emulator, product execution and physical output tiers.

## Rendered functional checks

Shared web/Settings panel: full catalog, official bundled icons, local case-insensitive
search, no-result search, connected grouping, multi-account labels, explicit default,
add account, reconnect, disconnect, Coming soon, loading, API failure, focus refresh,
UID reset, stale response suppression and active-attempt-only polling. Verify keyboard
focus/Enter, scroll, one click/one request and no HTML interpretation of server text.

Real website wrapper: branded navigation, sign-in/out, signed-out metadata, canonical
return, state removal from history, cross-account warning, callback replay and errors.
Local Settings: same embedded panel/assets, existing same-origin session guard,
default browser connection, account-change reset and open/focus refresh.

## Visual and layout checks

Desktop 1440×900 and narrow mobile 390×844 and 320×740; light/dark; expanded and
collapsed account rows; long identity wrapping; no horizontal overflow; thin list
scrollbar; + and check marks clearly aligned; no marketplace cards or provider IDs.
Use actual screenshots, not DOM styling alone. Reduced motion must remain usable.

## Security and lifecycle

Offline: UID/environment isolation, duplicate claims, cancellation, unknown write,
server capability/argument boundary, late OAuth/status/disconnect, existing permission
mode, session/account changes, no provider secrets/body logs and finite update leases.
Database-backed: concurrent claim transactions, deletion fence, tombstones, defaults,
minimal TTL metadata. Regression: existing selection, translation, audio, screen,
BA, workflows, Personal/Managed billing and system-instruction budget.

## Real staging gates (not replaced by fixtures)

Current custom OAuth config -> real provider consent -> independently ACTIVE ->
account bound to Firebase UID/environment -> read/search -> explicitly authorized
harmless write -> independent provider verification -> disconnect/reconnect.
Human consent/verification is separate from programmatic tests. Verify visible
authorization branding including intermediary hosts. Verify cleanup/TTL actually
deployed, revoked permissions, provider failure, app restart and cross-device sync.

## Tool choice

The requested browser-plugin setup is not available in this session. Use the installed
Playwright interactive runtime with real rendered pages, input and screenshots.
Browser mocks are clearly labeled development tooling and excluded from packaging.
The interactive kernel also failed to initialize twice (missing kernel asset path,
including after reset); the fallback is the same installed Playwright through a
reviewable CLI script with screenshots and browser error collection.

## Current reproducible gates / evidence

11 October intelligence continuation: `dotnet test
tests/KeyboardWtf.Tests/KeyboardWtf.Tests.csproj -c Release --no-restore
-p:KeyboardWtfConnectorStagingCandidate=true` passed **4,600/4,600**, no skips;
receipt `.artifacts/connectors/quality-tests/intelligence-final-candidate-desktop-v4.trx`.
Backend connector gate: **226 passed / 14 emulator skips**,
`intelligence-backend.trx`. Rendered owner/DPI fixtures and fresh quota-blocked
product evidence are indexed in
[the architecture review](INTELLIGENCE_ARCHITECTURE_REVIEW_2026-10-11.md).
Do not treat old proof/counts below as acceptance of the uninstalled continuation.

Subsequent typed initial-read recovery: `dotnet test
staging/backend/KeyboardWtf.Staging.Api.Tests/KeyboardWtf.Staging.Api.Tests.csproj
-c Release --no-restore --filter FullyQualifiedName~Connector`: **285 passed /
14 emulator skips** (`read-recovery-agent-final-all-connectors.trx`). The same
fresh assembly's complete non-emulator suite: **1,074 passed / 182 skips**, no
failures (`read-recovery-backend-wide-final.trx`). Focused recovery/adapter 97/97
passed (`read-recovery-agent-final-focused-v2.trx`). A fresh source broker test
made no HTTP/AI calls; Personal reasoning returned 429 before real mail execution.
These are not deployment, database-emulator, physical-popup or natural-quality
acceptance. Retain rejected receipts and all protected rollback/runtime artifacts.

- `dotnet test tests/KeyboardWtf.Tests/KeyboardWtf.Tests.csproj -c Release
  -p:KeyboardWtfPaidProduction=true`: integration checkpoint **4,116 passed**, no
  failures/skips (`desktop-public-connector-install-checkpoint.trx`). Earlier
  exact-source 4,103 (`connector-desktop-final-source-full.trx`) and 4,083 gates
  is retained. The intermediate `--no-build` run used an older Release test DLL
  before its expiry fixture update and failed that one fixture: 4,095/4,096.
  Timestamps independently established stale binaries; no product validation was
  weakened. Rebuilding Release from the correct source produced the green result.
- Connector staging candidate: **4,118 passed**, zero failed/skipped, same fresh
  Release binaries with `-- xUnit.MaxParallelThreads=2`
  (`desktop-connector-staging-controlled-scheduling.trx`). Preserve intermediate
  receipts: two new-flavor diagnostic-exclusion expectations corrected to assert
  absence; source legal packaging assertion scoped to the explicit public group;
  unchanged startup/contention and Vosk/cross-process timing failures in broad
  unconstrained runs. Startup isolated same-binary recheck passed. Test, ingress
  lease and startup implementation blob hashes match the accepted baseline; no
  product deadline or security assertion was weakened. Scheduling-sensitive
  failures remain documented, not reclassified as connector defects or erased.
  The controlled run limits concurrently scheduled collections only, leaving
  each test's explicit concurrency and original deadlines/assertions intact.
- Final public-flavor full run after the new packaging assertion: 4,116/4,117
  passed, one unchanged two-second startup-contention fixture failure, zero skips
  (`desktop-public-connector-final.trx`). Do not call this latest full run green.
  Earlier public integration full run was 4,116/4,116. The candidate controlled
  4,118/4,118 gate is green. Original reports and isolated rechecks are retained.
  Public connector promotion still requires bounded installed/core acceptance;
  a private staging installation is not a public-release certification.
- Final controller-only Debug/public-property gate: 40 passed. Do not describe
  it as the Release gate or substitute it for the rebuilt full result.
- `FIRESTORE_EMULATOR_HOST=127.0.0.1:8186` must be set for backend database tests.
  This uses an isolated emulator; it never uses hosted credentials/customer state.
  Latest connector subset: **87 passed** (`backend-connector-ui-install-verified.trx`).
  Initial 86/87 new fixture failure retained; legitimate persisted microsecond expiry
  now tested, with explicit rejection of expiry extension and unchanged product guards.
  Earlier connector subset: 78 passed. Original full report: 1,486/1,488 passed with two
  existing transaction-contention failures. Isolated unchanged-binary recheck:
  three passed. Preserve all TRX reports, not only successful rechecks.
  A further load-isolated full run passed 1,487/1,488; the existing Luma admission
  concurrency case reproduced seven SDK transaction aborts. This is not a green
  full-backend certificate. No Luma/billing code or tests were changed to hide it.
- `node scripts/connectors/preview.mjs` serves only the loopback review fixture
  at `http://127.0.0.1:4196/__connector-fixture`.
- `node scripts/connectors/test-ui.mjs`: 15 rendered checks, zero console errors.
  Screenshots and complete final proof: `.artifacts/connectors/ui/`.
- `node scripts/connectors/test-staging.mjs`: 19 hosted unauthenticated checks.
  `.artifacts/connectors/hosted/unauthenticated.json`; authenticated consent and
  provider actions are deliberately not inferred from this result.
- Protocol/branding probe requires the authorized external credential brief,
  reads it only in memory and creates/removes fresh synthetic staging INITIATED
  accounts: `scripts/connectors/probe-provider.ps1`. Receipts contain no keys,
  tokens or full authorization URLs.
- Staging deployment is restricted by `deploy-staging.ps1` and
  `deploy-web-staging.mjs` to the separate connector project/service. No public
  app installer or production traffic promotion is implied by `--deploy`.
- `configure-staging-cleanup.ps1` defaults to plan. Apply/Verify bind only the
  staging connector image, pinned staging secrets, six connector TTL fields,
  required group index and a dedicated quarter-hour job. Existing paid jobs are
  not changed. Job execution and eventual TTL deletion require separate proof.
  Repeat Apply on 8 October repinned the job to revision 00004-g2p's image;
  all six TTL fields remain ACTIVE, index READY and scheduler ENABLED.
  The installed gcloud update command requires `--update-headers` rather than
  create's `--headers`; tooling now chooses the correct flag. Initial failed
  update did not remove the existing scheduler. Final Apply independently passed.
- `probe-staging-cleanup.ps1` tests actual job cleanup of one fresh, expired,
  unconsented provider account with a synthetic Firestore owner (not a Firebase
  user/token). It independently checks absence and terminal journal, then removes
  only exact fixtures. This tests operational cleanup, not human OAuth or TTL delay.
- Verified hosted cleanup: job completed, provider absent, terminal journal and
  exact fixture removal. `.artifacts/connectors/hosted/cleanup-execution.json`.
  Active six TTL policies, READY group index and enabled quarter-hour scheduler:
  `cleanup-configuration.json`. Eventual TTL deletion delay is not asserted.
- `dotnet test ... --filter FullyQualifiedName~ConnectorSettingsHttpTests`:
  13 real loopback HTTP checks passed without application startup/profile/audio.
  `connector-settings-http.trx` in the desktop evidence directory.
- `node scripts/connectors/measure-staging.mjs`: actual signed-out hosted catalog,
  navigation and local-search timings, zero AI/purchase requests. Separate
  `performance.json` labels unmeasured authenticated/provider/runtime stages.
- Complete two-project Slack/Discord discovery and exact manual configuration:
  [STAGING_AUTH_CONFIGURATION.md](STAGING_AUTH_CONFIGURATION.md). No raw secret
  recovery, production-ID reuse or silent managed-auth fallback is claimed.
- `node scripts/connectors/test-theme.mjs`: actual website wrapper/CSS plus
  native Settings CSS with explicit auth fixtures. Four surface/theme groups,
  readable text >=4.5:1, rest/hover/active/disabled buttons, 320px no overflow,
  screenshots under ignored `output/playwright/connectors-theme/`. Dark Settings
  is a token-contract simulation; native Settings still ships its existing theme.
- `node scripts/connectors/test-theme.mjs --hosted`: four hosted check groups,
  zero page errors, actual signed-out staging light/dark/mobile and existing
  desktop-login and labelled TEST account page/module (account route 200, no buy
  controls). It deliberately does not click sign-in or generate
  a Firebase credential. Screenshots: `output/playwright/connectors-hosted-theme/`.
- `scripts/connectors/probe-connected-google.ps1 -Read`: independently verifies
  exactly one accepted staging Google connection's environment/UID/config/toolkit,
  then bounded Gmail/Calendar/Drive GETs. Source obtains only the pinned staging
  key from Secret Manager in memory. No tokens/content/raw responses persist.
  `accepted-google-read-before-api-enable.json` retains initial 403 proof;
  `accepted-google-diagnostic.json` records SERVICE_DISABLED;
  `accepted-google-read.json` records three successful reads and timings.
  Provider request IDs are safe operational references. No AI/credit/purchase/write.
  Inventory saturation (51 global documents) now fails closed before selecting an
  account or calling content APIs; the script must never infer uniqueness from a
  truncated sample. Use a paginated, explicitly scoped probe for larger inventories.
- `prepare-personal-test-profile.ps1` reads the owner-authorized credential Word
  document only in memory; it never renders/prints secrets, passes values in argv,
  logs provider responses or overwrites an existing vault. It invokes the real
  Personal metadata validator and, only with Configure, the existing per-profile
  CurrentUser DPAPI vault. On 8 October an eligible text-model credential was
  confirmed after three validation checks, with zero generation/Managed requests.
  Metadata validation alone does not certify generation quota or Live capability.
  Key formats are not inferred from a particular prefix. Diagnostic phases/types
  contain no exception messages. No production credential/profile mutation.
- Candidate installed from `3ba1e14`: exit 0, 24/24 installed file hashes match,
  separate uninstall identity, normal EXE/startup/native-host unchanged. User closed
  normal app; candidate-only GUI launched and fresh staging Google sign-in completed.
  Actual installed catalog12/four shared Google capability rows and PersonalOnly
  verified without manual refresh. No physical task/popup acceptance yet.
  Receipts: `.artifacts/connectors/pc-installation/`.
  The normal running app's paid-session file changed during the interval; do not
  claim all active-profile bytes unchanged. No production token/balance was copied.
- `probe-personal-live-planning.ps1 -AssemblyPath ...`: actual existing Personal
  adapter/vault, three exact connector brokers, one text turn, no executed tools.
  Setup **960ms**, correct Gmail resolve proposal **1,453ms**. No mic/player,
  Managed client/credits, retries within the corrected invocation, or content logs.
  Preserve earlier pre-input harness receipts; initial PowerShell reflection/frame
  assumptions were diagnostic-tool errors, not product failures. Production already
  decodes JSON from Text/Binary frames; no audio/Live implementation was changed.
  This proves Live setup/planner proposal only, not authenticated backend/native UI,
  full microphone conversation, real mail read/write, or end-to-end spoken latency.
- `node scripts/connectors/test-installed-settings.mjs`: real loopback candidate,
  signed-in credential identity, PersonalOnly, active Q shortcut, catalog12/four
  connected capability rows; automatic readiness at 1,569ms. Early sampling of the
  reset placeholder was reclassified as a test bug, not a product cold-load failure.
  Revised script requires actual automatic catalog readiness. No clipboard, model,
  connector content or account actions. Does not replace microphone/task acceptance.
- `node scripts/connectors/test-settings-navigation.mjs`: existing native Settings
  scroll-spy source with actual connector module, isolated APIs. Before-fix sidebar
  entry failed; the connector-link-only listener passes two groups, exactly one
  read per entry/re-entry, same-owner disconnect reconciliation and no work after
  destruction. Not installed proof; the source-only patch still needs packaging.
- `node scripts/connectors/test-staging-account-ui.mjs`: generated TEST account
  derivative with explicit fixture. Three groups; existing signed-in renderer,
  accurate remaining semantics, no purchase controls even with paymentsEnabled,
  no missing enhancement dependencies, light/dark and 320px no overflow. Original
  production account renderer and all billing backend policies unchanged.
- Candidate focused boundary gate: **94/94**, no skips,
  `connector-pc-navigation-boundary.trx`. Existing broader suite receipts and prior
  scheduling-sensitive failures are retained; this is not a new full-suite claim.

Runner scheduling reference: [official xUnit RunSettings](https://xunit.net/docs/config-runsettings).

Generated evidence is ignored and excluded from source/package. Reusable scripts,
tests and sanitized documentation are retained. Credentials remain external.

## Latest complex-read gate (9 October)

- Runtime `30f4db2`: 213 focused and 4,279 full candidate tests, zero skips or
  failures. Full receipt `connector-complex-read-full-30f4db2-20261009.trx` in
  `.artifacts/connectors/tests/gmail-real-read/`.
- Reproduce the source gate with `dotnet test
  tests/KeyboardWtf.Tests/KeyboardWtf.Tests.csproj -c Release
  -p:KeyboardWtfConnectorStagingCandidate=true --no-restore`; use a unique TRX name
  and results directory. Never rebuild a loaded diagnostic assembly mid-probe.
- Real natural combined requests include successes, failures and variable
  latency. See [current checkpoint](CONNECTOR_COMPLEX_READ_CHECKPOINT_2026-10-09.md)
  for exact source/backend/install distinctions. No private content in receipts;
  no direct provider diagnostic is counted as product acceptance.
- The diagnostic's timing-only publication events do not modify production
  audio or reduce content. Text-driven Live tests are not physical playback,
  microphone, installed popup or all-provider release proof.
- Keep the self-email fence: there is already one independently verified copy.
  Do not run the `SelfSend` diagnostic again.
