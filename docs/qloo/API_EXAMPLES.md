# API examples (synthetic)

> **Every response below is SYNTHETIC.** The names, UUIDs (`00000000-0000-4000-8000-00000000000N`),
> tag IDs (containing `example`) and scores are invented. Only the field layout matches what the
> official docs show and what we observed on 2026-10-31. Never replace these with real responses, because
> Qloo data must not be stored in a public repo ([hackathon guide][hg]).

Conventions:

- Host: `https://hackathon.api.qloo.com` only. Header: `X-Api-Key: YOUR_API_KEY` ([hackathon guide][hg]).
- Bodies are abbreviated. Real responses also carry `properties` such as `akas`, `image` and `external`, and cannot be trimmed ([hackathon guide][hg]).
- In Windows PowerShell 5.1, `curl` is an alias for `Invoke-WebRequest`. Type `curl.exe` to run these commands as written.

## 1. Entity search: resolve a liked title to an ID

```sh
curl.exe -s -H "X-Api-Key: YOUR_API_KEY" \
  "https://hackathon.api.qloo.com/search?query=Example%20Space%20Saga&types=urn:entity:movie&take=2"
```

```json
{
  "results": [
    {
      "name": "Example Space Saga (synthetic)",
      "entity_id": "00000000-0000-4000-8000-000000000001",
      "types": ["urn:entity:movie"],
      "properties": { "release_year": 2001, "description": "Synthetic placeholder." },
      "popularity": 0.97,
      "disambiguation": "2001, Example Director",
      "tags": [ { "id": "urn:tag:example_genre:media:science_fiction", "name": "Science Fiction (example)", "type": "urn:tag:example_genre:media" } ]
    }
  ]
}
```

Shape: `results[]` with `name`, `entity_id`, `types[]`, `properties`, `popularity`, `disambiguation`, `tags` **[LIVE 2026-10-31]**. Endpoint docs: [Search Entities][se].

## 2. Tag search: resolve a style word to tag IDs

```sh
curl.exe -s -H "X-Api-Key: YOUR_API_KEY" \
  "https://hackathon.api.qloo.com/v2/tags?filter.query=minimalist&take=3"
```

```json
{
  "duration": 12,
  "success": true,
  "results": {
    "tags": [
      { "id": "urn:tag:example_decor:qloo:minimalist", "name": "Minimalist (example decor)", "type": "urn:tag:example_decor:qloo",
        "parents": [ { "type": "urn:entity:place" } ], "popularity": 0.41, "properties": {}, "tags": [] },
      { "id": "urn:tag:example_genre:music:minimalist", "name": "Minimalist (example music genre)", "type": "urn:tag:example_genre:music",
        "parents": [ { "type": "urn:entity:artist" } ], "popularity": 0.22, "properties": {}, "tags": [] },
      { "id": "urn:tag:example_ambience:place:minimalist", "name": "Minimalist (example ambience)", "type": "urn:tag:example_ambience:place",
        "parents": [ { "type": "urn:entity:place" } ], "popularity": 0.18, "properties": {}, "tags": [] }
    ]
  }
}
```

The same word comes back in several namespaces. For a `urn:entity:place` decision, keep only tags with
`parents[].type == "urn:entity:place"` **[LIVE 2026-10-31]**. Endpoint docs: [Search Tags][tg].

## 3. Insights, recommend mode

```sh
curl.exe -s -H "X-Api-Key: YOUR_API_KEY" \
  "https://hackathon.api.qloo.com/v2/insights?filter.type=urn:entity:movie&signal.interests.entities=00000000-0000-4000-8000-000000000001&filter.exclude.entities=00000000-0000-4000-8000-000000000009&take=3"
```

```json
{
  "success": true,
  "results": {
    "entities": [
      {
        "name": "Example Orbit Drama (synthetic)",
        "entity_id": "00000000-0000-4000-8000-000000000004",
        "type": "urn:entity",
        "subtype": "urn:entity:movie",
        "properties": { "release_year": 2014 },
        "popularity": 0.95,
        "tags": [],
        "query": { "affinity": 0.91, "measurements": { "audience_growth": 0.01 } },
        "disambiguation": "2014, Example Director",
        "external": {}
      }
    ]
  }
}
```

Parameter docs: [Deep Dive][dd]. `filter.exclude.entities` removes the listed IDs ([Parameter Reference][pr]).

## 4. Insights, candidate scoring with explainability

Score two options that the user already has. Here 0002 is a sci-fi film and 0003 a musical.

```sh
curl.exe -s -H "X-Api-Key: YOUR_API_KEY" \
  "https://hackathon.api.qloo.com/v2/insights?filter.type=urn:entity:movie&signal.interests.entities=00000000-0000-4000-8000-000000000001&filter.results.entities=00000000-0000-4000-8000-000000000002,00000000-0000-4000-8000-000000000003&feature.explainability=true"
```

