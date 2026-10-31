# keyboard.wtf — master new-chat context and audit-first prompt

Prepared 11 October 2026, approximately 15:26 UTC / 20:56 IST. This is a handoff of an existing project, not a new project specification. It deliberately distinguishes current source, installed packages, real-provider proof and public production. Read the whole document before acting.

## 1. Instruction from the owner to the new Codex chat

You are taking over my existing keyboard.wtf project after a very long development/recovery/release/connector thread. **Do not restart the project or the connector architecture. Do not immediately implement anything.**

Your first assignment is a deep, **read-only** architecture/history/runtime audit. Understand the actual source and its owners, deployment boundaries, versions, installed applications, financial logic, task lifecycle, evidence and remaining work. Then give me a coherent reconciliation and wait. **I will give you the next implementation directions after you understand the project.** Old “continue autonomously” messages do not override this initial audit-only instruction.

Do not treat the text below as permission to build, install, start Jarvis, change settings/startup, switch modes, deploy, publish, alter OAuth, buy anything, send an email, create a document, resolve financial holds or clear fences. During this initial phase, ordinary file/Git/process/startup reads and bounded anonymous availability checks are appropriate. Do not run billable/provider actions or experimental production scripts merely to learn the architecture.

This is an established working product with fragile, deliberately tested ownership boundaries. Misunderstanding them could break working audio, selection, saved actions, billing or customers. Analyze deeply before proposing changes. If evidence contradicts this handoff, report the discrepancy, do not “repair” it by guessing.

### Non-negotiable working rules

- Preserve all accepted checkpoints, original receipts, public installer bytes, private profiles and rollback archives. Never overwrite a released asset with different bytes under the same version.
- Never reset a dirty checkout, cherry-pick a rejected recovery branch, copy an old profile wholesale, or delete another installation because its version looks older.
- Prefer the smallest causal fix in an existing owner. No global prompt inflation, broad audio rewrite, parallel confirmation system, new permanent memory store or framework migration without measured need.
- Separate connector failures, AI quota, Live transport failures, billing guards and diagnostic-harness failures. They are not interchangeable.
- PASS requires the stated evidence tier. Compiling, a green unit test, a mock UI, a direct provider call and a human-observed installed result are different kinds of proof.
- Keep progress concise. Do not ask me to repeat an already completed installation/approval/test simply because the chat changed. Ask only for a genuinely new external/manual decision after checking the records.
- Secret values must never appear in this prompt, tracked files, frontend/desktop assets, installers, logs, screenshots or reports. Do not ask for end-user access/refresh tokens.
- Prefer `rg`/`git ls-files`, exclude build/dependency/archive trees from broad searches, and use `apply_patch` for subsequent authorized file edits. Do not repurpose HOME/CODEX_HOME variables.
- This handoff is internal. Private email/document content belongs in an authorized answer to me, not in tracked development documentation.

## 2. Immediate executive reconciliation

There are **three different current realities**:

| Layer | Current identity | Meaning |
| --- | --- | --- |
| Public product | Normal keyboard.wtf **1.4.21** | Website, Windows download, public update feed and subscription catalog are live. Connectors have not been promoted into this public release. |
| Installed private connector candidate | Desktop source **7e9aebc**, also version **1.4.21** | Separate Connectors Staging Candidate application/profile. Installed and testable, but it does **not** contain the latest 11 October quality/context/UI source. |
| Latest connector source | **28ed1caae7ad99db509a301a2babfa49fefdd5a1** | Newer source-only checkpoint. Offline gates and some real no-AI product-path checks passed; repeated natural AI quality and matched private installation remain open. Not publicly released. |

**Connector platform: NOT READY for all-provider/public acceptance.** It is far beyond scaffolding: authenticated backend/provider abstraction, real account connections, desktop/web UI, task ownership, scoped capabilities, real reads, existing one-write evidence, pagination, activity, cancellation and new quality improvements exist. But “excellent reliable human requests across connectors” is not yet demonstrated repeatedly.

The main current external gate is a working **Personal structured-reasoning API quota**. Latest controlled reader attempts returned HTTP 429 before Gmail execution. Native connector reads still work. Do not silently fall back to Managed credits, rotate a credential mid-session, or call this disconnected Gmail.

Independent Personal Live **1011 / occasional speech cutoff remains open**. It predates connectors. A different Managed minute-scale reconnect cause was fixed in production. Do not conflate them.

The new contextual mute / Smart Writing tick UI feature was requested but is **deferred until connector quality acceptance**, not implemented in this checkpoint. Do not switch to it during the initial audit.

## 3. Exact workspace/Git map — inspect before editing later

Desktop root: `C:\Users\Admin\OneDrive\Desktop`.

| Checkout | Branch at handoff | HEAD at handoff | Notes |
| --- | --- | --- | --- |
| `keyboard.wtf-connectors` | `codex/connectors-foundation` | `28ed1caae7ad99db509a301a2babfa49fefdd5a1` | **Authoritative connector worktree.** Clean before these handoff documents were added. |
| `keyboard.wtf` | `codex/browser-automation-bakeoff` | `877cf745446668f8b3d7d81c77b596a881550aa1` | Ambient cwd of the old thread. Not the connector edit target. Untracked tooling/output/media directories exist; do not clean them. Remote is the GitHub repository. |
| `keyboard.wtf-public-release` | `codex/public-release-preparation` | `0aeba6633ebf516109b9f1fa985be6f6caf48ba1` | **86 modified/untracked status entries** at handoff. Historical approved release work and operator artifacts; not a clean representation of released bytes. Preserve everything. |
| `keyboard.wtf-personal-api` | `feature/personal-api-backend` | `b1f5b9aaeb4f28cceed111dbf28274d0855a6ec2` | Historical private recovery/API source; 16 dirty/untracked entries. Valuable architecture/forensic docs, not current connector authority. |
| `keyboard.wtf-paid-staging` | `paid-staging` | `74a3a93bddc5fa4cc5026cdc793ac76b9e22978a` | Historical paid recovery, seven dirty/untracked entries. |
| `keyboard.wtf-final-core` | `recovery/rejected-83e4b05-core-routing-regression` | `83e4b050253818ac9fbbadb19d1fd56a77f6d0d7` | **Rejected recovery reference**, not a safe starting point. One dirty/untracked entry. |
| `keyboard.wtf-3d8-scope` | `recovery/rejected-90c2e82-core-regressions` | `dabc950aae995cd39ef649285eb217e3834cafe0` | Rejected/experimental recovery lineage; do not promote because clean. |
| `keyboard.wtf.2` | `main` | `dd1a94c3a7120e39d8aa9d3decd8003d6107d462` | Older separate checkout, not authority. |

Other historical directories exist: `keyboard.wtf-local-snapshots`, `keyboard.wtf-snapshots`, `keyboard.wtf-pre-p49-baseline`, `keyboard.wtf-pre-p49-testclone`, `keyboard.wtf-stable-reference-20260726`, `keyboard.wtf-browser-automation-snapshot-20260726-155046`, and private Git backup/verification directories. Treat these as protected references until their provenance is understood.

### Critical linked-worktree detail

The connector worktree is linked to a **protected clean public export inside the public-release verification directory**, not to the ambient root checkout:

`C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-public-release\dist\verification\public-release\launch\public-1421-release-child-b612a64cc8e640e9816e5d1423400753`

That checkout is on `codex/public-1.4.21-release` at `987e4d4`. The connector worktree's common Git directory is under that export's `.git`. Its `origin` is a local protected export:

`C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-public-release\dist\verification\public-release\launch\public-1421-770e4c2464de4a569494e41a5f9fd10b`

**Do not delete these directories as temporary dist clutter. Do not assume `origin` is GitHub or push casually.** `codex/public-1.4.21-release` is not a local ref in the separate dirty public-release checkout; querying it there fails. Run Git commands in the right repository. Use `git rev-parse --show-toplevel`, `--git-common-dir`, `git worktree list`, `git remote -v`, status and tags before choosing a target.

## 4. Public release/source/rollback identity

| Item | Accepted identity |
| --- | --- |
| Canonical site | `https://keyboardwtf.com/` — note: domain is **keyboardwtf.com**, product is **keyboard.wtf** |
| Windows release | `https://github.com/UnknownGod2011/keyboard.wtf/releases/tag/v1.4.21` |
| Windows installer | `https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.21/keyboard-wtf-setup.exe` |
| Stable feed | `https://keyboardwtf.com/releases/stable.json` |
| Installer build/tag source | `07a57667055a1c47c7a1990ec0d45d7f20a0cd48` |
| Reviewed release/backend/site export | `987e4d4eaef6130f29f0818a3bc1a7a42c88f969`, branch `codex/public-1.4.21-release` |
| Installer bytes | **89,451,689** |
| Installer SHA-256 | `45C2E2D08444DE1A9293BB7FE116C665697E1054C3D236A9F6616204A0B3D412` |
| Installed normal EXE SHA-256 | `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5` |
| Immutable installer archive | Public-release checkout: `dist/public-release-archives/1.4.21-07a5766-45c2e2d0/` |
| Last documented serving API revision | `keyboard-wtf-paid-api-nospendceilings-20261007`, 100% default traffic |
| Last documented serving API image | `sha256:ee1f32464072c6ff9eb546bb983785de58e3d6ddd93c1f933137c6270d1e88ba` |
| Production backend base | `https://keyboard-wtf-paid-api-mhrc6c7goa-el.a.run.app` |
| Last documented website deployment | `dpl_uwabS86kSdPxF3RwMc6ToqWEZ8AL` |
| Website rollback | `dpl_DMSjeF3uQwj1A9BH5RMwheYLFgC2` — inspect feed/download before restoring aliases |
| Compatible reviewed backend rollback image | `sha256:6f3816cffccf4a856f3fa83ef28cc4244accccc5e9d8758238f1b29cb4c44c04` — retain financial/deletion fence enforcement |

