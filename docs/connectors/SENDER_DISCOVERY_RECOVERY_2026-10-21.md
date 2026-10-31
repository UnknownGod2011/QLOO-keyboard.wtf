# Sender discovery recovery, 21 October 2026

## Protected baseline

The user approved installed private **5b61b99** as stable, including speech, exact Gmail opening, tabs and fast camera capture. Source at documentation HEAD **1d1687d** has the identical application tree. Its 24 archived payloads and installer were checked before this investigation; this candidate is the rollback. Public installation, profile, authentication, hotkeys and Helper registration are protected.

Baseline EXE SHA256: `6CBB75932836AD1D068E91018F075A2681B052A39ACBE2C1FF29E611F2266C96`.
Baseline embedded DLL SHA256: `83796E52ADF2C12104DD0E5F763F035E04063BF0C988351368BBCEF5B339FFB8`.

## Confirmed causes

The actual failed desktop session was `3e28bd3489e048e9a8389db3c0aefb55`. Read-only provider execution logs were correlated with its three native-search timestamps. They show:

- Imran first restricted to **21 October**, then to **19 October**. The user confirmed asking today and then yesterday. At the originating local time, yesterday was **20 October**. The second request had the wrong day. The model generated the calendar date; the native adapter translated that date accurately, so Gmail was searching the wrong window, not failing to connect.
- The other query was **`from:Ricardo`**, not the known matching spelling `Riccardo`. It returned no match and the model stopped. A fresh installed-DLL replay of the original natural statement with `Ricardo` reproduced this premature not-found answer. The same DLL found four Imran messages and five Riccardo messages when no mistaken date/spelling was supplied. These are bounded returned message counts, not asserted mailbox totals.
- Earlier `40d8a72` reader instructions explicitly requested spelling/semantic refinement and candidate validation. The `ec4c493` native Live path correctly removed redundant reasoning for successful reads, but also bypassed bounded recovery for empty inferred searches. The latest speech-segment and translation corrections did not change search execution. Three real attempts to restore recovery through native-result instructions alone failed; those attempts were rejected and remain recorded.

## Narrow correction

Only four connector production files change:

1. `ConnectorMailSearchArguments.cs`: native `receivedOn` accepts `today`/`yesterday` as well as existing ISO dates; resolves relative days from the frozen request time and local timezone, including DST. No sender dictionaries or spelling mappings were added.
2. `JarvisConnectorController.cs`: exposes explicit local request time. A verified, complete empty inferred Gmail search enters the existing bounded reader. Nonempty searches stay direct, with no extra planner or provider request. Literal queries, other connectors, retained-thread reads and planned multi-goal operations keep their existing routes. Cancellation is checked before recovery starts. An unavailable refinement returns a failed, typed receipt rather than presenting the initial empty page as a successful semantic answer; existing retrieved-content fallback remains unchanged.
3. `ConnectorReadReasoner.cs`: accepts the already executed empty search receipt as its initial evidence, avoiding duplicate provider execution. Sender recovery checks header spelling before body mentions. Recovery stops at the first verified nonempty candidate page, before another synthesis or remaining speculative batch query; that page returns through the same direct Live receipt/retention path as an ordinary successful search. Existing query limits, scope validation, authorization and cancellation remain in use. No additional long-lived state was added.
4. `ConnectorMailDateScope.cs`: applies the existing originating local-day guard to yesterday as well as today. This also covers raw provider queries when the model omits `receivedOn`. Conflicting raw dates are rejected before dispatch with a recoverable correction receipt. Negative/conditional yesterday mentions fail closed; quoted subject text remains excluded. The existing narrower rules for changing an in-flight day are unchanged.

No permanent system prompt, global tool definition, speech/input/playback engine, browser/tab executor, translation engine/UI, camera, overlay or settings changed. No backend deployment is needed. The existing native provider retry/authority policy is unchanged.

## Evidence ledger

