# Public-beta release gates — 27 October 2026

**Working document; not release approval.** This records the current private baseline,
the bounded connector repair in progress and the remaining public-release gates.
Source checks, a provider operation, an installed private build and a clean public
installation are separate evidence. Nothing here authorizes publication or wider OAuth scopes.

## Protected baseline and pending correction

Installed private source: **4a1f4d059f0ff8714148e4c365643fb1e4be1bb2**.
Baseline documentation checkpoint: **5feb77f**. Protected rollback: **1d745d2**, with all
earlier recorded archives retained. The installed EXE SHA256 is
`0A965E4DD09B270C5F94CBEDD3D735BFC408900B84010522D72F2F104C5C03A5`;
the exact embedded DLL SHA256 is
`7E786885E3B3A6891695C05D2FDFC1C93FB1C88EBB2429F66928C57D097C0F6C`.
See `CALENDAR_MODE_AND_POPUP_FINDINGS_2026-10-27.md` for installed-file, profile,
Helper, hotkey, provider and rendered-Calendar evidence and its limitations.

The user's subsequent November21 reminder failed. The current targeted source repair
is restricted to `JarvisConnectorController.cs` and `ConnectorTaskService.cs`: distinguish
correctable arguments rejected **before dispatch/operation claim** from a dispatched
write with an unknown outcome; allow correction of the former without destroying its
task. Corrections are separately checkpointed as **f26495d** (backend) and **74acb47**
(client). The current focused source gate passed **141 client / 271 backend checks**.
The wider client run retained **623 passes / 2 failures**: one test-only reflection
signature mismatch was corrected; the unchanged Gmail classification timeout and the
corrected cleanup case passed in an isolated six-case baseline/current comparison.
Original failures remain. **This repair is not installed or accepted yet.**
Do not replay an unknown/successful write under a new task, generalize this into blind
retries, or change Gmail, speech, tabs, translation, camera or window management.

## Capability and availability matrix

The pre-binding live catalog reported seven available connector cards, as saved in
`.artifacts/connectors/stabilization-plan-20261027/calendar-retry/live-catalog.json`. Nine connectors
have implemented native adapters. Narrow managed Slack/Discord auth configurations have
now been created/read back and bound to held revision **00038-xeh** at test tag
`rec261027`. The app's private route remains **00036-haj** until the reviewed candidate
handoff. A catalog card, an OAuth connection and real capability acceptance
are distinct. No row below claims fresh provider testing unless stated.

| Connector | Implemented native subset | Current proof / remaining gate |
| --- | --- | --- |
| Gmail | Search/count/read/thread, one requested plain-text send | Existing search/open/follow-up and sender-recovery evidence retained. Sending and every wording/account/error case are not universally certified. |
| Google Calendar | List events; timed or all-day event create, no attendees | Installed4a created and independently read back one approved event; Google Calendar rendered it. New November21 failure/recovery is still under correction. |
| Google Drive | Name search and file metadata | Native adapter exists; arbitrary file-content/binary reading is not implemented. Real granted-scope/provider acceptance still needed. |
| Google Docs | Read an identified document; create a blank document | Adapter exists. This does not imply arbitrary editing or filling a created document. Provider acceptance still needed. |
| Google Forms | Read an identified form; create a blank private form | Adapter exists. Publishing/distributing forms is not implemented. Provider acceptance still needed. |
| YouTube | Connected channel details and public-video search | No uploads, public comments, transcripts or broad analytics. Provider acceptance still needed. |
| GitHub | Existing repository/issue/PR/release/asset/activity reads; issue create | Scope-dependent facets must fail accurately; no code changes, merge or workflow execution. Advanced runtime proof remains separate. |
| Slack | Accessible-message search, channel history, one channel post | Narrow managed-OAuth configuration created and read back; private binding pending. No connected user or real capability acceptance claimed. |
| Discord | Connected identity and server list | Narrow managed-OAuth configuration created and read back; private binding pending. No bot or message sending; no connected-user/runtime acceptance claimed. |
| Google Meet | None | Empty capabilities/ComingSoon=true. Managed OAuth availability is not an execution adapter. Do not enable a misleading card. |
| Reddit | None | Empty capabilities/ComingSoon=true; API/auth host adapters absent. Branding approval alone is insufficient. |
| LinkedIn | None | Empty capabilities/ComingSoon=true; API/auth host adapters absent. Branding approval alone is insufficient. |

`ConnectorRegistry` is the single capability/availability authority. `Available` requires
global enablement, a configured provider family and a non-coming-soon implementation.
The three unimplemented rows need separately scoped capability design, provider handling,
authorization and focused compatibility tests; toggling `ComingSoon` is not a safe fix.

## OAuth and branding

Read-only provider metadata in
`.artifacts/connectors/stabilization-plan-20261027/calendar-retry/provider-availability.json`
reports managed OAuth support for Slack, Discord, Meet, Reddit and LinkedIn. The user
accepts Composio branding. That permits evaluating managed configuration; it does not
grant every scope, connect an account, prove workspace-admin approval or supply a missing
adapter. Defaults advertised by the vendor can be much broader than our implemented
subset. Request only the exact required scopes and inspect the actual consent screen.

`managed-configs-created.json` in the same evidence directory records successful creation
and independent configuration readback under the user's authorization: Slack requests only
`channels:history,groups:history,im:history,mpim:history,search:read,chat:write`; Discord
only `identify,guilds`. Both are managed/enabled with the exact requested scopes verified.
No account connection or OAuth grant was created; existing configurations and public
production were untouched. Private backend binding remains pending at this checkpoint.

