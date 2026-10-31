# Relevant Jarvis limits and safeguards — 23 October 2026

This inventory covers the selected-text, translation, connector and speech paths
reviewed for this incident. It is not a claim to enumerate every constant in the
repository. Most entries predate 018b835; unchanged source is not runtime proof.
No settings, scopes or controls in the **kept** rows have been removed.

26October evidence update: installed e33 captured106859characters and completed
an ordered three-part French translation; app clipboard verification and native
Notepad paste/readback preserved all76section markers and532references in order.
This disproves a50k whole-document admission/copy cap on that path. Current e177447
preserves that translation source unchanged. It is not proof of the loaded Chrome
worker's separate fallback branch or visible progress pixels on Devpost. The
extra Devpost check was blocked by tool policy before executing. The table below
records the original23October findings; its older pending wording is historical.

| Limit / behavior | Purpose and effect | This continuation |
|---|---|---|
| 50,000 characters for complete selected source | Rejected capture, translation admission and retained source even when the model could handle it | Removed in installed private 7618450; authentication recovered. Fresh 48,242-character capture reached translation; provider returned HTTP 503. Large clipboard completion remains pending. |
| 50,000 characters for DOM selection fallback | Returned an empty selection above the threshold | Removed in source/package; active Chrome Store worker update remains unverified; foreground ownership retained |
| 50,000 characters for retaining verified copied output | Lost the full copied-result referent for later use | Removed in installed private 7618450 |
| 50,000 characters per translation request | Existing Managed endpoint admission limit; also leaves expansion room in output | Larger documents now use ordered requests instead of rejection; server unchanged |
| Partial capture rejected/emptied | Prevented using even the available text | Available text now returned; translation can copy the captured portion with accurate coverage |
| 65,536 output tokens for complete translation | Current configured output setting; STOP/completeness checked before copy | Kept; known MAX_TOKENS can subdivide one part once |
| 8,192 output tokens for ordinary Personal HTTP generations | Existing general generation budget; not the Live speech duration | Kept; no change outside selected translation |
| 200,000 prompt characters for ordinary Personal HTTP calls | Existing non-selection request guard | Kept |
| 20,000 system-instruction characters; 50,000 schema bytes | Structured request/configuration envelope | Kept |
| 20,000,000 serialized Personal HTTP request bytes; 2 MiB response | Memory/transport envelope | Kept |
| 75-second Personal HTTP deadline | Bounds one provider attempt, not the whole multipart translation | Kept; no timeout retry added |
| 2 MiB Personal Live **setup** payload | Bounds actual serialized setup including prefix; not a selected-document allowance | Kept |
| Actual model input/context and output token limits | Provider limits are not equivalent to character counts; context also contains tools/history | Kept; no model change; no unlimited-analysis claim |
| 8 original selected documents in session history | Bounded temporary history; oldest entry evicted, session end clears it | Kept |
| 90-second same-window selection-cache freshness | Avoid using an unintended stale foreground selection | Kept; explicit retained originals have a separate route |
| 8,000 characters for ordinary clipboard reading | Returns a marked partial clipboard view | Kept, listed for user review |
| 6,000 characters for Browser Automation captured-source context; bounded task envelope | Keeps browser planning small; not the full retained selection | Kept to protect working BA |
| 4,000 characters for ordinary request routing; 3,000 for retained-intent grammar; 60-second current request lease | Bounds current action authority rather than stored conversation identity | Kept; cannot attribute this live failure to them without its trace |
| 2-second wait for independent tool-input observation | Allows transcript arrival before selected actions; bounded wait | Kept |
| 8 local connector execution tasks; 16 operations per task | Execution-work bounds; completed execution does not itself erase retained references | Kept |
| Some connector read receipts bounded at 64,000 JSON characters | Limits data entering a reasoning read | Kept; needs actual receipt evidence before changing |
| Retained connector projection: 8 evidence receipts, 12 resources, 6,000-character answer | Bounded conversational projection, not restoration of old write authority | Kept; fresh authorized reads remain available |
| Retained factual excerpts: up to 6, 320 characters each, 1,600 total | Small attributed facts; not a full body cache | Kept; complete read bodies have separate task evidence paths |
| Gmail search page size 1–100; first-page metadata sample default 6, max 8 | Native paged retrieval, not a declaration that only that many messages exist | Kept |
| 2 MiB connector provider-response envelope | Bounds provider response memory | Kept |
| 32 MiB Helper/native message envelope | Native messaging/memory safeguard | Kept |
| 120-second audio buffer | Maximum **queued** PCM, not a total spoken-reply duration cap | Kept; audio engine unchanged |
| Eight-second prior-turn grace in some delivery paths | Prevents background notices interrupting a user's turn; not a source/text cap | Kept; lifecycle trace needed for actual failure |
| Private audit recording: 20 seconds PCM, 30-second replay endpoint deadline | Diagnostic-only bounds; not a human microphone or conversation limit | Kept; current opening replay hit the deadline |

Other preserved safeguards: account/session revocation; validated native resource
and account identity for exact opening; no invented URLs; foreground-window checks
for destructive tab actions; one owner/deduplication of dispatched effects;
confirmation for permitted writes; clipboard read-back; no placeholder-as-result
provenance; cancellation and stale-completion ownership; no automatic Managed
fallback after ambiguous Personal dispatch. These protect the user's data and
working execution behavior and are not generic refusals to answer from useful
available content.

Screen capture is unchanged. Existing evidence attributes the observed slow
screen analysis to provider stall/503 after a fast local capture; this patch does
not alter image dimensions, camera, overlay or screen-analysis code.
