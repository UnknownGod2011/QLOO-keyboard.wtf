# Connector quality pass — 11 October 2026

Status: source improvements and several real product boundaries verified; **NOT READY for release or universal natural-request acceptance**.

## Intelligence continuation (same day)

The subsequent source/research continuation is documented in
[the architecture review](INTELLIGENCE_ARCHITECTURE_REVIEW_2026-10-11.md).
Parent `0cf6b78` is protected by
`checkpoint/connectors-before-intelligence-0cf6b78-20261011`.
Its exact final source suite is **4,600 passed, no failures/skips**, with backend
**226 passed / 14 emulator skips**. These supersede neither historical installed
proof nor the earlier receipts below. One new controlled owned-reader attempt
returned Personal 429 before mail execution; natural quality is still open.
Public/installed/Helper/audio boundaries and one-send/create fences remain intact.
The subsequent backend initial-GET recovery slice passed 97 focused checks,
285 connector cases (14 emulator skips), and 1,074 wider backend cases (182 skips).
It remains undeployed and does not make Personal quota or natural-quality gates
PASS. The architecture review holds the exact receipts and recovery boundaries.

## Preserved checkpoint

- Source before changes: `aff814e72bf419da074e460bb184db2d555a20ef`.
- Rollback tag: `checkpoint/connectors-before-quality-aff814e-20261011`.
- Installed private runtime: `7e9aebc`, version 1.4.21, executable SHA-256 `7F7300306C4ADC4A42A7C5CE6174AB073D5BFBC46E9081DC886999F0BE13A4B0`.
- Public build, Helper 2.1.28, backend traffic, existing send/create fences and audio transport remain outside this change.

## Proven gaps and bounded design

1. Popup clears after each capability and restarts its display grace. Keep one task display identity through search/reasoning/read; distinguish visual continuity from genuinely executing work so Live response-health checks remain effective. Clear on terminal state, session end and expiry.
2. Connector orb reuses the ordinary white voice meter. Suppress that paint only for connector activity. Existing 100/150% captures did not reproduce logo clipping; add generic safe icon inset and additional DPI checks, without claiming a proven clipping cause.
3. Gmail search performs literal queries plus one simple empty-result OR refinement and enriches only the first hit. Add a provider-independent, injected read-only reasoner: small capability subset, bounded native searches, comparison of observed candidates, bounded reads, grounded detailed synthesis. Preserve ordinary/count paths and all write fences.
4. Completed task authority correctly dies, but no compact independent result-reference context survives. Keep bounded read-only session evidence separately; it must not authorize writes or revive tasks, and must clear on session/account change.
5. No normalized Gmail exact-open reference exists. Implement only from verified message/thread evidence and account identity; opening Inbox does not pass acceptance. Browser verification remains required.
6. Public capability help is missing. Project labels/limits from the existing server registry into the existing shared Settings/website panel. Do not advertise unimplemented or unverified service capabilities.

## Runtime audit

No keyboard.wtf processes were running at initial inspection. Physical Windows startup enumeration found the public app and obsolete Personal API Candidate 1.4.19. The obsolete Personal startup value was backed up and removed; independent physical enumeration then showed only the preserved public startup entry. No installation was removed. The connector candidate intentionally does not register startup.

The diagnostic launcher initially inherited Codex's shadow profile and stale credentials. It was relaunched outside that package context, using the unchanged physical private profile. This was a test-launch problem, not a failed Google connector. The authoritative private GUI is the executable/hash listed above; it remains idle, and the new quality source is not installed. No physical microphone/playback was exercised.

## Implemented boundaries

- Shared read reasoner: up to 8 read capabilities, 4 distinct searches, 8 executions and 9 model calls; bounded evidence, observed resource/cursor validation, same-scope pagination, strict JSON, recoverable rejected proposals, no writes or category counts. Existing structured AI gateway/owner is reused, not a separate model/billing system.
- The read owner prevents double query expansion: its Gmail candidate queries preserve `matchMode=exact`, while the reasoner owns bounded semantic variants. Ordinary and mixed content/count paths retain their existing behavior.
- Session context: at most 3 completed results, compact answer and at most 12 resource/header references each. No full bodies retained in this context; clipped answers disclose partial coverage. Session/account reset clears it, and recall cannot restore task/write authority.
- Exact mail opening: account-qualified URL from verified thread evidence. Ranked read evidence selects a primary target; unranked distinct read threads remain ambiguous. Search candidates alone are not treated as the selected email.
- Quota failures: a typed Personal AI 429 is reported as `ai_rate_limit`, not a disconnected connector. The task terminates and its activity clears. No credential rotation, Managed fallback or automatic task replay was added.
- UI: atomic resolve-to-task handoff, persistent between-step task display, expiry/disposal cleanup, full-aspect provider icons with inset, connector-only suppression of the white voice meter. Ordinary voice/translation UI retains its previous path.
- Capability information comes from server registry metadata, shared by Settings/website. Do not advertise drafts, attachments, analytics or other unimplemented capabilities. Existing normal permission mode already allows a requested write without an extra confirmation; explicit AlwaysAsk remains honored. No permission-policy bypass was added.

