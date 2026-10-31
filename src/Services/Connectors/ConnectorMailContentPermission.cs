namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;
using System.Text.RegularExpressions;

// A view/admission constraint inside the existing human-owned read, not new
// capabilities, credentials or storage. Only accepted human instructions enter.
internal static class ConnectorMailContentPermission
{
    private const RegexOptions IntentOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan MatchBudget = TimeSpan.FromMilliseconds(50);
    // Keep these fixed permission automata independent of the shared static
    // Regex cache; unrelated routing patterns must not repeatedly rebuild them.
    private static readonly Regex QuotedText = new(@"https?://[^\s<>""']+|""(?:[^""\\]|\\.)*""|“[^”]*”",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchBudget);
    private static readonly Regex HeadersCommand = new(@"\b(?:only|just)\s+(?:(?:read|inspect|fetch|show|list|display)\s+)?(?:(?:the|full|actual|email|message)\s+){0,3}(?:headers?|metadata|subjects?)\b", IntentOptions, MatchBudget);
    private static readonly Regex HeadersOnly = new(@"\b(?:headers?|metadata|subjects?)\s+only\b", IntentOptions, MatchBudget);
    private static readonly Regex BodyRestriction = new(@"\b(?:do\s+not|don't|never|avoid|skip|without|no)\s+(?:(?:read(?:ing)?|fetch(?:ing)?|inspect(?:ing)?|the|any|more|actual|full|message|email)\s+)*(?:bodies|body|content)\b", IntentOptions, MatchBudget);
    private static readonly Regex ConditionalApproval = new(@"\b(?:if|unless|until|after|when|later|once)\b", IntentOptions, MatchBudget);
    private static readonly Regex BodyApproval = new(@"^\s*(?:(?:actually|please|now|instead)\b[\s,]*)*(?:read|inspect|fetch|include)\s+(?:(?:the|full|actual|message|email)\s+)*(?:bodies|body|content)\b", IntentOptions, MatchBudget);

    internal static bool MetadataOnly(string original, IEnumerable<string> acceptedInstructions)
    {
        var restricted = false;
        foreach (var instruction in new[] { original }.Concat(acceptedInstructions ?? []))
        {
            var human = ConnectorReadReasoner.TryLiteralMailQuery(instruction, out _, out var prefix) ? prefix : instruction;
            var intent = QuotedText.Replace(human ?? "", " ");
            // Separate bounded automata: their combined alternative exceeds
            // the .NET nonbacktracking engine's default node ceiling.
            if (HeadersCommand.IsMatch(intent)
                || HeadersOnly.IsMatch(intent)
                || BodyRestriction.IsMatch(intent))
                restricted = true;
            else if (BodyApproval.IsMatch(intent)
                && !ConditionalApproval.IsMatch(intent))
                restricted = false;
        }
        return restricted;
    }

    internal static bool BodyCapability(string capability) => capability is "mail_read" or "mail_thread";

    // Known normalized metadata only. Unknown fields and alternate body,
    // snippet, preview, MIME, HTML and content-sample shapes never survive.
    internal static JsonElement Project(JsonElement receipt)
    {
        object Envelope(JsonElement value)
        {
            var result = Scalars(value, ["state", "verified", "taskId", "operationId"]);
            if (Unique(value, "result", out var child) && child.ValueKind == JsonValueKind.Object)
            {
                var projected = Scalars(child, ["source", "truncated"]);
                if (Unique(child, "coverage", out var coverage) && coverage.ValueKind == JsonValueKind.Object)
                    projected["coverage"] = Scalars(coverage, ["complete", "hasMore", "returned", "requested", "pageItemCount"]);
                if (Unique(child, "data", out var data) && data.ValueKind == JsonValueKind.Object)
                    projected["data"] = Data(data, true);
                result["result"] = projected;
            }
            return result;
        }
        object Data(JsonElement value, bool children)
        {
            var result = Scalars(value, ["id", "threadId", "from", "to", "subject", "date", "internalDate",
                "queryUsed", "pageItemCount", "hasMore", "nextPageToken", "contentTruncated"]);
            if (Unique(value, "headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
                result["headers"] = headers.EnumerateArray().Take(32)
                    .Where(header => Unique(header, "name", out var name) && name.ValueKind == JsonValueKind.String
                        && new[] { "From", "To", "Cc", "Bcc", "Reply-To", "Subject", "Date", "Message-ID", "In-Reply-To", "References" }
                            .Contains(name.GetString(), StringComparer.OrdinalIgnoreCase))
                    .Select(header => Scalars(header, ["name", "value"])).ToArray();
            foreach (var name in new[] { "messages", "sampledMessages" })
                if (children && Unique(value, name, out var items) && items.ValueKind == JsonValueKind.Array)
                    result[name] = items.EnumerateArray().Take(100).Where(item => item.ValueKind == JsonValueKind.Object)
                        .Select(item => Data(item, false)).ToArray();
            if (Unique(value, "metadataSample", out var sample) && sample.ValueKind == JsonValueKind.Object)
                result["metadataSample"] = Scalars(sample, ["requested", "returned", "complete"]);
            return result;
        }
        return JsonSerializer.SerializeToElement(Envelope(receipt));
    }

    private static Dictionary<string, object> Scalars(JsonElement value, string[] names)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in names)
            if (Unique(value, name, out var field)
                && (field.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                    || field.ValueKind == JsonValueKind.String && field.GetString().Length <= 8000))
                result[name] = field.Clone();
        return result;
    }
    private static bool Unique(JsonElement value, string name, out JsonElement field)
    {
        field = default;
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count(property => property.Name == name) == 1
            && value.TryGetProperty(name, out field);
    }
}
