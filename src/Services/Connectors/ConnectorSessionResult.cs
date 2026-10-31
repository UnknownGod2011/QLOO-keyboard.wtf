namespace KeyboardWtf.Services.Connectors;

using System.Net.Mail;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Facts/references only. No bodies, executable tools, tokens or write authority.
internal sealed record ConnectorSessionFact(string ResourceId, string Capability, string Text);
internal sealed record ConnectorMailThreadReference(string AccountId, string AccountLabel, string ThreadId,
    string[] MessageIds, string Subject, string[] Participants, string Date, string NativeWebUrl, bool BodyContentObserved);
internal sealed record ConnectorSessionResult(string TaskId, string Provider, string AccountId,
    string AccountLabel, string Request, string Answer, bool Partial, JsonElement[] Resources, string PrimaryThread = null)
{
    public ConnectorResourceReference[] ObjectReferences { get; init; } = [];
    public ConnectorSessionFact[] Facts { get; init; } = [];
    public string[] ContentLoadedCapabilities { get; init; } = [];
    public int GoalRevision { get; init; }
    public string[] AcceptedReadGuidance { get; init; } = [];
    public int[] EvidenceGoalRevisions { get; init; } = [];
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.MaxValue;
    public string GoalStatus { get; init; }
    public string TargetReference { get; init; } = "";
    public bool ExactResourceSelectionAllowed { get; init; } = true;
    // Local session pins survive pruning the execution owner. They are neither
    // provider data nor serialized tool authority and contain no message body.
    internal string OriginRequestIdentity { get; init; }
    internal string OriginLogicalSessionId { get; init; }
    internal long OriginGeneration { get; init; }
    internal string[] ReadCapabilityIds { get; init; } = [];
    private string PrimaryResourceUrl { get; init; }
    private bool ResourceSelectionAmbiguous { get; init; } = true;
    private bool MailSelectionAmbiguous { get; init; } = true;
    private bool MailCandidateSetComplete { get; init; }
    private bool MailCandidatePageComplete { get; init; }
    internal static ConnectorSessionResult Create(string taskId, string provider, string accountId, string label,
        string request, JsonElement last, IEnumerable<JsonElement> evidence, bool rankedPrimaryEvidence = true,
        string[] evidenceCapabilities = null, int goalRevision = 0,
        string[] acceptedReadGuidance = null, int[] evidenceGoalRevisions = null, bool allowExactResourceSelection = true, string goalStatus = null, string targetReference = null)
    {
        var guidance = acceptedReadGuidance?.ToArray() ?? [];
        var revisions = evidenceGoalRevisions?.ToArray() ?? [];
        if (goalRevision is < 0 or > 8 || guidance.Length != goalRevision
            || guidance.Any(value => string.IsNullOrWhiteSpace(value)) || string.Join("\n", guidance).Length > 3000
            || revisions.Length > 8 || revisions.Any(value => value < 0 || value > goalRevision))
            throw new ArgumentException("Invalid bounded retained read scope.");
        var resources = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var mailOmitted = false;
        bool AddMail(JsonElement item, string observedThread = null, bool threadReference = false, bool contentRead = false)
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            string Field(string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
            var id = Field("id"); var thread = observedThread ?? Field("threadId");
            static bool MailId(string value) => value.Length is >= 8 and <= 64 && value.All(char.IsAsciiHexDigit);
            if (!MailId(id) || !MailId(thread)) return false;
            if (!resources.ContainsKey(id) && resources.Count >= 12) { mailOmitted = true; return false; }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("headers", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var header in array.EnumerateArray().Take(20))
                    if (header.ValueKind == JsonValueKind.Object && header.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        && header.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                        && new[] { "From", "To", "Date", "Subject", "Message-ID" }.Contains(name.GetString(), StringComparer.OrdinalIgnoreCase))
                        headers[name.GetString()] = Limit(value.GetString(), 500);
            var internalDate = CanonicalInternalDate(item);
            var sameObject = resources.TryGetValue(id, out var previous) && previous.GetProperty("threadId").GetString() == thread;
            if (sameObject && previous.TryGetProperty("headers", out var previousHeaders))
                foreach (var header in previousHeaders.EnumerateObject())
                    if (!headers.ContainsKey(header.Name)) headers[header.Name] = header.Value.GetString();
            string RetainedField(string field) => Field(field) is { Length: > 0 } current ? current
                : sameObject && previous.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
            if (internalDate is null && resources.TryGetValue(id, out var prior)
                && prior.GetProperty("threadId").GetString() == thread && prior.TryGetProperty("internalDate", out var priorDate)
                && priorDate.ValueKind == JsonValueKind.String) internalDate = priorDate.GetString();
            resources[id] = JsonSerializer.SerializeToElement(new { id, threadId = thread,
                headers, subject = Limit(RetainedField("subject"), 500), date = Limit(RetainedField("date"), 160), from = Limit(RetainedField("from"), 500),
                internalDate, threadReference,
                bodyContentObserved = contentRead && !string.IsNullOrWhiteSpace(Field("text"))
                    || sameObject
                        && previous.TryGetProperty("bodyContentObserved", out var read) && read.ValueKind == JsonValueKind.True });
            return true;
        }
        bool ReadMailReceipt(JsonElement receipt, string capability, out bool pageComplete)
        {
            pageComplete = false;
            if (!Verified(receipt) || !receipt.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            // Legacy callers can identify only the normalized top-level shapes.
            // Never traverse envelope diagnostics, quoted content or arbitrary IDs.
            if (capability.Length == 0)
                capability = data.TryGetProperty("id", out _) ? data.TryGetProperty("messages", out _) ? "mail_thread" : "mail_read" : "mail_search";
            if (capability == "mail_read") return pageComplete = AddMail(data, contentRead: true);
            if (capability == "mail_thread")
            {
                var thread = data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : "";
                if (!AddMail(data, thread, threadReference: true)) return false;
                // The directly read thread is the target even when its content
                // is partial; child excerpts cannot authorize another thread.
                if (data.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                    foreach (var message in messages.EnumerateArray().Take(100))
                        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("threadId", out var childThread)
                            && childThread.ValueKind == JsonValueKind.String && childThread.GetString() == thread) AddMail(message, contentRead: true);
                return pageComplete = true;
            }
            if (capability != "mail_search" || !data.TryGetProperty("messages", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return false;
            var complete = candidates.GetArrayLength() <= 100;
            if (!complete) mailOmitted = true;
            foreach (var candidate in candidates.EnumerateArray().Take(100)) complete &= AddMail(candidate);
            if (data.TryGetProperty("sampledMessages", out var samples) && samples.ValueKind == JsonValueKind.Array)
                foreach (var sample in samples.EnumerateArray().Take(100))
                    if (sample.ValueKind == JsonValueKind.Object && sample.TryGetProperty("id", out var sampleId) && sampleId.ValueKind == JsonValueKind.String
                        && resources.TryGetValue(sampleId.GetString(), out var observed) && sample.TryGetProperty("threadId", out var sampleThread)
                        && sampleThread.ValueKind == JsonValueKind.String && sampleThread.GetString() == observed.GetProperty("threadId").GetString()) AddMail(sample);
            // Preview/content samples may describe only one of several matches.
            // Candidate identities, paging and coverage own unique navigation.
            complete &= !(result.TryGetProperty("truncated", out var truncated) && truncated.ValueKind != JsonValueKind.False)
                && !(data.TryGetProperty("pageItemCount", out var count) && (count.ValueKind != JsonValueKind.Number
                    || !count.TryGetInt32(out var total) || total != candidates.GetArrayLength()));
            if (result.TryGetProperty("coverage", out var coverage))
                complete &= coverage.ValueKind == JsonValueKind.Object && coverage.TryGetProperty("complete", out var covered)
                    && covered.ValueKind == JsonValueKind.True;
            // A paginated page may be explicitly selected on its own only
            // when its native count and paging metadata are well formed.
            pageComplete = complete
                && data.TryGetProperty("pageItemCount", out var pageCount) && pageCount.TryGetInt32(out var pageTotal)
                && pageTotal == candidates.GetArrayLength()
                && data.TryGetProperty("hasMore", out var pageMore) && pageMore.ValueKind is JsonValueKind.True or JsonValueKind.False
                && (!data.TryGetProperty("nextPageToken", out var pageNext) || pageNext.ValueKind == JsonValueKind.String);
            complete &= !(data.TryGetProperty("hasMore", out var more) && more.ValueKind != JsonValueKind.False)
                && !(data.TryGetProperty("nextPageToken", out var next) && (next.ValueKind != JsonValueKind.String || next.GetString().Length > 0));
            return complete;
        }
        // The native reader already admits at most eight receipts. Retain that
        // accepted evidence set; a ninth sentinel still detects omitted input.
        var sampled = evidence.Take(9).ToArray();
        var receipts = sampled.Take(8).ToArray();
        var receiptsOmitted = sampled.Length > receipts.Length;
        string primaryThread = null;
        bool Verified(JsonElement receipt) => receipt.ValueKind == JsonValueKind.Object
            && receipt.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "completed"
            && receipt.TryGetProperty("verified", out var flag) && flag.ValueKind == JsonValueKind.True;
        var mailSelectionIncomplete = false;
        var mailPageIncomplete = false;
        if (provider == "gmail") for (var index = 0; index < receipts.Length; index++)
        {
            var complete = ReadMailReceipt(receipts[index], evidenceCapabilities is not null && index < evidenceCapabilities.Length ? evidenceCapabilities[index] : "", out var pageComplete);
            mailSelectionIncomplete |= !complete;
            mailPageIncomplete |= !pageComplete;
            if (index == 0)
            {
                var firstThreads = resources.Values.Select(resource => resource.GetProperty("threadId").GetString()).Distinct().ToArray();
                if (rankedPrimaryEvidence && complete && !mailOmitted && firstThreads.Length == 1) primaryThread = firstThreads[0];
            }
        }
        var answer = last.ValueKind == JsonValueKind.Object && last.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("answer", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
        var partial = last.ValueKind == JsonValueKind.Object && last.TryGetProperty("result", out var scope)
            && scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
        partial |= receipts.Where(Verified).Any(receipt => receipt.TryGetProperty("result", out var evidenceResult)
            && evidenceResult.ValueKind == JsonValueKind.Object && evidenceResult.TryGetProperty("truncated", out var evidenceFlag)
            && evidenceFlag.ValueKind == JsonValueKind.True);
        var objects = ConnectorResourceReferences.Collect(provider, receipts, evidenceCapabilities ?? [], rankedPrimaryEvidence);
        (ConnectorSessionFact[] Values, bool Omitted) facts;
        try { facts = ExtractFacts(provider, receipts, evidenceCapabilities ?? [], resources.Keys.ToHashSet(StringComparer.Ordinal)); }
        catch (RegexMatchTimeoutException)
        {
            // Optional cached excerpts cannot invalidate a verified read or
            // interrupt its completion/UI cleanup. Keep resource identity and
            // native evidence; do not claim exhaustive cached content.
            facts = ([], true);
            AppLog.Info("Connector retained fact extraction incomplete: errorType=RegexMatchTimeoutException");
        }
        return new(taskId, provider, accountId, Limit(label, 200), Limit(request, 1000), Limit(answer, 6000), partial || receiptsOmitted || mailOmitted || answer.Length > 6000 || facts.Omitted, resources.Values.ToArray(), allowExactResourceSelection ? primaryThread : null)
        { ObjectReferences = objects.References, Facts = facts.Values,
            ContentLoadedCapabilities = (evidenceCapabilities ?? []).Where((cap, index) => index < receipts.Length && Verified(receipts[index])
                && cap is "mail_read" or "mail_thread" or "docs_read").Distinct().ToArray(),
            PrimaryResourceUrl = allowExactResourceSelection ? objects.PrimaryUrl : null,
            GoalRevision = goalRevision, AcceptedReadGuidance = guidance, EvidenceGoalRevisions = revisions, ExactResourceSelectionAllowed = allowExactResourceSelection, GoalStatus = goalStatus, TargetReference = targetReference ?? "",
            ResourceSelectionAmbiguous = objects.Ambiguous || !rankedPrimaryEvidence && receiptsOmitted,
            MailCandidateSetComplete = provider == "gmail" && !receiptsOmitted && !mailOmitted && !mailSelectionIncomplete,
            MailCandidatePageComplete = provider == "gmail" && !receiptsOmitted && !mailOmitted && !mailPageIncomplete,
            MailSelectionAmbiguous = !allowExactResourceSelection || (rankedPrimaryEvidence ? primaryThread is null : receiptsOmitted || mailOmitted || mailSelectionIncomplete) };
    }

    internal static string CanonicalInternalDate(JsonElement item)
    {
        if (item.EnumerateObject().Count(property => property.Name == "internalDate") != 1
            || !item.TryGetProperty("internalDate", out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (text is null || text.Length is < 1 or > 20
            || !Regex.IsMatch(text, @"^-?[0-9]{1,19}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50))
            || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds)) return null;
        try { _ = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
        return milliseconds.ToString(CultureInfo.InvariantCulture);
    }

    private static (ConnectorSessionFact[] Values, bool Omitted) ExtractFacts(string provider, JsonElement[] receipts,
        string[] capabilities, HashSet<string> mailIds)
    {
        // Keep a few short, source-attributed factual excerpts, never a body
        // snapshot. Search previews and unknown payload shapes cannot populate
        // these facts. Their text remains untrusted provider data on recall.
        var facts = new List<ConnectorSessionFact>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var omitted = false; var remaining = 1600;
        void Add(string id, string capability, string content)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(content)) return;
            foreach (var sentence in Regex.Split(content, @"(?<=[.!?])\s+|(?:\r?\n){2,}", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)).Take(200))
            {
                var text = Regex.Replace(sentence.Trim(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                if (text.StartsWith('>') || !Regex.IsMatch(text,
                    @"\b(?:prize|winner|won|award\w*|selected|finalist\w*|top\s+\d+|decid\w*|agreed|commit\w*|pending|deadline|due|next\s+steps?|action\s+items?|status|completed|domain|launch|release|publish\w*)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) continue;
                // A clipped sentence can change its meaning. Skip it rather
                // than claim a fragment is a complete extracted fact.
                if (text.Length > 320 || text.Length > remaining || facts.Count >= 6) { omitted = true; continue; }
                if (!seen.Add(id + ":" + text)) continue;
                facts.Add(new(Limit(id, 160), capability, text)); remaining -= text.Length;
            }
        }
        string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
        void DocumentRuns(JsonElement value, string id, int depth)
        {
            if (depth > 16) { omitted = true; return; }
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() > 100) omitted = true;
                foreach (var item in value.EnumerateArray().Take(100)) DocumentRuns(item, id, depth + 1);
            }
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                    if (property.Name == "paragraph")
                    {
                        // Styling splits one sentence into several textRuns.
                        // Join its paragraph before choosing complete facts so
                        // a qualifier or a split prize cannot be discarded.
                        if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("elements", out var elements)
                            || elements.ValueKind != JsonValueKind.Array || elements.GetArrayLength() > 100)
                        { omitted = true; continue; }
                        var paragraph = string.Concat(elements.EnumerateArray().Select(element =>
                            element.ValueKind == JsonValueKind.Object && element.TryGetProperty("textRun", out var run) ? Text(run, "content") : ""));
                        if (paragraph.Length > 24000) { omitted = true; continue; }
                        Add(id, "docs_read", paragraph);
                    }
                    else if (property.Name is "tabs" or "childTabs" or "documentTab" or "body" or "content" or "table" or "tableRows" or "tableCells")
                        DocumentRuns(property.Value, id, depth + 1);
        }
        for (var index = 0; index < receipts.Length; index++)
        {
            var receipt = receipts[index];
            if (Text(receipt, "state") != "completed" || !receipt.TryGetProperty("verified", out var verified)
                || verified.ValueKind != JsonValueKind.True || !receipt.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data)) continue;
            var capability = index < capabilities.Length ? capabilities[index] : "";
            if (capability.Length == 0 && provider == "gmail" && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _))
                capability = data.TryGetProperty("messages", out _) ? "mail_thread" : "mail_read";
            if (provider == "gmail" && capability == "mail_read" && mailIds.Contains(Text(data, "id")))
                Add(Text(data, "id"), capability, Text(data, "text"));
            else if (provider == "gmail" && capability == "mail_thread" && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray().Take(100))
                    if (mailIds.Contains(Text(message, "id")) && Text(message, "threadId") == Text(data, "id"))
                        Add(Text(message, "id"), capability, Text(message, "text"));
            }
            else if (provider == "googledocs" && capability == "docs_read" && Text(data, "documentId").Length > 0)
                DocumentRuns(data, Text(data, "documentId"), 0);
            else if (provider == "slack" && capability == "slack_history" && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("messages", out var channelMessages) && channelMessages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in channelMessages.EnumerateArray().Take(100)) Add(Text(message, "ts"), capability, Text(message, "text"));
            }
            else if (provider == "github" && capability == "github_issues" && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in data.EnumerateArray().Take(100)) Add(Text(issue, "html_url"), capability, Text(issue, "body"));
            }
            else if (provider == "github" && capability is "github_repo" or "github_releases" && data.ValueKind == JsonValueKind.Object)
            {
                // Small scalar facts for natural follow-ups; no release body,
                // raw API document or permanent storage. Numeric meanings stay
                // named exactly as returned by the normalized provider.
                var id = capability == "github_repo" ? Text(data, "full_name") : Text(data, "html_url");
                var fields = capability == "github_repo" ? new[] { "stargazers_count", "forks_count", "subscribers_count", "open_issues_count", "pushed_at", "default_branch" }
                    : new[] { "tag_name", "published_at", "totalAssetDownloads", "assetCountsComplete" };
                var pairs = fields.Where(field => data.TryGetProperty(field, out var item) && item.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    .Select(field => field + "=" + data.GetProperty(field).ToString());
                Add(id, capability, "Repository release/status facts: " + string.Join("; ", pairs) + ".");
            }
        }
        return (facts.ToArray(), omitted);
    }

    internal string ExactResourceUrl() => !ExactResourceSelectionAllowed ? null : Provider == "gmail" ? ExactMailUrl()
        : ResourceSelectionAmbiguous ? null : PrimaryResourceUrl ?? (ObjectReferences.Length == 1 ? ObjectReferences[0].Url : null);

    // Each reference originates in the normalized verified native receipt. A
    // model's answer/URL, a quoted body ID or a Gmail UI token is never an ID
    // source. This is temporary result context, not execution authority.
    internal ConnectorMailThreadReference[] MailThreadReferences()
    {
        if (Provider != "gmail") return [];
        static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        static bool Id(string value) => value.Length is >= 8 and <= 64 && value.All(char.IsAsciiHexDigit);
        static string Header(JsonElement item, string name)
        {
            if (!item.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object) return "";
            var value = headers.EnumerateObject().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        }
        return Resources.Where(item => item.ValueKind == JsonValueKind.Object && Id(Text(item, "id")) && Id(Text(item, "threadId")))
            .GroupBy(item => Text(item, "threadId"), StringComparer.Ordinal).Take(12).Select(group =>
            {
                var items = group.ToArray();
                string First(string field, string header) => items.Select(item => Text(item, field) is { Length: > 0 } value ? value : Header(item, header))
                    .FirstOrDefault(value => value.Length > 0) ?? "";
                return new ConnectorMailThreadReference(AccountId, AccountLabel, group.Key,
                    items.Where(item => !item.TryGetProperty("threadReference", out var root) || root.ValueKind != JsonValueKind.True)
                        .Select(item => Text(item, "id")).Distinct(StringComparer.Ordinal).ToArray(),
                    First("subject", "Subject"), items.SelectMany(item => new[] { Text(item, "from"), Header(item, "From"), Header(item, "To") })
                        .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray(),
                    First("date", "Date"), NativeMailUrl(group.Key),
                    items.Any(item => item.TryGetProperty("bodyContentObserved", out var read) && read.ValueKind == JsonValueKind.True));
            }).ToArray();
    }

    internal bool TryResolveExactOpen(ConnectorOpenSelection selection, out ConnectorResourceReference[] references, out string rejection, string openingRequest = null)
    {
        references = []; rejection = null;
        if (ConnectorFollowupIntent.NamedMailOpen(openingRequest, out var field, out var requested))
        {
            var named = MatchingMailThreads(field, requested);
            if (selection == ConnectorOpenSelection.Single && named.Length == 1 && named[0].NativeWebUrl is { } destination)
            {
                var target = named[0];
                references = [new("gmail_thread", target.ThreadId, target.Subject, target.Date, destination)];
                return true;
            }
            rejection = named.Length == 0
                ? "The retained headers do not identify that requested email. Find it using a fresh connected-service read; no guessed URL or different conversation was opened."
                : "More than one retained conversation matches that name or subject. Ask which matching conversation to open; no guessed URL was opened.";
            return false;
        }
        if (selection == ConnectorOpenSelection.Single)
        {
            var url = ExactResourceUrl();
            // The new explicit opening selects the sole returned conversation;
            // later search pages do not make that observed identity ambiguous.
            // This grants no exhaustive-search claim or semantic match after
            // failed synthesis. Multiple/omitted returned candidates still fail.
            if (url is null && Provider == "gmail" && (ExactResourceSelectionAllowed || GoalStatus == "partial")
                && (MailCandidateSetComplete || MailCandidatePageComplete)
                && (EvidenceGoalRevisions.Length > 0 ? EvidenceGoalRevisions.All(revision => revision == GoalRevision) : GoalRevision == 0))
            {
                var candidates = MailThreadReferences();
                if (candidates.Length == 1) url = candidates[0].NativeWebUrl;
            }
            if (url is not null)
            {
                var mail = Provider == "gmail" ? MailThreadReferences().FirstOrDefault(value => value.NativeWebUrl == url) : null;
                references = [mail is not null ? new("gmail_thread", mail.ThreadId, mail.Subject, mail.Date, url)
                    : ObjectReferences.FirstOrDefault(value => value.Url == url) ?? new(Provider, "", "", "", url)];
                return true;
            }
            rejection = "The previous result does not identify one exact object and account. Which matching result should I open? No provider homepage was opened.";
            return false;
        }
        // 'Both/all' is a new explicit selection of the observed set. It does
        // not promote the first match, or claim failed synthesis chose a match.
        // Incomplete pages/omitted evidence must not silently become 'all'.
        var threads = MailThreadReferences();
        if (selection is not (ConnectorOpenSelection.Both or ConnectorOpenSelection.All)
            || Provider != "gmail" || !MailCandidateSetComplete || threads.Length == 0
            || selection == ConnectorOpenSelection.Both && threads.Length != 2
            || threads.Any(value => value.NativeWebUrl is null))
        {
            rejection = "The retained results do not identify the requested complete set of exact objects and account. Please choose the matching results to open. No provider homepage was opened.";
            return false;
        }
        references = threads.Select(value => new ConnectorResourceReference("gmail_thread", value.ThreadId, value.Subject, value.Date, value.NativeWebUrl)).ToArray();
        return true;
    }

    // A model can select an observed native ID for an explicitly requested
    // opening. Keep the identity/account from native evidence, never its URL.
    // This neither promotes failed search evidence nor grants task authority.
    internal ConnectorSessionResult SelectObservedMailThread(string thread) =>
        Provider == "gmail" && ExactResourceSelectionAllowed
        && MailThreadReferences().Any(candidate => candidate.ThreadId == thread)
            ? this with { PrimaryThread = thread, MailSelectionAmbiguous = false } : null;

    internal ConnectorMailThreadReference[] MatchingMailThreads(string field, string requested)
    {
        static string[] Words(string value) => Regex.Matches(value ?? "", @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))
            .Select(match => match.Value.ToUpperInvariant()).ToArray();
        var needle = Words(requested);
        if (Provider != "gmail" || needle.Length == 0) return [];
        bool Contains(string haystack)
        {
            var words = Words(haystack);
            return Enumerable.Range(0, Math.Max(0, words.Length - needle.Length + 1))
                .Any(start => words.Skip(start).Take(needle.Length).SequenceEqual(needle));
        }
        bool ObservedSender(string thread)
        {
            foreach (var resource in Resources.Where(resource => resource.GetProperty("threadId").GetString() == thread))
            {
                if (resource.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String && Contains(from.GetString())) return true;
                if (resource.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
                    && headers.EnumerateObject().Any(header => header.Name.Equals("From", StringComparison.OrdinalIgnoreCase)
                        && header.Value.ValueKind == JsonValueKind.String && Contains(header.Value.GetString()))) return true;
            }
            return false;
        }
        return MailThreadReferences().Where(thread => field == "subject" ? Contains(thread.Subject)
            : field == "sender" ? ObservedSender(thread.ThreadId) : thread.Participants.Any(Contains)).ToArray();
    }

    // A unique observed candidate can be inspected on a NEW explicit read even
    // when failed synthesis could not assert that it satisfies the old goal.
    internal string UniqueMailThread()
    {
        if (Provider != "gmail" || Resources.Length == 0) return null;
        if (ExactResourceSelectionAllowed && PrimaryThread is not null
            && Resources.Any(r => r.GetProperty("threadId").GetString() == PrimaryThread)) return PrimaryThread;
        var threads = Resources.Select(r => r.GetProperty("threadId").GetString()).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return threads.Length == 1 && threads[0] is { Length: >= 8 and <= 64 } id && id.All(char.IsAsciiHexDigit) ? id : null;
    }

    internal string ExactMailUrl()
    {
        if (!ExactResourceSelectionAllowed || Provider != "gmail" || MailSelectionAmbiguous || Resources.Length == 0) return null;
        var available = Resources.Select(r => r.GetProperty("threadId").GetString()).Distinct(StringComparer.Ordinal).ToArray();
        var threads = PrimaryThread is not null && available.Contains(PrimaryThread, StringComparer.Ordinal) ? new[] { PrimaryThread } : available;
        if (threads.Length != 1 || !Regex.IsMatch(threads[0] ?? "", "^[a-fA-F0-9]{8,64}$")) return null;
        return NativeMailUrl(threads[0]);
    }

    private string NativeMailUrl(string thread)
    {
        if (thread?.Length is not (>= 8 and <= 64) || !thread.All(char.IsAsciiHexDigit)) return null;
        try
        {
            var email = new MailAddress(AccountLabel).Address;
            // Account-qualified Gmail web convention; real browser acceptance
            // is required before advertising this as verified navigation.
            return "https://mail.google.com/mail/?authuser=" + Uri.EscapeDataString(email) + "#all/" + thread;
        }
        catch (FormatException) { return null; }
    }
    private static string Limit(string value, int length) => (value ?? "")[..Math.Min(value?.Length ?? 0, length)];
}
