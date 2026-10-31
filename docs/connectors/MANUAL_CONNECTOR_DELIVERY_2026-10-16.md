# Verified private connector delivery — 16 October 2026

Ready for the user's final manual connector test. Freeze implementation here.
The later documentation-only commit records this delivery; application source
and the installed package remain exactly the source below.

| Identity | Verified value |
| --- | --- |
| Application source | `9d86dc5eee5532dae67c8298280a351a452660f0` |
| Installed checkpoint | `checkpoint/connectors-manual-installed-20261016` |
| Pre-fix rollback | `187913b6883ad7e5428abb457beec88d82227075` / `checkpoint/connectors-before-manual-issues-20261016` |
| Accepted public | 1.4.21 / `07a57667055a1c47c7a1990ec0d45d7f20a0cd48` |
| Private installer SHA256 | `5DF9E5A176BACBCFBE0497A776D8506D5F2F9FCBD794B2E2B53970A55562E4BD` |
| Installed EXE SHA256 | `F120871C2959804D9E45D45D381345EFD7E17690F4E4E7C1E94B99181542AE06` |
| Embedded/publish DLL SHA256 | `7ED0CD4972720571C8E322EC7D14B6D77C230AABD4A28A440A509216094F5236` |
| Public EXE SHA256, unchanged | `2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5` |
| Held backend | `00033-tip`, source `00901f5`, tree `8663bfda66ed96d9eded3540dd475bd41cf61aff` |

Held private tag remains `gs-q261013`; default staging remains accepted00016-vic
at100percent. No public deployment or default traffic change. Archive manifest is
`dist/connector-candidate-archives/9d86dc5-reviewed/package-proof.json`.

## Changes and actual acceptance

- A model's Browser Automation proposal now passes through the existing retained
  exact-object opener when the user's current request explicitly opens that
  object. No Browser Automation implementation or ordinary navigation change.
- A missing/pruned task reference can recover a fresh, explicit, same-account
  read through the existing authority checks. The old task's arguments are never
  replayed. Write/cancellation/account and ambiguity fences remain protected.
- Generic person matching tolerates titles and explicit alternatives. A tentative
  subject hint does not become a mandatory literal phrase. No names are hard-coded.
- Native Spam/Trash searches set Gmail's separate inclusion flag. Partial native
  results retain page/sample/paging facts after reasoning failure; no page count
  is presented as a mailbox total. HTML-only MIME fallback removes hidden markup
  before its bounded content budget; plain-text preference is unchanged.
- Actual Personal Live source acceptance returned useful Daisy details and
  retained follow-ups, exact dispatch, and Gao candidates with the tentative
  hint, one answer per turn and clean activity. Detail coverage was explicitly
  clipped/partial. The actual spam scope retest stated that more pages remained.
- Fresh Chrome navigation of the exact retained Daisy URL loaded the actual
  matching conversation in a separate new tab. This is fresh rendered-browser
  proof in addition to product-path dispatch; no mailbox content is included here.
- Packaged sanity loaded the managed DLL extracted from the verified installed
  EXE: one unique Daisy thread, four Sophia messages, six Gao candidates, working
  pruned-task recovery, no stale-query replay, exact-open dispatch with zero new
  backend calls, verified Spam page, and clean terminal activity. Its structured
  reasoning503 was injected; it made four actual native read operations and no
  paid structured reasoning calls. Earlier actual Live/Managed proof is separate.
- The first packaged resolve encountered transient local DNS failure before any
  provider operation. A read-only DNS check then resolved all tested hosts and
  one bounded packaged retry passed. No network settings were changed.

## Gates and protected boundaries

Final frozen desktop DLL479D79A4:5733passed/1failed/0skipped of5734; the sole short
regex timeout passed1/1 unchanged-binary control. Focused511passed/1short regex
timeout of512; its unchanged-binary control passed1/1. No additional full rerun.
Backend452/452 selected connector cases passed; Firestore emulator cases were
outside this selected gate, not silently promoted to current emulator proof.

Source and installed-assembly ordinary-command checks made zero connector
backend/account/provider/reasoning calls. Installed local medians were about6ms
Gmail,5ms Instagram,190ms Discord/multi-open; inert launchers, no speech/model
latency in those measurements. The ordinary path is frozen. Prompt19509chars,
62tools and30887serialized tool chars retain their previous exact hashes.
No repair changes to audio/Live transport, Smart Writing, BA implementation,
memory, selection/screen, popup dimensions, or permanent prompt/tool construction.
GitHub capability work remains preserved; this pass did not redesign it.

All24 installed payload hashes match the package. Offline extraction proves the
installed managed assembly exactly matches the corresponding publish DLL.
Stopped physical before/after-install and after-sanity comparisons passed:
29 protected profile files, settings, mappings, Personal vault, other installs,
public startup, native-host registration, and Helper manifests unchanged.

## Running app and shortcuts

Private GUI6368, Settings localhost8090, PersonalOnly/Gemini. Public native-host
17996 is intentionally retained and is not another GUI. PIDs are observations
at delivery, not stable future identity. Reverify path/hash before acting.

An unchanged public native host auto-started its GUI during the installation gap,
temporarily owning the Settings port and hotkeys. That duplicate GUI was closed
gracefully while the private app remained up. Reapplying unchanged shortcut values
was a no-op because the existing API skips unchanged bindings; no preference or
settings byte changed. One normal private restart restored registrations:

- Smart Writing Ctrl+Alt+K; dictation Ctrl+Alt+D.
- Jarvis Ctrl+Alt+J; Cancel Ctrl+Alt+Escape; Settings Ctrl+Alt+,.

Q/X remain configured defaults with the previously accepted J/Escape fallback.
Do not claim Reset to Default was tested or change preferences to conceal conflict.
No agent microphone start, external send/write, public binary/startup modification,
Helper installation, or permanent Personal-mode change occurred.

## Evidence and limits

Ignored evidence is under `.artifacts/connectors/manual-issues-20261016/`:
`final-delivery.json`, `final-gates.json`, `final-browser-exact-proof.json`,
`packaged-final-native-sanity-v2.json`, `packaged-protected-boundary.json`,
`installation-boundaries-physical.json`, `sanity-boundaries-physical.json`,
`installed-final-9d86dc5/extraction-proof.json`, `final-runtime-ready.json`,
and the package/archive manifest. Raw private provider answers remain ignored.

Physical spoken/UI acceptance still belongs to the user. Long-thread summaries
state clipped or unavailable evidence; a bounded Spam sample is not a complete
folder review. Personal reasoning can still return external429/503; native
results and references survive. Do not promise universally uncut content,
instant provider/model latency, or immunity to external service outages.

Manual test: Daisy search → full details → factual follow-up → exact new tab;
Gao with a tentative subject hint; Spam query; explicit cancellation while a
search is active. Check one continuous activity lifecycle and one final answer.
Stop implementation and wait for feedback.