The two source SHAs intentionally differ. Backend/site policy publication did not rebuild the frozen desktop installer. Do not label `987e4d4`, `0aeba66` or the current connector HEAD as the installer build source.

**Fresh read-only checks during this handoff, 11 October:** canonical home returned HTTP 200 and contains Luma/Windows download; GitHub v1.4.21 is neither draft nor prerelease, installer asset digest/size matches the table; public stable feed says 1.4.21 and the same SHA-256; anonymous `/v1/catalog` reports `killSwitch=false`, `paymentsKillSwitch=false`, one monthly plan at **99900 INR minor units / 500000000 credit micros** = ₹999 / 500 credits. These checks are not a new checkout/payment/provider/runtime test. Cloud deployment identities in the table are documentary 8 October evidence, not fresh cloud revision GETs performed for this handoff.

Earlier docs say “Coming soon,” checkout held, no connectors started, or accounting windows still future. Their **later dated addenda and final receipts supersede those statements**. Do not re-disable checkout to make an old paragraph true.

## 5. Installed applications and runtime authority

Fresh physical EXE/hash/version/process readback during this handoff:

| Installed directory under `%LOCALAPPDATA%\Programs` | Version | EXE SHA-256 |
| --- | --- | --- |
| `keyboard.wtf` | 1.4.21.0 | `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5` |
| `keyboard.wtf Connectors Staging Candidate` | 1.4.21.0 | `7F7300306C4ADC4A42A7C5CE6174AB073D5BFBC46E9081DC886999F0BE13A4B0` |
| `keyboard.wtf Personal API Candidate` | 1.4.19.0 | `EC66CD299C8A7A93EC75FFD343DE13BD4583F53B14858D34C224EAB3AD7DAA92` |
| `keyboard.wtf Paid Production Candidate` | 1.4.19.0 | `4828A2B6F4D55C4CB4BC8537424143B30D8D0662859C8B11CE0C1A79A27DF95C` |

Only one keyboard.wtf GUI was running: the **Connectors Staging Candidate** at the exact installation path above, PID 25164 at observation. PID is temporary; re-enumerate rather than relying on it. A running GUI is not evidence that Jarvis Live/microphone capture is active. Diagnostic Live sessions were ended in the recorded connector passes.

Startup inspection found only the **normal public app** entry. Earlier on 11 October, an obsolete Personal API Candidate startup value was backed up and removed after the user's duplicate-start report. No installation was deleted. Connector candidate intentionally has no startup registration. Do not confuse legitimate native-messaging child/native-host mode with a duplicate GUI; inspect full path, flags and parent process.

Profiles are compile-flavor isolated via `SettingsService`, including:

- Connector: `%LOCALAPPDATA%\keyboard.wtf-connectors-staging-candidate`.
- Personal candidate: `%LOCALAPPDATA%\keyboard.wtf-personal-api-candidate`.
- Paid candidate: `%LOCALAPPDATA%\keyboard.wtf-paid-production-candidate`.
- Normal: existing normal data-directory resolver/default; inspect it and any authorized override rather than assuming candidate data is shared.

DPAPI credentials and session stores must remain with their intended profile/Windows owner. An earlier diagnostic launcher inherited a Codex shadow profile and stale credentials; relaunching against the physical candidate profile corrected the **harness** issue. That was not provider OAuth failure. Never print profile credential/session files to diagnose.

Last separately verified audio condition was microphone muted/output volume 2 across default roles. This handoff did not change or freshly remeasure audio devices. Future diagnostics should preserve the owner's condition, close sessions when no longer needed, and never leave paid/private Jarvis running unattended.

Installed connector package source is `7e9aebcdab5c29b4d6108a4065a9909410d90ed7`:

- Archive: `dist/connector-candidate-archives/7e9aebc-reviewed/` in connector worktree.
- Installer: `keyboard-wtf-connectors-staging-candidate-setup.exe`.
- Installer SHA-256: `4C107E678D082202FE87C189F77369D6F305232E75415613A9A22EC111738901`.
- 24 payload files were independently verified; only EXE changed from prior reviewed payload.
- Previous `7a1f6f7-reviewed` complete rollback archive remains. Its installer SHA-256 is `5195427367620D34627BCE783462E87FF81823BF5FE5353DEB99818B319BC91D`.
- Historical package docs first say 7e9aebc packaged/not installed; **current status and fresh installed hash supersede that earlier line**.

Same version number does not imply same binary, source, profile, endpoint or release status. Always verify hashes and compile flavor.

## 6. Project architecture — actual technology and owners

### Desktop shell and local Settings

- **.NET 8 Windows WinForms**, not WPF/WinUI, targeting `net8.0-windows`, win-x64 (`src/KeyboardWtf.csproj`). Do not adopt another desktop UI framework.
- `src/Program.cs`: entry mode, single-instance/mutex, tray/native-host/deep-link entry; inspect before interpreting processes.
- `src/Services/KeyboardWtfApp.cs`: app orchestration and service wiring, hotkeys and lifecycle integration.
- `src/Models/KeyboardWtfState.cs`: app/voice/long-task state and operation-ID-owned UI.
- `src/VoiceOverlayForm.cs`: actual native Jarvis popup/orb/status/provider rendering. It is not a React component.
- `src/Services/SettingsService.cs`: existing settings persistence, build-flavor data roots, defaults and permissions.
- `src/Services/WebSettingsService.cs` and partials: embedded HTML/JS Settings served on loopback; host/origin/action-token checks, authenticated operations and finite admission remain important. Normal Settings often uses `http://localhost:8090/`, with existing fallback port behavior.
- `src/Resources/web/settings.html`: existing Settings UI. `SettingsSectionRegistry` owns navigation aliases. Do not loosen loopback CSRF/host rules or invent a friendly local hostname requiring HTTP.sys changes.
- `src/Services/WindowsStartupService.cs`: startup registration; candidate compile flags isolate names. Do not “fix” duplicates by uninstalling everything.

### Jarvis speech and shared action execution

- `src/Services/GeminiLiveConversationService.cs`: Live session understanding, fixed normalized tool catalog, transport/session lifecycle and tool handoff.
- `src/Services/LiveToolCallDispatcher.cs`: owned async tool execution, physical/logical session receipts, deduplication, interruptions/cancellation. Inspect real callback/lifecycle ordering.
- `src/Services/CommandRegistry.cs`: **shared Personal/Managed product action boundary**. A mode switch must not bypass the already tested selection, translation, screen, saved-action or connector executor.
- `src/Services/VoiceCaptureService.cs` and existing playback owners: working mic/VAD/STT/audio flow. Do not create another audio pipeline or broadly change capture/playback for connector work.
- `src/Services/JarvisLifecycleAnnouncement.cs`: owned background/long-task result announcements. Late/stale owners cannot announce or restart a completed task.
- Existing saved mappings, Links, workflows, intent memory and native app/file actions have their own authority. A model proposal is not authority to invent a destination or choose a different saved identity.

### AI ownership and transport

Read these together, not independently:

- `src/Services/Ai/Backends/AiBackendContracts.cs`.
- `AiBackendRouter.cs` and `AiBackendGateway.cs` in the same folder.
- `PersonalApiVault.cs`, `PersonalApiSettingsController.cs`, native secure key dialog.
- `PersonalGeminiHttpTransport.cs`, `PersonalGeminiLiveTransport.cs`.
- `src/Services/Ai/PaidCloudAiProvider.cs`.
- `src/Services/PaidStaging/PaidAccountService.cs` and `.AiBackend.cs`.
- `PaidSessionStore.cs`, `src/Helpers/SecureStore.cs`.

A logical operation/session freezes credential/backend ownership. Child reasoning must inherit that owner. **PersonalOnly is fail-closed**: no eligible key means no Managed delegate/reservation. A policy that allows fallback still has narrowly typed pre-dispatch rules; it is not permission to switch billing/keys in the middle of a session or replay already accepted actions.

Current Personal source constants: Live `gemini-3.1-flash-live-preview`; ordinary text `gemini-3.5-flash-lite`; reasoning `gemini-3.5-flash`. The connector read worker uses the existing reasoning capability, not a new credential/model owner. Managed routes are server-owned in `ModelRoutingService` plus deployed route documents. Do not infer active production models from a legacy fallback array or README.

Personal and Managed **share action semantics/desktop execution** but necessarily differ in transport/credential/metering boundaries: Personal direct key-owned Gemini; Managed authenticated Cloud Run/Vertex relay and financial reservations. “Exact same product path” does not mean deleting that necessary boundary.

### Browser Automation versus simple URL opening

`src/Services/BrowserAutomationV2/` owns coordinator, planner, safety/verifier, Helper bridge/native host and tab authority. Real DOM/click/fill/selected-text automation remains supported through its genuine Helper path; do not fake generic Edge automation or weaken tab ownership.

Simple saved website URLs are not inherently DOM automation. The accepted launch-boundary fix uses the existing path and safe Windows default-handler fallback when Chrome Helper has no window. The user tested Chrome available/closed and default Chrome opening from Edge, and accepted that generic fallback rather than brittle active-browser hacks. **Do not re-open that accepted enhancement or rewrite saved workflow format.** A background connected Chrome Helper is not itself a bug; BA can work in background. Respect current requested-action semantics.

### Selection, translation, screen and Smart Writing

