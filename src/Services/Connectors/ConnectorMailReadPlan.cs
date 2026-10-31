namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;
using System.Text.RegularExpressions;

// A bounded contract inside an existing Gmail task, not another planner or
// authority owner. Clear human clauses or a validated model partition supply
// literal request parts; only verified receipts can supply their evidence.
internal sealed record ConnectorMailReadGoal(string Id, string Request, string Kind)
{
    internal string ReadMode => Kind switch { "thread" => "thread", "read" => "message", _ => "preview" };
}

internal static class ConnectorMailReadPlan
{
    // Paired search literals are data, not instructions. Boundary checks keep
    // apostrophes in ordinary contractions/possessives from hiding a request.
    // An orphan closing apostrophe before another clear opening makes the
    // preceding single-quoted span ambiguous (e.g. a plural possessive).
    // Mask that span conservatively instead of promoting leaked words to intent.
    private const string QuotedText = "[\"“][^\"“”]*[\"”]|(?<![\\p{L}\\p{N}])'(?:[^'\\r\\n]|(?<=[\\p{L}\\p{N}])'(?=[\\p{L}\\p{N}]))*'(?![\\p{L}\\p{N}])(?:[^'\\r\\n]*\\b'(?![\\p{L}\\p{N}]))?|‘(?:[^‘’\\r\\n]|(?<=[\\p{L}\\p{N}])’(?=[\\p{L}\\p{N}]))*’(?:[^‘’\\r\\n]*\\b’(?![\\p{L}\\p{N}]))?";
    internal static ConnectorMailReadGoal[] FromClearUserParts(string user)
    {
        if (!Required(user)) return [];
        // Partition only clear imperative/question boundaries. Do not infer
        // names, addresses, queries or facts from this grammar. Ambiguous turns
        // still use the bounded model-supplied plan; normal requests bypass it.
        var unquotedPositions = Regex.Replace(user, QuotedText, match => new string(' ', match.Length),
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        var starts = Regex.Matches(unquotedPositions,
            @"(?:^|[,.!?;:]\s*|(?:[,;]\s*|\s+)(?:also|finally|then|and)\s+)(?<action>(?:please\s+)?(?:(?:read|summari[sz]e|count|find)\b|what\b|where\b|when\b|how\s+many\b))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (starts.Count is < 2 or > 6) return [];
        var parts = starts.Select((start, index) =>
        {
            var begin = index == 0 ? 0 : start.Groups["action"].Index;
            var end = index + 1 < starts.Count ? starts[index + 1].Index : user.Length;
            var request = user[begin..end].Trim(' ', '\t', '.', ';', ',', '!', '?');
            var kind = MailCountRequested(request) ? "count"
                : JarvisConnectorController.MailReadMode(request) == "thread" ? "thread" : "read";
            return new { request, kind };
        }).ToArray();
        try { return Parse(JsonSerializer.Serialize(parts), user, true); }
        catch (ArgumentException) { return []; } // No uncertain partition becomes authority.
    }

    internal static bool Required(string user)
    {
        var request = WithoutQuotes(user);
        // Mixed content + mailbox-count intents caused the measured failure.
        // Do not classify counts of words/action items as mailbox totals, or
        // require a plan for a simple count with subject/date examples.
        var content = JarvisConnectorController.MailReadMode(user) != "preview"
            || Regex.IsMatch(request, @"\b(?:details|summary)\s+(?:of|for|about)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            || Regex.IsMatch(request, @"\b(?:what|where|when)\b[^.;!?]{0,160}\b(?:pending|happening|conversation|thread|correspondence)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return content && MailCountRequested(request)
            && !Regex.IsMatch(request, @"\b(?:send|forward|delete|trash)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50));
    }

    internal static bool MailCountRequested(string user)
    {
        var request = WithoutQuotes(user);
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        return Regex.Matches(request,
            @"\b(?:count|how\s+many|number\s+of)\b(?<object>[^.;!?]{0,160}?)\b(?:e-?mails?|messages?|notifications?)\b(?!\s+(?:words?|characters?|sentences?|paragraphs?|action\s+items?|tasks?|links?|recipients?|attachments?|addresses?|threads?|conversations?)\b)",
            options, TimeSpan.FromMilliseconds(50)).Any(match =>
            // Count the messages, not words/action items/attachments inside
            // them. Only inspect text BEFORE the first mailbox noun so that
            // 'count emails containing words/action items' keeps its meaning.
            !Regex.IsMatch(match.Groups["object"].Value,
                @"\b(?:words?|characters?|sentences?|paragraphs?|action\s+items?|tasks?|links?|recipients?|attachments?|addresses?|threads?|conversations?)\b[^.;!?]{0,120}\b(?:in|inside|within|of|per|from)\b",
                options, TimeSpan.FromMilliseconds(50))
            && !Regex.IsMatch(request[..match.Index], @"\b(?:do\s+not|don['’]t|never)\s*$", options, TimeSpan.FromMilliseconds(50)));
    }

    internal static ConnectorMailReadGoal[] Parse(string json, string user, bool required)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            if (required) throw new ArgumentException("Separate the requested mailbox reads and count into a read plan.");
            return [];
        }
        if (json.Length > 3600) throw new ArgumentException("Read plan is too large.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 3 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 6)
            throw new ArgumentException("Read plan must have one to six parts.");
        var goals = new List<ConnectorMailReadGoal>();
        var previousEnd = 0;
        foreach (var part in document.RootElement.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) throw new ArgumentException("Read plan part must be an object.");
            var fields = part.EnumerateObject().ToArray();
            if (fields.Length != 2 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 2
                || fields.Any(field => field.Name is not ("request" or "kind") || field.Value.ValueKind != JsonValueKind.String))
                throw new ArgumentException("Read plan parts use only request and kind.");
            var request = part.GetProperty("request").GetString()?.Trim() ?? "";
            var kind = part.GetProperty("kind").GetString();
            if (request.Length is < 8 or > 1000 || request.Any(char.IsControl) || kind is not ("read" or "thread" or "count"))
                throw new ArgumentException("Read plan part has an invalid request or kind.");
            var position = user.IndexOf(request, previousEnd, StringComparison.OrdinalIgnoreCase);
            if (position < 0) throw new ArgumentException("Read plan requests must be non-overlapping, verbatim parts of this human turn, in order.");
            if (HasUncoveredRead(user[previousEnd..position]))
                throw new ArgumentException("Read plan omits another requested content/question part.");
            if (Regex.Matches(WithoutQuotes(request), @"\b(?:read|summari[sz]e|count)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)).Count > 1)
                throw new ArgumentException("Keep distinct requested reads/counts in separate parts.");
            if (kind == "count" && !MailCountRequested(request))
                throw new ArgumentException("A mailbox count must be requested in that human part.");
            if (kind != "count" && MailCountRequested(request))
                throw new ArgumentException("A mailbox count is a separate count part, not a sampled content read.");
            if (kind == "thread" && !Regex.IsMatch(request, @"\b(?:conversation|thread|correspondence)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                throw new ArgumentException("A thread part must refer to a conversation or thread.");
            previousEnd = position + request.Length;
            goals.Add(new("g" + (goals.Count + 1), request, kind));
        }
        if (required && (goals.Count < 2 || !goals.Any(goal => goal.Kind == "count") || !goals.Any(goal => goal.Kind != "count")))
            throw new ArgumentException("Include separate content and mailbox-count parts from this human turn.");
        if (HasUncoveredRead(user[previousEnd..])) throw new ArgumentException("Read plan omits another requested content/question part.");
        return goals.ToArray();
    }

    private static bool HasUncoveredRead(string fragment) => Regex.IsMatch(WithoutQuotes(fragment),
        @"\b(?:read|summari[sz]e|count|conversation|thread|correspondence|what|where|when|how)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static string WithoutQuotes(string user) => Regex.Replace(user ?? "", QuotedText, "",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
}
