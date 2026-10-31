# Evaluation: internal tests and Qloo OFF vs ON

Date: 2026-10-31. Machine: the developer's Windows 11 PC (keyboard.wtf was also running). All Qloo payloads in tests are synthetic; live results below are summarised, not stored as raw responses ([hackathon guide](https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide)).

## 1. Automated tests

| Suite | Command | Result |
|---|---|---|
| Desktop Qloo + taste memory (`QlooTasteTests`) | `dotnet test tests/KeyboardWtf.Tests -c Debug --filter FullyQualifiedName~QlooTasteTests` | 24 passed, 0 failed |
| Inherited focused regression (hotkeys, settings, Gemini Live, Personal API, system instruction) | same project, filter `Hotkey|Settings|GeminiLive|PersonalApi|Qloo|SystemInstruction` | 291 passed, 21 failed — the same 21 tests fail on the untouched baseline commit `88c4f5b`, so 0 regressions. One inherited test pinned the old hotkey fallbacks and was updated to the new keyboard.wtf-safe values. |
| Hosted demo (`demo/web`) | `node --test "demo/web/test/*.test.mjs"` | 30 passed, 0 failed (offline mock mode) |

The 21 pre-existing failures come from the keyboard.wtf export (for example tests that expect keyboard.wtf's five hotkeys, its paid-account settings page or production-site text). They are unrelated to Qloo and were left as found.

What the Qloo tests cover: hackathon base URL and `X-Api-Key` header (key never in the URL), documented parameters for `/search`, `/v2/tags`, `/v2/insights`; rejection of unsupported types, destination without an entity signal, location filters on non-place types and undocumented parameters; private cache and cache hits not consuming the per-decision budget; concurrent identical requests sharing one HTTP call; one retry on 503; no retry and a lockout on 401; per-decision cap and persisted daily budget; no network call without a key; DPAPI key round trip and masking; explicit vs inferred confidence and growth with repeated choices; contradictions halving old evidence; corrections; contextual and temporary separation and expiry; duplicate suppression; sensitive-trait refusal; persistence across restart; no Qloo call for technical questions; candidate scoring with `filter.results.entities`; outage fallback; missing-key fallback; feedback changing the next decision; taste tools refused when the toggle is off; and the Qloo-OFF tool list serializing exactly like the original.

## 2. Windows app smoke test (QLOO.exe Debug build)

| Check | Result |
|---|---|
| Startup, tray, settings server | Started; settings served on `localhost:8091` (8090 was taken by keyboard.wtf) |
| Qloo key import | `QLOO.exe --import-qloo-key <file>` exit 0; Settings reports a DPAPI profile key, 4-character mask only |
| `/api/qloo/state`, toggle off → on | Works; unauthenticated action rejected with 403 |
| Settings page | Qloo card and taste-profile editor rendered |
| Shortcuts with keyboard.wtf running | Jarvis `Ctrl+Alt+Shift+Q`, Settings `Ctrl+Alt+P` registered; Cancel fell back to `Ctrl+Alt+Shift+X` because keyboard.wtf owns `Ctrl+Alt+X` |
| Simulated key presses | `Ctrl+Alt+P` opened Settings; `Ctrl+Alt+Shift+Q` started Jarvis, which stopped with "No enabled, authenticated Personal API key" because the Gemini key has not been added to this new QLOO profile yet |

### End-to-end desktop run with the key pool (after importing 10 Gemini keys and the Qloo key)

- Key pool: 10 keys stored, 6 active. The other 4 were rejected by Google for both REST and Live with "The Gemini API free tier is not available for this project" (a project billing setting; no request format changes that) and are stored inactive. The `AQ.` keys work with the documented `x-goog-api-key` header for REST and for the Live WebSocket.
- Typed turns through the normal Jarvis Live route (`/api/jarvis/text-turn`): the Live session connected in about 1.5 s; "remember my taste: I love sci-fi films like Interstellar and Arrival, and I can't stand horror" produced one `taste_observe` call that stored four strong preferences; "Inception or Mamma Mia tonight?" produced one `taste_decide` call with 6 Qloo requests (4 `/search`, 1 `/v2/tags`, 1 `/v2/insights`) and a Qloo-grounded brief; "goodbye" ended the session. The test preferences were cleared afterwards.

Not verified automatically: a spoken (microphone) conversation and an on-screen `inspect_screen` comparison.

## 3. Live comparative evaluation (Qloo OFF vs ON)

Method: identical requests through the shared decision engine (`demo/web/lib/engine.js`) with the live hackathon Qloo API and Gemini (`gemini-3.5-flash-lite` for both modes in the final run). OFF = original Jarvis behaviour (Gemini only, no taste memory, no Qloo). ON = taste memory + Qloo + Gemini. Profile: the demo persona (likes Interstellar, Arrival, Blade Runner 2049, minimalist style, navy and earth tones, Uniqlo, Patagonia; dislikes loud logos, neon, horror). Gemini calls were paced at 13 s to respect free-tier limits.

| Scenario | Qloo OFF | Qloo ON | What Qloo contributed |
|---|---|---|---|
| "Inception or Mamma Mia! tonight?" | Inception, low confidence; generic ("if you prefer a musical…") | Inception, high confidence, Qloo-grounded; affinity 0.854 vs 0.676 from 3 liked films; 6 Qloo calls | Same pick, now justified by the user's taste with measurable affinity and explainability links |
| Two shirts (navy minimalist Uniqlo vs neon logo tee) | A, low confidence; "I don't know your taste" (an earlier run answered "insufficient information") | A, high confidence; taste memory +2.74 vs −1.37; Qloo brand affinity 0.769 vs 0.755 | Marginal: the choice came from taste memory; Qloo's brand scores were nearly tied and were reported as such |
| "Find a movie I would genuinely enjoy tonight" | Declined: asked for options or preferences | The Martian, Qloo-grounded; candidates Passengers 0.923, The Martian 0.915, Logan 0.913, Ex Machina 0.911 | Material: turned a refusal into a grounded, personal recommendation (1 network call; the rest came from cache) |
| "What is 17% of 2,340?" | — | 397.8; 0 Qloo calls | None, correctly not called |
| Qloo outage (simulated) | — | Inception, flagged "NOT Qloo-grounded", decided from taste memory | Graceful fallback |
| Feedback learning: jacket A (olive minimalist) vs B (red patterned), empty profile → two "chose red patterned" feedbacks → same question | before: A, low confidence, no memory | after: B, high confidence (taste memory +1.53) | Learning came from taste memory; Qloo was not called because the jackets are unbranded |

Findings, stated plainly:

- Qloo changed the outcome most for open-ended requests (recommendations) and for choices between named cultural entities. For unbranded visual items it adds little; the taste memory and Gemini carry those decisions, and the brief says so.
- With Qloo ON the answers cite the user's own preferences and Qloo's numbers instead of hedging between options.
- Close Qloo scores (0.769 vs 0.755) are not presented as a strong signal.
- Gemini free-tier quota was exhausted once during testing (HTTP 429 on `gemini-3.5-flash`). The hosted demo now uses `gemini-3.5-flash-lite` and backs off on 429; Qloo itself never failed during the evaluation.

Approximate live Qloo usage for all verification and evaluation on 2026-10-31: about 60 requests, many later answered from cache.

## 4. Hosted demo check

`https://qloo-jarvis-taste.vercel.app`: `/api/health` reports Qloo and Gemini configured (booleans only). The OFF/ON check for the movie scenario returned the same results as above; a repeat ON request was served entirely from the instance cache (0 Qloo calls, 6 cache hits).
