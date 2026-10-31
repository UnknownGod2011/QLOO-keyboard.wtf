# Parameter reference

Sources: [Deep Dive][dd] (types, ranges, defaults), [Parameter Reference][pr] (descriptions and compatible
types), [Entity Type Parameter Guide][et] (which `filter.type` accepts which parameter), and the
[hackathon guide][hg]. "All 10" means artist, book, brand, destination, movie, person, place, podcast,
tv_show and video_game, as listed in the Entity Type Parameter Guide. Defaults are given only where a
page states them.

## USED BY QLOO JARVIS: `/v2/insights`

| Parameter | Purpose | Type / format | Valid values | Default | Required? | Dependencies | Entity types (Entity Type Guide) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `filter.type` | Entity category to return | string enum | The 10 hackathon types ([hackathon guide][hg]). See [spelling note](#contradictions-between-official-pages) for `video_game`. | none | **Required** | A 403 means unsupported type | All 10 |
| `signal.interests.entities` | Liked entities that shape affinity | comma-separated entity IDs (GET) | IDs from `/search` | none | Optional. **Required for destination.** | `.weight` exists ([Deep Dive][dd]) | All 10 |
| `signal.interests.tags` | Tags that shape affinity | comma-separated tag IDs (GET). Weighted `{tag, weight}` objects are POST-only. | IDs from `/v2/tags` | none | Optional | Free text fails ([hackathon guide][hg]). `.weight` exists. | All 10 |
| `filter.results.entities` | Return only these entities, i.e. score candidates | comma-separated entity IDs | IDs of the same type as `filter.type` | none | Optional | Needs a signal for a meaningful affinity **[LIVE 2026-10-31]** | All 10 |
| `filter.exclude.entities` | Remove these entities from results | comma-separated entity IDs | IDs from `/search` | none | Optional | Jarvis uses it for dislikes in recommend mode | All 10 |
| `filter.location.query` | Limit results to a named locality | string (GET: one locality name) | Fuzzy, case-insensitive names, e.g. a city or neighbourhood | Includes a margin just outside the boundary unless `filter.location.radius=0` | Optional | 400 if no locality matches. One locality gives `query.locality.signal` in the response. | Parameter Reference: Destination, Place. **Not listed** in the Entity Type Guide. |
| `take` | Number of results | integer | 1 to 50 | 20 | Optional | Use `page` for more | All 10 |
| `feature.explainability` | Add per-result and aggregate explanations | boolean | `true` / `false` | `false` | Optional | A warning appears in `query.explainability.warning` if it cannot be computed | Not listed in the Entity Type Guide. Documented in the [Deep Dive][dd]. Works with movie **[LIVE 2026-10-31]**. |

## USED BY QLOO JARVIS: `/search` ([Search Entities][se])

| Parameter | Purpose | Type / format | Valid values | Default | Required? | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| `query` | Text to search for | string | Entity name, e.g. a film title or brand | none | Yes, in practice (not marked required on the page) | Send short public entity names only, never personal data ([kit: safe use][kit-safe]) |
| `types` | Category to search in | array of strings (comma-separated) | Entity URNs. The list differs from Insights. Search also accepts legacy `urn:entity:actor`, `album`, `author`, `director`, `locality`, which Insights rejects. | none | Optional | Jarvis always sends the target type |
| `take` | Number of results | integer | Clone: 1 to 100. The live page shows no range. | 20 (clone) | Optional | Jarvis sends `take=1` |

Other `/search` parameters: `filter.location` (lat,lon pair, WKT or UUID), `filter.radius` (miles, 0 to 100, default 10),
`filter.exists`, `filter.tags`, `filter.exclude.tags`, `filter.rating` (0 to 5), `filter.popularity` (0 to 1),
`filter.address`, `operator.filter.tags` and `operator.filter.exclude.tags` (union or intersection, default union),
`page` (1 to 100, default 1), `sort_by` (`match` default, `distance`, `popularity`) ([Search Entities][se]).

## USED BY QLOO JARVIS: `/v2/tags` ([Search Tags][tg])

| Parameter | Purpose | Type / format | Valid values | Default | Required? | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| `filter.query` | Partial string match on tag name | string | A style or genre word, e.g. `minimalist` | none | Yes, in practice | The same word can return tags from several namespaces **[LIVE 2026-10-31]** |
| `take` | Number of results | integer | 1 to 50 | 20 | Optional | |
| `filter.parents.types` (optional) | Limit to tags whose parent entity type matches | comma-separated types | The Parameter Reference describes entity types (e.g. `urn:entity:place`). The page's enum lists only `urn:audience:*` values (see contradictions). | none | Optional | Jarvis also checks `parents[].type` client-side |
| `filter.tag.types` (optional) | Limit to exact tag types | comma-separated tag types | From `/v2/tags/types` ([Tag Types][tt]) | none | Optional | |
| `feature.typo_tolerance` | Tolerate typos in `filter.query` | boolean | `true` / `false` | `false` | Optional | Not used by default |

Also available: `filter.results.tags`, `filter.popularity.min/max`, `page` ([Search Tags][tg]).

## Other Insights parameters (not used by Jarvis)

| Parameter | Purpose (paraphrased) | Type / values | Entity types (Entity Type Guide unless noted) |
| --- | --- | --- | --- |
| `bias.trends` | How much trending entities influence results | `off`, `low`, `medium`, `high` | All 10 |
| `bias.quality` | Quality bias for places | not specified | Place (live guide only) |
| `diversify.by`, `diversify.take` | Cap results per city or metro (`properties.geocode.city` / `.metro`); per subtype for tags | enum; integer ≥ 1 | Live guide: Tag type (`diversify.by=subtype`) |
| `filter.tags`, `operator.filter.tags` | Require tags; union (default) or intersection | tag IDs; enum | All 10 |
| `filter.exclude.tags`, `operator.exclude.tags` | Exclude tags; union (default) or intersection | tag IDs; enum | All 10 |
| `filter.results.tags` | Return only these tags | tag IDs | (Deep Dive) |
| `filter.results.entities.query` | Name-based candidate filter (GET: one name) | string | All 10 |
| `filter.exclude.entities.query` | Name-based exclusion | **POST only** | All 10 (Parameter Reference) |
| `signal.interests.entities.query` | Name-based entity signal with `name`/`address`/`resolve_to` | **POST only** | (Deep Dive) |
| `signal.interests.entities.weight`, `signal.interests.tags.weight` | Strength of entity or tag influence | number | (Deep Dive) |
| `signal.demographics.age` (+ `.weight`) | Age bands `24_and_younger`, `25_to_29`, `30_to_34`, `35_to_44`, `45_to_54`, `55_and_older` | enum | All 10 |
| `signal.demographics.gender` (+ `.weight`) | `male` / `female` | enum | All 10 |
| `signal.demographics.audiences` (+ `.weight`) | Audience IDs from `/v2/audiences` | IDs | All 10 |
| `signal.location`, `.query`, `.radius`, `.weight` | Location as a ranking signal. Qloo advises against `.radius`. | WKT / locality ID / name / meters | Not listed in the guide. Used with movie in [Location-Based Insights][li]. |
| `filter.location`, `filter.exclude.location`, `filter.exclude.location.query` | Area restriction or exclusion (WKT with longitude first, or a locality ID or name) | string | `filter.location`: Destination, Place |
| `filter.location.radius` | Radius in meters. `0` means strict boundary. | integer | Destination, Place |
| `filter.location.geohash`, `filter.exclude.location.geohash` | Geohash prefix include or exclude | string | Destination, Place |
| `filter.geocode.name`, `.admin1_region`, `.admin2_region`, `.country_code` | Exact geocode match | string | Destination, Place |
| `filter.address` | Partial address match | string | Place |
| `filter.popularity.min/max` | Popularity percentile bounds | 0 to 1 | All 10 |
| `filter.parents.types` | Parent type filter | types | All 10 |
| `filter.external.exists`, `operator.filter.external.exists` | Require external keys `resy`, `michelin`, `tablet` | string; enum | All 10 |
| `filter.external.tripadvisor.rating.min/max`, `.rating.count.min/max` | Tripadvisor rating (0 to 5) or review-count bounds | number / integer | Place |
| `filter.external.resy.*` vs `filter.resy.*` | Resy rating, count and party size | see contradictions | Place |
| `filter.price_level.min/max` | Restaurant price level | 1 to 4 | Place |
| `filter.price_range.min/max` (or `.from/.to`) | Hotel price range | 0 to 1,000,000 | Place |
| `filter.hotel_class.min/max` | Hotel class | 1 to 5 | Place |
| `filter.hours` | Open on a weekday | `monday` to `sunday` | Place |
| `filter.properties.business_rating.min/max` | Business rating | 1 to 5 | Place |
| `filter.references_brand` | Places belonging to given brand IDs | IDs | Place |
| `filter.content_rating` | MPAA ratings `G`, `PG`, `PG-13`, `R`, `NC-17` | comma list | Movie, TV Show |
| `filter.release_year.min/max`, `filter.release_country` (+ operator) | Release year or country | integer; list | Movie, TV Show |
| `filter.rating.min/max` | Qloo rating | 0 to 5 | Movie, TV Show |
| `filter.release_date.min/max` | Release date | YYYY-MM-DD | Not listed in the guide |
| `filter.finale_year.min/max`, `filter.latest_known_year.min/max` | TV finale or latest year | integer | TV Show |
| `filter.publication_year.min/max` | First publication year | number | Book |
| `filter.date_of_birth/death.min/max`, `filter.gender` | Person filters | YYYY-MM-DD; string | Person |
| `filter.exists` | Require `properties.image` or `properties.description` | enum | (Deep Dive) |
| `offset`, `page` | Paging. `page` ≥ 1 is the recommended method. | integer | `offset`: all 10. `page` is not listed in the guide. |
| `sort_by` | `affinity` or `distance` (distance needs `filter.location`). The live guide adds `quality` for places. | enum | Live guide: Place |
| `output.heatmap.boundary` | Heatmap output granularity | string | Heatmap only |

Legacy v1 names that you may see in old examples ([Legacy mapping][lm]): `type` became `filter.type`, `exclude.tags` became `filter.exclude.tags`, `filter.exists` became `filter.external.exists`, `bias.age`, `bias.gender` and `bias.audiences` became `signal.demographics.*`, `target` became `signal.location`, `target.radius` became `filter.location.radius`, and `filter.references` became `filter.references_brand`.

## Contradictions between official pages

| # | Topic | Page A says | Page B says | What Jarvis does |
| --- | --- | --- | --- | --- |
| 1 | Exclude-tag operator name | `operator.exclude.tags` ([Deep Dive][dd], [Parameter Reference][pr], [Entity Type Guide][et], [Legacy mapping][lm]) | `operator.filter.exclude.tags` ([Search Entities][se]) | Not used. If needed, use each name only on its own endpoint. |
| 2 | Resy filter names | `filter.external.resy.count.*`, `filter.external.resy.party_size.*`, `filter.external.resy.rating.*` ([Deep Dive][dd], [Parameter Reference][pr], [Legacy mapping][lm]) | `filter.resy.rating_count.*`, `filter.resy.rating.party.*`, `filter.properties.resy.rating.*` ([Entity Type Guide][et], clone and live) | Not used |
| 3 | Hotel price range | `filter.price_range.min/max` ([Deep Dive][dd], [Parameter Reference][pr], live) | `filter.price_range.from/to` ([Entity Type Guide][et], [Legacy mapping][lm]) | Not used |
| 4 | Video game type URN | `urn:entity:video_game` ([hackathon guide][hg]) | `urn:entity:videogame` (Deep Dive and Get Trending enums in the clone; [Trending Entities][tc]) | Uses `video_game` |
| 5 | HTTP method | `/v2/insights` is GET only; a POST body fails ([hackathon guide][hg]) | Several parameters are POST-only ([Deep Dive][dd], [Parameter Reference][pr]) | GET only. No POST-only parameters. |
| 6 | `filter.parents.types` values | Parental **entity** types, e.g. `urn:entity:place` ([Parameter Reference][pr], [Taste Analysis][ta]) | Enum lists only `urn:audience:*` values ([Deep Dive][dd], [Tags][tg], [Tag Types][tt], [Audiences][au]) | Treats `parents[].type` as an entity type, matching the live `/v2/tags` response |
| 7 | `filter.tag.types` meaning | Audience types from `/v2/audiences/types` ([Parameter Reference][pr]) | Tag types from the Tag Types API ([Tags][tg]) | Treats it as tag types |
| 8 | `filter.exclude.location.query` | Same WKT or ID text as `filter.exclude.location` ([Parameter Reference][pr]) | Fuzzy locality-name query ([Deep Dive][dd]) | Not used |
| 9 | `filter.location.query` support | Compatible with Destination, Place ([Parameter Reference][pr]); recipe uses it with place ([Recipes][rec]) | Absent from every type in the [Entity Type Guide][et] | Uses it for places and checks `query.locality.signal` |
| 10 | Location-use-case parameters | `filter.types`, `filter.distance.max` listed as optional ([Location-Based Insights][li]) | Neither appears in the [Parameter Reference][pr] or [Deep Dive][dd] | Not used |
| 11 | `signal.location.query` text | Describes its response field in terms of `filter.location.query` ([Deep Dive][dd]) | (internal cross-reference error) | Not used |
| 12 | `filter.rating.*` scope | Text refers to a "Point of Interest" | Compatible types are Movie, TV Show ([Parameter Reference][pr], [Entity Type Guide][et]) | Not used |
| 13 | Affinity scale | 0 to 100 ([Affinity][as], [Interpreting][ias], [FAQs][faq]) | 0 to 1 ([Glossary][gl]); 0 to +1 ([Insights API concept][ia]) | API value is 0 to 1 **[LIVE 2026-10-31]** |
| 14 | Tag ID examples | `urn:tag:genre:restaurant:Italian` ([Parameter Reference][pr]); `urn:tag:genre:comedy` in prose ([Recommendation Insights][bi]) | `urn:tag:genre:place:restaurant:italian` ([Parameter Overview][po]); `urn:tag:genre:media:comedy` in the same page's curl ([Recommendation Insights][bi]) | Never hard-codes tag IDs. Always resolves through `/v2/tags`. |
| 15 | `/search` details | Clone: `filter.location` is "latitude,longitude"; `take` 1 to 100 | Live: also WKT or UUID; no `take` range shown ([Search Entities][se]) | Uses `query`, `types`, `take=1` only |
| 16 | `page` / `feature.explainability` | Documented for Insights ([Deep Dive][dd]) | Not listed for any type in the [Entity Type Guide][et] | Uses explainability **[LIVE 2026-10-31]** |
| 17 | Entity count | Over 250 million ([Entities][ent]) | 500+ million ([FAQs][faq]) | Irrelevant to code |
| 18 | Access surface | Direct HTTP with `X-Api-Key` ([hackathon guide][hg]) | Event surfaces are the `@qloo/qloo-harness` commands `qloo explore/exec/api/mcp` ([kit: API access][kit-access]) | Direct HTTP per the docs.qloo.com guide. Confirm with organisers if submission rules require the harness. |
| 19 | `/trends/category` types | Only brand supported, tv_show and movie in beta (description) | Enum lists 19 types ([Trending Entities][tc]) | Not used |
| 20 | OpenAPI file | Clone README calls `openapi-schema.json` the Qloo v2 spec | The file is the generic OpenAPI 3.1 meta-schema | Ignored |

[hg]: https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide
[dd]: https://docs.qloo.com/reference/insights-api-deep-dive
[pr]: https://docs.qloo.com/reference/parameters
[po]: https://docs.qloo.com/reference/parameter-overview
[et]: https://docs.qloo.com/reference/available-parameters-by-entity-type
[lm]: https://docs.qloo.com/reference/legacy-insights-parameter-mapping
[se]: https://docs.qloo.com/reference/get-search
[tg]: https://docs.qloo.com/reference/get-tags
[tt]: https://docs.qloo.com/reference/get-tag-types
[au]: https://docs.qloo.com/reference/get-audiences
[tc]: https://docs.qloo.com/reference/get-trends-category
[ta]: https://docs.qloo.com/reference/taste-analysis
[bi]: https://docs.qloo.com/reference/basic-insights-use-case
[li]: https://docs.qloo.com/reference/insights-with-location-use-case
[rec]: https://docs.qloo.com/recipes
[ent]: https://docs.qloo.com/docs/entities
[gl]: https://docs.qloo.com/docs/glossary
[ia]: https://docs.qloo.com/docs/insights-api
[as]: https://docs.qloo.com/docs/affinity-score
[ias]: https://docs.qloo.com/docs/interpreting-affinity-scores
[faq]: https://docs.qloo.com/docs/faqs
[kit-access]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/API_ACCESS.md
[kit-safe]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/SAFE_USE.md
