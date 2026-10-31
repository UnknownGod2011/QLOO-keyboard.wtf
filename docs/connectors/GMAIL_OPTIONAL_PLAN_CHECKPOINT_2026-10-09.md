# Ordinary Gmail handoff correction — 9 October 2026

Status: **source verification in progress; not public/all-provider acceptance**.
Protected starting HEAD `11b2a9e5a7381144365c9c468315d973a373ca0e`, rollback tag
`checkpoint/connectors-before-optional-gmail-plan-20261009`. Installed runtime
remains private `8f202d9` until a separately verified package/install.

**Current runtime update:** private `6402e74` is now installed and hash-verified;
the preceding sentence records the pre-install boundary. Public remains untouched.
Full installed evidence follows below; all-provider release is still held.

## Exact cause and narrow correction

The installed natural event-search failure was reproduced using the compiled
desktop controller, full current Personal Live context and authenticated product
backend configuration. Three `connector_resolve` proposals failed locally:
an unnecessary model plan classified an event search as a mailbox count, then
paraphrased rather than partitioned the human request, then proposed count again.
Zero connector HTTP calls occurred; no 1011 or account-reference failure occurred.
Receipt `receipt-NaturalCodex-4475f1b73cd646cf8e7ed8716f17f2ec.json` is preserved.

The controller now consumes a read partition only when the originating human
request actually requires the mixed Gmail content/count contract. Ordinary reads,
thread summaries and count-only requests resolve the complete original human
turn. Irrelevant model partitions neither block nor grant authority. Mixed-request
partition validation, account-reference checks, task limits, confirmations,
write fences, cancellation and session ownership are unchanged.

Runtime change: `src/Services/Connectors/JarvisConnectorController.cs` only.
No Live/system prompt, model, audio, BA, selection/translation, screen, billing,
backend/provider or public website change. Backend remains reviewed `405fe51`
at zero traffic; no new backend deployment is required for this correction.

## Evidence so far

- Four new ordinary-mail tests failed against the original code. After correction,
  287 focused connector checks passed, zero failed/skipped. Original failed receipt
  and later UI-test receipt are preserved.
- The exact natural event-search request now resolves four read capabilities and
  executes one verified Gmail search. Resolution 1,131 ms; execution including
  network/provider/verification 3,271 ms; first transcript 8,387 ms after probe
  start. Real progress sampled; activity cleared, zero writes/extra model planners.
- Natural thread-summary request resolves four read capabilities and receives
  verified bounded thread content. First transcript 8,842 ms; last 15,434 ms.
  Bounded thread content is **not** proof of comprehensive semantic answer fidelity.
- These source natural checks used the same 19,509-character instruction/62-tool
  catalog. Their 28.519/40.083-second diagnostic totals include observation and
  cleanup, not just connector execution or user-perceived answer latency.
- All-provider native logo-switch/terminal-cleanup coverage is being exercised.
  The first pixel-exact comparison exposed a test-only difference between bitmap
  decoder paths; expected assets now use the same `Image` → `Bitmap` conversion
  as the renderer, not a looser pixel tolerance. No UI runtime code was changed.
- Secret scan and whitespace checks passed. Full desktop gate, larger natural
  read/count completion, private packaging/install and installed repeat remain
  pending at this written checkpoint.

### Completed source gates

Full desktop gate: **4,328 passed, zero failed/skipped**
(`optional-plan-full-after.trx`). Cross-provider native marks are pixel-matched
to the same decoded bundled artwork for nine supported services. Service changes,
voice terminal events during owned work, cleanup and rejected late updates pass.
This is real WinForms rendering/state test proof, not an installed popup screenshot.

The full-context combined event-details/thread/count request reached all three
owned goals and independently verified a **36-message count**; thread coverage
remained partial, not falsely labelled comprehensive. Four read capabilities,
zero extra planners/writes. Resolve 772 ms; four executions 4,212/3,066/3,503/1,684 ms;
first transcript 20.106 s, last 35.340 s. Its 83.391-second diagnostic total includes
observation/cleanup and must not be described as time to first answer.
Receipt `receipt-NaturalComplexDetails-76a97870ff994a5f85a7587cf82d44a2.json`.

