namespace KeyboardWtf.Connectors;

// Shared by the normalized server registry and the bounded desktop reader.
// Evidence role is not authorization: task/account ownership and the server
// capability allowlist remain authoritative. Unknown capabilities fail closed.
public static class ConnectorEvidenceTraits
{
    public static string For(string capabilityId) => capabilityId switch
    {
        "mail_search" or "drive_search" or "youtube_search" or "slack_search" => "search",
        "mail_read" or "mail_thread" or "docs_read" or "forms_read" or "github_issues" or "slack_history" => "content",
        "calendar_list" or "drive_read" or "youtube_channel" or "github_repos" or "github_repo" or "github_pulls"
            or "github_releases" or "github_release_assets" or "github_activity" or "discord_self" or "discord_guilds" => "metadata",
        _ => "unknown"
    };
}
