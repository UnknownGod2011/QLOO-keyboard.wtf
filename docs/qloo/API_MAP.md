# Qloo API map

## Architecture

Qloo exposes one core API and two supporting groups ([API Overview][ov]):

| Group | Endpoints | Role |
| --- | --- | --- |
| **Insights API** | `GET /v2/insights` | The core. It returns ranked entities, tags or demographics for a required `filter.type`, shaped by signals (what to base results on) and filters (what results to allow) ([Parameter Reference][pr]). |
| **Lookup APIs** | `GET /search`, `GET /entities`, `GET /v2/tags`, `GET /v2/tags/types`, `GET /v2/audiences`, `GET /v2/audiences/types` | Find and validate the IDs that Insights needs as inputs ([API Overview][ov]). |
| **Analysis & Trends APIs** | `GET /v2/analysis/compare`, `GET /v2/trending`, `GET /trends/category`, `GET /trends/entity` | Group comparison and popularity over time ([API Overview][ov], [Get Trending Data][tr], [Trending Entities][tc], [Week-Over-Week][te]). |
| **Legacy (do not use)** | `/recs`, `/recommendations` | Not supported in the hackathon ([hackathon guide][hg]). |

The recommended flow is: use `/search` to get entity IDs, or `/v2/tags` to get tag IDs, then pass
those IDs as signals or filters to `/v2/insights`. Check each parameter against the
[Entity Type Parameter Guide][et]. `/search` and `/v2/insights` are separate paths. `/v2/insights/search`
does not exist ([hackathon guide][hg]).

## Authentication and base URLs

| Item | Value | Source |
| --- | --- | --- |
| Header | `X-Api-Key: <key>`. Not Bearer, not a query parameter. | [hackathon guide][hg], [Onboarding][ob] |
| Hackathon base URL (**the only one Jarvis uses**) | `https://hackathon.api.qloo.com` | [hackathon guide][hg] |
| Staging / production | `https://staging.api.qloo.com` / `https://api.qloo.com`. A hackathon key always gets 401 here. | [hackathon guide][hg] |
| Bare base URL | Returns 404 or no response. Always append a path. | [hackathon guide][hg] |
| Response trimming | Not supported. The full payload always comes back. | [hackathon guide][hg] |

## Limits and quota (official)

- Hackathon keys have rate limits and monthly quotas. The numbers are not published. Limits can be raised per user through Discord `#api-help`. Keys stay active through judging and are deactivated after the hackathon ([hackathon guide][hg]).
- `429 Rate limit exceeded` is a documented response on `/search`, `/entities`, `/trends/*` and the API root ([Search][se], [Entities][en], [Trending Entities][tc], [API Information][ai]).
- Send the smallest request that answers the question, cache only what you need, keep retries bounded, and never bulk-scrape ([kit: API access][kit-access]).
- Caching: private server-side caching is allowed with no time limit. Never put cached data in a public repo ([hackathon guide][hg]).

