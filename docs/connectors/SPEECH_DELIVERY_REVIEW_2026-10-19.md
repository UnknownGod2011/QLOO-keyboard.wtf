# Speech delivery stabilization review — 19 October 2026

Status: active; latest user instruction authorizes one final private build/install after evidence review. Production source83eb981; documentation checkpoint18266eb. Frozen production DLL SHA256:4109AB021AB5CDBEED25B54587667CCC45FC17BCCA51B756028239858A62968C. Installed private remainsf082c5c; rollback46c4d17 and earlier candidates remain available.

## Confirmed causes and scope

- A connector opening could finish URL dispatch while Live generated an unheard, suppressed no-tool answer. Lifecycle delivery awaited that answer. The wait existed atfb0a067. cdb94ef/a0f06f2 allow only the current Personal connector-owned unheard reply to collect its completed result, rechecking generation, ownership, current human, speech, unpublished tools and empty playback under the existing send lock. Ordinary delivery is unchanged.
- A prior real recap exceeded the existing45-second PCM capacity and cleared queued speech while the device stayed Playing. The cap existed atfb0a067.12d0b1d raises only bounded capacity to120seconds; device, sample format, latency, VAD and interruption mechanics remain unchanged.
- A fresh retained read could throw after resolve but before admission remembered its execution task, leaving UI owned. a620304 binds that exact task earlier and clears only the failed read; earlier references remain. Original2RED and159/159corrected tests are preserved.
- Fresh-read validation threw a50ms regex timeout in MetadataOnly.83eb981 retains dedicated fixed matcher instances with the same six patterns/options/timeout. Cache-churn cost fell from roughly48ms to0.23ms; warm cost stayed0.02ms. Scheduling effects remain possible.270/270focused checks pass.

## Same-session six-turn result

Evidence: .artifacts/connectors/voice-followup-recurrence-20261018/raw-receive-six-turn-v5. Diagnostic raw-hook DLL BC562022…BC5055 maps to the production source; never install it.

Saved Daisy search/open recordings, then combined details/open, factual recall, repeat open with a2-hour injected controller clock, and another recap/open completed in182.6seconds. Six useful answers; four identical validated exact-thread URL dispatches;14native reads/20backend calls;0structured HTTP/Managed calls. Structured synthesis503 fallback was injected. Factual recall and repeat open made0additional data/reasoning calls. No inactive-task rejection, regex exception or overflow.

| Stage | Through device drain | Connector activity | Dispatch to delivered speech |
| --- | ---: | ---: | ---: |
| Recorded search |41.36s|12.819s|not applicable|
| Recorded opening |7.57s|no sustained owner|1.49s|
| Details/open |61.45s|6.271s|0.86s|
| Factual reply |9.36s|none|not applicable|
| Open again |4.71s|none|2.29s|
| Later recap/open |58.09s|2.033s|0.80s|

Long totals include roughly54–55seconds of recap speech, not a silent backend wait. All received recap PCM reached actual WaveOut consumption in order. Offline recognition independently reproduced both complete recaps and closing questions. Saved opening replaced one unheard0.101-second fragment; the provider interruption cleared0queuedbytes, then the complete acknowledgment reached the device and was independently recognized. UI ownership cleared before each read answer. This does not prove speaker acoustics, room echo or rendered Chrome outcomes; URL launching was inert.

## Ordinary speech and protected behavior

Production-DLL control speech-controls-source83-v1 recorded greeting,55-second ordinary reply, immediate follow-up and deliberate input-stream barge-in. Greeting did not terminate. Long speech completed in device capture although output text stopped mid-sentence; offline recognition includes the entire explanation and final sentence. First queued audio was0.91s for the long reply and0.67s for the follow-up. No resumed empty queue in the long answer;15ms maximum in the factual reply. Baselinefb0 observed1.18s/5.53s supply gaps, but varying provider replies prevent a controlled speed-win claim.

The strict control FAILED: Live proposed browser_automation for explaining the water cycle. Harness rejected it; ending was not reached. Offline replay through actual current andfb0 routing returned the identical no-start receipt,0network/actions and no UI owner. Preserve the failure; no browser/prompt rewrite justified. The separate greeting/ending control completed on the production DLL: greeting remained active; explicit ending closed the session once, approximately 164 ms after input activity ended. No connector work or protected-file changes. Existing synthetic input fixtures were used; this is not a microphone-acoustic test.

Open Gmail local dispatch median4.371ms; observation+dispatch9.343ms,12repetitions,0connector/account/model/provider work. This is not browser-rendering proof. Permanent19509-character prompt/62tools retain exact hashes. Listed translation, microphone/VAD, Helper/native-host, automation/command registry, hotkey/settings source files matchfb0 and installedf082. No new settings, hotkeys or public payload changes.

## Remaining gates

- Full source83 regression finished: 6,224 passed / 1 failed / 0 skipped. The 50 ms MailCountRequested timeout on a 19-character request remains unclassified. All four exact unchanged-binary controls passed. This does not replace the broad failure or prove environmental causation.
- Earlier corrected-run missing-middle speech and45/46-second socket waits remain unexplained. Later raw captures did not reproduce them; neither provider nor client is proven responsible.
- Physical first-two failed foreground/Helper matching before any input/action. Original receipt lacks mismatched-field data. A separate unexecuted diagnostic copy logs metadata before the unchanged guard. Current browser inventory reports local use disabled; physical first/last outcomes remain open.
- PublicGUI1748 started under Explorer after restart; privateGUI absent. No GUI handoff, new installation or public/Helper registration change.
- Installed f082 does not contain the source corrections. The user now authorizes final private packaging/install; final installed acceptance remains pending.

Continue from ignored handoff-live-checkpoint.json. Preserve every original failure. This report is not a readiness declaration.

## Human and runtime readback

The user heard the final corrected recap and reported **Clear and complete**. Evidence: raw-receive-six-turn-v5/human-audio-verdict.json, WAV SHA256 13728D328917F3CA1CC1CBB347E4D7A9C93C941F7DD01684C8A091BEBE401F9C. This does not prove a live microphone conversation is free of room echo.

Read-only runtime status on 19 October: public GUI 1748 owns a connected, fresh Helper with matching 2.1.21 versions and no duplicate Helper. Private GUI is absent. Private/public hashes remain unchanged. No browser actions, registration edits or process changes were made for this check. Supported browser observation still reports disabled; no current physical tab pass is claimed.
