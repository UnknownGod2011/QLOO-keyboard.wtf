# Gmail fresh-request recovery — 13 October 2026

This narrow correction follows the user's physical test of installed `67b9638`.
The rollback is `checkpoint/connectors-before-gmail-robustness-20261013`, pointing
to `67b9638c7716cc697f8f0cb1b271b1cb5508dd1d`. The restored ordinary Jarvis boundary
at `0fcdbb0` remains protected. The only application file changed is
`src/Services/Connectors/JarvisConnectorController.cs`.

## Incident evidence and limits

The physical private app log shows Gmail resolution at 21:03:57 IST, execution at
21:04:00, and HTTP 200 replies. The held backend independently records a completed
`mail_search`, one provider attempt and no provider error. At 21:04:18 the normal
assistant-answer boundary completed the task. Later `connector_execute` proposals
at 21:04:24 and 21:05:00 returned locally in about 1–2 ms, without another backend
request. No reasoning 429/503 was recorded in that incident sequence.

This supports a completed-task reference being reused, rather than a Gmail
connection failure. The exact model arguments and recognized Gao/Winter utterances
were not retained in these privacy-limited logs, so pronunciation, the original
query and its result quality cannot be reconstructed or blamed. A controlled
reproduction establishes the completed-reference defect independently.

## Correction and authority

An already-proposed connector read using a completed task can resolve a clearly
new observed human read, on the same authenticated provider/account and logical
session. It returns the new task and capabilities to the model. It does **not**
execute the stale proposal or copy its query, cursor, object IDs or write approval.
The model must form fresh arguments for the current request. Existing date/content
permission, account validation, read-plan, cancellation and write fences still run.

Recovery refuses same-turn proposals, ordinary navigation, other providers,
ambiguous account changes, write requests/capabilities, expired/failed/cancelled
tasks, pending operations and changed session/account ownership. A human request
change during resolution invalidates the newly resolved empty task. Unrecovered
terminal execution reports `task_lifecycle` and `connector_disconnected=false`,
with explicit guidance not to describe a local rejection as a Gmail outage.

This boundary runs only inside an already-proposed connector execution. There is
no new normal-turn classifier, permanent prompt/tool, model call, audio work,
transport change, profile migration or backend deployment.

## Evidence

Local receipts are in `.artifacts/connectors/robustness-20261013/`:

- `completed-task-red.trx`: three causal fresh-read failures against the original
  application; six negative authority cases already passed.
- `connector-focused-v1.trx`: 403 passed, zero failed/skipped, including 15 new
  recovery/authority/race cases and existing controller/person-retrieval checks.
- `real-new-request-v2.json`: real CommandRegistry/controller/PaidAccountService
  through held backend `00031-vup` and Gmail. Three Gao matches, then five Winter
  internship matches and one actual thread body; same account, fresh task,
  no stale-query replay, three native executions, zero model calls, no transport
  errors and cleared activity, in 10.05 seconds. This is a typed-input native
  product-component test; it does not prove natural Live tool selection or speech.
- The first helper attempt used metadataLimit=12, exceeding the existing maximum
  of eight; the backend rejected it before provider execution. The helper alone
  was corrected. The original failure receipt is preserved.
- The v2 helper's unscoped recall/open correctly required a task choice because
  both Gao and Winter results were retained. The final packaged probe supplies
  the returned Winter task reference. This did not justify changing recall rules.

The full desktop TRX, final source/binary gate record, private package manifest,
physical install/profile preservation and packaged native/hot-path receipts are
recorded separately in this directory. Consult `final-delivery.json` for actual
installation/readiness; this source document alone is not installed acceptance.
The unchanged backend retains its separately verified 438/438 emulator gate.

## Protected state and manual retest

Public 1.4.21, public startup, Helper, old candidates/exports, settings, mappings,
Personal configuration and default staging traffic remain protected. Held backend
source/tree is unchanged. There are no external sends, replies, OAuth edits,
accounting mutations or microphone sessions in this acceptance run.

After installation, repeat two distinct Gmail searches in the same Jarvis session
(Gao, then Winter internship), ask a follow-up and open the selected conversation.
Observe continuous activity and its return to normal after completion. Speech,
Live model choices and visible browser navigation remain physical acceptance.
