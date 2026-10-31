# Gmail outage fallback and voice readback — 13 October 2026

This follows the user's physical test of private source `9072f87`. The exact
pre-change rollback is `checkpoint/connectors-before-voice-delay-review-20261013`
(`9072f8782f14cb1cf2685411148465f13286e92a`). The installed pre-change executable
was verified as `CFAE4A47A1B07832629475AF030DCE1C1960AF35787A71163F44F596D73D1C2E`.
The previous iteration was `67b9638`; ordinary Jarvis remains frozen at the
user-accepted restored boundary `0fcdbb0`.

## What the incident actually establishes

- The 21:56–21:59 IST session had two finalized human turns followed by completed
  Live turns containing no text, audio or tool call. The first connector proposal
  arrived about 33.5 seconds after the earlier finalized input. Connector UI
  cannot represent an owned task before that proposal. Privacy-limited logs do
  not retain the utterances, so they cannot establish exactly which transcript
  was Gao or hello. This pre-admission silence is not fixed by this connector patch.
- Subsequent Gmail search/body operations succeeded. Backend telemetry confirms
  eight completed Gmail operations across the incident sessions, with no provider
  errors. Two later reasoning attempts each received Personal Gemini HTTP 503
  and the existing single bounded retry also failed. Native Gmail success and
  reasoning unavailability must remain distinct.
- One later task received ten local `connector_control` result polls in roughly
  ten seconds. They were local polls, not ten Gmail API requests. A pending reply
  should tell the model to yield until the existing completion event arrives.
- Across the three reviewed sessions, 19 speech-turn summaries recorded no
  missing playback sink. Four provider interruption events were recorded; two
  discarded queued output after microphone input was detected. This does not
  identify whether the input was speech, echo or noise, or prove audible quality.
  Startup was approximately 1.8–2.6 seconds, microphone readiness 0.96–1.15 seconds.
  Neither laptop load nor a new audio regression is established by these logs.

GeminiLiveConversationService, its microphone/connector partials, VoiceCapture,
Personal Live transport, CommandRegistry and JarvisAutomationService have identical
Git blobs across `0fcdbb0`, `67b9638` and `9072f87`. This pass changes none of them.
No audio/VAD/STT/playback/model/session-setting optimization is justified here.

## Narrow correction

The existing reader's partial-outage result now includes up to four verified
Gmail candidate headers (sender, subject and original Date header). It filters
to the current retrieval scope and verified completed read receipts, deduplicates
observed message IDs, bounds encoded output, and includes no bodies or snippets.
Headers are explicitly external evidence, not instructions, a complete summary,
confirmed semantic match, mailbox total or selected opening target. Existing
temporary evidence, content permissions, account/credential ownership, correction
budgets, cancellation and write safety are unchanged. No extra provider/model
request or automatic Managed fallback is introduced.

Pending execution/result receipts now explicitly tell the model not to poll or
repeat execution, to end the tool turn and listen, and to acknowledge new human
questions without cancelling the owned task. This is task-scoped receipt guidance,
not a permanent prompt/tool change or a new concurrency mechanism. Receipt tests
do not prove that every natural Live model turn will comply.

## Evidence and limits

Receipts are under `.artifacts/connectors/voice-delay-20261013/`:

- `gmail-outage-red.trx`: three reproduced missing-header failures against the
  prior implementation, for reasoning 503, quota and timeout.
- `connector-focused-v1.trx`: 449 passed, zero failed/skipped, including current
  scope/content restrictions, updates, ownership, interruption, cancellation,
  partial evidence, and pending-result receipt behavior.
- `real-gmail-simulated-outage-v2.json`: real CommandRegistry/controller,
  PaidAccountService, held backend `00031-vup` and authenticated Gmail. Dr Gao
  search found both candidate names and read three threads. Injecting typed
  reasoning 503 returned partial verified headers, `ai_provider_unavailable`,
  retained context and clean activity in 9.34 seconds. Four native executions,
  zero actual AI calls/charges, zero follow-up requests. This is a controlled
  native product-component test, not natural Live/spoken synthesis acceptance.
- The first real probe separately received HTTP 503 at backend resolution, before
  any Gmail execution. Cloud Run confirms that 35.2-second failed response. Its
  underlying cause is unproven. One fresh bounded read several minutes later
  succeeded. No automatic retry policy or deployment was changed in response.
- `protected-source-comparison.json`, physical log metadata and backend readback
  preserve the comparison and incident evidence without tracked mailbox content.

The final full desktop gate, frozen source/binaries, private package, installed
payload hashes, real packaged replay, hot-path measurements and profile/startup/
Helper preservation are separate receipts. Consult `final-delivery.json` for
actual installation/readiness; this source document does not claim those stages
have already finished. The backend is unchanged, retaining 438/438 emulator proof.

Public 1.4.21, default staging traffic, OAuth, billing, startup, Helper, old
checkpoints, settings/mappings/Personal preferences and once-write fences remain
protected. No microphone or unattended Live session is started for these tests.
After delivery, naturally repeat a Gao/Winter request, a follow-up and a brief
human interruption. Pre-admission silent turns and audible voice quality remain
separate physical observations; neither is claimed fixed by the fallback.
