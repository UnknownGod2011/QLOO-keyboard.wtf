# Repeated follow-up and protected behavior audit

Read-only reconciliation completed before application edits for this pass.

## Identities and comparison

- Current source HEAD at audit: `ba9e793e49134a8096d71f0e581cf8e7ba523bae`.
- Installed application: `2f5ea15b3b2279738538d9b289759f41fc17dd4f`, EXE SHA256
  `898D0677ED16438C853C7F48BFE6503F628CA4B33B58D299AF844EDD61A19C4D`.
  Physical capture proves private GUI17416, PersonalOnly, intended private path.
- Before tab patch: application `4be3d21`, documentation checkpoint `157142d`.
- Tab patch: application `7300c9b`; reversal application `2f5ea15`.
- Public:1.4.21 / `07a57667055a1c47c7a1990ec0d45d7f20a0cd48`.
- Pre-edit rollback tag: `checkpoint/connectors-before-context-audit-20261016`
  points to exact audit HEAD. Existing source/package archives remain protected.

Relative to4be/157142d, exactly FOUR application files differ:
ConnectorFollowupIntent, ConnectorReadReasoner, JarvisConnectorController and its
RetainedReadAdmission partial. CommandRegistry, JarvisAutomationService, Live,
VoiceCapture and app wiring match the pre-tab candidate byte-for-byte. Tab source
and tests are exactly restored. Helper source also equals public. No audio
parameters, transport/model, BA engine, memory, selected text, screen analysis,
settings, billing or permanent prompt/tool changes were introduced by2f5.

Public comparison has52 application paths:35 connector additions/resources and
17 existing integrations/staging configuration/quick-action/popup/mute/Smart
Writing completion ownership. No newly unrelated application path was found.
AudioRecorderService and AiProviderRegistry equal public. Historical
VoiceCapture owner-safety changes are unchanged since older working candidates.

## New physical failure

Ignored evidence `.artifacts/connectors/repeated-followup-audit-20261016/`
contains exact physical log capture with SHA256 A0E344...D1C2F9. Session
92950d9bc2e1446086888d73934cbb64, local23:03–23:05, installed2f5:

- Turn4 native Gmail search succeeded; answer completed23:03:48 and execution
  task correctly became terminal.
- Turn5 follow-up proposed connector_execute23:03:58; local controller rejected
  terminal_task in about10ms, with ZERO new resolve/provider/reasoning request.
- Turn6 fresh resolve/search succeeded; turn7 repeated the terminal rejection.
- Turn9 and10 model proposed end_conversation; neither ended the session. The
  user finally used the Jarvis hotkey23:05:38 to end it.

No latest-turn503 explains these inactive-task responses. Exact spoken text and
tool arguments are not retained by physical production logs; do not invent them.

## Root causes

**Read recovery is still overly specialized.** Results outlive execution tasks,
but TryReadRetainedThreadAsync first requires MailDetails/Affirmative grammar and
then Gmail. Other legitimate read questions and read-tool proposals fall through
to RunAsync's terminal rejection. Fresh unavailable-task recovery only handles
mail_search with another imperative/provider grammar. Extending phrases one by
one has not fixed that boundary. Bodies also remain in execution evidence;
pruning the owner removes immediate body reuse even while references survive.
Correction must select current retained context independently of terminal task
execution, return verified available facts, and use a fresh same-account read
when needed. Write/cancel/account/expiry/permission fences remain mandatory.

**Ending has an existing wording defect.** ShouldAutoEnd accepts "end the
conversation" but not "end the conversation now" or a polite "could you" wrapper.
The method and end-tool guard are identical public→pre-tab→current. The real
end tool is handled inside Live before CommandRegistry; connector broker
collection cannot intercept it. Fix only standalone explicit phrase recognition,
with negation/future/translation/compound guards. Do not broadly rewrite Live.

