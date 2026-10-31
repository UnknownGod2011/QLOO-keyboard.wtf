# Natural Gmail details and terminal follow-ups — 14 October 2026

This checkpoint supersedes universal follow-up/detail readiness claims from the
previous installed `ccc5dbe` candidate. The user's spoken Sophia Gao/Daisy test
found real failures. Logs show successful Gmail calls followed by two Personal
reasoning HTTP 503 responses and a later proposal rejected as `terminal_task`.
The logs do not retain the full spoken text or tool arguments. The user supplied
the initial natural request separately; it is a causal test input, not a claimed
transcript recovered from logs.

## Causes and narrow changes

- Native thread preparation depended on the exact person-request grammar.
  A longer find-and-details request could stop at search metadata when reasoning
  failed. Explicit details/summary requests now inspect at most three observed
  candidate threads before optional reasoning, through the existing account,
  body-permission, current-scope and eight-execution budget. Missing bodies from
  those observed thread references use the existing message reader. Day/literal
  scope preparation keeps its established guards.
- Several natural details/questions missed the anaphoric follow-up grammar and
  reused terminal execution authority. Supported details requests now resolve a
  fresh read of a unique retained thread. Questions can use already retrieved
  message content; multiple available conversations are presented separately
  without choosing an arbitrary match. Repeated proposals for a settled follow-up
  return its cached result without another read or resurrecting action authority.
- A reasoning outage exposed headers but withheld successfully read message
  content from Live. Current delivery receipts now include bounded verified body
  excerpts (6,000 characters/message, 18,000 total), marked external untrusted data
  with clipping/coverage information. Retained result records remain compact
  facts/references; no durable mailbox store was added. Follow-up body delivery
  filters evidence to the accepted current goal revision.
- Later identity-only receipts could overwrite previously verified subject and
  participant headers for the same message/thread. Those metadata fields merge
  without transferring them across different thread identities.
- An explicit single-object opening may select one complete observed candidate
  after failed synthesis, while preserving the distinction from a semantic match.
  Earlier-scope, negative, incomplete and ambiguous evidence cannot use this new
  selection. Existing native-ID/account validation, plural opening and dispatch
  deduplication remain in force. No guessed opaque Gmail URL is accepted.

## Evidence and limitations

Ignored receipts are under `.artifacts/connectors/manual-followup-20261014/`.
The initial causal run reproduced eight failures (eight passed); later focused
checks cover natural details, fresh/duplicate follow-ups, cached questions,
current scope, metadata preservation, unique/plural opening, account changes and
cancellation. `final-gates.json` is authoritative for the frozen final broad gate.

Real held-staging native reads with simulated reasoning 503 retrieved three Sophia
conversations and actual body excerpts. Details/questions then used the retained
content with zero further backend calls; three exact references were independently
verified in separate Chrome tabs. Daisy's actual five search message candidates
led to its retained conversation and body reads; a details request started one
fresh thread read without searching again, and later questions/opening made zero
backend calls. These probes exercise product command/controller/provider paths
with an inert browser dispatch delegate. Real Chrome checks are a separate proof
tier; they do not prove spoken Live selection, audible synthesis or publication.

Long threads and excerpts remain bounded and explicitly partial. Do not claim
all-history coverage, complete real AI-generated summaries or universal spoken
acceptance from simulated outages/native reads. A provider 503 still limits
optional structured reasoning; it must not erase native evidence or be described
as a Gmail disconnection. One initial Daisy resolve had a transient DNS failure
before any provider execution; the later bounded read succeeded.

## Protected boundaries and delivery

Application changes from `ccc5dbe` are confined to five files under
`src/Services/Connectors`. Normal routing, CommandRegistry, permanent prompts/tools,
popup dimensions, audio/Live transports and parameters, Smart Writing, Browser
Automation, memory, selected text/screen analysis and billing are unchanged.
Held backend component tree remains `7632e2eaf4fbfa29a407f3dccf417320a2de9925`
(`00031-vup` / `gs-q261013`, zero default traffic). Existing backend 438/438
evidence is reused only for this unchanged component.

Rollback: `checkpoint/connectors-before-natural-details-20261014` at
`ccc5dbedf328228d590303fbd3f3b2f8a4f86608`; its reviewed package remains protected.
Final source/package/installed hashes and preservation checks belong to
`final-delivery.json` and the unique archive `package-proof.json`. Source tests
alone do not establish installation. Public 1.4.21, startup, Helper, both profiles,
mappings and Personal configuration remain protected. No external writes, public
deployment, unattended microphone or permanent AI preference change are authorized
by this checkpoint.
