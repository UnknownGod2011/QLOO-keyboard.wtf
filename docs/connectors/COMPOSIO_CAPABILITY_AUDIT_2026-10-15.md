# Connector capability audit — 15 October 2026

Read-only audit of the current source on dirty `4a2b231`. This is a capability/gap assessment, not installed acceptance. No provider account, OAuth scope, backend deployment or registry was changed by this audit.

## Actual integration

The backend uses authenticated Composio REST v3.1 proxy calls, not a Composio SDK/agent runtime. `ConnectorRegistry` defines 28 normalized capabilities across nine implemented connectors and remains the source for task tools and descriptions. Provider-specific schemas are task scoped; permanent Live tools remain the three broker tools.

| Provider | Current normalized surface |
|---|---|
| Gmail | Search, count, message read, bounded thread read, plain-text send/reply |
| Calendar | Primary-calendar interval list; create without invitations |
| Drive | Name search; metadata |
| Docs | Read identified document; create blank document |
| Forms | Read identified form; create blank private form |
| YouTube | Channel information/statistics; public video search |
| GitHub | Repositories, metadata, issues, PRs, releases, assets/download counts, activity facets, issue creation |
| Slack | Search, history, post |
| Discord | Identity, guild list |

Meet, Reddit and LinkedIn are coming-soon entries with no operations. Historical 11 October sanitized connection evidence covered Google, Forms, YouTube and GitHub; Slack/Discord were unavailable. That receipt does not establish current account state.

## Concrete capability gaps

1. Gmail retains attachment filename/MIME/ID/size metadata, but has no attachment-content operation. Large message text stored behind an attachment ID also remains unread and marked truncated. The official [Gmail attachment API](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.messages.attachments/get) supplies JSON base64url under existing mail read/modify scopes. A normalized operation needs an observed parent-message/attachment reference, same account, content permission, bounded bytes and truthful extraction status.
2. Drive cannot download/export content or obtain a CV attachment through current metadata-only operations. The official [Drive download/export guide](https://developers.google.com/workspace/drive/api/guides/manage-downloads) distinguishes blob `alt=media`, Workspace export and `capabilities.canDownload`. New content operations must retain those distinctions and verified file references.
3. `ComposioConnectorProvider.RequestAsync` currently returns `payload.data` only. The current [v3.1 proxy contract](https://docs.composio.dev/reference/api-reference/tools/postToolsExecuteProxy) returns binary results through sibling `binary_data` (URL, content type, size, expiry). Adding a binary endpoint without typed adapter handling would lose its result. Signed binary URLs must not become model-authorized navigation destinations.
4. Backend tasks bind one provider/account. Separate tasks can participate in one human request, but the bounded reader receives one task's capabilities. Later15October corrections select the latest retained object matching an explicit provider/object type for both opening and details. Causal cross-provider fixtures and `cross-provider-native-details-20261015.json` prove Gmail→existing Docs→explicit email-details recovery with a fresh same-account thread task and no repeated mailbox search. Bare “it” still means the latest object. This does not establish autonomous multi-provider planning, Drive attachment retrieval or send-with-attachment acceptance.
5. `mail_send` builds plain-text MIME. Attachment sending, draft management, labels/archive and attachment extraction are not currently exposed. Existing consumed write fences remain in force. Do not claim the expanded user acceptance checklist passes for these absent operations.

## Discovery assessment and next steps

Composio currently supports [tool-router session discovery/execution](https://docs.composio.dev/reference/api-reference/tool-router) and [account/tool filtering](https://docs.composio.dev/kb/guide/mcp-tool-router-sessions). A wholesale ownership migration is unnecessary for the demonstrated local receipt and cache defects. Preserve existing owners and normalized contracts; add vetted useful capabilities rather than exposing arbitrary vendor tools or permanent provider schemas.

After the current Daisy ownership/detail regression passes: observed Gmail attachment retrieval, bounded Drive content/export, read-only Gmail+Drive acceptance with provider-qualified retained references, then separately authorized attachment-send verification. Every existing configured provider still needs meaningful product-path coverage. The older native smoke matrix and current Gmail/GitHub receipts do not prove attachments or cross-provider production readiness.
