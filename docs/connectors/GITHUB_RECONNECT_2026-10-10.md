# GitHub disconnect/reconnect — 10 October 2026

## Demonstrated lifecycle

The owner disconnected only GitHub and reconnected the same GitHub account on
the isolated staging website. Independent checks used the running installed
7a1f6f7 private candidate, its authenticated `/api/connectors` path, and read-only
Firestore metadata. No additional OAuth grant, disconnect, external write,
provider execution or model request was issued by the verifier.

- Old account: disconnected tombstone updated **12:32:59.126404 UTC**.
- New account: created **12:33:26.934026 UTC**; completed OAuth attempt updated at
  the same instant, pointing to that new account rather than reusing the old one.
- Current authenticated desktop/backend snapshot: GitHub Connected; the opaque
  account reference matches the new record under the same cloud owner.
- Google/Gmail remained Connected. Jarvis was idle.
- Metadata pagination completed; no identity, label, OAuth state, token or
  provider content is saved in the receipt/report.

Accepted ignored receipt: `.artifacts/connectors/diagnostics/github-reconnect-1791635854070.json`
(`timeUtc=2026-10-10T12:37:25.845Z`).
Earlier rejected verifier receipts remain preserved: one used the wrong
normalized field name (`id` instead of `accountId`); two rejected the diagnostic
collection-group filtered query with HTTP 400. The final bounded metadata-only
ownership lookup uses the existing unfiltered projection and fails closed if
its 51-record bound is reached. None of these were product OAuth failures.

## Read after reconnect

The existing `GitHubIssues` diagnostic used the accepted compiled desktop
controller/dispatcher, authenticated keyboard.wtf backend on held a25d094,
normalized task service and provider adapter. It is not a standalone provider
call, nor a spoken natural-routing/physical-popup test.

- Outcome: verified repository read followed by verified existing-issue read.
- Two issue rows had useful metadata and body excerpts, with no truncated issue
  body in this sample. The repository result remains a bounded preview, not a
  claim to enumerate every repository or long body.
- Two capabilities; five product HTTP requests; **zero AI calls or writes**.
- Total **4,794 ms**; connection/capability resolution **1,163 ms**; two execution
  and verification stages **1,665 / 1,496 ms**; control **91 / 123 ms**.
- Controller publishes `Searching GitHub…` with GitHub provider identity; task
  activity clears. No microphone/Live/playback session was started.
- Loaded accepted assembly SHA256 matches the earlier accepted runtime:
  `4A113C980BE0B5EEEF9855CD68352B4527A174EC1592ECB099E86BEB8D855A59`.

Receipt: `.artifacts/connectors/diagnostics/receipt-GitHubIssues-b7f6f37f8e764015a5bd9d9c0e231d7f.json`.

## Boundaries

This proves this real GitHub disconnect/reconnect and current product-state
agreement. It does not prove externally revoked consent, account switching,
multiple GitHub accounts, a second device, or all-provider OAuth acceptance.
No production/runtime source, public package, provider configuration, billing,
Live/audio or Helper changes were made.