- `SelectedTextTranslationOperation.cs` owns a translation request; `SessionSelectionHistory.cs` owns bounded temporary source/result continuity. Clipboard capture must be current, full-or-fail within supported bounds, not stale arbitrary clipboard text.
- Selected-text analysis must not become screenshot analysis merely because the word “analyze” appears. Tool choice must be reconciled to the authoritative human source/action.
- Translation should start promptly, show appropriate dynamic translating progress, and speak a concise completion. Preserve full-document output and existing clipboard ownership.
- `GeminiScreenGuidanceService.cs` owns screen capture and session snapshot (`_sessionSnapshot`); follow-ups can reason over S1, explicit “screen now/current” captures S2. Clear temporary image/context at session end; no permanent screenshot memory or unbounded images.
- Smart Writing uses existing dictation/pause/final editor lifecycle (`SmartWritingFinalEditor` and app orchestration). Its auto-polish threshold and single-operation ownership remain protected.

### Backend and website

- Backend lives at `staging/backend/KeyboardWtf.Staging.Api/` despite the directory name also containing production-capable code. **Environment/configuration/traffic determine staging versus production, not the word staging in the path.**
- ASP.NET Core Minimal APIs/DI: `Program.cs`, `Endpoints/ApiEndpoints.cs`, Firebase identity service, Firestore store and domain services. Connector and Luma endpoints are separate registrations.
- `production/site/`: existing website HTML/JS/CSS, Firebase authentication, account/pricing/download/update feed, Luma, theme and connector panel. Vercel-hosted public/staging deployments are separate.
- `production/site/app.js` and `config.js` centralize client auth/API configuration. Public Firebase client configuration is not a server OAuth/AI/payment secret; client access rules still must be strict.
- Installer: `installer/keyboard-wtf.iss`; release/provisioning scripts under `scripts/release/`; production audit/deploy operators in protected/ignored verification directories.

## 7. Billing/authentication — understand before touching anything

### Identity

Firebase UID authenticated on the server is authoritative. Never accept a client UID/email as ownership. Backend/account sessions, connectors, purchases, tasks and deletion checks must bind to this UID. Email is display/provider account metadata, not the primary key.

It is legitimate for a keyboard.wtf account signed in as one Google email to connect Gmail/Drive/GitHub belonging to another email. **The keyboard.wtf Firebase UID owns that connection**, and the provider account labels identify which mailbox/service will be used. This does not transfer someone else's keyboard.wtf account connections. Account switching must clear/refresh UI/session/task state immediately.

The candidate “100% remaining / 700 usable / monthly staging plan” screenshot was a **synthetic staging account/plan**, not proof that a cancelled production subscription renewed or that billing is free. “100% usage remaining” means unused allowance, not 100% consumed. Cancelling renewal normally retains paid access/credits for the current period; it is distinct from account deletion. Candidate Manage Account points to staging by design.

### Paid AI flow and financial invariants

Read `AiUsageService`, `FirestoreAccountStore` partials, `ModelRoutingService`, `VertexAiClient`, `VertexLiveProxyService`, `ProviderUsageMetadata`, `UsageReconciliationService`, `LedgerInvariantService`, `PaymentReconciliationService`, `ProviderCostReconciliationService`, `SubscriptionManagementService`, `RazorpayPaymentProvider`, account-deletion services and maintenance runners.

Managed AI must follow existing authentication → entitlement/route → reservation → dispatch → authoritative receipt → settlement/release → ledger/history. Reserve is not an upfront final debit; pending unknown usage is not zero usage. Avoid double charging or falsely releasing unknown exposure. AI planning adds model usage only when an actual model call occurs. **A connector provider/tool execution is not a Gemini invocation and does not itself consume AI model credits.**

₹999 monthly plan supplies 500 provider-cost-backed credits. Preserve prices, unit conversions, allowance/purchase/renewal/refund semantics and actual-usage math unless separately authorized. Do not change model tariffs or spending limits to make a test fit.

The former minute-scale Managed reconnect had a proven **terminal text-coverage guard overestimate** plus artificial spending ceilings, not proven connector/audio failure. Production correction preserves truthful usage settlement while disabling ordinary daily spending caps and making per-connection monetary ceiling effectively nonrestrictive. Small one-credit rolling reservations, available balance, entitlement, ownership, concurrency and financial security still apply.

Documented deployed policy:

- `PaidSystem__DailySpendingCapsEnabled=false`.
- `LiveSessionCreditMicrosCap=1000000000000000`, matching the live route's safe monetary ceiling.
- Managed connection lifetime **720 seconds**; existing desktop quiet-turn rollover approximately **9.5 minutes**. Personal uses client rollover/direct transport and no Managed spend caps.
- One no-action canary stayed on one socket beyond the former cutoff, question after 125 seconds, first audio about **1.056s**, normal close, **0.03 credits** actual debit, remainder released/counters zero.

This does not guarantee zero network/provider interruption, uninterrupted ten-minute physical speech, or unlimited/free usage. Removing ordinary spend caps did **not** remove unknown-usage review/security policy.

### Finite accounting follow-up — closed, remaining operator gate real

Read the final sections of these **ignored public-release files**:

- `dist/verification/public-release/launch/managed-live-loss-clock-followup-20261008.md`.
- `dist/verification/public-release/launch/managed-live-loss-operator-review-plan-20261008.md`.

Both natural maintenance windows were independently verified GET-only:

1. 8 October **17:45 IST** restored the approved **1.2175-credit** physical hold with no customer usage charge.
2. 8 October **23:45 IST** restored one additional **10-credit** hold. Combined **11.2175 credits**, immutable decisions/idempotency/distinct release ledgers and complete wallet deltas matched.

Final exact wallet observation: **95.11 available / 44.45 reserved** credits. Five enrolled physical holds totaling **41.64**, plus two legacy holds totaling **2.81**, remained. This is a dated operator receipt, not an assurance of today's wallet values.

The expected durable review fence was set at **2026-10-08 18:15:28.974284 UTC** / 23:45:28.974284 IST, matching the second immutable decision. Active request counters zero. This was policy-expected review, **not fabricated usage charges or proven financial corruption**.

The in-thread `verify-remaining-keyboard-wtf-launch-gates` automation was paused then deleted after completion. Two connector one-off continuation automations were also documented deleted after their finite runs. Handoff inspection of the local automation directory found no `automation.toml` jobs there; do not treat this as a full inventory of remote/cloud schedules. **The production cloud maintenance schedule remains enabled and unchanged. Do not delete it because an in-thread follow-up ended.**

Remaining operator action is reviewed causal/exposure assessment. If justified, unlock needs separately scoped **exact-fence compare-and-set with immutable operator audit**; no reviewed general fence-clear method exists. Never reset counters, edit wallets/ledger/reservation timestamps, force maintenance or invent final provider usage. Preserve accepted and rejected verifier receipts. JSON ordering/non-atomic historical snapshots/PowerShell timestamp conversion errors were harness faults, not incidents.

### Account deletion

Owner requirement: deletion cancels future renewals and must complete through authenticated ownership, actual provider subscription cancellation/reconciliation, credential/token invalidation and durable safe deletion. Never test by deleting the paid real account. A separately approved disposable account deletion was confirmed and stale token rejected 401; independent backend checks are distinct evidence. Preserve protection against late payment/webhook writers resurrecting a deleted account. Current public account/Google/provider configuration must not be replaced with a second auth system.

## 8. History before connectors — why the conservative boundaries exist

This is an operational summary, not every chat utterance. For exact old traces/changes, read the linked historical audit docs and Git rather than inventing a root cause from memory.

### Recovery and older references

The project had a long recovery pass, a P01–P61 issue ledger, older public/private 1.4.17/1.4.19 builds and rejected broad stabilizations. Historical docs in `keyboard.wtf-paid-staging` and `keyboard.wtf-personal-api` include:

- `docs/CODEX_HANDOFF_FULL.md`.
- `docs/RECOVERY_PROGRESS.md`.
- `docs/BASELINE_1417_CURRENT_AUDIT.md`.
- `docs/regression-differential-1.4.17.md`.
- `docs/FINAL_CORE_RECOVERY.md`.
- Personal `AI_BACKEND_ROUTING_AUDIT.md`, `PERSONAL_API_PRODUCTION_DIFF_AUDIT.md`, `PERSONAL_API_CONTEXT_RELIABILITY_AUDIT.md`, `PHYSICAL_P0_SAVED_SELECTION_RECOVERY.md`, `SAVED_REFERENCE_STABILIZATION_2EE839C.md`.

Do not treat an old P-issue closed label as current acceptance after later changes. Do not bring back a rejected branch merely because it includes a desired feature.

### Accepted fe60cec → URL and screen fixes

The user explicitly froze **fe60ceceff4212b13faa844e3c36f8e771db7894** (“Bind natural selected translation to owned executor and silent handoff”) and all prior rollback checkpoints. Helper **2.1.28 was already published/live**. Only URL launch gaps and screen latency/session context were in scope.

Historical Personal Git sequence:

| Commit | Narrow purpose |
| --- | --- |
| `adc9e4a` | Restore Live startup instruction budget while retaining prior instructions. A too-large system instruction can fail startup; preserve the 20,000-character transport bound. |
| `fe60cec` | Natural selected-text translation bound to the owned executor/silent handoff. |
| `0dff50f` | Simple URL fallback when connected Chrome Helper has no window. |
| `3519b8a07d27beadb0461960858c624a6b2e40f7` | Retain approved screen snapshot for session-scoped visual follow-ups. |
| `b1f5b9a` | Keep authoritative saved opens consistent across tool handoff. |

Screen symptoms: first analysis sometimes >40s, provider/quota/503 failures, follow-up forgot old screen or re-captured for a new detail question. The retained actual image matters: repeating a textual answer alone cannot prove visual recall. The user finally confirmed screen analysis worked after the bounded session snapshot change and required clearing at goodbye/session end. Explicit fresh analysis remained fresh. Do not blindly resize/compress/crop quality or rewrite audio to chase provider latency. Any future latency claim needs stage timings and a like-for-like old-build comparison; this handoff does not invent missing stage values.

