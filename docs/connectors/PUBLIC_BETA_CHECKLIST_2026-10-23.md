# Public-beta release gates — 23 October 2026

**Not public-ready. Preparation only; no GitHub publication, website/update-feed change or OAuth submission is authorized by this checklist.** Private reliability acceptance remains separate from public-release approval.

## Current private evidence

**Later26October update: private f25011b is installed with matching payload hashes,
380passing exact-package checks and29protected profile files unchanged.** It
adds isolated native app-ID closing and observer-only Personal wait captions;
normal Jarvis Paint/Teams closing and physical caption acceptance remain pending.
e177447 is the immediate rollback, and its accepted connectors/tab/translation
behavior is protected. Public release remains unapproved and gated below. See
[the current isolated handoff record](UI_AND_NATIVE_CLOSE_2026-10-26.md).

**26 October update: private `e177447` is installed with matching EXE/DLL hashes,
39 extracted-package Calendar/permission checks and29protected files preserved.**
One normal confirmation created the exact test event in real Google Calendar;
the rendered account/title/time and connector cleanup were independently checked.
Earlier installed e33 recorded Gmail continuity, rendered exact opening,
disposable first/last/ordinal tab outcomes and large native translation/clipboard
proof remain separate evidence. The Calendar-only delta does not relabel those
as new e177 physical tests. Original intermittent audio breakup remains unresolved;
the user subsequently confirmed Devpost French translation, clipboard output and
visible dynamic UI on e177447. Loaded >50k Helper fallback remains unverified;
selected-section extraction's optional wait feedback is not fixed. An earlier
automated Devpost attempt was blocked by tool policy before app execution and
remains separate failed diagnostic evidence.
Current rollback is7f91f5d; all older archives remain protected. See the
[current context and proof boundaries](STABILIZATION_CONTEXT_AND_PLAN_2026-10-26.md).
The public gates below remain unmet; this update grants no publication approval.

### Historical private evidence — 25 October

**25 October update: installed private `68af242` FAILED the user's normal
microphone acceptance and is not accepted.** The collected physical readback
records tab-request rejection before Chrome and the failed Gmail search path.
Earlier passing text-driven sessions, rendered-thread observations and the
398 checks against that packaged DLL do not override the live failure.
`68af242` and the prior rollback archives, including `4fb5948`, `7618450`,
`018b835` and `d0290be`, remain preserved.

The initial source checkpoint `3e756b0` contains isolated corrections
`b4ffa90` (first native Gmail person-query preparation) and `926234c` (explicit
spoken ordinal-tab forms). Its bounded final gate passed **57 checks** using
the normal transcript observer, ordered dispatcher and controlled provider
fixtures. These corrections are **source-only: no new package or installation,
real-provider acceptance, physical microphone acceptance or public readiness is
established by that gate**.

Subsequent normal-shell real-provider checks exposed additional compound-open
and completion defects. `2fa22d6` reuses validated observed-thread selection for
the compound request and preserves that selection through identical-evidence
task completion; 166 focused checks pass. Its real run opened the requested
thread initially, but optional cached-fact extraction timed out after the
provider completed the details reply, blocking lifecycle completion.
`a242409` isolates that confirmed timeout without changing playback, routing
prompts or the model; 42 focused checks pass. The same three-turn provider check
then passed: initial exact opening, detailed reply and repeated opening,
with clear activity and zero extra searches on reopening. The user confirmed
clear sound and the correct thread in these source checks.
**Installed `68af242` is unchanged and remains unaccepted.** These
source checks are not final installed, physical microphone or public acceptance.
See [the current recovery record](REMAINING_LIVE_ROUTE_FIXES_2026-10-25.md).
Large-selection translation/clipboard/UI and physical speech outcomes remain
unverified for final acceptance; protected subsystems were not changed by these
source corrections.

### Historical private evidence — 23 October

The figures below describe the older `018b835` delivery only. They are not the
current installed version, current acceptance, or cumulative test totals.