## Real product evidence (11 October)

All calls below use the compiled keyboard.wtf controller, authenticated desktop transport and normalized connector backend/provider path. None are standalone Gmail/Composio acceptance scripts. Identities and message bodies are absent from saved receipts.

| Check | Result / limitation |
| --- | --- |
| Native BioCyclic retrieval | Verified the expected project and **Top 10 Best Performers** in actual thread content. 5.773 s total; connection/capability resolution 1.358 s, search including verification 2.356 s, read including verification 1.253 s, task controls 0.178 s. Zero extra model calls. This controlled native query is not proof of natural AI search quality. |
| Exact Chrome thread | The product-resolved URL rendered the exact Blueprint Ideathon winner subject, Devpost sender, July 18 5:58 PM timestamp, BioCyclic and prize. Not Inbox. `quality-exact-browser-20261011.json` records boolean proof, no URL/body/identity. |
| Explicit cancellation | Actual in-flight read cancelled; server state Cancelled, UI cleared, zero completion notices and zero writes. 3.352 s including verification. |
| GitHub pagination | 55 distinct repositories, 4 pages, no duplicates, terminal series reached, UI cleared. 7.414 s; zero extra model calls. This proves retrieval, not a new semantic GitHub summary. |
| Ordinary broker overhead | 30,000 classifications in 2.5665 ms; zero connector HTTP calls and zero extra AI calls. This does not measure total voice/model response latency. |
| AI BioCyclic search | One earlier natural run found the expected prize in 83.365 s with 5 provider executions/6 additional model calls. Repeats were NOT consistently successful. Preserve those rejected receipts. |
| Latest Personal reasoning failure | HTTP **429 / Quota**, independently captured at the AI reasoning boundary. Gmail connection is valid. No Managed usage was used. Do not keep rerunning into the quota or claim the reader universally passes. |

Receipts: ignored `.artifacts/connectors/diagnostics/quality-native-0bcadf5e59274ff5a9ee4e44c67faad1.json`, `quality-exact-browser-20261011.json`, `quality-controls-*.json`, and `quality-biocylic-e37698f1c500434bb8cdada0bb0b672b.json`. All rejected diagnostics remain preserved, including the local build-flavor and DPAPI-overload harness faults, which are not product failures.

## Source/browser verification tiers

- Final candidate desktop suite: **4,561 passed, zero failures/skips**, 4m06s. Receipt: `.artifacts/connectors/quality-tests/quality-reviewed-candidate-desktop.trx`. It includes ordinary-interjection/explicit-cancel/late-answer protection and rejection of resource IDs from failed receipts. Earlier 367 focused and 4,559 full results remain separate preserved receipts.
- Backend capability registry: 105 passed, zero failures/skips. The wider connector set had 224 pass / 14 emulator-dependent skips; skips are not acceptance.
- Shared panel: 16 functional fixture checks; four actual website/Settings light/dark CSS cases, contrast >=4.5:1 and no 320px overflow, no console errors. These are isolated browser fixtures, not real OAuth or newly installed/hosted UI proof.
- Public EXE, Helper, accepted backend traffic and original once-send/create fences remain unchanged. No new quality installer or production deployment was made.
- Independent CodeRabbit review could not run: its official CLI installer returned `Unsupported operating system: mingw64_nt-10.0-26200`. The Windows WSL launcher also has no `/bin/bash`. No remote review result or review PASS is claimed; completing that review requires a supported Linux/WSL environment and CLI authentication. This is not a connector/runtime failure.

## Acceptance still required

1. After Personal quota recovers, repeat natural BioCyclic/vague-name and long-thread requests through the real AI owner; verify answer facts, useful depth, primary resource selection, prize/date/open follow-ups and latency. One prior success is not sufficient.
2. Exercise generic semantic reasoning on a second provider. Native GitHub retrieval has passed; semantic quality has not been freshly proven.
3. Verify new popup/context/help behavior in a privately installed matched build only after the source and real reasoning gates pass. Do not replace the stable public build merely to obtain this proof.
4. Preserve the used one-email/one-form fences. No new email send is authorized merely by this checklist, and none was performed in this pass.
5. Slack/Discord/Meet and branded production OAuth remain separate provider availability gates; do not mark Coming Soon services tested. The independent Personal Live 1011/speech-cutoff issue remains open; this pass did not alter transport/audio or switch models.