**Lea's natural wording misses participant preparation.** "I just received an
email from Lea. Could you look into it and give me the details?" does not match
the imperative-only ConnectorPersonSearch.Query. Body intent is recognized, but
participant search then depends on the model's query. Existing backend supports
native sender queries, verified metadata, bodies, cursors and bounded refinement;
no backend deployment is needed. Normalize only the clear same-object read
clause; do not invent a date/address or discard restrictions.

**First-N tab closing is absent in the restored implementation.** It supports
last N and single first/index, so "close first two" can fall to a conflicting
model last_tab_count proposal. Helper ordering is correct. The old first-N patch
is absent, not still active. After connector correction, test a narrow
authoritative first-N route using existing stable Helper IDs and exact close
receipts, without keyboard fallthrough or Helper/schema redesign.

## Verification discipline

Keep source fixtures, real-provider/model-selected paths, installed managed bytes,
fresh browser rendering and human microphone proof separate. The previous
selected-flow PASS did not establish universal natural follow-up handling; the
new physical failure supersedes that readiness claim. No new candidate is ready
until the new boundary fixes pass causal and full-workflow checks. Preserve
ordinary-command zero connector work and the permanent payload hashes.

## Current correction and evidence — 17 October, in progress

The comparison above records the state **before this pass's edits**. The new
correction has not yet been packaged or installed and does not supersede the
user's failed manual acceptance. Installed application remains2f5ea15 until a
separate delivery receipt proves replacement.

### Retained results and execution are separate

The new controller-only `RetainedReadRecovery` boundary handles a current
contextual read after the model proposes an obsolete execution task. Retained
results now carry local originating request/generation/logical-session pins and
the normalized non-mutating capability IDs. These pins survive pruning the old
execution owner; they do not revive that owner's authority or grant writes.

- Available retained facts can be recalled without network work. An additional
  native read starts fresh execution on the verified same account, using a
  validated retained native reference rather than replaying the old operation.
- A data-only recall and later native read are stages of the same current-human
  recovery. One receipt cannot permanently prevent the later needed read.
- Gmail/Docs/Forms/Drive/repository references use their existing typed object
  validation. Search/list/no-argument capabilities instead receive fresh
  resolution only, and must build arguments from the current user request;
  old query strings, cursors and arbitrary object arguments are not dispatched.
- A pending read can be recollected through its original source-result
  reference. If its verified operation already ended execution, collection
  returns that operation's evidence rather than another inactive-task error.
- Supplied stale task IDs contribute no authority. Multiple matching retained
  accounts/results require clarification; session/account/generation/expiry,
  cancellation, body permission and normalized read-only capability checks
  remain in force. Clarification context is projected through the same
  headers-only disclosure boundary as recall.
- Cheap contextual-reference checks precede account-owner validation. Normal
  navigation, unrelated desktop requests and writes do not acquire new
  connector authority through this boundary.

This is a generic recovery boundary, not a new universal Jarvis router. Actual
execution of write/open/cancel commands remains with their existing owners.
The normalized registry has not been expanded. In particular, Discord's
configured user OAuth reads account/server information, not message bodies.

### Narrow protected-behavior corrections

The ending change adds only recognition of standalone explicit polite/"now"
farewell wording at the existing parser, with negative, hypothetical, translation
and compound-request guards. It does not change Live transport, audio handling,
model setup, output publication or session ownership.

The newly authorized first-N tab correction is narrowly in CommandRegistry's
authoritative positional route and JarvisAutomationService's existing close
argument logic. It uses existing stable tab IDs and exact close receipts. No
Chrome Helper, Browser Automation engine or permanent tool/prompt changes were
made. This is distinct from the earlier7300 patch that was fully reverted.

Lea-style received-mail wording now prepares a generic participant-native query
only when the entire remaining clause requests same-object read-only details.
The original human goal and its date/account/content scope remain unchanged;
hard constraints, other objects, writes and combined opening keep the existing
scoped path. There is no hard-coded contact, inferred address or received date.

### Evidence tiers and remaining gates

