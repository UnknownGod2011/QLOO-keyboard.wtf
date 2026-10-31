# Delayed-open correction delivered — 22 October 2026

Private **1.4.21 / `d0290bee60cdec902d04de3d3f6fd35613a3eca0`** is installed. Its production correction is `c0f3fda`; the remaining intervening commits are documentation. Immediate rollback is the previously working **`872cef7`**, with `1da3da5` and all older archives retained. The user confirmed **“Correct Austin thread and clear recap”** after the installed smoke.

## Change and root cause

An older opening's delayed browser receipt could overwrite the selected thread and delivery owner belonging to a newer human request. `JarvisConnectorController.TryOpenRetainedResourcesAsync` now rechecks the existing request snapshot before those two retained-state writes. Superseded operations still receive their original receipt; they cannot replace the newer selection. Existing admitted status continuations remain supported.

Only `src/Services/Connectors/JarvisConnectorController.cs` changed in production relative to `872cef7` (**8 additions, 2 removals**). There is no new state store, provider call, retry, query rule or audio/browser implementation. The original synthetic race failed before the correction; both completion orders now pass against the exact packaged DLL. This proves the race correction, not that this timing caused every historical wrong-thread report.

## Verification and original failures

- Previous focused source result: **26/26**. Previous broader result: **271/272**, preserved with its `TryLiteralMailQuery` timeout and separate isolated pass.
- This delivery's first focused run: **25/26**, preserved. A different unchanged `MailReadMode` regex timed out during the initial fixture resolve, before the new opening code. The failed theory passed alone.
- Final identical focused gate: **26/26**, without changing production code, test assertions or timeout limits.
- Resource observations showed approximately **403 MB free of 7,979 MB** and substantial paging before the isolated repeat. Timing sensitivity is plausible, not proven; no pathological input or causal dependency on this correction was found.
- Corrected earlier characterization: the first regex exception originated in worker classification before its `try` boundary and was later surfaced by fixture cleanup. It was not merely a cleanup operation. No speculative regex or exception-handling change was bundled here.
- Offline package contracts passed retained references, sender recovery, local-day/yesterday scope and the protected translation route. **Both completion-order tests passed against the exact DLL extracted from the archived single-file EXE.**

## Installed eight-turn session

The real installed application processed original natural-language text through its existing private diagnostic endpoint in one logical session, `a914853c9f934fcfb639acaed724b335`. Gemini generated its own connector proposals. This was not a new live-microphone test; earlier split-speech evidence is reused because that code is unchanged.

| Request | Full observed seconds | Evidence |
|---|---:|---|
| Find recent Imran email | 22.30 | Native search, speech/device complete, UI clear |
| Open that in a new tab | 8.32 | Canonical URL and matching Helper conversation title/URL |
| Find Shipaton 2026 | 21.85 | Native topic search, speech/device complete, UI clear |
| It was sent by Austin | 24.07 | Fresh authorized clarification, no inactive-task error |
| More details on that conversation | 39.60 | Full thread read; all 121 provider audio chunks reached playback; user confirmed clear recap |
| Open that in a new tab | 28.01 | Correct canonical dispatch; empty active-tab Helper metadata; user confirmed visible Austin thread |
| What did he say most recently | 16.40 | Fresh native read chosen in this run, completed answer/device/UI |
| Open it again | 26.72 | Same verified canonical identity dispatched; empty active-tab Helper metadata |

All eight speech/device/UI stages completed, with zero buffered audio and no active connector indicator afterward. The saved trace contains no warnings, errors, inactive-task messages, provider interruption, regex timeout or overflow. The owned diagnostic session stopped cleanly.

**Do not mark the raw automated navigation gate passed.** Its aggregate `ok` remains false because the last two openings had no active-tab metadata. The Helper stayed connected and reported `Chrome has no active tab`; the cause of that observation loss is unproven. The user independently confirmed the correct Austin conversation and clear recap. This is separate human evidence, not a rewrite of the failed machine observation. It does not separately certify the rendered destination of each repeated opening. Browser/desktop observation tools failed to initialize (`failed to write kernel assets`), so there was no independent DOM inspection.

