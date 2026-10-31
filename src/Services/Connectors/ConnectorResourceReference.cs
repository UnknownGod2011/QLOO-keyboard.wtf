namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;
using System.Text.RegularExpressions;

// Session facts, never provider credentials, whole bodies or executable authority.
internal sealed record ConnectorResourceReference(string Kind, string Id, string Title, string Date, string Url);

internal static class ConnectorResourceReferences
{
    internal static (ConnectorResourceReference[] References, string PrimaryUrl, bool Ambiguous) Collect(
        string provider, JsonElement[] receipts, string[] capabilities, bool ranked)
    {
        var references = new Dictionary<string, ConnectorResourceReference>(StringComparer.Ordinal);
        string primary = null;
        var overflow = false;
        var incomplete = false;
        for (var index = 0; index < receipts.Length; index++)
        {
            var capability = index < capabilities.Length ? capabilities[index] : "";
            var selectionIncomplete = SelectionIncomplete(provider, capability, receipts[index]);
            var extracted = Extract(provider, capability, receipts[index]).ToArray();
            if (TryData(receipts[index], out _, out var data) && (IsList(provider, capability) || IsSingle(provider, capability)))
                selectionIncomplete |= extracted.Length != Items(provider, capability, data).Length;
            incomplete |= selectionIncomplete;
            var found = extracted.GroupBy(value => value.Kind + ":" + value.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            // A cited search page with several objects does not identify the
            // first object as the selected result. Never guess by array order.
            if (index == 0 && ranked && !selectionIncomplete && found.Length == 1) primary = found[0].Url;
            foreach (var value in found)
            {
                var key = value.Kind + ":" + value.Id;
                if (!references.ContainsKey(key) && references.Count >= 12) { overflow = true; continue; }
                references[key] = value;
            }
        }
        return (references.Values.ToArray(), primary, ranked ? primary is null : incomplete || overflow || references.Count != 1);
    }

    private static bool SelectionIncomplete(string provider, string capability, JsonElement receipt)
    {
        if (!IsList(provider, capability) && !IsSingle(provider, capability)) return false;
        if (!TryData(receipt, out var result, out var data)) return true;
        if (IsSingle(provider, capability)) return data.ValueKind != JsonValueKind.Object;
        static bool HasArray(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array;
        var hasCollection = (provider, capability) switch
        {
            ("googledrive", "drive_search") => HasArray(data, "files"),
            ("googlecalendar", "calendar_list") or ("youtube", "youtube_search") or ("youtube", "youtube_channel") => HasArray(data, "items"),
            ("github", "github_repos") or ("github", "github_issues") => data.ValueKind == JsonValueKind.Array,
            ("slack", "slack_search") => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("messages", out var messages) && HasArray(messages, "matches"),
            _ => false
        };
        if (!hasCollection) return true;
        var items = Items(provider, capability, data);
        // A bounded page/excerpt is not proof that its only retained openable
        // object was selected. A later specific read can establish that fact.
        if (items.Length > 100 || result.TryGetProperty("truncated", out var truncated) && truncated.ValueKind != JsonValueKind.False) return true;
        if (!result.TryGetProperty("coverage", out var coverage)) return false;
        return coverage.ValueKind != JsonValueKind.Object
            || !coverage.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True
            || coverage.TryGetProperty("hasMore", out var more) && more.ValueKind != JsonValueKind.False
            || coverage.TryGetProperty("pageItemCount", out var count)
                && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var total) || total != items.Length);
    }

    private static bool IsList(string provider, string capability) => (provider, capability) is
        ("googledrive", "drive_search") or ("googlecalendar", "calendar_list")
        or ("github", "github_repos") or ("github", "github_issues")
        or ("youtube", "youtube_search") or ("youtube", "youtube_channel") or ("slack", "slack_search");

    private static bool IsSingle(string provider, string capability) => (provider, capability) is
        ("googledrive", "drive_read") or ("googledocs", "docs_read") or ("googleforms", "forms_read") or ("github", "github_repo");

    private static bool TryData(JsonElement receipt, out JsonElement result, out JsonElement data)
    {
        result = data = default;
        return receipt.ValueKind == JsonValueKind.Object && Text(receipt, "state") == "completed"
            && receipt.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True
            && receipt.TryGetProperty("result", out result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out data);
    }

