# Gmail retained objects, admission latency and synthesis readback — 14 October 2026

This continues `0d1c546eb6e87eb2fb2a832f09ac9e105b25aa00`, without replacing the
connector architecture. The pre-change rollback is
`checkpoint/connectors-before-gmail-multiple-20261014`. The preceding `bbcff17`
archive and both old private installers remain protected. Resolve the new
`checkpoint/connectors-gmail-exact-multiple-20261014` tag for the source checkpoint;
package/installed identities belong to the separate delivery receipt below.

## Protected-boundary comparison

Public 1.4.21 desktop source `07a57667055a1c47c7a1990ec0d45d7f20a0cd48`
and released equivalent `987e4d4eaef6130f29f0818a3bc1a7a42c88f969` have the same
`src` tree, `0410589b443ce4d67c07bbe179ef5d38694fe394`. Git common-dir and origin
remain the protected local public-release exports, not GitHub.

The 49 public-to-pre-fix application paths were classified before editing:

| Paths | Classification and boundary |
|---|---|
| All 15 `Services/Connectors/*` files | Required connector capabilities, retrieval, read constraints, task/result/reference ownership and bounded reasoning. |
| `ConnectorActivityLabel.cs`, `ConnectorProviderIcon.cs`, 12 `Resources/icons/connectors/*.png` | Required connector activity presentation and provider assets. |
| `GeminiLiveConversationService.Connectors.cs`, `PaidAccountService.Connectors.cs`, `WebSettingsService.Connectors.cs` | Narrow connector entry points using existing AI/account/Settings owners. |
| `CommandRegistry.cs`, `JarvisAutomationService.cs` | Existing shared executor integration, deterministic connector admission, retained object opening, and the already accepted saved Link/workflow authority restoration. Ordinary navigation remains authoritative. |
| `GeminiLiveConversationService.cs`, `KeyboardWtfApp.cs` | Connector broker wiring, task-owned reply publication/offer observation, and quick-action ownership wiring; not a new general router. |
| `GeminiLiveConversationService.MicrophoneControl.cs`, `Models/PopupQuickAction.cs`, `PopupQuickActionButton.cs` | Existing narrow mute/Done ownership and tiny control. |
| `Models/KeyboardWtfState.cs`, `VoiceOverlayForm.cs` | Connector activity ownership, restored popup dimensions/white meter and existing quick-action presentation. |
| `VoiceCaptureService.cs` | Earlier Smart Writing Done/completion ownership safety; shared regression surface, but no new failure demonstrated. Left untouched. |
| `SettingsService.cs`, `SettingsSectionRegistry.cs`, `WebSettingsService.cs`, `Resources/web/settings.html` | Connector Settings and the existing quick-action preference. |
| `Program.cs`, `KeyboardWtf.csproj`, `PaidStagingConfiguration.cs` | Private flavor/profile isolation, connector resources and held backend pin. |

The preceding `bbcff17` to `0d1c546` change comprised four connector files,
a narrow pending-publication hook in Live and callback wiring in the app.
No unexplained application change was found outside those accepted boundaries.
This does not mean public and connector desktop source are byte-identical.

This pass changes five application files only: follow-up intent, session result,
controller, read reasoner and the existing retained-opening hook in CommandRegistry.
Audio format, microphone publication, VAD, STT, playback, Live/Personal/Managed
transport and model selection, popup dimensions, Smart Writing, BA, memory,
selection, translation, screen analysis, billing and Helper source are unchanged
from `0d1c546`. No backend deployment or traffic change occurred.

## Identifier failure and exact opening

The user's invalid `CtWLlTTscGXjJqpTGnBlql` value is not a Gmail REST hexadecimal
message/thread ID. Its originating object cannot be established from retained
logs: no matching provider receipt was found. No product constructor was found
that creates `#inbox/<opaque ID>`; the plural request bypassed the former singular
retained-opening grammar and left a proposed URL to generic navigation.
Earlier acceptance tested a different, explicitly selected native reference;
it did not prove this plural spoken flow.

Gmail REST message ID, REST thread ID, RFC Message-ID and Gmail UI tokens are
distinct. Native provider receipts supply message/thread IDs, not a universal
browser permalink field. References now retain observed thread/message IDs,
account, subject, participants, Date header and content-observed state. They do
not persist whole bodies or use model-supplied IDs as navigation authority.

Opening parses the object independently of tab/window modifiers. “Both/all” uses
the complete bounded retained set; two model opening proposals share one dispatch
receipt for that human turn. Wrong account/session, cancellation, incomplete sets
and ambiguous singular selection fail without Inbox substitution. Unknown/opaque
IDs are rejected. Dispatch success is explicitly not browser-render proof.

The two actual Daisy-related native thread IDs were independently observed in
Gmail's rendered DOM. Both product-returned account-qualified `#all/<native hex>`
destinations opened in distinct real Chrome tabs and canonicalized to distinct
Gmail UI URLs, with both matching conversation headings visible. Neither was
Inbox, blank, or a search page. One result is the main conversation; the other is
a related transfer notification. This is real-browser proof for these two native
references, not a claim that arbitrary provider IDs are safe or that Live speech
was exercised. No Daisy URL is hard-coded in application source.