Evidence directory: `.artifacts/connectors/voice-followup-recurrence-20261018/sender-recurrence-20261021/`.

| Evidence | Result and limits |
| --- | --- |
| `approved-baseline.json` | Installed/archive rollback identity checked; no install performed. |
| `provider-trace.json`, `provider-details-private.json` | Actual desktop query endpoints recovered, matching timestamps/account. Credentials and email bodies omitted. |
| `baseline-nl/` | Original Imran/Riccardo natural statements, Gemini-generated queries, actual installed DLL, real provider and WaveOut capture. Both found messages; one native search each, no secondary model call. Does not override the failed spoken session. |
| `baseline-accent/` | Actual failed spelling reproduced: one native search, premature not-found. |
| `red.trx` | Two expected failures for missing local-time/refinement contract, five controls passed on unchanged production code. |
| `green.trx`, `green-v2.trx` | 601 focused connector/date/combined-follow-up checks passed each. Full desktop suite was not rerun. |
| `corrected-natural-session/` | First correction found Imran on the correct day, but sender refinement searched body mentions and failed to identify the sender. Later topic turn ended at the provider without an answer; bounded run failed. Original evidence preserved. |
| `corrected-natural-session-v2/` | Date and contextual internship search succeeded. Nested spelling guidance was ignored; sender and dependent open/details remained failed. This candidate was not installed. |
| `corrected-natural-session-v3/` | Top-level guidance also failed: one empty sender query and premature not-found. Date/topic searches succeeded. Mechanical harness completion is not semantic acceptance; its `failed:false` does not override the failed search. |
| `empty-red.trx` | Actual recovery expectation failed before the correction (zero refinement/model calls); 25 controls passed. |
| `empty-green.trx` | All 26 checks passed: recovery without duplicate seed, direct success, literal/failure/provider boundaries, cancellation, and local-day normalization. |
| `bounded-recovery-natural-session/` | Full reader found an unrelated body mention, then encountered two Personal 503s. Search not accepted. This showed why recovery needed sender-header guidance and a native-result stopping point. |
| `date-red.trx` | Both new expectations failed: yesterday had no originating date scope and wrong raw model dates could dispatch. |
| `recovery-gate.trx` | 641 focused checks passed on the intermediate correction. |
| `recovery-final-natural-session/` | Correct Riccardo and Imran found, but redundant synthesis hit 503 and left research selection partial; exact opening was safely refused. Not accepted for installation. |
| `recovered-native-gate.trx` | 139 passed, one old expectation failed because it treated originating yesterday as unsupported. It was replaced with assertions for the new frozen yesterday bound while retaining refusal of unrecognized in-flight scope changes; negative/conditional tests were added. The original failed result remains preserved. |

| `native-final-gate.trx`, `sender-final-gate.trx` | 223 focused checks passed on each applicable source revision. |
| `native-recovery-acceptance/` | Connector HTTP connection errors preceded native execution; failed acceptance preserved. A separate read-only check subsequently confirmed both isolated and installed-app connector connectivity. |
| `native-recovery-acceptance-network-restored/` | Imran yesterday succeeded. Ricardo refinement hit two actual Personal 503s; Live incorrectly described the empty initial page as not found. Not accepted, despite mechanical harness completion. |
| `outage-red.trx`, `outage-green.trx` | The incorrect successful receipt reproduced before the narrow correction; afterward the same outage yields a failed typed receipt with no semantic absence claim, one native search, two bounded model attempts and cleared UI. |
| `sender-final-natural/` | Final corrected DLL, original natural input, one session: Ricardo recovered to actual Riccardo headers, exact retained open, substantive conversation details, Imran yesterday. Four native reads total; two model attempts (one 503, one successful spelling refinement). Open added zero native/model calls; details used one fresh thread read; Imran used one native search and zero secondary models. All four replies reached device drain and UI cleared. No protected profile changes. |
| Chrome tab `1674843039` | Directly opened the exact retained account/thread URL returned by the final session. Visible Gmail conversation was **I think you're someone with a lot of ideas**, sender **Riccardo Mancini**, including the October 20 follow-up. Gmail normalized the URL to its own conversation token. This verifies the destination, separately from the replay's inert launcher. |

