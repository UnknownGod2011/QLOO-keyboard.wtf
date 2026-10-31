using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeyboardWtf.Staging.Api.Connectors;

// Bounded reads inside the existing gateway operation. No planner, content store,
// independent task owner, provider discovery, write retry or AI invocation.
internal static class ConnectorMailQueries
{
    // Gmail excludes spam/trash from messages.list unless this separate flag
    // accompanies an explicit location query. Ordinary searches retain their
    // existing scope; quoted body text and negative operators grant no scope.
    internal static bool IncludesSpamTrash(string query)
    {
        var unquoted = Regex.Replace(query, "\"[^\"]*\"", " ", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        // Do not infer a positive location from inside a negated group. Leave
        // complex negative-group queries at their existing default scope.
        if (Regex.IsMatch(unquoted, @"-\s*[{(]", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50))) return false;
        return Regex.IsMatch(unquoted, @"(?:^|[\s{(])in:(?:spam|trash|anywhere)(?:$|[\s})])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(50));
    }

    internal static string MatchMode(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("matchMode", out var mode)) return "exact"; // Existing API clients retain exact queries.
        return mode.ValueKind == JsonValueKind.String && mode.GetString() is "exact" or "topic"
            ? mode.GetString()! : throw new ArgumentException("Mail match mode must be exact or topic.");
    }

