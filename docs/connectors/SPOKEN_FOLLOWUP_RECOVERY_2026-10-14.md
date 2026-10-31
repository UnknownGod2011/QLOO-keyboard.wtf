# Spoken follow-up recovery — 14 October 2026

The user's latest test of `a2ca3172b4d58798c6326fa264c3e601e51b2807`
supersedes universal follow-up acceptance. Native Gmail search succeeded; Personal
structured reasoning returned HTTP 503; later connector executions were locally
rejected as terminal. Logs do not retain the exact speech or tool arguments, so
they cannot uniquely identify which recognition gap caused each rejection.

## Reproduced integration gaps

* The details recovery hook covered resolve, selected mail executions and recall,
  but not status/result/guide proposals pointing at the previous task. Four added
  stale-control cases failed before the patch while seven earlier cases passed.
* `CompleteJarvisUserTurn(false)` cleared the current human text while preserving
  its identity. Four causal cases lost details/opening authority between tool-only
  frames. Connector input now has its own bounded continuation snapshot; ordinary
  routing cleanup remains unchanged. Final answers, new input and session end
  clear/replace it; tool frames never extend its original 60-second lifetime.
* Common details requests and offers (including “more information”) missed the
  anchored recognizer. The read grammar now handles these without treating a
  mixed write/cancel/navigation offer as read authority. Patterns remain small
  cached .NET 8 NonBacktracking expressions; the .NET 10 isolated phrase harness
  was insufficient to establish runtime compatibility, caught by project tests.

Recovery stays inside connector dispatch. Fresh human reads use retained native
evidence or a fresh same-account thread read; model-supplied old IDs/queries do
not regain execution authority. Collection of this turn's actual operation keeps
its existing delivery/ownership checks, including after a second reasoning outage.
The Live receive/dispatcher replay now covers both protocol shapes, terminal
tasks, natural affirmative/details requests, tool-only boundaries and retained
opening overriding an Inbox proposal.

## Protected boundary and evidence

Only application files changed in this pass: `CommandRegistry.cs` (connector
snapshot only), `ConnectorFollowupIntent.cs`, `JarvisConnectorController.cs`.
No Live/audio implementation, models, permanent prompt/tools, UI dimensions,
Smart Writing, Browser Automation, saved routing/mappings, backend, billing,
startup or Helper edits. Prior source and package remain protected by
`checkpoint/connectors-before-spoken-followup-20261014` and the `a2ca317-reviewed`
archive. Public baseline remains 1.4.21 / `07a5766`.

Physical readback confirmed saved `PersonalOnly`, Gemini provider, and Personal
Live/StructuredPlanning runtime. “Jarvis” names the assistant, not Managed billing.
No mode/config change was made; free-tier/billing eligibility was not inspected.

Gate, real native-read, package, installation and preservation receipts live in
`.artifacts/connectors/spoken-followup-20261014/`. Their final receipts, not this
source note, establish delivery identities and test totals. Native message evidence
with simulated reasoning failure is not proof of live AI-summary quality. Inert
URL dispatch is not fresh browser rendering or physical spoken acceptance.

## Bounded follow-up speed improvement

The user's later speed report prompted a measured native search: 5.36 seconds
total, including 1.32 seconds for resolve and 3.60 seconds for native search, with
no reasoning request. The detailed Daisy probe took 15.67 seconds initially and
needed individual message reads because the bounded thread response was clipped.
These are different operations; search speed must not be improved by claiming
unread thread content was read.

A fresh single-thread details follow-up also repeated complete individual body
reads. It now carries a bounded copy of eligible retained message receipts into
the new task. The thread itself is fetched again. Only exact message IDs observed
in that fresh same-thread response can reuse verified, complete bodies from the
same account and retained scope. Cancellation, current permissions and ownership
checks run before reuse; later guidance revisions disable it. Reused evidence
still consumes the normal reader budget, missing/clipped bodies still require
retrieval, and task disposal clears the temporary copies. No backend, ordinary
Jarvis, permanent prompt or tool changes are involved.

Final speed/gate/package receipts must establish the new source and measured
benefit. The first 5,508-case green gate preceded this additional narrow change;
it is preserved separately and must not be mistaken for the final gate.
