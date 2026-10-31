# QLOO Jarvis

An isolated Windows desktop project derived from the keyboard.wtf Jarvis implementation. This checkout exposes one product mode: **Jarvis**. It uses Gemini Personal API credentials for inference and can use an independently configured connector relay for provider authorization and native Gmail, Calendar, and other connector operations.

## Requirements

- Windows 10/11 x64.
- .NET 8 SDK to build; .NET 8 Desktop Runtime to run a framework-dependent build.
- A Gemini API key with access to the Gemini Live and required generation endpoints.
- A microphone and audio output for voice conversations.
- Chrome plus the QLOO unpacked Helper only for Chrome page interaction and browser automation.

## Build and run

From this folder:

```powershell
dotnet restore .\src\KeyboardWtf.csproj
dotnet run --project .\src\KeyboardWtf.csproj -c Release
```

The app is the tray process `QLOO.exe`. Start/end Jarvis with **Ctrl+Alt+Q** and cancel the active Jarvis action with **Ctrl+Alt+X**. If either shortcut is already owned, QLOO uses the existing conflict fallback and reports the active shortcut.

For a self-contained x64 publish:

```powershell
dotnet publish .\src\KeyboardWtf.csproj -c Release -r win-x64 --self-contained true
```

## Configure Gemini

Open QLOO Settings from the tray. Under **Personal Gemini API**, choose **Add Gemini API key**. The key is entered in a native Windows dialog, validated, and stored in the QLOO profile with Windows DPAPI. The browser settings page never receives or stores the key. QLOO forces the PersonalOnly policy; reset and settings actions cannot switch inference to a managed backend.

The default profile is `%LOCALAPPDATA%\QLOO`. `QLOO_APPDATA_DIR` may select a subdirectory inside that QLOO profile for isolated development. It cannot point into keyboard.wtf's profile or another arbitrary directory.

No credentials are tracked by Git. The source export did include ignored, credential-named local artifacts (`QLOO-API KEY.txt`, `API KEYS.docx`, and `qloo-source.tar`); their contents were not printed, and they must be removed locally before sharing or publishing this folder. The Qloo API has not yet been integrated into runtime code; a Qloo key can be configured when that feature is implemented, without reusing the Gemini credential.

## Optional connectors

Native connectors remain in the Jarvis execution flow. They require the separately deployed connector relay and the user's own provider authorization. Set both environment variables before starting QLOO:

```text
QLOO_CONNECTOR_BACKEND_URL=https://your-qloo-connector-api/
QLOO_CONNECTOR_WEB_URL=https://your-qloo-connector-site/
```

The two values must be supplied together and use HTTPS (or loopback for local development). The relay supplies connector catalog, OAuth connection, account-scoped access, and native Gmail/Calendar/provider operations. Gemini inference remains on the local Personal API key even when the relay is configured; connector sign-in does not activate credits or managed AI. Without a relay, Jarvis, desktop tools, and local browser actions remain available, while provider connectors report that authorization is unavailable.

Optional relay source is in `staging/backend/KeyboardWtf.Staging.Api/`; it is a separate service and is not started or published by the desktop project. Review its deployment configuration before hosting it. Connector provider scopes and account access are controlled by that service and each provider. The connector-site staging builder requires QLOO-owned Firebase web configuration (`QLOO_FIREBASE_API_KEY`, `QLOO_FIREBASE_AUTH_DOMAIN`, `QLOO_FIREBASE_PROJECT_ID`, `QLOO_FIREBASE_STORAGE_BUCKET`, `QLOO_FIREBASE_MESSAGING_SENDER_ID`, `QLOO_FIREBASE_APP_ID`). The Firebase web API key is public client configuration, not a provider secret; restrict it to the owned project and required APIs. Vercel deployment also requires `QLOO_VERCEL_PROJECT_ID` and `QLOO_VERCEL_SCOPE`. The builder does not deploy unless explicitly passed `--deploy`.

## Chrome Helper

Use **Prepare QLOO Chrome Helper** in Settings, then open `chrome://extensions`, enable Developer mode, and load the prepared folder as an unpacked extension. QLOO has a distinct native-host name, pipe, extension key, and extension ID. It does not register over the keyboard.wtf Helper during startup.

## Architecture map

- `src/Program.cs` — single-instance tray entry point and Jarvis-only menu.
- `src/Services/KeyboardWtfApp.cs` — application composition, two hotkeys, connector relay binding, and service lifecycle.
- `src/Services/GeminiLiveConversationService.cs` — Gemini Live speech, conversation/tool routing, cancellation, selected-text translation ownership, and session context.
- `src/Services/JarvisAutomationService.cs` and `src/Services/BrowserAutomationV2/` — validated desktop actions, Chrome Helper coordination, browser planning, and safety checks.
- `src/Services/Connectors/` — connector intent, task lifecycle, retained provider references, and authenticated native operations.
- `src/Services/Ai/Backends/` — Personal API credential vault, DPAPI protection, policy router, Gemini transports, and native key-entry settings controller.
- `src/Services/PaidStaging/` — connector relay HTTP/OAuth session transport only in QLOO; its managed inference fallback is unreachable because QLOO pins the PersonalOnly backend.
- `src/Services/SettingsService.cs` and `src/Resources/web/settings.html` — QLOO-local preferences and settings UI. Only Jarvis and Cancel hotkeys are persisted/registered.
- `extensions/keyboard-wtf-browser-helper-v2/` — QLOO-branded unpacked Chrome Helper source.
- `staging/backend/KeyboardWtf.Staging.Api/` — optional connector relay service source.
- `tests/KeyboardWtf.Tests/` — existing focused regression tests for shared Jarvis, connectors, Personal API policy, and related behaviors.

## Runtime flow

1. Ctrl+Alt+Q starts Jarvis and opens the microphone.
2. Gemini Live receives voice input over the Personal API transport; tool requests are validated and routed to the existing desktop, browser, or connector executor.
3. Connector calls use the authenticated relay only when it is configured and signed in. Verified provider resource identifiers and references remain account-scoped within the connector/session layer.
4. Tool receipts return to the same live conversation; speech and the existing dynamic activity UI finish through their normal lifecycle.
5. Ctrl+Alt+X cancels the active operation and ends its session-owned work.

## Known boundaries

- Physical microphone/speaker behavior and visible Chrome rendering must be verified on the target PC; source builds alone do not prove those outcomes.
- Connector operations require a compatible relay deployment, valid provider OAuth grants, and provider-side availability.
- The Chrome Helper must be loaded by the user in Chrome. Browser actions remain restricted by the existing active-window and tab safety checks.
- This project has not been code-signed or published as a public installer. OAuth review, signing, deployment, and release packaging are separate release tasks.
