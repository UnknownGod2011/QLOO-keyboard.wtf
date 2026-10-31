# Connector resource/context and UI checkpoint — 11 October 2026

**SOURCE-ONLY; NOT READY for universal natural-language or public acceptance.**
Continues `e2a167151b16235e5b492368a758bb6a0097f2fc`, protected by
`checkpoint/connectors-read-recovery-20261011`; no architecture restart.
The preceding [intelligence review](INTELLIGENCE_ARCHITECTURE_REVIEW_2026-10-11.md)
and [quality pass](QUALITY_PASS_2026-10-11.md) remain historical evidence.

## Protected runtime and scope

- Installed private GUI remains **`7e9aebc`, v1.4.21**, EXE SHA-256
  `7F7300306C4ADC4A42A7C5CE6174AB073D5BFBC46E9081DC886999F0BE13A4B0`.
  Fresh executable/CIM verification found one intended candidate GUI, not a
  second GUI or a new installation. No microphone/playback was started.
- Held authenticated test backend remains **`a25d094` / `00028-boh`**.
  New backend recovery source is not deployed. No traffic or public build change.
- Personal/API only; no Managed fallback, credential rotation within a session,
  purchase, new send/create, or reuse of the historical one-send/one-create tests.
- Helper 2.1.28, Live/audio/VAD/STT, Smart Writing, translation, billing,
  authentication and ordinary action systems are untouched. The requested
  contextual mute/Smart Writing button remains deferred until connector acceptance.

## Causal changes

