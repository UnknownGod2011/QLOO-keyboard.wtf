# Source index

All pages were accessed on **2026-10-31**. "Clone" means the local copy of Qloo's public docs repo
(`%TEMP%\qloo-docs-public`). In that repo, `reference/<name>.md` maps to
`https://docs.qloo.com/reference/<name>` and `docs/<name>.md` maps to `https://docs.qloo.com/docs/<name>`.
"Live" means the official URL was fetched directly. Clone pages are paraphrased only, because the repo
has no license file.

## Official Qloo documentation

| Page title | Official URL | What it documents | Why it matters to us | Accessed | How |
| --- | --- | --- | --- | --- | --- |
| Qloo Agentic Hackathon Developer Guide | https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide | Hackathon base URL, `X-Api-Key`, GET-only Insights, supported `filter.type` values, the error catalogue (401/403/404/empty results), caching rules, quota policy | Overrides generic docs for our key. It is the source of the base URL, error handling and caching rules. | 2026-10-31 | Live |
| API Overview | https://docs.qloo.com/reference/api-overview | Insights as the core API, Lookup APIs, Analysis & Trends APIs | API family map | 2026-10-31 | Clone + live |
| Insights API Deep Dive | https://docs.qloo.com/reference/insights-api-deep-dive | Full `/v2/insights` parameter list with types, ranges, defaults; `feature.explainability`; `take`/`page`/`offset` | Main reference for request construction | 2026-10-31 | Clone. Live page fetched, but its interactive enum lists are rendered by script and could not be read. |
| Parameter Overview | https://docs.qloo.com/reference/parameter-overview | How the Parameter Reference and the Entity Type Guide fit together; example parameter sets | Workflow for choosing parameters | 2026-10-31 | Clone |
| Parameter Reference | https://docs.qloo.com/reference/parameters | Parameter dictionary: filters, signals, output; compatible entity types | Formats and entity-type compatibility | 2026-10-31 | Clone + live (partial check) |
| Entity Type Parameter Guide | https://docs.qloo.com/reference/available-parameters-by-entity-type | Which parameters each `filter.type` accepts, and which are required | Avoids silently ignored parameters. Shows `destination` needs `signal.interests.entities`. | 2026-10-31 | Clone + live |
| Legacy Insights Parameter Mapping | https://docs.qloo.com/reference/legacy-insights-parameter-mapping | v1 to v2 parameter names | Recognising legacy names in old examples | 2026-10-31 | Clone |
| API Onboarding and Authentication | https://docs.qloo.com/reference/api-onboarding | `X-Api-Key` header; sample Insights response | Response field names (`type`, `subtype`, `properties`, `tags`, `popularity`) | 2026-10-31 | Clone |
| API Information | https://docs.qloo.com/reference/api-information | Root endpoint with 200/404/429 responses | Shows 429 is a documented response | 2026-10-31 | Clone |
| API System Status | https://docs.qloo.com/reference/api-system-status | Embedded status.qloo.com page | None. **Content not available:** the clone shows only a "refused to connect" frame. | 2026-10-31 | Clone (no content) |
| Search Entities | https://docs.qloo.com/reference/get-search | `/search` parameters: `query`, `types`, location, radius, tag filters, `page`, `take`, `sort_by` | Resolving liked entities to Qloo IDs | 2026-10-31 | Clone + live (the live page is newer: legacy search-only types, more `filter.location` formats, `filter.address`) |
| Search Entities by ID | https://docs.qloo.com/reference/get-entities | `/entities` lookup by Qloo ID or external IDs (IMDb, Spotify, ...) | Known, but not used | 2026-10-31 | Clone |
| Search Tags | https://docs.qloo.com/reference/get-tags | `/v2/tags`: `filter.query`, `feature.typo_tolerance`, `filter.tag.types`, `filter.parents.types`, `take` | Resolving style words to tag IDs | 2026-10-31 | Clone |
| Get Tag Types | https://docs.qloo.com/reference/get-tag-types | `/v2/tags/types` | Finding valid `filter.tag.types` values | 2026-10-31 | Clone |
| Get Audiences | https://docs.qloo.com/reference/get-audiences | `/v2/audiences` | Deliberately not used (no user demographics) | 2026-10-31 | Clone |
| Get Audience Types | https://docs.qloo.com/reference/get-audience-types | `/v2/audiences/types` | Deliberately not used | 2026-10-31 | Clone |
| Analysis Compare | https://docs.qloo.com/reference/analysis-compare | `/v2/analysis/compare` with `a.` and `b.signal.interests.entities` | Deliberately not used | 2026-10-31 | Clone |
| Analysis (single group) | https://docs.qloo.com/reference/get-analysis | Linked from the clone's API Overview | **Could not access:** live URL returned HTTP 404, and the page is not in the clone. | 2026-10-31 | Failed |
| Get Trending Data | https://docs.qloo.com/reference/get-trending | `/v2/trending` (required `signal.interests.entities`, `filter.type`, `filter.start_date`, `filter.end_date`) | Deliberately not used | 2026-10-31 | Clone |
| Trending Entities | https://docs.qloo.com/reference/get-trends-category | `/trends/category` | Deliberately not used | 2026-10-31 | Clone |
| Week-Over-Week Trending | https://docs.qloo.com/reference/get-trends-entity | `/trends/entity` | Deliberately not used | 2026-10-31 | Clone |
| Key Use Cases | https://docs.qloo.com/reference/use-cases | Index of use-case guides | Navigation | 2026-10-31 | Clone |
| Recommendation Insights | https://docs.qloo.com/reference/basic-insights-use-case | Basic recommendation request and full sample entity response | Response shape (`query.measurements.audience_growth`, `disambiguation`, `external`) | 2026-10-31 | Clone |
| Location-Based Insights | https://docs.qloo.com/reference/insights-with-location-use-case | `signal.location` / `signal.location.query` usage | Dining, hotel and venue decisions | 2026-10-31 | Clone |
| Demographic Insights | https://docs.qloo.com/reference/demographics-use-case | `filter.type=urn:demographics` | Deliberately not used | 2026-10-31 | Clone |
| Taste Analysis | https://docs.qloo.com/reference/taste-analysis | `filter.type=urn:tag` tag insights, `results.tags[]` shape | Known, not used in v1 of our engine | 2026-10-31 | Clone |
| Insights API (concept) | https://docs.qloo.com/docs/insights-api | Inputs and outputs; affinity 0 to +1; privacy statement | Affinity range | 2026-10-31 | Clone |
| Affinity | https://docs.qloo.com/docs/affinity-score | What affinity means; 0 to 100 scale | Score semantics | 2026-10-31 | Clone |
| Affinity Score Methodology | https://docs.qloo.com/docs/affinity-score-methodology | Embeddings plus cosine similarity | Explaining scores in briefs | 2026-10-31 | Clone |
| Interpreting Affinity Scores | https://docs.qloo.com/docs/interpreting-affinity-scores | Score ranges; small gaps are not meaningful; scores fall as inputs are added; normalised per query | Basis for qualitative confidence | 2026-10-31 | Clone |
| Signals | https://docs.qloo.com/docs/signals | Signals and signal arrays | Concept behind `signal.*` | 2026-10-31 | Clone |
| Filters | https://docs.qloo.com/docs/filters | Filter semantics; several attributes combine as OR by default | Expect union behaviour | 2026-10-31 | Clone |
| Entities | https://docs.qloo.com/docs/entities | Entity types; stable Qloo IDs; tags as labels | Caching IDs is safe | 2026-10-31 | Clone |
| Audiences | https://docs.qloo.com/docs/audiences | Audience concept | Deliberately not used | 2026-10-31 | Clone |
| Location Insights | https://docs.qloo.com/docs/insights-with-location | Geohash tiles; density discovery | Location behaviour | 2026-10-31 | Clone |
| Glossary | https://docs.qloo.com/docs/glossary | Affinity 0 to 1; popularity is a percentile; signals can be negative | Term definitions | 2026-10-31 | Clone |
| FAQs | https://docs.qloo.com/docs/faqs | No PII needed; data refresh cadence; affinity FAQ | Privacy and score interpretation | 2026-10-31 | Clone |
| Recipes | https://docs.qloo.com/recipes (path assumed from the clone's `recipes/recipes.md`) | Ready-made requests, e.g. restaurants like X in New York via `filter.location.query` | Evidence that `filter.location.query` works with `urn:entity:place` | 2026-10-31 | Clone |

