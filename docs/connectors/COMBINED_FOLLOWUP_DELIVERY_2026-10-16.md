# Private combined-follow-up delivery — 16 October 2026

**Installed and running for final manual connector testing. Stop implementation
here unless a new human test reproduces a defect.** Application/package source:
`7300c9b2560a5be9f56c53f6cc2e366adf1400ac`. The later documentation commit does not
change the packaged application. Pre-fix working HEAD `157142d` is preserved at
`checkpoint/connectors-before-after-open-followup-20261016`; installed application
rollback `4be3d21` and all earlier archives remain intact. Final source tag:
`checkpoint/connectors-combined-source-20261016`.

## Cause and narrow correction

The human confirmed that opening the retained Daisy email and requesting its
details were one sentence. Previously that combined request could miss the
anchored retained-read grammar, reach the terminal search task after a reasoning
503, and then fall through to a generic browser new-tab proposal. The correction
recognizes two independently authorized same-mail read/open clauses and gives
them one owned operation. It pins the verified account/thread, starts fresh read
authority, preserves actual body evidence, opens only that retained object, and
reports the read and browser outcomes separately. Repeated model proposals share
the receipt rather than repeat reads or tabs. Ambiguous matches require a choice;
no guessed destination or body is used. A new details offer after opening also
binds to that opening turn instead of reviving an old execution task.

The secondary first-two-tabs issue was reproduced in source: only a last-N
positional pre-route existed. The narrow fix resolves the requested first/last
set to stable Helper IDs before closing any tab, and preserves that explicit
intent over conflicting model targets. It changes no Helper, browser engine,
extension, or permanent schema. Tests use inert tab fixtures, not user tab closes.

## Proof and limits

- Focused final source gate: **553/553**.
- One broad final desktop run: **5,845/5,846**, zero skips. The sole failure was an
  existing count-evidence receipt regex timeout. The unchanged-binary method
  control passed **2/2** in 79 ms. The scheduling cause is not proven; this is a
  recorded timing exception, not a claim that the first broad run was fully green.
  No runner tuning or repeated full gate occurred.
- Backend application tree is unchanged: selected **452/452** gate reused;
  Firestore-emulator cases were outside that selected gate and remain separately
  accounted for. Held revision `keyboard-wtf-connectors-staging-00033-tip`,
  source `00901f5`, tree `8663bfda66ed96d9eded3540dd475bd41cf61aff`. No deployment
  or public/default traffic change.
- Actual Personal Gemini Live, real Gmail and generated speech: Daisy search,
  combined exact-open/full-details request, next spoken details request and final
  spoken exact-open request all returned **one completed answer each**, no generic
  denial/inactive-task error, and cleared activity. Structured reasoning failures
  were injected 503s; Gmail and Live were real. No microphone or room playback.
  The detailed answer truthfully disclosed missing/clipped coverage. Repeated
  summary wording occurred inside one provider answer; it was not a competing
  generation or second publication.
- Installed EXE's exact managed assembly was extracted offline and matched to
  publish. Its native combined check performed one initial person search, one
  fresh thread read plus bounded missing-message reads, returned seven actual
  body excerpts, dispatched one exact account-qualified URL, and retained one
  receipt across three conflicting follow-up proposals with **zero** additional
  backend work/tabs. Simulated reasoning 503, zero actual structured AI calls.
- Installed native Gao/Winter regression returned six/five candidates, recovered
  a fresh same-account Winter task without replaying the old Gao query, clarified
  an ambiguous details request without an inactive-task error or guessed body,
  and dispatched all five verified Winter references with **zero** follow-up
  backend calls. This is native/component proof, not fresh spoken Gao/Winter or
  rendered-tab proof. Previous spoken/provider evidence remains at its own tier.
