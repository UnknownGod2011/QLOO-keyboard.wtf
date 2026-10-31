# Verified private candidate d245ac4 — 18 October 2026

This supersedes the earlier **not ready** checkpoint for this closure pass.
The private candidate is ready for a final human connector test. It is not a
public release or a claim that every provider response is universally correct.

## Source, installation and rollback

- Pre-fix clean HEAD: `109ae3f88f7bfce98dad1903449bf0c708f2a761`, protected by
  `checkpoint/connectors-before-reported-regressions-20261018`.
- Installed source: `d245ac42d0d7981aa33b1e3d795332fd0469d137`.
- Application checkpoint: `be4646b5b72313abbe83194af3c42a31b81a40fc`.
  d245ac4 adds checkpoint documentation; application trees match.
- Application tree: `b71eee36df502d12997347494f6cb2f4e9956970`.
- Rollback: `4c647c250660921263887670928608d600710a8a`, with preserved
  `96eff5033ff93152d8169e8e45d5f8b50bcb9fa2` and older archives.
- Public baseline: 1.4.21 / `07a57667055a1c47c7a1990ec0d45d7f20a0cd48`, verified.
- Exactly one new private build/install; no deployment or backend changes.
- Installer SHA-256:
  `EEBE4F288A0B39D23AB03C39FCB6B2FA2B4D544BF74C5B86CD4DDC8832C7C6A6`.
- Installed EXE SHA-256:
  `11A5203C959DACA988A3EC6B34B4F0B43331D1C86BAD78B96EC4529A7156E66C`.
- Exact managed assembly extracted from installed EXE matches publish:
  `D0BFFAB0FC30D1D57B3F276566DBBF64D86FF7A6E6BD0AED3E445EA3739B1D40`.
- Running private GUI PID23700, private Settings8090, **PersonalOnly**;
  all five saved hotkeys registered. No mode, mapping or hotkey reset performed.

Package manifest is under `dist/connector-candidate-archives/d245ac4-reviewed/`.
Operator/extraction/runtime receipts are under
`.artifacts/connectors/recorded-regression-20261017/package-accepted-read-final/`.
The evidence commit containing this report may follow the installed source;
that does not represent a second package or another application change.

## Verified causes and narrow corrections

1. The exact combined request, “Could please provide more details about the
   entire conversation and open the conversation thread in a new tab,” missed
   the existing anchored read/open grammar. Its compound object noun and omitted
   polite pronoun bypassed recovery and allowed a terminal task proposal.
   a1dc698 normalizes that wording before the existing parsers, without supplying
   any identifier, account, permission or action authority.
2. A separately reproduced cache-boundary bug accepted a fresh read at19:59,
   then rejected its result at20:01 when the older cache entry expired. be4646b
   binds accepted delivery and its pinned verified opening to the actual fresh
   task's existing lease. Cancellation, account/session changes, the new task's
   own expiry and unbound expired-reference admission still fail closed. This
   does not extend the cache indefinitely or revive a completed write authority.
3. The positional selector accepted the short first-two phrase but rejected
   bounded courtesy/current-browser suffixes. A model's conflicting rightmost
   proposal could then reach the unchanged executor. 61379c3 extends only that
   anchored human-intent grammar. Stable-ID resolution, execution, extension,
   foreground checks and normal last-tab closing remain unchanged.
   The exact51-character transcript from the earlier human failure was not
   retained; do not claim its complete wording was recovered.
4. Browser failure was a competing GUI owner. The unchanged public native host
   automatically launches the public GUI when the shared pipe is unavailable
   during a private-app exit. The public GUI can then own the Helper and hotkeys.
   Identity-checked public GUI exits were normal authenticated exits, never force
   termination. The private candidate now owns the connected Helper. The public
   binary, startup and native-host registration were preserved. Registration
   still points to the public host and the private installation warning remains;
   this is an operational handoff, not new isolation architecture.

Production changes from installed4c647c2 are confined to four files:
`ConnectorFollowupIntent.cs`, connector controller/admission, and the positional
intent regex in `JarvisAutomationService.cs`. No new framework or permanent tool.

## Acceptance evidence