URL user acceptance: Chrome active worked; Chrome closed opened all sites; Edge foreground still opened the configured default Chrome. User preferred safety and accepted this rather than complex active-browser routing. Do not treat every background Helper request as a defect.

### Saved Links/mappings/workflows and mode parity

After screen fixes, compound “open RevenueCat link, Devpost project and my website” sometimes requested a redundant confirmation, picked the generic service rather than the saved project, or treated the whole compound string as one nonexistent alias. User demanded comparisons against two prior versions before fixing. These were **authoritative saved-identity/tool-handoff problems**, not permission for a general saved-memory rewrite.

Saved Link/workflow exact identity must outrank a generic homepage guess, and model-proposed workflow/URL variants cannot authorize a different saved target. Preserve negation, mentions, literal conjunctions inside actual saved names, explicit native/workflow overrides, selected-reference behavior and exactly-once compound execution. Old `private-rollback/3519b8a-before-saved-handoff` exists. Never silently rewrite persisted workflow records to make a runtime test pass.

Personal/API and Managed tests exposed inconsistent selected-text tool proposals, “analyze selection” incorrectly shown as screen analysis, clipboard/configuration translation failures and wrong dynamic status. User demanded one already-tested shared product executor, not separate semantic implementations for each backend. Narrow shared desktop handoff plus **bounded Managed translation contract** were authorized. Full source up to 50k characters and complete translated output needed matching server validation/budget/incomplete-output handling. Exact provider token-count admission was considered preferable to opaque over-conservative rejection for exceptional CJK sources, without raising spending budgets arbitrarily. Read the actual token-count gate/doc to know what shipped.

Later normal 1.4.20 installation initially failed translation due configuration/output contract mismatch; matched backend correction was tested and user confirmed translation worked. A subsequent requested screen/selection regression must be diagnosed against exact installed route/source, not assumed a fresh broad regression because model behavior varied.

### Public release preparation

Public prep accumulated security/update/accounting/installer/legal/operational audits. 1.4.20 `231a309` archive was accepted before a distinct **1.4.21** installer/runtime-provisioning/Luma release. Safe manual updates were explicitly chosen. No code-signing certificate exists; unsigned early release is an accepted limitation, **not** “Microsoft is verifying it.” Browser Keep/Keep anyway and SmartScreen More info → Run anyway are different prompts. Never advise disabling protection or ignoring a malware warning.

No customer Python/Node/separate .NET/WebView2 prerequisites are required by the verified package. Native Visual C++ x64 runtime gap was addressed through installer provisioning using Microsoft's download/license/UAC flow; first-run speech-model downloads/fallback remain existing behavior. The owner waived a spare PC/VM and asked current-PC/package/source proof. **Fresh-machine/offline acceptance was not performed.** Do not make customers install development runtimes or claim that waiver proves a clean machine.

Voice redistribution was initially unresolved, then the owner affirmed the necessary permissions on 7 October. Do not substitute voices/delete assets or claim independent legal review without evidence. Original licensing/notices remain relevant.

### Checkout accidentally-looking “Coming soon”

Live pricing screenshot showed Coming soon/subscriptions not open because the catalog **payments kill switch was deliberately held** during financial cutover. Owner asked to reopen safely. Read-only deployed-source/payment checks plus focused payment boundary tests preceded an owner-authorized, version-preconditioned two-field switch/message change. Prices/allowances/provider ownership were preserved. It is now enabled, freshly rechecked for this handoff. No new bank checkout/refund was silently performed.

### Security email investigation

The specific Git secret alert was traced to a public **Firebase browser configuration key**, not demonstrated leakage of a server AI/OAuth/payment secret. Recorded checks restricted its API allowlist, Gemini access was denied, Firebase AI Logic absent and Firestore client rules deny-all. The alert was not forcibly resolved, the key not rotated, and Git history not rewritten. Do not assume all Google emails are harmless; distinguish the investigated alert from any new notice. Do not reproduce keys while explaining this.

## 9. Luma website/local Settings — preserve accepted work, separate hosted voice

Luma is the original blue crystal pet, not a generic replacement. Original assets/reference package are protected under:

`C:\Users\Admin\Documents\Codex\2026-09-24\create-a-pet-based-on-what\outputs`

Important original folders: `luma-playground`, `luma-pet`, `luma-safe-v1`; installed Codex pet `C:\Users\Admin\.codex\pets\luma` is separate. Modules include eyes/blink/gaze/walking/wave/jump/sleep/loading/error/success, staged growth/edge-peek, articulated rare dance, glass/water trails, wind/snow/attached realistic hat and four ambience moods. Preserve licenses and reference atlas; no playground UI/fake install demo transplant.

Owner's final accepted design directions:

- Preserve original website layout/spacing/content/Windows link; add macOS button alongside, same treatment. Mobile compact icons/short Account/Pricing labels and smaller pet, not clutter.
- Comprehensive light/dark tokens, **black not navy** dark theme; **polished-glass** hero material chosen, original faint background effect respected; no blanket inversion/black-on-black names.
- Luma roams the page safely, walks/runs instead of teleporting away from content. Rare peek/dance/sleep appropriate to activity, startup edge hiding/peek → run in → wave/short hello. Drag freely without permanently disabling random behavior.
- Pet stays visible on tab return and during Live; smooth animation, no repeated disappearing/reappearing. Asset initialization/performance guards should not cause mid-conversation flicker.
- Toolbar under pet is hidden unless its bottom hover/focus/tap zone is engaged, aligned while moving; Voice/Cursor/Ambience/Settings controls, accessible pause/mute/hide/reduced motion.
- Glass → water → none → glass cursor cycle immediately; original effects, slightly stronger but never distort text. Dark snow/hat/gentle gusts requested automatically, not an exposed weather toy.
- Translucent nearby speech bubble, never overlaps pet/controls/text, stable on scroll; captions of actual speech, restrained reveal/type effect.
- Music changes actually change/crossfade compatible moods, speech ducking and mute/user choice respected. Owner requested full audible volume in website experiments, separate from Windows output 2 diagnostic preference.
- Project-aware narration distinguishes shipped/coming soon, accurate pricing/settings/Helper/install warnings. No unrestricted local data/secrets in context.

Live public **visual/theme/mobile Luma work is published**. Local preview voice was owner-confirmed working in one stage. **Hosted Luma voice is still disabled/unaccepted**: requires dedicated authorized cloud credential/project/quota, constrained short-lived tokens, admission/session limits, actual hosted audio/permission/reconnect proof and cleanup. Do not put permanent keys in browser or share Jarvis session/billing ownership.

A disabled-Luma contention test failed availability (fewer admissions than expected, no observed over-admission); original receipts retained. It does not prove public Jarvis billing failed. Before enabling hosted voice, fix/prove this boundary rather than declaring local voice success a production PASS. Keep tour/text guidance available when voice unavailable. TTL ACTIVE configuration is not actual deletion proof.

## 10. Connector architecture — continue it, do not replace it

### Core chosen design

**Native hybrid** on existing keyboard.wtf owners, not a new generic framework:

Human request → existing Jarvis understanding → small normalized connector broker → one provider/account/frozen goal → owned controller/task → lazy 2–8 normalized capabilities → deterministic native provider retrieval/actions, bounded read reasoning only when needed → verified structured result → useful answer/session-only reference.

Composio is infrastructure behind `IConnectorProvider`/adapter, **not Jarvis vocabulary**. No Composio schemas/IDs/errors/branding exposed to users. Vendor should be replaceable without rewriting Jarvis. No hundreds of tools in permanent Live prompt.

**Important truth:** obvious requests receive deterministic registry/service narrowing once the connector broker is selected; current provider choice is still primarily Live tool selection. There is no separate expensive classifier AI request, but also no proven universal deterministic pre-Live natural-intent router. Do not describe an aspiration as already implemented.

Permanent Live catalog uses three normalized broker tools (resolve / execute / control) within the fixed tool setup. Older full-setup probes refer to **62 tools**; inspect current exact catalog rather than count provider capabilities as permanent tools.

### Desktop connector source map

- `src/Services/Connectors/JarvisConnectorController.cs`: one task/account owner, cancellation, account handoff, UI, evidence/write fences and temporary results.
- `ConnectorReadReasoner.cs`: bounded injected provider-independent read worker, structured JSON actions/evidence. Uses existing AI gateway owner.
- `ConnectorMailReadPlan.cs`: required mixed content/count partition versus optional read path; simple Gmail requests must not be blocked by an unnecessary model plan.
- `ConnectorCountInterpretation.cs`: optional one-time interpretation of already observed headers for malformed inferred category counts, not a global planner.
- `ConnectorSessionResult.cs`: independent bounded read-only session result, not task/action authority.
- `ConnectorResourceReference.cs`: validated exact provider object references; **no provider-home fallback**.
- `ConnectorEvidenceTraits.cs`: common search/content/metadata evidence roles (shared with backend source).
- `GeminiLiveConversationService.Connectors.cs`: narrow connector child reasoning/control integration.
- `PaidAccountService.Connectors.cs`: authenticated normalized backend client; local session is existing Firebase-owned account.
- `WebSettingsService.Connectors.cs`: actual Settings endpoints/resources.
- `ConnectorActivityLabel.cs`, `ConnectorProviderIcon.cs`, `VoiceOverlayForm.cs`: friendly native activity rendering.

### Backend connector source map

`staging/backend/KeyboardWtf.Staging.Api/Connectors/`:

- `ConnectorEndpoints.cs`: authenticated normalized API routes, server-derived identity, validation/admission.
- `ConnectorContracts.cs`: normalized request/results, provider interface/contract boundaries.
- `ConnectorRegistry.cs`: enabled families/capabilities, argument shapes, help and activity source of truth.
- `ConnectorConnectionService.cs`: actual provider connection reconciliation, OAuth ownership/connect/disconnect/reconnect and account binding.
- `ConnectorTaskService.cs`: goal/task lifetime, narrowing, execution authorization and control.
- `ConnectorOperations.cs`: durable claims/idempotency, verification and unknown-write safety.
- `FirestoreConnectorStore.cs`: UID-owned task/accounts/OAuth/operation persistence; cache is not truth.
- `ComposioConnectorProvider.cs`: only adapter knows vendor API/auth-config/tool/native argument details.
- `ConnectorMailQueries.cs`: bounded literal/topic/category search policy.
- `ConnectorTelemetry.cs`: separate infrastructure counters/latency/category/correlation without message bodies.
- `ConnectorMaintenance.cs`: finite cleanup/lifetime work, not uncontrolled polling or financial override.

Normal clients receive opaque normalized account/task/result references, never arbitrary internal provider connection IDs/raw responses/tokens. Server must validate ownership every time. Multi-account model is records, not `gmail=true`.

### UI source of truth

Website files: `production/site/connectors.html`, `connectors.js`, `connectors-panel.js`, `connectors-settings.js`, `connectors-panel.css`, local `connector-icons/`.

The actual local Settings embeds these shared modules/CSS/resources. Registry help projects to both UIs for the subtle capability ⓘ. One capability list, not three drifting hardcoded lists. Customer UI states Connected/Available/Connecting/Reconnect/Coming Soon/Unavailable are normalized, searchable, scrollable, mobile/theme tested. Connected must be reconciled from provider/backend, not stale localStorage.

Canonical intended flow: Settings → default browser → keyboard.wtf connectors page for **same authenticated Firebase account** → provider OAuth → our page success → optional installer-backed return deep link → desktop focus/return/account refresh. Inspect existing actual protocol handling/registration; do not improvise a new unbacked URI handler. Current testing uses staging management and candidate endpoints, not production enablement.

## 11. Environment and OAuth configuration — do not break isolation

- Staging website: **`https://keyboard-wtf-connectors-staging.vercel.app/connectors`**.
- Desired production route: `https://keyboardwtf.com/settings/connectors` or already-established compatible `/connectors` route. Do not choose a new route independently of current Vercel rewrites/deep links.
- Staging backend base: `https://keyboard-wtf-connectors-staging-aac6362p6a-el.a.run.app`.
- Last documented **accepted default staging traffic**: 100% `keyboard-wtf-connectors-staging-00016-vic`.
- **Held private backend**: source `a25d094a36e4d9718ed4dcc47d9cd3f05b31ab22`, revision `keyboard-wtf-connectors-staging-00028-boh`, zero default traffic.
- Candidate uses pinned held URI: `https://gs-a25d094---keyboard-wtf-connectors-staging-aac6362p6a-el.a.run.app/`.
- Held backend API tree recorded `07b46d01a9db49fb10e0bcc04c05157803da1f2f`.
- **New e2/28ed backend recovery/source is not deployed**. A current compiled desktop probe against held a25d094 cannot prove the undeployed server retry slice.
- Public backend/site traffic, public installer and Helper remain protected. Staging deploy success is not public release permission.

Initial architecture checkpoint was `64040f3eece47b9387f0476e61e223b823000366`, based on clean public `987e4d4`, with only connector README. That statement is historical; substantial implementation now exists. Do not restart at scaffolding.

### Credential rules

The owner supplied production/staging Composio project keys and Google/GitHub custom OAuth credentials in the old thread, plus Personal Gemini keys in authorized secure configuration/a Downloads Word document called API KEY/API KEYS. **No values are included here.** Discover existing approved secret-manager/server env/ignored config/DPAPI entries without printing contents. Do not store permanent server keys in desktop/frontend/package. Multiple same-project Gemini keys do not create independent quota. Freeze one owner per session; never mid-session key-pool hop to keep a conversation alive.

Before requesting any Slack/Discord credential, inspect both Composio projects and all enabled/disabled/name variants with their proper authorized project keys. This discovery has already been done and documented; verify only if state changed, do not blindly repeat/create duplicates.

### Google consolidation and actual availability

Production had the twelve supplied enabled Auth Configs; staging had **four custom families**: Google Super, Forms, YouTube, GitHub. Google Super powers normalized Gmail/Calendar/Drive/Docs; Forms and YouTube require separate families. Therefore seven capability rows can correspond to four account families. Do not expose duplicate overlapping Google Super/dedicated tools or require redundant consent where scopes/capabilities genuinely consolidate.

Configured Google/GitHub provider APIs in `keyboard-wtf-prod` were initially disabled; actual SERVICE_DISABLED evidence led to enabling Gmail/Calendar/Drive/Docs/Forms/YouTube APIs without arbitrary scope/client changes. This solved API access, **not OAuth app verification**.

Read `docs/connectors/STAGING_AUTH_CONFIGURATION.md` for exact nonsecret Auth Config IDs and owner UI steps. Historical production IDs are existing config references to reuse; do not blindly recreate them from an old prompt.

| Provider gate | Actual recorded result / next external step |
| --- | --- |
| Google Super | Staging custom config, real UID-bound Gmail/Calendar/Drive/Docs access. Super is not assumed to cover every Google product. |
| Forms | Separate staging custom config; one authorized blank private Form created/read back historically; don't create again. |
| YouTube | Separate staging custom config; channel/public search reads, no analytics/transcripts/upload claim. |
| GitHub | Separate custom staging config, reads and actual disconnect/reconnect independently verified. |
| Slack | No staging config after full discovery. Production secret is masked; production ID queried with staging key returns 404. Missing exact field is `credentials.client_secret` in an isolated staging custom Auth Config. No supported cross-project clone/reference shortcut found in inspected SDK/API. |
| Discord | Same staging missing client-secret boundary; normal **user OAuth**, not Bot. Initial capabilities identity/server list only. |
| Meet | Owner believed a config existed, but recorded full discovery found none in either inspected project. Locate actual project/ID before enabling; do not invent/create duplicate. |
| Reddit/LinkedIn | Managed OAuth branding not proven white-label. Coming Soon in production until custom/branding-safe auth, not secretly enabled. |

Slack: current production scopes documented 47 distinct/94 repeated fields, no `admin.*` or auditlogs scope. Do not trim scopes without failure/evidence. External-workspace distribution/admin approval is separate real acceptance. Discord staging recommendation `identify guilds`; do not carry over unrelated broad/RPC/bot scopes or change production callback blindly.

Working production callback URLs must remain intact. A provider internally returning to Composio may be unavoidable; normal flow must finish on our branded website, no Composio success page/provider IDs/tool names. **Zero visible Composio is a production gate, not a globally proven PASS for all unavailable providers.**

## 12. Actual supported capability registry — avoid exaggerated product claims

At 28ed1ca, inspect the registry/adapters/tests to verify additions. These are actual bounded exposed operations, not promises of complete ChatGPT feature parity:

| Service | Current normalized scope |
| --- | --- |
| Gmail | Search; verified paginated count; read message/headers/body; bounded recent thread; one explicit plain-text send, optional existing thread/reply headers; exact verified result opening/reference. No separately verified full attachments/draft-management feature merely because vendor supports it. |
| Calendar | Primary-calendar time-interval list with pagination; explicit event create **without automatic attendee invitations**. |
| Drive | File-name search and file metadata; not arbitrary file content/binary downloads. |
| Docs | Read identified bounded document; create blank document. Not a rich editing surface. |
| Forms | Read identified form; create blank private form, no automatic publishing/distribution. |
| YouTube | Connected user's channel info/statistics and public video search. No transcripts, uploads, analytics dashboard or public comment writes. |
| GitHub | Accessible repositories; open issues endpoint may include PR entries; explicit issue create. No merge/code execution or closed-issue coverage claim. |
| Slack | Defined bounded workspace search/history and explicit post, **not real staging/public accepted while config absent**. |
| Discord | Own identity and server membership list through user OAuth; **no bot/admin/message send**, staging config absent. |
| Meet / Reddit / LinkedIn | Coming Soon/unavailable, not tested supported execution. |

The user wants eventual full useful capabilities. Do not arbitrarily disable a supported safe send just because old browser-draft instructions said drafts only. Conversely, do not expose the vendor universe or advertise unimplemented capabilities to satisfy that wish. Capability expansion requires actual scope/implementation/verification under the same generic task owners, after new direction.

## 13. Connector lifecycle, cancellation, writes and temporary memory

### One goal, one task, one UI owner

Resolve freezes human request, logical session, Firebase user and one service/account. Same task reuses discovery. Account ambiguity returns brief choices; a sole/default verified account can be used without gratuitous clarification. Correcting account retains visual identity but changes bound execution authority only through fenced handoff.

One task starts dynamic activity, updates status while real work runs, remains present between internal search/reasoning/read steps and clears only at complete/fail/explicit cancel/session end/expiry. Voice health ownership remains separate; a visual Thinking state cannot hide a hung voice indefinitely.

Ordinary speech/noise/“how long is it taking?” cannot destroy connector work. Clear human cancellation (“cancel that,” correctly targeted stop/forget it) is required. Account/session reset/disposal remain trusted cleanup. Late providers/model/account rebind cannot publish, perform a write or revive a cancelled task.

Latest model-control cancellation checks current eligible owner, not just a supplied task ID. Two possible owners require disambiguation; provider-named cancel must match. Quoted/negated noise is not authority. Cross-subsystem model-control ambiguity remains an integration gate; preserve existing BA/translation immediate precedence.

### Read intelligence and progressive retrieval

Provider native search/index first; rare person/project/topic/date constraints → bounded candidate headers/snippets → observed IDs → actual content → refinement if irrelevant/empty → grounded detailed answer. Do not download/index the full mailbox or require exact remembered subject.