Final source-DLL SHA256: `1FF4039CCE3BE109E85EE361F24FC677C2A414B46E3F57C139431FBD28A7D760`. Completion/device-drain timings in milliseconds were: sender 34545/34868 (includes the provider 503 and recovery), exact open 6328/6649, detailed speech 32891/33212, Imran 17082/17401. These are full turn durations including speech, not local dispatch latency. The source gates contain 223 focused checks plus the one new outage test; earlier passing evidence is reused for unchanged subsystems.

Package/installed outcomes follow in the delivery receipt. Generated answers, device-consumed audio, URL dispatch and visible Gmail rendering remain separate proof levels. Test account-refresh calls are forbidden; the desktop app remains the authentication owner. An app-owned refresh changed the token-file hash during an earlier long replay; no test-side refresh or token-store write occurred.

## Frozen package and installed continuation

Source **aacda101f2bbfc610d6372976f588b0174425e48**, application tree `3ca9b4f0d724aded6d82d30d4d98c1f33d9c92d5`, was frozen after the above gates and source-DLL real-provider acceptance. One private package was built and installed. The prior user-approved **5b61b99** remains archived as immediate rollback; older rollback artifacts remain protected.

- Version: private **1.4.21 / aacda10**. The application's informational version deliberately omits the source suffix; the frozen build manifest and embedded assembly hash identify the revision.
- Installed EXE: `47124158A99AF8E50A70E227705A5DB71FC2CD228D7CC7BCD507192862684C90`.
- Embedded/published DLL: `67FAB5593CFF5C131035468EBFF0B8A2ABBA5CD0CDEFA2ACA3AA62DA5D54953F`.
- Installer: `245068BBED0B77D36D2222CAFFD85AF39BA1D3E78702357F02DD8BA48EEC7B70`.
- All 24 payload files and 29 stopped-profile files matched the install boundary comparison. Public binaries, startup, Helper registration and protected archives were preserved. The private GUI restarted once (PID 2264), PersonalOnly, all five saved hotkeys, Helper connected. No public GUI takeover.
- Eight packaged routing/date contract checks passed. Two original harness failures are preserved: an absent host dependency, then an incorrect requirement that the informational version contain the commit. Neither required a product change or rebuild; exact bundle/publish hashes reconcile the tested assembly.

Delivery receipts are in `.artifacts/connectors/voice-followup-recurrence-20261018/sender-final-delivery-20261021/`.

Installed acceptance failures are retained, not overwritten:

1. `installed-six-turn/` and `installed-six-turn-provider-retry/`: original natural input `Ricardo` returned an empty initial native page. Each run then hit two real Personal structured-provider HTTP 503 responses, before any alternative query could be generated. No email was selected; the dependent open did not run. Both speech/UI states settled. This is **not** an installed fuzzy-search pass, and the earlier successful source-DLL recovery does not erase these failures.
2. `installed-direct-six-turn/`: original clear-spelling `Riccardo` found messages with one native search and zero secondary model calls. The follow-up dispatched the verified exact URL once, without a native search or secondary model. The test checked too early while Gmail displayed its transient `#all/%23thread-f%3A1879251055036360289` route and generic Gmail loading title. That decimal identity is exactly hexadecimal `1a1470e0bb386661`. A subsequent Helper readback showed the expected normalized conversation URL and **I think you're someone with a lot of ideas** title. The original failed assertion and late readback remain preserved. The observation wait was corrected, keeping the same exact final destination assertion; no product edit.
3. `installed-direct-six-turn-navigation/`: **six stages passed in one installed desktop session** (`0027ef302ca14135ae8bbe7228ecc3d1`). Original natural-language inputs were interpreted by Live; no prepared provider queries were supplied. The two final inputs used the user's Daisy and paused compound recordings through the actual microphone callback path. Riccardo search, exact retained open, details, Imran yesterday, recorded Daisy search, and recorded combined details/open all reached response completion, empty device buffers, Listening state and no lingering connector activity. Both open stages produced the expected exact conversation URL/title through the Helper. No inactive-task error, buffer overflow or extra secondary reasoning call occurred. One provider interruption was recorded during the exact-open acknowledgment handoff, with **zero queued audio bytes**: two initial provider chunks were withheld, then the subsequent acknowledgment's eighteen chunks reached playback and completed. This is not a claim that no interruption event occurred or a new change to speech handling. The owned test session ended; GUI 2264 remains running.

