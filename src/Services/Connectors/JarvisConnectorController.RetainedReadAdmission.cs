namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;
using KeyboardWtf.Models;

internal sealed partial class JarvisConnectorController
{
    // A local receipt for an explicit finalized retained-object follow-up, not
    // a provider tool call or new source of task authority. Reads/openings use
    // their existing executors; collection uses the existing broker.
    private readonly List<AdmittedRead> _admittedReads = [];

    internal Task<Dictionary<string, object>> TryCollectAdmittedReadProposalAsync(string name, JsonElement args,
        ConnectorHumanRequestSnapshot human, CancellationToken token)
    {
        // This cannot admit a connector request. An already admitted explicit
        // follow-up owns this exact human turn, independent of the model's
        // proposed local tool. Other human requests keep their normal owner.
        if (name == "connector_control" && Text(args, "action") is "cancel" or "complete" or "change_account")
            return Task.FromResult<Dictionary<string, object>>(null);
        AdmittedRead admitted;
        lock (_sync)
        {
            admitted = _admittedReads.LastOrDefault(read => read.MatchesDeliveryHuman(human)
                && (!read.LaterInputObserved || read.StatusHuman == human) && AdmissionCurrent(read));
            // Live may propose a tool after its authoritative status transcript
            // but before ACTIVITY_END. Joining an existing receipt is not new
            // read/open authority, so it need not wait for that final-input hook.
            admitted ??= BindRetainedReadStatus(human, token);
        }
        return admitted is null ? Task.FromResult<Dictionary<string, object>>(null)
            : CollectAdmittedReadProposalAsync(admitted, name, args, token);
    }

    private async Task<Dictionary<string, object>> CollectAdmittedReadProposalAsync(AdmittedRead read, string proposedTool,
        JsonElement args, CancellationToken token)
    {
        lock (_sync)
        {
            var human = _userRequestSnapshot?.Invoke();
            var exactResult = proposedTool == "connector_control" && Text(args, "action") == "result"
                && Text(args, "operation_id") == read.OperationId && Text(args, "task_id") == read.ContextTaskId;
            if (!exactResult && read.StatusHuman is not null && read.StatusHuman == human)
            {
                if (read.StatusReceipt is not null) return read.StatusReceipt;
                if (!read.Receipt.Task.IsCompleted)
                {
                    // A status question joins the existing receipt; it grants
                    // no new read, opening or permission and must not wait for
                    // a slow provider merely to report that work continues.
                    read.StatusReceipt = new()
                    {
                        ["ok"] = true, ["started"] = true, ["state"] = "executing",
                        ["task_id"] = read.ContextTaskId, ["operation_id"] = read.OperationId,
                        ["actual_operation"] = "status_retained_conversation",
                        ["reply_owner"] = "connector_status_receipt", ["proposed_action_dispatched"] = false,
                        ["message"] = "The connected-service task is still working on the earlier request. No new action was started.",
                        ["instruction"] = "Give one concise, truthful progress answer. The original read/open task is still owned and will deliver its verified result when ready. "
                            + "This is not a browser, connector or capability failure. Do not start another search, read, browser task or opening and do not claim completion."
                    };
                    return read.StatusReceipt;
                }
            }
        }
        var result = await CollectAdmittedReadAsync(read, token);
        lock (_sync)
            if (read.StatusHuman is not null && read.StatusHuman == _userRequestSnapshot?.Invoke()) read.StatusReceipt = result;
        if (read.ReadAndOpen) return result;
        if (read.Open is not null)
            return new(result, StringComparer.Ordinal)
            {
                ["actual_operation"] = "open_retained_object", ["proposed_action_dispatched"] = false,
                ["instruction"] = "This is the receipt for the current human's retained-object opening, already handled through its verified references. "
                    + "Use opened_count and resolved_objects to report what opened; navigation_verified=false means rendering was not verified. "
                    + "Do not open another URL, re-search, or describe this as a message read. Repeated tool proposals cannot repeat the tabs."
            };
        // The returned new task/operation already identifies a successful
        // retained read. Do not deny that semantic read merely because the
        // model supplied the old execution ID; recovery did the requested work.
        if (proposedTool == "connector_resolve"
            || proposedTool == "connector_execute" && Text(args, "capability_id") is "mail_search" or "mail_read" or "mail_thread"
            || proposedTool == "connector_control" && Text(args, "action") is "result" or "recall" or "status")
            return result;
        // Tool selection is not the user's intent. A successful read returned
        // to an unrelated proposal must not look like that proposal succeeded.
        // Copy the shared operation receipt so another collection cannot
        // change the provenance of this tool response.
        return new(result, StringComparer.Ordinal)
        {
            ["actual_operation"] = "read_retained_conversation",
            ["proposed_action_dispatched"] = false,
            ["browser_action_performed"] = false,
            ["instruction"] = "This receipt is the user's retained email read, not execution of the proposed " + proposedTool
                + ". It did not open a browser, tab, website or app and did not perform a write. "
                + (result.GetValueOrDefault("instruction") as string ?? "")
        };
    }