Read-only worker limits: **2–8 capabilities, ≤4 distinct search queries, ≤8 executions, ≤9 model turns**. Strict bounded JSON/evidence/cursor validation, no identical duplicate execution. Up to exactly two independent validated search proposals may execute **serially** in one model turn. No batching writes/counts/cursors/resource reads. Same original task/account/deadline/AI owner throughout.

Approximate display/company names can be recovered with observed/topic/date evidence for reads. Literal addresses/domains/Gmail expressions remain exact. Ambiguous candidates must be admitted/clarified. **Never fuzzy-select a send recipient.**

Search previews are not full content; content questions require actual primary content evidence where supported. Metadata tasks should not be forced to fetch unrelated content. Empty search is only a failed query, not proof no message exists; inferred negatives need bounded alternative search. Partial/truncated coverage must be stated. Provider `verified`/task Complete/`evidence_available` is not proof the human semantic goal was correctly answered.

Detailed thread synthesis should cover topic, important messages/dates/decisions, pending actions/latest state, distinguish quoted old history from current replies and use stronger reasoning only where useful. More AI turns cost time/quota; simple exact/count/native paths need no new reasoning model.

### Counts are a distinct evidence problem

Devpost winner count is **36 matching notification emails in the verified historical scope**, not a current guaranteed mailbox total or count of distinct competitions/prizes. Broad sender total **604** was separately paginated. Do not use resultSizeEstimate, preview page size or sample count as exact total.

An inferred category requires observed sender and stable subject pattern, correct query scope, actual pages/deduplication and complete cursor handling. `complete=true` proves that query's count, not that the semantic pattern was right. Partial count is a lower bound. Literal user query must not be rewritten. Existing optional interpretation uses at most six verified headers once under same owner, not an expensive mandatory model step for every request.

### External writes

Use existing permission/confirmation philosophy: a clear explicit normal write with exact account/recipient/content can authorize that action according to the configured policy; destructive/bulk/ambiguous/inferred writes need existing elevated confirmation. Do not ask “would you like me to send?” after a fully clear send request merely from habit, but do not delete AlwaysAsk or ignore the actual policy.

Durable operation claims/idempotency and verification protect sends/posts/creates/updates. **Never retry an ambiguous external write blindly.** Check the actual result/reconcile first; if still unknown, say unknown. Do not relabel the original journal because later readback proves delivery.

**Protected already-used fixtures:** exactly one self-email, subject `Connector Test`, body `Hello Tanush How are you.`, was authorized/sent and independently verified as one Sent match. Initial operation remained truthfully outcome_unknown. It must **never be rerun** under the old authorization. One blank private Google Form was also authorized/created/read back and unpublished; no new Form/Doc/email creation is authorized by this handoff. Preserve ignored send/create fences.

### Session-only result memory and exact opening

Completed task loses action/routing authority, but a separate bounded result record retains provider/account, object IDs/titles/dates/validated URLs, compact grounded answer/facts and references for current-session follow-ups. Clear on session/account reset; no permanent full email/document content.

Current bounds: ≤3 retained results, ≤12 references, ≤6 evidence receipts, compact answer ≤6k. Unaccounted candidate overflow is tracked, not silently discarded to pretend unique target. An omitted task reference may be accepted only for one unambiguous **read-only recall/open** result; writes/control/rebinding still require current exact authority. IDs in results are not permission.

Gmail URL comes from verified observed thread/message/account. Four search candidates with only one sample are still four candidates; partial/truncated/cursor/malformed/omitted candidates block search-only unique navigation. Direct identified thread read can have exact-object identity even when its content is partial. Ranked primary content selects target; arbitrary first receipt/inbox URL does not.

Other validated references: Drive `webViewLink`, Calendar `htmlLink`, observed GitHub repo/issue/PR-matching URL, Docs/Forms/YouTube observed ID-derived URL, Slack observed matching permalink. No unsupported Discord/history provider-home fallback. Returning a reference is **not browser-open acceptance**. Exact Gmail thread was actually browser-verified in the earlier 11 October pass; new generic reference checks alone do not prove every provider page opens.

## 14. What the latest source checkpoints changed — causal, not a redesign

| Source/tag | Changes and proof boundary |
| --- | --- |
| `aff814e72bf419da074e460bb184db2d555a20ef` | Pre-quality docs/source boundary. Protected `checkpoint/connectors-before-quality-aff814e-20261011`. |
| `0cf6b789784e9be1dcdc3e0387e01a5f5fce4879` | Initial quality: bounded read reasoner, independent session results/exact mail reference, activity continuity, inset logos/no white connector meter, capability help from registry. Natural acceptance still not repeatable. |
| `3a1fb06fc7371eeb06f04e3d8ee8c4228a22fbb8` | Visual owner independent of health detail, atomic account handoff/cancel fences, evidence roles/content policy, serial two-search batching, unambiguous ID-free read-only recall. 4,600 desktop passes; later quota still blocks natural quality. |
| `e2a167151b16235e5b492368a758bb6a0097f2fc` | One typed transient **initial read GET** recovery inside original server claim/account/deadline; not a whole-task retry. Protected `checkpoint/connectors-read-recovery-20261011`. Undeployed. |
| `28ed1caae7ad99db509a301a2babfa49fefdd5a1` | Latest generic observed-object context, conservative ambiguity/overflow, trusted Gmail candidate shape, explicit human cancellation authority, actual Thinking/read statuses, shared panel duplicate/focus/scroll/reconcile fences, safe optional regex-timeout handling. Protected `checkpoint/connectors-resource-context-ui-20261011`. **Source only**. |

Transient read retry details: transport/IO/internal timeout or HTTP **408/429/500/502/503/504** can produce internal typed hints; auth/scope/config, malformed/oversized envelopes and generic unavailable do not. Only initial existing server-planned non-write GET/no body may dispatch once more, default wait **250ms**, supplied delay >2s/invalid/duplicate/over original deadline fails closed. Recheck UID/account/grant/task/deadline immediately before redispatch. No write/OAuth/discovery/continuation/enrichment/write-reconciliation replay, no new AI/billing owner. Cancelled late result stays cancelled.

Latest optional count-regex issue: a 50ms regex timed out under local load. Preserve that timeout; skip optional interpretation rather than aborting the task, broadening query or raising the safety budget. An 8-case serial replay passed. Preserve the rejected full TRX (4,762 passed / one fail); don't claim it never failed.

The shared panel now fences duplicate account-family mutations until real reconciliation, preserves focus/scroll, shows connecting only while creation occurs and removes stale Connected after ambiguous mutation. Failed reconciliation is Unavailable, not cached success. Account-generation checks reject late old-user results. No mutation replay to make UI green.

## 15. Evidence and measured performance — latest versus historical

### Latest exact-source gates recorded in checkpoint 28ed1ca

- Desktop final: **4,776 passed, 0 failed, 0 skipped**, about 4m21s (TRX interval 263.994s): `.artifacts/connectors/quality-tests/resource-owner-final-desktop-v2.trx`.
- Backend connectors with real local Firestore emulator: **299 passed, 0 failed/skipped**: `resource-owner-backend-connectors-local-emulator-v1.trx` in same directory.
- Wider backend with emulator: **1,709 passed, 0 failed/skipped**: `resource-owner-backend-wide-local-emulator-v1.trx`.
- Shared panel/browser fixture suites: **15 ownership / 16 existing UI / 4 theme-mobile groups**, zero errors. Actual shared modules/CSS, inspected 320px light/dark screenshots. Fake accounts/mutations: not installed OAuth proof.
- Native popup rendered fixtures at 100/125/150/200% synthetic DPI show inset unclipped provider icons, no white connector inner meter and retained activity. These are **not physical Windows scaling or installed popup acceptance**.

Earlier 14/182 emulator skips are historical; a usable existing Java21/emulator was later located and fresh complete gates passed. Preserve both old skipped/failed and new accepted receipts. Do not add overlapping focused counts to totals.

### Latest real compiled-source controller → authenticated product backend → provider checks

Final test assembly SHA-256: `C8D1B61A468F0C25A68EF1396D06664FBEA6DA5B713B187D89CCA5B5BC61029A`. Receipts froze parent e2a1671 plus then-dirty source; do **not** rewrite receipt provenance to the later committed HEAD.

| Case, 11 October 13:50–13:53 UTC | Verified result | Measurements / scope |
| --- | --- | --- |
| BioCyclic | Actual project and prize **Top 10 Best Performers**, four candidate matches, identified thread content, account-qualified mail reference | **6.094s**, five product HTTP requests, zero AI/writes. Resolve 1279ms; search 2849ms; thread 1224ms; controls 97/72ms. Not an AI summary or fresh browser-open trial. |
| User-authorized existing Doc | Identified document read, untruncated recall/ref, no follow-up search | **3.286s**, four HTTP, zero AI/writes; resolve 1097ms, read 1646ms, controls 79/80ms. Exact link retained, not browser navigation proof. |
| Existing protected Form | Blank/unpublished/not accepting responses; read-only recall and correct editor reference | **2.529s**, four HTTP, zero AI/new creation. |
| In-flight cancel | Explicit cancel, UI cleared, no late completion/write | **4.181s** diagnostic total, five HTTP, cancel round trip 102ms; not all concurrency scenarios. |
| Ordinary classifier | 30,000 compiled classifications | **0.5737ms** warm total, **zero backend/provider/AI/account/credential reads**. Microbenchmark only, not whole spoken response latency or before/after improvement. |

Receipt names under `.artifacts/connectors/diagnostics/`:

- `quality-native-5d5de484f2704d80bc8051ced12cbda7.json`.
- `resource-open-c5b9a2feefc94583a7d4c173cd22adfa.json`.
- `resource-form-f0dc19af5473446a9b62d0705e382c65.json`.
- `final-cancel-b080ac2b0117420b969e34b9769dc53e.json`.
- `hotpath-classification-649ca10bc61f45b7a56ccfffff5fd3fe.json`.