| Check | Actual result and proof boundary |
|---|---|
| Full desktop | 6,029/6,031 passed,2 explicit regex timeouts in fixture setup,0 skips. ONE unchanged-binary NoBuild control passed10/10 and covered both failed methods. Original failures retained. |
| Focused new boundaries | 654/655 with1 setup regex timeout; one unchanged-binary45/45 control. Causal red cases retained for grammar/cache failures. |
| Installed native connector | Initial real Gmail search, fresh combined read/open,7 body excerpts, shared receipt for3 proposals, no repeated search/calls, clean UI. One injected503, no actual AI or email writes. |
| Installed-assembly Personal Live | Real saved Daisy/open audio and exact combined human text;4 turns passed, one useful answer each, no inactive error/denial, same account/thread, valid response-ID/ack tap, UI clear. No room microphone. |
| Details | Useful10-message-excerpt chronology, participants, asks, commitments, fees and latest transfer state. Answer explicitly disclosed clipped/possibly missing content; not certified as an exhaustive mailbox history. |
| Retained factual question | “What did she ask me to provide?” answered correctly about account code/contact verification;0 new native/backend/reasoning calls. |
| Exact-open follow-up | Same verified thread dispatched once;0 new native/backend/reasoning calls. Browser launch was inert in this diagnostic. |
| Real Chrome destination | Cua visibly rendered the matching Daisy domain-transfer conversation from the current receipt URL, neither Inbox/search nor blank. Gmail normalized native `#all/1a09467590afcb15` to its own UI URL. |
| First/last tabs | User explicitly reported the requested new-candidate first-two/last-two tests worked. Current real GUI logs also show successful first-three/last-two positional closes. Human outcome proof; exact remaining IDs were not captured by the agent. |
| Ending | Current real GUI end tool accepted and socket cancelled normally; shutdown11ms. Entire Live file still matches96eff50; no ending/audio change in this pass. |
| Translation | Real installed backend translated an owned synthetic10,077-character document to10,562 French characters in5.97s, complete, all60 section markers plus ending marker,0 Jarvis credits. No clipboard/UI mutation in this diagnostic. |
| Ordinary Jarvis | Gmail/Instagram/saved Discord/multisite/arithmetic:0 connector backend/account/provider/reasoning work. Installed Gmail median local dispatch12.42ms; no claim of a paired full spoken-speed comparison. |
| Prompt/tools | Unchanged19509-character base prompt,62 permanent tools,30887-character JSON. Hashes D094925C…3D5E95 / F1D89E81…1F35FF. |
| GitHub/other connectors | Existing shared retained recovery/opening, release/assets/insights and cross-provider tests remain green in the full gate. No fresh live-provider acceptance is claimed for every provider. Backend tree unchanged; earlier452/452 held-revision gate retained. |

Actual installed-assembly Live case timings include ASR/model generation and
answer completion: search35.36s, details/open16.38s, factual9.70s, later open6.86s.
These are one observed run, not promised latency. Factual/open turns made zero
connector/reasoning calls. The separate native packaged read/open check took17.55s.

For initial activity publication, final transcript→model proposal300.61ms;
proposal→published connector ownership57.12ms. Source and trace show publication
before awaiting account resolution, provider search or reasoning. WPF paint was
not measured. Existing immediate resolve UI and continuous transfer remain.

## Protected boundaries and honest limits

Public EXE remains
`2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`.
Stopped install/post-sanity comparisons checked29 profile files. Settings,
mappings, Personal configuration, public startup, executables and Helper
registration/manifests were preserved. One public authentication-token refresh
caused the strict comparator to fail; it is preserved and explicitly reviewed,
correlated to the unchanged public GUI's successful refresh within2.45ms.
No token bytes were printed, copied back or restored. Normal private launch later
refreshed its authentication token too. Do not call token bytes unchanged.

Translation source resolver,50,000-character cap,65,536 output-token limit,
complete-output gate, Translating/Copying UI, selection history, transport and
backend ownership match the working references. The earlier Devpost capture was
10,183 characters, below the cap; its log did not show the owned translation
starting. The exact human request passes the full-source dispatch tests. That
original admission failure lacks a recorded full request, so its precise cause
is unproven. The new real large-document test is backend completion proof, not
an automated repeat of that selected page and clipboard UI interaction. Its first
test-host attempt was rejected before dispatch for an unbound owner; corrected
setup used a real Personal Live binding. No production safeguard was weakened.

Current structured Personal reasoning still returned actual429/cooldown; native
retrieval/results survived and Live synthesized useful answers. Text translation
using Personal's separate text model succeeded. No automatic Managed fallback,
extra retained-content reasoning retry, quota loop or saved-mode change was added.
An earlier source run's generic model denial remains recorded; later source and
installed runs passed without it. A successful run is not a universal guarantee.

Computer Use temporarily worked for the rendered Gmail check, then again reported
local browser use disabled even after Chrome restart. User authorized the manual
tab verdict. A real Helper recording probe was prepared/compiled but never run;
do not count it as execution proof. The installed recording-audit endpoint was
404, so no GUI audio replay through that endpoint occurred. No unattended mic.

Audio/VAD/STT/playback, Live transport/model, Smart Writing, Browser Automation
executor, memory, selected text/screen, popup dimensions, billing and public
release code were not changed in this closure pass. The latest user-confirmed
tab success closes that reported failure; do not redesign its executor now.

**READY FOR FINAL MANUAL CONNECTOR TEST.** This is a private review candidate.
