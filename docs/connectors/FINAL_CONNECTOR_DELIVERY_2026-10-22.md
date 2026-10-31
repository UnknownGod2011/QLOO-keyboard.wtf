# Private connector delivery — 22 October 2026

Private **1.4.21 / `872cef73b1cbb0a28896b9f0f7d4ff2d1fbef3a6`** is installed and running. The final actual-desktop eight-turn session passed its execution, device-completion, UI and Helper-destination gates. The user subsequently confirmed seeing the correct tab, but did not listen to the speech; do not count that reply as acoustic acceptance. Public release is not authorized by this report.

## Confirmed causes and corrections

| Failure | Confirmed cause | Correction |
|---|---|---|
| Austin details after completed search | The courtesy grammar required “you”; more importantly, multiple observed conversations were rejected before Gemini could select a verified native ID. | Accept the shared optional pronoun and let the existing native-read boundary select an observed resource with fresh authorization. |
| Immediate opening failed despite a retained result | Desktop Gemini proposed `connector_execute` where the passing replay had proposed `open_url`; unresolved candidate selection was only handled on one route. | Both existing proposal routes use the same retained account/resource validation and canonical URL. The model's ID must already be observed. |
| Wrong URL reached Inbox | An alternate `#thread/<id>` proposal could bypass retained opening. | Resolve that proposal through verified native account/thread metadata. Reject unknown IDs, conflicting accounts and conflicting selections. |
| Later clarification reached a completed task | Fresh-read preparation accepted search capabilities but not a registered read proposed for a contextual clarification. | Resolve new read authority under the existing context, cancellation, account and unrelated-command guards; never reactivate or execute the old proposal. |
| Topic search returned irrelevant mail | An empty topic-plus-year query was broadened into an OR expression containing the year alone. | Preserve exact initial native execution where the desktop owns refinement, and keep numeric qualifiers outside alternatives. Existing bounded refinement remains available. |
| Generic connector error after valid resolution | Gemini omitted a character from the issued task ID; execution threw `ArgumentException`. | Reuse the existing strict reference-correction receipt. No guessed binding, automatic execution or new retry. |

Completed execution tasks still end. Useful account-scoped context and the successfully selected thread survive independently. A cached context does not grant write authority or bypass expiry, revocation or session fences. No sender, subject or regression sentence was added to production as a special case.

Production files changed since rollback `1da3da5`:

- `CommandRegistry.cs`: pass existing URL/read proposals to retained opening before general automation.
- `Connectors/ConnectorFollowupIntent.cs`: optional courtesy pronoun; reuse structural read exclusions for contextual clarification.
- `Connectors/ConnectorReadReasoner.cs`: retain numeric topic constraints during bounded refinement.
- `Connectors/ConnectorSessionResult.cs`: preserve a successfully selected observed thread using the existing primary-thread field.
- `Connectors/JarvisConnectorController.MailOpenProposal.cs`: validate current account/resource identity and build the native canonical destination.
- `Connectors/JarvisConnectorController.RetainedReadAdmission.cs`: defer unresolved selection to the existing proposal boundary.
- `Connectors/JarvisConnectorController.cs`: fresh authorized read resolution, exact native execution, typed temporary-provider errors and structured malformed-reference rejection.

There is no audio, VAD, playback, accumulated-input, tab executor, translation engine/UI, camera, overlay, permanent-prompt, authentication or hotkey change in this delivery.

## Verification and preserved failures

The original parser, ambiguous selection, terminal-task, wrong-link and topic failures are retained in `AUSTIN_FOLLOWUP_RECOVERY_2026-10-21.md`. The actual desktop failures on intermediate `90374b5` and `5934e65` were not overridden by their successful source replays. Both intermediate packages remain archived; neither is the approved rollback.

The final additional malformed-reference reproduction failed all four cases before correction. Afterwards, **50 focused checks passed**, including retained opening, fresh clarification and authorization fences. Earlier 493 passing checks and the separately explained synthetic provider-discrimination fixture remain evidence, not a fabricated all-green aggregate. Packaged contract checks passed for reference parsing, native sender recovery, local-day handling and the protected translation route.

The final source replay (`desktop-proposal-eight-turn-v4`) used original natural-language requests, real Personal Gemini/Gmail and product device playback. All eight outcomes passed: three searches, a necessary thread read, three exact openings and a factual follow-up. Four native executions, zero secondary reasoning calls, zero managed use. Every speech capture drained and every activity cleared. Its session-file hash changed because the running GUI performed its normal token refresh; the isolated probe transport explicitly forbids refresh. App logs verify that exact rotation. Other protected settings/credentials were unchanged.

The preceding `v2` run contained a genuine empty Gemini response after a successful detail read: zero provider text and audio. It failed the bounded wait. `v3` delivered the Austin recap but failed earlier searches because of the malformed ID. Its generic host completion flag was explicitly rejected as outcome acceptance. These failures remain preserved. The final source and installed runs did not reproduce empty speech; that does not establish that the external preview model can never return an empty response.

## Actual installed-app session

One logical session used the installed application and its real connector/orchestration/Chrome/device paths. Inputs were natural-language text through the supported private application endpoint; no prepared Gmail query or native resource ID was supplied to Gemini. Prior recorded split-speech evidence is reused because that code is unchanged.

