namespace KeyboardWtf.Services.Connectors;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

// A provider argument adapter, not a natural-language classifier. The Live model
// chooses constraints; the existing task/account owner still authorizes the read.
internal static class ConnectorMailSearchArguments
{
    internal static JsonElement Describe(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("connectorId", out var provider) || provider.GetString() != "gmail"
            || !metadata.TryGetProperty("capabilities", out var capabilities)
            || !capabilities.EnumerateArray().Any(cap => cap.GetProperty("id").GetString() == "mail_search")) return metadata;
        var copy = JsonNode.Parse(metadata.GetRawText());
        foreach (var cap in copy["capabilities"].AsArray())
            if (cap["id"]?.GetValue<string>() == "mail_search" && cap["arguments"] is JsonObject fields)
            {
                fields["query"] = "Topic terms or valid Gmail syntax; optional with sender/receivedOn. Use distinctive words, not an exact subject unless known. Quote literal phrases; sender and local calendar dates belong in the structured fields.";
                fields["sender"] = "optional sender display name or email from the request/observed headers; application quotes it as from:. Do not invent an address.";
                fields["receivedOn"] = "optional YYYY-MM-DD, today or yesterday. Prefer today/yesterday for those relative requests; the application resolves them from the frozen request_context.localTime and supplies exact local-day bounds. Do not add a date unless requested.";
            }
        return JsonSerializer.SerializeToElement(copy);
    }

    internal static void Normalize(Dictionary<string, JsonElement> arguments, bool literal, string timeZoneId,
        DateTimeOffset requestUtc = default, string request = null)
    {
        var hasSender = arguments.TryGetValue("sender", out var senderValue);
        var hasDay = arguments.TryGetValue("receivedOn", out var dayValue);
        if (!hasSender && !hasDay)
        {
            // The direct Live path must use the same participant preparation as
            // the reader. An unqualified name searches bodies too and can hide
            // an empty sender lookup, preventing the existing spelling recovery.
            // The caller supplies the request only for the first native lookup.
            // Reuse the reader's original person query rather than requiring
            // the model to repeat that name/direction exactly. Later spelling
            // refinements, literals and structured constraints keep their input.
            if (!literal && arguments.TryGetValue("query", out var proposed) && proposed.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(proposed.GetString())
                && ConnectorPersonSearch.PrepareQuery(request) is { } personQuery)
                arguments["query"] = JsonSerializer.SerializeToElement(personQuery);
            return;
        }
        if (literal) throw new ArgumentException("A literal human query cannot be combined with inferred sender/date fields. Preserve the exact query.");
        var parts = new List<string>();
        if (arguments.TryGetValue("query", out var queryValue))
        {
            if (queryValue.ValueKind != JsonValueKind.String) throw new ArgumentException("query must be text.");
            var query = queryValue.GetString();
            if (!string.IsNullOrWhiteSpace(query))
            {
                ConnectorMailDateScope.ValidateInferredQuery(query);
                parts.Add("(" + query.Trim() + ")");
            }
        }
        if (hasSender)
        {
            if (senderValue.ValueKind != JsonValueKind.String) throw new ArgumentException("sender must be one display name or email.");
            var sender = senderValue.GetString().Trim();
            if (sender.Length is 0 or > 320 || sender.Any(c => char.IsControl(c) || c is '"' or '\\'))
                throw new ArgumentException("sender must be a bounded display name or email, without query syntax or control text.");
            parts.Add("from:\"" + sender + "\"");
        }
        if (hasDay)
        {
            if (dayValue.ValueKind != JsonValueKind.String)
                throw new ArgumentException("receivedOn must be YYYY-MM-DD, today or yesterday.");
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            { throw new ArgumentException("The originating local timezone is unavailable; no date-scoped read ran."); }
            DateOnly day;
            var requestedDay = dayValue.GetString();
            if (requestedDay is "today" or "yesterday")
            {
                if (requestUtc == default) throw new ArgumentException("A relative day requires the frozen request clock.");
                day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(requestUtc, zone).DateTime)
                    .AddDays(requestedDay == "yesterday" ? -1 : 0);
            }
            else if (!DateOnly.TryParseExact(requestedDay, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out day))
                throw new ArgumentException("receivedOn must be a valid YYYY-MM-DD local date, today or yesterday.");
            var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            if (day == DateOnly.MaxValue) throw new ArgumentException("receivedOn needs a representable next day.");
            var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(start) || zone.IsAmbiguousTime(start) || zone.IsInvalidTime(end) || zone.IsAmbiguousTime(end))
                throw new ArgumentException("This local day boundary cannot be mapped uniquely; no date-scoped read ran.");
            long Unix(DateTime value) => new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, zone)).ToUnixTimeSeconds();
            // Parenthesized topic OR clauses cannot escape the compulsory bounds.
            parts.Add($"after:{Unix(start)} before:{Unix(end)}");
        }
        var native = string.Join(" ", parts);
        ConnectorMailDateScope.ValidateInferredQuery(native);
        arguments["query"] = JsonSerializer.SerializeToElement(native);
        arguments.Remove("sender"); arguments.Remove("receivedOn");
    }
}
