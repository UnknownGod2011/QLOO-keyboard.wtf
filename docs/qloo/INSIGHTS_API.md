# Insights API (`GET /v2/insights`)

## Request construction

- **Method and URL:** `GET https://hackathon.api.qloo.com/v2/insights?<params>` with the header `X-Api-Key`. All parameters go in the query string. A POST with a JSON body fails ("No path matching request parameters" or an unexpected 404) ([hackathon guide][hg]).
- **Lists:** in GET requests, multi-value parameters are comma-separated strings, for example `signal.interests.entities=<id1>,<id2>` ([Deep Dive][dd], [Parameter Reference][pr]).
- **Encoding:** official examples use both raw `urn:entity:movie` and percent-encoded `urn%3Aentity%3Amovie`. The Taste Analysis example also percent-encodes the comma between list items (`%2C`) ([Taste Analysis][ta], [Recommendation Insights][bi]). Jarvis percent-encodes every value.
- **Minimum request:** `filter.type` plus at least one valid signal or filter ([Recommendation Insights][bi], [Parameter Overview][po]).
- **Validate per type:** parameters that are not valid for the chosen `filter.type` are silently ignored ([hackathon guide][hg]). Check the [Entity Type Parameter Guide][et].

## `filter.type` values

| Value | Hackathon support | Notes |
| --- | --- | --- |
| `urn:entity:artist` | Yes | Music. `urn:entity:music` does not exist ([hackathon guide][hg]). |
| `urn:entity:book` | Yes | |
| `urn:entity:brand` | Yes | Also used for food and beverage brands ([hackathon guide][hg]). |
| `urn:entity:destination` | Yes | `signal.interests.entities` is **required** ([Entity Type Parameter Guide][et]). |
| `urn:entity:movie` | Yes | **[LIVE 2026-10-31]** |
| `urn:entity:person` | Yes | Replaces the deprecated actor, author and director types ([Entity Type Parameter Guide][et]). |
| `urn:entity:place` | Yes | Restaurants, hotels, venues, businesses ([Entity Type Parameter Guide][et]). |
| `urn:entity:podcast` | Yes | |
| `urn:entity:tv_show` | Yes | |
| `urn:entity:video_game` | Yes | The hackathon guide spells it `video_game`. The Deep Dive and Trending pages in the clone spell it `videogame`. Jarvis uses `video_game`. |
| `urn:tag`, `urn:demographics`, `urn:heatmap` | Not in the hackathon list | Documented for taste analysis, demographics and heatmaps ([Taste Analysis][ta], [Demographic Insights][dm], [Entity Type Parameter Guide][et]). Not used by Jarvis. |

A 403 "You do not have permission to access urn:entity:..." means the type is not supported ([hackathon guide][hg]).

## Signals, filters and output

The [Parameter Reference][pr] sorts parameters into three groups:

- **Signals** (`signal.*`, `bias.*`) say what the ranking should be based on: liked entities, tags, demographics, location. They change scores but do not remove results.
- **Filters** (`filter.*`, `operator.*`) say which results are allowed: entity type, tags, location, year, price and so on. Several values in one filter combine as OR by default. `operator.*` parameters switch that to `intersection` ([Filters][fil], [Deep Dive][dd]).
- **Output** parameters control presentation: `take`, `page`, `offset`, `sort_by`, `diversify.*`, `feature.explainability` ([Deep Dive][dd]).

### Kinds of input

| Input | Parameter(s) | Where valid IDs come from |
| --- | --- | --- |
| Entities | `signal.interests.entities`, `filter.results.entities`, `filter.exclude.entities` | `/search` ([hackathon guide][hg]). Qloo IDs are stable GUIDs ([Entities][ent]). |
| Tags | `signal.interests.tags`, `filter.tags`, `filter.exclude.tags`, `filter.results.tags` | `/v2/tags`. Free text is rejected ("at least one valid signal or filter is required") ([hackathon guide][hg]). |
| Audiences | `signal.demographics.audiences` | `/v2/audiences` ([Deep Dive][dd]). **Not used by Jarvis.** |
| Demographics | `signal.demographics.age`, `signal.demographics.gender` (fixed enums) | [Deep Dive][dd]. **Not used by Jarvis.** |
| Location | `filter.location*`, `signal.location*` | Locality names or WKT ([Deep Dive][dd]). |

Signal weights such as `signal.interests.entities.weight` and `signal.interests.tags.weight` exist. The per-tag weight objects can only be sent in a POST body, so they are unavailable through GET ([Deep Dive][dd]).