    private static JsonElement[] Items(string provider, string capability, JsonElement data) => (provider, capability) switch
    {
        ("googledrive", "drive_search") => Array(data, "files"),
        ("googledrive", "drive_read") or ("googledocs", "docs_read") or ("googleforms", "forms_read") or ("github", "github_repo") => new[] { data },
        ("googlecalendar", "calendar_list") or ("youtube", "youtube_search") or ("youtube", "youtube_channel") => Array(data, "items"),
        ("github", "github_repos") or ("github", "github_issues") => data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Take(101).ToArray() : [],
        ("slack", "slack_search") => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("messages", out var messages) ? Array(messages, "matches") : [],
        _ => System.Array.Empty<JsonElement>()
    };

    private static IEnumerable<ConnectorResourceReference> Extract(string provider, string capability, JsonElement receipt)
    {
        if (!TryData(receipt, out _, out var data)) yield break;
        // Inspect only the capability's known resource collection, not arbitrary
        // nested IDs, body/description URLs, quoted mail or instructions.
        foreach (var item in Items(provider, capability, data).Take(100))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Text(item, "id"); var title = Text(item, "title"); var date = Text(item, "modifiedTime");
            string kind = null, url = null;
            switch (capability)
            {
                case "drive_search": case "drive_read":
                    if (!ResourceId(id)) break;
                    kind = "drive_file"; title = Text(item, "name");
                    url = DriveUrl(Text(item, "webViewLink"), id);
                    break;
                case "docs_read":
                    id = Text(item, "documentId"); kind = "document";
                    if (ResourceId(id)) url = "https://docs.google.com/document/d/" + id + "/edit";
                    break;
                case "forms_read":
                    id = Text(item, "formId"); kind = "form";
                    title = item.TryGetProperty("info", out var info) ? Text(info, "title") : "";
                    // A respondent URL may use a different public ID and is not
                    // authority to edit/open an unrelated form.
                    if (ResourceId(id)) url = "https://docs.google.com/forms/d/" + id + "/edit";
                    break;
                case "calendar_list":
                    if (!ResourceId(id)) break;
                    kind = "calendar_event"; title = Text(item, "summary");
                    date = item.TryGetProperty("start", out var start) ? Text(start, "dateTime") : "";
                    if (date.Length == 0 && item.TryGetProperty("start", out start)) date = Text(start, "date");
                    var eventUri = Web(Text(item, "htmlLink"));
                    if (eventUri is not null && eventUri.Host is "calendar.google.com" or "www.google.com" && eventUri.AbsolutePath == "/calendar/event"
                        && eventUri.Fragment.Length == 0 && ValidQuery(eventUri.Query, ["eid", "ctz"], "eid")) url = eventUri.AbsoluteUri;
                    break;
                case "github_repos": case "github_repo":
                    id = Text(item, "full_name"); kind = "repository"; title = Text(item, "name"); date = Text(item, "updated_at");
                    if (Repository(id)) url = GithubUrl(Text(item, "html_url"), "/" + id);
                    break;
                case "github_issues":
                    if (!item.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var ordinal) || ordinal <= 0) break;
                    kind = item.TryGetProperty("pull_request", out var pull) && pull.ValueKind == JsonValueKind.Object ? "pull_request" : "issue";
                    var issueUri = Web(kind == "pull_request" ? Text(pull, "html_url") : Text(item, "html_url"), "github.com");
                    if (issueUri is null || issueUri.Query.Length > 0 || issueUri.Fragment.Length > 0) break;
                    var path = issueUri.AbsolutePath.Split('/');
                    if (path.Length != 5 || !Repository(path[1] + "/" + path[2]) || path[3] != (kind == "issue" ? "issues" : "pull")
                        || path[4] != ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)) break;
                    if (kind == "pull_request")
                    {
                        var baseUri = Web(Text(item, "html_url"), "github.com");
                        if (baseUri is null || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0
                            || baseUri.AbsolutePath != "/" + path[1] + "/" + path[2] + "/issues/" + path[4]
                            && baseUri.AbsolutePath != "/" + path[1] + "/" + path[2] + "/pull/" + path[4]) break;
                    }
                    id = path[1] + "/" + path[2] + "/" + path[3] + "/" + path[4]; date = Text(item, "updated_at"); url = issueUri.AbsoluteUri;
                    break;
                case "youtube_search":
                    id = item.TryGetProperty("id", out var video) ? Text(video, "videoId") : ""; kind = "video";
                    if (Match(id, "^[A-Za-z0-9_-]{11}$")) url = "https://www.youtube.com/watch?v=" + id;
                    break;
                case "youtube_channel":
                    kind = "channel";
                    if (Match(id, "^UC[A-Za-z0-9_-]{22}$")) url = "https://www.youtube.com/channel/" + id;
                    break;
                case "slack_search":
                    var channel = item.TryGetProperty("channel", out var channelItem) ? Text(channelItem, "id") : "";
                    var timestamp = Text(item, "ts"); kind = "slack_message";
                    var messageUri = Web(Text(item, "permalink"));
                    if (!Match(channel, "^[CGD][A-Z0-9]{8,24}$") || !Match(timestamp, @"^[0-9]{10}\.[0-9]{6}$")
                        || messageUri is null || !Match(messageUri.Host, "^[a-z0-9][a-z0-9-]{0,62}\\.slack\\.com$")
                        || messageUri.AbsolutePath != "/archives/" + channel + "/p" + timestamp.Replace(".", "", StringComparison.Ordinal)
                        || messageUri.Query.Length > 0 || messageUri.Fragment.Length > 0) break;
                    id = channel + ":" + timestamp; date = timestamp; url = messageUri.AbsoluteUri;
                    break;
            }
            if (capability.StartsWith("youtube_", StringComparison.Ordinal) && item.TryGetProperty("snippet", out var snippet))
            { title = Text(snippet, "title"); date = Text(snippet, "publishedAt"); }
            if (url is not null) yield return new(kind, id, Limit(title, 500), Limit(date, 160), url);
        }
    }

    private static string DriveUrl(string raw, string id)
    {
        var uri = Web(raw);
        if (uri is null || !ValidQuery(uri.Query, ["usp", "resourcekey"])) return null;
        var valid = uri.Host == "drive.google.com" && (uri.AbsolutePath == "/file/d/" + id + "/view" || uri.AbsolutePath == "/drive/folders/" + id)
            || uri.Host == "docs.google.com" && new[] { "document", "spreadsheets", "presentation", "forms" }
                .Any(kind => uri.AbsolutePath == "/" + kind + "/d/" + id + "/edit");
        return valid && uri.Fragment.Length == 0 ? uri.AbsoluteUri : null;
    }
    private static string GithubUrl(string raw, string path)
    {
        var uri = Web(raw, "github.com");
        return uri is not null && uri.AbsolutePath == path && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri.AbsoluteUri : null;
    }
    private static Uri Web(string raw, string host = null) => raw.Length is > 0 and <= 2048 && !raw.Any(char.IsControl)
        && raw == raw.Trim() && !raw.Contains('\\') && Uri.TryCreate(raw, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && !uri.AbsolutePath.Contains('%')
        && (host is null || uri.Host.Equals(host, StringComparison.Ordinal)) ? uri : null;
    private static bool ValidQuery(string query, string[] fields, string required = null)
    {
        if (query.Length == 0) return required is null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !fields.Contains(parts[0], StringComparer.Ordinal) || !names.Add(parts[0])
                || parts[1].Length is < 1 or > 512 || !Match(parts[1], "^[A-Za-z0-9_%+/-]+$")) return false;
            var value = Uri.UnescapeDataString(parts[1]);
            if (value.Any(char.IsControl) || value.Contains(':') || value.Contains('&') || value.Contains('=') || value.Contains('?')) return false;
        }
        return required is null || names.Contains(required);
    }
    private static bool ResourceId(string id) => Match(id, "^[A-Za-z0-9_-]{1,256}$");
    private static bool Repository(string name) => Match(name, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$") && name.Split('/')[1] is not ("." or "..");
    // These anchored, non-nested character-class validators are linear. The
    // symbolic engine's default automata cap rejects the 256-char identifier
    // bound itself; retain a timeout without raising a global engine limit.
    private static bool Match(string value, string pattern) => Regex.IsMatch(value ?? "", pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static JsonElement[] Array(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var items)
        && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Take(101).ToArray() : [];
    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
        && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static string Limit(string value, int length) => value[..Math.Min(value.Length, length)];
}
