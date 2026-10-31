namespace KeyboardWtf.Services.Connectors;

using System.Text.RegularExpressions;
using System.Collections.Concurrent;

// Only anaphoric, current human requests. Provider names alone never enter here.
internal enum ConnectorOpenSelection { Single, Both, All }
internal enum ConnectorMailSelection { Unspecified, Sent, Received }

internal static class ConnectorFollowupIntent
{
    private const string Courtesy = @"(?:(?:please|pls)\s+)?(?:(?:can|could|would)\s+(?:you\s+)?)?(?:(?:please|pls)\s+)?";
    private const string Object = @"(?:it|(?:that|this|the|its)\s+(?:email|message|thread|conversation|document|file|repo|repository|issue|pull\s+request|event))";
    private const string PluralNoun = @"(?:emails|messages|threads|conversations|documents|files|repos|repositories|issues|pull\s+requests|events)";
    private const string OpeningPresentation = @"\s+(?:separately(?:\s+(?:in|on)\s+(?:(?:a|the)\s+)?(?:new\s+)?(?:tabs|windows))?|(?:in|on)\s+(?:(?:(?:a|the)\s+)?new\s+(?:tab|window|thread)|(?:(?:separate\s+)?new\s+|separate\s+)?(?:tabs|windows)|(?:the\s+)?browser))(?:\s+again)?[.!?]?\s*$";
    // Only the fixed patterns in this class enter this cache. The global Regex
    // cache is small and shared with ordinary Jarvis; repeated retained-object
    // checks must not rebuild NonBacktracking automata on that hot path.
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);
    private static Regex Pattern(string value) => Patterns.GetOrAdd(value, static pattern => new Regex(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)));
    private static bool Match(string text, string pattern) => text?.Length is > 0 and <= 3000
        && Pattern(pattern).IsMatch(text);

    internal static bool IsExactOpen(string request) => TryParseExactOpen(request, out _);

    internal static bool TryParseExactOpen(string request, out ConnectorOpenSelection selection)
    {
        selection = ConnectorOpenSelection.Single;
        if (request?.Length is not (> 0 and <= 3000)) return false;
        request = NormalizeMailReference(request).Trim().TrimEnd('.', '!', '?').TrimEnd();
        // Most normal requests have no retained-object pronoun/determiner.
        // Avoid constructing/matching connector regexes for service opening.
        var words = request.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (!words.Any(word =>
            word.TrimEnd('.', '!', '?', ',').ToLowerInvariant() is "it" or "that" or "this" or "the"
                or "both" or "all" or "them" or "those" or "these" or "email" or "message" or "thread" or "conversation")) return false;
        request = TrimOpeningWrappers(request);
        // Presentation never supplies or replaces an object destination.
        var presentation = Pattern(OpeningPresentation).Match(request);
        var destination = presentation.Success ? request[..presentation.Index] : request;
        if (Match(destination, @"^\s*" + Courtesy + @"(?:open|show(?:\s+me)?)\s+(?:that|this)\s*$")
            || Match(destination, @"^\s*" + Courtesy + @"(?:open|show(?:\s+me)?)\s+" + Object
            + @"(?:\s+(?:in\s+Gmail|you\s+(?:found|read)|we\s+just\s+discussed))?(?:\s+again)?[.!?]?\s*$")
            || Match(destination, @"^\s*" + Courtesy + @"pull\s+" + Object + @"\s+up(?:\s+again)?[.!?]?\s*$")
            // A bare mail-object noun is contextual only with an explicit
            // presentation modifier. Plain "open email" retains its old owner.
            || presentation.Success && Match(destination, @"^\s*" + Courtesy + @"(?:open|show(?:\s+me)?)\s+(?:email|message|thread|conversation)[.!?]?\s*$")) return true;
        // Parse the verb and presentation separately from the bounded object
        // phrase; combining all alternatives creates a needlessly large DFA.
        var action = Pattern(@"^\s*" + Courtesy + @"(?<verb>open|show(?:\s+me)?|pull)\s+(?<object>.+?)[.!?]?\s*$").Match(destination);
        if (!action.Success) return false;
        var objects = action.Groups["object"].Value;
        if (action.Groups["verb"].Value.Equals("pull", StringComparison.OrdinalIgnoreCase))
        {
            if (!Match(objects, @"\s+up\s*$")) return false;
            objects = Pattern(@"\s+up\s*$").Replace(objects, "");
        }
        else objects = Pattern(@"\s+you\s+(?:found|read)\s*$").Replace(objects, "");
        if (NamedMailObject(objects, out _, out _)) return true;
        if (Match(objects, @"^both(?:\s+(?:of\s+)?(?:them|those|these|the))?(?:\s+" + PluralNoun + @")?\s*$"))
        { selection = ConnectorOpenSelection.Both; return true; }
        if (Match(objects, @"^(?:them|(?:those|these|the)\s+" + PluralNoun + @")\s*$")
            || Match(objects, @"^all(?:\s+(?:of\s+)?(?:them|those|these|the))?(?:\s+" + PluralNoun + @")?\s*$"))
        { selection = ConnectorOpenSelection.All; return true; }
        return false;
    }

    private static string TrimOpeningWrappers(string request)
    {
        for (var wrapper = 0; wrapper < 2; wrapper++)
            request = Pattern(@"\s+(?:for\s+me|please|pls)[.!?]?\s*$").Replace(request, "");
        return request;
    }

    // Human selectors identify only previously verified metadata. They never
    // provide an account, native ID, URL or execution authority.
    internal static bool NamedMailOpen(string request, out string field, out string value)
    {
        field = value = null;
        if (request?.Length is not (> 0 and <= 3000)) return false;
        request = TrimOpeningWrappers(NormalizeMailReference(request));
        request = Pattern(OpeningPresentation).Replace(request, "");
        var action = Pattern(@"^\s*" + Courtesy + @"(?<verb>open|show(?:\s+me)?|pull)\s+(?<object>.+?)[.!?]?\s*$").Match(request);
        if (!action.Success) return false;
        var objects = action.Groups["object"].Value;
        if (action.Groups["verb"].Value.Equals("pull", StringComparison.OrdinalIgnoreCase))
        {
            if (!Match(objects, @"\s+up\s*$")) return false;
            objects = Pattern(@"\s+up\s*$").Replace(objects, "");
        }
        else objects = Pattern(@"\s+you\s+(?:found|read)\s*$").Replace(objects, "");
        return NamedMailObject(objects, out field, out value);
    }

    private static bool NamedMailObject(string text, out string field, out string value)
    {
        field = value = null;
        var match = Pattern(@"^(?:(?:the|that|this)\s+)?(?:email|message|thread|conversation)\s+(?<field>from|with|about)\s+(?<value>.+?)\s*$").Match(text);
        if (!match.Success)
        {
            match = Pattern(@"^(?<value>[\p{L}][\p{L}'’ .-]{0,99})['’]s\s+(?:email|message|thread|conversation)\s*$").Match(text);
            if (!match.Success) return false;
            field = "participant";
        }
        else field = match.Groups["field"].Value.ToLowerInvariant() switch { "about" => "subject", "from" => "sender", _ => "participant" };
        value = match.Groups["value"].Value.Trim();
        // Reject another goal, scope, literal URL or embedded command. A
        // participant is a short display name, never a guessed email address.
        if (value.Length is < 1 or > 160 || Pattern(@"[\r\n:;/<>\""?!]|\b(?:and|or|then|except|not|without|send|reply|delete|forward|account|today|yesterday|before|after|in\s+a|in\s+the)\b").IsMatch(value)
            || field is "participant" or "sender" && !Pattern(@"^[\p{L}][\p{L}'’.-]*(?:\s+[\p{L}][\p{L}'’.-]*){0,3}$").IsMatch(value))
        { field = value = null; return false; }
        return true;
    }

    internal static bool ObjectMatchesProvider(string request, string provider)
    {
        if (Match(request, @"\b(?:emails?|messages?|threads?|conversations?|Gmail)\b"))
            return provider is "gmail" or "slack" or "discord";
        if (Match(request, @"\b(?:repos?|repository|repositories|issues?|pull\s+requests?)\b")) return provider == "github";
        if (Match(request, @"\bevents?\b")) return provider == "googlecalendar";
        if (Match(request, @"\b(?:documents?|files?)\b")) return provider is "googledocs" or "googledrive";
        return true; // "it" means the most recent retained object, never a guessed provider.
    }

    internal static bool Affirmative(string request) => Match(request,
        @"^\s*(?:yes|yeah|yep|sure|okay|ok|please\s+do|go\s+ahead)(?:[,\s]+(?:yes|yeah|yep|sure|okay|ok)){0,2}(?:[,\s]+(?:please(?:\s+do)?|do\s+that|go\s+ahead))?[.!?]?\s*$");

    // Bare demonstratives reference the same retained object as "it". The
    // controller still resolves its verified account/resource and read scope.
    private const string MailObject = @"(?:it|that|this|(?:that|this|the)\s+(?:(?:full|entire|whole|complete)\s+)?(?:thread|conversation|email|chat|message)(?:\s+history)?(?:\s+you\s+(?:just\s+)?(?:found|read))?)";
    private const string MailDetailNoun = @"(?:(?:a|the|some|more|full|complete|entire|all|detailed|conversation|conversational)\s+)*(?:details?|information|messages|conversation|history|summary)";

    private static string MailReadRequest(string request)
    {
        if (request?.Length is not (> 0 and <= 3000)) return "";
        request = NormalizeMailReference(request);
        // Strip only conversational wrappers around the complete read clause.
        // The anchored grammar below still rejects another goal or action.
        request = Pattern(@"^\s*(?:(?:yes|yeah|yep|sure|okay|ok)[,\s]+){0,3}(?:(?:please|pls)\s+|(?:can|could|would)\s+(?:you\s+)?){0,3}").Replace(request, "");
        return Pattern(@"\s+please[.!?]?\s*$").Replace(request, "");
    }

    private static string NormalizeMailReference(string request)
    {
        // Normalize human object nouns before parsing actions. This supplies no
        // native ID or destination and leaves both compound clauses anchored.
        // Keep these small patterns separate from the bounded action grammar.
        request = Pattern(@"\b(?:email|message|conversation)\s+thread\b").Replace(request, "thread");
        request = Pattern(@"\b(that|this|the|its)\s+(?:(?:exact|actual|specific|matching)\s+)+(email|message|thread|conversation|document|file|repo|repository|issue|event)\b").Replace(request, "$1 $2");
        return Pattern(@"^\s*(can|could|would)\s+(please|pls)\s+").Replace(request, "$1 you $2 ");
    }

    // These are contextual READS only. A named new search, write, cancellation
    // or unrelated question never borrows the most recent mailbox object.
    internal static bool MailDetails(string request)
        => MailDetailsSingle(request) || TryReadAndOpen(request, out _, out _);

    private static bool MailDetailsSingle(string request)
    {
        if (request?.Length is not (> 0 and <= 3000)) return false;
        request = MailReadRequest(request);
        request = NormalizeMailSelection(request, out _);
        return MailDetailsCore(request);
    }

    internal static ConnectorMailSelection MailSelection(string request)
    {
        if (TryReadAndOpen(request, out var read, out _)) request = read;
        request = NormalizeMailSelection(MailReadRequest(request), out var selection);
        return MailDetailsCore(request) ? selection : ConnectorMailSelection.Unspecified;
    }

    // Two compatible operations on one retained mail object share an owner,
    // but they are not an opening-only intent. Each complete clause must
    // independently authorize a bounded read or one exact mail opening.
    // This parser supplies no destination, account, ID or write authority.
    internal static bool TryReadAndOpen(string request, out string readRequest, out string openRequest)
    {
        readRequest = openRequest = "";
        if (request?.Length is not (> 0 and <= 3000)) return false;
        var clauses = Pattern(@"\s+(?:and(?:\s+then)?|then)\s+").Split(request);
        if (clauses.Length != 2) return false;
        var first = clauses[0].Trim().TrimEnd(',', ';');
        var second = clauses[1].Trim();
        if (SingleMailOpen(second) && ContextualMailReadClause(first))
        { readRequest = first; openRequest = second; return true; }
        if (SingleMailOpen(first) && ContextualMailReadClause(second))
        { readRequest = second; openRequest = first; return true; }
        return false;
    }

    // Structural read/question intent, evaluated only after a connector tool
    // proposal names a retained result. It is not a normal Jarvis router and
    // does not depend on a list of particular conversational phrasings.
    internal static bool IsContextualReadRequest(string request, bool requireReadVerb = true)
    {
        if (request?.Length is not (> 0 and <= 3000) || JarvisAutomationService.IsOrdinaryNavigationRequest(request)
            || JarvisConnectorController.IsExplicitCancellation(request) || ConnectorFollowupIntent.IsExactOpen(request)) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        bool Has(string pattern) => Regex.IsMatch(request, pattern, options, TimeSpan.FromMilliseconds(100));
        const string writes = @"send|reply|forward|create|delete|draft|post|edit|modify|update|publish|switch|cancel|stop|end|exit|quit|close|open|navigate";
        if (Has(@"(?:^|[;.!]|\b(?:and|then|also))\s*(?:(?:please|now)\s+|(?:(?:can|could|would|will)\s+you\s+))*(?:do\s+)?\b(?:" + writes + @")\b")
            || Has(@"\b(?:fix|repair|restart|reboot|troubleshoot)\b.*\b(?:laptop|computer|device|screen|desktop|windows)\b")
            || Has(@"\b(?:this|that|my|the)\s+(?:laptop|computer|device|screen|desktop|clipboard|selection)\b")
            || Has(@"\b(?:other|different|another|second|work|personal)\s+(?:gmail\s+|google\s+)?(?:account|mailbox)\b|\b(?:switch|change|choose|select)\s+(?:the\s+|my\s+)?account\b")) return false;
        return !requireReadVerb || Has(@"^\s*(?:(?:hey\s+)?jarvis[,!\s]+)?(?:(?:please|now|also|actually)\s+)*(?:who|what|when|where|why|how|is|are|was|were|did|does|do|has|have|had|can|could|would|should|read|tell|give|provide|get|pull|show|explain|summari[sz]e|recap|analy[sz]e|describe|check|inspect|compare|list)\b");
    }

    // The same structural read guard used by retained tool recovery also applies
    // to the read half of a compound request. Additional scope must never borrow
    // the other clause's already-selected object or account.
    private static bool ContextualMailReadClause(string request)
    {
        if (MailDetailsSingle(request)) return true;
        request = MailReadRequest(request);
        if (Match(request.TrimEnd('.', '!', '?'), @"[.!?;\r\n]")
            || !IsContextualReadRequest(request) || !ObjectMatchesProvider(request, "gmail")
            || Match(request, @"\b(?:find|search|look\s+for|new|another|other|different|additional|spam|unread|inbox|today|yesterday|tomorrow|before|after|since|between|until|account|mailbox|not|never|without|except|instead)\b")
            || Match(request, @"\b(?:from|with|by|to)\s+(?:dr\.?\s+|prof\.?\s+)?[\p{L}@]|\b[0-9]{4}[-/][0-9]{1,2}")) return false;
        return Match(request, @"\b(?:that|this|the)\s+(?:(?:full|entire|whole|complete|last|latest)\s+)?(?:email|message|thread|conversation)\b")
            || Match(request, @"\bwhat\s+(?:we|they)\s+(?:discussed|said|agreed)\b");
    }

    private static bool SingleMailOpen(string request) =>
        TryParseExactOpen(request, out var selection) && selection == ConnectorOpenSelection.Single
        && ObjectMatchesProvider(request, "gmail");

    private static string NormalizeMailSelection(string request, out ConnectorMailSelection selection)
    {
        selection = ConnectorMailSelection.Unspecified;
        // Parse a participant-relative object separately from the read action.
        // The whole remaining clause must still be a bounded read, never a write
        // or a new search. This selector contributes no provider ID or address.
        var target = Pattern(@"\b(?:(?:that|this|the)\s+)?(?:one\s+)?(?:one|email|message|thread|conversation)(?:\s+(?:which|that))?\s+I\s+(?:(?:had|have|just|already)\s+)?(?<direction>sent|received)[.!?]?\s*$").Match(request);
        if (!target.Success) return request;
        selection = target.Groups["direction"].Value.Equals("sent", StringComparison.OrdinalIgnoreCase)
            ? ConnectorMailSelection.Sent : ConnectorMailSelection.Received;
        return request[..target.Index] + "that email";
    }

    private static bool MailDetailsCore(string request)
    {
        return Match(request, @"^(?:read|summari[sz]e|recap|explain)\s+" + MailObject + @"(?:\s+in\s+detail)?[.!?]?\s*$")
        || MailDetailClause(request, @"^(?:show|give|provide|tell|get)(?:\s+me)?\s+")
        || MailDetailClause(request, @"^pull\s+up\s+")
        || Match(request, @"^tell\s+me\s+more(?:\s+about\s+" + MailObject + @")?[.!?]?\s*$")
        || Match(request, @"^tell\s+me\s+about\s+" + MailObject + @"[.!?]?\s*$")
        || MailQuestion(request);
    }

    private static bool MailDetailClause(string request, string verb)
    {
        var prefix = Pattern(verb).Match(request);
        if (!prefix.Success) return false;
        var detail = Pattern(@"\s+(?:of|from|about|on)\s+" + MailObject + @"[.!?]?\s*$").Replace(request[prefix.Length..], "");
        return Match(detail, "^" + MailDetailNoun + @"[.!?]?\s*$");
    }

    internal static bool MailQuestion(string request)
    {
        request = MailReadRequest(request);
        request = Pattern(@"^tell\s+me\s+").Replace(request, "");
        // Parse the optional object separately so each cached .NET 8
        // NonBacktracking automaton stays small and bounded.
        var question = Pattern(@"\s+in\s+" + MailObject + @"[.!?]?\s*$").Replace(request, "");
        if (Match(question, @"^what\s+(?:did|does)\s+(?:she|he|they|the\s+sender)\s+(?:say|ask(?:\s+me)?(?:\s+for)?|want)[.!?]?\s*$")
            || Match(question, @"^what\s+(?:she|he|they|the\s+sender)\s+(?:said|asked(?:\s+me)?(?:\s+for)?|wanted)[.!?]?\s*$")
            || Match(question, @"^what\s+(?:did\s+we\s+discuss|was\s+the\s+(?:latest|last)\s+message)[.!?]?\s*$")) return true;
        if (Match(request, @"^what\s+is\s+" + MailObject + @"\s+about[.!?]?\s*$")
            || Match(request, @"^what\s+is\s+the\s+subject\s+of\s+" + MailObject + @"[.!?]?\s*$")) return true;
        question = Pattern(@"\s+to\s+" + MailObject + @"[.!?]?\s*$").Replace(request, "");
        return Match(question, @"^did\s+I\s+(?:reply|respond)[.!?]?\s*$");
    }

    internal static bool OffersMailDetails(string answer)
    {
        if (answer?.Length is not (> 0 and <= 3000)) return false;
        // Consider the first offer, then require its whole remaining clause to
        // be a read. A later "or should I ..." cannot erase a mixed-action offer.
        var offer = Pattern(@"\b(?:want\s+me\s+to|would\s+you\s+like(?:\s+me\s+to)?|should\s+I|shall\s+I|I\s+(?:can|could)(?:\s+also)?|is\s+there\s+(?:anything|something)(?:\s+(?:specific|else))?|is\s+there\s+(?:a|any)\s+(?:specific|particular)\s+detail)\s+").Match(answer);
        if (!offer.Success) return false;
        var clause = answer[(offer.Index + offer.Length)..];
        if (Match(offer.Value, @"^I\s+(?:can|could)(?:\s+also)?\s+$"))
        {
            // A conditional read offer is the same conversational commitment
            // as a question. Require the condition at the end, then validate
            // the entire action below; capability claims and mixed actions
            // cannot authorize a read from a bare affirmative.
            var condition = Pattern(@"\s+if\s+you(?:(?:'d|’d|\s+would)\s+like|\s+(?:like|want))[.!?]?\s*$").Match(clause);
            if (!condition.Success) return false;
            clause = clause[..condition.Index];
        }
        if (Match(offer.Value, @"^is\s+there\s+(?:a|any)\s+(?:specific|particular)\s+detail\s+$"))
            return Match(clause, @"^you(?:\s+(?:were|are)\s+looking\s+for|(?:'d|’d|\s+would)\s+like\s+to\s+know(?:\s+more)?\s+about)[.!?]?\s*$");
        if (offer.Value.StartsWith("is there", StringComparison.OrdinalIgnoreCase))
        {
            const string desire = @"you(?:'d|’d|\s+would)\s+like\s+to\s+know";
            var target = @"(?:" + MailObject + @"|them|(?:these|those)\s+(?:emails|messages|threads|conversations))";
            return Match(clause, "^" + desire + @"\s+(?:about|from)\s+" + target + @"[.!?]?\s*$")
                || Match(clause, @"^(?:about|from)\s+" + target + @"\s+" + desire + @"[.!?]?\s*$")
                || Match(clause, @"^you\s+wanted\s+to\s+focus\s+on\s+within\s+(?:that|this|the)\s+summary[.!?]?\s*$");
        }
        // Alternatives are safe only when every whole clause is a read of the
        // retained conversation. A send/open/other-object alternative must not
        // borrow the read branch's authority from an ambiguous "yes".
        var alternatives = Pattern(@"\s+or\s+").Split(clause);
        return alternatives.Length <= 2 && alternatives.All(MailReadOfferClause);
    }

    private static bool MailReadOfferClause(string clause)
    {
        // Uncertainty qualifies the same read, not its target or permissions.
        // Peel one leading modality only; the complete remaining clause must
        // still be a retained-object read, including every offered alternative.
        clause = Pattern(@"^(?:try|attempt)\s+to\s+").Replace(clause, "");
        if (Match(clause, @"^look\s+into\s+(?:any|a)\s+(?:specific|particular)\s+part\s+of\s+" + MailObject + @"[.!?]?\s*$")) return true;
        if (Match(clause, @"^(?:more\s+)?details\s+on\s+(?:any|a)\s+(?:specific|particular)\s+(?:email|message)\s+from\s+" + MailObject + @"[.!?]?\s*$")) return true;
        if (Match(clause, @"^to\s+know\s+more\s+about\s+(?:any\s+of\s+)?(?:these|those)\s+(?:emails|messages|threads|conversations)[.!?]?\s*$")) return true;
        clause = Pattern(@"^(?:(?:pull(?:\s+up)?|read(?:\s+out)?|show|give|provide|summari[sz]e|get|retrieve|to\s+see)\s+)?(?:you\s+)?").Replace(clause, "");
        clause = Pattern(@"\s+for\s+you[.!?]?\s*$").Replace(clause, "");
        clause = Pattern(@"\s+(?:of|from|about)\s+" + MailObject + @"[.!?]?\s*$").Replace(clause, "");
        if (Match(clause, @"^(?:the\s+)?(?:most\s+recent|latest|last)\s+(?:one|email|message)[.!?]?\s*$")) return true;
        return Match(clause, @"^(?:(?:a|the|some|more|full|complete|entire|detailed|conversation|thread)\s+)*(?:details?|information|messages|thread|conversation|summary)[.!?]?\s*$")
            || Match(clause, @"^(?:any|a)\s+(?:specific|particular)\s+(?:email|message|thread|conversation)(?:\s+in\s+(?:more\s+)?detail)?[.!?]?\s*$")
            || Match(clause, @"^tell\s+you\s+more[.!?]?\s*$");
    }
}
