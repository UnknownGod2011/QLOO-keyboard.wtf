namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;

// Deterministic presentation of normalized, verified API facts when synthesis
// is unavailable. It makes no analytics, completeness or target-selection claim.
internal static class ConnectorGithubFacts
{
    internal static string Format(IEnumerable<(string Capability, JsonElement Receipt)> evidence)
    {
        var lines = new List<string>();
        foreach (var (capability, receipt) in evidence)
        {
            if (capability is not ("github_repo" or "github_releases")
                || receipt.ValueKind != JsonValueKind.Object
                || !receipt.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
                || !receipt.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) continue;
            string Text(string field) => data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "")[..Math.Min(value.GetString()?.Length ?? 0, 220)] : "";
            if (capability == "github_repo")
            {
                lines.Add("Repository: " + Text("full_name") + ". " + Text("description"));
                foreach (var field in new[] { "stargazers_count", "forks_count", "subscribers_count", "open_issues_count" })
                    if (data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0)
                        lines.Add(field + ": " + count + (field == "open_issues_count" ? " (includes pull requests)." : "."));
                lines.Add("Last repository push: " + Text("pushed_at") + "; default branch: " + Text("default_branch") + ".");
            }
            else
            {
                lines.Add("Release: " + Text("tag_name") + "; published: " + Text("published_at") + ".");
                if (data.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    foreach (var asset in assets.EnumerateArray().Take(8))
                        if (asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                            && asset.TryGetProperty("download_count", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0)
                            lines.Add((name.GetString() ?? "")[..Math.Min(name.GetString()?.Length ?? 0, 180)] + ": " + count + " downloads.");
                if (data.TryGetProperty("assetCountsComplete", out var complete) && complete.ValueKind == JsonValueKind.True
                    && data.TryGetProperty("totalAssetDownloads", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var downloads) && downloads >= 0)
                    lines.Add("Total release asset downloads: " + downloads + ".");
                lines.Add("Asset downloads are cumulative counts, not unique users or installs; no traffic analytics were inferred.");
            }
        }
        var text = string.Join("\n", lines.Distinct().Take(24));
        return text.Length == 0 ? "" : "\nVerified API facts (not a complete analysis):\n" + text[..Math.Min(text.Length, 2800)];
    }
}