References: [Gmail message resource](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.messages),
[thread resource](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.threads),
[IMAP thread identifier semantics](https://developers.google.com/workspace/gmail/imap/imap-extensions#access_to_the_gmail_thread_id_x-gm-thrid).

## Measured timing and response ownership

An explicit 500 ms connector resolve display grace accounted for an avoidable
506–507 ms activity delay after ownership. It is removed only after deterministic
connector admission. Warm compiled measurements publish ownership/display state
in 7.13–20.80 ms, before the first fake provider request; display state is available
at the UI event (0.027–0.082 ms measured extra). Cold JIT was about 150 ms. Actual
speech-to-tool selection and window paint are not measured by this fixture.
The normal popup refresh and dimensions are unchanged.

The existing exact-reference parser rebuilt NonBacktracking regexes through the
shared small regex cache, adding about 20–22 ms to two ordinary opening cases.
A fixed-pattern cache and cheap anaphoric-word check remove that connector-owned
cost; no generic navigation implementation changed. Current source median local
total times: Gmail 7.00 ms, Instagram 5.32 ms, Discord 190.15 ms, four sites 189.41 ms;
math input observation 0.0069 ms. Twelve measured repetitions each made zero
connector account/backend/provider/model calls and acquired no connector UI.
Discord's saved HTTPS workflow still wins. Public Discord/four-site comparisons
were about 189 ms; small public opens varied 3–11 ms. This does not explain every
microphone/network/model delay in the user's physical session.

Permanent base prompt/tool payload is unchanged this pass: 19,481 characters,
62 tools, 30,887 serialized tool JSON characters. Public baseline was 19,362 /59 /
28,580. Provider tools remain task-scoped. Existing Live pending-reply gating is
preserved; recorded physical phases showed provider output/audio suppressed until
connector collection. No Live/audio edit was needed here.

## Real synthesis failure discovered during acceptance

The initial Managed Daisy run retrieved native search/thread evidence but exhausted
its reasoning attempts. An empty accepted citation set caused successful native
references to disappear, so a later “Yes” reached terminal-task rejection. The
controller now retains current-revision verified native receipts even when there
is no accepted synthesis citation. This grants neither semantic selection nor old
execution authority. Multiple candidates clarify; a selected/unique retained
thread reads through a fresh task.

A separate full-details probe retrieved the thread plus seven missing bodies.
The returned JSON was cut inside its answer. The bounded diagnostic measured
2,305 reasoning tokens and 77 output tokens under a 2,400 output ceiling.
[Google documents the combined thinking/output limit](https://ai.google.dev/gemini-api/docs/generate-content/thinking).
Only long-content synthesis now requests 4,096 tokens, within the existing
structured route ceiling; small plans remain 2,400. Two consecutive malformed
JSON responses stop further inference and yield typed `ai_invalid_response`
with retained verified evidence. No model/transport/global prompt change was made.

The larger-output real Managed attempt was declined before a paid response.
A separately bound Personal attempt encountered provider failure; the read-only
probe refused a subsequent vault-status write. No credential/configuration was
changed, no fallback/rotation was performed, and no further paid/quota retries
were attempted. Consequently **a complete real AI-generated Daisy summary is not
accepted** in this delivery. Do not relabel fixture summaries, native body reads,
or the rejection fallback as successful natural synthesis.

Real native Daisy plus simulated reasoning 503 proves a fresh “Yes” task, one
thread read plus seven body reads, retained question/reply context, exact selected
thread dispatch, zero follow-up backend requests and clean activity. Real native
Gao/Winter readback returns five candidates each, safely replaces completed task
authority without replaying the old query, reads content, retains context and
dispatches the Winter exact thread. GitHub exact repository/release/asset work is
preserved and separately smoke-tested.

## Gates and delivery authority

All receipts are under `.artifacts/connectors/gmail-exact-multiple-20261014/`.
Focused final: 664/664 ownership/opening/cancellation checks; synthesis/retention:
60/60. The earlier broad run was 5,413/5,415 with one short regex timeout and one
loopback timeout; both passed the same-binary isolated control. That broad run
predates the subsequently reproduced synthesis/retention correction and is not
the final source gate. The final frozen source broad run,
`exact-multiple-full-frozen.trx`, passed **5,417/5,417, zero failures/skips**.
`final-gates.json` records every outcome, any same-binary controls and source pins.
Backend 438/438 is reused only with its unchanged component tree verified.

`final-delivery.json` and the unique archived `package-proof.json` identify the
one new private build/install and preserved physical files. Until those receipts
exist, source completion is not installation. Public EXE/startup, Helper,
Settings/mappings/Personal vault and older private archives stay protected.
Normal session-token refresh is not a configuration mutation.

Final manual/real gates still include natural recognition, audible one-response
behavior, cancellation speech and complete AI summary quality. This checkpoint
must not be called universally connector-complete while synthesis acceptance is
blocked or merely partial.
