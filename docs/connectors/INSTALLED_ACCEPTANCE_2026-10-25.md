# Private 7618450 installation and acceptance checkpoint

## Confirmed installed state

The user-run guarded v3 handoff completed on 25 October at 05:58 IST.
`handoff-result-v3.json` records successful installation, exact EXE and extracted
DLL matches, 29 protected profile files compared, PersonalOnly, all five saved
hotkeys preserved, and Chrome Helper connected. Private GUI PID 17796 owns the
normal physical profile. Public PID 33256 is the Chrome native host, not another
GUI. Source/package is `7618450c021c7832204c76e8183084eed9a607af`.
Previous private 018b835 and d0290be rollback artifacts remain preserved.

No production source was edited or rebuilt during this scheduled continuation.
The 402 exact-package focused checks and existing real-provider multipart
translation evidence were reused, not rerun.

## Installed acceptance failed before the first scenario

The prepared installed-app sequence was attempted once. Its diagnostic start
returned HTTP400 before any Gmail stage. Preserve
`candidate-7618450/installed-acceptance/terminal.json`; this is not a passing
microphone, speech, connector, browser or translation check.

The fresh physical log, read through native Notepad because the tool filesystem
still sees an older redirected log, identifies the cause:

- 06:25:49: account refresh returned HTTP409.
- 06:25:50: Jarvis connection failed with PaidApiException409.
- Physical shutdown recorded `hadProviderSocket=False`.
- The runtime reported: "The refresh session is expired or revoked."

The Personal API key remains enabled and authentication-validated. This account
failure occurs before the Gemini connection; it does not implicate playback.
The server's 409 can also represent a refresh-token hash mismatch. The available
evidence does not distinguish revocation, expiration and competing refresh use.

## Supported recovery and pending work

Normal Sign in while the local token is considered signed in retries refresh.
The existing refresh code invalidates 401/403, but not this 409. Accordingly,
the app's normal **Sign out** control was used for this desktop only, then
**Sign in with Google**. Signed-out state was visibly confirmed. No account
files were copied, connector scopes expanded or public profile changed.

Google offers an account that has not been verified against the previous app
account. User completion with the same Jarvis account is pending. Do not guess
an account or reconnect Gmail. This recovery preserves settings, Personal keys
and hotkeys; retained in-memory references properly begin fresh after login.

After authentication, verify the same running process and Personal policy,
then run the bounded installed sequence into a new evidence directory. Verify
rendered Gmail and actual selected-text translation, dynamic UI and clipboard
separately. Physical speech quality remains a separate observation. Do not
rerun installation or treat queue settlement as semantic success.

Evidence root:
`.artifacts/connectors/voice-followup-recurrence-20261018/reliability-fixes-20261023`.
The continuation checkpoint and `candidate-7618450/installed-auth-blocker-20261025.json`
contain exact next actions. Public release and email sending remain deferred.

## Authentication recovered; bounded installed sequence completed

The user completed sign-in with the expected account. Runtime state confirmed
signed-in/active, no account error, PersonalOnly and the same private PID17796.
No reinstall, production edit or connector reconnection was needed.

One new evidence directory preserves the prior failed start:
`candidate-7618450/installed-acceptance-auth-restored`.
Session `418334e8ce8a4a7b9ec132379ebf2aaf` used the actual GUI recording endpoint
and real WaveIn callback timing; room audio was not published.

| Turn | Input path | Total seconds including input/output and settlement | Evidence |
| --- | --- | ---: | --- |
| Daisy search | User recording | 25.84 | Native resolve/execute, 50 provider and playback parts |
| Open it in a new tab | User recording | 8.31 | `open_url`, 18 provider and playback parts |
| More details | Text turn | 25.74 | Fresh authorized native read, 80 provider and playback parts |
| Most recent statement | Text turn | 5.55 | No tool call, 20 provider and playback parts |
| Open the thread again | Text turn | 6.05 | `open_url`, 15 provider and playback parts |
| Seven plus three | Text turn | 5.95 | No connector/tool call, 9 provider and playback parts |

All six turns returned to Listening with zero queued bytes and no long-task UI
owner. Fresh physical log inspection found no provider interruption in this
sequence, no missing playback sink, and an expected error-free device stop with
zero queued bytes. Startup was1292ms; native search3475ms and details read1339ms.
These are stage measurements, not a statistical latency benchmark.

Helper metadata after the first opening identified the correct Daisy subject
and canonical conversation URL. It temporarily disconnected during the repeated
opening. Both action receipts contain the same validated account/thread URL;
neither invoked Browser Automation. This does **not** yet establish rendered
page success or the correctness/completeness of the audible answers.

Browser tools are disabled. Native Chrome inspection reported a minimized
window. Notepad eventually exposed the current log, but a subsequent Ctrl+N did
not demonstrably open a disposable tab and screenshot retrieval timed out.
No translation request was sent without a confirmed source selection. The user
was asked to restore the desktop windows and, if heard, confirm recap quality.
Large installed selection, visible translation UI and actual clipboard remain
pending. Do not repeat the six-turn test merely to wait for those observations.
