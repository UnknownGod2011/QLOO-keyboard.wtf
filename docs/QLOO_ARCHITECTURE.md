# QLOO architecture and boundaries

## Lineage and isolation

- Desktop source: read-only `keyboard.wtf` checkout at `877cf745446668f8b3d7d81c77b596a881550aa1`.
- Connector implementation source: read-only `keyboard.wtf-connectors` checkout at `19f6324ac84eee6982433ff8f6eb1420d02dc7de`.
- QLOO is its own Git repository, with no remote. The executable, DPAPI entropy, profile, instance lock, log, model paths, TTS paths, connector session, and Chrome native host/pipe/extension ID use QLOO-specific identities.
- The desktop app embeds its settings page and connector-panel resources from this QLOO checkout. The project does not point at either source checkout at runtime.

## AI and credential flow

Gemini inference uses `PersonalApiVault` in `src/Services/Ai/Backends`. QLOO creates that vault unconditionally and defaults it to `PersonalOnly`. QLOO's settings controller prevents switching to Managed and preserves PersonalOnly after reset. The native Windows key dialog validates keys and stores them using the current user's DPAPI protection. The browser settings page receives only masked key metadata.

Jarvis Live, selected-text translation, browser planning, and screen guidance bind to the shared `AiBackendGateway` operation. In QLOO the gateway's personal authorization callback only checks cancellation; it does not require a paid account. The optional connector relay does not select an AI provider or enable managed inference.

## Connector relay

The client calls the connector API only through `PaidAccountService.ConnectorRequestAsync`, which requires a separate signed-in QLOO relay session and pins the account owner for each operation. Provider credentials remain with the connector relay and the user's provider authorization. The relay source is kept under `staging/backend/KeyboardWtf.Staging.Api/` because the native connector client depends on that API contract. It is a separate deployable service, not launched by the desktop app.

Configure both `QLOO_CONNECTOR_BACKEND_URL` and `QLOO_CONNECTOR_WEB_URL` or neither. When absent, connector routes are disabled; Gemini Personal API and local Jarvis tools remain available. No keyboard.wtf production endpoints are used.

## Product mode and execution

`Program.cs` creates the QLOO tray app. `HotkeyService` registers only Ctrl+Alt+Q (Jarvis) and Ctrl+Alt+X (Cancel); the tray exposes only Jarvis, Settings, and Exit. Smart Writing and Local Dictation hotkeys are not registered, model downloads for their offline transcription path are skipped, and alternative AI providers are not in the provider registry.

Jarvis routes user actions through its existing tool and safety layers. Browser automation additionally requires its existing user consent, foreground, Helper, and page-level safeguards. The QLOO Helper uses a separate manifest key and native-host/pipe identity and is prepared from this project's own files.

## Credential artifact note

The source export contained credential-named local artifacts. Their names are excluded by `.gitignore`; their contents were not printed or added to Git. The API-key text file, Word key document, Word lock file, and temporary source archive remain in this working copy because the execution policy blocked deletion and the Word document is locked by an existing Word process. They must be removed by the developer before sharing or publishing this folder. The inherited Firebase web key was also removed from source config; the connector-site builder now requires QLOO-owned environment configuration and does not deploy unless explicitly passed `--deploy`.

## Evidence boundaries

A successful compile proves source compatibility only. The actual API key and provider account must be configured independently in QLOO before live Gemini, Gmail, or Calendar operations can be exercised. Extension rendering, physical microphone/speaker behavior, provider OAuth, and user-visible browser actions must be reported separately from source tests.

## Verification checkpoint (2026-10-31)

- `dotnet build .\src\KeyboardWtf.csproj -c Release --no-restore`: passed; 0 warnings, 0 errors.
- Focused Personal API and key-retry tests: 48 passed, 0 failed. The test project excludes Luma and paid-inference-only tests because those modes are intentionally absent from this product.
- The Release build produced `QLOO.exe` and had no embedded references to either source checkout's absolute path; generated build outputs were then cleaned from this working copy.
- Connector-site deployment script syntax passed `node --check`; with no QLOO deployment environment configured, it refuses before creating output. The old Firebase key and old backend/site endpoints were removed from its source configuration.
- A live tray/microphone/browser/connector session was not launched during this check: the public keyboard.wtf GUI and its Chrome native host were already running, and QLOO has no separately configured Gemini key or connector relay yet. Do not interpret the build and focused tests as live-provider acceptance.
- Credential-named local files remain ignored in the working folder; remove them before sharing or publishing. They are not tracked by this independent Git repository.
