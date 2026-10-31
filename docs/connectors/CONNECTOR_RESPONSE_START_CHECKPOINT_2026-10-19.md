# Connector response-start stabilization — 19 October 2026

Status: in verification; nothing installed. Base source f9608b9. Installed private remains 0ded695 (rollback f082c5c preserved).

## Reproductions and comparison

The installed six-turn replay had a 97.06-second opening, including an 85.67-second socket receive wait. The later diagnostic repeated-recording run reproduced a 56.15-second first opening and canceled the next at the 180-second session limit. Exact URL dispatch completed promptly, UI cleared, dispatcher had no unpublished calls, and no new provider speech arrived during the long wait. This was not a playback-buffer stall.

The cdb94ef correction allowed current connector-owned unheard replies to be superseded immediately. fb0a067 instead waited for the original turn to finish. A same-DLL counterfactual restoring only that wait avoided the very long stall, but openings took 14.56–23.11 seconds and one spoken result incorrectly denied an opening that had dispatched. That run's raw failed=false is not semantic acceptance; reviewed-result.json rejects it.

A second isolated counterfactual waited only for the current provider response to begin before invoking the existing guarded notice. Its waits were 78.9,225.2,171.8ms; all three subsequent recorded openings acknowledged the correct completed operation. Total turns including input recording and speech were 9.28,9.82,9.69 seconds. These runs establish a reproducible ordering problem and evidence for the boundary correction; they do not prove provider internals or all physical speech scenarios.

## Minimal correction under test

Only GeminiLiveConversationService.cs production code changes: the existing current-owner replacement waits on the current reply's response-start/completion signal, then retains its generation, human, cancellation, playback and ownership rechecks. The receive handler signals actual received response data even when intentionally suppressed. Begin/end boundaries release stale waiters. No timer delay, polling, additional model call, prompt/tool, VAD, microphone, device or format change.

connector-response-start-red: original production fails the real loopback WebSocket assertion because it sends the notice before first output. New cases protect cancellation, superseding replies, completion without response and existing send-lock safety. Previous safety tests now explicitly represent their existing-response premise.

Full tab gate:6273/6274 passed. The one failure was an exact-source-string assertion expecting the old two-argument observer call; farewell ordering and code did not change. Its source anchor is corrected to the actual three-argument call while preserving the ordering checks. Original failure retained.

## Current next action

Await connector-response-start-v1 focused gate. If it passes, pin exact built assembly and replay the original six-turn Gmail scenario with actual WaveOut consumption and independent speech-content checks. Do not use a diagnostic success flag as browser rendering or acoustic proof. Then rerun the final broad gate, protected performance/prompt comparisons, and controlled packaging/install only if outstanding primary blockers are resolved.

Real ordinal close has separately passed: second/third B/C closed; independent Chrome inventory confirms A/D remain, foreground guard unchanged. First/last recorded passes remain preserved. Multi-reopen and final installed GUI/Helper/hotkey ownership are still pending.


Current revision note: v1 focused gate816/817passed; replacement waiter took old8second send wait (corrected). v2 then817/817passed. Additional actual loopback variant proved an older CurrentTurnHadResponse flag could bypass the new boundary (stale-red1failed/1passed). Final candidate uses the per-generation response-start signal only, signaled by actual data in the current provider frame; it does not change that existing diagnostic flag. v3 expanded focused gate running. All failures preserved, no installation.


## Current milestone — corrected production six-turn replay

Focused gate connector-response-start-v3:818/818 passed,0failed,0skipped. Frozen corrected production DLL F7E6087D4EF0DE027D451D81380358960BBCD48C2E62CB712BE32887F95533E5. Independent read-only concurrency review found no substantiated blocker; existing session/task cancellation, owner/generation/send-lock and audible-output guards remain.

corrected-response-six-turn-v1 completed all six turns in one actual Personal Live session, using the original saved Daisy/open recordings then the original four text follow-ups. Windows device consumption and drain were verified; all six replies completed, four openings used the same validated destination, no inactive-task error, all UI clear, no protected profile changes, no Managed charges. Browser dispatch was intercepted, so this run is NOT rendered-browser proof. Reasoning fallback was injected,0actual structured requests; live/native Gmail calls were real.

Opening9.674s includes3.5s recorded input and4.53s emitted speech; first queued audio4.716s after input start. Repeat-open6.477s includes4.24s speech, first queue1.452s. Factual follow-up first queue0.798s with zero native reads. Detailed read/open first queue8.220s; device drain67.826s includes58.89s continuous speech. Final recap first queue3.382s; drain53.652s includes49.19s speech. These durations must not be mislabeled as backend latency. UI ownership intervals14.382s search,7.201s detailed read,2.314s later refreshed recap; opens/factual answer had no connector loading state. No internal zero gap longer than90ms in emitted device audio.

Offline transcription of device-consumed opening is complete and agrees with the acknowledgment. Longer recaps are being independently transcribed. Physical speaker/room acoustics remain separate; prior human confirmation of an earlier capture is not new-build acoustic proof. No installation. Next:finish independent audio-content checks,freeze correction independently,full serial regression gate and protected speed/prompt comparison,then package and test exact packaged assembly before controlled private handoff. Current installed0ded695/rollbackf082c5c remain preserved.

Offline checks now complete: opening acknowledgment and both long recaps independently transcribed from actual device-consumed audio; complete final sentences agree with generated answers. reviewed-result.json accepts corrected-source behavior with explicit browser/physical-acoustics and injected-reasoning limitations. No retained source or provider search change.
