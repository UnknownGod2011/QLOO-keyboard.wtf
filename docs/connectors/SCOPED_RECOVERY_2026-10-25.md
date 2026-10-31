# Scoped desktop recovery — 25 October 2026

## Preserved baseline

Installed private source `7618450c021c7832204c76e8183084eed9a607af`, executable
SHA256 `785C4550ABA5DFCFF04A2E276B84730F92C843E0C768A25A0A01CE7BCE2BB0E5`.
Its reviewed archive and the `018b835` / `d0290be` rollback archives are retained.
Existing account recovery and Personal settings remain in the running private app.

## Confirmed evidence

The physical log, read through Notepad, records local positional-close rejections
at 08:22:17 and 08:26:26 IST: `explicitEdge=true wholeRequestOwned=false
noTabClosed=true`. Those attempts never contacted Chrome. This establishes a
local parsing rejection, not a Helper connection failure. The user's exact ASR
text was intentionally not logged, so it is not possible to identify every word
that caused those two rejections retrospectively.

The whole-request rejection fence was added by `5d846e5`, after the earlier
positional checkpoint `d245ac4`; ordinal-set support later extended it in
`f9608b9`. The executor and foreground safety checks are not changed here.
Focused reproduction establishes that numeric ranges such as `2-3 tab` and
continuation prefixes such as `Then close the first two tabs` were not owned.
Standalone saved first-two and last-two recordings still transcribed correctly
in consecutive turns through installed `7618450` (session
`83008482a40a452bbeace9150a0617c8`). That was a routing probe with a non-browser
foreground, not proof of actual tab deletion.

At 08:34:13 IST session `2d469e32333d494ea8b1826680fdf707` captured **48,242**
characters in 188 ms, accepted the full source and started translation operation
`e329d28a5a8d4d3ea39f14c968574468`. Backend operation
`7ac305e242b8477294c0ef730d596c15` returned Personal provider **HTTP 503** at
08:34:14.896. The job failed and cleared its progress owner. This failure was
not a selection-size rejection. The shortened Live context excerpt is separate
from the full retained translation source.

## Isolated corrections

`778a870` changes only routing in `JarvisAutomationService` and the rejection
receipt in `CommandRegistry`. Bounded ascending numeric ordinal ranges and
leading continuation/courtesy words use the existing exact-set close path.
Invalid ranges, negation, discussion, mixed intent and conflicting model targets
remain rejected. Unresolved requests explicitly report `request_not_resolved`,
`connection_attempted=false`, and clarification rather than a connection error.
No change to tab closing execution, the extension or foreground checks.

`4b14f9a` changes only `SelectedTextTranslationOperation`. One confirmed Personal
HTTP 503 can retry the failed part once after a cancellation-aware one-second
delay. Successful paths have no delay or extra provider request. Completed parts
are not repeated; ambiguous transport, quota and authorization failures are not
replayed; backend/account ownership stays fixed. Clipboard publication remains
after every part completes, with one final copy. Existing source partitioning,
output-limit subdivision and dynamic UI remain in place.

## Verification before packaging

Evidence: `.artifacts/connectors/desktop-recurrence-20261025/`.

- Original translation recovery tests: **3 failed / 3 passed**, preserved.
- Original tab/routing reproduction: **11 failed / 22 passed**, preserved.
- Corrected translation routing/ownership checks: **208 passed**.
- Corrected tab/translation checks: **464 passed**.
- Final Release ConnectorStagingCandidate gate: **772 passed, 0 failed/skipped**,
  66 seconds. Includes consecutive first/last edge ownership, exact ordinal sets,
  invalid/mixed requests, foreground safety, translation recovery/cancellation,
  translation dispatch/reply ownership, retained combined read/open, sender
  recovery and connector exception cleanup.

No production changes to Gemini Live, VAD, audio/playback, prompts/tools, goodbye,
camera/overlay, connector search/retention/opening, authentication or settings.
The range parser is reached only for close requests; ordinary commands do not
gain model, connector or retry work.

## Remaining acceptance

These source checks do not establish physical tab outcomes, rendered Gmail,
clipboard completion or acoustic speech quality. The corrected package must be
checked and handed off once, then run a bounded installed session: Riccardo native
search/open/details/third follow-up/reopen plus one ambiguous sender; small and
large selected-text translation; first/last closes on disposable tabs in existing
and newly created Chrome windows. Preserve failures and do not infer rendered
success from dispatch. Browser-tool access currently reports disabled; native
window input has also been interrupted by changing/minimized windows.

Installation must use the existing physical-profile guard. Tool-side profile
access has previously resolved to an old redirected copy; do not bypass that
guard or lose configuration/rollback in order to claim deployment.