## Qloo hackathon kit (GitHub)

| Page title | Official URL | What it documents | Why it matters to us | Accessed | How |
| --- | --- | --- | --- | --- | --- |
| Qloo Hackathon Kit (README) | https://github.com/qloo/qloo-hackathon-kit | The `@qloo/qloo-harness` CLI/MCP routes; results are aggregate affinities | Framing for how results may be presented | 2026-10-31 | Live |
| API Access and Limits | https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/API_ACCESS.md | Credential rules, smallest-request principle, bounded retries, no bulk scraping, supported surfaces | Quota and retry policy | 2026-10-31 | Live |
| Safe Use and Data Handling | https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/SAFE_USE.md | No personal data to Qloo; no sensitive inference; keep Qloo results separate from interpretation | Privacy rules for the preference store and briefs | 2026-10-31 | Live |
| Troubleshooting | https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/TROUBLESHOOTING.md | `QLOO_API_KEY` env var for the harness; never include the key in support requests; do not force fuzzy matches | Env-var fallback; ambiguity handling | 2026-10-31 | Live |

## Other sources

| Source | What it is | Notes |
| --- | --- | --- |
| Clone `openapi-schema.json` | Generic OpenAPI 3.1 meta-schema (`$id` `https://spec.openapis.org/oas/3.1/schema/2022-10-07`), **not** Qloo's API spec | No `paths`, no `servers`, no occurrence of "qloo". The clone README's claim that it is the Qloo v2 spec is wrong. |
| Live hackathon API checks | 6 GET calls to `https://hackathon.api.qloo.com` on 2026-10-31 (`/search`, `/v2/insights` with explainability, `/v2/insights` with `filter.results.entities`, `/v2/tags`) | Only response *shapes* are recorded here (tagged **[LIVE 2026-10-31]**). No response data was stored. |

## Not used

Heatmap pages (`heatmaps-use-case`, `docs/heatmaps`), the Insights UI pages, the data and methodology pages
other than those listed above, kit submission/contribution docs, and the docs MCP server
(`https://docs.qloo.com/mcp`). None of them is needed for Jarvis's decision flow.