## Performance

Five native executions took **2.435, 2.054, 2.484, 1.619 and 1.766 seconds**. The earlier baseline session used four; Gemini requested a fresh read for the latest-state question this time. No opening performed a native read, and there were **zero secondary reasoning or Managed AI dispatches**. One Personal Live session handled all turns.

For the three openings, first provider audio arrived **1.77, 2.87 and 1.61 seconds** after direct URL resolution. Their response-complete events arrived approximately **4.02, 5.54 and 3.89 seconds** after resolution. The final two full-observation totals include the harness's approximately 20-second destination-observation wait; that wait is not a product speech delay. The long recap began audio about 1.32 seconds after its native read completed, then continued through the complete response. No fresh ordinary-command benchmark is claimed; the normal dispatch path and prior approximately 9.9 ms evidence are unchanged.

## Installation protection

| Item | Identity |
|---|---|
| Installed source | `d0290bee60cdec902d04de3d3f6fd35613a3eca0` |
| Production correction | `c0f3fda79ee4cb8cb69982e6df3dca4eb360a09f` |
| Installed EXE SHA256 | `ACDE75E2A8987D41DF1BAA076D95A319B37087CDB0246A04A9A44901CF92CF3A` |
| Extracted DLL SHA256 | `03914F92BF9438EAA0AA4C02A7C14E3F1F4E97AC486BCE62CDC73E63CDA8DBB6` |
| Package manifest SHA256 | `160F5FC764F4EA34B0F472A8E087AD50EAEB420E21CD12FC57625D0F853E8A8E` |
| Immediate rollback | `872cef73b1cbb0a28896b9f0f7d4ff2d1fbef3a6` |

One installer executed. All **24 payloads** matched, all **29 protected profile files** were unchanged, and public executable/profile/startup/Helper registration comparisons passed. Final runtime readback confirmed private PID 11440, PersonalOnly and all five saved hotkeys. Helper 2.1.21 was connected; its active-tab availability was false at the final readback, as noted above. Public native-host registration remains intentionally unchanged.

The first handoff stopped before exiting the app or invoking the installer because its operator compared a JSON-parsed timestamp against a formatted string. PID/hash/startup-time readback confirmed the same process. A versioned operator correction compares exact UTC ticks; all source, hash, idle-session, single-GUI and profile guards remain enforced. Failed handoff receipts remain preserved. This was a deployment-script defect, not a production change.

## Status and limits

- **Fixed and delivered:** late opening completions cannot replace a newer selected conversation; source and exact package tests cover both orders.
- **Verified in this installed session:** sender/topic discovery, clarification/details, later follow-up, completion/UI cleanup, canonical opening. The user confirmed visible Austin opening and clear complete recap.
- **Preserved:** speech/input/interruption handling, tab executor, browser execution, translation/clipboard/UI, camera, overlay, prompts, settings and authentication. No fresh physical tab test was needed for this connector-only correction.
- **Unresolved/unmodified:** Photos-close popup disappearance, older multi-tab reopening limitation, occasional local regex timeouts under the observed environment, external provider empty-response/503 possibility. No safe root-cause correction for those was established here.
- **Unverified:** why Helper active-tab metadata disappeared, independent DOM evidence for each repeat open, and a new live-microphone run of the exact sequence. No further manual request is pending after the user's confirmation.

Evidence is under `.artifacts/connectors/voice-followup-recurrence-20261018/delayed-open-delivery-20261022/`, particularly `final-source-focused.trx`, `packaged-race.trx`, `installed-eight-turn/terminal-private.json`, `user-outcome-confirmation.json`, `reconciled-installed-acceptance.json`, installation boundaries and final runtime readback. Earlier failures remain unchanged. This is a private correction delivery, not public-release approval or completion of every historical issue.