Same-day separate real GitHub pagination: **55 distinct repositories, four pages, zero duplicates**, **11.870s**, seven product HTTP requests, no AI/writes. Not an atomic snapshot of a changing repository universe or a natural semantic summary.

Earlier same-day browser proof: `quality-exact-browser-20261011.json` rendered actual Gmail Blueprint Ideathon subject/sender/July 18 time/BioCyclic/prize, **not Inbox**. Do not publish private Gmail URLs/IDs/body in documentation.

### Preserved failing/limited attempts matter

- Latest Personal reader attempt at **13:06 UTC, 11 October** resolved four capabilities then HTTP429/Quota **before any Gmail execution**. One model attempt, two HTTP, **2.990s**, activity cleared, no writes/Managed fallback. `quality-biocylic-9d751707d01045639c3b70075348c693.json`. Full Live setup accepted with no microphone/playback input; not a 1011 diagnosis or natural provider-routing success.
- Earlier attempt at 12:06 UTC similarly rejected Personal reasoning quota; another older **83.365s** natural run succeeded with five executions/six extra model calls, but unsuccessful repeats remain. One successful slow answer is not a reliable semantic-search PASS.
- Parallel Forms diagnostics hit **connector endpoint HTTP429** from the existing two-operation-per-UID admission fence, no model ran. This differs from Personal AI 429. Serial correction passed; don't weaken admission to make a harness pass.
- One Forms open harness lacked current human “Open that form” authority; refusal was correct. The harness was fixed, not product auth weakened.
- Earlier long combined connector response took **127.745s**; later improvements/count checks do not prove every vague/complex query is now fast.
- Some historical runs lacked partial-body disclosure, counted a wrong semantic category, or stopped after search without proposing count. Preserve failures; don't retroactively certify them.

### Earlier real product acceptance, still useful but bounded

- Codex Community event: product search/read verified sender/event/updated timing/registration details. Private contents remain in original user answer/ignored evidence, not this file.
- Daisy conversation: actual message/latest-thread read verified recent domain-transfer discussion; bounded thread partial coverage acknowledged. Detailed fresh quality still needs repeat.
- Devpost winner notifications: **36** via observed subject/sender pattern, complete pagination/dedup/body sampling. Separate broad sender pagination yielded 604. Neither implies today's mailbox immutable.
- One authorized self-email: one send dispatch; independent product read found one exact Sent match. Original unknown journal preserved; no duplicate.
- GitHub actual user disconnect/reconnect on 10 October independently verified old tombstone/new account/OAuth completion and desktop/backend agreement. Google unaffected. Fresh subsequent two-capability repo/issues product read **4.794s**, no AI/write.
- Earlier installed Settings 10/10, 12 rows/seven Connected capabilities across four families, theme/name legibility; owner saw Gmail logo/friendly status/blue orb. Those are installed **older candidate** proofs, not new 28ed UI acceptance.
- Full-tool arithmetic made zero connector requests; Open Spotify/Close Chrome selected local tools with zero connector calls but tests stopped at proposal, did not close the user's browser. Zero hot-path overhead claim must remain this precisely scoped.

Real product path means authenticated keyboard.wtf user → desktop/controller where applicable → our normalized backend/task/gateway → Composio adapter → provider → normalized verified result. **Direct Gmail/Composio scripts are diagnosis, never product acceptance.** Controlled controller calls are stronger than direct provider calls but weaker than natural spoken Jarvis/physical UI proof.

## 16. Research already performed — do not repeat it from scratch

Read `docs/connectors/INTELLIGENCE_ARCHITECTURE_REVIEW_2026-10-11.md`. It evaluates official/primary sources for:

- Lazy tool discovery, meaningful tool contracts and bounded batching (Anthropic engineering).
- Native Gmail filtering and API-versus-UI alias/thread-search differences (Google Workspace).
- Composio direct execution versus sessions and account pinning.
- LangGraph replay/idempotency: checkpoints do not make unknown external writes exactly-once.
- Microsoft Agent Framework ownership/workflow abstractions versus duplicate auth/AI/task layers.
- MCP structured tool/result contracts versus untrusted tool annotations.

Decision: keep native hybrid injected worker/gateway; no heavyweight dependency, unrestricted code-execution tools, new MCP server, embedding store or mailbox sync. Useful patterns adopted without transplanting an unrelated architecture. Models assist refinement/ranking/long content, deterministic routing/calls remain cheap, one task owns everything. Further research should answer a specific unresolved evidence gap rather than reset design.

## 17. Personal Live 1011 and speech cutoff — independent open issue

Owner repeatedly saw listening/reconnecting/1011 and occasional truncated speech, including before connectors. Comparisons were requested against **1.4.17**, not merely previous connector version. Source/installed reports show connector work did not alter Personal microphone/VAD/STT/playback transport. Minimal/full setup text and synthesized-audio tests passed bounded cases; this does not disprove intermittent real mic failures.

Current state: **STILL OPEN**, not fixed by removing Managed monetary caps. Sanitized server close reason, socket state, last provider event, mic chunks/bytes/frame/send gaps, resumption/reconnect timing and playback ownership are needed from an actual failing installed session. Do not guess cause or change buffers/VAD blindly.

Speech cutoff causes to distinguish: 1011/socket close, ownership rollover, explicit barge-in, playback cancellation/replacement, publication/queue/device failure, reset, or a diagnostic stopping before final playback drain. One historical harness did stop early and was corrected; it is not proof all human cutoff reports were harness-induced. Received PCM parts reaching sink is not physical audible completeness.

The isolated **3.8 trial is NOT PROVEN BETTER**: full-tool setup acceptance and isolated successful count exist alongside 1011 and a 180s timeout. Production remains existing source/server model routes. A fair isolated benchmark needs stability/latency/interruption/transcription/tool/audio/cost evidence, never a blind switch because newer. No mid-session owner/model/credential migration.

Do not let this investigation consume connector work again unless evidence proves shared cause. Initial new-chat phase is read-only, no mic/provider reproduction yet.

## 18. Remaining connector acceptance — ordered queue, not permission to execute now

1. **Audit/reconcile first**, then wait for my next directions.
2. Establish eligible Personal reasoning configuration/quota **without** printing keys, mid-session rotation or Managed fallback. Current no-AI connector success is independent.
3. Repeat real natural imperfect-language quality on current source: BioCyclic winner, Extinsify/Extenify approximate name, vague event/topic/date, Daisy/domain-transfer and long thread. Use real product path, bounded retrieval/content, accurate deep summary and uncertainty.
4. Verify natural prize/date/open follow-ups from actual image-independent connector result context, no redundant search, exact thread/object and correct account. Distinguish cached textual answer from an unseen fact requiring retained evidence/read.
5. Measure search/resolve/extra model/provider/verification/task totals and tools exposed. Reduce redundant inference only causally; never trade useful content for a fake two-second response.
6. Exercise at least one second-provider complex semantic task to prove reusable intelligence, not Gmail hardcoding. Scope must match actual registry capabilities.
7. Matched new backend retry slice/private candidate packaging **only after** appropriate source/real-quality gates; verify exact payload, separate profile, correct held endpoint, no public promotion. Current installed 7e9 stays until reviewed replacement.
8. Physical installed popup/logo/no-white-meter/continuous progress/noise survival/explicit cancel, actual Settings info ⓘ/search/scroll/themes/320px, account-bound focus return; synthetic fixture proof isn't enough.
9. Multi-account choices, account switching, external revocation/expired grant/reconnect, session expiry, disconnect during execution, callback replay/duplicate connect, late cancel, timeout/429/partial reads; same UID across restart/second device. GitHub disconnect/reconnect is already one real PASS, not every scenario.
10. Representative supported writes require **fresh concrete authority if the old fixture is consumed**. Existing self-send/create fences are not renewed. Ambiguous effect must not duplicate. Read-only agent cannot turn email content into authorization.
11. Slack/Discord exact staging custom credentials/distribution remain external, do not block Google/GitHub. Meet actual config, managed-branding providers unavailable until verified.
12. Production Google OAuth verification and zero-vendor-branding flow; representative real production tests only after staging acceptance and broader regressions/approval.
13. Managed AI/connector metering correlation: additional model calls exactly once, provider calls no invented model credit; no new customer billing system. Operational telemetry separate, minimal content.
14. Freeze a genuinely accepted private/public checkpoint only after matched installed and broader regression. **Do not publish merely because implementation/tests exist.**

The owner's desired Gmail acceptance is finding **BioCyclic / Devpost / July 18, 2026 / 1st Nature's Blueprint Ideathon** winner email and prize **Top 10 Best Performers**, detailed summary, then “What prize?” / “When?” / “Open it” exact thread; vague remembered sender recovery; useful long-thread summary; one clear authorized send once if still authorized; stable activity and ordinary-noise survival. Current deterministic content/reference proofs pass portions, repeated natural AI synthesis remains open.

## 19. Google OAuth warning/public verification — exact owner handoff still needed

“Google hasn't verified this app” is not fixed by choosing a different end-user email or connecting successfully once. OAuth app owner/developer identity shown in warning may differ from the login account. API enablement, publishing status and completed app verification are different.

Known custom OAuth project is **`keyboard-wtf-prod`**. Audit actual Google Auth Platform/consent branding, publishing/audience status, homepage/privacy/support, authorized domains/domain ownership, client/callback and exact granted sensitive/restricted scopes. Do not remove useful scopes merely to suppress warning; identify genuinely unnecessary scopes by evidence.