## Location parameters

| Parameter | Behaviour | Source |
| --- | --- | --- |
| `filter.location` | Restricts results to a WKT `POINT`, `POLYGON` or `MULTIPOLYGON`, or to one locality ID. WKT puts longitude first, e.g. `POINT(-73.99823 40.722668)`. | [Deep Dive][dd] |
| `filter.location.query` | Fuzzy, case-insensitive locality name (GET accepts one string). Equivalent to passing the locality's ID. When one locality is given, the response includes `query.locality.signal`. Returns 400 if nothing matches. By default, entities just outside the boundary are included. | [Deep Dive][dd] |
| `filter.location.radius` | Radius in **meters** with `filter.location` or `filter.location.query`. `0` limits results strictly to the boundary. | [Deep Dive][dd] |
| `filter.location.geohash`, `filter.exclude.location*` | Geohash prefix match, and exclusion variants. | [Deep Dive][dd] |
| `filter.geocode.name`, `.admin1_region`, `.admin2_region`, `.country_code` | Exact match on geocode properties (city, state, county, two-letter country). | [Parameter Reference][pr] |
| `signal.location`, `signal.location.query`, `signal.location.radius`, `signal.location.weight` | Uses the location as a ranking signal instead of a hard boundary. Qloo advises against `signal.location.radius` because it overrides dynamic density discovery. | [Deep Dive][dd], [Location-Based Insights][li] |

The Parameter Reference lists `filter.location.query` as compatible with Destination and Place, and the
official recipe uses it with `urn:entity:place` ([Recipes][rec]). The Entity Type Parameter Guide does
**not** list it under any type. Jarvis checks that `query.locality.signal` is present in the response,
which tells it the locality was applied rather than silently ignored.

Note that `/search` measures `filter.radius` in **miles** ([Search][se]), while Insights measures
radius in **meters**.

## `feature.explainability`

Defaults to `false`. When `true` ([Deep Dive][dd]):

- **Per result:** `query.explainability` shows which inputs (for example `signal.interests.entities`) contributed to that result, with normalised scores from 0 to 1. Inputs scoring at least 0.1 are always listed. Smaller ones may be dropped.
- **Aggregate:** a top-level `query.explainability` gives the average influence of each input over the top 3, 5, 10 and all results.
- **Failure:** if explainability cannot be computed, `query.explainability.warning` appears and the results are still returned.

Shape observed **[LIVE 2026-10-31]** (with entity signals only; tag signals not yet observed):

```text
results.entities[i].query.explainability["signal.interests.entities"] = [ { entity_id, score } ]
query.explainability["signal.interests.entities"] = {
  effect_on_top3_recs: [ { entity_id, avg_score } ], effect_on_top5_recs, effect_on_top10_recs, effect_on_all_recs }
```

## Pagination

- `take`: 1 to 50, default 20 ([Deep Dive][dd]).
- `page`: ≥ 1. Qloo recommends it for paging. It behaves like take plus offset ([Deep Dive][dd]).
- `offset`: number of results to skip, starting at 0. Less common ([Deep Dive][dd]).

## Candidate scoring with `filter.results.entities`

`filter.results.entities` takes a comma-separated list of entity IDs. Qloo describes it as a way to
measure an entity's affinity to the input ([Parameter Reference][pr], [Deep Dive][dd]). All ten
entity types accept it ([Entity Type Parameter Guide][et]).

**[LIVE 2026-10-31]:** with a movie signal plus `filter.results.entities=<idA>,<idB>`, only those two
candidates came back, each with `query.affinity`. A sci-fi candidate scored about 0.89 and a musical
about 0.69 for a sci-fi signal.

Rules Jarvis follows (our design choices, based on the docs):

- Compare affinities **only within one response**. Scores are normalised per query and drop as more inputs or filters are added ([Interpreting Affinity Scores][ias]).
- Treat small gaps (for example 91 vs 92 on the 0 to 100 scale) as a tie ([Interpreting Affinity Scores][ias], [FAQs][faq]).
- If a candidate is missing from the response, record it as "no Qloo score", not as a score of zero.

## Response structure

```text
{
  success: true,
  results: {
    entities: [ {
      name, entity_id,
      type,            // "urn:entity" in the official examples
      subtype,         // e.g. "urn:entity:movie"
      properties,      // type-specific: release_year, description, content_rating, duration, image.url, akas, ...
      popularity,      // percentile 0..1 within the domain
      tags: [ { id, name, type } ],
      query: { affinity, measurements: { audience_growth }, explainability },
      disambiguation, external  // e.g. imdb, metacritic, wikidata ids and ratings
    } ]
  },
  query: { explainability },   // only when feature.explainability=true
  duration                     // shown in official examples; treat as optional
}
```

