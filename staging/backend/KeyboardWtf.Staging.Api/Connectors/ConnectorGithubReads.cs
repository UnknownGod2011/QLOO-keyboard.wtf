using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeyboardWtf.Staging.Api.Connectors;

// Native authenticated GETs only. This is a normalized capability adapter, not
// an arbitrary HTTP proxy or a second task/account owner.
internal static class ConnectorGithubReads
{
    internal static string Argument(IReadOnlyDictionary<string, JsonElement>? args, string name, string fallback = "") =>
        args is not null && args.TryGetValue(name, out var value)
            ? value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 220 && !value.GetString()!.Any(char.IsControl)
                ? value.GetString()! : throw new ArgumentException("Invalid GitHub " + name + ".") : fallback;
    internal static string Choice(IReadOnlyDictionary<string, JsonElement>? args, string name, string fallback, params string[] allowed)
    {
        var value = Argument(args, name, fallback);
        return allowed.Contains(value, StringComparer.Ordinal) ? value : throw new ArgumentException("Unsupported GitHub " + name + ".");
    }
    internal static ConnectorRequestPlan Plan(string capability, string repository, IReadOnlyDictionary<string, JsonElement> args, string page)
    {
        var root = "https://api.github.com/repos/" + repository;
        string endpoint;
        switch (capability)
        {
            case "github_repo": endpoint = root; break;
            case "github_pulls": endpoint = root + "/pulls?per_page=15&state=" + Choice(args, "state", "open", "open", "closed", "all") + page; break;
            case "github_releases":
                var mode = Choice(args, "mode", "latest", "latest", "list", "tag");
                var tag = Argument(args, "tag");
                if (mode == "tag" && string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("A release tag is required.");
                endpoint = root + "/releases" + (mode == "list" ? "?per_page=15" + page : mode == "latest" ? "/latest" : "/tags/" + Uri.EscapeDataString(tag));
                break;
            case "github_release_assets":
                if (!args.TryGetValue("releaseId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number <= 0)
                    throw new ArgumentException("Use an observed numeric release ID.");
                endpoint = root + "/releases/" + number + "/assets?per_page=100" + page; break;
            case "github_activity":
                var kind = Choice(args, "kind", "commits", "branches", "commits", "contributors", "languages", "events", "workflows", "runs");
                endpoint = root + (kind == "workflows" ? "/actions/workflows" : kind == "runs" ? "/actions/runs" : "/" + kind);
                if (kind != "languages") endpoint += "?per_page=15" + page;
                break;
            default: throw new ArgumentException("Unsupported GitHub read.");
        }
        return new(endpoint, "GET", null);
    }

    internal static bool Search(IReadOnlyDictionary<string, JsonElement>? args) => !string.IsNullOrWhiteSpace(Argument(args, "query"));
    private static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    internal static JsonElement Match(JsonElement data, string query)
    {
        if (data.ValueKind != JsonValueKind.Array) throw new ConnectorProviderException("unavailable");
        var key = Key(query);
        if (key.Length is < 2 or > 200) throw new ArgumentException("Use a repository name with at least two letters or digits.");
        var rows = data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray();
        var literal = rows.Where(item => ComposioConnectorProvider.Text(item, "name").Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || ComposioConnectorProvider.Text(item, "full_name").Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        // An exact supplied name outranks punctuation recovery. Distinct real
        // repositories can differ only by a hyphen; do not blur that authority.
        if (literal.Length > 0) return JsonSerializer.SerializeToElement(literal);
        var exact = rows.Where(item => Key(ComposioConnectorProvider.Text(item, "name")) == key
            || Key(ComposioConnectorProvider.Text(item, "full_name")) == key).ToArray();
        // Keep every equal candidate; never pick a different owner's repository
        // merely because it occurred first. Partial-name matches remain candidates.
        return JsonSerializer.SerializeToElement(exact.Length > 0 ? exact : rows.Where(item =>
            Key(ComposioConnectorProvider.Text(item, "name")).Contains(key, StringComparison.Ordinal)).ToArray());
    }

    internal static JsonElement Project(JsonElement data, string capability)
    {
        if (capability == "github_repo") return Keep(data, RepoFields);
        if (capability == "github_releases")
            return data.ValueKind == JsonValueKind.Array ? JsonSerializer.SerializeToElement(data.EnumerateArray().Select(Release)) : Release(data);
        if (capability == "github_release_assets") return JsonSerializer.SerializeToElement(data.EnumerateArray().Select(Asset));
        if (capability is "github_pulls" or "github_activity") return Compact(data, 0);
        return data;
    }
    private static readonly string[] RepoFields = ["id", "name", "full_name", "description", "html_url", "private", "visibility", "archived", "fork", "default_branch", "topics", "language", "created_at", "updated_at", "pushed_at", "stargazers_count", "forks_count", "subscribers_count", "watchers_count", "open_issues_count", "size", "license"];
    private static JsonElement Keep(JsonElement value, params string[] fields) => value.ValueKind == JsonValueKind.Object
        ? JsonSerializer.SerializeToElement(value.EnumerateObject().Where(p => fields.Contains(p.Name, StringComparer.Ordinal)).ToDictionary(p => p.Name, p => p.Value.Clone())) : value.Clone();
    private static JsonElement Asset(JsonElement value) => Keep(value, "id", "name", "size", "content_type", "state", "download_count", "created_at", "updated_at", "browser_download_url");
    private static JsonElement Release(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return value.Clone();
        var result = Keep(value, "id", "tag_name", "name", "html_url", "draft", "prerelease", "created_at", "published_at", "target_commitish")
            .EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        if (value.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var retained = assets.EnumerateArray().Take(100).Select(Asset).ToArray();
            result["assets"] = retained;
            var ids = new HashSet<long>();
            var complete = assets.GetArrayLength() < 100 && retained.All(asset => asset.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var assetId) && assetId > 0 && ids.Add(assetId)
                && asset.TryGetProperty("download_count", out var count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt64(out var n) && n >= 0 && n <= long.MaxValue / 100);
            result["assetCountsComplete"] = complete;
            if (complete) result["totalAssetDownloads"] = retained.Sum(asset => asset.GetProperty("download_count").GetInt64());
            result["downloadMetric"] = "Cumulative downloads of these release assets; not unique users, installs, clones, or source archive downloads.";
        }
        // Identifiers and measured asset facts precede the potentially long
        // release notes in the shared serializer's unchanged evidence budget.
        if (value.TryGetProperty("body", out var body)) result["body"] = body.Clone();
        return JsonSerializer.SerializeToElement(result);
    }
    private static JsonElement Compact(JsonElement value, int depth)
    {
        if (depth > 8) return JsonSerializer.SerializeToElement((object?)null);
        if (value.ValueKind == JsonValueKind.Array) return JsonSerializer.SerializeToElement(value.EnumerateArray().Take(100).Select(v => Compact(v, depth + 1)));
        if (value.ValueKind != JsonValueKind.Object) return value.Clone();
        // Avoid vendor URL templates, avatars and permission payloads drowning
        // useful API evidence. Keep exact html_url and all scalar metrics.
        return JsonSerializer.SerializeToElement(value.EnumerateObject().Where(p => p.Name == "html_url"
            || !(p.Name.EndsWith("url", StringComparison.Ordinal) || p.Name is "permissions" or "_links"))
            .ToDictionary(p => p.Name, p => Compact(p.Value, depth + 1)));
    }
}