- Fresh Chrome verification was attempted but the tool still reports local
  browser use disabled in Work settings. The current two spoken Daisy opening
  dispatches exactly match the URL visibly verified in Chrome earlier today:
  `manual-issues-20261016/final-browser-exact-proof.json`. That check rendered the
  matching Daisy subject/participant/Reply controls, not Inbox, search or blank.
  It is explicitly reused identical-URL proof, not a new installed-browser claim.

Private receipts, actual answers and failed harness attempts are preserved under
`.artifacts/connectors/after-open-followup-20261016/`, with `final-delivery.json`
as the exact readback. The native harness corrections matched production's
turn-snapshot callback and distinguished raw provider data from final answer
receipts. Historical manual native reads against terminal/unobserved authority
were not promoted to product acceptance. Application source/package never changed
after the final source gate.

## Protected boundaries and runtime

Normal Gmail/Instagram/Discord/multi-site/math probes each ran 12 repetitions:
**zero** connector backend calls, account lookups, provider calls or extra model
calls. Current source median local dispatch totals: Gmail 5.82 ms, Instagram
5.10 ms, Discord 189.62 ms, multi-site 189.95 ms, math observation 0.033 ms.
These are isolated local measurements, not complete spoken/network latency.
The established normal-routing performance conclusion remains frozen.

Source and extracted-installed payload hashes match the pre-fix prompt/tools:
19,509 base-prompt characters; 62 permanent tools; 30,887 serialized tool JSON
characters. No permanent payload growth. No Live/audio/STT/VAD/playback,
Smart Writing, BA engine, memory, selected-text/screen, billing, popup-dimension,
transport, model or Personal configuration change. Application changes in this
pass are connector grammar/controller composition plus the explicit positional
tab correction in the two existing routing/executor files.

All 24 installed package files matched. Stable stopped snapshots compared 29
profile files and preserved settings, mappings, Personal configuration, public
EXE, startup and Helper registration/manifests. Session-token ciphertext did
renew: public startup/background refreshes correlate with public logs and write
times; the private token write correlates within 0.22 seconds of native-test Auth
initialization. Frozen-session/same-account checks passed. Those exceptions are
explicitly reviewed in separate receipts; original non-green comparisons survive.
No token was manually edited or restored. No mail writes or microphone starts.

The public native host automatically started its public GUI while the private
app was stopped, as in the prior delivery. Only that idle duplicate GUI was
gracefully closed after the new private GUI was alive. The private app used its
normal restart to reclaim port8090 and shortcuts. No force, public startup change
or Helper/native-host termination.

Final private GUI **PID6440**, PersonalOnly/Gemini, Settings **localhost8090**:
Smart Writing Ctrl+Alt+K; Dictation Ctrl+Alt+D; Jarvis Ctrl+Alt+J;
Cancel Ctrl+Alt+Escape; Settings Ctrl+Alt+comma. Only the private GUI plus normal
public native host remain. No Jarvis session/microphone was started by the agent.

## Identity and final manual check

- Installer SHA256: `6C09E0E6465E4E33BDF399629095397DDC600DA3C22079A2C2E81A4798494A5B`
- Installed EXE SHA256: `F973DE2ACDCF1B7324E3D52931B4190AAB3BB4F3DC65D0CD2F4541FBA8737BE1`
- Embedded assembly SHA256: `3F60D4DACA1E4F7C860CE6E71A8AD2244AC3A9D9715048765447232FD5F3EF16`
- Public EXE remains: `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5`
- Private archive: `dist/connector-candidate-archives/7300c9b-reviewed/`
- Public accepted baseline remains 1.4.21 / `07a5766`; no publication.

Remaining human proof: actual microphone wording and visible browser result.
Find Daisy, then say **“Open it in a new tab and give me the full details about
that email.”**, then request more details. With multiple materially different
conversations, choosing a conversation is appropriate. Optionally use disposable
tabs to verify “close the first two tabs.” Do not add theoretical work after this
delivery. **READY FOR FINAL MANUAL CONNECTOR TEST.**
