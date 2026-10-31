# Recorded speech and connector boundary recurrence, 18 October 2026

Current HEAD da964929e890302afe1f0036a87055aa955d22e9; installed application source 40d8a722e77dc59bc4f7b7ac3692145b67c43b97. Installed EXE 0D847FEEED162618ECDB65986E0077DFCB1FD551A56E67EBBBC1AAA1DCBDD990; extracted DLL DA33618B690F2AD45FE72888605C3F66FAFC41694744FFDA7C662EE6D8B32D97. Human failure supersedes earlier readiness prose. No new installation in this investigation. Checkpoint tag checkpoint/connectors-40d8a72-human-voice-followup-failure-20261018 and preserved pre-final-binding-working-tree.patch.

## Confirmed failure

recorded-barge-installed-v3 replays the user's Daisy recording, then streams the user's exact-open recording after first actual reply PCM. START frame40 -> provider interruption frame41 -> OLD output completion frame42 -> correctly recognized new open input frame44 -> END frame45. Output completion cleared ConnectorSpeechActive/InputObserved, so final connector admission never ran. Zero opening proposals/dispatches; Live produced a denial. This is an admission bypass, not a Gmail outage or an inactive backend receipt. Original failed JSON/log/WAV are preserved.

The exact GeminiLive source blob ea6f8112e8154acf556d59baee8c97a2dff6829b is identical in d245ac4/fb0a067/40d8a72; microphone control and Personal Live transport also identical. The boundary flags originated in f355a4d. The latest search patch did not introduce the underlying race. Normal provider interruption still clears queued speech; no VAD/audio-format/playback change is justified by this evidence.

## Recorded uninterrupted result and proof limits

recorded-installed-v1 failed because the harness rejected an additional address-bar-focus proposal. That failure is preserved and is not product acceptance. Corrected v2 passed Daisy -> exact retained open -> combined details/open on installed bytes; no repeated search in follow-up. Two actual Personal reasoning calls returned429, native results survived. All three replies contain decoded PCM; details waveform38.12s was independently transcribed offline using the existing local Whisper model and contains the substantive recap. No clipping or virtual queue supply gaps in these three captured waveforms. This does not prove physical speaker audibility or establish the cause of human-reported cracking. v2 URL dispatch is inert-browser proof only.

## Narrow correction under validation

Only GeminiLiveConversationService connector input tracking changes. Input activity flags and a bounded per-activity transcription buffer survive OLD reply completion, then consume/reset at END. Accepted finalized connector input is observed through the existing ordered callback before its existing read/open executor starts, preserving exact human-text authority even if older output committed conversation history between ASR fragments. Ordinary commands do not reach this accepted callback. No audio/VAD/STT/playback/browser/translation/prompt/tools code changes.

Original regression failed; removing only the two flag resets passed209 focused checks but split-ASR still failed. Three-case ordering test on the interim buffer patch:2pass/1fail, full expected text versus suffix observed. The final binding change is now being tested. An empty final-focused log and wrapper exit with null native exit are UNVERIFIED, not a pass. New tests launch dotnet directly hidden with redirected logs.

## Console investigation

At08:05, shell calls from Codex PID16964 spawned pwsh24948/14524 and conhost8516/17528 at the same timestamps. Later read-only console snapshot contains zero visible console windows, while Chrome native-host wrappers remain hidden. Program/nativehost/update/test-helper launch code is unchanged across d245ac4/fb0a067/40d8a72. The tool call/process pairing supports our diagnostic launches as one source of flashes, but not attribution of every historical transient window. Subsequent diagnostics use Node fs or windowsHide/no-console child execution, retaining all errors/logs; no user terminal/process or app registration was changed.

## Current protected runtime

Fresh runtime capture: sole private GUI12964; public27676 is Chrome native host only; PersonalOnly revision17, all5saved hotkeys; Helper connected/current page available, no multiple helpers. Public EXE remains2928EED20023B40198BD6AA3D1418775224EA97A7393E8D33114ADA07B6452B5. Source-only fix remains uninstalled. Pending: final focused run, actual corrected recorded barge-in, ordinary dispatch/payload measurement, one final desktop gate, and only then a controlled private package/install. Browser tool and hardware audibility remain separate proof requirements.

## Corrected actual recording acceptance and timing

`recorded-barge-corrected-v5/acceptance.json`: frozen source DLL93288B5A2301BDA3DA875A7A1AB44CCB6AB9B897CBE8E13661F85A9CA956C7AF; failed=false,117.865s,17backend requests/13native executions,2reasoning delegate attempts:1actualHTTP429 then1local cooldown (not two wire429s),0Managed. Same recorded physical order now admits the follow-up. Exactly1verified retained URL dispatch for barge open and1for explicit combined details/open; extra model proposal reuses the same operation. Old interrupted partial completes before newer recognized input; no old-human reply after newhuman recognition.

Open END to admission25ms, exact dispatch57ms, first PCM736ms, answer completion2.615s; no native/reasoning work. Combined END to native response2.049s, final native response6.404s, exact dispatch6.446s, first PCM7.852s, final completion65.708s; PCM57.881s. Factual question first PCM846ms, completion14.349s, PCM13.440s;0new backend/native/search/structured reasoning/open. These are separate stages; queued PCM is not proof of acoustic speaker quality.

Final source focused gate296/296. Ordinary benchmark: Gmail dispatch7.7495ms, observation+dispatch18.9309ms median versus preserved installed9.8663ms/19.349ms;0connector/account/provider/reasoning calls. Other cases retain zero connector work; saved Discord~196ms dispatch remains. Prompt19509chars/62tools/30887toolJSONchars and exact hashes unchanged.

One real output-device check used installed unchanged StartPlayback/TryQueuePlaybackAudio/StopPlayback with3.550625s capturedclip: device remainedPlaying,170430PCMbytes drained,positionadvanced188986bytes,no unexpected PlaybackStopped/noexception,normal300mslead. No microphone/loopback/network/configchange. This verifies output-device consumption, not sound quality at the physical speaker. Broad final gate is running once on frozen93288...; no new candidate installed yet.

The normal app account refresh was used once, renewing the existing rotating session via the app itself. Copied-profile refresh was deliberately avoided. Physical metadata after renewal confirms same session hash, access expiry08:30local, while virtual diagnostic reads still expose an oldOct9 profile. A v4auth-precondition failure before any provider work is preserved; v5 was launched in the physical host with explicit hidden startup.