    internal Action ReserveRetainedRead(string text, CancellationToken token,
        Func<string, CancellationToken, Task<Dictionary<string, object>>> open = null)
    {
        text = (text ?? "").Trim();
        var status = ReserveRetainedReadStatus(text, token);
        if (status is not null) return status;
        var readAndOpen = open is not null && ConnectorFollowupIntent.TryReadAndOpen(text, out _, out _);
        var exactOpen = open is not null && ConnectorFollowupIntent.IsExactOpen(text);
        if (!exactOpen && !ConnectorFollowupIntent.MailDetails(text) && !ConnectorFollowupIntent.Affirmative(text)) return null;
        lock (_sync)
        {
            PruneRetainedResults();
            if (token.IsCancellationRequested || _session().IsCancellationRequested || _sessionResults.Count == 0
                || _resultOwner != _accountOwner()) return null;
            var context = LatestMatchingOpenResult(text);
            if (context is null || !exactOpen && context.Provider != "gmail") return null;
            // A candidate set needs the model's observed native selection.
            // Do not preempt that tool with a cached ambiguity receipt before
            // the model can request the relevant thread. Single-thread reads
            // and combined read/open ownership retain their existing path.
            if (!exactOpen && !readAndOpen && ConnectorRetainedMailSelection.Thread(context,
                    ConnectorFollowupIntent.MailSelection(text)) is null) return null;
            if (exactOpen && ConnectorFollowupIntent.TryParseExactOpen(text, out var openingSelection)
                && !context.TryResolveExactOpen(openingSelection, out _, out _, text)) return null;
            // A different provider's completed task can dispose the earlier
            // execution owner without discarding its bounded result. Explicit
            // typed reads resolve fresh account authority in the same existing
            // recovery path; a bare follow-up still belongs to the latest object.
            _tasks.TryGetValue(context.TaskId, out var origin);
            if (origin is not null && (!IsOriginCurrent(origin) || origin.State == "cancelled"
                    || origin.LogicalLiveSessionId is not null
                        && origin.LogicalLiveSessionId != NormalizeLogicalLiveSessionId(_logicalLiveSessionId?.Invoke()))
                || ConnectorFollowupIntent.Affirmative(text) && _detailOfferTaskId != context.TaskId) return null;
            var human = _userRequestSnapshot?.Invoke();
            if (_admittedReads.Any(read => AdmissionCurrent(read) && read.Text == text
                    && (read.Human is null || read.Human == human))) return null;
            _admittedReads.RemoveAll(read => !AdmissionCurrent(read) || read.Delivered);
            if (_admittedReads.Count >= 8) return null;
            // Freeze the read's verified selection before either operation can
            // replace the latest retained result. Ambiguity is handled by the
            // read and must never cause the open half to choose another object.
            var selectedThread = readAndOpen ? ConnectorRetainedMailSelection.Thread(context,
                ConnectorFollowupIntent.MailSelection(text)) : null;
            var accepted = new AdmittedRead(text, context.TaskId, _resultOwner, _generation, context.ExpiresAt,
                NormalizeLogicalLiveSessionId(_logicalLiveSessionId?.Invoke()),
                CancellationTokenSource.CreateLinkedTokenSource(token, _session()))
                { Open = exactOpen || readAndOpen ? open : null, ReadAndOpen = readAndOpen,
                    OpenContext = readAndOpen ? context : null, SelectedThread = selectedThread };
            _admittedReads.Add(accepted);
            // Called only by the ordered transcript dispatcher (or after an
            // explicit local text submission). The reservation itself performs
            // no network work and cannot rewrite ordinary routing context.
            return () => { _ = RunAdmittedReadAsync(accepted); };
        }
    }

