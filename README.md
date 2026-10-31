# QLOO Jarvis: a desktop assistant that learns your taste

QLOO Jarvis is the Jarvis mode of [keyboard.wtf](https://github.com/UnknownGod2011/keyboard.wtf): a Windows tray assistant you summon with a shortcut and talk to. It answers questions, reads the screen, and operates Windows apps and Chrome. This hackathon version adds two things:

- an **adaptive taste memory** that learns what you like from what you say and choose, and
- **Qloo's cultural intelligence** (the Taste AI Insights API), which grounds personal decisions in real cultural affinity data.

Ask "which of these two shirts suits me?", "find a film I'd actually enjoy" or "Inception or Mamma Mia tonight?" and Jarvis compares the options against your remembered preferences, asks Qloo how those options relate to things you already like, picks one, and says why. Tell it "not that one" and the next decision changes.

**Hosted demo (no install, no keys needed):** https://qloo-jarvis-taste.vercel.app. It runs the same decision engine in the browser with a demo persona, side by side with Qloo OFF and ON.

## The problem

General assistants are good at reasoning and bad at taste. Asked to choose, they fall back on popularity ("Inception is a great pick… or Mamma Mia if you prefer musicals") or refuse to choose. They also forget what you told them yesterday. QLOO Jarvis keeps a private, evidence-weighted profile of your taste and uses Qloo to connect it to the options in front of you.

## How it works

```
You (voice/text, optional screen) ──► Gemini Live (Jarvis): intent, vision, planning, final answer
                                           │  calls tools
                     ┌─────────────────────┼──────────────────────────┐
              inspect_screen         taste_decide                taste_observe / taste_feedback / taste_profile
           (Gemini vision; image      │                                │
            never leaves Gemini)      ▼                                ▼
                              TasteDecisionEngine ◄──────────── TasteProfileStore (%LOCALAPPDATA%\QLOO\taste-profile.json)
                                      │ named entities + style words only
                                      ▼
                              QlooClient ──► https://hackathon.api.qloo.com  (/search, /v2/tags, /v2/insights)
                              cache · de-dup · per-decision cap · daily budget · retry/timeout
```

- **Gemini stays the brain.** Gemini Live understands the request, looks at the screen (existing `inspect_screen` tool), extracts the options and their attributes, makes the final choice and explains it.
- **Qloo is a specialised tool, not a chatbot.** For taste decisions Gemini calls `taste_decide`. The engine resolves the things you like (films, brands, artists, places) and style words (e.g. "minimalist") to Qloo IDs with `/search` and `/v2/tags`, then scores *your own options* with `/v2/insights` using `filter.results.entities` and `feature.explainability`. With no options it asks Qloo for recommendations and excludes what you dislike.
- **Qloo is only called when it helps.** Maths, coding, logistics and factual questions never reach Qloo. Unbranded items ("a navy shirt") are judged from taste memory and Gemini's vision; Qloo does not judge colour or fit.
- **Honest output.** Each brief says whether it was Qloo-grounded, which preferences drove it, the Qloo affinities, and what is missing. Confidence is high/medium/low, never a fake percentage.

Details: [docs/QLOO_ARCHITECTURE.md](docs/QLOO_ARCHITECTURE.md) and the Qloo reference library in [docs/qloo/](docs/qloo/README.md).

## How Jarvis learns your taste

`TasteProfileStore` (`src/Services/Taste/`) keeps evidence, not a favourites list:

| Kind | Example | Weight per observation |
|---|---|---|
| Explicit | "I love minimalist clothes" | 1.0 |
| Inferred from a choice | you picked the navy shirt | 0.6 |
| Negative | you rejected the neon tee | 0.6 against |
| Inferred mention | "…like Arrival, but…" | 0.35 |
| Contextual | "for weddings I go formal" | stored separately, used only for that occasion |
| Temporary | "comedy tonight" | expires (default 14 days), never changes long-term taste |

- Confidence = 1 − e^(−1.2·|net evidence|), with recency decay (half-life 365 days for explicit, 120 for inferred). Repeated choices raise it; one explicit statement starts "strong".
- A contradicting explicit statement halves the older opposite evidence instead of overwriting it, and the preference is marked "recently changed". A correction from Settings is authoritative.
- The same observation within 24 hours is counted once.
- Health, religion, politics, sexuality, ethnicity, finances and identifiers are refused, never stored.
- You can inspect, flip ("actually I dislike it"), forget or clear everything in **Settings → Qloo cultural intelligence**, or by voice ("what do you know about my taste?", "forget that I like sushi").

## Qloo ON / OFF

- **On by default.** Settings → **Qloo cultural intelligence** has a single toggle. It applies from the next Jarvis conversation.
- **OFF = the original Jarvis.** The Gemini tool list and system instruction are exactly the pre-Qloo ones (a unit test checks the serialized tool list is unchanged).
- **ON without a key** still uses taste memory and says the result is not Qloo-grounded, with a setup hint.
- **Qloo down, quota used up, or 401** → the same decision falls back to taste memory and ordinary reasoning, flagged as not Qloo-grounded.

## Setup (Windows 10/11 x64)

Requirements: .NET 8 SDK, a Gemini API key, a Qloo hackathon API key (optional but needed for Qloo grounding), a microphone and speakers. Chrome + the QLOO Helper only for browser automation.

```powershell
dotnet restore .\src\KeyboardWtf.csproj
dotnet run --project .\src\KeyboardWtf.csproj -c Release
```

The tray process is `QLOO.exe`. On first run Settings opens in your browser.

1. **Gemini key:** Settings → *Personal Gemini API* → *Add Gemini API key* (native Windows dialog, stored with DPAPI).
2. **Qloo key**, any one of:
   - Settings → *Qloo cultural intelligence* → *Add or replace Qloo API key* (native dialog, stored with DPAPI in `%LOCALAPPDATA%\QLOO\qloo-api-v1.dat`);
   - `QLOO.exe --import-qloo-key "C:\path\to\key.txt"` (stores the first key-shaped token from a local file, prints nothing);
   - the `QLOO_API_KEY` environment variable (developer fallback; see [.env.example](.env.example)).

   Keys are never sent to the settings page, prompts or logs. The Qloo key is sent only in the `X-Api-Key` header to `https://hackathon.api.qloo.com`.

### Keyboard shortcuts

| Action | Default | Fallback if taken |
|---|---|---|
| Start / end Jarvis | `Ctrl+Alt+Q` | `Ctrl+Alt+J` |
| Cancel current action | `Ctrl+Alt+X` | `Ctrl+Alt+Escape` |
| Open QLOO Settings | `Ctrl+Alt+P` | `Ctrl+Alt+Shift+P` |

`Ctrl+Alt+P` avoids Windows-reserved combinations and keyboard.wtf's own defaults (K, D, Q, X and comma). Shortcuts live in `%LOCALAPPDATA%\QLOO\settings.json` (`Hotkeys`) and can be changed through the settings API (`POST /api/settings` with `hotkeys`), which rejects duplicates and rolls back if Windows refuses a combination.

### Qloo quota protection

Per decision at most 8 Qloo calls; a persisted daily budget (default 150, `QlooDailyBudget` in settings.json); a private on-disk cache (`/search` and `/v2/tags` 30 days, `/v2/insights` 7 days; allowed by Qloo's hackathon terms); identical in-flight requests share one call; 8 s timeout; one retry only for 429/5xx/timeouts; a 401 stops calls until the key changes. Settings shows today's requests and cache hits.

## Tests

```powershell
dotnet test .\tests\KeyboardWtf.Tests\KeyboardWtf.Tests.csproj -c Debug --filter "FullyQualifiedName~QlooTasteTests"
node --test "demo/web/test/*.test.mjs"
```

The Qloo tests use synthetic fixtures only; Qloo's hackathon terms forbid storing real response data in a public repository. Results of the internal tests and the live OFF/ON comparison are in [docs/qloo/EVALUATION.md](docs/qloo/EVALUATION.md).

## Repository layout

- `src/Services/Qloo/`: `QlooClient` (HTTP, validation, cache, budgets) and `QlooCredentialStore` (DPAPI key).
- `src/Services/Taste/`: `TasteProfileStore` (memory), `TasteDecisionEngine` (generic decisions), `TasteJarvisTools` (Gemini tool declarations, prompt section, execution).
- `src/Services/GeminiLiveConversationService.cs`: Gemini Live session; taste tools are attached only when the toggle is on.
- `src/Services/WebSettingsService.Qloo.cs`, `src/Resources/web/settings.html`: settings routes and UI.
- `demo/web/`: hosted web demo (Vercel, zero dependencies) with its own README.
- `docs/qloo/`: researched Qloo API reference library. `docs/QLOO_ARCHITECTURE.md`: architecture and checklist.
- Everything else is the existing Jarvis: desktop/browser automation (`JarvisAutomationService`, `BrowserAutomationV2/`), connectors (`Connectors/`, optional relay in `staging/backend/`), Personal Gemini backend (`Ai/Backends/`), Chrome Helper (`extensions/`).

## Optional connectors and Chrome Helper

Native Gmail/Calendar connectors need a separately deployed relay: set both `QLOO_CONNECTOR_BACKEND_URL` and `QLOO_CONNECTOR_WEB_URL` (HTTPS) before starting QLOO. Without them, Jarvis, desktop tools and local browser actions still work. For Chrome page actions use **Prepare QLOO Chrome Helper** in Settings and load the folder as an unpacked extension (`chrome://extensions`, Developer mode).

## Known limitations

- Qloo grounds named cultural entities (films, shows, artists, books, podcasts, games, brands, places, destinations) and style tags. It does not evaluate colour, fit, materials or price; those come from Gemini's vision and your taste memory.
- Qloo results depend on hackathon data coverage; unresolved names are reported, not guessed.
- Taste settings take effect from the next Jarvis conversation, because Gemini Live receives tools at session setup.
- The hosted demo keeps the taste profile in your browser; its rate limits and cache are per serverless instance.
- Microphone, speaker and real-time screen behaviour must be checked on the target PC; the automated tests cover logic, not audio hardware.

## License

MIT, see [LICENSE](LICENSE). Third-party components are listed in [NOTICES.md](NOTICES.md). Qloo and Gemini are third-party services used under their own terms; no Qloo response data is included in this repository.
