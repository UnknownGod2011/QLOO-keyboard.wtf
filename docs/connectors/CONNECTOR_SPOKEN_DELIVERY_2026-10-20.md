# Native connector spoken delivery

## Confirmed failure

The frozen ec4c493 package was not installed. Its six-turn Personal Live replay retrieved and retained the correct Daisy conversation, selected the same verified URL three times, made no secondary reasoning calls and cleared activity after each turn. The last recap nevertheless failed speech acceptance.

The provider's output transcription contained four numbered chronology items. All 32 provider audio parts reached the product playback sink, with no missing sink, overflow or interruption on that reply. Independent offline recognition of the actual WaveOut capture contained only the introduction and closing question. The provider had not generated audio for the substantive list. This is not evidence of a broken playback buffer, and no playback change is justified.

Original failed evidence is preserved under `native-contract-delivery-20261020/packaged-six-turn-v2`, including `semantic-verdict.json` and `offline-final-recap/whisper-transcription-private.json`.

## Isolated control and minimal correction

Before editing production, the same archived DLL was exercised with one additional field on successful connector receipts: `spoken_delivery`, requesting the requested answer in full as plain prose rather than details appearing only in a list/table. The 49-second combined recap/open and 71-second detailed follow-up both delivered the chronology and final outcome in actual device audio. Independent offline recognition verified both. Evidence: `native-contract-delivery-20261020/speech-delivery-contract-control`.

This supports a connector delivery-contract correction; it does not prove that formatting is the sole cause of every possible provider omission. `JarvisConnectorController.OperationReceipt` now adds that field only for verified successful native reads. Writes, failed results and the compatibility reader do not receive it. It neither requests additional facts beyond the user's scope nor removes partial-result or metadata-only restrictions.

The change adds no provider call, retry, state machine or audio processing. Permanent prompts, tool declarations, ordinary commands, tabs, translation and the audio engine remain unchanged. Six focused pre-change cases produced four expected failures and two passes. The corrected scoped gate passed all 696 cases, including verified reads across Gmail/Docs/Drive/Slack, exclusion of writes/errors/legacy reads, combined retained actions, cancellation, UI, navigation and Live delivery compatibility. Original failures remain in `native-spoken-delivery-20261020/tests`. Packaged and installed speech acceptance is recorded separately and is not implied by this gate.

## Acceptance boundary

Device capture plus independent transcription establishes delivered speech content, not room acoustics. Exact URL selection/dispatch does not establish a rendered Gmail page. Preserve those distinctions in the final report. The installed 28e5489 package remains the rollback until controlled delivery succeeds.
