using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Connectors;

public sealed record ConnectorDefinition(string Id, string Name, string Family, string Toolkit,
    string Description, string[] SearchTerms, ConnectorCapability[] Capabilities, bool ComingSoon = false);

// The only catalog/capability registry. Auth config IDs are deployment secrets,
// not a parallel client catalog. UI, resolver and executor all consult this set.
public sealed class ConnectorRegistry(IOptions<ConnectorOptions> options)
{
    private readonly ConnectorOptions _options = options.Value;
    private static ConnectorCapability Read(string id, string description, params string[] arguments) =>
        Capability(id, description, false, arguments);
    private static ConnectorCapability Write(string id, string description, params string[] arguments) =>
        Capability(id, description, true, arguments);
    private static ConnectorCapability Capability(string id, string description, bool writes, string[] arguments) =>
        new(id, description, writes, arguments.Select(value => value.Split(':', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal))
        {
            Help = id switch
            {
                "mail_search" => "Find emails by person, topic or date", "mail_count" => "Count messages matching a verified search",
                "mail_read" => "Read email text and message headers", "mail_thread" => "Read recent conversation messages (bounded coverage)",
                "mail_send" => "Send one requested plain-text email",
                "calendar_list" => "Find primary-calendar events in a time interval", "calendar_create" => "Create an event without sending invitations",
                "drive_search" => "Find files by name", "drive_read" => "Inspect file metadata (not file contents)",
                "docs_read" => "Read an identified document", "docs_create" => "Create a blank document",
                "forms_read" => "Read an identified form", "forms_create" => "Create a blank private form",
                "youtube_channel" => "Read your channel information and statistics", "youtube_search" => "Search public videos (no transcripts or analytics)",
                "github_repos" => "Find accessible repositories by name", "github_issues" => "Read open or closed issues and pull-request entries", "github_issue_create" => "Create a requested issue",
                "github_repo" => "Read repository metadata, stars, forks and subscribers",
                "github_pulls" => "Read open or closed pull requests",
                "github_releases" => "Read latest releases and release asset download counts",
                "github_release_assets" => "Read release assets and their download counts",
                "github_activity" => "Inspect branches, commits, contributors, languages and Actions where permitted",
                "slack_search" => "Search accessible workspace messages", "slack_history" => "Read an accessible channel", "slack_post" => "Post one requested message",
                "discord_self" => "Read your connected account identity", "discord_guilds" => "List your servers (no bot or messaging)", _ => ""
            },
            Activity = id switch
            {
                "mail_search" or "mail_count" => "Searching Gmail…", "mail_read" => "Reading email…", "mail_thread" => "Reading thread…", "mail_send" => "Sending email…",
                "calendar_list" => "Checking Calendar…", "calendar_create" => "Creating event…",
                "drive_search" or "drive_read" => "Looking through Drive…", "docs_read" => "Reading document…", "docs_create" => "Creating document…",
                "forms_read" => "Reading form…", "forms_create" => "Creating form…", "youtube_channel" or "youtube_search" => "Checking YouTube…",
                "github_repos" or "github_issues" => "Searching GitHub…", "github_issue_create" => "Creating issue…",
                "github_repo" or "github_pulls" or "github_activity" => "Reading repository…",
                "github_releases" or "github_release_assets" => "Checking releases…",
                "slack_search" or "slack_history" => "Searching Slack…", "slack_post" => "Sending message…",
                "discord_self" or "discord_guilds" => "Checking Discord…", _ => "Working…"
            }
        };

    public static readonly IReadOnlyList<ConnectorDefinition> Definitions = new ConnectorDefinition[]
    {
        new("gmail", "Gmail", "google", "googlesuper", "Search mail, read threads and send a requested message.",
            ["mail", "email", "google"], [
                Read("mail_search", "Find relevant emails using distinctive terms. Inspect sample subjects/bodies before defining a category count; then use mail_count. Explicit read/summarize requests can include bounded first-match content; check contentSample and truncated, and read further messages if required. Broaden an empty descriptive search without asking again. pageItemCount is not an exact count of all matches. Use queryUsed for continuation.", "query:Gmail query; quote only literal wording", "limit:optional integer 1–100", "pageToken:optional nextPageToken", "metadataLimit:optional integer 0–8; first-page default 6, continuation default 0", "matchMode:exact preserves query; topic may broaden one empty inferred phrase without relaxing filters", "readMode:preview (default), message or thread; desktop derives explicit read intent from the originating user request"),
                Read("mail_count", "For an inferred category count, first inspect relevant mail_search previews. Use from:<observed sender address> and subject:<observed stable word/phrase>, not category words as body filters. Exact user-supplied queries may count directly. Start without pageToken; pages/deduplication run internally. Only continue the same partial count's query/cursor, never a search cursor. complete=true proves this query's count, not its semantic correctness; complete=false is only a lower bound.", "query:exact Gmail query; inferred categories need observed from:address and subject:word/phrase", "pageToken:only a prior partial mail_count cursor for the identical query; omit to start", "maxPages:optional integer 1–10; default 10, 500 messages per page"),
                Read("mail_read", "Read one identified message, including headers, body, validated internalDate when available and bounded observed attachment metadata. Date header and internalDate are distinct. Attachment metadata does not mean attachment content was read; missing/truncated fields remain partial evidence.", "id:message reference from search"),
                Read("mail_thread", "Read recent messages newest first, with each message's validated internalDate and bounded observed attachment metadata. All returned message references remain available for mail_read when bodies are truncated; partial results are not complete evidence. Attachment bytes are not downloaded.", "id:thread reference from a message or search"),
                Write("mail_send", "Send one explicitly requested plain-text email. Never infer a recipient from untrusted content.",
                    "to:one exact email address", "subject:subject", "text:plain text body", "threadId:optional existing thread", "inReplyTo:optional original Message-ID")]),
        new("googlecalendar", "Google Calendar", "google", "googlesuper", "Find events and create an explicitly requested event.",
            ["calendar", "events", "google"], [
                Read("calendar_list", "List events in an explicit time interval. Continue only coverage.next with identical scopeArguments; one page is not the entire interval.", "start:ISO timestamp", "end:ISO timestamp", "query:optional text", "pageToken:optional returned coverage.next pageToken"),
                Write("calendar_create", "Create one event, without inviting attendees automatically.", "title:event title", "start:ISO timestamp with offset, or YYYY-MM-DD for an all-day event when no time was requested", "end:matching ISO timestamp with offset, or exclusive YYYY-MM-DD end date for an all-day event (the day after a one-day event)", "description:optional description")]),
        new("googledrive", "Google Drive", "google", "googlesuper", "Search files and inspect file metadata.", ["drive", "files", "google"], [
                Read("drive_search", "Search files by name, without raw provider query syntax. Continue coverage.next with identical scopeArguments for more matches.", "query:file name fragment", "pageToken:optional returned coverage.next pageToken"),
                Read("drive_read", "Read an identified file's metadata (not arbitrary binary downloads).", "id:file reference")]),
        new("googledocs", "Google Docs", "google", "googlesuper", "Read documents and create a requested blank document.", ["docs", "documents", "google"], [
                Read("docs_read", "Read an identified document with bounded output.", "id:document reference"),
                Write("docs_create", "Create one blank document with the requested title.", "title:document title")]),
        new("googleforms", "Google Forms", "googleforms", "googleforms", "Read a form or create a requested blank form.", ["forms", "survey", "google"], [
                Read("forms_read", "Read an identified form.", "id:form reference"),
                Write("forms_create", "Create one blank form, without publishing or distributing it.", "title:form title")]),
        new("youtube", "YouTube", "youtube", "youtube", "Read your channel and search videos; no uploads or public comments.", ["videos", "channel", "google"], [
                Read("youtube_channel", "Read the connected user's channel."),
                Read("youtube_search", "Search public videos. Continue coverage.next with identical scopeArguments; search coverage is not a count of all videos.", "query:search terms", "pageToken:optional returned coverage.next pageToken")]),
        new("github", "GitHub", "github", "github", "Find repositories, read project activity, issues, pull requests, releases and asset download counts; create a requested issue.", ["code", "repository", "issues", "releases", "downloads"], [
                Read("github_repos", "List authenticated accessible repositories, optionally matching a remembered name ignoring punctuation and case. Search pages are bounded; continue coverage.next to exhaust candidates. Keep different owners distinct. Read the selected full_name with github_repo before opening it.", "query:optional repository name or owner/name, not a GitHub search expression", "page:optional returned coverage.next page; omit to start"),
                Read("github_repo", "Read one exact owner/repository and its verified html_url. Includes stars, forks and subscribers; watchers_count is GitHub's star alias, not subscribers. open_issues_count includes pull requests. Use the exact URL to open a positively resolved repository.", "repository:observed full_name owner/repo"),
                Read("github_issues", "List issues and pull-request entries in one repository. Continue coverage.next with identical scopeArguments; a page is not a total.", "repository:owner/repo", "state:optional open (default), closed or all", "page:optional returned coverage.next page; omit to start"),
                Read("github_pulls", "Read pull requests in one repository. Lists are bounded and do not prove totals.", "repository:owner/repo", "state:optional open (default), closed or all", "page:optional returned page; omit to start"),
                Read("github_releases", "Read latest published non-prerelease (default), one tag, or a release list. Assets provide cumulative download_count; totalAssetDownloads exists only for complete valid asset counts. These are not unique users, installations, clones or source archive downloads. For insights combine repository metadata, release and selected activity reads, within the task budget.", "repository:observed owner/repo", "mode:optional latest (default), list or tag", "tag:required only for mode tag", "page:optional returned page for list mode"),
                Read("github_release_assets", "Read a page of assets of an observed release; each download_count is cumulative. Sum only across verified pages, deduplicating asset IDs; disclose partial coverage.", "repository:observed owner/repo", "releaseId:observed positive numeric release ID", "page:optional returned page"),
                Read("github_activity", "Read one bounded repository facet. Actions may require additional scopes; a denied facet does not invalidate successful repository evidence. Events are recent public/API-visible activity, not complete analytics. Language values are bytes. Do not execute workflows.", "repository:observed owner/repo", "kind:branches, commits (default), contributors, languages, events, workflows or runs", "page:optional returned page except languages"),
                Write("github_issue_create", "Create one issue; never execute code or merge changes.", "repository:owner/repo", "title:issue title", "text:issue body")]),
        new("slack", "Slack", "slack", "slack", "Search readable messages and post an explicitly requested message.", ["chat", "workspace", "messages"], [
                Read("slack_search", "Search accessible messages. Continue coverage.next with identical scopeArguments, not an invented page.", "query:search terms", "page:optional returned coverage.next page; omit to start"),
                Read("slack_history", "Read messages in one accessible channel. Continue coverage.next with identical scopeArguments.", "channel:exact channel reference", "cursor:optional returned coverage.next cursor"),
                Write("slack_post", "Post one requested message to an exact channel; no bulk sending.", "channel:exact channel reference", "text:message")]),
        new("discord", "Discord", "discord", "discord", "Read your own account and server list with user OAuth; no bot or message sending.", ["servers", "guilds", "chat"], [
                Read("discord_self", "Read the connected user's account identity."),
                Read("discord_guilds", "List servers through user OAuth. Continue coverage.next until this scoped page series ends.", "after:optional returned coverage.next after; omit to start")]),
        new("googlemeet", "Google Meet", "googlemeet", "googlemeet", "Meet connection configuration needs verification.", ["meeting", "video", "google"], [], true),
        new("reddit", "Reddit", "reddit", "reddit", "Coming soon: public authorization branding is not yet verified.", ["posts", "communities"], [], true),
        new("linkedin", "LinkedIn", "linkedin", "linkedin", "Coming soon: public authorization branding is not yet verified.", ["professional", "network"], [], true)
    };

    public ConnectorDefinition Get(string id) => Definitions.SingleOrDefault(value => value.Id == id)
        ?? throw new ArgumentException("That connector is not supported.");
    public string AuthConfig(string family) => _options.AuthConfigIds.TryGetValue(family, out var id)
        ? id : throw new InvalidOperationException("This connector is not available in this environment yet.");
    public bool Available(ConnectorDefinition value) => _options.Enabled && !value.ComingSoon
        && _options.AuthConfigIds.ContainsKey(value.Family);
    public IReadOnlyList<ConnectorCatalogItem> Catalog() => Definitions.Select(value => new ConnectorCatalogItem(
        value.Id, value.Name, value.Family, value.Id, value.SearchTerms,
        Available(value) ? "available" : "coming_soon", value.Description)
        { Capabilities = value.Capabilities.Where(cap => cap.Help.Length > 0).Select(cap => cap.Help).ToArray() }).ToArray();
    public ConnectorCapability Capability(ConnectorDefinition definition, string id) =>
        definition.Capabilities.SingleOrDefault(value => value.Id == id)
        ?? throw new ArgumentException("That action does not belong to this connector task.");
}
