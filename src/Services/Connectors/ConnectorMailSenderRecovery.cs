namespace KeyboardWtf.Services.Connectors;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// One candidate query after an empty inferred sender/participant lookup. It has no task,
// account, paging or execution authority; the existing bounded reader owns I/O.
internal sealed record ConnectorMailSenderRecovery(string Name, string Query, string[] Names)
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    internal bool ParticipantLookup { get; init; }

    internal static ConnectorMailSenderRecovery Create(string query)
    {
        if (query?.Length is not (> 0 and <= 1600) || query.Any(char.IsControl)) return null;
        // Only the canonical single-person expression produced by native
        // preparation. Incoming sender candidates are valid conversations with
        // that person, but are not complete recipient-spelling coverage.
        var participant = Regex.Match(query,
            "^\\{from:\"(?<from>[^\"]+)\" to:\"(?<to>[^\"]+)\" cc:\"(?<cc>[^\"]+)\"\\}$", Options, Timeout);
        if (participant.Success && participant.Groups["from"].Value == participant.Groups["to"].Value
            && participant.Groups["from"].Value == participant.Groups["cc"].Value)
        {
            var recovery = Create("from:\"" + participant.Groups["from"].Value + "\"");
            return recovery is null ? null : recovery with { ParticipantLookup = true };
        }
        // Only one positive sender in an AND expression. Never reinterpret a
        // quoted body/subject, OR branch, exact address, domain or negation.
        var tokens = Regex.Matches(query, "(?:[+-]?[A-Za-z][A-Za-z0-9_-]*:)?(?:\"[^\"]*\"|[^\\s(){}\"]+)|[()]", Options, Timeout);
        if (Regex.Replace(string.Concat(tokens.Select(t => t.Value)), @"\s", "", Options, Timeout)
            != Regex.Replace(query, @"\s", "", Options, Timeout)
            || tokens.Any(t => t.Value.Equals("OR", StringComparison.OrdinalIgnoreCase)
                || t.Value.Equals("NOT", StringComparison.OrdinalIgnoreCase) || t.Value.Contains('|'))) return null;
        var senders = tokens.Where(t => t.Value.StartsWith("from:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (senders.Length != 1) return null;
        var sender = senders[0];
        var name = sender.Value[5..].Trim('"');
        if (name.Length > 80 || !Regex.IsMatch(name, @"^[\p{L}][\p{L}\p{M}'’-]*(?: [\p{L}][\p{L}\p{M}'’-]*){0,3}$", Options, Timeout)) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var plain = Normalize(name);
        if (plain != name.ToLowerInvariant()) names.Add(plain);
        // Character edits are language/name independent. Repeated letters and
        // adjacent transpositions cover common spelling/transcription errors;
        // unfamiliar phonetics still use the existing model refinement.
        for (var index = 0; index < plain.Length && names.Count < 24; index++)
        {
            if (!char.IsLetter(plain[index])) continue;
            if (plain.Length >= 5)
            {
                names.Add(plain.Insert(index, plain[index].ToString()));
                if (index + 1 < plain.Length && plain[index] == plain[index + 1]) names.Add(plain.Remove(index, 1));
                if (index + 1 < plain.Length && char.IsLetter(plain[index + 1]) && plain[index] != plain[index + 1])
                    names.Add(plain[..index] + plain[index + 1] + plain[index] + plain[(index + 2)..]);
            }
        }
        var candidates = names.Take(24).ToArray();
        // Broad text may discover aliases/body references, but only independently
        // returned matching From headers survive Filter below.
        var alternative = "{\"" + name + "\" " + string.Join(" ", candidates.Select(value => "from:\"" + value + "\"")) + "}";
        var result = query[..sender.Index] + alternative + query[(sender.Index + sender.Length)..];
        return result.Length <= 2000 ? new(name, result, candidates) : null;
    }

    internal JsonElement Filter(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object || Text(reply, "state") != "completed"
            || !reply.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
            || !reply.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return reply;
        var references = messages.EnumerateArray().Take(100).ToArray();
        var samples = data.TryGetProperty("sampledMessages", out var sampleArray) && sampleArray.ValueKind == JsonValueKind.Array
            ? sampleArray.EnumerateArray().Take(100).ToArray() : [];
        var observed = samples.Where(sample => references.Any(reference =>
            Text(reference, "id").Length > 0 && Text(reference, "id") == Text(sample, "id")
            && Text(reference, "threadId") == Text(sample, "threadId"))).ToArray();
        string Header(JsonElement sample)
        {
            var from = Text(sample, "from");
            if (from.Length > 0) return from;
            return sample.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array
                ? headers.EnumerateArray().Where(h => Text(h, "name").Equals("From", StringComparison.OrdinalIgnoreCase))
                    .Select(h => Text(h, "value")).FirstOrDefault() ?? "" : "";
        }
        var accepted = observed.Where(sample => MatchesHeader(Header(sample))).ToArray();
        var ids = accepted.Select(sample => Text(sample, "id")).ToHashSet(StringComparer.Ordinal);
        var completeHeaders = messages.GetArrayLength() <= 100
            && references.All(reference => observed.Any(sample => Text(reference, "id") == Text(sample, "id") && Header(sample).Length > 0));
        var copy = JsonNode.Parse(reply.GetRawText());
        var output = copy["result"]["data"].AsObject();
        output["messages"] = JsonSerializer.SerializeToNode(references.Where(reference => ids.Contains(Text(reference, "id"))).ToArray());
        output["sampledMessages"] = JsonSerializer.SerializeToNode(accepted);
        output["pageItemCount"] = ids.Count;
        output["sourceMetadataSample"] = output["metadataSample"]?.DeepClone();
        output["metadataSample"] = JsonSerializer.SerializeToNode(new { requested = ids.Count, returned = accepted.Length, complete = completeHeaders });
        output["senderRecovery"] = JsonSerializer.SerializeToNode(new {
            requestedName = Name, sourcePageItemCount = messages.GetArrayLength(), candidateCount = ids.Count,
            interpretation = "Candidates verified against observed From headers, including close spellings. State the actual sender; do not claim an exact spelling match. Multiple senders or threads may require clarification. This bounded page is not a mailbox total."
                + (ParticipantLookup ? " These are incoming conversation candidates; recipient spelling variations have not been exhaustively searched." : "")
        });
        if (!completeHeaders || ParticipantLookup) copy["result"]["truncated"] = true;
        return JsonSerializer.SerializeToElement(copy);
    }

    internal bool MatchesHeader(string header)
    {
        if (header?.Length is not (> 0 and <= 1000)) return false;
        // Compare display-name/local-part tokens, never a body, subject or the
        // sender's domain (a domain coincidence is not a person match).
        var identity = Regex.Replace(header, @"@[\p{L}\p{N}_.-]+", "", Options, Timeout);
        var words = Words(identity);
        return Names.Any(name => Words(name).All(word => words.Contains(word)));
    }

    private static string[] Words(string value) => Regex.Matches(Normalize(value), @"[\p{L}\p{N}]+", Options, Timeout)
        .Select(match => match.Value).ToArray();
    private static string Normalize(string value) => new string(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
        .ToArray()).ToLowerInvariant().Normalize(NormalizationForm.FormC);
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
}
