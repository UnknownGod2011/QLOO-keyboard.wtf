# QLOO Jarvis architecture: Gemini + Qloo taste intelligence

Status: implemented on `main` (desktop: `src/Services/Qloo/`, `src/Services/Taste/`; hosted demo: `demo/web/`).
Qloo API facts used here are documented, with sources, in [docs/qloo/](qloo/README.md).

## 1. Existing project architecture

QLOO Jarvis is a WinForms tray app (`QLOO.exe`, .NET 8, `src/KeyboardWtf.csproj`) exported from keyboard.wtf with only Jarvis mode and only the Gemini Personal API backend (see [Lineage](#lineage-and-isolation)).

- `Program.cs` takes a single-instance lock on `%LOCALAPPDATA%\QLOO\qloo.lock`, builds the tray icon and constructs `KeyboardWtfApp`.
- `KeyboardWtfApp` is the composition root (no DI container). It loads settings and stores, creates the Personal API vault and `AiBackendGateway`, `GeminiLiveConversationService`, `JarvisAutomationService`, `CommandRegistry`, `JarvisConnectorController`, browser automation, the overlay, `WebSettingsService` and `HotkeyService`.
- Settings are an embedded HTML page served by a loopback `HttpListener` (`WebSettingsService`, `src/Resources/web/settings.html`). State-changing requests need a per-process action token and same-origin headers.
- Persistence is JSON in `%LOCALAPPDATA%\QLOO` with atomic temp-file writes; secrets use DPAPI.

## 2. Existing agent request and execution flow

1. The Jarvis shortcut (`Ctrl+Alt+Shift+Q`) → `HotkeyService` → `CommandRegistry.ToggleJarvisMode` → `GeminiLiveConversationService.StartAsync`.
2. The service opens a Gemini Live WebSocket (`gemini-3.1-flash-live-preview`) through the Personal API transport and sends one `setup` message: voice config, `tools = BuildTools()`, and `systemInstruction = BuildSystemInstruction()` (behaviour rules + saved workflows, link aliases and intent memory).
3. Microphone audio (16 kHz PCM) streams to Gemini; Gemini answers with audio and, when needed, `toolCall` frames.
4. `LiveToolCallDispatcher` queues each call, applies the duplicate guard and calls `ExecuteToolAsync`. Memory tools are handled in the service; everything else goes to `CommandRegistry.ExecuteJarvisToolAsync` → routing, consent (`JarvisPermissionPolicy`) → `JarvisAutomationService` / connectors / browser automation.
5. Results return as `toolResponse.functionResponses`; Gemini speaks the outcome. The overlay shows phase text via `KeyboardWtfState.SetUi`.
6. Screen questions use `inspect_screen`: one approved screenshot is analysed by Gemini (`GeminiScreenGuidanceService`); only the textual guidance returns to the Live session.

## 3. Qloo API architecture and relevant capabilities

Hackathon keys work only against `https://hackathon.api.qloo.com`, authenticated with the `X-Api-Key` header; `/v2/insights` is a GET with query parameters and requires `filter.type`. We use:

| Endpoint | Use in Jarvis |
|---|---|
| `GET /search?query&types&take` | Resolve a liked or candidate entity name to a Qloo `entity_id` |
| `GET /v2/tags?filter.query&take` | Resolve a style word to a tag ID; keep tags whose `parents[].type` matches the target type |
| `GET /v2/insights` + `filter.results.entities` + `feature.explainability` | Score the user's own options against their taste signals (per-option `query.affinity`, contributing signals) |
| `GET /v2/insights` + `filter.exclude.entities` (+ `filter.location.query` for places) | Recommend when the user gave no options |

Not used (and why) is listed in [docs/qloo/API_MAP.md](qloo/API_MAP.md): audiences/demographics (we never infer demographics), trends and compare (not needed for a choice), legacy `/recs`.

## 4. Gemini and Qloo responsibilities

| Gemini (Live + vision) | Qloo (via `QlooClient`) | Local engine (`Taste*`) |
|---|---|---|
| Understand the request, decide whether it is a taste decision, look at the screen, extract options and attributes, final choice and explanation, ask clarifying questions | Cultural affinity between the user's liked entities/style tags and candidate entities; recommendations | Remember and weight preferences, match option attributes, call Qloo within budget, combine evidence, record decisions, learn from feedback |

Qloo never sees screenshots, names of people, or free-form personal text. Gemini never invents Qloo numbers: it receives them in the tool result.

## 5. Connector design

Qloo is a **local Jarvis tool path**, not a relay connector: it needs no OAuth and must work without the connector relay. `TasteJarvisTools` provides four Gemini function declarations — `taste_decide`, `taste_observe`, `taste_feedback`, `taste_profile` — appended to the original tool list only when the toggle is on (`GeminiLiveConversationService.BuildToolsWith`). `ExecuteToolAsync` routes `taste_*` calls to `TasteJarvisTools` before the generic executor, like the existing memory tools.

## 6. Preference-memory architecture

`TasteProfileStore` → `%LOCALAPPDATA%\QLOO\taste-profile.json` (atomic writes, last 25 decisions kept for feedback).

- **Preference** = domain + attribute + value + context (+ temporary flag), holding a list of **observations** {weight, polarity, time, source, quote, fingerprint, decision}.
- **Sources and weights:** explicit statement 1.0, correction 1.0, choice 0.6, rejection 0.6 (negative), inferred mention 0.35.
- **Kinds:** explicit (any explicit/correction evidence), inferred, contextual (non-empty context), temporary (expires, default 14 days).
- **Confidence** = 1 − e^(−1.2·|Σ polarity·weight·0.5^(age/half-life)|); half-life 365 days for explicit, 120 for inferred/contextual. Labels: strong ≥ 0.6, moderate ≥ 0.4, else tentative.
- **Contradiction:** a new explicit statement of opposite polarity halves older opposite evidence and sets `RecentlyChanged`; a Settings correction removes contrary evidence (authoritative).
- **Duplicates:** same fingerprint within 24 h is ignored. **Decay** is computed at read time. **Capacity:** 300 preferences; weakest non-explicit ones are trimmed first.
- **Privacy guard:** health, religion, politics, sexuality, ethnicity, immigration, criminal, finances and identifiers are refused.
- **Retrieval:** `Relevant(domain, context)` returns same-domain plus lifestyle/general (and fashion↔brands, movies↔tv) preferences; contextual ones only when the context matches.
- **Prompt digest:** the top 12 preferences are appended to the system instruction (data, not instructions) only when the toggle is on.
- **User control:** Settings lists every preference with strength, kind and evidence count; "Actually I dislike it", "Forget", "Clear taste memory". Voice: `taste_profile list/forget/correct`.

## 7. Decision-engine architecture

`TasteDecisionEngine.DecideAsync(question, domain, options[], constraints, context, location)` — one generic pipeline, no per-category prompts:

1. Normalize the domain; map to a Qloo type (movies→movie, tv→tv_show, music→artist, books→book, podcasts→podcast, games→video_game, dining/hotels→place, travel→destination, fashion/brands→brand, people→person). Others (technology, general) → no Qloo.
2. Retrieve relevant preferences; match each option's label, description, entity and attributes (`colour:navy`) against them (+/−, weighted by confidence; direct entity match ×2; contextual ×1.25).
3. If Qloo is relevant, enabled, keyed and not locked out: resolve up to 4 option entities, 4 liked entities and 2 style tags (cap 8 calls including the insights call). An option entity is never its own signal.
4. Choose/rank mode: one `/v2/insights` call with `filter.results.entities=<option ids>`; recommend mode: `filter.exclude.entities` for liked/disliked entities, places need a location, results matching strong dislikes are dropped.
5. Combine: score = preference score + 1.5 × (affinity − mean affinity of scored options); options with `meets_constraints=false` rank last. Confidence high/medium/low from margin and evidence agreement.
6. Record the decision (options + attributes) for `taste_feedback`; return a brief with `decision_id`, `qloo.grounded`, reason, calls/cache hits, signals, preferences considered, ranking with evidence lines, top choice, confidence, caveats, missing information, and an instruction telling Gemini to decide, cite, and separate Qloo data from its own judgement.

`taste_feedback(decision_id, chosen_label, rejected_labels)` turns the chosen option's entity and attributes into `choice` observations and rejected ones into `rejection` observations, so the next comparison changes.

## 8. Screenshot-based decision flow

1. User: "Which of these two shirts fits my taste?" with a shop page on screen.
2. Gemini calls `inspect_screen` (existing consent rules apply). The screenshot goes only to Gemini; the guidance text describes each option.
3. Gemini calls `taste_decide` with domain `fashion` and options such as `{label:"A: navy linen shirt", attributes:["colour:navy","style:minimalist","material:linen"], entity_name:"Uniqlo"}`.
4. Taste memory matches colour/style; Qloo scores named brands against liked brands and style tags. Unbranded options are judged from memory and vision only, and the brief says so.
5. Gemini answers with one choice and the reasons; "I'll take A" → `taste_feedback`.

## 9. Settings and feature-toggle implementation

- `AppSettings.QlooEnabled` (default `true`) and `QlooDailyBudget` (default 150) in `settings.json`; `SettingsService.SaveQlooEnabled` commits atomically before publishing the value.
- `WebSettingsService.Qloo.cs`: `POST /api/qloo/state` and `POST /api/qloo/action` (`enabled`, `add-key`, `remove-key`, `forget`, `correct`, `clear-profile`, `clear-cache`), protected by the existing action-token guard and update-work admission.
- The Qloo key: `QlooCredentialStore` (DPAPI, `qloo-api-v1.dat`), native `QlooKeyEntryDialog`, `QLOO.exe --import-qloo-key <file>`, or `QLOO_API_KEY`. The page receives only `configured`, `keySource` and a 4-character mask.
- Toggle semantics: read at Gemini Live session setup. OFF → `BuildToolsWith(null)` and no prompt section, which serialize exactly as before (unit-tested). ON → original declarations first, taste declarations appended. Switching the toggle ends a running conversation (`KeyboardWtfApp.EndJarvisConversationIfActiveAsync`) so the next one uses the new mode. The switch lives in Settings → Connectors → Qloo.
- Hotkeys: Jarvis defaults to `Ctrl+Alt+Shift+Q` (fallback `Ctrl+Alt+Shift+J`) because `Ctrl+Alt+Q`, `Ctrl+Alt+J` and `Ctrl+Alt+Space` belong to keyboard.wtf; a one-time migration (`HotkeysRevision`) moves older QLOO settings off `Ctrl+Alt+Q`. `HotkeySettings.OpenSettings = "Ctrl+Alt+P"` is now persisted and registered (fallback `Ctrl+Alt+Shift+P`), handled by `CommandRegistry.OpenSettings` → the same `KeyboardWtfApp.OpenSettings` used by the tray. Cancel keeps `Ctrl+Alt+X` with a new non-conflicting fallback `Ctrl+Alt+Shift+X`.

## 10. Fallback architecture

| Situation | Behaviour |
|---|---|
| Toggle off | Original Jarvis; taste tools not offered; any stale call returns "turned off" |
| No key | Decision from taste memory, `qloo.grounded=false`, setup message |
| 401 | `QlooClient` locks out until the key changes; taste-memory fallback |
| 403 / 400 / invalid params | Error code in the brief; taste-memory fallback |
| 429 / 5xx / timeout | One retry with backoff (honours `Retry-After` ≤ 3 s); then fallback |
| Daily budget or per-decision cap reached | Fallback with a clear caveat |
| Non-taste question | No Qloo call by design |

## 11. API quota strategy

Private on-disk cache keyed by canonical sorted query (search/tags 30 days, insights 7 days; Qloo permits private caching without time limit), in-flight de-duplication (`Lazy<Task>` per request), per-decision cap of 8 network calls (cache hits are free), persisted daily budget (`qloo-usage.json`), early parameter validation so invalid requests never reach the network, 8 s timeout, bounded retry, and a 401 lockout. The hosted demo applies the same ideas in memory per serverless instance, plus a per-IP limit (10 decisions / 10 min).

## 12. Files modified

- `src/Services/GeminiLiveConversationService.cs` — `BuildToolsWith`, `OriginalFunctionDeclarations`, `AttachTasteTools`, prompt section, `taste_*` routing.
- `src/Services/KeyboardWtfApp.cs` — construct store/client/tools, attach, UI status, dispose.
- `src/Services/SettingsService.cs` — `QlooEnabled`, `QlooDailyBudget`, `SaveQlooEnabled`, persisted `OpenSettings` hotkey + migration.
- `src/Services/HotkeyService.cs` — register/replace the Open Settings hotkey.
- `src/Services/WebSettingsService.cs` — `/api/qloo/*` routes and update-work classification; hotkey default.
- `src/Resources/web/settings.html` — Qloo entry inside the Connectors card (On switch, key, usage, collapsible taste-profile editor), shortcut note.
- `src/Services/Ai/Backends/PersonalKeyImporter.cs` (new) + `src/Program.cs` — `--import-gemini-keys` bulk import into the Personal API key pool, probing each key with a real `generateContent` call.
- `src/Program.cs` — `--import-qloo-key`.
- `README.md`, `.env.example`, this document.

## 13. New files

- `src/Services/Qloo/QlooClient.cs`, `src/Services/Qloo/QlooCredentialStore.cs`
- `src/Services/Taste/TasteProfileStore.cs`, `TasteDecisionEngine.cs`, `TasteJarvisTools.cs`
- `src/Services/WebSettingsService.Qloo.cs`
- `tests/KeyboardWtf.Tests/QlooTasteTests.cs`
- `demo/web/**` (hosted demo), `docs/qloo/**` (reference library, evaluation)

## 14. Testing and evaluation strategy

- Unit tests with synthetic fixtures (`QlooTasteTests`, 24 tests): request construction and auth header, parameter validation, caching, de-duplication, retry/401/budgets, missing key, DPAPI key storage, memory maths (explicit/inferred/negative/contextual/temporary, contradiction, duplicates, sensitive refusal, persistence), engine (no Qloo for technical questions, candidate scoring, outage fallback, missing key, feedback changes the next decision), and the OFF tool list being unchanged.
- Regression: the inherited focused suites (hotkeys, settings, Gemini Live, Personal API, system instruction) are compared against the baseline commit; see [docs/qloo/EVALUATION.md](qloo/EVALUATION.md).
- Web demo: 30 offline `node:test` tests in mock mode.
- Live evaluation: a small, paced set of identical OFF/ON scenarios through the shared engine with the real hackathon API (results summarised without storing raw Qloo data).

## 15. Known technical risks and limitations

- Taste tools and the prompt digest are fixed at Live session setup; toggling mid-conversation takes effect next session.
- Qloo coverage varies; unresolved names are reported. Style words map to tags only when a tag with the right parent type exists.
- Affinity differences between two options can be small; the engine treats small margins as low confidence rather than inflating them.
- The free-tier Gemini key can hit per-minute limits in the hosted demo; it uses a lighter model for extraction and backs off on 429.
- Physical audio, microphone and live screen flows still need manual verification on the target PC; automated tests cover logic and request construction.
- The inherited test suite has pre-existing failures unrelated to this work (recorded in the evaluation document).

## Implementation checklist

- [x] Qloo documentation researched; local library in `docs/qloo/`
- [x] `QlooClient` with documented endpoints, validation, cache, de-dup, budgets, retry, 401 lockout
- [x] DPAPI Qloo key storage, native dialog, file import, env fallback
- [x] Adaptive taste memory with kinds, confidence, decay, contradictions, duplicates, privacy guard, persistence
- [x] Generic decision engine with candidate scoring and recommendations
- [x] Jarvis tools + system prompt section, attached only when enabled; OFF unchanged
- [x] Settings toggle, key management, usage, taste-profile editor
- [x] Second global shortcut (Open Settings, `Ctrl+Alt+P`)
- [x] Unit tests; demo tests; live OFF/ON evaluation
- [x] Hosted demo deployed (https://qloo-jarvis-taste.vercel.app)
- [ ] Manual voice/screen session on the target PC with both keys configured (needs the user's microphone and Gemini key in Settings)

## Lineage and isolation

- Desktop source: read-only `keyboard.wtf` checkout at `877cf745446668f8b3d7d81c77b596a881550aa1`; connector implementation from `keyboard.wtf-connectors` at `19f6324ac84eee6982433ff8f6eb1420d02dc7de` (`.qloo-origin.json`). The first commit on `main` is that baseline; all Qloo work is in later commits.
- QLOO uses its own executable, DPAPI entropy, profile (`%LOCALAPPDATA%\QLOO`), instance lock, logs, TTS paths, connector session and Chrome native host/pipe/extension ID, so it can run beside keyboard.wtf.
- Gemini inference uses `PersonalApiVault` with the PersonalOnly policy. The optional connector relay (`staging/backend/`) is separate and only enabled when `QLOO_CONNECTOR_BACKEND_URL` and `QLOO_CONNECTOR_WEB_URL` are both set.
- Credential-named local files (`API KEYS.docx`, `QLOO-API KEY.txt`, `qloo-source.tar`) are git-ignored and were never committed.
