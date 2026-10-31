# Status follow-up ownership and latency — private delivery

Application source `2f5ea15b3b2279738538d9b289759f41fc17dd4f` is installed as
Connectors Staging Candidate. Private GUI17416, localhost8090, PersonalOnly /
Gemini, all five shortcuts; Jarvis Ctrl+Alt+J. No microphone was started.
The older `7300c9b` private package is preserved as rollback. Pre-fix source
`6d74027489726964dfb9051f4716318679755229` is retained at
`checkpoint/connectors-before-latency-20261016`; corrected source is tagged
`checkpoint/connectors-status-owned-source-20261016`.

## Cause and narrow correction

The physical user run did not show premature original-turn speech. Eight Gmail
retrieval operations took about12seconds, followed by two Personal structured503
failures consuming about30seconds. A subsequent status question received mixed
connector/browser receipts, then the original result arrived separately.

The connector controller now binds a pure status question to its uniquely owned
undelivered retained read/open operation. This works at the broker boundary even
when the model proposes tools before ACTIVITY_END. The real test also reproduced
ASR appending the status question to the original request. Only the exact
already-admitted request plus a bounded status suffix is accepted; appended new
targets, writes, navigation and permission changes remain excluded. Original
execution/account/thread/scope/cancellation authority is preserved. Same-turn
model proposals share the status or final operation receipt.

One Personal503 is sufficient when a direct retained-thread operation already
has fresh, verified body evidence for the same native thread, guidance revision
and retrieval scope. The extra synthesis retry is skipped in that case. Initial
successful synthesis, new searches, missing-body cases and other callers keep
their existing behavior. The existing truthful native partial-result recovery
returns evidence without claiming Gmail disconnected. No general timeout,
budget, audio, transport, model, prompt or permanent-tool changes were made.

The entire first-N tab-closing patch was reverted, including tests, exactly to
`4be3d21` / `157142d`. No further tab-control repair is included.

## Verification

- Focused450/450 before cumulative-ASR correction; final33/33 causal checks.
- ONE final desktop gate: **5857/5857**, zero failures/skips; no controlled
  exceptions or repeat full run needed.
- Backend unchanged, held revision00033-tip/tree8663bfda; reuse selected452/452.
- Actual Personal Live, generated speech and native Gmail: Daisy search,
  combined exact opening/details, status interruption, one progress answer and
  ONE substantive final details answer. All completed answers reviewed: zero
  inactive-task/LLM/browser inability contradictions or tool-argument leakage.
  Same account/thread, zero follow-up searches, one injected structured503,
  one exact URL dispatch, activity clear. Whole run78.6seconds includes initial
  speech, generated input/status and final speech generation; native thread and
  seven message reads were about10.7seconds. This is not a universal latency SLA.
- Exact managed bytes extracted from installed EXE: real Daisy search plus
  fresh retained-thread read, seven message bodies, one exact dispatch, one
  injected503, three repeat proposals with zero additional calls/tabs, clean UI.
  Native whole flow18.4seconds. Browser dispatch was inert, not fresh rendering.
- Installed isolated ordinary-command measurements: zero connector backend,
  account, provider or model calls. Prompt19509chars, tools62 / JSON30887chars,
  unchanged hashes. Live/audio/VoiceCapture/automation/app implementation equals
  previous4be; application delta vs4be is four connector files only.

## Package and preservation

Archive: `dist/connector-candidate-archives/2f5ea15-reviewed/package-proof.json`.

- Installer SHA256:
  `DDD3D246C07532FD6A956E10E612F89E13FB7B43F3B1B8E3FDB2E9FFF8E189EB`
- Installed EXE SHA256:
  `898D0677ED16438C853C7F48BFE6503F628CA4B33B58D299AF844EDD61A19C4D`
- Extracted embedded/published DLL SHA256:
  `ABB05E3251F38F3E65E0A65378896B3D9C655861B6E9458F61AD074965B886E0`
- Manifest SHA256:
  `EFB84ED535C06BFB19B9641845533859F8C18B52315D175A30507F36A2AB36C5`

All24 payload files verified. Stable stopped and final physical-running
snapshots compare29 profile files. Settings, mappings, Personal configuration,
public EXE, startup, Helper registrations/bytes and rollback are preserved.
Only encrypted public/private session tokens renewed; their write times match
successful refresh logs within1.15ms/2.35ms. Original non-green token comparison
receipts remain, with explicit separate reviews. No token content read/restored.

The public native host spawned a duplicate old GUI during private replacement.
That idle GUI was closed gracefully and the private app restarted normally to
reclaim8090 and its five hotkeys. Native-host process lifetimes changed naturally;
no forced termination, public binary/startup change or Helper mutation occurred.
Final physical lock inspection proves GUI17416 uses the intended private profile.

## Evidence limits and manual handoff

Fresh Chrome navigation still returns `Local browser use is disabled in Work
settings`; inventory availability does not establish navigation access. Current
exact dispatch matches the same-day previously rendered Daisy URL. Fresh visual
rendering and physical microphone behavior remain user checks. This pass proves
the reported ownership/retry correction, not every future provider/model answer.
The outage summary truthfully marks incomplete/clipped coverage; no complete
conversation claim is made.

All current receipts are in ignored
`.artifacts/connectors/followup-latency-20261016/`, including `final-delivery.json`,
`final-gates.json`, `status-interruption-real-v3.json`, installed-native sanity,
`final-preservation-reviewed.json` and `final-runtime-ready.json`.

**READY FOR FINAL MANUAL CONNECTOR TEST. Stop implementation.** Find Daisy,
request opening in a new tab plus details, ask "What's taking so long?" during
retrieval if needed, and check one useful final answer plus the matching tab.
Public release remains unchanged; nothing was published.
