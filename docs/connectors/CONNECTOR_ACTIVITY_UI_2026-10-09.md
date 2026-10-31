# Connector activity and evidence checkpoint — 9 October 2026

**UI: implemented and source-tested. CONNECTOR PLATFORM: NOT READY for promotion.**
The new popup is not installed. Complex natural-language orchestration remains a
real blocker; a green HTTP request or unit suite does not close it.

## Exact boundaries

- Runtime source: `0db54568d190f0036b48f57cc1588e59d2ceda0e`, branch
  `codex/connectors-foundation`. UI/receipt-order correction: `0a4e577`.
- Preserved source tags: `checkpoint/connectors-before-activity-polish-20261009`
  at `845334b`, and `checkpoint/connectors-activity-ui-source-20261009` at
  `0a4e577`. Earlier accepted/rejected archives and the original one-send fence
  remain untouched.
- Installed candidate remains **`3f02a9e`**, EXE SHA256
  `C15F5608F46E0EC4D438555F68798C25DE6A293AB2187FB7743EC4E289D00493`.
  Public EXE remains
  `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.
  Both hashes were independently rechecked; no package/install/public changes.
- GET-only Cloud Run inspection confirms **100% `00016-vic`** staging traffic;
  reviewed `00024-cal` / `b986706` has zero percentage traffic. Real product
  probes below use its fixed no-traffic tag, not the default/published endpoint.
- Backend tree is unchanged from `b986706` (`2e99543f56d673756976c625215512dbc85c5991`
  for `staging/backend`). No backend deployment/configuration mutation occurred.
- API/PersonalOnly only. No Managed inference, purchases, second self-email,
  financial maintenance, extension changes, production model switch or audio edits.

## Implemented inside the existing owners

The native WinForms popup now displays a locally bundled service mark, blue outer
activity circle and restrained two-second text shine. It uses the existing task
owner and 500ms display grace; **execution never waits for the animation**. No
React overlay, remote assets or new customer prerequisites. Hidden/non-connector
views stop the shine timer; native painting respects Windows animation preferences.
Existing microphone meter, playback pulse and voice phases remain unchanged.

Actual resolve/operation lifetimes publish Checking connection, Searching Gmail,
Searching and reading Gmail, Counting Gmail matches, Checking Calendar or Looking
through Drive. Success/failure/cancellation clear the owner. Ordinary proposal/
speech interruption does not cancel its worker; explicit cancellation/session
shutdown retains the existing cancellation and late-response fences. No fake
percentage or duplicate progress narrator was introduced.

The same controller now supplies stricter, task-scoped read receipts:

- malformed read proposals get the exact existing task/capability reference and a
  valid formatting example; parsing stays strict and writes retain approval/fences;
- inferred `sender:` filters are rejected before a misleading empty Gmail search;
  exact human queries and quoted body words retain their path;
- bounded evidence is scoped per operation, not incorrectly applied to every later
  complete count; trusted facts precede unchanged external bodies;
- typed search coverage distinguishes page size, sampled-body completeness and a
  **null/unverified query total**. Complete counts expose their verified total;
  incomplete counts expose only a lower bound. This is not semantic proof that the
  model chose the right category, nor a guarantee it will obey the receipt.

No global Live/system instruction was added: normal diagnostic setup remains
19,509 characters and 62 existing tools, with four task-scoped Gmail capabilities.
Router/gateway/provider abstraction, Firebase-derived ownership, account model,
write confirmation and metering owners are unchanged. No new memory subsystem,
AI planner or automatic query rewrite/count was added.

## Gates and proof limits

| Check | Actual result |
| --- | --- |
| Exact candidate desktop, final scope source | **4,204/4,204**, zero failures/skips, 3m56s |
| Earlier UI candidate checkpoint | **4,201/4,201**, zero failures/skips, 3m41s |
| Unchanged backend, excluding both database categories | **927/927**, zero failures/skips |
| Installed old candidate Settings | **10/10**; 12 rows, four shared-Google connected rows, PersonalOnly; catalog 1.608s |
| Native UI | Five STA/resource/ownership/motion checks; bitmap renders inspected at 100% and manually scaled 150% geometry |
| Secret/whitespace checks | PASS |

Native renders are not physical monitor-DPI, microphone or heard-playback proof.
Installed Settings evidence belongs to `3f02a9e`, not the new source. No new database
emulator/cloud-financial test was run; earlier database receipts remain separately
documented. Backend 927 excludes `FirestoreEmulator` **and** `FirestoreIntegration`;
do not relabel it the earlier 939-test gate.

Preserved causal failures include the hidden-parent timer, count-scope warning,
unsupported sender filter, dictionary serialization order and missing typed search
coverage. A default-flavor run passed 4,191 and failed two private-diagnostic-type
expectations; the correct explicit staging-candidate run above passed. A preceding
probe with the wrong build flavor failed during configuration, before dispatch.
Neither failed run is presented as acceptance. Original receipts remain preserved.

## Real keyboard.wtf product path

Compiled controller → PaidAccountService → authenticated keyboard.wtf backend →
normalized gateway/provider adapter. No direct Gmail/Composio script is counted as
acceptance. Receipts freeze source HEAD, dirty flag, assembly hash and backend tag
before loading. Stored receipts contain timing/status, not message bodies.

| Operation | Verified evidence / timing |
| --- | --- |
| Winning-notification exact query | **36**, one complete page; 2.913s including resolve, verification and cleanup; four capabilities |
| Natural single-topic count | **36** after real search/count and strict preflight corrections; status included Counting Gmail matches; first transcript 12.249s, total 23.592s including generated audio/drain |
| Calendar bounded next-day list | Verified empty result, not a content-reading claim; 2.632s total, one capability |
| Drive name search | Verified empty result, not document-reading proof; 3.030s total, two capabilities |
| Explicit in-flight Gmail cancellation | Cancelled, activity cleared, zero late completion notices/writes; 3.719s at unchanged-control `0a4e577` |
| Ordinary arithmetic, current full setup | Correct answer, zero connector HTTP/tools/discovery; first transcript 2.281s including 1.523s setup (about 0.758s after submission) |
| Ordinary broker classifier | 30,000 checks in 19.032ms; zero HTTP/capabilities/model calls; not whole microphone-to-speaker timing |

For representative direct reads, combined connection/capability resolution was
0.901–1.204s and execution **including** verification 1.359–1.732s. These boundaries
are measured together; no fabricated split into server routing/provider/verification.
The richer Live query had longer resolution (4.823/5.628s) and read steps
1.600–3.952s. Total Live timings include generated speech and the diagnostic drain,
not just time waiting for connector HTTP. No extra connector planning model call.

## Remaining real defect — do not install/promote from these suites alone

The richer three-part natural request is **inconsistent**:

1. At `0a4e577`, the model read relevant content but reported **five preview matches
   as the total** and claimed complete coverage without invoking `mail_count`.
2. At `0db5456`, another run mixed topics in one literal/Boolean search, then guessed
   an unsupported sender address. Count preflight correctly dispatched nothing;
   the model searched that bad address instead and incorrectly reported absence.
3. An isolated **350-character / three-broker-tool** context trial still reported
   three sampled matches as the total and falsely claimed all reads complete.
   This is a diagnostic comparison, not the normal installed context or a fix.

Thus a large preprompt is **not sufficient to explain or fix** the flaw. Actual
counting/access works; query planning, required-count invocation and answer-evidence
discipline do not consistently work for combined requests. Typed receipts improve
the contract but have **not** closed natural-answer fidelity. Do not stack more
global prompt text or retry until a lucky answer is labelled PASS.

The compact trial also saw two task-control HTTP 409s. Their exact cause is not
established; do not call them financial/provider incidents or silently dismiss them.
The diagnostic now GET-inspects exact task state/expiry after future control/execute
409s, without changing authority or recreating a write.

Next implementation checkpoint: keep the existing gateway/executors and make
required read/count outputs a narrowly enforced connector request-plan/evidence
contract before declaring the **whole request** complete. First prove a bounded
isolated approach; preserve ordinary Jarvis, audio, billing and one-owner lifecycle.
No further owner test is needed for this engineering step.

## Other acceptance still open

- Real OAuth cancellation/disconnect/reconnect, two real accounts, account switching,
  restart/cross-device and provider revocation need genuine lifecycle proof. Do not
  disconnect the useful Google connection merely to manufacture a green test.
- GitHub/additional services still need consent and meaningful content tests. The
  Calendar/Drive empty reads above do not certify all Google capabilities.
- Slack/Discord staging credential gates remain as last audited in
  [STAGING_AUTH_CONFIGURATION.md](STAGING_AUTH_CONFIGURATION.md); not re-audited
  or requested again here. Meet remains unresolved; Reddit/LinkedIn stay unavailable
  pending customer-branding clearance. Public Google verification is an external gate.
- Managed physical-billing correlation and representative production connector proof
  remain separate gates; API reads cannot certify them. No billing changes.
- Personal 1011/speech cutoff are **still open independently**. These text-driven
  probes are not failing microphone-session diagnostics. The 3.8 bounded trial
  produced an unusable tool-like answer with no audio; no compatibility or superiority
  claim/model switch. Neither compact context nor newer-model speculation is a fix.

All diagnostic sockets/leases were disposed; the actual allowed-state GET confirmed
`nativeVoiceBusy=false`. The completed one-off follow-up stays deleted. No indefinite
polling, public promotion or second email was started.

Artifacts: ignored `.artifacts/connectors/tests/gmail-real-read/`, unique
`diagnostics/receipt-*.json`, `ui/activity-0a4e577-20261009/`, and installed-only
`pc-installation/3f02a9e-activity-preinstall-20261009/`.