Existing Google integrations use custom OAuth configurations. The
[11 October OAuth audit](GOOGLE_OAUTH_PUBLIC_READINESS_2026-10-11.md) identified
`keyboard-wtf-prod` / **Keyboard WTF Production** as the owning Google Cloud project,
with published branding and an External/In-production audience at that time. Those
historical settings are not current console verification or data-access approval.
Record the current owning client and Verification Center status; a project label such
as “Prod” is not proof of Google production approval or of the public backend being ready. Managed
OAuth for another connector does not verify the app's existing custom Google client.
For managed OAuth, identify which party owns consent verification and policy obligations,
then verify the user-facing scopes/branding and application data handling independently.

Required Google release evidence: the correct consent screen/project, verified domains,
published privacy policy, capability-to-scope inventory, least-required scope justifications,
review/demo submission and the applicable restricted-data requirements. The
[20 October submission packet](GOOGLE_OAUTH_SUBMISSION_PACKET_2026-10-20.md) records
owner confirmation that data-access review had not been submitted; this document does not claim a current
Verification Center result. See Google's
[restricted-scope verification guidance](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification).

## Ordered release gates

1. **Finish the narrow Calendar recovery acceptance.** Preserve the original failed
   request/receipts, prove no provider dispatch on invalid arguments, retain task/context
   for a corrected proposal, and keep unknown-write dedup/readback safeguards. Run only
   affected source/package checks and one bounded installed path after freezing a candidate.
2. **Resolve honest capability availability.** Bind the reviewed narrow Slack/Discord
   managed configurations to the isolated private deployment, preserve the previous
   configuration as rollback and verify the actual catalog. The user still
   completes their own OAuth connection/admin approval. Verify status, account identity,
   one permitted read and safe revocation before calling these connectors working.
   Leave unsupported connectors explicitly unavailable.
3. **Freeze and review the release source.** Record source, backend tree, package inventory,
   known limitations and rollback. Source/package tests cannot supersede a failed live
   user result. No full-suite loop is required without a meaningful change.
4. **Sign Windows artifacts.** Fresh checks found both installed4a EXE and its archived
   installer **Authenticode NotSigned**. Obtain the release signing identity, sign the
   actual public payload/installer and verify signatures/timestamp on the final bytes.
5. **Build and inspect the genuine public flavor.** Current acceptance is a private staging
   build. Verify actual public DLL excludes private recording/audit adapters and keys,
   package excludes tests/source/evidence, and production config/URLs are correct. The
   earlier test build-flavor expectation mismatch is already corrected in
   `PublicDiagnosticExclusionTests.cs`; final public-binary proof is still required.
6. **Run isolated public installer lifecycle acceptance.** Clean install, upgrade, repair,
   uninstall, startup, hotkeys, Chrome Helper registration/ownership and rollback must be
   verified without disturbing the private/public profiles. Confirm recovery from a partial
   update and protect settings/authentication; private handoff success is not this proof.
7. **Complete privacy/security release review.** Update the actual public policy to describe
   native provider/Composio data flow, local/backend/provider retention, Personal/Managed
   processing, account disconnection/deletion and support contact. `site/privacy.html`
   currently lacks these connector/Composio disclosures; hosted policy parity was not
   freshly verified. Match claims to observed behavior rather than policy templates.
8. **Verify production service operation.** Actual production account/auth, connector scopes,
   quotas, provider billing, Personal-vs-Managed isolation, recovery and deletion must pass
   with the intended public configuration. Private tag staging acceptance is insufficient.
9. **Publish only after explicit release approval.** Git commit, remote backup/push, public
   GitHub release, backend deployment and website change are separate actions. Reconcile
   signed download/feed/version/hash, release notes, beta limitations and rollback/support
   procedure before distributing. This document performs none of them.

## Security findings and limits

Confirmed release risks are the unsigned artifacts, incomplete connector privacy disclosure,
unfinished public-flavor/lifecycle acceptance and unverified OAuth/production readiness.
No new exploitable vulnerability was established by this narrow review. No CodeRabbit
security certification or independent penetration-test result is claimed.

Existing controls inspected in source include authenticated connector calls (public catalog
contains metadata only), account/session binding, provider destination/method allowlists,
bounded response/deadline handling, exact write readback, unknown-write non-replay and
loopback settings action-token/origin checks. Preserve them. Review their final public
implementation and actual deployment rather than treating their presence as certification.

Prior source secret scan and NuGet advisory results are retained evidence from their stated
date/scope, not a fresh all-dependency/native-library assessment. Recheck the final frozen
public artifact and production configuration before release. Do not put credentials,
provider raw records or private investigation evidence into GitHub/package payloads.

## Safe efficiency priorities

- Measure end-to-end user delay separately from local routing, provider I/O, model response,
  speech startup and playback. Optimize only a measured dominant path.
- Keep ordinary commands free of extra connector discovery/reasoning and provider calls.
  Reuse verified session-scoped context and exact identities; stop when the requested
  outcome is already available. Do not keep completed execution authority alive indefinitely.
- Correct pre-dispatch proposals locally using structured receipts. Keep transient reads
  bounded/cancellation-aware and writes reconciliation-first; do not add global retries.
- Preserve existing app discovery cache; cold discovery and machine pressure need separate
  evidence before changing process/model lifetime or adding background warmers.
- Keep tests: they are not shipped in the installed24-file package. Delete neither coverage
  nor rollback to make installed Jarvis faster. Developer artifacts may be archived separately
  only after exact inventory and authorization.
- Popup-behind-Photos and cross-desktop following remain separate unresolved issues. Do not
  add focus stealing, TopMost polling or camera changes to address an unproven cause.
- Do not trim prompts, switch models, alter VAD/playback/goodbye or touch proven tab safety
  as part of connector configuration or release preparation.

Update this document with actual pending-fix/configuration receipts before final delivery.