    private Action ReserveRetainedReadStatus(string text, CancellationToken token)
    {
        lock (_sync)
        {
            // Live can extend its current ASR buffer with the later status
            // question instead of starting a new text buffer. Match only the
            // exact already-owned request followed by a pure bounded status;
            // neither another target nor a permission update is a status.
            var statusText = IsRetainedReadStatusQuestion(text)
                || _admittedReads.Any(read => IsRetainedReadStatusQuestion(read, text));
            if (!statusText)
            {
                // Only finalized input reaches this boundary. Incremental ASR
                // prefixes can release a join wait, but cannot authorize or
                // permanently invalidate a status continuation on their own.
                foreach (var existing in _admittedReads.Where(read => !read.Delivered
                    && !read.Text.Equals(text, StringComparison.OrdinalIgnoreCase)))
                    existing.StatusEligible = false;
                return null;
            }
            var pending = _admittedReads.Where(read => !read.Delivered && AdmissionCurrent(read)).ToArray();
            if (token.IsCancellationRequested || pending.Length != 1) return null;
            var read = pending[0];
            if (!read.StatusEligible || !read.Started || read.Human is null
                || !IsRetainedReadStatusQuestion(read, text)) return null;
            read.PendingStatusText = text;
            return () =>
            {
                lock (_sync)
                {
                    var human = _userRequestSnapshot?.Invoke();
                    if (human?.Text == text) BindRetainedReadStatus(human, token, read);
                }
            };
        }
    }

    // Called under the owner lock from either final-input reservation or the
    // broker boundary. Only the observed current human text can join this
    // uniquely owned operation; proposed tool arguments contribute nothing.
    private AdmittedRead BindRetainedReadStatus(ConnectorHumanRequestSnapshot human, CancellationToken token,
        AdmittedRead expected = null)
    {
        if (token.IsCancellationRequested || human is null || string.IsNullOrEmpty(human.Identity)
            || human != _userRequestSnapshot?.Invoke()) return null;
        if (!IsRetainedReadStatusQuestion(human.Text)
            && !_admittedReads.Any(read => IsRetainedReadStatusQuestion(read, human.Text))) return null;
        var pending = _admittedReads.Where(read => !read.Delivered && AdmissionCurrent(read)).Take(2).ToArray();
        if (pending.Length != 1) return null;
        var read = pending[0];
        if (expected is not null && expected != read || !read.StatusEligible || !read.Started || read.Human is null
            || !IsRetainedReadStatusQuestion(read, human.Text)) return null;
        if (read.StatusHuman != human) read.StatusReceipt = null;
        read.StatusHuman = human;
        read.PendingStatusText = null;
        // Keep the old proposal's handoff intact: it may have been cancelled
        // by the provider. This alias owns only status/result delivery; the
        // original execution identity, account, scope and permission stay.
        return read;
    }

