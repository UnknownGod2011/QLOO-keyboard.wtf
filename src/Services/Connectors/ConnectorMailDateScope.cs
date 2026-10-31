namespace KeyboardWtf.Services.Connectors;

using System.Globalization;
using System.Text.RegularExpressions;

// Request-local calendar authority, not a new clock, planner or provider owner.
// Gmail calendar-string filters use a provider timezone, so today's human scope
// is frozen once using the originating local zone and expressed as Unix seconds.
internal sealed record ConnectorMailDateScope(DateTimeOffset RequestUtc, string TimeZoneId,
    string LocalDay, long AfterUnixSeconds, long BeforeUnixSeconds)
{
    internal string Query => $"after:{AfterUnixSeconds} before:{BeforeUnixSeconds}";
    internal bool CanInspectWholeDay { get; init; }
    internal string RelativeDay { get; init; } = "today";
    internal object Prompt => new { relativeDay = RelativeDay, requestUtc = RequestUtc, timeZoneId = TimeZoneId,
        localDay = LocalDay, afterUnixSeconds = AfterUnixSeconds, beforeUnixSeconds = BeforeUnixSeconds, query = Query };

    internal static ConnectorMailDateScope Freeze(string request, DateTimeOffset requestUtc, string timeZoneId)
    {
        if (string.IsNullOrEmpty(request)) return null;
        // Literal query text (including subject:"today") cannot invent a human
        // calendar instruction. Its actual prefix can still explicitly say today.
        var intent = ConnectorReadReasoner.TryLiteralMailQuery(request, out _, out var prefix) ? prefix : request;
        intent = Regex.Replace(intent, @"https?://[^\s<>""']+|""(?:[^""\\]|\\.)*""|“[^”]*”", " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
        if (Regex.IsMatch(intent, @"\btoday(?:['’]s)?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)))
            return FromRelativeDay(request, requestUtc, timeZoneId, 0);
        if (!Regex.IsMatch(intent, @"\byesterday(?:['’]s)?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50))) return null;
        if (ConnectorReadScope.ParseRelativeDay(request) is ConnectorReadRelativeDay.Ambiguous or ConnectorReadRelativeDay.Unsupported)
            throw new ArgumentException("The requested Gmail day is ambiguous; no day-scoped read ran.");
        return FromRelativeDay(request, requestUtc, timeZoneId, -1);
    }

    internal static ConnectorMailDateScope FromRelativeDay(string request, DateTimeOffset requestUtc, string timeZoneId, int dayOffset)
    {
        if (requestUtc == default || string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("The Gmail day read needs its frozen request clock and local timezone.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("The originating timezone could not be verified for this Gmail read."); }
        return FromLocalDay(request, requestUtc, zone, dayOffset);
    }

    // The zone overload permits deterministic synthetic DST-boundary tests;
    // runtime callers use the original, resolved timezone ID above.
    internal static ConnectorMailDateScope FromLocalDay(string request, DateTimeOffset requestUtc, TimeZoneInfo zone, int dayOffset)
    {
        if (requestUtc == default || zone is null || dayOffset is not (0 or -1))
            throw new ArgumentException("Only the frozen local today or yesterday is supported.");
        var day = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(requestUtc, zone).Date.AddDays(dayOffset), DateTimeKind.Unspecified);
        var nextDay = day.AddDays(1);
        if (zone.IsInvalidTime(day) || zone.IsAmbiguousTime(day) || zone.IsInvalidTime(nextDay) || zone.IsAmbiguousTime(nextDay))
            throw new ArgumentException("A local midnight could not be mapped uniquely; the Gmail read scope was unchanged.");
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day, zone));
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextDay, zone));
        var scope = new ConnectorMailDateScope(requestUtc, zone.Id, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds()) { RelativeDay = dayOffset == 0 ? "today" : "yesterday" };
        // Explicit human filters stay literal; conflicting filters are corrected
        // by the human, never silently replaced by an inferred calendar window.
        var humanTokens = Tokens(request, false);
        scope.ValidateDates(humanTokens);
        return scope with { CanInspectWholeDay = !ConnectorReadReasoner.TryLiteralMailQuery(request, out _, out _)
            && !humanTokens.Any(token => Regex.IsMatch(token.Value, @"^-?(?:from|to|cc|bcc|subject|label|category|has|list|filename|in|is|deliveredto|rfc822msgid|header):",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50))) };
    }

    internal static void ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new ArgumentException("A bounded Gmail query is required.");
        _ = Tokens(query, true);
    }

    // Provider grammar, never person/phrase-specific intent. Literal user queries
    // remain opaque. Source: Gmail Help search operators (answer/7190).
    private static readonly HashSet<string> SearchOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "to", "cc", "bcc", "subject", "after", "before", "older", "newer", "older_than", "newer_than",
        "label", "category", "has", "list", "filename", "in", "is", "deliveredto", "size", "larger", "smaller", "rfc822msgid", "header"
    };

    internal static void ValidateInferredQuery(string query)
    {
        ValidateQuery(query);
        foreach (var token in Tokens(query, true))
        {
            var value = token.Value.TrimStart('-');
            var colon = value.IndexOf(':');
            if (colon <= 0 || !value[..colon].All(c => char.IsAsciiLetter(c) || c == '_')) continue;
            // URLs are ordinary search text, not Gmail fields. Quoted literal
            // text and explicit human queries are also preserved unchanged.
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") continue;
            if (!SearchOperators.Contains(value[..colon]))
                throw new ArgumentException("Unsupported Gmail operator '" + value[..colon] + ":'. Use sender/receivedOn fields, or valid Gmail filters such as from:, subject:, after: and before:. Quote literal text containing a colon. Preserve the other requested constraints.");
        }
    }

    internal string Apply(string query, bool literal)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new ArgumentException("A bounded Gmail query is required for the requested day scope.");
        var tokens = Tokens(query, true);
        ValidateDates(tokens);
        if (literal)
        {
            // A literal expression is never rewritten. Its day constraints must
            // already be compulsory AND filters, not an OR branch or quoted text.
            if (!HasBothBounds(tokens.Where(token => token.Depth == 0))
                || tokens.Any(token => token.Depth == 0 && token.Value.Equals("OR", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The literal Gmail query does not preserve the requested local day scope. Supply compatible exact date filters.");
            return query;
        }
        if (IsWholeDayQuery(tokens)) return Query;
        var value = query.Trim();
        var suffix = " " + Query;
        // This is the exact shape we produce. Removing only our outer wrapper
        // makes application idempotent across controller, reader and cursors.
        if (value.EndsWith(suffix, StringComparison.Ordinal))
        {
            var wrapped = value[..^suffix.Length].Trim();
            if (HasSingleOuterGroup(wrapped)) value = wrapped[1..^1].Trim();
        }
        var bounded = "(" + value + ") " + Query;
        if (bounded.Length > 2000) throw new ArgumentException("The Gmail query cannot retain the requested day scope within its bound.");
        return bounded;
    }

    internal bool IsWholeDayQuery(string query) => IsWholeDayQuery(Tokens(query, true));
    private bool IsWholeDayQuery(List<QueryToken> tokens) => HasBothBounds(tokens)
        && tokens.All(token => DateField(token.Value, out _, out _, out _)
            || token.Value.Equals("AND", StringComparison.OrdinalIgnoreCase));
    private bool HasBothBounds(IEnumerable<QueryToken> tokens)
    {
        var start = false; var end = false;
        foreach (var token in tokens)
            if (DateField(token.Value, out var field, out var value, out var negative) && !negative
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                start |= (field is "after" or "newer") && seconds == AfterUnixSeconds;
                end |= (field is "before" or "older") && seconds == BeforeUnixSeconds;
            }
        return start && end;
    }
    private void ValidateDates(IEnumerable<QueryToken> tokens)
    {
        foreach (var token in tokens)
        {
            if (!DateField(token.Value, out var field, out var value, out var negative)) continue;
            var valid = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds);
            var compatible = !negative && valid && ((field is "after" or "newer") && seconds == AfterUnixSeconds
                || (field is "before" or "older") && seconds == BeforeUnixSeconds);
            if (!compatible)
                throw new ArgumentException("A Gmail date filter conflicts with the frozen local day scope. Use " + Query + "; no conflicting search ran.");
        }
    }
    private static bool DateField(string token, out string field, out string value, out bool negative)
    {
        negative = token.StartsWith('-'); var text = negative ? token[1..] : token;
        var colon = text.IndexOf(':');
        field = colon > 0 ? text[..colon].ToLowerInvariant() : ""; value = colon > 0 ? text[(colon + 1)..] : "";
        return field is "after" or "before" or "newer" or "older" or "newer_than" or "older_than";
    }
    private sealed record QueryToken(string Value, int Depth);
    private static List<QueryToken> Tokens(string value, bool strict)
    {
        var result = new List<QueryToken>(); var groups = new Stack<char>(); var quoted = false; var escaped = false;
        var start = -1; var depth = 0;
        void Flush(int end)
        { if (start >= 0) { result.Add(new(value[start..end], depth)); start = -1; } }
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (strict && char.IsControl(character)) throw new ArgumentException("The scoped Gmail query contains invalid control text.");
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
                continue;
            }
            if (character == '"') { if (start < 0) { start = index; depth = groups.Count; } quoted = true; continue; }
            if (char.IsWhiteSpace(character)) { Flush(index); continue; }
            if (character is '(' or '{')
            {
                Flush(index); groups.Push(character);
                if (strict && groups.Count > 20) throw new ArgumentException("The scoped Gmail query has oversized grouping.");
                continue;
            }
            if (character is ')' or '}')
            {
                Flush(index);
                if (groups.Count == 0 || groups.Peek() != (character == ')' ? '(' : '{'))
                { if (strict) throw new ArgumentException("The scoped Gmail query has invalid grouping."); }
                else groups.Pop();
                continue;
            }
            if (start < 0) { start = index; depth = groups.Count; }
        }
        Flush(value.Length);
        if (strict && (quoted || groups.Count > 0)) throw new ArgumentException("The scoped Gmail query has incomplete quoting or grouping.");
        return result;
    }
    private static bool HasSingleOuterGroup(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')') return false;
        var depth = 0; var quoted = false; var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quoted)
            { if (escaped) escaped = false; else if (character == '\\') escaped = true; else if (character == '"') quoted = false; continue; }
            if (character == '"') { quoted = true; continue; }
            if (character == '(') depth++;
            if (character == ')' && --depth == 0) return index == value.Length - 1;
        }
        return false;
    }
}
