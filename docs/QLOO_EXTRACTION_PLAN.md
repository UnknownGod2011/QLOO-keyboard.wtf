# QLOO extraction plan

## Frozen source boundary

QLOO is an independent Git repository created from the tracked `19f6324ac84eee6982433ff8f6eb1420d02dc7de` connector source and the read-only base checkout `877cf745446668f8b3d7d81c77b596a881550aa1`. The original `keyboard.wtf` and `keyboard.wtf-connectors` checkouts are read-only inputs. No local profiles or installed binaries were copied. The source export did contain credential-named artifacts; `.gitignore` excludes them, but see the credential-artifact note in `QLOO_ARCHITECTURE.md` before sharing this folder.

## What must remain functional

- Gemini Live Jarvis conversation, microphone capture, playback, cancellation, and the existing Jarvis/Cancel shortcuts.
- Desktop and browser automation, including the Chrome Helper safety boundary.
- Native connector orchestration, account-scoped retained references, and connector activity UI.
- Personal Gemini API credential storage using the existing native dialog, validator, and Windows DPAPI mechanism.

## Deliberate QLOO boundaries

- QLOO must use its own `%LOCALAPPDATA%\\QLOO` profile, instance lock, logs, downloaded models, TTS assets, and Personal API vault.
- QLOO must use a distinct Chrome native-host name, pipe, extension key, and extension ID so installing it cannot redirect the original app's helper.
- QLOO initializes the existing AI gateway independently from the optional connector relay and forces `PersonalOnly` inference. Jarvis Live, translation, screen guidance, and browser planning bind to that gateway. The connector-session transport remains separate because native provider authorization and execution require it; the desktop UI exposes only connector sign-in/out, not managed AI, usage, credits, subscription, or billing.
- Smart Writing, local Dictation, their hotkeys, their tray/settings UI, and non-Gemini provider selection are removed from the QLOO product surface. Shared microphone and speech components remain only when Jarvis actually needs them.

## Verification gates

1. QLOO compiles without resolving source assets outside this directory.
2. A clean QLOO profile is created without reading the original profile or credentials.
3. Only Jarvis and Cancel are registered as global hotkeys, retaining their existing default values.
4. The packaged browser helper has QLOO-only host/extension identities.
5. Focused build and configuration checks pass. Live provider, microphone, connector-account, and rendered-browser tests require an independently entered QLOO key and account, and are reported separately.
6. Verify the staged PersonalOnly gateway owns all Jarvis AI paths before making any live provider claim.
