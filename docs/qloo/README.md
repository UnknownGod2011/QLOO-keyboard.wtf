# Qloo API reference library (QLOO Jarvis)

Local, paraphrased reference for the Qloo APIs as used by QLOO Jarvis. Qloo's official
docs are the source of truth. Every factual claim here links to the official page it came
from. Claims marked **[LIVE 2026-10-31]** were also confirmed by our own calls to the
hackathon API on 2026-10-31. All example payloads in this folder are **synthetic**.

Do not paste real Qloo responses into this folder or anywhere else in the repo. The
hackathon guide forbids storing Qloo response data in a public repository ([hackathon guide][hg]).

## Which file to open

| File | Open it when you need to |
| --- | --- |
| [SOURCE_INDEX.md](SOURCE_INDEX.md) | Find the official page behind a claim, or see which pages were used or could not be accessed. |
| [API_MAP.md](API_MAP.md) | Understand the Qloo API families, auth, base URLs, each endpoint, and which ones Jarvis uses or deliberately skips. |
| [INSIGHTS_API.md](INSIGHTS_API.md) | Build or debug a `/v2/insights` request: signals vs filters, location, explainability, pagination, candidate scoring, response shape, errors. |
| [PARAMETER_REFERENCE.md](PARAMETER_REFERENCE.md) | Look up a parameter: format, valid values, default, which entity types accept it. Parameters our code uses come first. |
| [API_EXAMPLES.md](API_EXAMPLES.md) | Copy a working request shape (curl, `YOUR_API_KEY` placeholder) and see a synthetic response. |
| [DECISION_CAPABILITIES.md](DECISION_CAPABILITIES.md) | Decide whether Qloo can help with a type of user decision, and what Gemini or the local taste memory must handle instead. |
| [INTEGRATION_DESIGN.md](INTEGRATION_DESIGN.md) | See how Jarvis wires Gemini Live, the taste tools, the preference store and the Qloo client together (code in `src/Services/Taste/` and `src/Services/Qloo/`). |
| [EVALUATION.md](EVALUATION.md) | See the automated test results, the Windows smoke test and the live Qloo OFF vs ON comparison. |

## Top 10 facts an implementer must know

1. **Base URL.** Hackathon keys work only against `https://hackathon.api.qloo.com`. The same key returns 401 against staging (`https://staging.api.qloo.com`) or production (`https://api.qloo.com`). Official curl examples use those other hosts, so swap the host before running them ([hackathon guide][hg]).
2. **Auth header.** Send the key in the `X-Api-Key` header. A Bearer token or a query-string key fails with 401 "No API key found in request" ([hackathon guide][hg]).
3. **GET only for Insights.** `/v2/insights` is a GET request with parameters in the query string. A POST with a JSON body fails in the hackathon environment, even though the Deep Dive documents some POST-only parameters ([hackathon guide][hg], [Deep Dive][dd]).
4. **`filter.type` is mandatory.** The hackathon supports `urn:entity:artist`, `book`, `brand`, `destination`, `movie`, `person`, `place`, `podcast`, `tv_show`, `video_game`. A 403 means the type is not supported. Music is `urn:entity:artist`, not `urn:entity:music` ([hackathon guide][hg]).
5. **Wrong parameters fail silently.** Parameters that are invalid for the chosen `filter.type` are ignored without an error. An empty `entities` array usually means a parameter is not valid for that type, or the hackathon data has no coverage for that entity or region. Check the [Entity Type Parameter Guide][et] first ([hackathon guide][hg]).
6. **Tags and audiences need real IDs.** `signal.interests.tags`, `filter.tags` and `signal.demographics.audiences` take Qloo IDs from `/v2/tags` and `/v2/audiences`, not free text. Free text produces "at least one valid signal or filter is required". There is no static tag list ([hackathon guide][hg]). The same word can return several tags in different namespaces, so match the tag's parent type to the target `filter.type` **[LIVE 2026-10-31]**.
7. **Two different type lists.** `/search` accepts a different set of `types` than `/v2/insights` accepts for `filter.type`. Search also accepts legacy types such as `urn:entity:actor`, `album`, `author`, `director`, `locality`, which Insights rejects ([hackathon guide][hg], [Search Entities][se]).
8. **Candidate scoring.** `filter.results.entities=<id>,<id>` limits Insights results to those IDs, which is how you get an affinity for specific candidates ([Parameter Reference][pr]). Combined with a signal, it returns each candidate with its `query.affinity` (0 to 1) **[LIVE 2026-10-31]**. Jarvis uses this to score the user's own options.
9. **Paging and explainability.** `take` is 1 to 50 (default 20) on Insights. Use `page` for more results, or `offset` if you must. `feature.explainability=true` adds a per-result `query.explainability` and an aggregate one at the top level ([Deep Dive][dd]).
10. **Data handling.** Private server-side caching is allowed with no time limit, but Qloo response data must never go into a public repo ([hackathon guide][hg]). Do not send names, emails, device or account IDs, or location histories to Qloo. Do not use results to infer sensitive traits. Present Qloo results separately from your own interpretation, and keep retries bounded with no bulk scraping ([kit: safe use][kit-safe], [kit: API access][kit-access]).

Also worth knowing:

- Do not call the legacy `/recs` or `/recommendations` endpoints. They are not supported for the hackathon ([hackathon guide][hg]).
- `urn:entity:destination` **requires** `signal.interests.entities` ([Entity Type Parameter Guide][et]).
- Rate and monthly limits exist, but the numbers are not published. Ask in the hackathon Discord `#api-help` channel to have them raised ([hackathon guide][hg]).
- Affinity is documented on a 0 to 100 scale in some pages and 0 to 1 in others. The API field `query.affinity` is 0 to 1 ([Glossary][gl], [Affinity][as]) **[LIVE 2026-10-31]**.
- The responses have no field selection. You always get the full payload, including verbose fields such as `akas` ([hackathon guide][hg]).

## Notes on the sources

- The local clone of Qloo's docs repo (`qloo-docs-public`) has no license file. This library paraphrases it and links to the official pages instead of copying text.
- The clone's `openapi-schema.json` is **not** Qloo's API spec. It is the generic OpenAPI 3.1 meta-schema (`$id` `https://spec.openapis.org/oas/3.1/schema/2022-10-07`). It has no `paths` or `servers`, and the word "qloo" never appears in it. The clone's README wrongly calls it the Qloo v2 specification. Do not generate a client from it.
- Contradictions between official pages are listed in [PARAMETER_REFERENCE.md](PARAMETER_REFERENCE.md#contradictions-between-official-pages).

[hg]: https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide
[dd]: https://docs.qloo.com/reference/insights-api-deep-dive
[pr]: https://docs.qloo.com/reference/parameters
[et]: https://docs.qloo.com/reference/available-parameters-by-entity-type
[se]: https://docs.qloo.com/reference/get-search
[gl]: https://docs.qloo.com/docs/glossary
[as]: https://docs.qloo.com/docs/affinity-score
[kit-safe]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/SAFE_USE.md
[kit-access]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/API_ACCESS.md
