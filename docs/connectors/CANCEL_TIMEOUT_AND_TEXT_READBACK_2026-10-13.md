# Search cancellation, uncertain reasoning and text clipping — 13 October 2026

This corrects reproduced issues from the user's physical test of `804fd8e`.
Rollback: `checkpoint/connectors-before-cancel-live-review-20261013`, exact source
`804fd8eabd5ffd9c1969e46bfd2d0f2d56b5ee28`. Physical installed EXE was verified as
`B52A720760D4A2AC9545DD9F1C574E9409ADE78B076A82FF61F5C5DBD84C1D7B`.

## Incident and causal findings

The 23:11–23:15 IST session connected to Personal Gemini Live normally. Both Gmail
searches completed successfully according to desktop HTTP 200 receipts and held
backend telemetry. The first search took 1.88 seconds of provider processing;
the second 0.65 seconds. This does not prove they selected the desired threads.

After each search, Personal structured reasoning lost its response after roughly
75 seconds and surfaced `PersonalAiFailure.AmbiguousTransport`, with no HTTP status.
The existing transport intentionally wraps timeouts/network failures in that type.
The reader handled raw TaskCanceledException and HTTP 429/503 but missed this real
wrapped failure. It therefore dropped out of the useful partial-evidence path.
There is no observed 429/quota rejection in this incident, so exhausted keys cannot
be asserted. No retry, key rotation or backend preference change is justified.

Live separately closed prematurely at 23:15:16 and its two bounded reconnects
failed with `PreDispatchConnection`, without an HTTP status. A later manual session
at 23:17 connected and responded. Earlier account refresh network failures also
exist. This supports transient connection trouble, not proof of its precise
network/provider/laptop cause. This patch does not repair a remote Live outage.

The user explicitly confirmed saying **"cancel the search"**. That clear human
instruction was absent from the anchored cancellation grammar, which accepted
"cancel that" and "cancel the task". A rejected model task reference was also logged,
but the deterministic immediate-control path should own this human cancellation.

The screenshot's title clipping was reproduced in real quiet HWND fixtures at
144 DPI: the fixed 26-pixel title label lost three pixel rows from "Reconnecting".
Earlier tests checked geometry and the custom detail painter, but did not compare
the title's full glyph extent. The corrected raster keeps both rows visible.

## Narrow corrections

- Extend explicit connector task cancellation to "cancel/stop the search",
  "this/that search" and "my current search". Existing single-current-owner,
  competing-subsystem, account/session, negative/quoted input and late-result
  rejection rules remain. No model ID can grant cancellation authority.
- Preserve verified current-scope native evidence when the reasoning delegate
  throws typed `AmbiguousTransport` or `PreDispatchConnection`. Report
  `ai_response_unconfirmed` or `ai_connection_unavailable`; neither claims Gmail
  disconnected, a quota error, HTTP 503, or a complete summary. Ambiguous reasoning
  is not replayed. Human cancellation still fences results. Transport timeout,
  audio, model, Live setup and credential ownership are unchanged.
- Use the existing fitted-ink painter for the title only while connector activity
  is displayed. Keep the ordinary title's original paint path otherwise. The
  connector detail uses solid fitted text when system animation is disabled.
  Preserve 460x82 bounds, 20x20 corner button, font sizes, positioning and audio orb.

## Verification and remaining limits

Local receipts: `.artifacts/connectors/cancel-live-failure-20261013/`.
`incident-red.trx` has seven causal failures and 20 existing passes. `ui-red.trx`
has four native DPI failures. `incident-green.trx` passes all 31 corresponding
cases with unchanged assertions. Quiet rendered images show the corrected title.
The added receive/dispatcher/CommandRegistry/controller replay checks that spoken
"cancel the search" wins even over a subsequent malformed model task ID.
Both Personal and Managed receive-path replay cases passed. The wider focused
run recorded 1,281 passes and one unchanged 50 ms date-parser regex timeout;
the same binary then passed all 15 relative-day cases in the isolated control.
The original failed receipt is retained, not replaced or presented as all-green.
The single full desktop run recorded 5,339 passes and ten failures, with zero
skips. Nine failures were image-save `DirectoryNotFoundException` errors caused
by the operator setting an evidence directory without creating it. One existing
Personal spoken-guidance assertion failed intermittently after passing in the
focused run; its precise cause was not established. After creating the image
directory, the same frozen binary passed all 31 UI/guidance control cases,
including every failed full-run case. Thus all 5,349 cases have passing evidence,
but the original full run itself was not all-green. No source, test or binary was
changed between these runs; this does not claim the intermittent fixture cause
is fixed. No second full run or test-runner tuning was performed.

Focused/full gates, any preserved harness failures and unchanged-binary controls,
package source/hash, installed verification, real provider read with a simulated
wrapped reasoning failure, and profile/startup/Helper preservation are separate
receipts. Consult `final-delivery.json` for delivery, not this pre-package document.
The backend remains unchanged with its prior 438/438 emulator gate. Normal routing,
permanent prompt/tools, Live/audio/VAD/STT, Smart Writing behavior, Browser Automation,
memory, selected text, billing, Helper and public/default traffic are protected.

Natural spoken quality still depends on transcription and a working Live provider.
Deterministic cancellation is immediate once the recognized instruction arrives;
it cannot receive speech through a broken network session. The existing Cancel
shortcut remains available, and this pass starts no microphone sessions or external
writes. No universal connector acceptance or fixed Live connectivity is claimed.
