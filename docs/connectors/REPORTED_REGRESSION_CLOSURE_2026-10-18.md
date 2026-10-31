# Reported regression continuation — 18 October 2026

This report supersedes earlier universal readiness claims. It separates current
source tests, actual Live/provider results, installed state and rendered-browser
proof. Overall readiness remains **NO**.

## Preserved baseline and current correction

Pre-fix HEAD `109ae3f88f7bfce98dad1903449bf0c708f2a761`, branch
`codex/connectors-foundation`, was clean. It is protected by
`checkpoint/connectors-before-reported-regressions-20261018`. Its application
code matches installed private source `4c647c250660921263887670928608d600710a8a`.
Rollback `96eff5033ff93152d8169e8e45d5f8b50bcb9fa2` and the existing package
archives remain available. Public source is `07a5766` / version 1.4.21.

The only new production change is eleven lines in `ConnectorFollowupIntent.cs`.
The user's exact combined request was:

> Could please provide more details about the entire conversation and open the conversation thread in a new tab.

The existing bounded action grammar recognized `conversation` or `thread`, but
not the compound noun `conversation thread`. It also missed the polite prefix
without `you`. Because combined admission requires both clauses to match, the
whole request bypassed retained recovery; the generic read recovery correctly
refused to absorb an unrecognized opening action. The remaining proposal could
therefore reach a terminal execution ID even though retained content existed.

The correction normalizes these human reference nouns and the omitted polite
pronoun before the existing anchored parsers. It supplies no native identifier,
URL, account, permission or execution authority. The existing fresh-read,
same-turn deduplication, cancellation, privacy, expiry and write fences remain.
Tests cover the exact wording with/without `you`, alternate model proposals,
an extra delete clause and an account-change rejection.

Causal new cases: 58 passed / 6 failed before the correction. An intermediate
large-regex attempt failed the .NET nonbacktracking automaton size limit and was
fully removed. The corrected small normalization passed **379/379**, zero skips.
The failed attempt and original red receipts are preserved; neither was installed.
Tested source DLL SHA-256:
`874D192025BEB27E339820AE915BFA164E7F2B0E5A0CB52479D894F2072EF545`.
Application correction checkpoint:
`a1dc6988efbfa9407d660fc423b7566331e8b503`.

## Browser failure: competing application owners

Physical capture found public GUI PID6116 and private GUI PID22564. The public
GUI held the connected Store Helper; the private GUI had no heartbeat. Both
use the existing `keyboard.wtf.browser.v2` pipe. The bridge, protocol and native
transport implementation match the older references; this is a side-by-side
runtime ownership failure, not evidence that the recent tab executor broke.

The verified duplicate public GUI was exited through its normal authenticated
Settings endpoint, after checking that no browser task was active. It was not
force-terminated. The Helper automatically reconnected to the still-running
private candidate. A later physical readback confirms both service worker and
content script 2.1.21, fresh active tab, current-tab automation available and no
last error. No browser action was used to obtain that result.

Public EXE, startup, Helper registration, profiles and settings were not changed.
Registration still points to the public native-host executable and reports that
installation mismatch. Its transport currently connects to the private GUI over
the shared pipe. Relaunching the public GUI may reintroduce competing ownership;
do not represent this operational handoff as new isolation architecture.

No tab-closing source was changed in this pass. Current selection still resolves
distinct stable tab IDs before closing, validates window/session and execution
receipts, and preserves the existing close mechanism. A fresh Computer Use retry
still returns `Local browser use is disabled in Work settings`; rendered Gmail
and disposable first/last-tab outcomes remain **unverified**, not failed or passed
by the local readiness readback.

## Actual Personal Live / native Gmail results

Two bounded normal-Personal runs used the same source DLL and Daisy recording;
no microphone, Managed reasoning, permanent setting change or injected failure.
Combined follow-up uses the exact user-supplied text through the normal Live text
input path. Browser launches are inert callbacks, not rendered acceptance.

- **v1 FAILED:** native Gmail reads and actual Personal HTTP429 returned verified
  message bodies, but Live answered: "I found a threadI'm just a language model
  and can't help with that." The refusal was actual provider output in the same
  generation, after the successful receipt. No inactive receipt or interruption
  occurred. This remains a real unresolved natural-response failure.
- **v2 PASSED this run:** useful Daisy search, exact combined details/open and
  subsequent recorded opening. Combined follow-up used five native reads, zero
  additional searches and one retained exact dispatch. The later open used zero
  backend/reasoning requests. Actual429 and local typed cooldown retained native
  content. Activity cleared after every turn, no denial/inactive answer, zero
  Managed charge. Whole turns were 47.25s / 47.78s / 7.64s; these include audio,
  model and output time and are not local dispatch latency or a speed acceptance.