- Installed private source: `018b835431f60271d7e7bae8dcd95035a5ea0213`, private version `1.4.21`. Archive: `dist/connector-candidate-archives/018b835-reviewed`.
- Focused source checks: **820 passed**. Checks against the extracted packaged assembly: **174 passed**. Actual desktop acceptance is **partial and not a release pass**: translation/copy and speech controls succeeded, but two recorded Gmail turns received empty provider responses. See [the delivery report](RELIABILITY_DELIVERY_2026-10-23.md).
- Immediate rollback: `d0290be`; `872cef7` remains preserved. Use archive manifests and the delivery receipt for exact hashes rather than historical process IDs.
- The candidate adds playback-stop identity/error diagnostics only. It does not change VAD, audio format, playback policy, model or permanent prompts. Provider interruption triggers and physical acoustic quality remain separate verification questions.
- Private signing evidence is **unsigned**. A private archive is neither an approved public installer nor proof of public production configuration.

## Required release gates

| Gate | Evidence required before public beta |
| --- | --- |
| Private reliability | Finish the bounded installed-app translation/copy, speech, Gmail search/open/follow-up and UI-cleanup acceptance. Preserve failed receipts. Record actual outcomes and any physical/browser-observation limits separately from tests, text generation and URL dispatch. Reuse unchanged browser/tab evidence with its version boundary. |
| Google OAuth | Last owner record, **20 October: data-access verification not submitted**. Current console status has not been refreshed. Reconcile requested and granted scopes, actual OAuth-owning project, consent branding/domains, privacy policy, scope justifications and demonstration video before owner submission. Sensitive/restricted access requires the applicable review; server-side restricted-data handling may require an assessment unless Google confirms an exception. |
| Windows signing | Establish the public publisher identity and signing process; sign and timestamp the final public executable/installer and verify the resulting signatures. Do not promise SmartScreen reputation or treat the unsigned private candidate as the public artifact. |
| Privacy and security | Approve accurate disclosures for connector providers, Composio/backend processing, Personal/Managed AI ownership, retained references, diagnostics, disconnection and deletion. Verify actual deletion/retention behavior, safe logs/errors, local action authentication, secret/dependency checks and public exclusion of private recording endpoints. Earlier website-policy findings are historical, not a current compliance verdict. |
| Production configuration | Verify intended production backend revision, connector catalog/auth configuration, endpoints, quotas and billing ownership without exposing credentials. Private staging success does not establish public backend readiness. Preserve Personal-mode routing and authorization boundaries. |
| Public installer | Build the actual public flavor; never rename a staging candidate. Verify package inventory, version consistency, SBOM/provenance, prerequisites, clean install, upgrade/uninstall, settings retention, one GUI/hotkey owner and correct Chrome Helper registration in an isolated supported environment. |
| GitHub | Obtain explicit publication approval, verify the intended public repository/branch and review the source delta. Current local `origin` must not be mistaken for a GitHub publication target. Exclude credentials, private profiles, recordings, email fixtures, operational logs, `.artifacts` and staging installers. |
| Website and update feed | Obtain explicit approval. Release notes, known limitations, privacy/support text, version, download URL, hashes and updater metadata must describe the same verified public package. No changes are made by this preparation. |
| Rollback and support | Retain immutable candidate/rollback packages and manifests. Test a compatible public rollback/update procedure; private rollback alone is insufficient. Define beta platform requirements, support contact, provider-outage behavior and a minimal incident template using version, time, command category and observed outcome without unnecessary private content. |

## Existing submission and release references

- [OAuth submission packet](GOOGLE_OAUTH_SUBMISSION_PACKET_2026-10-20.md) and [earlier public-readiness assessment](PUBLIC_RELEASE_READINESS_2026-10-20.md): historical preparation, not current acceptance.
- [Google Verification Center](https://console.cloud.google.com/auth/verification?project=keyboard-wtf-prod) and [declared Data Access](https://console.cloud.google.com/auth/scopes?project=keyboard-wtf-prod): confirm the intended project before changing or submitting anything.
- Google's [sensitive-scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification), [restricted-scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification) and [Workspace API user-data policy](https://developers.google.com/workspace/workspace-api-user-data-developer-policy).
- Microsoft [code-signing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control).

The release operator must append the final acceptance receipt and public approval separately. Passing private tests or preparing this checklist does not authorize publication.
