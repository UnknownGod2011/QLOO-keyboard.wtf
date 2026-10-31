# Post-delivery connector review — 22 October 2026

**Later delivery:** this source-only review was subsequently promoted as private `d0290be` with `c0f3fda` included. See [DELAYED_OPEN_DELIVERY_2026-10-22.md](DELAYED_OPEN_DELIVERY_2026-10-22.md); the original review state below is retained as history. The later investigation clarified that the regex fault originated in pre-try worker classification and was surfaced by fixture cleanup, rather than being exclusively a cleanup operation.

The installed private application remains **1.4.21 / `872cef7`**. This review produced one isolated **source-only correction, `c0f3fda`**, after a failing concurrency reproduction. It has not been packaged or installed. No public or private application process was restarted, and no account, browser, audio device, camera or clipboard was exercised by this review.

## Confirmed finding: delayed opening can replace the newer selection

`JarvisConnectorController.TryOpenRetainedResourcesAsync` checked the current human request before dispatch, but its completion block checked only session/account validity. Two openings from the same retained candidate set could finish in reverse order:

1. The first user turn selects verified conversation A; browser confirmation remains pending.
2. A newer turn selects verified conversation B; its confirmation completes.
3. A's late confirmation replaces the retained primary thread with A.
4. A subsequent “open it again” opens A instead of the more recently selected B.

An isolated test reproduced this on the delivered source. The final assertion expected `#all/bbbbbbbb`, but received `#all/aaaaaaaa`. The test uses inert browser receipts and synthetic mail: it does not access Gmail or create tabs. The actual browser path awaits the existing Helper's `open_direct_tab` receipt, so a delayed completion is a relevant execution boundary; this exact interleaving has **not** been observed in the user's desktop session.

Git blame identifies the primary-thread write as added in `5934e65`. The neighboring delivery-owner write dates to `7300c9b2`; it had the same missing current-request check. This is evidence for the code race, not proof that it caused any particular historical wrong-tab report.

## Small correction and verification

The completion block now rechecks its existing human request snapshot before updating either the retained primary thread or delivery owner. The existing admitted-status continuation callback remains accepted. The action's original receipt is still completed, even when its request is superseded. No task is revived, no tab is repeated, and no new timeout, retry, model call or state subsystem was introduced.

Only one production method changed, in `src/Services/Connectors/JarvisConnectorController.cs`. The two completion orders are covered in `ConnectorCombinedRetainedOperationTests.Delayed_open_completion_cannot_replace_a_newer_turns_selected_conversation`.

Evidence under `.artifacts/connectors/voice-followup-recurrence-20261018/post-delivery-review-20261022/`:

- `delayed-open-red.trx`: the original single failing reproduction, before production correction. Its test name begins `Late_open_completion`.
- `delayed-open-green.trx`: **26 passed, 0 failed**, including both completion orders, normal observed selection, later detail reads, duplicate proposals, exact account validation, cancellation, unknown-dispatch handling, status continuations and combined read/open cleanup.
- Both original logs and terminal receipts are preserved. Existing compiler/analyzer warnings are retained in the logs; they were not new failures.
- A bounded cross-area run covering retained operations, topic recovery, sender recovery, exact open dispatch and generic retained recovery completed **271/272**. One existing retained-context test threw `RegexMatchTimeoutException` inside `TryLiteralMailQuery` while its fixture was resetting. The same test passed by itself on the immediate isolated rerun (`isolated-cleanup-timeout.trx`). The timeout cause is undetermined; the broader run remains recorded as failed, and the regex is outside this correction. No retry was used to conceal its original result.

These are source-level controller/broker tests, not a new installed-app acceptance or physical sound/rendering claim. No large regression suite or paid provider test was rerun. The correction adds one local ownership comparison at retained-open completion; the ordinary-command, native-search, audio and browser execution paths are unchanged. No new latency measurement is claimed.

## Other reviewed boundaries

- Proposed Gmail destinations resolve against observed account/thread/message identities and canonical native URLs; the model's URL does not become authority. Existing tests cover unknown identities, conflicting accounts and repeated proposals.
- Completed task recovery resolves fresh read authority; session cancellation, account invalidation and explicit action restrictions remain in force. Account invalidation removes retained results and cancels their pending admissions.
- Exact opening and cached follow-ups do not add native searches or secondary reasoning calls. Successful evidence and temporary 503/429 errors retain distinct handling. This reuses the delivered eight-turn runtime evidence; it is not a fresh provider reliability certification.
- The overlay code still specifies topmost/no-activate behavior and hides on idle or completed UI states. Its file is unchanged from `fb0a067`. This cannot distinguish an intentional hide, virtual-desktop cloaking or a z-order event in the reported Photos-close incident. No speculative window-management correction was made.

No other confirmed defect emerged from these inspected boundaries. This statement is limited to the review scope, not a guarantee about every request or subsystem.

## Installed protection and continuation

Readback still matched the delivered EXE SHA256 `E29C4B93476ED2C5B6E27B512357830D37D0524079E550D2BB55154D989B9499`, with private GUI PID 19024. The second executable process was the existing registered public native host, not a public GUI. The approved `1da3da5-reviewed` rollback installer, package and proof remain available.

Speech, accumulated input, interruption handling, browser/tab execution, translation, camera, overlay, settings and hotkeys were not modified. The running `872cef7` therefore still has the newly reproduced completion race; `c0f3fda` must not be described as installed. A future promotion must use a frozen package and bounded installed validation. Do not replace or restart the user's working candidate merely to extend this review.

The prior physical-speaker and live-microphone limits, Photos-close disappearance, older multi-tab reopen limitation and external Gemini empty-response possibility remain as documented in `FINAL_CONNECTOR_DELIVERY_2026-10-22.md`. The user is busy; no additional manual check was requested.
