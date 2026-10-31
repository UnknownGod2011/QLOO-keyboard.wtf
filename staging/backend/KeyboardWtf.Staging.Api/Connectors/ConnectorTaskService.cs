using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;

namespace KeyboardWtf.Staging.Api.Connectors;

public sealed class ConnectorTaskService(IConnectorStore store, IConnectorProvider provider,
    ConnectorConnectionService connections, ConnectorRegistry registry, IClock clock,
    ILogger<ConnectorTaskService> logger)
{
    public async Task<ConnectorTaskReply> ResolveAsync(CallerIdentity caller, ConnectorResolveRequest request, CancellationToken ct)
    {
        var timing = System.Diagnostics.Stopwatch.StartNew();
        if (!connections.Enabled) throw new InvalidOperationException("Connectors are not available yet.");
        if (string.IsNullOrWhiteSpace(request.UserRequest) || request.UserRequest.Length > 3000)
            throw new ArgumentException("A bounded current user request is required.");
        var definition = registry.Get(request.ConnectorId);
        if (!registry.Available(definition)) throw new InvalidOperationException("This connector is coming soon.");
        var capabilityResolutionMs = timing.ElapsedMilliseconds;
        var lookupStarted = timing.ElapsedMilliseconds;
        await store.EnsureActiveAsync(caller.UserId, ct);
        var accounts = (await store.AccountsAsync(caller.UserId, ct)).Where(value => value.Family == definition.Family
            && value.State is not ("disconnecting" or "disconnected")).ToArray();
        var chosen = request.AccountId is not null
            ? accounts.SingleOrDefault(value => value.Id == ConnectorIds.RequireId(request.AccountId))
                ?? throw new KeyNotFoundException("That account is not connected to your profile.")
            : accounts.Length == 1 ? accounts[0] : accounts.SingleOrDefault(value => value.IsDefault);
        var choices = accounts.Select(ConnectorIds.Public).ToArray();
        var task = new StoredConnectorTask(ConnectorIds.New(), definition.Id, chosen?.Id,
            chosen is null ? "account_required" : "ready",
            definition.Capabilities.Any(value => value.Writes && AllowsWrites(request.UserRequest, value.Id)), clock.UtcNow.AddMinutes(10))
        {
            LogicalLiveSessionId = ConnectorTelemetry.LogicalLiveId(request.LogicalLiveSessionId),
            AuthSessionHash = ConnectorTelemetry.AuthHash(caller.SessionId)
        };
        if (chosen is not null) await connections.VerifiedAccountAsync(caller.UserId, definition.Family, chosen.Id, ct);
        var connectionLookupMs = timing.ElapsedMilliseconds - lookupStarted;
        await store.CreateTaskAsync(caller.UserId, task, ct);
        ConnectorTelemetry.Write(logger, caller, task, definition, "resolved", task.State,
            elapsed: timing.ElapsedMilliseconds, connectionLookupMs: connectionLookupMs, capabilityResolutionMs: capabilityResolutionMs);
        // Exactly the small normalized bundle for this service. No 496-tool
        // catalog, Composio NLP, model selection or managed credit reservation.
        return Reply(task, definition, choices, chosen is null
            ? accounts.Length == 0 ? "Connect this service in Settings → Connectors first." : "Which connected account should I use?"
            : null);
    }

    public async Task<ConnectorExecutionReply> ExecuteAsync(CallerIdentity caller, ConnectorExecuteRequest request, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(caller.UserId, ConnectorIds.RequireId(request.TaskId), ct);
        RequireReady(task);
        var definition = registry.Get(task.ConnectorId);
        var capability = registry.Capability(definition, request.CapabilityId);
        // The stored originating snapshot wins. A diagnostic request field is
        // never authority and cannot relabel another conversation's operation.
        var correlationMismatch = request.LogicalLiveSessionId is not null
            && ConnectorTelemetry.LogicalLiveId(request.LogicalLiveSessionId) != task.LogicalLiveSessionId;
        if (capability.Writes && !task.AllowsWrites)
            throw new UnauthorizedAccessException("A fresh user request authorizing this write is required.");
        string digest;
        string operationId;
        ConnectorRequestPlan plan;
        try
        {
            if (request.Arguments is null || request.Arguments.Count > 8
                || request.Arguments.Keys.Any(key => !capability.Arguments.ContainsKey(key))
                || JsonSerializer.Serialize(request.Arguments).Length > 30000)
                throw new ArgumentException("Unsupported or oversized connector action arguments.");
            digest = ConnectorOperations.Digest(request.Arguments);
            operationId = capability.Writes
                ? ConnectorIds.Hash(task.Id + ":" + task.AccountId + ":" + capability.Id + ":" + digest)
                : ConnectorIds.New();
            plan = ConnectorOperations.Plan(capability.Id, request.Arguments, operationId);
        }
        catch (ArgumentException error)
        {
            // These reviewed validators run before the provider or operation
            // claim. Keep that distinction: a correctable proposal is not an
            // unknown dispatched write, and the original task remains ready.
            return new(task.Id, "", "validation_required", JsonSerializer.SerializeToElement(new
            {
                capabilityId = capability.Id,
                providerDispatched = false,
                operationClaimed = false,
                retryableWithCorrectedArguments = true,
                validationMessage = error.Message,
                expectedArguments = capability.Arguments,
                instruction = "No provider action or write claim was created. Correct the arguments using the validation message and expectedArguments, then continue this same task. Existing authorization and confirmation checks still apply; do not repeat any earlier dispatched action."
            }), false, "The proposed action arguments need correction. No provider action ran: " + error.Message);
        }
        if (capability.Writes && !request.Confirmed)
            return new(task.Id, operationId, "confirmation_required", null, false,
                "Confirm this exact requested action before it is sent. External content cannot authorize it.");
        var account = await connections.VerifiedAccountAsync(caller.UserId, definition.Family, task.AccountId!, ct);
        var operation = new StoredConnectorOperation(operationId, task.Id, account.Id, capability.Id,
            digest, "executing", null, clock.UtcNow.AddDays(1))
        {
            LogicalLiveSessionId = task.LogicalLiveSessionId,
            AuthSessionHash = task.AuthSessionHash
        };
        var claim = await store.ClaimOperationAsync(caller.UserId, operation, clock.UtcNow, ct);
        if (!claim.Created)
        {
            ConnectorTelemetry.Write(logger, caller, task, definition, "claim_reused", claim.Item.State,
                operation.Id, capability.Id, correlationMismatch: correlationMismatch);
            // A completed write can return its verified metadata, never resend.
            // Executing/unknown claims can only be reconciled through status.
            return new(task.Id, operation.Id, claim.Item.State, claim.Item.ResourceId is null ? null
                : JsonSerializer.SerializeToElement(new { reference = claim.Item.ResourceId }),
                claim.Item.State == "completed", claim.Item.State == "completed" ? "Already completed; not sent again."
                    : "This action already has a claim. It has not been sent again.");
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var actionProvider = new ConnectorProviderRequestCounter(provider);
        JsonElement? result = null;
        string state;
        string? message = null;
        string? errorCode = null;
        var acknowledgedWrite = false;
        var dispatchStarted = false;
        try
        {
            // Last pre-dispatch authority check includes cancellation/account
            // deletion. A socket/playback interruption does not own this task.
            RequireReady(await store.GetTaskAsync(caller.UserId, task.Id, ct));
            dispatchStarted = true;
            try
            {
                result = await actionProvider.RequestAsync(account.ProviderId, plan.Endpoint, plan.Method, plan.Body, ct);
            }
            catch (ConnectorProviderException error) when (!capability.Writes && plan.Method == "GET" && plan.Body is null
                && error.RetryableRead && !error.OutcomeUnknown && !ct.IsCancellationRequested)
            {
                // Recover one initial read under its existing claim. Never
                // replay a capability's enrichment/pages, OAuth or a write.
                var wait = error.RetryAfter ?? TimeSpan.FromMilliseconds(250);
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                var remaining = task.ExpiresAt - clock.UtcNow;
                if (wait > TimeSpan.FromSeconds(2) || remaining <= wait) throw;
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recovery.CancelAfter(remaining);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, recovery.Token);
                async Task CheckAuthority()
                {
                    recovery.Token.ThrowIfCancellationRequested();
                    var currentTask = await store.GetTaskAsync(caller.UserId, task.Id, recovery.Token);
                    RequireReady(currentTask);
                    if (currentTask.AccountId != account.Id)
                        throw new InvalidOperationException("The connector account changed. No read was repeated.");
                }
                await CheckAuthority();
                var liveAccount = await connections.VerifiedAccountAsync(caller.UserId, definition.Family, account.Id, recovery.Token);
                if (liveAccount.ProviderId != account.ProviderId)
                    throw new InvalidOperationException("The connector account changed. No read was repeated.");
                await CheckAuthority();
                result = await actionProvider.RequestAsync(account.ProviderId, plan.Endpoint, "GET", null, recovery.Token);
            }
            acknowledgedWrite = capability.Writes;
            if (capability.Writes)
            {
                using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                result = await ConnectorOperations.VerifyWriteAsync(actionProvider, account.ProviderId, capability.Id,
                    operationId, plan, result, verification.Token, async token =>
                    {
                        RequireReady(await store.GetTaskAsync(caller.UserId, task.Id, token));
                        await connections.VerifiedAccountAsync(caller.UserId, definition.Family, task.AccountId!, token);
                    });
                state = result is null ? "outcome_unknown" : "completed";
                if (result is null) errorCode = "verification_unknown";
                if (result is null) message = "The action was sent, but completion could not be independently verified. Do not repeat it yet.";
            }
            else
            {
                if (capability.Id == "mail_search")
                {
                    result = await ConnectorMailQueries.SearchAsync(actionProvider, account.ProviderId, result.Value,
                        request.Arguments, async token => RequireReady(await store.GetTaskAsync(caller.UserId, task.Id, token)), ct);
                }
                else if (capability.Id == "mail_count")
                    result = await ConnectorMailQueries.CountAsync(actionProvider, account.ProviderId, result.Value,
                        request.Arguments, async token => RequireReady(await store.GetTaskAsync(caller.UserId, task.Id, token)), ct);
                result = ConnectorOperations.SafeResult(result.Value, capability.Id, request.Arguments);
                state = "completed";
                // An empty exact query proves only that query, not the user's
                // paraphrased topic. Give guidance at the point of evidence;
                // never silently rewrite the query or add provider requests.
                if (capability.Id == "mail_search"
                    && result.Value.GetProperty("data").GetProperty("pageItemCount").GetInt32() == 0)
                    message = "No messages matched this query. If its wording was inferred from the user's topic, broaden the descriptive search and continue now; the original read request already authorizes that read, so do not ask again. Do not conclude the topic is absent from this query alone. Preserve any exact query the user explicitly supplied.";
            }
        }
        catch (ConnectorProviderException exception)
        {
            state = !capability.Writes && ct.IsCancellationRequested ? "cancelled"
                : exception.OutcomeUnknown || acknowledgedWrite ? "outcome_unknown" : "failed";
            errorCode = state == "cancelled" ? "cancelled" : ConnectorTelemetry.Error(exception.Code);
            message = state == "cancelled" ? "Task cancelled." : exception.Message;
            // Only deterministic resource identities can reconcile a lost POST.
            // No resending, probabilistic matching or implicit fallback account.
            if (capability.Writes && state == "outcome_unknown" && plan.ReconcileEndpoint is not null)
            {
                try
                {
                    using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    result = await ConnectorOperations.VerifyWriteAsync(actionProvider, account.ProviderId, capability.Id,
                        operationId, plan, null, verification.Token);
                    if (result is not null) { state = "completed"; message = null; errorCode = null; }
                }
                catch (Exception error)
                {
                    // A malformed/late verification response is not write
                    // failure proof. Settle the existing claim as unknown below.
                    result = null;
                    errorCode = "verification_unknown";
                    logger.LogWarning("Connector verification remained unknown: operation={Operation} errorType={ErrorType}",
                        operation.Id, error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            state = capability.Writes ? "outcome_unknown" : "cancelled";
            errorCode = capability.Writes ? "outcome_unknown" : "cancelled";
            message = capability.Writes ? "A dispatched action cannot be unsent. Its outcome is not yet verified." : "Task cancelled.";
        }
        catch (Exception exception)
        {
            state = capability.Writes && dispatchStarted ? "outcome_unknown" : "failed";
            errorCode = "local_failure";
            message = capability.Writes && dispatchStarted ? "The action may have been sent, but its outcome could not be verified. Do not repeat it yet."
                : "The task could not continue. No success has been confirmed.";
            logger.LogWarning("Connector step did not settle normally: operation={Operation} errorType={ErrorType}", operation.Id, exception.GetType().Name);
        }
        // A failed enrichment/verification may leave the first raw provider
        // receipt in this variable. It is not a normalized successful result.
        if (state != "completed") result = null;
        var elapsed = timer.ElapsedMilliseconds;
        using (var save = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var reference = capability.Writes && result is not null && state == "completed"
                ? result.Value.GetRawText() : null; // Only independently verified IDs/URLs, never content/arguments.
            await store.SaveOperationAsync(caller.UserId, operation with { State = state, ResourceId = reference,
                ProviderRequestCount = actionProvider.Requests, ElapsedMilliseconds = elapsed,
                ErrorCode = ConnectorTelemetry.Error(errorCode) }, save.Token);
            if (state is "failed" or "outcome_unknown" or "cancelled")
                await store.EndTaskAsync(caller.UserId, task.Id, state == "cancelled" ? "cancelled" : "failed", save.Token);
        }
        ConnectorTelemetry.Write(logger, caller, task, definition, "settled", state, operation.Id,
            capability.Id, actionProvider.Requests, elapsed, errorCode, correlationMismatch);
        // Suppress result delivery after cancellation/deletion, without falsifying
        // a side effect already independently verified in the operation journal.
        var current = await store.GetTaskAsync(caller.UserId, task.Id, ct);
        if ((current.State != "ready" && !(current.State == "failed" && state is "failed" or "outcome_unknown"))
            || current.ExpiresAt <= clock.UtcNow)
            return new(task.Id, operation.Id, "cancelled", null, false,
                state == "completed" && capability.Writes ? "Further work stopped. The already-dispatched action completed." : "Task no longer active.");
        return new(task.Id, operation.Id, state, result, state == "completed", message);
    }

    public async Task<ConnectorTaskReply> StatusAsync(CallerIdentity caller, string id, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(caller.UserId, id, ct);
        if (task.ExpiresAt <= clock.UtcNow && task.State is "ready" or "account_required") task = task with { State = "expired" };
        var operations = await store.OperationsAsync(caller.UserId, task.Id, ct);
        return Reply(task, registry.Get(task.ConnectorId), []) with
        {
            Operations = operations.Select(value => new ConnectorExecutionReply(task.Id, value.Id, value.State,
                value.ResourceId is null ? null : JsonSerializer.SerializeToElement(new { verifiedResource = value.ResourceId }),
                value.State == "completed", value.State == "outcome_unknown" ? "Outcome remains unverified; do not repeat this write." : null)).ToArray()
        };
    }
    public Task CancelAsync(CallerIdentity caller, string id, CancellationToken ct) => store.CancelTaskAsync(caller.UserId, id, ct);
    public Task FinishAsync(CallerIdentity caller, string id, CancellationToken ct) => store.FinishTaskAsync(caller.UserId, id, ct);
    public async Task<ConnectorTaskReply> ChangeAccountAsync(CallerIdentity caller, string id, string accountId, CancellationToken ct)
    {
        var previous = await store.GetTaskAsync(caller.UserId, ConnectorIds.RequireId(id), ct);
        if (previous.State is not ("ready" or "account_required") || previous.ExpiresAt <= clock.UtcNow)
            throw new InvalidOperationException("The original task is no longer active.");
        var definition = registry.Get(previous.ConnectorId);
        var account = await connections.VerifiedAccountAsync(caller.UserId, definition.Family, ConnectorIds.RequireId(accountId), ct);
        var replacement = previous with { Id = ConnectorIds.New(), AccountId = account.Id, State = "ready" };
        if (!await store.RebindTaskAsync(caller.UserId, previous.Id, replacement,
            definition.Capabilities.Where(value => value.Writes).Select(value => value.Id).ToArray(), clock.UtcNow, ct))
            throw new InvalidOperationException("This task cannot change account after a write was claimed. Check its outcome first.");
        ConnectorTelemetry.Write(logger, caller, replacement, definition, "account_rebound", replacement.State);
        return Reply(replacement, definition, [ConnectorIds.Public(account)], "Account changed. Discard the previous account's results and restart the requested checks; no write was replayed.");
    }
    private void RequireReady(StoredConnectorTask task)
    {
        if (task.State != "ready" || task.AccountId is null || task.ExpiresAt <= clock.UtcNow)
            throw new InvalidOperationException("This connector task is not active. Resolve a fresh request first.");
    }
    private static ConnectorTaskReply Reply(StoredConnectorTask task, ConnectorDefinition definition,
        IReadOnlyList<ConnectorAccount> choices, string? message = null) => new(task.Id, task.State,
        definition.Id, task.AccountId, choices, task.State == "ready"
            ? definition.Capabilities.Where(value => !value.Writes || task.AllowsWrites).ToArray() : [], task.ExpiresAt, message);
    internal static bool AllowsWrites(string request, string? capabilityId = null)
    {
        // Request must come from the current real user turn, never retrieved
        // email/docs/tool text. A verb mentioned in a question, a quoted
        // instruction, or a negated action is not permission to write.
        // Each current service exposes just one reviewed write capability;
        // its task grant is service-specific, not a blanket "any write" grant.
        var verbs = capabilityId switch
        {
            "mail_send" => "send|email|reply|follow[- ]?up",
            // Calendar reminder directives authorize offering the existing
            // confirmed event operation, not arbitrary "set" actions or recall
            // questions such as "remind me what/when/how ...".
            "calendar_create" => @"create|schedule|add|book|set\s+(?:(?:a|an|the|my)\s+)?(?:(?:google\s+)?calendar\s+)?reminders?|remind\s+me\b(?!\s+(?:about|of)\b)(?:(?!\b(?:what|when|whether|how|why|who|which|where|if)\b)[^.!?,;]){0,120}?\s+to(?=\s+(?!(?:what|when|whether|how|why|who|which|where|if)\b)[\p{L}\p{N}])",
            "docs_create" or "forms_create" => "create|make|build",
            "github_issue_create" => "create|open|file|submit",
            "slack_post" => "send|post|message",
            null => "send|email|reply|follow[- ]?up|create|schedule|add|book|make|build|open|file|submit|post|message",
            _ => null
        };
        if (verbs is null || string.IsNullOrWhiteSpace(request)) return false;
        var unquoted = Regex.Replace(request, "\"[^\"]*\"|(?<![a-z])'[^']*'|“[^”]*”|‘[^’]*’", " ", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(unquoted, @"\b(?:do\s+not|don['’]t|never|without|not)\s+(?:\w+\s+){0,2}(?:" + verbs + @")\b", RegexOptions.IgnoreCase))
            return false;
        // A directed action at a clause boundary, with normal polite/conditional
        // prefixes. This does not match "what does send mean", "how to send",
        // "draft an email" or "read a message that says send".
        var prefix = @"(?:^|[.!?,;]\s*|\band\s+|\bthen\s+)\s*"
            + @"(?:(?:hey\s+)?jarvis[,!\s]+)?"
            + @"(?:if\b[^.!?,;]{0,180}?\s+)?"
            + @"(?:(?:can|could|would|will)\s+you\s+|i(?:['’]d|\s+would)\s+like\s+you\s+to\s+|i\s+want\s+you\s+to\s+)?"
            + @"(?:(?:please|just|then|also)\s+){0,3}(?:" + verbs + @")\b";
        return Regex.IsMatch(unquoted, prefix, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
    }
}