    internal static string ReadMode(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("readMode", out var mode)) return "preview";
        return mode.ValueKind == JsonValueKind.String && mode.GetString() is "preview" or "message" or "thread"
            ? mode.GetString()! : throw new ArgumentException("Mail read mode must be preview, message or thread.");
    }

    internal static string SearchQuery(IReadOnlyDictionary<string, JsonElement> args)
    {
        var query = args["query"].GetString()!;
        // A model-inferred whole topic is not an exact contiguous phrase. Keep
        // all terms (AND semantics); never loosen user literals, operators,
        // filtered queries or an already-started pagination window.
        var phrase = query.Trim();
        if (MatchMode(args) != "topic" || query.Any(char.IsControl) || phrase.Length < 2 || phrase[0] != '"' || phrase[^1] != '"'
            || args.TryGetValue("pageToken", out var page) && !string.IsNullOrEmpty(page.GetString())) return query;
        var words = phrase[1..^1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 12 && words.All(word => !word.StartsWith('-') && word is not ("OR" or "AND" or "NOT")
            && Regex.IsMatch(word, @"^[\p{L}\p{N}_'’.-]+$", RegexOptions.CultureInvariant))
            ? string.Join(" ", words) : query;
    }

    internal static int CountPageLimit(IReadOnlyDictionary<string, JsonElement> args)
    {
        var pages = 10;
        if (args.TryGetValue("maxPages", out var value)
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out pages) || pages is < 1 or > 10))
            throw new ArgumentException("Mail count page limit must be between 1 and 10.");
        return pages;
    }

    internal static string? BroaderTopicQuery(string query)
    {
        // Called only after an empty, first-page inferred-topic read. Keep
        // sender/date/negative filters and separate subject scopes intact.
        // Exact/default queries and continuation pages never enter this path.
        if (query.Any(char.IsControl) || query.IndexOfAny(['(', ')', '{', '}', '|', '\\']) >= 0) return null;
        var tokens = Regex.Matches(query, "(?:-?[A-Za-z][A-Za-z0-9_-]*:)?(?:\"[^\"\\r\\n]*\"|[^\\s\"]+)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)).Select(value => value.Value).ToArray();
        if (string.Concat(tokens) != Regex.Replace(query, @"\s", ""))
        {
            // Whitespace inside a quoted phrase is significant to tokenization.
            var compact = string.Join(" ", tokens);
            if (compact != Regex.Replace(query.Trim(), @"\s+", " ")) return null;
        }
        if (tokens.Any(value => value is "OR" or "AND" or "NOT")) return null;
        var retained = new List<string>(); var terms = new List<string>();
        var subjectRefined = false;
        foreach (var token in tokens)
        {
            if (token.StartsWith("subject:\"", StringComparison.OrdinalIgnoreCase) && token.EndsWith('"'))
            {
                var subjectWords = token[9..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (subjectWords.Length > 12 || subjectWords.Any(word => word.StartsWith('-')
                    || word.Equals("OR", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("AND", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("NOT", StringComparison.OrdinalIgnoreCase)
                    || !Regex.IsMatch(word, @"^[\p{L}\p{N}_'’.-]+$", RegexOptions.CultureInvariant))) return null;
                var unique = subjectWords.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (unique.Length >= 2)
                {
                    // Alternatives remain subject-scoped; unrelated bare terms
                    // or other subject groups are still required (AND).
                    retained.Add("{" + string.Join(" ", unique.Select(word => "subject:" + word)) + "}");
                    subjectRefined = true;
                }
                else retained.Add(token);
                continue;
            }
            if (token.StartsWith('-') || token.Contains(':')) { retained.Add(token); continue; }
            var words = token.Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(word => !Regex.IsMatch(word, @"^[\p{L}\p{N}_'’.-]+$", RegexOptions.CultureInvariant))) return null;
            terms.AddRange(words);
        }
        terms = terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (terms.Count > 12 || !subjectRefined && terms.Count < 2) return null;
        if (terms.Count >= 2) retained.Add("{" + string.Join(" ", terms) + "}");
        else if (terms.Count == 1) retained.Add(terms[0]);
        var broader = string.Join(" ", retained);
        return broader == query ? null : broader;
    }

    internal static async Task<JsonElement> SearchAsync(IConnectorProvider provider, string account,
        JsonElement firstPage, IReadOnlyDictionary<string, JsonElement> args,
        Func<CancellationToken, Task> authority, CancellationToken ct)
    {
        var query = SearchQuery(args);
        var page = ValidatePage(firstPage, 100);
        var refined = false;
        if (page.Messages.Length == 0 && page.Next.Length == 0 && MatchMode(args) == "topic"
            && (!args.TryGetValue("pageToken", out var token) || string.IsNullOrEmpty(token.GetString()))
            && BroaderTopicQuery(query) is { } broader)
        {
            await authority(ct); ct.ThrowIfCancellationRequested();
            var refinedArgs = args.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            refinedArgs["query"] = JsonSerializer.SerializeToElement(broader);
            var plan = ConnectorOperations.Plan("mail_search", refinedArgs, "");
            firstPage = await provider.RequestAsync(account, plan.Endpoint, "GET", null, ct);
            ValidatePage(firstPage, 100); query = broader; refined = true;
        }
        await authority(ct); ct.ThrowIfCancellationRequested();
        var enriched = await ConnectorOperations.MailSearchMetadataAsync(provider, account, firstPage,
            ConnectorOperations.MailMetadataLimit(args), ct, ReadMode(args));
        var result = enriched.EnumerateObject().ToDictionary(pair => pair.Name, pair => (object)pair.Value.Clone());
        result["queryUsed"] = query;
        result["refinedEmptySearch"] = refined;
        result["topicMatchVerified"] = false; // Provider matching is not semantic classification.
        return JsonSerializer.SerializeToElement(result);
    }

    internal static async Task<JsonElement> CountAsync(IConnectorProvider provider, string account,
        JsonElement firstPage, IReadOnlyDictionary<string, JsonElement> args,
        Func<CancellationToken, Task> authority, CancellationToken ct)
    {
        var maximum = CountPageLimit(args);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var samples = new List<JsonElement>(); var pages = 0; var reason = "";
        var next = ""; var raw = firstPage;
        var fromBeginning = !args.TryGetValue("pageToken", out var initial) || string.IsNullOrEmpty(initial.GetString());
        if (!fromBeginning) tokens.Add(initial.GetString()!);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = ValidatePage(raw, 500);
            foreach (var message in page.Messages)
                if (ids.Add(message.GetProperty("id").GetString()!) && samples.Count < 6) samples.Add(message.Clone());
            pages++; next = page.Next;
            if (next.Length == 0) break;
            if (!tokens.Add(next)) { reason = "pagination_cycle"; break; }
            if (pages >= maximum) { reason = "page_window_limit"; break; }
            try
            {
                await authority(deadline.Token); deadline.Token.ThrowIfCancellationRequested();
                var continued = args.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                continued["pageToken"] = JsonSerializer.SerializeToElement(next);
                var plan = ConnectorOperations.Plan("mail_count", continued, "");
                var received = await provider.RequestAsync(account, plan.Endpoint, "GET", null, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                raw = received;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            { reason = "read_window_timeout"; break; }
            catch (ConnectorProviderException error) when (!ct.IsCancellationRequested && error.Code is "rate_limited" or "unavailable")
            { reason = deadline.IsCancellationRequested ? "read_window_timeout" : error.Code; break; }
        }
        ct.ThrowIfCancellationRequested();
        return JsonSerializer.SerializeToElement(new
        {
            queryUsed = args["query"].GetString(), matchedMessageCount = ids.Count,
            pagesRead = pages, complete = fromBeginning && next.Length == 0,
            windowComplete = next.Length == 0, startedFromBeginning = fromBeginning,
            nextPageToken = next, stoppedBecause = reason, sampleMessageReferences = samples,
            scope = "Distinct messages matching this exact query in the returned page window, not a verified semantic category or atomic mailbox snapshot. If complete=false this is a lower bound, not the total."
        });
    }

    private static (JsonElement[] Messages, string Next) ValidatePage(JsonElement page, int maximum)
    {
        if (page.ValueKind != JsonValueKind.Object || (page.TryGetProperty("messages", out var value)
            && value.ValueKind != JsonValueKind.Array)) throw new ConnectorProviderException("unavailable");
        var messages = page.TryGetProperty("messages", out value) ? value.EnumerateArray().ToArray() : [];
        if (messages.Length > maximum || messages.Any(message => !ValidReference(ComposioConnectorProvider.Text(message, "id"))
            || (ComposioConnectorProvider.Text(message, "threadId") is { Length: > 0 } thread && !ValidReference(thread))))
            throw new ConnectorProviderException("unavailable");
        var next = ComposioConnectorProvider.Text(page, "nextPageToken");
        if ((page.TryGetProperty("nextPageToken", out var token) && token.ValueKind != JsonValueKind.String)
            || next.Length > 2048 || next.Any(char.IsControl)) throw new ConnectorProviderException("unavailable");
        return (messages.Select(message => JsonSerializer.SerializeToElement(new
            { id = ComposioConnectorProvider.Text(message, "id"), threadId = ComposioConnectorProvider.Text(message, "threadId") })).ToArray(), next);
    }
    private static bool ValidReference(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant);
}
