# Exact outstanding staging configuration

Verified 8 October 2026, through both supplied project keys, full pagination and
direct configuration reads. No production config was edited. This is provider
configuration, not an end-user token request.

## Findings

- Production has the 12 supplied enabled configs. Staging has four custom configs:
  Google Super `ac_bHRNzkSvozqr`, Forms `ac_j0VY2AweQIGf`, YouTube
  `ac_NX7vxftp9d9c`, GitHub `ac_W9LMs9a8Vl76`.
- No staging Slack/Discord config exists, including disabled entries or alternate
  names. Production Slack is `ac_Zc-2QYnJjs4h`; Discord is `ac_OfSb8s-ajUKZ`.
- Their client IDs, callback and scopes are readable; their client secrets are
  **masked**, not recoverable credentials. Directly reading either production ID
  with the staging project key returns 404.
- The current API/SDK documents creation with credentials, not a cross-project
  clone/import reference. This establishes no supported shortcut in the inspected
  interfaces, not a claim about undocumented internal provider features.
- Reusing the developer app is possible with an actual stored client secret and
  separate staging auth config. Do not reference a production config from staging.

## SLACK

**Exact missing field:** `credentials.client_secret` for a custom **staging** auth
config. Client ID, callback and scopes already exist in production metadata.

1. In Composio, select the project belonging to the supplied **STAGING** key—the
   project containing the four IDs above. Open **Platform → Auth Configs** and
   search Slack again. If an entry now exists, inspect it; do not create a duplicate.
2. Otherwise choose **Create Auth Config → Slack → OAuth2 → Authentication
   management → Manage authentication with custom credentials** (or **Use your
   own developer credentials**).
3. Name it `keyboard.wtf staging Slack`. Use the keyboard.wtf Slack app's client ID
   and enter its actual stored client secret directly in this dashboard. The source
   app is visible under [Slack apps](https://api.slack.com/apps) → keyboard.wtf →
   **Basic Information → App Credentials**. Do not enter an access/refresh token.
4. Preserve the existing registered redirect URI exactly. Current production uses
   the supported legacy callback `https://backend.composio.dev/api/v1/auth-apps/add`;
   do not change the working production callback to match a newer example.
5. Keep the current scopes for the initial parity test; no production scope trimming
   is authorized by a demonstrated failure yet. There are **47 distinct scopes**
   (94 entries across the two fields), no `admin.*`/`auditlogs:read`.
6. Enable the new config. Its newly assigned ID must attach only to staging's
   `Connectors:AuthConfigIds:slack` after independent inspection. No staging Slack
   ID exists yet; one is not invented in this document.

External-workspace gate: Slack app **Manage Distribution → Share Your App with
Other Workspaces**; complete the checklist and, if appropriate, activate public
distribution. Then test an actual external workspace. Some workplaces require
admin approval; commercial Marketplace eligibility remains a separate gate.

## DISCORD

**Exact missing field:** `credentials.client_secret` for a custom **staging** auth
config. Client ID and existing callback are readable.

1. In the same staging project, **Platform → Auth Configs**, search Discord again.
   Create only if still absent: **Create Auth Config → Discord → OAuth2 → custom
   developer credentials**; name it `keyboard.wtf staging Discord`.
2. Use the keyboard.wtf app from [Discord applications](https://discord.com/developers/applications)
   → keyboard.wtf → **OAuth2**, with its client ID and the actual stored client
   secret. Enter the secret in Composio, not chat. Do not reset an in-use production
   secret to obtain it; an isolated developer app is the alternative if unavailable.
3. Match the already registered callback exactly; preserve the production config.
   Use user authorization, not a Bot setup.
4. The initial registry exposes only own-account and server-list reads. Staging
   should request **`identify guilds`** for this slice, then validate that both
   operations and account labels work. Do not blindly carry over production's
   eight scopes: `messages.read` concerns local RPC, `guilds.join` is unnecessary
   here, and `openid` is absent from the inspected official scope table. This is
   a staging recommendation; production scopes have **not** been changed and
   actual consent with a narrowed config remains unverified.
5. Enable it and attach its verified new ID only to staging's
   `Connectors:AuthConfigIds:discord`. No staging Discord ID exists yet.

## Other consent/configuration gates

- Existing Google/GitHub work is not blocked by these missing fields. Real Google
  staging consent is now independently verified ACTIVE/UID-bound/adopted, with
  successful Gmail/Calendar/Drive provider reads. Dashboard **Connect Account**
  alone would still be a Playground identity, not application-user proof.
- The supplied Google OAuth client belongs to `keyboard-wtf-prod`. Required
  Gmail/Calendar/Drive/Docs/Forms/YouTube APIs were disabled there and enabled
  after a real read produced SERVICE_DISABLED. No consent scope/client changes.
  This does not resolve Google's unverified sensitive/restricted scope warning;
  public Google approval is still required, independently of end-user email.
- Meet was absent in both projects; enable only after its actual project/ID is
  located and reconciled. Reddit/LinkedIn stay Coming soon in production without
  customer-branding clearance.
- Configuration alone is not PASS for reads, confirmed writes, disconnect/reconnect,
  multi-account, restart or cross-device acceptance.

## Primary references

- [Project-scoped configs and dashboard navigation](https://docs.composio.dev/kb/guide/dashboard-auth-configs-navigation)
- [Custom credentials and supported legacy callback](https://docs.composio.dev/docs/auth-configuration/custom-auth-configs)
- [Slack external distribution](https://docs.slack.dev/app-management/distribution/)
- [Discord OAuth scopes](https://docs.discord.com/developers/topics/oauth2)

Non-secret independent discovery receipt:
`.artifacts/connectors/hosted/auth-config-discovery.json` (ignored development artifact).