Before submission, prepare owner-ready exact project/app/page, scope rationale, verified homepage/privacy/domain evidence, demo screenshots/video showing login → consent → each scope use → disconnect/deletion, and precise submission text/clicks. Restricted scopes may require additional Google security assessment; don't promise approval or fabricate console evidence. Read official current Google sensitive/restricted verification requirements when this task is resumed. Current research identifies likely verification cause, **not completed console/submission approval**.

## 20. Broader public-readiness limitations still tracked

Public app/site/download/subscription entry already live. “Fully public-ready” is not all evidence complete:

- Real fresh owner/bank checkout, nonrenewal and refund canary not completed merely by historical payment GETs/catalog enabled. No purchase/OTP authority is inferred from “do anything.”
- Remaining unknown-usage exposure/review fence needs separately reviewed operator handling as above; actual provider billing export/invoice reconciliation not established by local arithmetic.
- Received alert delivery, isolated restore drill and live provider-cost export proof remain independent operational gates.
- Hosted Luma voice/credential/project/token/audio/admission/actual TTL deletion not accepted; keep unavailable truthfully.
- Personal 1011/cutoff and repeatable complex connector quality remain open.
- Connector production OAuth/config/branding/lifecycle/representative production and Managed correlation not accepted.
- Unsigned installer/manual updates/no fresh-machine test are **accepted early-launch limitations**, not grounds for unrequested feature/signing/auto-update work. Automatic prerequisites must still be provisioned.
- macOS is **Coming Soon**, no package. Owner plan: stable genuinely useful connector checkpoint + final bounded polish/regression → later macOS and marketing/big updates, not simultaneous redesign.

Never call tests mocked/compiled green “everything perfect.” State what is actually shippable, what is owner-accepted limitation, what is safety gate and what needs external approval/access.

## 21. Deferred contextual quick-action button — record, do not implement yet

Latest user included a small separate UI specification, but also explicitly kept connectors first and deferred it until quality. Preserve this request for later direction:

- One reusable **trailing** popup action slot, proper layout/padding/ellipsis; no absolute overlay/text overlap/orb movement/popup resize jitter.
- Normal active idle listening: mic Mute; muted: Unmute; connecting/failed/closed hide; speaking playback unaffected.
- Mute suspends/ignores mic input via existing ownership only. **Not cancel/goodbye/end session/reconnect/context loss**; instant unmute/resume.
- Smart Writing: optional ✓ finalizes captured nonempty input into existing polish flow; retain existing auto-long-pause threshold unchanged. Tick/timer/double-click compete for **one operation**, never duplicate polish.
- Existing Settings persistence, small ON-default “Quick action button” toggle; OFF leaves old behavior/auto-polish intact. No whole new section.
- Reusable Hidden/Mute/Unmute/Done slot for future confirmation without inventing confirmation now.
- Guard state-change races, session close/restart, toggle while open, tick near timeout, new speech/popup close during polish.
- Real popup/DPI/mute continuity/unmute next request/Smart Writing both manual+auto/race/preference restart testing required. Source/unit-only not acceptance.
- Do not change connectors, audio model/VAD/STT/playback, selection/screen/saved memory/BA/billing/provider routing to implement it.

Not implemented at 28ed1ca. New chat must not confuse this pending feature with an active task to patch immediately.

## 22. Required first-pass reading — ordered and bounded

All paths below are local; verify existence. Read selected files fully where needed, not a search snippet treated as full evidence. Use dated latest addenda, read older sections as history.

### A. Current connector authority

In `C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-connectors`:

1. This entire handoff, repository/ancestor AGENTS instructions and applicable skills.
2. `docs/connectors/CURRENT_CONNECTOR_STATUS.md`.
3. `docs/connectors/RESOURCE_CONTEXT_AND_UI_CHECKPOINT_2026-10-11.md`.
4. `docs/connectors/INTELLIGENCE_ARCHITECTURE_REVIEW_2026-10-11.md`.
5. `docs/connectors/QUALITY_PASS_2026-10-11.md`.
6. `docs/CONNECTORS.md` (many historical addenda, latest top sections first).
7. `docs/connectors/PC_READY_2026-10-10.md`, `GITHUB_RECONNECT_2026-10-10.md`, `STAGING_AUTH_CONFIGURATION.md`.
8. Needed history: `REAL_GMAIL_CHECKPOINT_2026-10-08.md`, `GMAIL_RELIABILITY_2026-10-09.md`, `CONNECTOR_CONTINUATION_2026-10-09.md`, `CONNECTOR_ACTIVITY_UI_2026-10-09.md`, `CONNECTOR_COMPLEX_READ_CHECKPOINT_2026-10-09.md`, `INSTALLED_CONNECTOR_ACCEPTANCE_2026-10-09.md`, `GMAIL_OPTIONAL_PLAN_CHECKPOINT_2026-10-09.md`, `CONNECTOR_HANDOFF_HARDENING_2026-10-09.md`, `COUNT_COMPLETION_CHECKPOINT_2026-10-10.md`, `COUNT_CONTEXT_DIAGNOSTIC_2026-10-10.md`, `OWNED_COUNT_RECOVERY_2026-10-10.md`.
9. Actual owners/source/tests referenced above. Relevant tests in `tests/KeyboardWtf.Tests/` and `staging/backend/KeyboardWtf.Staging.Api.Tests/Connector*.cs`; inspect test assertions/skip conditions, not only summary counts.
10. Ignored `.artifacts/connectors/diagnostics/`, `quality-tests/`, `quality-ui/`, hosted discovery receipts, immutable candidate package proof; inspect metadata/booleans/timings without private content/secrets.

### B. Production financial/release authority

In `C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-public-release`:

1. `docs/production/PUBLIC_1.4.21_LIVE_CHECKPOINT.md` and **later** `LAUNCH_AND_CONNECTOR_CHECKPOINT_2026-10-08.md`.
2. `docs/production/LIVE_SERVICE_POLICY.md`.
3. `docs/production/EARLY_LAUNCH_REMAINING_ACTIONS.md` — historical queue with dated updates, not today's all-open list.
4. `docs/production/MANAGED_LIVE_UNKNOWN_USAGE_POLICY.md`, `ACCOUNT_DELETION_COMPLETION_GATE.md`, `TOKEN_COUNT_ADMISSION_GATE.md`, `VISUAL_CPP_PREREQUISITE_GATE.md`, `VOICE_LICENSE_RELEASE_GATE.md`, `RELEASING.md`, `GO_LIVE_GATES.md`, `PUBLIC_RELEASE_PREPARATION.md`.
5. Final ignored clock follow-up and operator plan referenced above; original immutable publication, exact-source export, checkout reopening/availability and live-drop receipts. Never execute an operator script merely because you find it.
6. Clean export Git/source versus dirty preparation checkout; installer archive/signature/feed/inventory. Do not interpret dirty diff as a deployed diff without provenance.

### C. Historical recovery reference

Use the historical Personal/Paid doc list in section 8. Read 1.4.17 comparisons for the known Live issue when relevant, not as permission to revert current product. Inspect `git show`, file blobs and exact parents/tags to identify accepted narrow changes; preserve all rejected archives. If a commit isn't present in the clean exported ancestry, inspect the correct historical repository rather than fetching/merging randomly.

### D. Skills and research

Use applicable repo/skill instructions. ASP.NET Core skill for backend review; Composio skill in `.agents/skills/composio/SKILL.md` when reviewing its API/tool configuration. Read skills yourself fully. Prefer existing official research in the architecture review; browse current official docs for uncertain live provider facts. No framework installation or credential discovery output dumps during audit.

## 23. What to report after the new chat's read-only audit

Report a detailed but organized reconciliation, then **wait for my next work instructions**:

1. Which checkout/branch/HEAD is authority; what changed since this handoff; dirty/untracked inventory and linked export dependency.
2. Which EXEs/profiles/processes/startup/deep links are installed/running, exact hashes/versions, correct intended GUI/native-host distinction. No silent launch/close/migration.
3. Public site/download/feed/catalog availability versus last documented cloud revision/traffic; distinguish fresh readback from stale documentation.
4. A clear architecture/ownership map from human request to shared action executor, AI owner/transport, connector provider/task/session results/UI, and billing/reservation/settlement. Name files and actual boundaries.
5. Past accepted fixes/rejected approaches and why reverting/duplicating them is dangerous.
6. Current connector behavior, supported scope, tools/models, task/cancel/write/memory/evidence/retry rules; source versus installed versus provider versus physical proof.
7. Exact remaining gates, ordered by current priority, with external owner/provider actions isolated. No vague “needs testing” or claims of universal perfection.
8. Independent 1011/speech issue, hosted Luma, OAuth verification, financial review/operations and deferred quick action status.
9. Any discrepancies or unverified claims you discovered; no fix proposal disguised as executed work.
10. A narrowly scoped next-step recommendation, but **do not implement it until I direct you**.

After later work is authorized, acceptance must include real intelligent connector goals, temporary follow-ups, exact object opening, coherent continuous dynamic UI, explicit cancellation/late-response safety, account/lifecycle/branding, representative provider operations, small schemas/low ordinary-path overhead and exact AI metering. Preserve public release until broader matched regression and publication decision. Customer value, reliability and grounded useful answers—not merely “API succeeded”—are the purpose.

## 24. Handoff provenance and limits

This document was produced from the long user thread plus fresh read-only Git/process/installed hash/file/public availability checks. No application source, installed app/settings, financial document, provider account or deployment was changed to create it. No provider/model request, purchase/send/create, new installer/test run or audio capture was performed for this handoff.

It is comprehensive operational context, **not a verbatim archive of every prior message** or an assertion every subsystem has been re-audited today. Existing repository docs, original receipts and Git retain exact historical evidence. Temporary counts/PIDs/account balances/traffic can change; re-read safely before relying on them. The next chat must first understand deeply and wait, rather than treating this detailed history as a to-do list with blanket execution authority.
