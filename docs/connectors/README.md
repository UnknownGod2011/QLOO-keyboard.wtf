# Connector phase — isolated from accepted 1.4.21

Base: `987e4d4eaef6130f29f0818a3bc1a7a42c88f969`, the reviewed public export.
Branch: `codex/connectors-foundation`.

Connector runtime is now implemented in this isolated worktree and deployed to
an isolated staging backend/website, not to the accepted public product. Three
small broker declarations are added only to the development source. One real
Google staging grant and bounded provider reads are independently verified;
installed/Jarvis E2E and confirmed writes are not certified. Do not import private
development Git history, credentials or verification archives into this lineage.
Current evidence and missing release gates: [QA](QA.md) and
[platform decision record](../CONNECTORS.md).

## Frozen contracts

Preserve accepted selection/translation, screen session context, URL opening,
saved Links/workflows, BA ownership, Smart Writing, microphone/VAD/STT/playback,
Personal/Managed routing, pricing, billing, startup/hotkeys and Helper 2.1.28.
Connector work must use the existing action owner and truthful result lifecycle;
it must not create a second narrator, global memory store or prompt-heavy router.

## Original slice guidance (superseded by the approved connector master brief)

The approved master brief expands the normalized catalog to multiple services;
its ownership/security and staging-first gates remain mandatory. The following
sequence is retained as background, not a claim that runtime is still plan-only.

1. Define one integration and a minimal read-only use case; compare official
   provider APIs, OAuth verification requirements, scopes, quota and retention.
   Candidate families are email, calendar and documents; none is selected or
   advertised as shipped here.
2. Trace the existing tool registration/execution/receipt boundary. Propose a
   small adapter contract before modifying runtime code. Keep unavailable tools
   out of a session's schema; do not make the accepted prompt larger casually.
3. Use per-user OAuth and OS-protected/server-protected token storage as
   appropriate. No shared owner access token, browser permanent key, plaintext
   credential fallback or silent Managed charge from PersonalOnly.
4. Prove auth denial, expiry, refresh, revocation, account deletion, pagination,
   cancellation, quota/outage handling and bounded data/context with test doubles.
   Logs contain IDs/statuses or safe hashes, not raw email/docs or credentials.
5. Add read-only action + result + same-session bounded context. Clearly mark
   provider content as untrusted; it cannot grant action authority.
6. Only afterward add individually confirmed writes. Bind the confirmation to
   concrete target/content/account. Use provider idempotency where supported;
   otherwise reconcile an uncertain outcome instead of replaying it.
7. Run focused tests plus one bounded accepted-core regression. Package a
   separately identified candidate and preserve 1.4.21 for rollback. Do not
   update the live website/feed or advertise the connector before acceptance.

## Acceptance matrix

| Area | Required proof |
| --- | --- |
| Isolation | Same user/account boundaries; wrong-account and deleted-user access denied |
| Routing | PersonalOnly never touches Managed usage; one billing owner per operation |
| Grounding | Exact retrieved object/provenance, bounded freshness and explicit ambiguity |
| Writes | Explicit approval, once-only effect or truthful uncertain state |
| Recovery | No infinite spinner, no fake success, no destructive automatic replay |
| Privacy | Least privilege, revoke/disconnect, documented retention/deletion |
| Regression | Accepted Jarvis and all frozen contracts remain unchanged |

## Release facts

Windows 1.4.21 and paid checkout are live as an owner-authorized early launch.
Manual updates/unsigned distribution and absent clean-PC/bank-canary proof are
recorded limitations, not claims of perfect validation. Hosted Luma Voice remains
disabled pending dedicated credentials and admission/retention/audio proof.
Connector work does not resolve or bypass outstanding accounting-review fences.

Current implementation, independent proof tiers, staging URLs and real remaining
gates are in [CONNECTORS.md](../CONNECTORS.md) and [QA.md](QA.md). Exact missing
Slack/Discord staging fields are in [STAGING_AUTH_CONFIGURATION.md](STAGING_AUTH_CONFIGURATION.md).
These source/staging checkpoints do not replace the accepted public app.

The detailed private evidence/remaining-risk index stays in the release workspace:
`docs/production/LAUNCH_AND_CONNECTOR_CHECKPOINT_2026-10-08.md`.