Jarvis applies its own budget on top of this: 8 calls per request, 150 per day. See [INTEGRATION_DESIGN.md](INTEGRATION_DESIGN.md#quota-reliability-and-fallback).

## Endpoints

"Quota use" describes how Jarvis spends calls. Every HTTP call counts against the key's quota.

### `GET /v2/insights`: used

- **Purpose:** ranked recommendations or affinity scores for one entity type ([Deep Dive][dd]).
- **Inputs:** `filter.type` (required) plus at least one valid signal or filter. Signals: `signal.interests.entities`, `signal.interests.tags`, `signal.demographics.*`, `signal.location*`. Filters: `filter.tags`, `filter.results.entities`, `filter.exclude.entities`, `filter.location*`, and type-specific ranges. Output controls: `take`, `page`, `offset`, `sort_by`, `feature.explainability` ([Deep Dive][dd], [Parameter Reference][pr], [hackathon guide][hg]).
- **Outputs:** `success`, `results.entities[]` (each with `name`, `entity_id`, `type`, `subtype`, `properties`, `popularity`, `tags`, `query`, `disambiguation`, `external`), plus a top-level `query` when explainability is on ([Recommendation Insights][bi], [Deep Dive][dd]) **[LIVE 2026-10-31]**. With `filter.type=urn:tag` the results come back as `results.tags[]` ([Taste Analysis][ta]). With `urn:demographics` they come back as `results.demographics[]` ([Demographic Insights][dm]).
- **Limits:** `take` 1 to 50, default 20. `page` ≥ 1. `offset` ≥ 0 ([Deep Dive][dd]).
- **Errors:** 400, 401, 403 (unsupported type), 404 (no path or POST body), 500. A 200 with an empty array is possible. Details in [INSIGHTS_API.md](INSIGHTS_API.md#error-cases).
- **Quota use:** one call to score the candidate options, or one call to recommend. Results are cached for 7 days.

### `GET /search`: used

- **Purpose:** find entity IDs by name ([Search][se]).
- **Inputs:** `query` (text) and `types` (categories). Optional: `filter.location` (lat,lon pair, WKT or Qloo UUID on the live page; the clone shows lat,lon only), `filter.radius` (miles, 0 to 100, default 10), `filter.exists`, `filter.tags`, `filter.exclude.tags`, `filter.rating`, `filter.popularity`, `filter.address`, `operator.filter.tags`, `operator.filter.exclude.tags`, `page` (1 to 100, default 1), `take`, `sort_by` (`match` default, `distance`, `popularity`) ([Search][se]).
- **Type list:** different from Insights `filter.type`. Search also accepts the legacy types `urn:entity:actor`, `album`, `author`, `director`, `locality`, which Insights does not ([Search][se], [hackathon guide][hg]).
- **Outputs:** `results[]` with `name`, `entity_id`, `types[]`, `properties`, `popularity`, `disambiguation`, `tags` **[LIVE 2026-10-31]**.
- **Limits:** the clone shows `take` 1 to 100 with default 20. The live page shows no range. Responses: 200, 404 (no results), 429 ([Search][se]).
- **Quota use:** one call per liked entity name that is not yet cached, with `take=1` and `types=<target type>`. Cached for 30 days.

### `GET /v2/tags`: used

- **Purpose:** find tag IDs by keyword. No static tag list exists ([Tags][tg], [hackathon guide][hg]).
- **Inputs:** `filter.query` (partial match on tag name), `feature.typo_tolerance` (default false), `filter.tag.types` (exact tag types, from `/v2/tags/types`), `filter.parents.types`, `filter.results.tags`, `filter.popularity.min/max`, `page`, `take` (1 to 50, default 20) ([Tags][tg]).
- **Outputs:** `duration`, `success`, `results.tags[]` with `id` (`urn:tag:...`), `name`, `type` (`urn:tag:<category>:<namespace>`), `parents[]` (each `{type: urn:entity:...}`), `popularity`, `properties`, `tags` **[LIVE 2026-10-31]**.
- **Gotcha:** one word can match tags in several namespaces (for example decor, music genre, ambience). Keep only tags whose `parents[].type` equals the target `filter.type` **[LIVE 2026-10-31]**.
- **Quota use:** one call per style word not yet cached. Cached for 30 days.

### `GET /v2/tags/types`: optional

- **Purpose:** list tag types, optionally filtered by `filter.parents.types`. Paged with `page` and `take` (1 to 50) ([Tag Types][tt]). The clone does not document the response shape.
- **Quota use:** none at runtime. Developer exploration only.

### Not used by Jarvis

| Endpoint | What it does | Why we skip it |
| --- | --- | --- |
| `/recs`, `/recommendations` | Legacy recommendations | Not supported for the hackathon ([hackathon guide][hg]). |
| `GET /entities` | Look up entities by Qloo ID or external IDs (`external.imdb.ids`, `external.spotify.ids`, ...) ([Entities][en]) | `/search` and `/v2/insights` already return the fields we need. It would cost extra quota. |
| `GET /v2/audiences`, `GET /v2/audiences/types` | Audience segment IDs and types ([Audiences][au], [Audience Types][at]) | Audiences include categories such as political preferences and communities ([Tags][tg] enum, [Audiences concept][aud]). Jarvis never infers or sends user demographics or segment membership ([kit: safe use][kit-safe]). |
| `signal.demographics.*`, `filter.type=urn:demographics` | Age, gender and audience weighting; demographic breakdowns ([Demographic Insights][dm]) | Same reason. No demographic inference. |
| `GET /v2/analysis/compare` | Compares two groups of entities (`a.` and `b.signal.interests.entities`, both required) ([Analysis Compare][ac]) | Compares groups, not candidate options against one taste. `filter.results.entities` does that more directly. |
| `GET /v2/analysis` | Single-group analysis (linked from the clone's overview) | The page is not reachable (404). Not needed. |
| `GET /v2/trending`, `/trends/category`, `/trends/entity` | Popularity trends over time ([Get Trending Data][tr], [Trending Entities][tc], [Week-Over-Week][te]) | A trend does not decide a personal choice, and it costs quota. |
| `filter.type=urn:heatmap` | Geographic heatmaps ([Entity Type Parameter Guide][et]) | No map UI in Jarvis. |
| `filter.type=urn:tag` (taste analysis) | Tags that describe a signal ([Taste Analysis][ta]) | Possible later use for "why do I like this?". Not needed for decisions now. |
| POST-only parameters (`signal.interests.entities.query`, `filter.exclude.entities.query`, weighted tag objects, multi-locality arrays) | Name-based or weighted inputs in a JSON body ([Deep Dive][dd]) | Insights must be called with GET in the hackathon ([hackathon guide][hg]). |

## Product needs to endpoints

| Jarvis need | Endpoint and parameters | Notes |
| --- | --- | --- |
| Turn a liked film, brand or artist name into a Qloo ID | `/search?query=<name>&types=<target type>&take=1` | Cache the result. Never send personal names or other identifiers. |
| Turn a style word ("minimalist", "cozy") into tag IDs | `/v2/tags?filter.query=<word>&take=<n>`, optionally `filter.parents.types` | Keep only tags whose parent type matches the target type. |
| Score the user's own options | `/v2/insights?filter.type=<t>&signal.interests.entities=<liked>&signal.interests.tags=<tags>&filter.results.entities=<option ids>&feature.explainability=true` | Returns only those candidates, each with `query.affinity` **[LIVE 2026-10-31]**. |
| Recommend when the user gave no options | `/v2/insights` with the same signals, plus `filter.exclude.entities=<disliked>` and `take` | Destinations require `signal.interests.entities` ([Entity Type Parameter Guide][et]). |
| Restrict places to an area | add `filter.location.query=<locality name>` | A locality name, not a precise location. Returns 400 if nothing matches ([Deep Dive][dd]). |
| Explain why an option scored well | `feature.explainability=true` | Per-result and aggregate contribution of each input ([Deep Dive][dd]). |

[ov]: https://docs.qloo.com/reference/api-overview
[hg]: https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide
[dd]: https://docs.qloo.com/reference/insights-api-deep-dive
[pr]: https://docs.qloo.com/reference/parameters
[et]: https://docs.qloo.com/reference/available-parameters-by-entity-type
[ob]: https://docs.qloo.com/reference/api-onboarding
[ai]: https://docs.qloo.com/reference/api-information
[se]: https://docs.qloo.com/reference/get-search
[en]: https://docs.qloo.com/reference/get-entities
[tg]: https://docs.qloo.com/reference/get-tags
[tt]: https://docs.qloo.com/reference/get-tag-types
[au]: https://docs.qloo.com/reference/get-audiences
[at]: https://docs.qloo.com/reference/get-audience-types
[ac]: https://docs.qloo.com/reference/analysis-compare
[tr]: https://docs.qloo.com/reference/get-trending
[tc]: https://docs.qloo.com/reference/get-trends-category
[te]: https://docs.qloo.com/reference/get-trends-entity
[ta]: https://docs.qloo.com/reference/taste-analysis
[bi]: https://docs.qloo.com/reference/basic-insights-use-case
[dm]: https://docs.qloo.com/reference/demographics-use-case
[aud]: https://docs.qloo.com/docs/audiences
[kit-access]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/API_ACCESS.md
[kit-safe]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/SAFE_USE.md
