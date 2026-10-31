# Paged-result opening continuation — 24 October 2026

## Installed state and scope

The installed private application remains `018b835`; `d0290be` is preserved as
rollback. No installation, public publication, configuration or authentication
change was performed in this continuation. Capacity source correction `ec2dd6f`
is still source-only. Speech, prompts, VAD, playback, tabs, camera and window
management were not changed.

## Confirmed retained-reference defect

Stored real native search receipts in
`sender-recurrence-20261021/desktop-proposal-eight-turn-v4/live-receipts-retained-session-final-v1-private.jsonl`
contain a verified, untruncated, fully retained page with one conversation and
`hasMore=true`. The old code treated this page as unusable for an explicit
singular opening because it was not an exhaustive mailbox search. Earlier replay
success depended on the model proposing an observed resource ID; when no tool
was proposed, the deterministic retained opener did not run.

`ConnectorSessionResult` now distinguishes complete returned-page identities
from exhaustive search coverage. A fresh explicit singular opening may select
the sole returned conversation with a verified account-qualified native URL.
It still refuses omitted/truncated/unverified candidates, multiple threads,
unknown accounts, malformed paging and stale goal evidence. Automatic primary
selection and Both/All opening remain unchanged. No additional model or provider
call is introduced.

This is a confirmed code/real-receipt defect, **not proof that it caused every
reported refusal**. The human-session log does not include receipt payloads.

## Focused evidence

Under `reliability-fixes-20261023/paged-open-recovery/`:

- `paged-open-before.trx`: both reported opening phrasings fail on unchanged code.
- `paged-open-after.trx`: 193 passing related retained/dispatcher/receive tests.
- `paged-open-final.trx`: 72 passing checks after malformed-paging coverage.
- `paged-open-review-negative-20261024.trx`: 70 passing selection checks,
  including truncated/unverified paged evidence, invalid account and stale goal.

Counts overlap and must not be added. The receive-path test uses separate
activity/transcript/end frames with no model tool proposal, verifies one exact
dispatch and cleared ownership, and proves no additional Gmail/reasoning call.
It is not physical browser or microphone acceptance.

## Actual installed-app failures remain open

The user supplied the complete historical log, and a supported read-only native
Notepad view exposed newer desktop events. The stale ordinary file view is no
longer a reason to request more user log excerpts.

- Human session `f82db6f7b1664725aa1c3543f408d794` (23 October) completed native
  retrieval and then generated follow-up speech with no tool calls. Final spoken
  input was present before activity-end. That trace does not demonstrate the
  earlier split-speech race. Provider interruptions cleared queued speech;
  their acoustic/user triggers remain uncertain.
- Diagnostic session `1ee0f7542a0c417f91cbbd3661e28d4e` reached native retrieval.
  Its second recording never produced a sent-recording receipt or provider
  input event before the diagnostic endpoint deadline. The timeout must not be
  described as proof that the model received and refused the opening request.
- Installed text session `15d35dc2c1d240b6b850634bc27b69b3` (24 October,
  12:55 IST) generated speech without any connector call for the initial search.
  The follow-up invoked `browser_action`; Chrome displayed New Tab. This is a
  failed sequence, despite drained audio queues and successful tool dispatch.
- Separate session `c4d676db09134d10b28b5c80dc8096a0` (12:57 IST) did resolve and
  execute Gmail twice, then produced 42 provider/playback audio chunks. The log
  reported partial read coverage. No follow-up was sent in that session. This
  establishes that tools were available, not that the first search was correct.

The remaining diagnostic gap is the actual response content and retained native
receipt for the failed desktop route. Do not suppress refusals, rewrite prompts,
or infer successful speech from queue settlement. Corrected-build desktop
acceptance and later sending capability acceptance remain outstanding.

## Subsequent bounded runtime evidence

Source correction is frozen at `b54efd3`. The following evidence does not erase
the failed attempts above or establish corrected-build installed acceptance.

- Installed baseline session `006d1149ee674a79949d58c99eaa1bb7`, 24 October
  15:37–15:38 IST, replayed the user's saved Daisy recording through the actual
  GUI's microphone callback clock, then submitted “Can you open that email
  thread in a new tab?” as a text turn. Native connector resolve/search succeeded.
  The search completed with 95 provider/playback audio parts and cleared task/UI
  state. The follow-up used `open_url`, not Browser Automation; it delivered 17
  audio parts, drained its queue, and stopped normally with no device error.
- Native Chrome accessibility inspection independently showed the actual Daisy
  conversation, “DeveloperWeek Domain Roulette → request to acquire
  keyboard.wtf,” with its messages and participants. It was not Inbox, search
  results or a blank tab. Initial Helper metadata was stale; the rendered page,
  rather than that snapshot, establishes this baseline opening pass.
- Search took 39.76 seconds including recorded input and the full spoken output;
  the follow-up took 5.16 seconds including acknowledgement. Native search API
  execution took about 2.95 seconds. Physical acoustic quality and a microphone
  follow-up were not newly verified. The owned diagnostic session was stopped.
- The existing direct-read retention preference already preserves a verified
  thread identity when body coverage is partial. No additional selection rule or
  body-completeness change was justified or made.

## Real-provider large translation

`capacity-native-provider-20261024/result.json` used the actual source translation
operation and Personal Gemini transport with 106,489 source characters. Three
ordered requests (49,015 / 49,070 / 8,404 characters) completed with `STOP` in
27.33 / 24.44 / 4.62 seconds. The combined French output was 119,913 characters;
all 76 numbered section markers were present in order, with one final output
publication. Total operation time was 56.93 seconds. Progress advanced through
all three parts, copying, and cleanup.

The test used an isolated encrypted Personal-key vault and an in-memory copy
sink. It did not refresh the paid account, modify the real clipboard, install
anything or exercise visible UI/microphone admission. Protected source profile
hashes were unchanged. Marker/content checks establish section coverage, not a
human sentence-by-sentence linguistic review. Preserve these distinctions during
packaging and acceptance.
