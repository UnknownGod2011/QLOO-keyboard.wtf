namespace KeyboardWtf.Services.Connectors;

using System.Text.RegularExpressions;
using System.Text.Json;

// A small native-search preparation inside an already-admitted Gmail task.
// No address inference, write routing, account choice or changed explicit query.
internal static class ConnectorPersonSearch
{
    internal static bool CanAnswerNativeLookup(string request, JsonElement receipt)
    {
        // Only a plain find/show participant request can finish from headers.
        // Subject hints, details, questions and empty/unusable pages retain the
        // existing refinement/content reader. No scope or evidence is removed.
        if (Query(request) is null || !Regex.IsMatch(NormalizeCourtesy(request), @"^\s*(?:find|show(?:\s+me)?)\s+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50))
            || Regex.IsMatch(request, @"\b(?:subject|details|information|summary|summarize|summarise)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50))
            || receipt.ValueKind != JsonValueKind.Object || Text(receipt, "state") != "completed"
            || !receipt.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
            || !receipt.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("messages", out var references) || references.ValueKind != JsonValueKind.Array
            || !data.TryGetProperty("sampledMessages", out var items) || items.ValueKind != JsonValueKind.Array) return false;
        return items.EnumerateArray().Take(8).Any(item =>
        {
            var id = Text(item, "id"); var thread = Text(item, "threadId");
            string Header(string name, string field)
            {
                var value = Text(item, field);
                if (value.Length > 0) return value;
                return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array
                    ? headers.EnumerateArray().Where(header => Text(header, "name").Equals(name, StringComparison.OrdinalIgnoreCase))
                        .Select(header => Text(header, "value")).FirstOrDefault() ?? "" : "";
            }
            return id.Length is >= 8 and <= 64 && id.All(char.IsAsciiHexDigit)
                && thread.Length is >= 8 and <= 64 && thread.All(char.IsAsciiHexDigit)
                && references.EnumerateArray().Take(100).Any(reference => Text(reference, "id") == id && Text(reference, "threadId") == thread)
                && Header("From", "from").Length > 0 && Header("Subject", "subject").Length > 0;
        });
    }

    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";

    // Preparation may isolate one explicit opening clause; it never executes it
    // or changes the original task goal. Keep the headers-only shortcut on Query
    // so a compound request still requires its normal read/open completion.
    internal static string PrepareQuery(string request)
    {
        if (request?.Length is not (> 0 and <= 3000)) return null;
        var clauses = Regex.Split(request, @"\s+(?:and(?:\s+then)?|then)\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(50));
        if (clauses.Length == 2)
        {
            bool Opening(string clause) => ConnectorFollowupIntent.TryParseExactOpen(clause, out var selection)
                && selection == ConnectorOpenSelection.Single && ConnectorFollowupIntent.ObjectMatchesProvider(clause, "gmail");
            if (Opening(clauses[1])) return Query(clauses[0].TrimEnd(',', ';'));
            if (Opening(clauses[0])) return Query(clauses[1]);
        }
        return Query(request);
    }

    internal static string Query(string request)
    {
        if (request?.Length is not (> 0 and <= 3000)) return null;
        request = NormalizeCourtesy(NormalizeReceivedDetailsRequest(request));
        // An explicitly tentative subject hint helps rank candidates; it is
        // not a required quoted phrase. Preserve hard subject/date/literal
        // restrictions for the existing scoped planner instead.
        var hint = Regex.Match(request,
            @"[.!?]\s+(?:the\s+)?subject(?:\s+line)?\s+(?:may|might|could)\s+(?:mention|include|contain|be(?:\s+something\s+like)?)\s+(?<hint>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
        if (hint.Success)
        {
            var hintText = hint.Groups["hint"].Value.Trim().TrimEnd('.', '!', '?');
            if (hintText.Length is 0 or > 160 || Regex.IsMatch(hintText,
                @"[.!?;:\""/]|\b(?:and|or|but|except|exclude|excluding|without|not|don't|unread|only|today|yesterday|since|before|after|between|last|next|week|month|year|send|reply|forward|delete|account|headers|bodies)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(50))) return null;
            request = request[..hint.Index];
        }
        // A read-only details clause does not change the named participant.
        // Other conjunctions (dates, another search, or writes) retain their
        // full request for the existing scoped planner instead of being lost.
        var details = Regex.Match(request, @"\s+and\s+(?<read>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
        if (details.Success && ConnectorFollowupIntent.MailDetails(details.Groups["read"].Value))
            request = request[..details.Index];
        const string person = @"(?:(?:dr\.?|doctor|prof\.?|professor)\s+)?[\p{L}][\p{L}'’-]*(?:\s+[\p{L}][\p{L}'’-]*){0,2}";
        var match = Regex.Match(request,
            @"^\s*(?:please\s+)?(?:find|read|summari[sz]e|show(?:\s+me)?|(?:give|tell)\s+me\s+(?:the\s+)?details\s+(?:about|of))\s+"
            + @"(?:(?:my|the|an?)\s+)?(?:emails?|messages?|conversations?|threads?)\s+(?<relation>with|from|by)\s+"
            + @"(?<names>.+?)[.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
        if (!match.Success || match.Groups["names"].Value.Length > 240) return null;
        var alternatives = Regex.Split(match.Groups["names"].Value, @"\s+or\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        if (alternatives.Length > 3 || alternatives.Any(name => !Regex.IsMatch(name, @"\A" + person + @"\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)))) return null;
        var names = alternatives.Select(name => Regex.Replace(name, @"^(?:dr\.?|doctor|prof\.?|professor)\s+", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            .Select(name => string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Any(name => name.Split(' ').Any(word => new[] { "and", "or", "today", "yesterday", "only", "about", "regarding", "since", "before", "after" }.Contains(word, StringComparer.OrdinalIgnoreCase)))) return null;
        var fromOnly = !match.Groups["relation"].Value.Equals("with", StringComparison.OrdinalIgnoreCase);
        var terms = names.SelectMany(name => fromOnly ? new[] { "from:\"" + name + "\"" }
            : new[] { "from:\"" + name + "\"", "to:\"" + name + "\"", "cc:\"" + name + "\"" }).ToArray();
        return terms.Length == 1 ? terms[0] : "{" + string.Join(" ", terms) + "}";
    }

    private static string NormalizeCourtesy(string request) => request is null ? "" : Regex.Replace(request,
        @"^\s*(?:(?:please|pls)\s+|(?:can|could|would)\s+you\s+){0,3}", "",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    // A reported received message plus a same-object read is the same native
    // participant lookup as "find my email from ...". This prepares only the
    // query: the original task request still owns time, content and account
    // scope, and the normal reader still verifies the returned candidates.
    private static string NormalizeReceivedDetailsRequest(string request)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        const string person = @"(?:(?:dr\.?|doctor|prof\.?|professor)\s+)?[\p{L}][\p{L}'’-]*(?:\s+[\p{L}][\p{L}'’-]*){0,2}";
        var received = Regex.Match(request,
            @"^\s*I\s+(?:(?:have|had)\s+)?(?:just\s+)?(?:received|got)\s+(?:an?|the)\s+(?:email|message)\s+from\s+"
            + @"(?<name>" + person + @")(?:[.!?]\s+|,\s*|\s+and\s+)(?<read>.+)$",
            options, TimeSpan.FromMilliseconds(50));
        if (!received.Success) return request;
        if (Regex.IsMatch(received.Groups["name"].Value,
            @"\b(?:and|or|today|yesterday|now|only|about|regarding|since|before|after|between|last|next|in|on|at|account|accounts|work|personal|school|university|headers|bodies|unread)\b",
            options, TimeSpan.FromMilliseconds(50))) return request;
        var read = Regex.Replace(received.Groups["read"].Value,
            @"^\s*(?:(?:please|pls)\s+|(?:can|could|would)\s+you\s+){0,3}look\s+into\s+(?:it|(?:that|this|the)\s+(?:email|message))\s+and\s+",
            "", options, TimeSpan.FromMilliseconds(50));
        if (!ConnectorFollowupIntent.MailDetails(read)
            || ConnectorFollowupIntent.MailSelection(read) != ConnectorMailSelection.Unspecified
            || ConnectorFollowupIntent.TryReadAndOpen(read, out _, out _)) return request;
        return "Find my email from " + received.Groups["name"].Value;
    }
}
