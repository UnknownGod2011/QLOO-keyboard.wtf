namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;
using System.Text.RegularExpressions;

// Structural selection inside one existing read owner. No credentials, route,
// stored body, navigation dispatch or semantic classifier. The owned executor
// and controller's current-revision TryCommit supply account/session authority.
internal static class ConnectorReadTargetSelection
{
    internal static bool HasUniqueObject(string provider, string capability,
        IReadOnlyDictionary<string, JsonElement> arguments, JsonElement receipt)
    {
        if (receipt.ValueKind != JsonValueKind.Object || Text(receipt, "state") != "completed"
            || !receipt.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
            || !receipt.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data)) return false;
        if (provider == "gmail")
        {
            static bool Id(string value) => value.Length is >= 8 and <= 64 && value.All(char.IsAsciiHexDigit);
            if (capability is "mail_read" or "mail_thread")
            {
                if (data.ValueKind != JsonValueKind.Object || !Id(Text(data, "id"))
                    || !arguments.TryGetValue("id", out var requested) || requested.ValueKind != JsonValueKind.String
                    || requested.GetString() != Text(data, "id")) return false;
                if (capability == "mail_read") return Id(Text(data, "threadId"));
                // This specific observed thread remains one target even if its
                // recent child text is bounded; no child can retarget it.
                var boundedReferences = result.TryGetProperty("truncated", out var partial) && partial.ValueKind == JsonValueKind.True
                    && data.TryGetProperty("messageReferences", out var references) && references.ValueKind == JsonValueKind.Array
                    && references.GetArrayLength() is > 0 and <= 100
                    && references.EnumerateArray().All(reference => Id(Text(reference, "id")) && Text(reference, "threadId") == Text(data, "id"));
                bool KnownChildOrClippedPlaceholder(JsonElement message)
                {
                    if (Id(Text(message, "id")) && Text(message, "threadId") == Text(data, "id")) return true;
                    // The provider normalizer retains references before its body
                    // budget. Later clipped children may have empty fields; they
                    // are no evidence of another object or of complete content.
                    return boundedReferences && message.ValueKind == JsonValueKind.Object
                        && Text(message, "id").Length == 0 && Text(message, "threadId").Length == 0
                        && Text(message, "text").Length == 0 && Text(message, "snippet").Length == 0
                        && message.TryGetProperty("contentTruncated", out var clipped) && clipped.ValueKind == JsonValueKind.True;
                }
                return data.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
                    && messages.GetArrayLength() <= 100 && messages.EnumerateArray().All(KnownChildOrClippedPlaceholder);
            }
            if (capability != "mail_search" || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("messages", out var candidates) || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() != 1 || !Id(Text(candidates[0], "id")) || !Id(Text(candidates[0], "threadId"))) return false;
            if (!result.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False
                || !data.TryGetProperty("hasMore", out var more) || more.ValueKind != JsonValueKind.False
                || data.TryGetProperty("nextPageToken", out var next) && (next.ValueKind != JsonValueKind.String || next.GetString().Length != 0)
                || !data.TryGetProperty("pageItemCount", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var number) || number != 1) return false;
            if (data.TryGetProperty("metadataSample", out var sample) && (sample.ValueKind != JsonValueKind.Object
                || !sample.TryGetProperty("complete", out var sampleComplete) || sampleComplete.ValueKind != JsonValueKind.True)) return false;
            if (result.TryGetProperty("coverage", out var coverage) && (coverage.ValueKind != JsonValueKind.Object
                || !coverage.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)) return false;
            return true;
        }
        if ((provider, capability) is not (("googledrive", "drive_search") or ("googledrive", "drive_read")
            or ("googledocs", "docs_read") or ("googleforms", "forms_read") or ("googlecalendar", "calendar_list")
            or ("github", "github_repos") or ("github", "github_issues") or ("github", "github_repo")
            or ("youtube", "youtube_search") or ("youtube", "youtube_channel") or ("slack", "slack_search"))) return false;
        // Reuse the existing provider-specific identity/URL/coverage validators.
        // A list with multiple objects or unknown/omitted references stays unsafe.
        var selected = ConnectorResourceReferences.Collect(provider, [receipt], [capability], ranked: false);
        if (selected.Ambiguous || selected.References.Length != 1) return false;
        if (capability == "github_repo") return arguments.TryGetValue("repository", out var repo)
            && repo.ValueKind == JsonValueKind.String && string.Equals(repo.GetString(), selected.References[0].Id, StringComparison.OrdinalIgnoreCase);
        if (arguments.TryGetValue("id", out var resource))
            return resource.ValueKind == JsonValueKind.String && resource.GetString() == selected.References[0].Id;
        return true;
    }
    // Compatibility only: no semantic selection can be inferred from a
    // six-field answer. Recognize a small explicit one-object human grammar;
    // unsupported wording keeps its answer/evidence but gains no auto-target.
    internal static bool SingleLiteralRequest(string request, string capability, string id)
    {
        if (request?.Length is not (> 0 and <= 3000) || capability is not ("docs_read" or "forms_read")
            || string.IsNullOrEmpty(id)) return false;
        const string marker = "KWT_LITERAL_OBJECT";
        if (request.Contains(marker, StringComparison.Ordinal)) return false;
        var urls = new Regex(@"https://docs\.google\.com/(?<kind>document|forms)/d/(?<id>[A-Za-z0-9_-]{1,256})(?:/[^\s<>""']*)?",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
        var matches = urls.Matches(request);
        var objects = matches.Select(match => (Kind: match.Groups["kind"].Value, Id: match.Groups["id"].Value)).Distinct().ToArray();
        var kind = capability == "docs_read" ? "document" : "forms";
        if (objects.Length > 0 && (objects.Length != 1 || objects[0].Kind != kind || objects[0].Id != id)) return false;
        // Tabs/query suffixes of the same canonical object do not create a
        // second object. Mixed bare IDs or unsupported scope syntax fail closed.
        var scope = objects.Length == 1 ? urls.Replace(request, marker) : request;
        var singular = capability == "docs_read" ? "(?:document|doc)" : "form";
        var objectPattern = objects.Length == 1 ? marker + @"(?:\s*(?:,|and|&)\s*" + marker + @")*" : Regex.Escape(id);
        var pattern = @"^\s*(?:(?:please|can\s+you|could\s+you)\s+)*(?:read|summari[sz]e|analy[sz]e|explain)\s+"
            + @"(?:(?:this|the|my)\s+)?(?:Google\s+)?(?:" + singular + @"\s*:?\s+)?" + objectPattern
            + @"\s*(?:[.!]?|[.!]\s*(?:do\s+not|don't)\s+(?:change|edit)\s+it[.!]?)\s*$";
        return Regex.IsMatch(scope, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(50));
    }
    private static string Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}