```json
{
  "success": true,
  "results": {
    "entities": [
      { "name": "Example Sci-Fi Option (synthetic)", "entity_id": "00000000-0000-4000-8000-000000000002",
        "type": "urn:entity", "subtype": "urn:entity:movie", "properties": {}, "popularity": 0.9, "tags": [],
        "query": { "affinity": 0.89, "measurements": { "audience_growth": 0.0 },
          "explainability": { "signal.interests.entities": [ { "entity_id": "00000000-0000-4000-8000-000000000001", "score": 1.0 } ] } },
        "disambiguation": "synthetic", "external": {} },
      { "name": "Example Musical Option (synthetic)", "entity_id": "00000000-0000-4000-8000-000000000003",
        "type": "urn:entity", "subtype": "urn:entity:movie", "properties": {}, "popularity": 0.8, "tags": [],
        "query": { "affinity": 0.69, "measurements": { "audience_growth": 0.0 },
          "explainability": { "signal.interests.entities": [ { "entity_id": "00000000-0000-4000-8000-000000000001", "score": 1.0 } ] } },
        "disambiguation": "synthetic", "external": {} }
    ]
  },
  "query": {
    "explainability": {
      "signal.interests.entities": {
        "effect_on_top3_recs":  [ { "entity_id": "00000000-0000-4000-8000-000000000001", "avg_score": 1.0 } ],
        "effect_on_top5_recs":  [ { "entity_id": "00000000-0000-4000-8000-000000000001", "avg_score": 1.0 } ],
        "effect_on_top10_recs": [ { "entity_id": "00000000-0000-4000-8000-000000000001", "avg_score": 1.0 } ],
        "effect_on_all_recs":   [ { "entity_id": "00000000-0000-4000-8000-000000000001", "avg_score": 1.0 } ]
      }
    }
  }
}
```

Only the listed candidates come back, each with `query.affinity` **[LIVE 2026-10-31]**. The pattern
(sci-fi option about 0.89, musical about 0.69 for a sci-fi signal) mirrors what we observed, but these
numbers are synthetic. Explainability semantics: [Deep Dive][dd].

## 5. Places with a locality (not live-verified)

```sh
curl.exe -s -H "X-Api-Key: YOUR_API_KEY" \
  "https://hackathon.api.qloo.com/v2/insights?filter.type=urn:entity:place&signal.interests.entities=00000000-0000-4000-8000-000000000005&filter.location.query=Example%20City&take=5"
```

On success, the response should include `query.locality.signal` (the matched locality). A name that
matches nothing returns 400 ([Deep Dive][dd]). The official recipe uses this pattern ([Recipes][rec]).

## 6. Errors

**401: hackathon key against the wrong host** ([hackathon guide][hg])

```sh
curl.exe -s -i -H "X-Api-Key: YOUR_API_KEY" "https://api.qloo.com/v2/insights?filter.type=urn:entity:movie"
# HTTP/1.1 401 ...   (use https://hackathon.api.qloo.com instead)
```

**401: key sent as a Bearer token.** The message "No API key found in request" is documented ([hackathon guide][hg]).

```sh
curl.exe -s -i -H "Authorization: Bearer YOUR_API_KEY" "https://hackathon.api.qloo.com/v2/insights?filter.type=urn:entity:movie"
```

**403: unsupported entity type.** The message "You do not have permission to access urn:entity:..." is documented ([hackathon guide][hg]).

```sh
curl.exe -s -i -H "X-Api-Key: YOUR_API_KEY" "https://hackathon.api.qloo.com/v2/insights?filter.type=urn:entity:music"
# HTTP/1.1 403 ...   (music is urn:entity:artist)
```

The JSON layout of error bodies is not documented. Branch on the HTTP status code and do not parse the message text.

## 7. Empty results (200)

```json
{ "success": true, "results": { "entities": [] } }
```

This is synthetic. Documented causes: a parameter that is invalid for the `filter.type` (silently
ignored), or no hackathon data for that entity or region. Check the [Entity Type Parameter Guide][et]
before assuming a data gap ([hackathon guide][hg]). Jarvis treats this as "no Qloo evidence" and falls
back to preference-only ranking.

A free-text tag such as `signal.interests.tags=minimalist` gives "at least one valid signal or filter is
required". Resolve the word through `/v2/tags` first ([hackathon guide][hg]).

[hg]: https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide
[dd]: https://docs.qloo.com/reference/insights-api-deep-dive
[pr]: https://docs.qloo.com/reference/parameters
[et]: https://docs.qloo.com/reference/available-parameters-by-entity-type
[se]: https://docs.qloo.com/reference/get-search
[tg]: https://docs.qloo.com/reference/get-tags
[rec]: https://docs.qloo.com/recipes
