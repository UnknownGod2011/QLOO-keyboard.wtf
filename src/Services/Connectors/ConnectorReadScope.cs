namespace KeyboardWtf.Services.Connectors;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal enum ConnectorReadRelativeDay { None, Today, Yesterday, Ambiguous, Unsupported, Unrecognized }
internal enum ConnectorReadScopeChangeKind { Applied, Unchanged, AmbiguousDay, UnsupportedDay, LiteralDayConflict, InvalidLocalDay, InvalidQuery }
internal sealed record ConnectorReadScopeChange(ConnectorReadScope Scope, ConnectorReadScopeChangeKind Kind)
{
    internal bool Accepted => Kind is ConnectorReadScopeChangeKind.Applied or ConnectorReadScopeChangeKind.Unchanged;
}

// Pure value proposed for an already-authorized human-owned read. The existing
// controller remains the only authority for identity, revision and admission.
// It supplies prior accepted guidance; this value does not create another log.
internal sealed record ConnectorReadScope
{
    internal string OriginalRequest { get; }
    internal DateTimeOffset RequestUtc { get; }
    internal string TimeZoneId { get; }
    internal string LiteralQuery { get; }
    internal ConnectorMailDateScope DateScope { get; }
    internal bool MetadataOnly { get; }
    internal string SemanticGuidance { get; private init; } = "";
    internal string RetrievalIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        requestUtc = RequestUtc.ToUniversalTime().ToString("O"), timeZoneId = TimeZoneId,
        literalQuery = LiteralQuery, dayQuery = DateScope?.Query, semanticGuidance = SemanticGuidance
    }))));
    internal string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        requestUtc = RequestUtc.ToUniversalTime().ToString("O"), timeZoneId = TimeZoneId,
        literalQuery = LiteralQuery, dayQuery = DateScope?.Query, wholeDayEligible = DateScope?.CanInspectWholeDay, metadataOnly = MetadataOnly, semanticGuidance = SemanticGuidance
    }))));

    private ConnectorReadScope(string original, DateTimeOffset requestUtc, string timeZoneId,
        string literalQuery, ConnectorMailDateScope dateScope, bool metadataOnly)
    {
        OriginalRequest = original; RequestUtc = requestUtc; TimeZoneId = timeZoneId;
        LiteralQuery = literalQuery; DateScope = dateScope; MetadataOnly = metadataOnly;
    }

    internal static ConnectorReadScope Freeze(string original, DateTimeOffset requestUtc, string timeZoneId)
    {
        if (original?.Length is not (> 0 and <= 3000)) throw new ArgumentException("A bounded originating read is required.");
        var day = ParseRelativeDay(original);
        // Unsupported original natural dates keep the existing native/model
        // planning path. Only active-task replacements use the narrow grammar.
        // Keep the existing original-day parser as its source of truth. The
        // narrower positive grammar applies to refinements and new yesterday
        // support; it cannot discard an already-owned today restriction.
        var date = day == ConnectorReadRelativeDay.Yesterday
            ? ConnectorMailDateScope.FromRelativeDay(original, requestUtc, timeZoneId, -1)
            : ConnectorMailDateScope.Freeze(original, requestUtc, timeZoneId);
        if (day == ConnectorReadRelativeDay.Unrecognized && date is null)
            throw new ArgumentException("The explicit originating relative day could not be preserved; no unscoped read was allowed.");
        // Preserve the originating product parser's exact restriction, including
        // Summarize/Explain/question prefixes. The additional refinement guard
        // below must never weaken this already-frozen human authority.
        var literal = ConnectorReadReasoner.TryLiteralMailQuery(original, out var query, out _) ? query : null;
        ValidateLiteral(literal, date);
        return new(original, requestUtc, timeZoneId, literal, date, ConnectorMailContentPermission.MetadataOnly(original, []));
    }

    internal ConnectorReadScopeChange Refine(string observedHumanInstruction, IEnumerable<string> priorAcceptedInstructions)
    {
        if (observedHumanInstruction?.Length is not (> 0 and <= 3000))
            return new(this, ConnectorReadScopeChangeKind.InvalidQuery);
        var day = ParseRelativeDay(observedHumanInstruction);
        if (day == ConnectorReadRelativeDay.Ambiguous) return new(this, ConnectorReadScopeChangeKind.AmbiguousDay);
        if (day is ConnectorReadRelativeDay.Unsupported or ConnectorReadRelativeDay.Unrecognized)
            return new(this, ConnectorReadScopeChangeKind.UnsupportedDay);
        var date = DateScope;
        if (day is ConnectorReadRelativeDay.Today or ConnectorReadRelativeDay.Yesterday)
        {
            var dayInstruction = ConnectorReadReasoner.TryLiteralMailQuery(observedHumanInstruction, out _, out var literalPrefix)
                ? literalPrefix : observedHumanInstruction;
            try { date = ConnectorMailDateScope.FromRelativeDay(dayInstruction, RequestUtc, TimeZoneId,
                day == ConnectorReadRelativeDay.Today ? 0 : -1); }
            catch (ArgumentException) { return new(this, ConnectorReadScopeChangeKind.InvalidLocalDay); }
        }
        var changedLiteral = TryHumanLiteralQuery(observedHumanInstruction, out var query, out var refusedLiteral);
        if (refusedLiteral) return new(this, ConnectorReadScopeChangeKind.InvalidQuery);
        var literal = changedLiteral ? query : LiteralQuery;
        try { ValidateLiteral(literal, date); }
        catch (ArgumentException) { return new(this, date is null ? ConnectorReadScopeChangeKind.InvalidQuery : ConnectorReadScopeChangeKind.LiteralDayConflict); }
        if (date is not null)
            date = date with { CanInspectWholeDay = literal is null && date.CanInspectWholeDay && DateScope?.CanInspectWholeDay != false };
        var metadataOnly = ConnectorMailContentPermission.MetadataOnly(OriginalRequest,
            (priorAcceptedInstructions ?? []).Append(observedHumanInstruction));
        var semantic = SemanticGuidance;
        if (day == ConnectorReadRelativeDay.None && !changedLiteral && metadataOnly == MetadataOnly
            && !ConnectorMailContentPermission.MetadataOnly(observedHumanInstruction, [])
            && Match(HumanIntent(observedHumanInstruction), @"^\s*(?:actually[,\s]+)?(?:only|just)\b")
            && Match(HumanIntent(observedHumanInstruction), @"\b(?:emails?|messages?|from|responses?|replies)\b"))
            semantic = string.Join("\n", (priorAcceptedInstructions ?? []).Append(observedHumanInstruction));
        var next = new ConnectorReadScope(OriginalRequest, RequestUtc, TimeZoneId, literal, date, metadataOnly)
            { SemanticGuidance = semantic };
        return next.Identity == Identity ? new(this, ConnectorReadScopeChangeKind.Unchanged)
            : new(next, ConnectorReadScopeChangeKind.Applied);
    }

    internal ConnectorReadScope RestrictToMetadata() => new(OriginalRequest, RequestUtc, TimeZoneId, LiteralQuery, DateScope, true)
        { SemanticGuidance = SemanticGuidance };

    internal string Apply(string query)
    {
        if (LiteralQuery is not null && !string.Equals(query, LiteralQuery, StringComparison.Ordinal))
            throw new ArgumentException("This search differs from the accepted exact human query.");
        if (LiteralQuery is null) ConnectorMailDateScope.ValidateInferredQuery(query);
        if (DateScope is not null) return DateScope.Apply(query, LiteralQuery is not null);
        ConnectorMailDateScope.ValidateQuery(query);
        return query;
    }

    private static void ValidateLiteral(string literal, ConnectorMailDateScope date)
    {
        if (literal is null) return;
        if (date is null) ConnectorMailDateScope.ValidateQuery(literal);
        else _ = date.Apply(literal, true); // Validation only: preserve every supplied byte.
    }

    private static bool TryHumanLiteralQuery(string instruction, out string query, out bool refused)
    {
        query = null; refused = false;
        if (!ConnectorReadReasoner.TryLiteralMailQuery(instruction, out var value, out var prefix)) return false;
        var intent = HumanIntent(prefix);
        // A query boundary inside a quote or explanatory/provider sentence is
        // not an explicit replacement instruction. Inner query text is opaque.
        if (HasUnclosedQuote(intent)
            || Match(intent, @"\b(?:if|unless|until|after|before|once|when|later|not|never|don't|do\s+not)\b")
            || !Match(intent, @"^\s*(?:(?:actually|please|now|instead)\b[\s,]*)*(?:(?:can|could|would)\s+you\s+)?(?:search|count|find|read|check|show|list|summarize|explain|use|replace|change|update|only|just|today|yesterday|(?:exact|literal)\s+query|query)\b"))
        { refused = true; return false; }
        query = value;
        return true;
    }

    internal static ConnectorReadRelativeDay ParseRelativeDay(string humanInstruction)
    {
        if (humanInstruction?.Length is not (> 0 and <= 3000)) return ConnectorReadRelativeDay.None;
        var prefix = ConnectorReadReasoner.TryLiteralMailQuery(humanInstruction, out _, out var literalPrefix)
            ? literalPrefix : humanInstruction;
        var intent = HumanIntent(prefix);
        var days = Regex.Matches(intent, @"\b(?:today|yesterday)(?:['’]s)?\b", Options, Timeout);
        if (days.Count == 0)
            return HasUnsupportedCalendarInstruction(intent)
                ? ConnectorReadRelativeDay.Unsupported : ConnectorReadRelativeDay.None;
        if (days.Count != 1) return ConnectorReadRelativeDay.Ambiguous;
        var clauses = Regex.Split(intent, @"[,;!?\r\n]+|\.(?:\s|$)", Options, Timeout);
        if (clauses.Length > 32) return ConnectorReadRelativeDay.Ambiguous;
        var clauseIndex = Array.FindIndex(clauses, value => Match(value, @"\b(?:today|yesterday)(?:['’]s)?\b"));
        var clause = clauses[clauseIndex];
        var dayInClause = Regex.Match(clause, @"\b(?:today|yesterday)(?:['’]s)?\b", Options, Timeout);
        // One positive instruction only; neither a choice nor a negative,
        // condition, explanatory mention or unclosed quote changes authority.
        // Negation after a positive day that restricts body access does not
        // negate the calendar instruction; content uses the existing singleton.
        if (HasUnclosedQuote(intent) || Match(intent, UnsupportedCalendar)
            || clauses.Take(clauseIndex).Any(value => Match(value, @"\b(?:not|never|no|don't|do\s+not|without|except|exclude)\b")
                && !ExclusiveBodyRestriction(value))
            || Match(clause[..dayInClause.Index], @"\b(?:not|never|no|don't|do\s+not|without|except|exclude)\b")
            || Match(intent, @"\b(?:if|unless|until|after|before|once|when|later)\b")
            || Match(intent, @"\b(?:about|mention|mentioned|quote|quoted|contains|said|says)\b"))
            return ConnectorReadRelativeDay.Ambiguous;
        const string start = @"^\s*(?:(?:actually|please|now|instead)\b[\s,]*)*(?:(?:can|could|would)\s+you\s+)?";
        const string day = @"(?:today|yesterday)(?:['’]s)?";
        const string mail = @"(?:emails?|mail|messages?|inbox|responses?|replies|reply|replied|responded|correspondence)\b";
        var positive = HasLeadingDayMailInstruction(clause, start, mail, day)
            || HasBoundedMailDayInstruction(clause, start, @"(?:check|find|read|inspect|show|list|count|summarize|search)", mail, day, true)
            || HasBoundedMailDayInstruction(clause, start, @"(?:did|has|have|was|were|is|are)", mail, day, false)
            // The existing owner already proves an active Gmail read. A clear
            // day correction need not repeat a mail noun or its original goal.
            || Match(clause, start + @"(?:only|just|use|check)\s+" + day + @"\s*(?:instead|only|please)?\s*$")
            || Match(clause, @"^\s*(?:(?:actually|please|now|instead)\b[\s,]+)+" + day + @"\s*(?:instead|only|please)?\s*$")
            || Match(clause, @"^\s*" + day + @"\s*(?:instead|only|please)?\s*$")
                && clauses.Take(clauseIndex).Any(value => Match(value, @"^\s*(?:actually|please|now|instead)\s*$"));
        if (!positive) return ConnectorReadRelativeDay.Unrecognized;
        return days[0].Value.StartsWith("today", StringComparison.OrdinalIgnoreCase)
            ? ConnectorReadRelativeDay.Today : ConnectorReadRelativeDay.Yesterday;
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const string UnsupportedCalendar = @"\b(?:tomorrow|last\s+(?:day|week|month|year|night|monday|tuesday|wednesday|thursday|friday|saturday|sunday)|(?:one|two|three|four|five|six|seven|\d{1,3})\s+days?\s+ago|(?:this|next)\s+(?:week|month|year))\b";
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    private static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern, Options, Timeout);
    private static bool HasLeadingDayMailInstruction(string clause, string start, string mail, string day)
    {
        // Preserve the leading day plus zero-to-three ASCII modifier words.
        // Matching the mail noun separately avoids the symbolic node product.
        var leading = Regex.Match(clause, start
            + @"(?:(?:only|just)\s+)?(?:(?:check|find|read|inspect|show|list|count|summarize|search|use)\s+(?:(?:only|just)\s+)?)?" + day,
            Options, Timeout);
        if (!leading.Success) return false;
        var leadingEnd = leading.Index + leading.Length;
        foreach (Match noun in Regex.Matches(clause, @"\b" + mail, Options, Timeout))
        {
            var gap = noun.Index - leadingEnd;
            if (gap >= 0 && Match(clause.Substring(leadingEnd, gap), @"^\s+(?:[A-Za-z]+\s+){0,3}$")) return true;
        }
        return false;
    }
    private static bool HasUnsupportedCalendarInstruction(string intent)
    {
        // Keep the same grammar and 100-character gap without multiplying the
        // bounded gap by every calendar alternative in one automaton.
        var prefix = Regex.Match(intent, @"^\s*(?:(?:actually|please|now|instead)\b[\s,]*)*(?:(?:only|just)\s+)?", Options, Timeout);
        var prefixEnd = prefix.Index + prefix.Length;
        var command = Regex.Match(intent[prefixEnd..], @"^(?:check|find|read|show|list|count|summarize|search|use)\b", Options, Timeout);
        var commandEnd = prefixEnd + command.Length;
        foreach (Match calendar in Regex.Matches(intent, UnsupportedCalendar, Options, Timeout))
        {
            if (calendar.Index == prefixEnd) return true;
            var gap = calendar.Index - commandEnd;
            if (command.Success && gap is >= 0 and <= 100
                && !intent.AsSpan(commandEnd, gap).Contains('\r') && !intent.AsSpan(commandEnd, gap).Contains('\n')) return true;
        }
        return false;
    }
    private static bool HasBoundedMailDayInstruction(string clause, string start, string command, string mail, string day, bool allowPoliteSuffix)
    {
        // The original two gaps remain 160 and 100 characters. Separate the
        // leading command, observed mail noun and trailing day so the engine
        // retains NonBacktracking and its existing complexity ceiling.
        var leading = Regex.Match(clause, start + command + @"\b", Options, Timeout);
        if (!leading.Success) return false;
        var leadingEnd = leading.Index + leading.Length;
        var suffix = @"^[^\r\n]{0,100}\b" + day
            + (allowPoliteSuffix ? @"\s*(?:instead|only|please)?[.!?\s]*$" : @"\s*[.!?\s]*$");
        foreach (Match noun in Regex.Matches(clause, @"\b" + mail, Options, Timeout))
        {
            var gap = noun.Index - leadingEnd;
            if (gap is < 0 or > 160 || clause.AsSpan(leadingEnd, gap).Contains('\r') || clause.AsSpan(leadingEnd, gap).Contains('\n')) continue;
            if (Match(clause[(noun.Index + noun.Length)..], suffix)) return true;
        }
        return false;
    }
    private static bool ExclusiveBodyRestriction(string clause)
        // Syntactic boundary only: the whole clause must be one body directive,
        // and the existing singleton alone decides actual content permission.
        // "No bodies and do not" must not exempt its unrelated negation.
        => Match(clause, @"^\s*(?:please\s+)?(?:(?:no|without|skip|avoid)\s+"
            + @"(?:(?:any|more|the|actual|full|message|email)\s+)*(?:bodies|body|content)"
            + @"|(?:do\s+not|don't|never)\s+(?:read(?:ing)?|fetch(?:ing)?|inspect(?:ing)?)\s+"
            + @"(?:(?:any|more|the|actual|full|message|email)\s+)*(?:bodies|body|content))\s*$")
            && ConnectorMailContentPermission.MetadataOnly(clause, []);
    private static bool HasUnclosedQuote(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is '"' or '“' or '”' or '‘') return true;
            if ((value[index] is '\'' or '’') && !(index > 0 && index + 1 < value.Length
                && char.IsLetterOrDigit(value[index - 1]) && char.IsLetterOrDigit(value[index + 1]))) return true;
        }
        return false;
    }
    private static string HumanIntent(string value) => Regex.Replace(value ?? "",
        @"https?://[^\s<>""']+|""(?:[^""\\]|\\.)*""|“[^”]*”|‘[^’]*’|(?:^|[^\w])'[^'\r\n]*'(?:$|[^\w])", " ", Options, Timeout);
}