**Causal fixtures:** focused correction v2 passed399 cases and v3 passed400;
v4 subsequently passed779/779. The latest targeted v5 passed102/102 on assembly
`D862D3C8AD49CE8557CD92E814C2DCCAD355E904018CE406345666030264882C`.
The earlier counts remain tied to their own source; the later provider/cache
progression and clarification projection corrections are covered by the newer
gates. The full desktop gate `context-recovery-full-final-v1` is currently
running onD862 with no further application changes; its result is pending.

**Real native before/after:** the extracted installed2f5ea15 baseline reproduces
the natural retained follow-up's terminal rejection with zero incremental
backend calls. On compiled assembly
`AF4A55C1BA3515F358AF9E2D51373815128B7E418AFD0468A4E60B0CA5ED4201`,
`natural-followup-corrected-v2.json` succeeds for:

> When did Daisy reply, and what is the latest status of that email?

The real Gmail path retained the same account and thread, created fresh read
authority, returned seven message-body excerpts through one thread read and
seven body reads, made zero follow-up searches, exercised one injected503,
and cleared activity. The18.1-second case duration includes the initial search,
native reads and local completion; host duration was20.1seconds. This proves
native content recovery and bounded retry behavior, not actual Live answer
quality, microphone input or universal latency.

The first corrected native attempt hit the existing50ms DateScope regex timeout
under concurrent test load. The controlled run succeeded without changing that
regex or its timeout. Preserve both receipts rather than treating the first run
as a Gmail failure or silently discarding the harness/load exception.

**Fresh browser rendering:** `daisy-browser-render-v1.json` records Chrome
opening the account-qualified native route ending `#all/1a09467590afcb15` and
resolving to canonical conversation
`https://mail.google.com/mail/u/0/#all/FMfcgzQhWTkHtjtXDRpRQCqPFNQNvWxH`.
The rendered participant was Daisy Edwards and the subject was
"DeveloperWeek Domain Roulette → request to acquire keyboard.wtf", with
multiple messages. It was neither blank, Inbox-only nor a search page. This
proves those observed native-ID URL semantics in the real browser; it is
source-neutral browser evidence, not proof of a newly installed candidate.

**Real Personal Live stages:** preserve the overall failed receipt
`natural-retained-live-v3.json`. Its first four stages succeeded: initial Daisy
search; the natural retained question with seven body excerpts and no new
search; generated spoken full-details request with ten excerpts and one useful
approximately950-character answer; and generated spoken exact opening with
zero backend requests and the retained thread URL. Captured completed answers
and tool receipts substantiate those individual stages, without inactive-task
or competing generic-denial speech in them. Generated speech used no physical
microphone or speaker. This does not convert the overall receipt into a PASS.

The final typed ending check in that combined harness timed out after30seconds.
It used low-level `SendTextTurn`, bypassing KeyboardWtfApp's existing
`TryEndFromAuthoritativeUserText` and the current spoken-transcript ownership
used by the end tool. The failed receipt and its final captured answer remain
evidence of that harness path, not proof that all ending paths passed.

The separate actual spoken test `spoken-end-live-v1.json` passed onD862:
"End the conversation now." reached actual `ACTIVITY_END`, the log observed
the end tool, the end observer ran exactly once, the socket closed and the
session cancelled in3.04seconds, with zero native connector requests. Its
correct runtime log is `end-live-v1-runtime/keyboard.wtf.log`; obsolete harness
log filename assumptions must not be used to conclude the end tool was absent.

**Real Lea native read:** `lea-native-corrected-v2.json` passed in5.62seconds
on assembly
`E8FC676BE36B03AF7EA2CBE91217AAB8A12D5B01C8C3F8EDF994D8758BB7C7D4`.
The exact natural received-mail request produced `from:"Lea"`, one native
reference with matching sender header, two body excerpts and clean activity.
It used one search and one thread read on the same account, no actual
structured-AI calls, and two injected503s under the unchanged new-search retry
policy. This is successful native participant/content retrieval, distinct from
actual Live synthesis or human microphone acceptance.

### First broad gate and final source correction

