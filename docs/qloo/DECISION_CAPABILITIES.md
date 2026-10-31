# Decision capabilities: what Qloo can and cannot help with

Qloo returns **aggregate cultural affinity** between entities and tags ([Interpreting Affinity Scores][ias],
[kit README][kit]). It ranks or scores entities of one supported type ([hackathon guide][hg]). It does
not see images, prices, stock or a specific product's physical attributes. Gemini (vision and
reasoning) and the local taste memory cover those parts. Parameter support comes from the
[Entity Type Parameter Guide][et].

| Category | Qloo type and parameters that help | What Qloo cannot do | What Gemini and local taste memory handle | Example requests |
| --- | --- | --- | --- | --- |
| Movies & TV | `urn:entity:movie`, `urn:entity:tv_show`. Liked titles in `signal.interests.entities`, genre tags in `signal.interests.tags`, options in `filter.results.entities`. Optional `filter.content_rating`, `filter.release_year.*`, `filter.rating.*`, `filter.exclude.entities`. | Live showtimes, ticket prices, guaranteed streaming availability in the user's region (streaming tags exist on entities, [Recommendation Insights][bi]), today's mood | Reading titles from a screenshot (e.g. a streaming grid), runtime and time constraints, the user's stated mood, past likes and dislikes | "Which of these three films should I watch tonight?" "Recommend a show like the one I just finished." |
| Music | `urn:entity:artist` (not `urn:entity:music`, [hackathon guide][hg]). Liked artists, genre tags. | Rank individual songs, albums or playlists (album is a search-only legacy type, [Search Entities][se]); play audio; judge sound quality | Picking out artist names on screen, the listening context (focus, party), temporary moods | "Which of these artists will I like most at the festival?" |
| Books | `urn:entity:book`, `filter.publication_year.*` | Price, edition, format, availability | Reading covers or lists from a screenshot, length and reading-time constraints | "Pick my next book from these four." |
| Podcasts | `urn:entity:podcast` | Episode-level content, release schedule, episode length | Episode topic matching from the visible description | "Which of these podcasts fits my taste for a long drive?" |
| Games | `urn:entity:video_game` | Platform or hardware compatibility, price, performance, age suitability beyond what the user states | Platform constraints, price limits the user states, reading the store page | "Which of these two games should I buy?" |
| Fashion & brands | `urn:entity:brand` for brand-level affinity; style tags from `/v2/tags` | Judge a garment's **colour, fit, size, fabric or physical look**; compare **prices**; check **stock or availability**; score an individual product (Qloo entities are brands, not SKUs) | Gemini vision describes the visible item (colour, cut, style words); hard constraints (budget, "no logos"); stated style preferences | "Which of these two jackets suits my style?" (Qloo scores the brands; Gemini judges the items.) |
| Dining | `urn:entity:place` with `filter.location.query`; optional `filter.price_level.*` (1 to 4), cuisine `filter.tags` from `/v2/tags`, `filter.hours`, `filter.exclude.entities` | Real-time table availability, current menu prices, allergen or dietary safety guarantees | Reading a menu or map screenshot; constraints for *this* request (time, party size, budget); cuisine likes and dislikes | "Which of these restaurants should we book?" "Find a place like my favourite café in this city." |
| Travel & destinations | `urn:entity:destination`. **`signal.interests.entities` is required.** Optional `filter.geocode.country_code`, `filter.tags`. | Flights, prices, visas, weather, safety advisories, dates | Trip constraints (dates, budget, duration), logistics reasoning | "Lisbon or Barcelona for a long weekend, given what I like?" |
| Hotels & venues | `urn:entity:place`. Optional `filter.hotel_class.*`, hotel price range (name conflict: `filter.price_range.min/max` vs `.from/.to`, see [PARAMETER_REFERENCE.md](PARAMETER_REFERENCE.md#contradictions-between-official-pages)), `filter.location.query` | Live room availability, booking, exact current rates | Comparing listings in a screenshot, distance and budget constraints | "Which of these three hotels fits me better?" |
| Lifestyle & aesthetics | `/v2/tags` style words (match the parent type), `urn:entity:brand` for brand fit, `urn:entity:place` for ambience | Judge whether a specific sofa, colour or layout looks good together; physical dimensions | Gemini vision compares the visible items; local memory of stated style preferences | "Which of these lamps matches my minimalist taste?" |
| People & celebrities | `urn:entity:person` (actor, author and director filter types are deprecated, [Entity Type Parameter Guide][et]) | Verify biographical facts; anything about a private person | Gemini answers factual questions. Jarvis never infers sensitive traits about anyone ([kit: safe use][kit-safe]). | "Which comedian's special would I enjoy, given my favourite shows?" |
| Technical, maths, logistics, other | **None. No Qloo call is made.** | Everything here: code, maths, schedules, routes, prices, specs | Gemini alone. The brief says `qloo_grounded=false`. | "Which laptop has more RAM?" "What is the fastest route?" "Solve this equation." |

## Limits that apply everywhere

- **Not a product catalogue.** There is no price comparison, inventory or SKU data. Supported types are the ten entity categories ([hackathon guide][hg], [Entities][ent]).
- **Not visual.** Screenshots never go to Qloo. Gemini extracts short entity names and style words ([INTEGRATION_DESIGN.md](INTEGRATION_DESIGN.md#privacy)).
- **Not individual prediction.** Affinity is an aggregate signal and must not be presented as a probability that this user will like something ([kit: safe use][kit-safe], [FAQs][faq]).
- **Close scores are ties.** Small gaps carry no real meaning, and scores are normalised per query ([Interpreting Affinity Scores][ias]).
- **Coverage gaps.** The hackathon data may lack some entities or regions. An empty result means "no Qloo evidence", not "bad option" ([hackathon guide][hg]).

[hg]: https://docs.qloo.com/reference/qloo-llm-hackathon-developer-guide
[et]: https://docs.qloo.com/reference/available-parameters-by-entity-type
[se]: https://docs.qloo.com/reference/get-search
[bi]: https://docs.qloo.com/reference/basic-insights-use-case
[ent]: https://docs.qloo.com/docs/entities
[ias]: https://docs.qloo.com/docs/interpreting-affinity-scores
[faq]: https://docs.qloo.com/docs/faqs
[kit]: https://github.com/qloo/qloo-hackathon-kit
[kit-safe]: https://github.com/qloo/qloo-hackathon-kit/blob/main/docs/SAFE_USE.md
