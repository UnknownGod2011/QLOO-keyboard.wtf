# QLOO Jarvis: hosted web demo

QLOO Jarvis is a Windows desktop voice assistant (Gemini Live + computer-use tools). This folder is a browser demo of its taste-aware decision engine, so judges can try it without installing the Windows app. It runs the same design:

1. Gemini reads the question (and up to 3 images) and extracts the options.
2. An adaptive **taste memory** and **Qloo Taste AI** build an evidence brief.
3. Gemini makes the final, cited decision.

The page shows, side by side:

| | Qloo OFF (original Jarvis) | Qloo ON (enhanced) |
|---|---|---|
| Inputs to the final decision | question + extracted options | question + options + engine brief (taste memory + Qloo evidence) |
| Badge | `Gemini only` | `Qloo-grounded`, or `Taste memory only (Qloo unavailable)` |
| Learns from feedback | no | yes ("I'm choosing this" / "Not for me") |

Click **How this was decided** on a result to see the evidence: the resolved Qloo entity names and IDs, Qloo affinity per option, the option's offset from the options' average, explainability links (for example "Interstellar 0.6"), the taste-memory matches, caveats and the per-request call log.

## Architecture

```
index.html, app.js, styles.css   UI (plain ESM, no CDNs, strict CSP)
lib/profile.js                   taste-profile maths (pure; imported by the browser AND the server)
api/decide.js                    POST /api/decide  (Vercel Node function)
api/health.js                    GET  /api/health  -> {ok, qlooConfigured, geminiConfigured, mock}
lib/engine.js                    decision engine: validate -> extract -> taste memory -> Qloo -> combine -> final
lib/qloo.js                      Qloo client: TTL cache, in-flight de-dup, caps, timeout, retry
lib/gemini.js                    Gemini REST client, prompts, defensive JSON parsing and normalizers
lib/fixtures.js                  SYNTHETIC mock data for DEMO_MOCK=1 and the tests
lib/http.js                      JSON helpers usable on Vercel and plain node:http
dev-server.mjs                   zero-dependency local server (static files + /api/*)
test/*.test.mjs                  node:test suites, fully offline (mock mode)
```

### Decision flow (`POST /api/decide`)

Request: `{question, optionsText?, images?: [{mimeType, data}] (max 3, each <= 1.5 MB base64), profile, mode: 'off'|'on', location?, extraction?, simulateQlooOutage?}`