The first broad desktop run passed5956/5971. It is preserved as a failed gate,
not certified by the earlier focused counts. Investigation identified two real
body-permission regrant failures and four real active-task collection/ownership
regressions in the newly added recovery boundary. Those were corrected in
`08d6674` (`fix(connectors): preserve active collection and explicit body regrant`).
The final focused correction passed112/112. No further application changes have
been made after that frozen correction.

The correction leaves active execution owners on their established collection
path before inspecting recovery context. Empty-retained-state and same-human
impossibility guards avoid unnecessary transcript/regex processing. A later
explicit body-access regrant can disclose previously verified same-scope native
content without reviving terminal execution or repeating the provider read;
headers-only disclosure continues to hide that evidence until such a regrant.

**Local idle-guard measurement:** `idle-guard-before-v2.json` usedD862 and
`idle-guard-after-v2.json` used final assembly
`08B1893A9CF36AB7EFDFF44A96EE2A6260339801BCA185B7304E86CD94193153`.
In the isolated empty-retained-result guard, ten repetitions of the initial
search phrase dropped from88.10ms median to0.0163ms; the contextual-question
guard measured0.0040ms afterward. The corrected empty-context path performed
zero snapshot/transcript/identity reads and both before/after probes performed
zero network, provider or model calls. This measures a local guard under the
observed test conditions; it is not a Live speech or end-to-end Gmail benchmark.

**Actual spoken status interruption on final assembly:**
`context-recovery-status-v2/context-recovery-status-v2.json` passed the generated
speech Personal Live/Gmail combined full-details plus exact-open flow with
"What's taking so long?" arriving during the first real thread request. The
status transcript and model proposal were observed even though no finalized
status callback occurred; the broker still joined the existing owner correctly.
It produced one truthful in-progress answer while activity remained owned,
followed by exactly one useful final details answer, zero contradictions and
clean activity completion. The same account/thread and one retained exact URL
dispatch were preserved, with zero repeated follow-up searches and one injected
retained-synthesis503. Native reads were one search, one thread and seven message
reads. Total80.17seconds includes initial search/answer, generated input and
final Live audio generation; it is not native retrieval latency. Actual Personal
Live selected the tools. No Managed usage, physical microphone, room audio,
speaker output, actual browser dispatch or mail writes were used in this run.

The fresh Chrome render and earlier actual spoken ending receipts above remain
separate supporting evidence. Neither is relabeled as final installed-candidate
proof, and the overall failed combined Live-v3 ending-harness receipt remains
failed despite its individually successful connector stages.

**Final broad desktop v2:**5971passed/5974total,3failed,0skipped in21m48s.
The failures were a three-second lifecycle-notice wait, an existing50ms regex
timeout on "Find unread emails", and a queued retained read returning a generic
failure receipt after reaching mail_thread. The broad trace did not capture the
last receipt's underlying exception, so its exact cause is not established.
One unchanged-binary controlled gate passed13/13, including each exact failed
case and nearby status/ownership cases. All three names were checked against
passing controlled results. No source, timeout, regex, fixture or build change
was made for this rerun. Combined with final-binary actual Live status/read
acceptance, no reproducible product failure remains from these cases. The raw
broad gate remains failed evidence, not a claimed single green5974/5974 run.

**Packaging blocker:**Windows paging grew to15561MiB allocated and C: reached
zero free space during the broad run. With exact-path/hash/archive checks, only
the generated private publish EXE was removed (321252814bytes, SHA898D0677...),
which duplicated the protected2f5 archive. The archive, installed private/public
apps, profiles, settings, mappings, startup and Helper were not modified. A new
private build/install has not been attempted. More free disk space is required.
Do not change pagefile settings or delete protected evidence to conceal this.

**Pending:** final documentation/source checkpoint, ONE private package/install,
installed-byte native follow-up/combined acceptance and protected normal-command,
payload/profile/runtime readbacks. Backend code is unchanged. No packaged
readiness or final manual-test claim is made by this audit update.
