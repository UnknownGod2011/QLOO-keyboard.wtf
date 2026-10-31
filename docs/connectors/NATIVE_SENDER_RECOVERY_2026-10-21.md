# Native sender recovery — 21 October 2026

The installed private `aacda10` and approved `5b61b99` archives remain protected. This continuation changes only `ConnectorReadReasoner.cs` and the isolated `ConnectorMailSenderRecovery.cs` helper. Speech, input boundaries, browser/tabs, translation, camera, overlay, permanent prompts, settings, backend and authentication are unchanged.

## Reproduction, not assumptions about Gmail

The screenshot shows Gmail web search suggesting a relevant conversation for `ricardo`. The same authenticated native connector was tested directly, without a model or query rewriting. These diagnostics are not natural-language acceptance tests:

| Native query | Actual result | Backend wall time |
| --- | --- | --- |
| `from:Ricardo` | No messages | 1.96 s |
| `ricardo` | Two unrelated senders/body matches; no Riccardo conversation | 2.19 s |
| `from:Riccardo` | Five messages, one verified conversation | 2.63 s |
| `from:ric`, `from:rica` | No messages | 1.40 / 1.21 s |
| One sender-scoped OR query of repeated-letter variants | Five messages, the correct conversation | 2.54 s |

Original receipts are under `.artifacts/connectors/voice-followup-recurrence-20261018/sender-native-recovery-20261021/native-semantics/` and `native-prefix-semantics/`. Thus a broad body search or arbitrary prefix loop would not solve this failure safely. Google's [API filtering documentation](https://developers.google.com/workspace/gmail/api/guides/filtering) explicitly distinguishes API matching from the web UI, including thread-wide search and alias expansion. The screenshot alone does not identify the web UI's matching mechanism.

The last installed bounded recovery (`sender-final-delivery-20261021/installed-bounded-recovery-final/`) again made one successful empty Gmail lookup, then received two Personal structured-planning HTTP 503s. The earlier typed-failure fix correctly avoided a verified semantic absence receipt, but no alternative query could be generated. This is the remaining model-availability dependency.

## Small correction

The existing reader now tries one native candidate query after a complete empty inferred sender lookup, before secondary reasoning. It uses the already generated single positive `from:` name, keeps all other topic/date/exclusion constraints, and combines broad name text with a bounded set of language-independent repeated-letter, adjacent-transposition and diacritic alternatives. There are no sender dictionaries, names, dates or example sentences in production code. At most 24 forms fit in one query of at most 2,000 characters; it consumes the existing four-search/eight-execution budget.

Only observed matching From headers survive into the recovered candidate page. Body/subject mentions, domain coincidences, unobserved IDs and unmatched references do not become selected resources. Multiple matching senders/threads stay visible for existing disambiguation. Missing header coverage remains partial; pagination and provider failures remain explicit. Exact addresses/domains, literal human queries, complex OR expressions, other connectors, task revisions and already successful searches keep their existing routes.

The recovered page returns through the existing verified selection/retention/opening path. There is no extra synthesis after successful recovery. Less familiar phonetic differences still use the existing bounded model refinement; this is not a claim to handle every possible misspelling while Gemini is unavailable.

## Evidence and delivery state

- `native-sender-red.trx`: the realistic controller scenario failed on unchanged production code when secondary reasoning returned 503, despite a possible native alternative.
- `native-sender-focused.trx`: **259 passed**, zero failed/skipped. Includes the same outage scenario now succeeding with two native executions and zero secondary model calls; body-only matches excluded; scope/cancellation, ambiguity, missing headers, local-day constraints, direct success, retained compound follow-ups and existing reader behavior covered.
- The focused test's source DLL SHA256 is `C807B10C2B3339D6612AC452B9AD8403FC66DF88F87D5AD98B85BBBDF209FD07`.
- `sender-recurrence-20261021/native-bounded-final/`: real Personal Gemini interpreted the original natural input `I had received an email from Ricardo.` It generated `from:"Ricardo"`; native recovery returned the five verified Riccardo messages and excluded two body-only matches. Search used two native calls and **zero secondary reasoning calls**. In the same session, `Can you open that in a new tab?` selected the exact retained account/thread URL with zero reads/models, then `Give me the details of that conversation.` retrieved the eleven-message thread with one native read and no secondary reasoning. All three replies completed and device buffers drained; activity cleared; protected profile hashes stayed identical. The source harness captures navigation rather than rendering Chrome, so the installed/browser check remains separate.
- Native search execution times were 1.51 s for the empty seed and 3.21 s for recovery; full search turn including spoken reply/device drain was 24.46 s. Exact open was 6.24 s and full detailed reply 33.31 s including speech. These are not local-dispatch benchmarks. Direct successful searches still incur no recovery or additional model work.
- Single packaged/installed acceptance is recorded below when complete. No installed acceptance is inferred from the diagnostics or unit gate.

The user's clarified overlay incident affected only Jarvis; the rest of the desktop stayed visible. This does not prove a virtual-desktop switch. No supported observation establishes whether the popup hid or was covered, so window/camera code remains unchanged.

## Final private installation and bounded desktop acceptance