Independent comparison found the first execute receipts identical after removing
only task/operation IDs (normalized JSON SHA-256
`731F7988CE08A17C1F5A7A4B1D100205B41173162B61E877E10454A110639FEC`).
Both contain the same six body excerpts, answer, successful state, partial
coverage and synthesis-only quota error. No deterministic receipt-schema cause
for v1's refusal is established. v2 does not erase v1; do not add speculative
prompt reinforcement, phrase suppression or repeat provider calls for a green
result. Raw complete websocket frames were not preserved, limiting causal proof.

Private receipts are under ignored
`.artifacts/connectors/recorded-regression-20261017/reported-compound-live-20261018-v1/`
and `...-v2/`. Do not commit private message bodies or credentials.

## Translation, dynamic UI, ending and normal Jarvis

The user's translation request was "translate the selection to French and copy
it to the clipboard." Public `07a5766`, rollback `2f5ea15`, pre-tab `4be3d21`,
`96eff50`, installed `4c647c2` and current source retain the same **50,000-character**
complete-selection limit. Translation executor, selection history, backend
gateway and Personal HTTP transport are byte-identical across those references.
The owned translation still requests 65,536 output tokens, rejects incomplete
output before copying, and retains the operation-specific Translating/Copying UI.
The popup/task owner implementation matches the private working references.

The incident log accepts 10,183 source characters. Its 6,232-character BA excerpt
is a separate bounded Unicode preview and does not truncate the original source.
The log shows ordinary Live selection/copy handling, with no owned translation
start. Thus the incident does not establish a new text cap; why the real spoken
request missed admission remains unproven because the GUI log omits its words.
The exact supplied wording was added to the existing dispatch test, which feeds
the complete large selection into the actual owned executor boundary. No
translation production code, cap or UI logic was modified.

Current Live source is byte-identical to `96eff50`. Compared with pre-tab/2f5,
only eight anchored explicit-ending recognition lines differ; the provider end
handler, scheduling, transcript-order wait and transport/audio parameters retain
the earlier behavior. Prior actual recorded greeting/discussion/explicit-ending
tests on installed4c passed. No new reproducible ending regression was found, so
no ending, listening, STT, VAD or playback change is made in this pass.

Current ordinary-command measurement: zero connector backend calls, account
lookups, provider requests and reasoning calls for Gmail, Instagram, saved
Discord, multi-site opening and arithmetic. Gmail median local dispatch8.16ms;
the run overlaps the broad suite and is not a paired public performance claim.
Permanent payload remains 19,509 prompt characters /62 tools /30,887 tool JSON
characters; prompt/tool hashes match the previous private receipt. No prompt,
tool, normal navigation, Smart Writing, memory, selected-text, screen or backend
ownership production change was made in this pass.

## Final gate and delivery status

The ONE current broad desktop run `reported-final-desktop-20261018` finished:
**6,003 passed /6 failed /0 skipped /6,009 total**, duration16m48s. Every failure
is a `RegexMatchTimeoutException`, at six different connector test families.
The original failure is preserved and is not relabeled a fully green broad run.

ONE unchanged-binary `--no-build` controlled run
`reported-final-controlled-20261018` passed **72/72**, zero failures/skips,
duration20s. It includes every failed family, the exact French selection/copy
request, complete-selection limits, translation UI ownership, explicit-ending
and false-positive safeguards, and stable-ID first/last-tab selection. These are
source/fixture gates, not rendered browser or physical microphone acceptance.
Both application copies used SHA874D above; test assembly SHA-256
`BD7B90E2FEFB79422B20EF9A0952383D94AA1DDABE14A61861FE594F31B451DA`.
An initial audit command used the nonexistent `net8.0` test-output directory;
its missing hash is preserved. The separate corrected metadata review records
the actual `net8.0-windows` paths, matching product hashes and unchanged timestamps.
No binary was rebuilt between the broad and controlled runs.

The new eleven-line correction is **not installed**. The running private candidate
remains4c647c2 /PID22564 /PersonalOnly. Private EXE SHA-256:
`D9BCBEA363F357D5FB285D3B1E811D56D4D97E39E555AAA01160240BC545398F`.
Public EXE SHA-256 remains
`2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.

Final physical readback at01:12local confirms private GUI22564, fresh Store
Helper/content script/current-tab readiness and no last error; public GUI remains
exited and public native-host21904 remains the expected transport process.
Read-only private Settings state independently confirms **PersonalOnly**, revision10.
No inference request or configuration mutation was used for that check. Public
native-host manifest SHA remains4C8B1FCA...3C5E691. The observed remaining native
registration warning is retained in the evidence rather than hidden.

Remaining blockers: v1's real generic denial, rendered browser/tab acceptance
behind the external tool permission, and a new private package/installed
acceptance only once delivery is justified. Do not
announce ready for manual connector testing from the successful v2 alone.