1. **Exact object context was mail-only.** A small provider-specific reference
   adapter now retains validated object identity/title/date/URL from verified
   normalized capability receipts, inside the existing bounded session-result
   owner. No new memory subsystem, full bodies, model calls or write authority.
   Drive uses matching `webViewLink`; Calendar uses observed `htmlLink`; GitHub
   URLs must match the actual repository/issue/PR; Docs/Forms and YouTube URLs
   derive from observed identities; Slack requires a matching observed permalink.
   Discord or history-only results do not fall back to provider home pages.

   These source contracts follow the official resource fields:
   [Drive files](https://developers.google.com/workspace/drive/api/reference/rest/v3/files),
   [Calendar events](https://developers.google.com/workspace/calendar/api/v3/reference/events),
   [YouTube search](https://developers.google.com/youtube/v3/docs/search),
   [Forms](https://developers.google.com/workspace/forms/api/reference/rest/v1/forms),
   [GitHub issues](https://docs.github.com/en/rest/issues/issues), and
   [Slack permalinks](https://docs.slack.dev/reference/methods/chat.getPermalink/).
   Generated references are **not proof of browser navigation**.

2. **A sole retained URL was not always a uniquely selected object.** Ranked
   primary evidence must identify one actual resource; it cannot promote a later
   supporting receipt. Unranked results cannot silently discard a malformed,
   unopenable, omitted or unaccounted-for candidate/read to select another object.
   Lists are inspected with a bounded overflow sentinel; references remain <=12,
   receipts <=6, session results <=3, answer <=6k characters. Account/session end
   clears context. Failed/unverified receipts, body URLs and envelope diagnostics
   are not navigation authority. Ambiguity does not itself imply partial content
   or a partial count.

3. **Gmail search samples could hide candidate ambiguity.** Only trusted
   `result.data` and known mail capability shapes are visited. Search identities
   come from its candidate list, not a preview/sample. `hasMore`, a next cursor,
   count mismatch, truncation or malformed candidates block unique search-only
   navigation. Samples can enrich already-observed matching IDs only. A directly
   read message/thread keeps its exact thread even when content is partial.

4. **Model-issued cancellation could lack human authority.** Both immediate
   and model control now select one current eligible owner from clear cancellation
   intent. Task ID alone cannot disambiguate two owners. Named cancellation must
   match the exact provider; questions, noise, quoted/negated requests and unrelated
   local commands do not cancel. Account handoff is still active work even after
   its old read becomes locally cancelled. Expiry/reset/dispose and the private
   zero-choice document fallback retain trusted cleanup. Existing BA/translation
   immediate-control precedence is unchanged; cross-subsystem model-control
   ambiguity remains an integration gate, not a claim of global cancellation proof.

5. **Activity started as search before dispatch.** Owned complex reads now start
   Thinking and publish search/read statuses around real provider work; one visual
   owner survives internal reasoning/search steps and ordinary interjections.
   No fake delay, per-tool popup recreation or playback change. The read worker's
   concise prompt permits approximate display/company names only for safe reads,
   while literal address/domain/query constraints and write recipient rules stay
   strict. No global Live instruction or model selection change.

6. **Shared panel lost focus/scroll and allowed duplicate mutations.** The
   existing Settings/website module now retains focus/scroll and excludes duplicate
   actions per provider family until authoritative reconciliation. Connecting
   reflects real creation work. An ambiguous mutation removes stale Connected
   claims immediately; failed reconciliation remains unavailable, never retries
   a write or guesses success. Account-switch generations fence old replies.

7. **Optional inferred count qualification could throw under local load.**
   The earlier full run recorded a 50ms regex timeout on a short query; a serial
   eight-case rerun passed. The flat pattern is not evidence of catastrophic
   backtracking. Keep its timeout, but skip optional inferred rewriting on timeout
   rather than aborting the task, broadening a query or raising a safety budget.

## Verification and original receipts

All artifacts below are ignored local evidence; earlier rejected receipts remain.

| Gate | Actual result / boundary |
| --- | --- |
| Focused desktop | `resource-owner-reconciled-v3.trx`: **534 passed**, zero failures/skips; precedes the final missing-data/collection fence. |
| Final desktop | `resource-owner-final-desktop-v2.trx`: **4,776 passed**, zero failures/skips; CLI duration 4m21s, TRX wall interval 263.994s. Independently verified counters. |
| Earlier rejected full desktop | `resource-owner-final-desktop-v1.trx`: **4,762 passed / 1 failed**, regex timeout above. Preserved, not relabelled green. |
| Serial qualification rerun | `count-qualification-serial-v1.trx`: **8 passed**, zero failures/skips. |
| Backend connector, local Firestore | `resource-owner-backend-connectors-local-emulator-v1.trx`: **299 passed**, zero failures/skips. |
| Wider backend, local Firestore | `resource-owner-backend-wide-local-emulator-v1.trx`: **1,709 passed**, zero failures/skips. Synthetic local identities/project only; no cloud financial writes. |
| Shared panel ownership | **15 groups**, zero browser errors; duplicate clicks, focus/scroll, ambiguous disconnect, failed reconciliation and stale account generations. |
| Existing shared UI | **16 groups**, zero console errors. |
| Theme/mobile | **4 groups**, zero errors; actual CSS/modules, light/dark website/Settings at 320px; screenshots inspected. Fixture auth, not installed/OAuth acceptance. |

TRX base: `.artifacts/connectors/quality-tests/`. UI receipts:
`output/playwright/connector-panel-ownership/proof.json`,
`.artifacts/connectors/ui/functional.json`,
`output/playwright/connectors-theme/theme-proof.json`. Native popup DPI/inset
captures remain separate from these web fixtures.

## Real product-path evidence, not direct provider acceptance

The compiled controller, existing Personal/authenticated client, normalized
keyboard.wtf backend/router/gateway and held provider adapter were exercised.
Receipts freeze parent HEAD, dirty-source state and loaded assembly hash; a later
commit must not relabel a previously loaded assembly. Provider bodies/identities
are not copied into this document.

- **Docs read + temporary recall/open reference:** at 13:31 UTC, **3.669s**,
  four backend HTTP requests, zero AI/send/create. Resolution 1.339s; execution
  including verification 1.710s; controls 0.130/0.079s. Read untruncated, session
  reference retained, exact document URL returned, no follow-up search. No browser
  navigation or newly installed source claimed.
  Receipt `resource-open-c39fb28b5899433d9033dbb26ea792d6.json`.
- **Real in-flight Gmail cancellation:** **3.465s**, five HTTP, zero AI/writes.
  Cancel HTTP 153ms; no completion notice, activity cleared. Receipt
  `quality-controls-Cancel-dac4a7afdda84451b5cec3d211582619.json`.
- **Paginated GitHub read:** **55 distinct repositories / four pages / zero
  duplicates**, all observed pages consumed; **11.870s**, seven HTTP, zero AI/writes.
  Resolution 610ms, provider+verification pages 1.514/1.791/1.472/6.246s. This is
  an observed page series, not an atomic changing-list snapshot or natural AI
  summary proof. Receipt `quality-controls-GitHubPages-6783a943024544f79f8fca7c3f5e5d4e.json`.
- **Non-connector classification:** 30,000 compiled classifications in **2.1085ms**,
  zero account/credential reads, connector HTTP, provider discovery or AI.
  `hotpath-classification-13f9a940faa3409daba1e2dd7f7125d0.json`.
  This is broker overhead, not whole voice/model latency.
- **Personal reasoning remains externally blocked:** fresh 13:06 UTC controlled
  attempt returned **HTTP429 / Quota before any Gmail execution**. Four narrowed
  Gmail capabilities, one additional reasoning attempt, two backend HTTP; total
  2.990s. Activity cleared, process exited. Full Live setup completed without
  microphone/playback input. It is not natural provider-routing success or a 1011
  failure. `quality-biocylic-9d751707d01045639c3b70075348c693.json`.
  No Managed fallback, mid-session key rotation or repeated retries.

13:31 receipts above loaded assembly
`D000E28A473FDEE6A7D6DFAF734F5AA3D18D8B3EFCA1DF440B713FAF7F018374`
before the last account-handoff/navigation fences. Fresh final-source checks
must be recorded separately; do not overwrite or upgrade those original receipts.

### Final compiled-source recheck — 13:50–13:53 UTC

Release build succeeded with zero warnings/errors. Exact loaded assembly
SHA-256: `C8D1B61A468F0C25A68EF1396D06664FBEA6DA5B713B187D89CCA5B5BC61029A`,
parent `e2a1671` plus the final source diff (before its checkpoint commit).
Every diagnostic process exited; no provider content was persisted.

| Real read/control | Fresh result |
| --- | --- |
| BioCyclic | **6.094s**, five backend HTTP, zero extra AI/writes. Four search matches were inspected; one matching thread was read. The actual read contained BioCyclic and **Top 10 Best Performers**; session context returned its exact account-qualified Gmail reference. Five narrowed product stages: resolve 1.279s, search+verification 2.849s, thread+verification 1.224s, controls 97/72ms. This is deterministic product retrieval/reference proof, **not AI summary, natural routing or fresh browser navigation**. `quality-native-5d5de484f2704d80bc8051ced12cbda7.json`. The new private URL artifact is unique/DPAPI-protected; the old accepted artifact was not overwritten. |
| Docs | **3.286s**, four HTTP, zero AI/send/create; untruncated read, session reference retained, exact document URL, no new search for follow-up. Resolve 1.097s, read+verify 1.646s, controls 79/80ms. `resource-open-c5b9a2feefc94583a7d4c173cd22adfa.json`. |
| Forms | **2.529s**, four HTTP, zero AI/send/create. The already-created fenced Form remained blank, unpublished and not accepting responses; exact readback, session reference and editor URL verified. Resolve 686ms, read+verify 1.516s, controls 89/61ms. No browser navigation claimed. `resource-form-f0dc19af5473446a9b62d0705e382c65.json`. |
| Cancellation | **4.181s**, five HTTP, zero AI/writes; real in-flight cancellation, no late completion notice, activity cleared. Cancellation HTTP 102ms. `final-cancel-b080ac2b0117420b969e34b9769dc53e.json`. |
| Ordinary hot path | 30,000 compiled classifications **0.5737ms**, zero backend/provider/AI/account/credential work. Warm microbenchmark only, **not a before/after speech-speed improvement claim**. `hotpath-classification-649ca10bc61f45b7a56ccfffff5fd3fe.json`. |

All four tests use the real authenticated keyboard.wtf controller/backend/provider
path, not direct Composio/Gmail scripts. The unchanged held backend does not
contain the new undeployed initial-read recovery slice.

Two rejected Forms diagnostics remain preserved, not erased or called PASS:
the parallel four-probe attempt received endpoint HTTP429 before a verified
execution (`resource-form-e713d8a011844ea1aa0a657464109465.json`); the backend has
an existing two-operation per-UID admission fence, which this diagnostic load
can exceed. This was **not a Personal model quota request**; no model ran. The
next serial read verified the Form but its test harness had not supplied a
current human open request, so the controller correctly refused navigation
(`resource-form-8ef880906482426cb8709de3d0db3c7a.json`). The harness was corrected
to issue “Open that form,” then the final serial read/control passed. No product
safety limit, provider configuration or action authorization was weakened to
make the diagnostic pass.

The final shared-panel suites were rerun: **15/16/4 groups passed**, zero errors.
Fresh 320px light/dark and unavailable-reconciliation screenshots were inspected:
legible names/controls, unclipped logos, no horizontal overflow. They remain
isolated fixtures, not installed OAuth/Settings acceptance.

## Remaining gates and exact next step

1. Completed here: final desktop regression, real no-AI read/recall/cancel checks,
   independent source/privacy review, UI fixture reruns and source checkpoint
   `checkpoint/connectors-resource-context-ui-20261011`. No rollout from this proof.
2. The **next external gate is a functioning Personal structured-reasoning quota** to repeat
   natural vague/typo searches, long-thread grounded summaries, conversational
   follow-ups and second-provider reasoning. Deterministic native read success
   and one older natural success do not close this quality gate.
3. After repeatable quality acceptance, package a matched private candidate and
   verify installed Settings/popup, account ownership and physical product path.
4. Multi-account/account-switch/revocation/cross-device OAuth, Google public
   verification, Slack/Discord staging/custom external distribution and unavailable
   Meet/managed-branding providers retain their exact documented external gates.
5. Broader production acceptance, Managed billing correlation and independent
   Personal Live1011/speech-cutoff evidence remain separate. Do not rewrite audio
   or call the connector platform/public release READY to bypass them.

No new installer, public website promotion or production connector enablement
is approved by this source checkpoint alone. The existing public product remains
protected while these acceptance tiers are completed.