    private static bool IsRetainedReadStatusQuestion(string text) => text?.Length is > 0 and <= 200
        && System.Text.RegularExpressions.Regex.IsMatch(text,
            @"^\s*(?:what(?:'s|’s|\s+is)\s+taking\s+so\s+long|how\s+much\s+longer(?:\s+will\s+(?:it|this|that)\s+take)?|is\s+(?:it|the\s+search|the\s+task)\s+still\s+(?:searching|running|working)|are\s+you\s+still\s+searching)[.!?]?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                | System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    private static bool IsRetainedReadStatusQuestion(AdmittedRead read, string text)
        => IsRetainedReadStatusQuestion(text)
            || read.Text.Length > 0 && text is not null && text.Length > read.Text.Length
                && text.StartsWith(read.Text, StringComparison.Ordinal)
                && char.IsWhiteSpace(text[read.Text.Length])
                && IsRetainedReadStatusQuestion(text[read.Text.Length..]);

    private bool AdmittedOpeningStillAuthorized(AdmittedRead read)
    {
        lock (_sync)
        {
            var current = _userRequestSnapshot?.Invoke();
            return AdmissionCurrent(read) && current is not null
                && (read.Human == current || read.StatusEligible && read.StatusHuman is not null
                    && (read.StatusHuman == current || current.Text.Length == 0
                        && read.StatusHuman.Identity == current.Identity));
        }
    }

    private async Task RunAdmittedReadAsync(AdmittedRead read)
    {
        try
        {
            lock (_sync)
            {
                if (read.Started) return;
                read.Started = true;
                var human = _userRequestSnapshot?.Invoke()
                    ?? new ConnectorHumanRequestSnapshot(_userRequest(), _userRequestIdentity?.Invoke() ?? "");
                if (!AdmissionCurrent(read) || human.Text != read.Text || string.IsNullOrEmpty(human.Identity))
                { read.Lifetime.Cancel(); return; }
                read.Human = human;
            }
            // Bypass the join method: this is the sole owner of this read.
            var result = read.ReadAndOpen
                ? await ReadAndOpenRetainedAsync(read)
                : read.Open is null
                ? await TryReadRetainedThreadAsync("admitted_read", default, read.Lifetime.Token, waitForFinalReceipt: true)
                : await TryOpenRetainedResourcesAsync(read.Human, read.Open, read.Lifetime.Token,
                    currentHumanStillAuthorized: () => AdmittedOpeningStillAuthorized(read));
            read.Receipt.TrySetResult(result ?? Failure(read.Open is null
                ? "The retained conversation changed before this follow-up could read it. Please select the conversation again."
                : "The retained object changed before it could open. Please select the object again; no guessed URL was opened."));
        }
        catch (OperationCanceledException) when (read.Lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLog.Info("Connector admitted read failed: errorType=" + error.GetType().Name);
            var (code, message) = error switch
            {
                PaidStaging.PaidApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized } =>
                    ("authentication_required", "The connector session could not be authenticated. Sign in again before this new read."),
                PaidStaging.PaidApiException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
                    ("permission_denied", "The connector backend denied permission for this read."),
                PaidStaging.PaidApiException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } =>
                    ("connector_rate_limit", "The connector backend temporarily rate-limited this read."),
                PaidStaging.PaidApiException => ("connector_backend_unavailable", "The connector backend could not complete this read."),
                HttpRequestException => ("network_failure", "A network failure prevented this connector read."),
                OperationCanceledException => ("connector_timeout", "The connector read timed out before it returned a result."),
                JsonException => ("invalid_connector_response", "The connector backend returned an unreadable response."),
                _ => ("connector_followup_failed", "The connected-service follow-up could not finish.")
            };
            var failed = Failure(message + " The earlier retrieved result remains available.");
            failed["error_code"] = code;
            lock (_sync)
            {
                // This admission owns only this freshly resolved read. Keep
                // earlier conversation evidence and other tasks untouched.
                if (read.ExecutionTaskId is not null && _tasks.TryGetValue(read.ExecutionTaskId, out var task)
                    && IsOriginCurrent(task) && task.UserRequestIdentity == read.Human?.Identity
                    && task.State is not ("cancelled" or "completed" or "outcome_unknown"))
                {
                    task.State = "failed";
                    task.ResearchActive = false;
                    task.Cancellation.Cancel();
                    KeyboardWtfState.ClearLongTaskUi("connector_" + task.Id);
                    failed["task_id"] = task.Id;
                    var faulted = task.Operations.FirstOrDefault(pair => pair.Value.IsFaulted).Key;
                    if (faulted is not null) failed["operation_id"] = OperationId(faulted);
                    _ = StopServerAsync(task.Id, task.Owner, "cancel");
                }
            }
            if (read.ReadAndOpen)
            {
                failed["read_succeeded"] = false;
                failed["open_succeeded"] = false;
                failed["opened_count"] = 0;
                failed["browser_action_performed"] = false;
            }
            read.Receipt.TrySetResult(failed);
        }
        finally
        {
            if (read.Lifetime.IsCancellationRequested) read.Receipt.TrySetCanceled(read.Lifetime.Token);
        }
        bool NeedsDelivery() { lock (_sync) return AdmissionCurrent(read) && !read.Delivered; }
        if (!NeedsDelivery()) return;
        try
        {
            var delivered = await _announce(new JarvisLifecycleAnnouncement
            {
                Source = "connector", State = "settled",
                Detail = (read.Open is null || read.ReadAndOpen ? "The requested retained-conversation follow-up settled. task_id="
                    : "The requested exact retained-object opening settled. task_id=") + read.ContextTaskId
                    + "; operation_id=" + read.OperationId + ". Retrieve this exact local result using connector_control action=result before answering. No new user authority is granted.",
                IsCurrent = NeedsDelivery,
                OwnsCurrentReply = () =>
                {
                    lock (_sync) return AdmissionCurrent(read) && !read.Delivered
                        && read.HoldsDeliveryReply(_userRequestSnapshot?.Invoke());
                }
            }, read.Lifetime.Token);
            if (!delivered) NotifyAdmittedReadUnavailable(read);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or HttpRequestException)
        {
            AppLog.Info("Connector admitted-read notice unavailable: errorType=" + error.GetType().Name);
            NotifyAdmittedReadUnavailable(read);
        }
    }

    private async Task<Dictionary<string, object>> ReadAndOpenRetainedAsync(AdmittedRead read)
    {
        var details = await TryReadRetainedThreadAsync("admitted_read", default, read.Lifetime.Token,
            waitForFinalReceipt: true);
        if (details is null || details.GetValueOrDefault("ok") is not true) return details;
        // A useful partial native answer survives a reasoning outage, but an
        // ambiguous read cannot establish which conversation the human meant.
        if (read.SelectedThread is null) return new(details, StringComparer.Ordinal)
        {
            ["actual_operation"] = "read_and_open_retained_conversation",
            ["opened_count"] = 0,
            ["opening"] = new { ok = false, state = "clarification_required",
                message = "Which retrieved conversation should I open? No tab was opened." },
            ["instruction"] = "The read evidence remains usable, but the opening has no uniquely selected conversation. "
                + "Give the requested verified details within their coverage and ask which conversation to open. Do not claim a tab opened. "
                + (details.GetValueOrDefault("instruction") as string ?? "")
        };
        read.Lifetime.Token.ThrowIfCancellationRequested();
        ConnectorSessionResult selected;
        lock (_sync)
        {
            if (!AdmissionCurrent(read)) throw new OperationCanceledException(read.Lifetime.Token);
            selected = read.OpenContext;
            // A fresh actual read may improve references, but can only open
            // the original account/thread selected before retrieval began.
            if (details.GetValueOrDefault("task_id") is string taskId
                && _sessionResults.TryGetValue(taskId, out var fresh)
                && fresh.AccountId == selected.AccountId && fresh.UniqueMailThread() == read.SelectedThread)
                selected = fresh;
        }
        var opened = await TryOpenRetainedResourcesAsync(read.Human, read.Open,
            read.Lifetime.Token, selected, read.SelectedThread, () => AdmittedOpeningStillAuthorized(read));
        var openedCount = opened?.GetValueOrDefault("opened_count") is int count ? count : 0;
        var openingSucceeded = opened?.GetValueOrDefault("ok") is true;
        var result = new Dictionary<string, object>(details, StringComparer.Ordinal)
        {
            ["actual_operation"] = "read_and_open_retained_conversation",
            ["proposed_action_dispatched"] = false,
            ["read_succeeded"] = true,
            ["browser_action_performed"] = openedCount > 0,
            ["open_succeeded"] = openingSucceeded,
            ["message"] = (openingSucceeded
                ? $"Browser dispatch succeeded for {openedCount} retained conversation; page rendering was not verified. "
                : "Browser dispatch was not confirmed. ") + (details.GetValueOrDefault("message") as string ?? ""),
            ["opening"] = opened,
            ["retained_object_open"] = opened?.GetValueOrDefault("retained_object_open") is true,
            ["opened_count"] = openedCount,
            ["resolved_objects"] = opened?.GetValueOrDefault("resolved_objects"),
            ["navigation_verified"] = false,
            ["instruction"] = "The current human requested BOTH details and an exact opening of the same retained conversation. "
                + "The read evidence in this receipt is the requested answer: give its useful details now, respecting partial coverage and content restrictions. "
                + "The controller's open_succeeded/opened_count report the separate browser dispatch; navigation_verified=false means rendering is unverified, not that dispatch failed. "
                + "Do not repeat either operation from another model proposal or substitute an inactive-task message. "
                + (details.GetValueOrDefault("instruction") as string ?? "")
        };
        return result;
    }

    private void NotifyAdmittedReadUnavailable(AdmittedRead read)
    {
        lock (_sync)
        {
            if (!AdmissionCurrent(read) || read.Delivered) return;
            read.NoticeUnavailable = true;
            foreach (var task in _tasks.Values.Where(task => task.UserRequestIdentity == read.Human?.Identity))
                KeyboardWtfState.ClearLongTaskUi("connector_" + task.Id);
        }
        try { _notify?.Invoke("Connected-service update", read.Open is null
            ? "The email follow-up finished, but its spoken update was unavailable. Ask Jarvis for the retained result."
            : "The retained-object opening finished, but its spoken receipt was unavailable. Ask Jarvis for its status; tabs will not be repeated."); }
        catch (Exception error) { AppLog.Info("Connector admitted-read visual notice unavailable: errorType=" + error.GetType().Name); }
    }

    private AdmittedRead FindAdmittedRead(string name, JsonElement args)
    {
        lock (_sync)
        {
            if (name == "connector_control" && Text(args, "action") == "result")
            {
                var operation = Text(args, "operation_id");
                var task = Text(args, "task_id");
                var exact = _admittedReads.FirstOrDefault(read => read.OperationId == operation && read.ContextTaskId == task
                    && AdmissionCurrent(read));
                if (exact is not null) return exact;
            }
            var human = _userRequestSnapshot?.Invoke();
            return human is null ? null : _admittedReads.LastOrDefault(read => read.MatchesDeliveryHuman(human) && AdmissionCurrent(read));
        }
    }

    private async Task<Dictionary<string, object>> CollectAdmittedReadAsync(AdmittedRead read, CancellationToken token)
    {
        Dictionary<string, object> result;
        TaskCompletionSource<bool> handoff;
        lock (_sync)
        {
            read.JoinHandoff = handoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (read.LaterInputObserved) handoff.TrySetResult(true);
        }
        try
        {
            await Task.WhenAny(read.Receipt.Task, handoff.Task).WaitAsync(token);
            if (!read.Receipt.Task.IsCompleted) return new()
            {
                ["ok"] = true, ["started"] = true, ["task_id"] = read.ContextTaskId,
                ["operation_id"] = read.OperationId, ["state"] = "executing",
                ["reply_owner"] = "connector_pending_result", ["instruction"] = PendingReadInstruction
            };
            result = await read.Receipt.Task;
        }
        catch (OperationCanceledException) when (read.Lifetime.IsCancellationRequested)
        { return Failure("The connector follow-up was cancelled. No late result was published."); }
        finally { lock (_sync) if (ReferenceEquals(read.JoinHandoff, handoff)) read.JoinHandoff = null; }
        lock (_sync)
        {
            if (!AdmissionCurrent(read)) return Failure("The originating connector account or session changed. No result was published.");
            read.Delivered = true;
            // Retrieval may settle before an unrelated intervening turn. The
            // actual collection turn owns completion/cleanup, not that earlier
            // worker's internal receipt construction.
            if (result.TryGetValue("task_id", out var id) && id is string taskId && _tasks.TryGetValue(taskId, out var task))
            {
                task.LastDeliveryIdentity = _userRequestIdentity?.Invoke() ?? read.Human.Identity;
                if (result.TryGetValue("operation_id", out var operation))
                    foreach (var key in task.Operations.Keys.Where(key => OperationId(key) == operation as string))
                        task.DeliveredOperations.Add(key);
                if (task.State != "ready") KeyboardWtfState.ClearLongTaskUi("connector_" + task.Id);
            }
            return result;
        }
    }

    private bool AdmissionCurrent(AdmittedRead read) => !read.Lifetime.IsCancellationRequested
        && read.Generation == _generation && read.Owner == _accountOwner() && AdmissionLeaseCurrent(read)
        && read.LogicalSession == NormalizeLogicalLiveSessionId(_logicalLiveSessionId?.Invoke())
        && (read.Human is null || !_tasks.Values.Any(task => task.UserRequestIdentity == read.Human.Identity && task.State == "cancelled"));

    private bool AdmissionLeaseCurrent(AdmittedRead read) => read.ExecutionTaskId is null
        // A completed browser dispatch is a fact to acknowledge, not renewed
        // opening authority. New admissions still require an unexpired cache.
        ? read.ExpiresAt > _utcNow() || read.Open is not null && !read.ReadAndOpen
            && read.Receipt.Task.IsCompletedSuccessfully
            && read.Receipt.Task.Result.GetValueOrDefault("retained_object_open") is true
        // An accepted fresh read owns its existing execution lease. Expiring
        // the older result cache cannot revoke that new task's delivery.
        : _tasks.TryGetValue(read.ExecutionTaskId, out var task) && IsOriginCurrent(task)
            && task.UserRequestIdentity == read.Human?.Identity && task.ExpiresAt > _utcNow()
            && !task.AccountChanging && task.State is not ("cancelled" or "outcome_unknown");

    private void CancelAdmittedReads(string identity)
    {
        foreach (var read in _admittedReads.Where(read => read.Human?.Identity == identity)) read.Lifetime.Cancel();
    }

    private bool TryCancelAdmittedRead(string text, out Dictionary<string, object> result)
    {
        result = null;
        if (!TryParseExplicitCancellation(text, out var provider) || provider.Length > 0 && provider != "gmail") return false;
        var pending = _admittedReads.Where(read => !read.Delivered && AdmissionCurrent(read)).Take(2).ToArray();
        if (pending.Length != 1) return false;
        pending[0].Lifetime.Cancel();
        result = new() { ["ok"] = true, ["state"] = "cancelled", ["message"] = "The connector follow-up was cancelled." };
        return true;
    }

    private sealed class AdmittedRead(string text, string task, string owner, long generation, DateTimeOffset expiresAt,
        string logicalSession, CancellationTokenSource lifetime)
    {
        internal readonly string Text = text, ContextTaskId = task, Owner = owner, LogicalSession = logicalSession;
        internal readonly string OperationId = Guid.NewGuid().ToString("N");
        internal readonly long Generation = generation;
        internal readonly DateTimeOffset ExpiresAt = expiresAt;
        internal readonly CancellationTokenSource Lifetime = lifetime;
        internal readonly TaskCompletionSource<Dictionary<string, object>> Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConnectorHumanRequestSnapshot Human;
        internal string ExecutionTaskId;
        internal ConnectorHumanRequestSnapshot StatusHuman;
        internal string PendingStatusText;
        internal Dictionary<string, object> StatusReceipt;
        internal Func<string, CancellationToken, Task<Dictionary<string, object>>> Open;
        internal ConnectorSessionResult OpenContext;
        internal string SelectedThread;
        internal TaskCompletionSource<bool> JoinHandoff;
        internal bool Started, Delivered, NoticeUnavailable, LaterInputObserved, ReadAndOpen;
        internal bool StatusEligible = true;
        internal bool MatchesDeliveryHuman(ConnectorHumanRequestSnapshot human) => human is not null
            && (Human is null || Human == human || StatusEligible && (StatusHuman == human
                || PendingStatusText is not null && PendingStatusText == human.Text));
        internal bool HoldsDeliveryReply(ConnectorHumanRequestSnapshot human) => MatchesDeliveryHuman(human)
            && !(StatusHuman == human && StatusReceipt is not null);
    }
}
