namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Updates;

// One explicitly requested operation, not a speech generation. Provider barge-in
// may cancel its Live tool proposal/acknowledgement, but cannot cancel this job.
// Only explicit task cancellation/replacement or its logical session can do so.
internal sealed class SelectedTextTranslationOperation : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _operationToken;
    private readonly UpdateWorkAdmissionGate _updateWorkAdmission;
    private Dictionary<string, object> _result;
    private bool _disposed;
    private int _started;
    private Action<string> _statusChanged;
    // This is a per-request size, not a document limit. It preserves the
    // existing Managed endpoint contract and leaves output expansion room.
    internal const int TranslationPartCharacters = 50_000;
    private readonly StringBuilder _translatedParts = new();
    private string _sourceBoundary = "";
    private int _completedSourceCharacters;
    private int _sourceCharacters;
    private bool _sourceComplete = true;
    private bool _temporaryFailureRetried;
    internal string RetainedPartialTranslation { get { lock (_sync) return _translatedParts.ToString(); } }
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Request { get; }
    internal string Language { get; }
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal bool IsRunning { get { lock (_sync) return _result == null && !_cancellation.IsCancellationRequested; } }
    internal bool IsCancelled => _cancellation.IsCancellationRequested;

    internal SelectedTextTranslationOperation(string request, string language, CancellationToken session,
        UpdateWorkAdmissionGate updateWorkAdmission = null)
    {
        Request = request;
        Language = language;
        _updateWorkAdmission = updateWorkAdmission;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(session);
        _operationToken = _cancellation.Token;
    }

    internal void Start(string source, Func<string, string, CancellationToken, Task<string>> translate,
        Func<string, Dictionary<string, object>> copy,
        Func<SelectedTextTranslationOperation, Task> announce,
        Action<string> statusChanged = null, bool sourceComplete = true)
    {
        IDisposable updateWork = null;
        if (_updateWorkAdmission != null && !_updateWorkAdmission.TryAcquire(out updateWork))
            throw new InvalidOperationException("An update is preparing installation. Please wait.");
        try
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("The translation operation already started.");
            _statusChanged = statusChanged;
            _sourceComplete = sourceComplete;
            // Cancellation clears feedback immediately, even if an external
            // generation ignores its token. The existing source owns registration lifetime.
            _ = _operationToken.Register(() => PublishStatus(null));
            // Task.Run preserves the originating AsyncLocal backend scope. A
            // dedicated session token, never the cancellable provider call token,
            // owns the only generation and clipboard mutation.
            Completion = Task.Run(async () =>
            {
                using var ownedUpdateWork = updateWork;
                var started = Stopwatch.GetTimestamp();
                var copyAttempted = false;
                try
                {
                    _operationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(source))
                        throw new InvalidOperationException("No selected text was captured.");
                    _sourceCharacters = source.Length;
                    PublishStatus("Translating...");
                    AppLog.Info($"Selected translation started: operation={Id} sourceCharacters={source.Length}");
                    var parts = SplitSource(source, TranslationPartCharacters);
                    for (var index = 0; index < parts.Count; index++)
                    {
                        _operationToken.ThrowIfCancellationRequested();
                        if (parts.Count > 1) PublishStatus($"Translating part {index + 1} of {parts.Count}...");
                        await TranslatePartAsync(parts[index], translate, allowOutputSplit: true);
                    }
                    var output = RetainedPartialTranslation;
                    if (!string.IsNullOrWhiteSpace(output)) PublishStatus("Copying...");
                    // Cancel vs clipboard publication has one local linearization
                    // point. An explicit cancel accepted first prevents the copy.
                    lock (_sync)
                    {
                        _operationToken.ThrowIfCancellationRequested();
                        copyAttempted = !string.IsNullOrWhiteSpace(output);
                        _result = string.IsNullOrWhiteSpace(output)
                            ? Result(false, "Translation returned no complete text; the clipboard was left unchanged.")
                            : copy(output);
                        if (!_sourceComplete && _result.GetValueOrDefault("ok") is true)
                            _result["message"] = _result.GetValueOrDefault("clipboard_verified") is true
                                ? "Translated and copied the captured portion. The original selection was only partially captured."
                                : "Translation of the captured portion is ready. " + _result.GetValueOrDefault("message");
                    }
                    AppLog.Info($"Selected translation finished: operation={Id} outputCharacters={output?.Length ?? 0} "
                        + $"ok={Receipt()["ok"]} elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    lock (_sync) _result = Result(false, "Translation cancelled. No translated result was copied.");
                    AppLog.Info($"Selected translation cancelled: operation={Id}");
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        _result = Result(false, copyAttempted
                            ? "The clipboard result could not be verified. Check the clipboard before retrying."
                            : "Translation did not complete; no result was copied. " + ex.Message);
                        _result["clipboard_unchanged"] = !copyAttempted;
                        if (_completedSourceCharacters > 0)
                            _result["message"] += $" Completed {_completedSourceCharacters} of {_sourceCharacters} source characters before the failure.";
                    }
                    AppLog.Info($"Selected translation failed: operation={Id} errorType={ex.GetType().Name}");
                }
                finally { PublishStatus(null); }
                // The existing lifecycle sender refuses stale sessions and never
                // starts an AI session. Cancellation has its own control receipt.
                if (!_cancellation.IsCancellationRequested)
                {
                    try { await announce(this); }
                    catch (Exception ex) { AppLog.Info($"Selected translation notification not delivered: operation={Id} errorType={ex.GetType().Name}"); }
                }
            });
        }
        catch
        {
            updateWork?.Dispose();
            throw;
        }
    }

    private async Task TranslatePartAsync(string part,
        Func<string, string, CancellationToken, Task<string>> translate, bool allowOutputSplit)
    {
        string output;
        // Formatting-only parts need no model call, and cannot be submitted
        // as an empty translation prompt. Preserve their original whitespace.
        if (string.IsNullOrWhiteSpace(part)) output = part;
        else try { output = await TranslateWithTemporaryRecoveryAsync(part, translate); }
        catch (PersonalAiException error) when (error.Failure == PersonalAiFailure.Truncated
            && allowOutputSplit && part.Length > 1)
        {
            // Only a known output-budget exhaustion can subdivide a request.
            // Ambiguous transport/auth errors are not replayed. One subdivision
            // per initial part bounds recovery and keeps cancellation intact.
            PublishStatus("Continuing translation in smaller parts...");
            foreach (var smaller in SplitSource(part, Math.Max(1, part.Length / 2)))
                await TranslatePartAsync(smaller, translate, allowOutputSplit: false);
            return;
        }
        _operationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(output) && !string.IsNullOrWhiteSpace(part))
            throw new InvalidOperationException("Translation returned no complete text for this section.");
        lock (_sync)
        {
            if (_translatedParts.Length > 0 && !char.IsWhiteSpace(_translatedParts[^1]) && !char.IsWhiteSpace(output[0]))
                _translatedParts.Append(_sourceBoundary);
            _translatedParts.Append(output);
            var boundaryStart = part.Length;
            while (boundaryStart > 0 && char.IsWhiteSpace(part[boundaryStart - 1])) boundaryStart--;
            _sourceBoundary = part[boundaryStart..];
            _completedSourceCharacters += part.Length;
        }
    }

    private async Task<string> TranslateWithTemporaryRecoveryAsync(string part,
        Func<string, string, CancellationToken, Task<string>> translate)
    {
        try { return await translate(part, Language, _operationToken); }
        catch (PersonalAiException error) when (error.Failure == PersonalAiFailure.Provider
            && error.HttpStatusCode == 503 && !_temporaryFailureRetried && !_operationToken.IsCancellationRequested)
        {
            // A confirmed temporary rejection has no output to publish. Retry
            // only this part once per job; never repeat completed parts, change
            // account/backend, or replay an ambiguous network completion.
            _temporaryFailureRetried = true;
            PublishStatus("Translation service busy; retrying...");
            AppLog.Info($"Selected translation temporary recovery: operation={Id} httpStatus=503 attempt=2");
            await Task.Delay(TimeSpan.FromSeconds(1), _operationToken);
            _operationToken.ThrowIfCancellationRequested();
            return await translate(part, Language, _operationToken);
        }
    }

    internal static IReadOnlyList<string> SplitSource(string source, int partCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partCharacters, 1);
        var parts = new List<string>();
        for (var offset = 0; offset < source.Length;)
        {
            var length = Math.Min(partCharacters, source.Length - offset);
            if (offset + length < source.Length)
            {
                // Prefer a paragraph boundary in the latter half of a part.
                var newline = source.LastIndexOf('\n', offset + length - 1, length);
                if (newline >= offset + length / 2) length = newline - offset + 1;
                else
                {
                    // A long paragraph still has word boundaries. Split there
                    // before resorting to a hard cut through an unbroken token.
                    for (var boundary = offset + length - 1; boundary >= offset + length / 2; boundary--)
                        if (char.IsWhiteSpace(source[boundary])) { length = boundary - offset + 1; break; }
                }
                if (char.IsHighSurrogate(source[offset + length - 1]) && char.IsLowSurrogate(source[offset + length]))
                    length = length > 1 ? length - 1 : 2;
                else if (source[offset + length - 1] == '\r' && source[offset + length] == '\n')
                    length = length > 1 ? length - 1 : 2;
            }
            parts.Add(source.Substring(offset, length));
            offset += length;
        }
        return parts;
    }

    private void PublishStatus(string status)
    {
        if (status != null && _cancellation.IsCancellationRequested) return;
        try { _statusChanged?.Invoke(status); }
        catch { /* UI feedback must never change generation, copying, or cancellation. */ }
    }

    internal bool Cancel()
    {
        lock (_sync)
        {
            if (_result != null || _cancellation.IsCancellationRequested) return false;
            _cancellation.Cancel();
            return true;
        }
    }

    internal Dictionary<string, object> Receipt()
    {
        lock (_sync)
        {
            var receipt = _result != null ? new Dictionary<string, object>(_result)
                : Result(true, "");
            receipt["operation_id"] = Id;
            receipt["started"] = true;
            receipt["completed"] = _result != null;
            receipt["translated_source_characters"] = _completedSourceCharacters;
            receipt["source_characters"] = _sourceCharacters;
            receipt["source_capture_complete"] = _sourceComplete;
            receipt["partial_translation_retained"] = _completedSourceCharacters > 0 && _completedSourceCharacters < _sourceCharacters;
            // The terminal lifecycle event owns the one spoken outcome, even
            // if a fast operation has already completed when a receipt is read.
            receipt["announcement_owner"] = "selected_text_translation_lifecycle";
            return receipt;
        }
    }

    internal static bool TryParseRequest(string request, bool hasOriginal, out string language)
    {
        language = "";
        // A complete selected-source + translation + local-copy imperative.
        // No action is inferred from source text, a negation, or a mere mention.
        const string selectedSource = @"(?:(?:the|this|that|my)\s+)?(?:(?:entire|whole|full|complete|original|earlier|previous|prior|fresh|new|newly|different|current|currently)\s+)*(?:selected\s+text|selection|highlighted\s+text)";
        var normalized = NormalizeRequest(request);
        var sourcePattern = selectedSource;
        // Share only the reference grammar. The dispatcher still has to
        // resolve the named immutable source; parsing does not authorize a
        // guessed document or substitute another selection.
        if (SessionSelectionHistory.TryReferenceLabel(normalized, out _, out var rawLabel))
            sourcePattern += @"|(?:(?:the|this|that|my)\s+)?(?:(?:earlier|previous|prior|original)\s+)?"
                + Regex.Escape(rawLabel) + @"\s+(?:selected\s+text|selection)";
        var match = Regex.Match(normalized,
            @"^\s*(?:(?:okay|ok|now|please|actually)[,\s]+)*(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?"
            + @"translate\s+(?<source>" + sourcePattern + @"|(?:it|that)(?:\s*,\s*(?:" + sourcePattern + @")\s*,)?)"
            + @"\s+(?:in|into|to)\s+(?<language>[\p{L}][\p{L} -]{0,35}?)"
            + @"\s*[,;]?\s+(?:and|then)\s+copy(?:\s+(?:it|that|(?:(?:the|full|complete)\s+)*(?:result|translation)))?"
            + @"\s+(?:to|into|onto)\s+(?:(?:my|the)\s+)?clipboard(?:\s+please)?[.!?]*\s*$",
            RegexOptions.IgnoreCase);
        if (!match.Success || (Regex.IsMatch(match.Groups["source"].Value, @"^(?:it|that)(?:\s*,|$)", RegexOptions.IgnoreCase) && !hasOriginal))
            return false;
        language = match.Groups["language"].Value.Trim();
        if (TryCanonicalLanguage(language, out var canonical)) language = canonical;
        return !Regex.IsMatch(language, @"\b(?:and|then|not|instead|but)\b", RegexOptions.IgnoreCase);
    }

    // A structured tool proposal can describe the requested transformation,
    // but cannot authorize it. Bind both actions and the target language to
    // the originating user's words, not generated text or tool metadata.
    internal static bool TryParseStructuredRequest(string request, bool hasOriginal,
        string proposedLanguage, bool copyRequested, out string language, out bool intentRecognized)
    {
        language = "";
        intentRecognized = false;
        var text = NormalizeRequest(request).Trim();
        if (!copyRequested || text.Length == 0 || text.Length > 4000
            || Regex.IsMatch(text, "[\"“”`]|\\b(?:not|never|without|don't|dont|cannot|can't|cancel|stop|instead)\\b", RegexOptions.IgnoreCase))
            return false;
        var actions = Regex.Matches(text, @"\b(?:translate|copy)\b", RegexOptions.IgnoreCase);
        if (actions.Count != 2 || !actions[0].Value.Equals("translate", StringComparison.OrdinalIgnoreCase)
            || !actions[1].Value.Equals("copy", StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = text[..actions[0].Index];
        // One declarative source-capture clause is context, not an action.
        // It must end at punctuation or the following polite request mood;
        // it cannot swallow a second command or an "explain how" preface.
        var capturePreface = Regex.Match(prefix,
            @"^\s*I(?:\s+have|['’]ve)?\s+(?:(?:just|already|currently)\s+)?(?:selected|highlighted)\s+"
            + @"(?:(?!\b(?:and|then|or|but|can|could|would|will|translate|copy|explain|how)\b)[\p{L}\p{N}\s'-]){1,160}?"
            + @"(?:\s*[,.;!?]\s*|(?=\s+(?:can|could|would|will)\s+you\b))", RegexOptions.IgnoreCase);
        if (capturePreface.Success) prefix = prefix[capturePreface.Length..];
        const string courtesy = @"(?:(?:okay|ok|now|please|actually)[,\s]+)*(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?|I\s+(?:need|want|would\s+like)\s+you\s+to\s+)?";
        if (!Regex.IsMatch(prefix, @"^\s*" + courtesy + @"\s*$", RegexOptions.IgnoreCase))
            return false;

        var copyClause = text[actions[1].Index..];
        if (!Regex.IsMatch(copyClause,
            @"^copy(?:\s+(?:it|that|(?:(?:the|full|complete)\s+)*(?:result|translation)))?"
            + @"\s+(?:to|into|onto)\s+(?:(?:my|the)\s+)?clipboard(?:\s+(?:now|please|for\s+me))*[.!?]*\s*$", RegexOptions.IgnoreCase))
            return false;
        var transformation = text[(actions[0].Index + actions[0].Length)..actions[1].Index];
        transformation = Regex.Replace(transformation,
            @"[\s,;.!?]+(?:(?:and|then)\s+)?" + courtesy + @"$", "", RegexOptions.IgnoreCase).Trim();
        var target = Regex.Match(transformation,
            @"^(?<source>.+?)\s+(?:in|into|to)\s+(?<language>[\p{L}][\p{L} -]{0,35})\s*$", RegexOptions.IgnoreCase);
        if (!target.Success || Regex.IsMatch(target.Groups["source"].Value, @"\b(?:and|then|or|but)\b", RegexOptions.IgnoreCase))
            return false;
        var source = target.Groups["source"].Value.Trim();
        // The whole captured source, not a requested derived subset such as
        // "the judges' names from the selected text", owns this operation.
        const string selectionReference = @"(?:(?:all|the|this|that|my|entire|whole|full|complete|original|earlier|previous|prior|fresh|new|newly|different|current|currently)\s+)*"
            + @"(?:(?:selected|highlighted)\s+(?:text|content|page|passage|document)|selection|(?:text|content|page|passage|document)\s+(?:that\s+)?I\s+(?:(?:have|had|just|already)\s+)*(?:selected|highlighted))";
        var explicitSelection = Regex.IsMatch(source, "^" + selectionReference + "$", RegexOptions.IgnoreCase);
        if (!explicitSelection && SessionSelectionHistory.TryReferenceLabel(source, out _, out var label))
            explicitSelection = Regex.IsMatch(source, @"^(?:(?:the|this|that|my|earlier|previous|prior|original)\s+)*"
                + Regex.Escape(label) + @"\s+(?:selected\s+text|selection)$", RegexOptions.IgnoreCase);
        var pronoun = Regex.IsMatch(source, @"^(?:it|that)$", RegexOptions.IgnoreCase);
        if (!explicitSelection && !(pronoun && (hasOriginal || capturePreface.Success)))
            return false;
        // A real authorized transform with an unclear target must fail
        // truthfully rather than fall through to copying model narration.
        intentRecognized = true;
        if (!TryCanonicalLanguage(target.Groups["language"].Value, out var requestedLanguage))
            return false;
        if (!string.IsNullOrWhiteSpace(proposedLanguage)
            && (!TryCanonicalLanguage(proposedLanguage, out var proposed)
                || !requestedLanguage.Equals(proposed, StringComparison.OrdinalIgnoreCase)))
            return false;
        language = requestedLanguage;
        return true;
    }

    // The capture/copy action supplies a semantic boundary, not permission.
    // Bind the transform, its whole source and its clipboard effect locally;
    // courtesy/address/context outside these clauses need not fit one sentence.
    // A pronoun may refer to a fresh foreground selection, even before the
    // first snapshot exists. The existing source resolver decides provenance.
    internal static bool TryResolveClipboardTranslationIntent(string request,
        out string language, out bool intentRecognized) =>
        TryResolveClipboardTranslationIntent(request, out language, out intentRecognized, out _);

    internal static bool TryResolveClipboardTranslationIntent(string request,
        out string language, out bool intentRecognized, out string source)
    {
        language = "";
        intentRecognized = false;
        source = "";
        var text = NormalizeRequest(request).Trim();
        if (text.Length == 0 || text.Length > 4000
            || Regex.IsMatch(text, "[\"“”`]|\\b(?:not|never|without|don't|dont|cannot|can't|cancel|stop|instead|if|when|later|hypothetical)\\b", RegexOptions.IgnoreCase)
            || text.StartsWith("'") || text.EndsWith("'")
            || Regex.IsMatch(text, @"\b(?:explain|teach|learn)\b.{0,80}\b(?:how|translate)\b|\bhow\s+to\s+translate\b", RegexOptions.IgnoreCase))
            return false;
        var transforms = Regex.Matches(text, @"\btranslate\b", RegexOptions.IgnoreCase);
        if (transforms.Count != 1) return false;
        var translate = transforms[0];
        if (Regex.IsMatch(text[..translate.Index],
            @"\b(?:how|whether|why|say|said|saying|sentence|command|instructions?|possible|capable)\b"
            + @"|\b(?:can|could|should|would)\s+(?:I|we|someone|users?)\b", RegexOptions.IgnoreCase))
            return false;
        var copy = Regex.Matches(text,
            @"\b(?:copy(?:\s+(?:(?:the|my|your|full|entire|complete)\s+)*(?:it|result|translation|output))?(?:\s+(?:to|into|on)\s+(?:(?:my|the)\s+)?clipboard)?"
            + @"|(?:put|place)\s+(?:(?:the|my|your|full|entire|complete)\s+)*(?:it|result|translation|output)\s+(?:on|in|into)\s+(?:(?:my|the)\s+)?clipboard)\b",
            RegexOptions.IgnoreCase);
        if (copy.Count != 1 || copy[0].Index <= translate.Index) return false;
        // Do not seize a compound page/document mutation or another result
        // extraction. Those retain their normal existing tool authority.
        var remaining = text.Remove(copy[0].Index, copy[0].Length);
        if (Regex.IsMatch(remaining, @"\b(?:copy|put|place|open|click|submit|send|paste|fill|upload|delete|extract)\b", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(text[(copy[0].Index + copy[0].Length)..],
                @"^[\s,.!?;]*(?:(?:please|now|thanks|thank\s+you|for\s+me)[\s,.!?;]*)*$", RegexOptions.IgnoreCase))
            return false;
        var clause = text[(translate.Index + translate.Length)..copy[0].Index].Trim();
        clause = Regex.Replace(clause,
            @"[\s,;.!?]+(?:(?:and|then)\s*)?(?:(?:can|could|would|will)\s+you\s*)?(?:please\s*)?$", "", RegexOptions.IgnoreCase).Trim();
        var target = Regex.Match(clause,
            @"^(?<source>.+?)\s+(?:in|into|to)\s+(?<language>[\p{L}][\p{L} -]{0,35})\s*$", RegexOptions.IgnoreCase);
        if (!target.Success) return false;
        source = target.Groups["source"].Value.Trim();
        const string wholeSource = @"(?:(?:all(?:\s+of)?|the|this|that|my|entire|whole|full|complete|original|earlier|previous|prior|fresh|new|newly|different|current|currently)\s+)*"
            + @"(?:(?:selected|highlighted)\s+(?:text|content|page|passage|document)|selection|(?:text|content|page|passage|document)\s+(?:that\s+)?I\s+(?:(?:have|had|just|already)\s+)*(?:selected|highlighted))";
        var supportedSource = Regex.IsMatch(source, "^(?:" + wholeSource + "|it|that|this|this text)$", RegexOptions.IgnoreCase);
        if (!supportedSource && SessionSelectionHistory.TryReferenceLabel(source, out _, out var label))
            supportedSource = Regex.IsMatch(source, @"^(?:(?:the|this|that|my|earlier|previous|prior|original)\s+)*"
                + Regex.Escape(label) + @"\s+(?:selected\s+text|selection)$", RegexOptions.IgnoreCase);
        if (!supportedSource) return false;
        intentRecognized = true;
        if (!TryCanonicalLanguage(target.Groups["language"].Value, out language)) return false;
        return true;
    }

    // Selection intent and fresh-source authority must see the same command.
    // Strip only a leading address to the configured assistant, never quoted
    // content, negation, or words inside the request. Raw Request still owns
    // correlation/deduplication; this does not rewrite the Live transcript.
    internal static string NormalizeRequest(string request)
    {
        var assistant = KeyboardWtfState.AssistantName?.Trim();
        if (string.IsNullOrWhiteSpace(assistant)) return request ?? "";
        return Regex.Replace(request ?? "",
            @"^\s*(?:(?:hi|hello|hey)\s+)?" + Regex.Escape(assistant)
            + @"(?=$|[\s,!:;.?])[\s,!:;.?]*", "", RegexOptions.IgnoreCase);
    }

    private static readonly CultureInfo[] LanguageCultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(culture => culture.Name.Length != 0).ToArray();

    internal static bool TryCanonicalLanguage(string value, out string canonical)
    {
        canonical = "";
        var name = (value ?? "").Trim();
        if (Regex.IsMatch(name, @"\b(?:or|and|not|instead|but)\b", RegexOptions.IgnoreCase)) return false;
        var exact = LanguageCultures.Where(culture =>
            name.Equals(culture.EnglishName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(culture.NativeName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)
            || name.Equals(culture.ThreeLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            .Select(culture => culture.EnglishName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exact.Length == 1) { canonical = exact[0]; return true; }
        // Preserve existing explicit regional qualifiers (e.g. Brazilian
        // Portuguese). Only the actual, exact language suffix is canonicalized.
        var qualified = LanguageCultures.Where(culture =>
                name.EndsWith(" " + culture.EnglishName, StringComparison.OrdinalIgnoreCase))
            .Select(culture => culture.EnglishName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (qualified.Length == 1)
        {
            canonical = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                name[..^(qualified[0].Length)].ToLowerInvariant()) + qualified[0];
            return true;
        }
        // This bounded spelling tolerance applies ONLY to language names,
        // never actions/aliases. Short names/codes are exact-only. Ties and
        // weak matches require clarification, not a guessed target language.
        if (name.Length < 5 || name.Length > 24 || !name.All(char.IsLetter)) return false;
        var limit = name.Length >= 6 ? 2 : 1;
        var candidates = LanguageCultures.Select(culture => culture.EnglishName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(candidate => candidate.All(char.IsLetter) && Math.Abs(candidate.Length - name.Length) <= limit)
            .Select(candidate => (Name: candidate, Distance: LanguageSpellingDistance(name, candidate)))
            .Where(candidate => candidate.Distance <= limit && candidate.Distance * 3 <= Math.Max(name.Length, candidate.Name.Length))
            .OrderBy(candidate => candidate.Distance).ToArray();
        if (candidates.Length == 0 || candidates.Length > 1 && candidates[0].Distance == candidates[1].Distance) return false;
        canonical = candidates[0].Name;
        return true;
    }

    private static int LanguageSpellingDistance(string source, string target)
    {
        source = source.ToLowerInvariant();
        target = target.ToLowerInvariant();
        var distance = new int[source.Length + 1, target.Length + 1];
        for (var row = 0; row <= source.Length; row++) distance[row, 0] = row;
        for (var column = 0; column <= target.Length; column++) distance[0, column] = column;
        for (var row = 1; row <= source.Length; row++)
            for (var column = 1; column <= target.Length; column++)
            {
                distance[row, column] = Math.Min(Math.Min(distance[row - 1, column] + 1, distance[row, column - 1] + 1),
                    distance[row - 1, column - 1] + (source[row - 1] == target[column - 1] ? 0 : 1));
                if (row > 1 && column > 1 && source[row - 1] == target[column - 2] && source[row - 2] == target[column - 1])
                    distance[row, column] = Math.Min(distance[row, column], distance[row - 2, column - 2] + 1);
            }
        return distance[source.Length, target.Length];
    }

    internal static bool IsExplicitCancellation(string request) => Regex.IsMatch(request ?? "",
        @"^\s*(?:(?:okay|ok|please)[,\s]+)*(?:cancel(?:\s+(?:(?:the|that|this|my)\s+)?(?:translation|translating|task|operation|it|that))?"
        + @"|stop\s+(?:(?:the|that|this|my)\s+)?(?:translation|translating))"
        + @"(?:\s+(?:now|please))?[.!?]*\s*$", RegexOptions.IgnoreCase);

    private static Dictionary<string, object> Result(bool ok, string message) => new()
    {
        ["ok"] = ok, ["message"] = message, ["clipboard_unchanged"] = true,
    };

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
        }
        // CancellationToken registrations can still be unwinding in HTTP.
        _ = Completion.ContinueWith(_ => _cancellation.Dispose(), TaskScheduler.Default);
    }
}