The final six turn durations were **33.19, 6.68, 41.98, 17.75, 40.04 and 40.77 seconds**, including spoken input where supplied and full playback. Native connector HTTP times were **3.96, 1.70, 2.36, 3.01 and 1.62 seconds** for the five reads. Exact opening added no native read or secondary reasoning call. These are not ordinary-command dispatch measurements. The unchanged ordinary path reuses its prior measurements and focused no-connector-work tests; no new benchmark claim is made.

`installed-provider-evidence/` independently confirms the actual provider calls from that session: native Riccardo search returned five message IDs; its full conversation read returned eleven messages. Imran was searched with the exact October 20 local-day bounds (`after:1792434600 before:1792521000`) and returned one message. Recorded Daisy search returned five message IDs and the combined follow-up read the verified ten-message thread. Returned page sizes are not mailbox totals. These five desktop connector executions include the provider's normal metadata/content hydration calls; there was no secondary planning request in this sequence.

`senderfinal20261021-runtime-after-smoke.json` verifies the same installed EXE, PersonalOnly, all five registered hotkeys and connected Helper 2.1.21 after testing. `gui-after-smoke.json` verifies exactly one intended private GUI and no public GUI. No forced termination, public startup change, authentication reset or additional installation occurred.

**Acceptance boundary:** clear-name discovery, local-day search, retained exact navigation, detailed follow-ups, paused speech routing and activity/device completion passed the installed session. Misspelling recovery succeeded on the final source DLL with real Gemini/Gmail before packaging, but both installed recovery attempts were blocked by real secondary Gemini 503s. It remains conditional on provider availability and is not reported as an installed misspelling pass. The explicit outage receipt and bounded retry behavior are covered by the preserved red/green regression and actual failed desktop traces. Do not repeat requests indefinitely or silently change the user's Personal model/provider to manufacture a pass.

Chrome observation worked earlier for the actual Riccardo conversation, then the browser tool again reported **Local browser use is disabled in Work settings**. Current Helper URL/title readbacks are distinct from a fresh browser DOM/screenshot observation. Physical speaker quality is also separate from device drain.

Google's [Gmail API filtering documentation](https://developers.google.com/workspace/gmail/api/guides/filtering) confirms that API search differs from the web UI (including alias expansion) and recommends epoch seconds for timezone-accurate boundaries. Its [search operators](https://support.google.com/mail/answer/7190?co=GENIE.Platform%3DDesktop&hl=en) support `from:`, date constraints and OR alternatives. These documented capabilities support bounded, model-generated query refinement; they do not establish arbitrary prefix matching or reveal ChatGPT's internal retrieval implementation. No sender-specific spelling table or unbounded prefix-query loop was introduced.

## Lower-priority desktop observation

The user reports instantaneous capture; the real desktop trace measures **2.020 seconds** from photo proposal to receipt, compared with the earlier isolated 16.27-second stall. Camera and overlay code match `fb0a067` and the approved baseline. The trace shows `take_photo`, a later `open_path`, then a later `virtual_desktop_action`; it does not capture actual overlay desktop membership or window z-order. No causal claim or speculative window-management change is justified. Topmost z-order is distinct from cross-virtual-desktop visibility. The supported desktop observation tool remains unavailable, so that physical incident is unresolved.