| Request | Observed full-turn seconds | Outcome |
|---|---:|---|
| Find the recent email from Imran | 19.10 | Native search, speech/device drain, UI clear |
| Can you open that in a new tab? | 10.09 | Verified Imran URL; Helper reached matching Gmail conversation title/URL |
| Find the email about Shipaton 2026 | 17.25 | Native topic search, speech/device drain, UI clear |
| It was sent by Austin | 19.55 | Fresh authorized clarification search; no inactive-task error |
| Could give me more details on that conversation | 32.26 | Fresh thread read, provider response/device completion, UI clear |
| Could you open that in a new tab? | 8.68 | Canonical Austin destination and matching Helper metadata |
| What did he say most recently? | 14.29 | Retained context route, no new backend read, speech/device completion |
| Open it again | 6.15 | Same verified Austin identity opened again |

All eight completed with empty playback buffers and no active connector indicator. Logs contain no warnings, inactive-task errors, provider interruption or buffer overflow. Exactly four native executions occurred. The only AI-backend dispatch was the Personal Live session; no secondary reasoning or Managed dispatch occurred. The test-owned session ended cleanly.

These totals include spoken content and destination observation. Native calls took **2.489, 1.809, 2.251 and 1.611 seconds**; account/task resolution took **1.195, 0.598, 0.610 and 0.672 seconds**. The three exact openings needed no backend read or secondary model call; first provider audio arrived **1.88, 1.47 and 1.53 seconds after URL dispatch**. No fresh ordinary-command benchmark is claimed: its earlier approximately 9.9 ms result and zero-extra-connector-work coverage are reused with the ordinary execution path preserved.

Helper URL/title observations prove more than dispatch, but are not DOM/rendered-content inspection. The browser tool again reported local browser use disabled. The user's subsequent observation confirms the correct visible tab. Physical acoustic quality and a fresh live-microphone version of this exact sequence remain unverified; no further manual test was requested after the user said they were busy.

## Installation, protection and rollback

| Item | Identity |
|---|---|
| Installed source | `872cef73b1cbb0a28896b9f0f7d4ff2d1fbef3a6` |
| EXE SHA256 | `E29C4B93476ED2C5B6E27B512357830D37D0524079E550D2BB55154D989B9499` |
| Extracted installed DLL SHA256 | `07C6C9F55C1BE9C8D0F7F1711BC7872CD8EDE56BD399ADB586B395A852A8B92D` |
| Reviewed package manifest SHA256 | `69D8F0FAAD3469BBF5E2DB04DCA01903B0384E9C197430AEAD3737F1A1B98878` |
| Approved rollback source | `1da3da561bc4f7ed38661c2bdde0c64e4f7406e8` |
| Rollback manifest SHA256 | `3FEE6B568F16C5AF6E460DC24FF22EF957D3A7AA2C62AA3E2F1E295DC6283AA7` |

Package: `dist/connector-candidate-archives/872cef7-reviewed/`; rollback: `dist/connector-candidate-archives/1da3da5-reviewed/`. Older `aacda10`, approved `5b61b99`, `cdd33d3` and `fb0a067` packages remain preserved. The installed embedded DLL equals the packaged/published DLL. All 24 payloads and all 29 stopped-profile files passed comparison. PersonalOnly, all five hotkeys and Helper 2.1.21 were verified after the smoke test; exactly one private GUI remained (PID 19024).

A duplicate public GUI appeared during packaging alongside native-bridge connection errors. The first handoff correctly stopped without changing the installation. Its precise launching parent had exited, so the auto-start cause is not conclusively proven. After confirming no human activity, the existing normal-exit path closed only that idle duplicate; the native host was not stopped. The existing public lock was then held through the single private update. Public executable bytes, saved configuration, startup and Helper registration comparisons passed; no product startup change was made. All diagnostics ran hidden.

Core source blobs for speech/private recording, ordinary automation, Browser Automation coordinator, translation, app/hotkeys and overlay are identical to `1da3da5`. Browser/tab and translation evidence is reused, not mislabeled as a fresh physical test. No new ordinary-command reasoning or connector call was introduced.

## Remaining limits

- **FIXED & VERIFIED in the bounded final session:** current sender/topic searches, Austin clarification/details, completed-task follow-ups, verified exact opening/reopening, UI cleanup and device response completion.
- **PRESERVED:** stable speech/input logic, first/last/positional tab executor, translation/50,000-character admission/UI, fast camera path, normal ending logic, settings and Personal mode.
- **UNVERIFIED:** physical speaker clarity and live accent/pause recognition for this final exact sequence; arbitrary phonetic/topic misspellings; a fresh rendered DOM inspection.
- **STILL UNRESOLVED / unchanged:** Photos-close popup disappearance and the older multiple-tab reopening limitation. Neither justifies a speculative window or browser change here. Earlier Gemini 503/empty-response failures are external-provider limitations, not proof that no matching email exists.

Primary ignored evidence: `.artifacts/connectors/voice-followup-recurrence-20261018/austin-final-validation-20261022/` (installation, protected boundaries, actual desktop, timing, final runtime); `sender-recurrence-20261021/desktop-proposal-eight-turn-v4/` (source natural-language/device/content); `austin-followup-recurrence-20261021/` (original failing and focused regression checks). Private contents and credentials are not included in this report.
