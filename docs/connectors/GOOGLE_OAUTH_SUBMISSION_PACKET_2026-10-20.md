# Google connector submission packet — 20 October 2026

**Preparation only. No OAuth settings, grants, public policy, backend, installed application or credentials were changed.** The owner confirmed on 20 October that data-access verification has not been submitted. Private application source `cdd33d3ec93be6ea67cd9466f7c5bb2e7ffa0cb1` stays frozen.

This packet updates the [11 October audit](GOOGLE_OAUTH_PUBLIC_READINESS_2026-10-11.md). It gives the owner usable submission material and records what must be resolved before submitting it. Scope proposals below are supported by current endpoint documentation, but have **not** been validated through a new restricted-scope Composio consent. They are not an approved replacement for the working configuration.

## Direct links and current evidence

- [Project overview](https://console.cloud.google.com/auth/overview?project=keyboard-wtf-prod), [Branding](https://console.cloud.google.com/auth/branding?project=keyboard-wtf-prod), [Data Access](https://console.cloud.google.com/auth/scopes?project=keyboard-wtf-prod), [Verification Center](https://console.cloud.google.com/auth/verification?project=keyboard-wtf-prod).
- Project: **Keyboard WTF Production**, ID `keyboard-wtf-prod`.
- On 11 October, branding was published/verified, audience was External/In production, and Data Access had no declared scopes. This is historical console evidence; current browser access is unavailable. Owner confirmation on 20 October establishes that no review has been submitted, not that every setting is unchanged.
- The published privacy URL was freshly read at **13:43:41 UTC on 20 October**. `https://keyboardwtf.com/privacy.html` redirected to `https://keyboardwtf.com/privacy`, HTTP 200. The text contains no connector or Composio disclosure. Its Limited Use statement concerns the Chrome extension, not Google connector data.
- Published-policy SHA-256: `fd6b214d291f7c539c4890ab66b10b76d07df729442f51866700fb3df6f56c8c`.
- Privacy evidence: `.artifacts/connectors/voice-followup-recurrence-20261018/context-route-final-delivery-20261020/public-readiness/public-privacy-readback.json`.
- Current Google capability and request paths were read from `ConnectorRegistry.cs`, `ConnectorOperations.cs`, and `ConnectorConnectionService.cs` at frozen source. The changes below are documentation, not a new implementation.

## Scope reconciliation

Use `G = https://www.googleapis.com/auth/` in this table. Preserve all implemented read and explicitly requested write operations. The final declared set must match the scopes actually requested by the chosen public OAuth configuration. Declaring narrower scopes in Google Console alone does not narrow Composio's authorization request.

| Feature and actual endpoint | Narrow candidate for isolated validation | Submission rationale / constraint |
| --- | --- | --- |
| Gmail search/count, message and thread reads: `users.messages.list/get`, `users.threads.get` | `Ggmail.readonly` | Read user-requested email content to find relevant conversations, summarize them and answer follow-ups. Header-only access cannot provide the requested message content. This is a restricted scope. |
| Explicit email/reply send: `users.messages.send`, followed by independent sent-message readback | `Ggmail.send` plus the read scope above | Send one user-requested plain-text message to an exact recipient and verify the resulting message. Current code does not implement permanent deletion, mailbox editing or draft management. Full `https://mail.google.com/` is not justified by those absent features. |
| Primary-calendar event list/create/readback | `Gcalendar.events.owned` is the first candidate; `Gcalendar.events` is the documented broader event-only alternative if a demonstrated ownership constraint requires it | The implementation targets `primary`, with no calendar-list/ACL management and no automatically supplied attendees. Validate owned-primary reads, creation and readback before choosing the final scope. |
| Drive file-name search and metadata read: `files.list/get` with explicit metadata fields | `Gdrive.metadata.readonly` | Discover existing accessible files by name and retrieve their verified links/metadata. `drive.file` alone would not preserve global discovery. This does not justify arbitrary file-content downloads or writes. Restricted access remains involved. |
| Read existing Docs and create a requested blank Doc: `documents.get/create` and readback | `Gdocuments.readonly` plus `Gdrive.file` is a narrower combination to validate | Read the identified existing document, while creation/readback can use app-created-file access. Google documents both endpoint permissions. Do not assert this combination already works through the vendor. Keep full `documents` only if a concrete supported operation cannot use the narrower combination and that necessity can be justified. |
| Read Forms, create a blank unpublished Form, verify/restore its private publication state | `Gforms.body` | Form definition access and explicit blank creation, including `forms.setPublishSettings` when needed to preserve the requested private result. Current capabilities do not read response submissions or require full Drive access. |
| YouTube own-channel metadata/statistics and public-video search | `Gyoutube.readonly` | Read channel identity/statistics and search videos. No upload, partner/audit, memberships, comments or edit/delete feature is exposed. |
| Account identity for Google, Forms and YouTube | Reconcile `Guserinfo.email` and any actually required profile/OpenID scopes with the vendor | `ReadLabelAsync` calls `/oauth2/v3/userinfo` for all three families. Preserve verified account labels and exact account selection. Current broad grants on a shared client can conceal missing permissions in a fresh family-specific consent; do not certify identity from an already broadly authorized account alone. |
| Exact opening, retained follow-ups and spoken summaries | No additional scope just for these actions | Reuse authorized resource identity/content; fetch new content with a fresh authorized read when needed. Opening an observed link does not justify extra account permissions. |

The 11 October configured lists remain historical evidence, not a list to submit unchanged. Contacts, Sheets, Slides and Tasks; Forms response access; and YouTube partner/membership/upload/edit scopes lacked a matching implemented feature in this source. Do not justify them with future functionality.

Official endpoint checks on 20 October: [Gmail scopes](https://developers.google.com/workspace/gmail/api/auth/scopes), [Calendar insert](https://developers.google.com/workspace/calendar/api/v3/reference/events/insert), [Drive list](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/list), [Docs get](https://developers.google.com/workspace/docs/api/reference/rest/v1/documents/get), [Docs create](https://developers.google.com/workspace/docs/api/reference/rest/v1/documents/create), [Forms create](https://developers.google.com/workspace/forms/api/reference/rest/v1/forms/create), [Forms publication settings](https://developers.google.com/workspace/forms/api/reference/rest/v1/forms/setPublishSettings), [YouTube authorization](https://developers.google.com/youtube/data/guides/auth/server-side-web-apps).

The current candidate scopes need only a bounded **new-consent compatibility check**, not another speech/tab regression campaign: identity in each Google family, one representative read, explicitly authorized write/readback where included, and revocation. Do not alter the existing working grants. The shared production Google client means that a different Composio environment alone is not proof of OAuth isolation. A review configuration/account and actual granted scope readback are prerequisites to making these proposals final.

## Draft submission description

Use this after reconciling the scope set, public disclosures and demonstration:

> keyboard.wtf is a Windows productivity assistant. Its optional Google integrations let a signed-in user find and read email, summarize an identified conversation, answer related questions, locate primary-calendar events, find Drive files, read an identified document or form, and read YouTube channel information or search public videos. Supported writes occur only for an explicitly requested email, event, blank document or blank private form. Native provider requests retrieve the data. Account ownership and resource references are validated by the application. Relevant content is passed to the configured AI service to interpret requests and produce user-facing answers. Composio handles connector authorization and provider access. Our application backend receives the results and keeps operation/account metadata for reliable execution and cleanup. We disclose these transfers and the applicable retention/deletion controls in our privacy policy.

The per-feature rationales in the table can be adapted into the scope-justification fields once the final scopes are actually configured. Do not claim completed verification, a security assessment, full-mailbox synchronization, attachment-content reading, arbitrary document editing, or other unsupported functionality.

## Draft connector privacy addition

This is unpublished review copy, not a statement that all supplier/retention requirements are already satisfied:

> Google connections are optional. If you connect an account, keyboard.wtf uses the permissions shown by Google to perform supported tasks you request. Depending on the connected service and your request, this includes account identity, email headers and message text, calendar events, file metadata, identified document or form content, and YouTube channel information. We use this information to retrieve the requested material, answer questions, reopen the identified resource and perform supported explicitly requested writes.
>
> Composio handles connector authorization and requests to the connected provider. Task-relevant results pass through keyboard.wtf's authenticated backend and may be sent to the AI provider selected for the session. Personal API mode and Managed mode determine the AI route and billing ownership; Personal mode does not mean that connector retrieval bypasses Composio or the backend.
>
> The application retains bounded result content and verified references for follow-up interaction. Backend connector records contain account labels/provider references and operation state, resource identifiers, timing and error metadata. The inspected connector operation journal does not store email or document bodies. Supplier processing and any separate diagnostic retention must be described according to their actual terms.
>
> You can disconnect a connector or remove its permission in your Google Account. Account deletion includes connector cleanup. If provider cleanup cannot be confirmed, the application reports that cleanup is pending instead of asserting completion. Provider-side revocation prevents new authorized access; it does not retract information already displayed or sent to an AI service.

Before publishing, resolve these specific blanks rather than making generic promises:

- State the applicable Composio and Personal/Managed AI data-processing, retention and training terms. No claim that all modes exclude provider training has been established by this packet.
- Confirm deployed retention/TTL policies, backup handling and vendor deletion behavior. A code field named `deleteAfter` is not evidence of actual removal.
- Finalize the Google Workspace Limited Use statement against actual practices and supplier terms; the existing Chrome Web Store statement is a different disclosure.
- Place a clear connector-specific disclosure before Google consent, not solely in a long privacy page. Preserve the existing account/connection UI unless a verified disclosure gap requires a narrow addition.
- Confirm support and deletion contact details. The currently published policy names `support@keyboardwtf.com`.

[Google Workspace policy](https://developers.google.com/workspace/workspace-api-user-data-developer-policy) is the reference for disclosures and allowed data use. Restricted data currently passes through servers; do not claim the local-only assessment exception. Google determines the applicable review and [CASA assessment requirements](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification).

## Demonstration storyboard

Record in English using a prepared demonstration account and non-private fixtures. Upload as **Unlisted** on YouTube. The owner's existing recordings verify private interactions, but do not demonstrate the complete Google consent flow or every requested scope.

| Segment | What must be visible |
| --- | --- |
| Product and consent | App identity/version, relevant disclosure, Google grant flow, displayed app name, OAuth client ID and actual requested permissions. |
| Gmail read | Find a fixture message by sender/topic; read and summarize it; open its exact conversation; answer a follow-up after completion. Show the actual browser and spoken outcome. |
| Included writes | Demonstrate only the implemented, included writes with a specified disposable target and independently visible readback. A new send requires concrete recipient/content authorization; prior single-send approvals are not reusable. |
| Other requested scopes | Calendar read; existing Drive discovery and exact link; identified Docs/Form read; blank creation if included; YouTube own-channel read/search. Do not use fabricated output or unsupported features to justify permissions. |
| User control | Demonstrate disconnect and the available deletion controls using the disposable review account. State any pending-cleanup behavior truthfully. |

After the scope, policy and demonstration items are ready: Data Access → add the matching scopes → Verification Center → justifications and video → review → submit. Keep the project contacts reachable for reviewer questions and assessment instructions. Google approval is external and cannot be replaced by private-account success. See [Google's submission procedure](https://support.google.com/cloud/answer/13461325).

## Exact next actions and preservation boundary

1. Owner/operator reconciles the final public scope configuration using the table and a properly isolated consent check. Existing private grants, Firebase client and registered callbacks remain protected.
2. Finalize supplier/retention facts and publish the reviewed disclosure through the normal site release process.
3. Capture/upload the demonstration and submit the review. No review URL, submission receipt or assessment approval exists yet.
4. Complete the separate [production release gates](PUBLIC_RELEASE_READINESS_2026-10-20.md). Production backend promotion and public-flavor packaging are not accomplished by OAuth submission.

No new source test, model request, provider content read/write, install, backend deploy or OAuth mutation was performed for this packet. Completed private regression evidence is reused.