1. **Extraction** (Gemini, both modes, same prompt) returns `{domain, task, options[{label, description, attributes, entity:{name,type}}], constraints, context, location}`. "Compare OFF vs ON" sends the OFF extraction back with the ON request, so both modes judge the exact same options.
2. **Mode `off`**: one Gemini call with only the question and options. No profile and no Qloo calls.
3. **Mode `on`**:
   - **(a) Domain to Qloo type.** movies→movie, tv→tv_show, music→artist, books→book, podcasts→podcast, games→video_game, dining/hotels→place, travel→destination, fashion/brands→brand, people→person. Technical and other questions make **no Qloo calls**.
   - **(b) Select preferences.** Keep preferences from the same domain (or a domain with the same Qloo type), plus `lifestyle`. Contextual preferences count only when the context matches. Expired temporary preferences are skipped.
   - **(c) Local matching.** Each option's label, description and attributes are compared with the preference values. Every match becomes a + or − evidence line weighted by confidence. A direct match on the option's entity counts double.
   - **(d) Qloo lookups.**
     - Resolve up to 4 liked entities with `/search` and up to 2 style words with `/v2/tags` (the tag's parents must include the target type). Resolve the option entities with `/search`.
     - **Candidate scoring:** one `/v2/insights` call with `filter.results.entities=<option ids>`. An option entity is never used as its own signal.
     - **Recommend mode:** `/v2/insights` with `take=5` and `filter.exclude.entities` (disliked and already-liked entities), plus `filter.location.query` for places. Results tagged with an explicit dislike (for example horror) are dropped and listed.
     - **Cold start** (no signals): no insights call, and the result says so.
   - **(e) Combine.** score = preference score + 1.5 × (Qloo affinity − the options' mean affinity). Options that fail a constraint rank last. Confidence is qualitative only: high, medium or low.
   - **(f) Final decision.** Gemini receives the question, the options and the brief. The prompt forbids claiming Qloo grounding when `qloo_grounded=false` and says that Qloo does not judge colour, fit or visual appearance. The server enforces this too: it removes Qloo citations from ungrounded answers and drops preference citations that are not in the brief.
4. **Response:** `{mode, extraction, engine:{qlooRelevant, qlooGrounded, reason, entityType, calls, cacheHits, signals, options:[{label, qlooEntity, affinity, prefScore, evidence}], recommendations?, warnings, ...}, decision:{choice, rationale, cited_preferences, cited_qloo, confidence, uncertainty}, timingMs}`

**Failures:**
- If Qloo fails, the decision continues on taste memory only, with `qlooGrounded=false` and a warning.
- If Gemini fails, the server returns HTTP 502 with a friendly message.
- If no Gemini key is set (and mock mode is off), the server returns HTTP 503.

### Taste profile (`lib/profile.js`)

- Each preference has the shape `{id, domain, attribute, value, polarity, kind: explicit|inferred|contextual|temporary, context, observations[], expiresAt?}`.
- **Confidence** = `1 − exp(−1.2·|net|)`, where `net = Σ polarity·weight·decay(age)`.
- **Weights:** explicit 1.0, choice feedback 0.6, inferred mention 0.35, rejection 0.6 (negative).
- **Half-lives:** explicit 365 days, inferred and contextual 120 days. Temporary preferences expire after 14 days by default.
- **Contradictions:** a new explicit statement with the opposite polarity halves the older opposite observations and marks the preference "recently changed". Old evidence is not simply overwritten.
- **Duplicates:** the same fingerprint within 24 hours is ignored, so double-clicking feedback does not count twice.
- **Context:** contextual preferences (for example "formal for weddings") are stored separately and never overwrite general ones.
- **Sensitive data is refused** with a visible message: health, religion, politics, sexuality, ethnicity or race, finances, contact details, immigration status and criminal record.
- **Demo persona "Alex":**
  - Likes Interstellar, Arrival and Blade Runner 2049.
  - Likes minimalist style, navy and earth tones, Uniqlo and Patagonia.
  - Dislikes loud logos, neon colours and horror.
  - For weddings, prefers formal.

## Privacy

- The taste profile lives in the browser (`localStorage`). It is sent with each `on` request for that one decision. The server keeps no copy, writes nothing to disk and never logs request bodies.
- Only short entity names (titles, brands) and style or tag words are sent to Qloo. Images, people's personal names, emails and location histories are never sent. A city name is sent only for place recommendations, and only if the user typed it.
- API keys are read from environment variables only. They are sent as headers (`X-Api-Key`, `x-goog-api-key`), never put in URLs, cache keys, logs or responses. `/api/health` reports only booleans.
- No real Qloo responses are stored in the repo. `lib/fixtures.js` is synthetic: the UUIDs are fake (`00000000-0000-4000-8000-0000000000NN`), the tags are invented (`urn:tag:demo:*`) and the affinities come from a toy formula. Qloo responses are cached in memory only.
- Strict Content-Security-Policy (`'self'` only), no third-party scripts or fonts.

## Quota protection (honest scope)

All of these limits are **per serverless instance**, in module memory. Vercel can run several instances, and every cold start begins with an empty cache and a fresh budget. For hard global limits, use a shared store such as Vercel KV or Upstash Redis.

- **TTL cache** for Qloo GETs, keyed by the canonical URL (sorted parameters, no key): `/search` and `/v2/tags` for 24 hours, `/v2/insights` for 6 hours. Concurrent identical calls are de-duplicated while in flight.
- **Per-request cap:** 8 Qloo HTTP calls, retries included. Calls are budgeted up front: options first, then liked entities, then style tags, then dislikes, with one call reserved for insights.
- **Per-instance daily cap:** `QLOO_DAILY_BUDGET`, default 300.
- **Per-IP limit:** 10 decide calls per 10 minutes. "Compare" uses 2.
- **Timeouts and retries:** 8 seconds per Qloo attempt, with an overall Qloo deadline of about 14 seconds per request. One retry with backoff, only on 429, 5xx or timeout (never on 401 or 403). Gemini calls time out after 20 seconds.

## Run locally (no keys needed)

Requires Node 20+ (uses the built-in `fetch`). There are no npm dependencies, so no install step is needed.

```powershell
cd demo\web
node dev-server.mjs --mock          # offline synthetic fixtures -> http://localhost:3000
```

To use the live APIs, copy `.env.example` to `.env`, fill in the keys, then run `node dev-server.mjs` (optionally with `--port 4000`). The dev server reads `.env` but never prints its values.

### Tests (offline, mock mode)

```powershell
node --test "demo/web/test/*.test.mjs"      # from the repo root (Node 21+ glob form)
node --test demo/web/test/                   # Node 20 also accepts a directory
```

On Node 22, `node --test demo/web/test` (a bare directory) does not discover the files. Use the glob form above, or run `npm test` from `demo/web`.

## Deploy (Vercel, no build step)

The project root is `demo/web`. Static files are served as-is, and `api/*.js` become Node functions (`vercel.json` sets `maxDuration` to 30 seconds).

```powershell
cd demo\web
vercel env add QLOO_API_KEY production
vercel env add GEMINI_API_KEY production
vercel env add QLOO_API_BASE_URL production   # https://hackathon.api.qloo.com
vercel env add GEMINI_MODEL production        # gemini-3.5-flash
vercel env add DEMO_MOCK production           # 0 (or 1 for an offline demo deployment)
vercel deploy --prod
```

### Environment variables

| Name | Required | Default | Notes |
|---|---|---|---|
| `GEMINI_API_KEY` | yes (unless `DEMO_MOCK=1`) | | Without it, `/api/decide` returns 503. |
| `QLOO_API_KEY` | recommended | | Without it, ON mode falls back to taste memory only. |
| `QLOO_API_BASE_URL` | no | `https://hackathon.api.qloo.com` | Must be https. |
| `GEMINI_MODEL` | no | `gemini-3.5-flash` | |
| `DEMO_MOCK` | no | `0` | `1` = no network, synthetic fixtures. |
| `QLOO_DAILY_BUDGET` | no | `300` | Per-instance daily cap on Qloo calls. |

## Limits and known gaps

- Request bodies on Vercel are limited to about 4.5 MB. The browser downsizes images (JPEG, at most 1024 px) before upload, so three photos fit easily.
- Mock mode cannot see images. It labels them `Image 1..3` without extracting attributes; live Gemini describes them.
- Qloo scores only options that name a known brand, title or place. Unbranded items, like the "neon graphic tee", are judged from taste memory and by Gemini, and the brief says so.