Private **1.4.21 / 1da3da561bc4f7ed38661c2bdde0c64e4f7406e8** is installed and running. The source application tree is `b2e1233c320937a17b29143e674066cca7b1330f`. The single guarded handoff verified all 24 payloads and 29 stopped-profile files, preserved public binaries/startup/Helper registration and the public lock file, and started exactly one private GUI (PID 21324). PersonalOnly, all five saved hotkeys and Helper 2.1.21 were verified again after acceptance. No test-side token refresh, public GUI takeover or additional installation occurred.

- Installed EXE SHA256: `12D02AC5FD6E37EABE0931A02496058316D7742E62CA1CDA640BEB79F7EA2519`.
- Embedded/published DLL SHA256: `630182721D0D54D75A18081DFE9740EA041946D96DB538A441EF7BBB563DA07A`.
- Installer SHA256: `95485BEDA90E0C40B5CB7B7AC903F59DC93A3D6EA4AB5939BDDC873F1D1E6F9C`.
- Immediate rollback: archived **aacda10**. User-approved **5b61b99** and older **cdd33d3** remain preserved.
- Nine packaged contract checks passed before installation. The focused source gate remains **259 passed**; unaffected full-suite work was not repeated.

The final six-turn session ran in the **actual installed desktop application**, using original natural-language text through its supported input endpoint. Gemini created its own native queries; the test did not supply Gmail query strings. The private recording adapter prevented room audio publication. This run does not replace the earlier recorded split-speech/user-microphone evidence for unchanged input code.

| Same-session request | Independently observed outcome | Full turn, including speech/device drain and observation |
| --- | --- | --- |
| I had received an email from Ricardo. | Initial native query empty; one bounded native alternative retrieved seven candidates. Sender validation selected the correct Riccardo conversation; no secondary model call. | 22.93 s |
| Can you open that in a new tab? | Exactly one `open_url` action for the retained account/thread; Helper observed the expected normalized Gmail conversation URL and title. No connector read. | 9.44 s |
| Give me the details of that conversation. | Native full-thread read returned eleven messages; 107 provider audio chunks reached playback, with zero missing sink chunks and an empty final buffer. | 36.00 s |
| What did he say most recently? | Answer from existing context: no tool/provider call; 27 audio chunks reached playback and drained. | 7.46 s |
| I received an email from Imran yesterday. Could you find it? | One native search returned one message, with October 20 local bounds `after:1792434600 before:1792521000`. | 19.03 s |
| Could you find the email from Annika about an internship involving humanoid motion generation? | Gemini's sender/topic query returned one native message. | 20.37 s |

The seed/recovery backend calls took **1.425 / 3.567 seconds**. The thread read, Imran and Annika calls took **1.742 / 2.205 / 2.202 seconds**. All five native executions succeeded, with **zero secondary reasoning dispatches and zero Managed calls**. These timings include normal provider metadata/content hydration and are not raw Gmail latency or ordinary-command dispatch measurements. Successful first searches still avoid recovery; opening and the factual follow-up add no search. The ordinary path is unchanged from the prior approximately 9.3–9.9 ms observation/dispatch evidence; no new benchmark is claimed.

All six turns reached reply completion, empty playback buffers, Listening UI and no active long task. Provider/playback chunk counts matched for every turn (48, 19, 107, 27, 42, 53). There were no provider interruption events, buffer overflows, missing playback sinks or inactive-task errors in this run. Device output is not independent proof of physical speaker acoustics or of every spoken sentence's semantic completeness.

For the open turn, Gemini later proposed `connector_control,browser_automation`. The existing retained-opening interception returned the completed opening receipt: the actual action history contains one direct validated `open_url`, no Browser Automation workflow or duplicate navigation. The requested destination existed before those model proposals. This preserves the distinction between proposed tools and executed actions.

Evidence: `.artifacts/connectors/voice-followup-recurrence-20261018/sender-native-delivery-20261021/`, particularly `installed-six-turn/`, `installed-provider-evidence/`, `installed-acceptance-metrics.json`, the guarded installation receipts and `sendernative20261021-runtime-after-smoke.json`. The independent provider trace contains all five execution paths plus metadata hydration; no email bodies or credentials were copied into this report. Earlier failed results remain intact.

**Limits:** the final browser inspection again returned `Local browser use is disabled in Work settings`. Current exact URL/title readback is stronger than dispatch alone, but is not a fresh DOM/screenshot inspection. The same exact Riccardo destination was visually verified earlier. Fresh room acoustics/live-accent behavior, other connector providers, and every possible phonetic spelling remain outside this bounded run. Ambiguity, scope, authorization, cancellation and simulated secondary-503 recovery are covered by focused tests; no new real provider outage was forced during the successful session. Real earlier Gemini 503 failures are preserved. Unsupported phonetic differences still depend on bounded model refinement; no universal fuzzy-search claim is made.

Speech, split-input protection, tabs/foreground guards, translation and its 50,000-character limit, camera, overlay, permanent prompts/tools and ordinary commands have no production differences from approved `5b61b99`. Only five connector files differ from that baseline; only the two files identified above differ from `aacda10`. The user-reported popup disappearance and the older multiple-tab reopening limitation remain unresolved/unmodified, not silently promoted to passes. This private delivery is not a public-release approval.
