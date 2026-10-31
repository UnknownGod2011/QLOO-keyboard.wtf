namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;

// Preserve native page/sample scope when a reasoning outage produces a new
// answer envelope. These small facts contain no body, cursor or action authority.
internal static class ConnectorMailSearchCoverage
{
    internal static JsonElement[] Capture(IEnumerable<(string Capability, JsonElement Receipt)> evidence)
    {
        var pages = new List<JsonElement>();
        foreach (var (capability, receipt) in evidence.Take(8))
        {
            if (capability != "mail_search" || receipt.ValueKind != JsonValueKind.Object
                || !receipt.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "completed"
                || !receipt.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
                || !receipt.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            var page = new Dictionary<string, object>();
            if (data.TryGetProperty("queryUsed", out var query) && query.ValueKind == JsonValueKind.String
                && query.GetString().Length <= 3000) page["queryUsed"] = query.Clone();
            if (data.TryGetProperty("pageItemCount", out var count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out var items) && items is >= 0 and <= 100)
                page["pageItemCount"] = items;
            if (data.TryGetProperty("hasMore", out var more) && more.ValueKind is JsonValueKind.True or JsonValueKind.False)
                page["hasMore"] = more.GetBoolean();
            if (data.TryGetProperty("metadataSample", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                var sample = new Dictionary<string, object>();
                foreach (var name in new[] { "requested", "returned" })
                    if (metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                        && value.TryGetInt32(out var number) && number is >= 0 and <= 100) sample[name] = number;
                if (metadata.TryGetProperty("complete", out var complete) && complete.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    sample["complete"] = complete.GetBoolean();
                page["metadataSample"] = sample;
            }
            if (page.Count > 0) pages.Add(JsonSerializer.SerializeToElement(page));
        }
        return pages.ToArray();
    }

    internal static string Format(IReadOnlyList<JsonElement> pages)
    {
        if (pages.Count == 0) return "";
        var lines = new List<string>
        {
            "Native Gmail search coverage: these are separate returned pages, not a verified mailbox/query total."
        };
        foreach (var (page, index) in pages.Take(8).Select((page, index) => (page, index)))
        {
            var count = page.TryGetProperty("pageItemCount", out var size) && size.ValueKind == JsonValueKind.Number
                && size.TryGetInt32(out var items) ? items + " message references in this returned page. "
                : "The number of message references in this returned page is unknown. ";
            var continuation = page.TryGetProperty("hasMore", out var more) && more.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? more.GetBoolean() ? "More matching pages remain; this page count is NOT the total. "
                    : "The provider reported no further page for this query; the page count does not verify a semantic category total. "
                : "Whether more matching pages exist is unknown; no total is established. ";
            var sample = "";
            if (page.TryGetProperty("metadataSample", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("returned", out var returned) && returned.ValueKind == JsonValueKind.Number
                && returned.TryGetInt32(out var sampled))
                sample = "Metadata was returned for " + sampled + " messages; the displayed headers may be a smaller sample. "
                    + "Metadata is not a full body review or an importance/authenticity determination.";
            lines.Add("Page " + (index + 1) + ": " + count + continuation + sample);
        }
        if (pages.Count > 1) lines.Add("These pages or queries may overlap and have not been added into a total.");
        return string.Join("\n", lines) + "\n";
    }
}
