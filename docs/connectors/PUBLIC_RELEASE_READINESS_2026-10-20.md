# Public release readiness — 20 October 2026

**Private delivery frozen; public promotion not yet certified.** This is a concrete release handoff, not a request for another stabilization audit. The user accepted the working behavior and asked to stop unnecessary tests. Do not reopen tab/audio/translation work or repeat the completed suites.

## Release input

- Frozen application source: cdd33d3ec93be6ea67cd9466f7c5bb2e7ffa0cb1, src tree 0887c8b536138284fc910ca1aa5d05f57621fe55.
- Installed private 1.4.21 confirmed, rollback 5ca3698 and earlier 28e5489 preserved.
- Reviewed private installer archive: dist/connector-candidate-archives/cdd33d3-reviewed/package-proof.json.
- Evidence and exact limits: [PRIVATE_STABILIZATION_ACCEPTANCE_2026-10-20.md](PRIVATE_STABILIZATION_ACCEPTANCE_2026-10-20.md).
- This executable is ConnectorStagingCandidate, pointed at the existing staging backend. It must never be relabeled or uploaded as a public installer.

## Completed preparation

- Frozen source/payload/hash provenance and rollback inventory.
- Original-language native Gmail searches, preserved repeated-opening/six-turn evidence, final three-turn real Gmail/device replay, independent complete-details audio transcription.
- Focused 13-case recovery gate; reuse of prior 696-case shared connector/native gate and 452-case backend evidence.
- Public binaries, startup, Helper registration and 29 protected profile files preserved during final handoff.
- Known issues explicitly retained below, with no hidden passing rerun substituted for a failure.
- Owner-ready [Google submission packet](GOOGLE_OAUTH_SUBMISSION_PACKET_2026-10-20.md): current capability-to-scope review, draft scope rationales and connector disclosure, demonstration storyboard, and exact remaining actions. Proposed narrower scopes remain unvalidated through fresh consent; existing grants are unchanged.

## Current release evidence

- **Google submission:** owner explicitly confirmed on 20 October that data-access verification has not been submitted. This supersedes any assumption that the earlier verified branding covered connector permissions. The last console scope inventory is still the 11 October observation.
- **Public privacy:** read at 13:43:41 UTC on 20 October. The existing privacy URL redirects to `/privacy`, HTTP 200, and has no connector/Composio disclosure. Its Limited Use statement concerns the Chrome extension. Connector-specific disclosure is a verified release gap.
- **Production backend:** read-only Cloud Run metadata at 13:33:27 UTC shows 100% traffic on `keyboard-wtf-paid-api-nospendceilings-20261007`, source label `2532b6739b891070d3ea4ef182026c29255adb84`. Safe configuration read at 13:34:23 UTC shows no `Connectors__*` environment entries. Secret values were neither printed nor saved.
- **Backend promotion scope:** cross-repository Git object comparison of deployed source `2532b67` and reviewed connector backend source `00901f5` finds 105 changed files under `staging/backend`: 75 added, 30 modified, none removed. Changes include authentication, account deletion, usage/payment reconciliation and runtime identity as well as connector code/tests. This is an inventory, not a completed review or permission to promote the entire private backend.
- **Evidence:** `.artifacts/connectors/voice-followup-recurrence-20261018/context-route-final-delivery-20261020/public-readiness/` contains production-service-metadata.json, production-connector-config-safe.json, production-to-reviewed-backend-file-map.json and public-privacy-readback.json.
- **Preservation:** installed private source `cdd33d3` and rollback remain the release input. This preparation made no application, public-site, production, OAuth or profile change and did not rerun completed tests.

## Gates that still require evidence before public promotion

| Gate | Existing evidence / exact outstanding action |
| --- | --- |
| Google/provider public access | Owner confirms review was not submitted. Reconcile actual requested scopes, validate any narrower set with isolated consent, complete the disclosure/demo packet and obtain the required Google review/assessment. Current private grants and published branding are not public data-access approval. |
| Production configuration and release packaging | Fresh metadata confirms the older production revision has no connector configuration. Review the inventoried backend delta and intended production catalog/credential family before a controlled promotion. Build the actual public flavor with the existing release/version/signing process and verify its manifest/routes. Do not copy the private target/profile or alter the running private candidate. |
| Public Helper ownership | Private Helper is connected, but registration correctly still points to another/public installation, which the private Settings UI reports. Registration stayed protected. Verify one correct public GUI/Helper owner during the eventual public handoff; avoid duplicate GUI/hotkey ownership. |
| Production acceptance | Reuse all unchanged-source gates. Perform only the bounded checks specific to production packaging/routing and actual consent/public identity; no wholesale repetition of mailbox/audio/tab tests. Fresh rendered Gmail and physical mic/speaker outcomes cannot be certified by current disabled browser tooling. Earlier user confirmations are retained with their original build identity. |
| Release decision on residual limitations | Reopen-three remains at its last reported one-tab behavior. A prior provider Live stall after retrieval was not explained; final runs passed, but no universal stall recovery claim. Model ambiguity and all-provider real-account coverage remain limited. These must be disclosed or explicitly resolved before claiming those behaviors fixed. |
| Public assets and deployment | Release notes, version/feed/hash consistency and rollback verification must match the final public artifact. No website/update feed/installer publication or backend deployment has been performed by this private handoff. |

## Draft user-facing release notes

- Faster native connector reads with fewer redundant reasoning steps.
- Better retained-context handling for follow-up questions and exact conversation opening.
- Improved complete spoken delivery of connector details.
- Existing browser tab selection, translation and everyday Jarvis controls preserved.

Do not include claims of universal query understanding, perfect network recovery, newly fixed multiple-tab reopening, or fresh all-provider public acceptance.

## Next action

Complete the owner/operator actions in the Google submission packet and review the bounded production-backend delta before public promotion. Google approval remains absent. Keep the working private build frozen; do not install speculative candidates or repeatedly ask the user to retest accepted private behavior. The earlier source-level scope inventory is preparation, not an authorization to mutate the shared Google client or a substitute for fresh consent compatibility.