The separate natural count-only request recovered from the existing evidence
guard, inspected samples, then used the real count executor: resolve 725 ms,
search 2,221 ms, count 1,427 ms; first transcript 10.098 s, last 10.988 s. Sampled
logo owner was Gmail, real search/count text appeared and all activity cleared.
No start narration detected in its transcript. Its normalized count value was not
retained by that diagnostic, so it is not claimed as independent count fidelity
proof; the combined request above did retain verified goal-count evidence.

Current natural source checks are **product-path execution proof**, not proof of
audible answers or every private fact in their generated summaries. Private
packaging/install and installed repeat are next; accepted public remains unchanged.

## Private installation and installed repeat

Runtime source `6402e74af9d379d0ba7a649f413050a958623204`, archive
`dist/connector-candidate-archives/6402e74-reviewed/`.
Installer SHA256 `665440656934FE949473ECD7F038736EDA177490E4D3E382F02600A97B1DA3E5`;
EXE `D9C656B47517951263E4CF599845B8D7E838C0185204E700743957059AFEC01E`.
Protected tag `checkpoint/connectors-private-6402e74-20261009`. Older full archives
and original successful/failed receipts remain; same-volume archive moves avoided
a redundant large payload copy on the nearly-full disk.

Installer exit 0; all **24 installed payload hashes match**, only the EXE differs
from `8f202d9`. Existing profile bytes before relaunch, public EXE, public startup
and native-host registration stayed unchanged. Store/private Helper state was not
changed or repackaged. Actual installed Settings passed **10/10**, PersonalOnly,
12 catalog/seven connected capability rows, automatic connector response at
3.239 s from browser test start, no page errors; section screenshot inspected.

Installed Settings text-turn → normal Jarvis → real backend execution:

| Request | Resolution | Execution incl. network/verification | First audio / evidence |
| --- | --- | --- | --- |
| Event search | 639 ms | 2,978 ms | 5.798 s after owner/setup log; independently authenticated GET verified completed task/one operation |
| Conversation summary | 595 ms | 3,588 ms | 6.324 s; completed turn, 71 received/71 queued audio parts; bounded independent GET retry verifies one completed operation; original DNS failure preserved |
| Winning-email count request | 522 ms | One execution at 1,389 ms | 4.786 s; completed turn, 37 received/37 queued audio parts. Only one dispatched read is observed; do not claim an independently verified total from this installed answer. |
| Combined three-part request | 531 ms | **No read dispatched** | First tool batch approximately 44.876 s after owner/setup; no audio before bounded 120-second stop; FAIL/not acceptance |

The combined installed batch proposed two resolves plus `connector_control` before
any read. Control threw `ArgumentException`; one combined receipt was published
but no read followed before stop. Its exact rejected argument/cause is not proven
by exception type alone. No 1011 occurred. Do not weaken reference/ownership checks
or alter audio based on this trace; source mixed-request success is not installed
universal reliability. Original trace/failed acceptance is retained.

The separate natural source count repeat did retain **36 messages, one count page,
complete/verified=true**, after inspected search samples. Its Gmail logo owner and
search/count activity cleared, no start narration detected. This proves that source
product count, not every installed answer's semantics. Real in-flight cancellation
also passed: Cancelled task, cleared activity, zero late completion notices/writes.

Current hot-path check: 30,000 ordinary-tool classifications **2.7519 ms**, zero
connector HTTP/capabilities/planner calls. Installed ordinary arithmetic after event
search had zero connector calls. This is not physical microphone latency proof.

All installed diagnostic sessions ended goodbye/stop and verified idle. No Managed
usage, purchases, new self-email, second Form create or public/cloud deployment.

## Remaining boundaries

Seven connected service capabilities have real product reads on the prior private
candidate: Gmail (historical successful reads plus this corrected source repeat),
Calendar, Drive, Docs, Forms, YouTube and GitHub. This does not imply complete
coverage of every task or equivalence to another AI product's entire connector set.
Drive currently searches file names/metadata; Forms/Docs creates are blank-resource
operations. No unsupported capability is advertised as implemented.

Slack/Discord custom staging OAuth secrets, public Google consent verification,
Meet configuration and Reddit/LinkedIn branding remain external availability gates
documented in `STAGING_AUTH_CONFIGURATION.md`. Real lifecycle/account-switch/
multi-device and physical popup/audible answer acceptance remain distinct gates.
No second self-email or Form creation is permitted; original one-write fences stay
protected. The independent intermittent Personal Live 1011/speech-cutoff issue is
not marked fixed by these connector tests.