Sources: [Recommendation Insights][bi], [Onboarding][ob], [Glossary][gl] (popularity), and **[LIVE 2026-10-31]** (`query.affinity`, `type` plus `subtype`, the top-level `query`).

- **Affinity scale:** the API returns 0 to 1 ([Glossary][gl], [Insights API concept][ia]) **[LIVE 2026-10-31]**. Pages about the UI use 0 to 100 ([Affinity][as]). Qloo's own bands on that scale are above 80 very high, 60 to 80 high, around 50 medium, below 50 low ([Interpreting Affinity Scores][ias]).
- **Meaning:** affinity is cosine similarity of embeddings for entity queries ([Methodology][asm]). It is an aggregate measure, not a probability about one person ([kit: safe use][kit-safe], [FAQs][faq]).
- For `urn:tag` the items are `results.tags[]` with `tag_id`, `name`, `types[]`, `subtype`, `tag_value`, `query` ([Taste Analysis][ta]). `/v2/tags` uses `id` instead of `tag_id` **[LIVE 2026-10-31]**.

## Error cases

| Symptom | Cause | Source |
| --- | --- | --- |
| 401 | Hackathon key used against staging or production | [hackathon guide][hg] |
| 401 "No API key found in request" | Key sent as Bearer token or query parameter instead of `X-Api-Key` | [hackathon guide][hg] |
| 403 "You do not have permission to access urn:entity:..." | Unsupported `filter.type` | [hackathon guide][hg] |
| 404 or no response | Base URL with no path | [hackathon guide][hg] |
| "No path matching request parameters" or unexpected 404 | POST with a JSON body | [hackathon guide][hg] |
| "at least one valid signal or filter is required" | Free-text tag or audience instead of a Qloo ID | [hackathon guide][hg] |
| 400 | Bad request, e.g. `filter.location.query` matched no locality, or a `*.query` parameter matched no entity | [Deep Dive][dd], [Parameter Reference][pr] |
| 429 | Rate limit exceeded (documented on lookup and trends routes) | [Search][se], [API Information][ai] |
| 500 | Internal server error | [Deep Dive][dd] |
| 200 with `entities: []` | Parameters not valid for the type (silently ignored), or no hackathon data for that entity or region | [hackathon guide][hg] |

## Limitations

- GET only in the hackathon, so POST-only features are unavailable: name-based `*.query` lists, weighted tag objects, multi-locality arrays ([hackathon guide][hg], [Deep Dive][dd]).
- No field selection. Responses can be large because of `akas` and similar fields ([hackathon guide][hg]).
- No static tag list ([hackathon guide][hg]).
- The hackathon data may not cover every entity or region ([hackathon guide][hg]).
- Results are aggregate cultural affinities. They are not evidence of a person's identity, preferences or future behaviour ([kit README][kit]).

[hg]: https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide
[dd]: https://docs.qloo.com/reference/insights-api-deep-dive
[pr]: https://docs.qloo.com/reference/parameters
[po]: https://docs.qloo.com/reference/parameter-overview
[et]: https://docs.qloo.com/reference/available-parameters-by-entity-type
[ob]: https://docs.qloo.com/reference/api-onboarding
[ai]: https://docs.qloo.com/reference/api-information
[se]: https://docs.qloo.com/reference/get-search
[ta]: https://docs.qloo.com/reference/taste-analysis
[bi]: https://docs.qloo.com/reference/basic-insights-use-case
[li]: https://docs.qloo.com/reference/insights-with-location-use-case
[dm]: https://docs.qloo.com/reference/demographics-use-case
[rec]: https://docs.qloo.com/recipes
[fil]: https://docs.qloo.com/docs/filters
[ent]: https://docs.qloo.com/docs/entities
[gl]: https://docs.qloo.com/docs/glossary
[ia]: https://docs.qloo.com/docs/insights-api
[as]: https://docs.qloo.com/docs/affinity-score
[asm]: https://docs.qloo.com/docs/affinity-score-methodology
[ias]: https://docs.qloo.com/docs/interpreting-affinity-scores
[faq]: https://docs.qloo.com/docs/faqs
[kit]: https://github.com/qloo/qloo-hackathon-kit
[kit-safe]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/SAFE_USE.md
