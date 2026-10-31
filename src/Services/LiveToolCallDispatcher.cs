namespace KeyboardWtf.Services;

using System.Text.Json;
using KeyboardWtf.Services.Updates;

// The socket reader must remain available for input, farewell and cancellation
// while a tool is awaiting I/O. Actions still execute serially, and all work is
// owned by this one physical connection rather than a later replacement socket.
internal sealed class LiveToolCallDispatcher : IDisposable
{
    // Execution-local provenance only; never a session authority or a tool
    // argument. A later call in this batch was proposed before these captures
    // were returned to the provider, even though local execution is ordered.
    private static readonly AsyncLocal<bool> UnpublishedSelectedContext = new();
    internal static bool HasUnpublishedSelectedContext => UnpublishedSelectedContext.Value;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> _execute;
    private readonly Func<Func<object>, Action, CancellationToken, Task> _respond;
    private readonly UpdateWorkAdmissionGate _updateWorkAdmission;
    private Task _completion = Task.CompletedTask;
    private int _stopped;
    private int _disposed;
    private int _pendingObservations;
    private TurnBatch _currentTurnBatch;
    private InputObservation _awaitingInput;

    internal LiveToolCallDispatcher(
        CancellationToken token,
        Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> execute,
        Func<Func<object>, CancellationToken, Task> respond,
        UpdateWorkAdmissionGate updateWorkAdmission = null)
        : this(token, execute, async (build, onSent, sendToken) =>
        {
            await respond(build, sendToken);
            onSent();
        }, updateWorkAdmission)
    {
    }

    internal LiveToolCallDispatcher(
        CancellationToken token,
        Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> execute,
        Func<Func<object>, Action, CancellationToken, Task> respond,
        UpdateWorkAdmissionGate updateWorkAdmission = null)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _execute = execute;
        _respond = respond;
        _updateWorkAdmission = updateWorkAdmission;
    }

    internal Task Completion { get { lock (_sync) return _completion; } }
    internal int PendingCount { get { lock (_sync) return _pending.Count + _pendingObservations; } }
    internal bool HasUnpublishedCalls { get { lock (_sync) return _pending.Count > 0; } }

    internal void Observe(Action<string, CancellationToken> observer, string transcript, bool inputComplete = true)
    {
        if (observer == null)
            return;
        lock (_sync)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;
            // A destructive proposal can precede its input transcription.
            // Bind that input at the proposal's reserved queue position, not
            // behind the action that needs it. Other observations keep their
            // existing serial order.
            if (!string.IsNullOrWhiteSpace(transcript)
                && _awaitingInput is { Invalidated: false } pendingInput)
            {
                pendingInput.Observer = observer;
                pendingInput.Transcript = transcript;
                if (inputComplete) pendingInput.Ready.TrySetResult(true);
                return;
            }
            var updateWork = AcquireUpdateWork();
            Task scheduled = null;
            try
            {
                var previous = _completion;
                _pendingObservations++;
                _completion = scheduled = Task.Run(async () =>
                {
                    try
                    {
                        await previous;
                        _lifetime.Token.ThrowIfCancellationRequested();
                        observer(transcript, _lifetime.Token);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                    catch (Exception exception) { AppLog.Warning(exception, "Jarvis transcript observer failed"); }
                    finally { lock (_sync) _pendingObservations--; }
                });
            }
            finally { RetainUntilCompletion(scheduled, updateWork); }
        }
    }

    internal void Enqueue(JsonElement toolCall, ToolAttemptGuard guard,
        Action<IReadOnlyList<Dictionary<string, object>>> receiptObserver = null,
        bool awaitUserInput = false)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;
            var updateWork = AcquireUpdateWork();
            Task scheduled = null;
            try
            {
                var work = RegisterCallsLocked(toolCall);
                if (work.Count == 0)
                    return;
                var previous = _completion;
                var input = awaitUserInput ? _awaitingInput ??= new InputObservation() : null;
                _completion = scheduled = Task.Run(() => RunAsync(previous, work, guard, receiptObserver, input));
            }
            finally { RetainUntilCompletion(scheduled, updateWork); }
        }
    }

    internal void CompleteInputObservation()
    {
        lock (_sync)
            if (_awaitingInput?.Observer != null)
                _awaitingInput.Ready.TrySetResult(true);
    }

    internal void InvalidateInputObservation()
    {
        lock (_sync)
        {
            if (_awaitingInput == null) return;
            _awaitingInput.Invalidated = true;
            _awaitingInput.Ready.TrySetResult(false);
            _awaitingInput = null;
        }
    }

    private async Task<bool> ApplyInputObservationAsync(InputObservation input, CancellationToken token)
    {
        try
        {
            if (!await input.Ready.Task.WaitAsync(TimeSpan.FromSeconds(2), token)) return false;
            Action<string, CancellationToken> observer;
            string transcript;
            lock (_sync)
            {
                if (input.Invalidated) return false;
                if (input.Applied) return true;
                input.Applied = true;
                observer = input.Observer;
                transcript = input.Transcript;
                if (ReferenceEquals(input, _awaitingInput)) _awaitingInput = null;
            }
            token.ThrowIfCancellationRequested();
            observer(transcript, token);
            return true;
        }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                input.Invalidated = true;
                input.Ready.TrySetResult(false);
                if (ReferenceEquals(input, _awaitingInput)) _awaitingInput = null;
            }
            return false;
        }
    }

    // Live may split one model turn's function calls across socket frames. A
    // separate response for each frame gives the model partial knowledge of
    // one compound request and can prompt an unnecessary retry/second answer.
    // Registration is immediate so provider cancellation still owns each ID;
    // only response publication waits for this provider turn's completion.
    internal void StageCurrentTurn(JsonElement toolCall, ToolAttemptGuard guard,
        Action<IReadOnlyList<Dictionary<string, object>>> receiptObserver = null)
    {
        TurnBatch batch;
        var startedBatch = false;
        lock (_sync)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;
            var updateWork = AcquireUpdateWork();
            Task scheduled = null;
            try
            {
                var work = RegisterCallsLocked(toolCall);
                if (work.Count == 0)
                    return;
                if (_currentTurnBatch == null)
                {
                    batch = new TurnBatch(guard, receiptObserver);
                    _currentTurnBatch = batch;
                    var previous = _completion;
                    // Reserve this position in the existing dispatcher chain now.
                    // A newer user transcript/turn cannot overtake an unflushed
                    // tool batch while the provider is still finishing this turn.
                    _completion = scheduled = Task.Run(async () =>
                    {
                        await batch.Ready.Task;
                        await RunAsync(previous, batch.Calls, batch.Guard, batch.ReceiptObserver);
                    });
                    startedBatch = true;
                }
                else batch = _currentTurnBatch;
                batch.Calls.AddRange(work);
            }
            finally { RetainUntilCompletion(scheduled, updateWork); }
        }

        // A missing turnComplete must not strand a tool forever. This path is
        // inactive when the provider supplies its normal completion frame.
        if (startedBatch)
            _ = FlushMissingCompletionAsync(batch);
    }

    internal void FlushCurrentTurn()
    {
        lock (_sync)
            FlushCurrentTurnLocked();
    }

    private IDisposable AcquireUpdateWork()
    {
        if (_updateWorkAdmission is null) return null;
        if (!_updateWorkAdmission.TryAcquire(out var lease))
            throw new InvalidOperationException("An update is preparing installation. Please wait.");
        return lease;
    }

    private static void RetainUntilCompletion(Task scheduled, IDisposable updateWork)
    {
        if (updateWork is null) return;
        if (scheduled is null)
        {
            // No original worker was started (empty/duplicate call, staged
            // addition to an already-owned batch, or pre-scheduling failure).
            updateWork.Dispose();
            return;
        }
        // Preserve the original worker/completion identity and scheduling.
        // Cancellation, Stop, Dispose and settled pending state do not prove
        // that execute/respond/observer dependencies have actually unwound.
        _ = scheduled.ContinueWith(static (_, state) => ((IDisposable)state).Dispose(),
            updateWork, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task FlushMissingCompletionAsync(TurnBatch batch)
    {
        try
        {
            // The slowest observed normal completion was 1.16 s after its
            // first tool frame. Leave headroom so that fallback cannot split
            // an otherwise complete compound turn merely because it was slow.
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            lock (_sync)
            {
                if (ReferenceEquals(batch, _currentTurnBatch))
                    FlushCurrentTurnLocked();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void FlushCurrentTurnLocked()
    {
        if (_currentTurnBatch == null)
            return;
        var batch = _currentTurnBatch;
        _currentTurnBatch = null;
        if (batch.Calls.Count > 1)
            AppLog.Info($"Jarvis combined same-turn tool calls: count={batch.Calls.Count}");
        batch.Ready.TrySetResult(true);
    }

    private List<Call> RegisterCallsLocked(JsonElement toolCall)
    {
        var work = new List<Call>();
        if (!toolCall.TryGetProperty("functionCalls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            return work;
        foreach (var call in calls.EnumerateArray())
        {
            var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            if (!_seen.Add(id))
                continue;
            var name = call.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
            var args = call.TryGetProperty("args", out var argsElement) ? argsElement.Clone() : default;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _pending.Add(id, cancellation);
            work.Add(new Call(id, name, args, cancellation));
        }
        return work;
    }

    internal void Cancel(JsonElement ids)
    {
        if (ids.ValueKind != JsonValueKind.Array)
            return;
        lock (_sync)
        {
            foreach (var id in ids.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String && _pending.TryGetValue(id.GetString() ?? "", out var pending))
                    pending.Cancel();
        }
    }

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _lifetime.Cancel();
            lock (_sync)
                FlushCurrentTurnLocked();
        }
    }

    private async Task RunAsync(Task previous, List<Call> calls, ToolAttemptGuard guard,
        Action<IReadOnlyList<Dictionary<string, object>>> receiptObserver = null,
        InputObservation input = null)
    {
        try
        {
            await previous;
            var results = new List<(Call Call, Dictionary<string, object> Response)>();
            foreach (var call in calls)
            {
                var token = call.Cancellation.Token;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (input != null && !await ApplyInputObservationAsync(input, token))
                    {
                        results.Add((call, new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["no_tab_closed"] = true,
                            ["message"] = "The current spoken request could not be verified. No action ran. Ask the user to repeat the request; do not claim success.",
                        }));
                        continue;
                    }
                    var duplicateSuppressed = guard.TryGetSuppressed(
                        call.Name, call.Args, out var response);
                    if (duplicateSuppressed)
                        AppLog.Info($"Jarvis repeated tool proposal suppressed: tool={call.Name}");
                    else
                    {
                        try
                        {
                            var previousContext = UnpublishedSelectedContext.Value;
                            try
                            {
                                UnpublishedSelectedContext.Value = results.Any(result =>
                                    result.Call.Name is "get_selected_text" or "get_clipboard_text"
                                    && !result.Call.Cancellation.IsCancellationRequested
                                    && result.Response.GetValueOrDefault("ok") is true
                                    && result.Response.GetValueOrDefault("text") is string { Length: > 0 });
                                response = await _execute(call.Name, call.Args, token);
                            }
                            finally { UnpublishedSelectedContext.Value = previousContext; }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            AppLog.Warning(exception, $"Jarvis tool execution failed: {call.Name}");
                            response = new Dictionary<string, object>
                            {
                                ["ok"] = false,
                                ["supported"] = false,
                                ["message"] = "The action failed without a verified result. Do not claim success or automatically repeat it.",
                            };
                        }
                        token.ThrowIfCancellationRequested();
                        guard.Record(call.Name, call.Args, response);
                    }
                    token.ThrowIfCancellationRequested();
                    results.Add((call, response));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    AppLog.Warning(exception, $"Jarvis tool dispatch failed: {call.Name}");
                }
            }
            // One provider request owns one combined response, as in 1.4.17.
            // Keep completed calls cancellable until the batch is published:
            // cancellation arriving during another action must suppress them too.
            _lifetime.Token.ThrowIfCancellationRequested();
            // Materialize only once the transport owns its send lock. A result
            // cancelled while waiting for audio/socket output must not survive
            // in an already-created message; unrelated results still publish.
            string[] publishedNames = [];
            await _respond(() =>
            {
                var responses = results
                    .Where(result => !result.Call.Cancellation.IsCancellationRequested)
                    // These declarations use the provider's default blocking
                    // behavior. Scheduling metadata belongs only to explicitly
                    // NON_BLOCKING calls; adding it here can defer a dependency
                    // and replay an already-completed effect. Keep the plain
                    // response shape used by 1.4.17 and the blocking contract.
                    .Select(result => new
                    {
                        id = result.Call.Id,
                        name = result.Call.Name,
                        response = new Dictionary<string, object>(result.Response),
                    })
                    .ToArray();
                AnnotateReferentialGroup(responses.Select(result => result.response));
                // The physical sender owns this snapshot. Cancelled calls
                // cannot reserve response publication for an ended action.
                receiptObserver?.Invoke(responses.Select(result => result.response).ToArray());
                publishedNames = responses.Select(result => result.name).ToArray();
                return responses.Length == 0 ? null : new { toolResponse = new { functionResponses = responses } };
            }, () =>
            {
                // The physical sender calls this immediately after the
                // receipt is sent, before the dispatcher finishes logging
                // and disposing call tokens. Published calls are no longer
                // pending: a fast post-receipt answer must see settled tools.
                lock (_sync)
                    foreach (var call in calls)
                        _pending.Remove(call.Id);
            }, _lifetime.Token);
            if (publishedNames.Length > 0)
                AppLog.Info($"Jarvis Live tool response batch published: tools={string.Join(',', publishedNames)} count={publishedNames.Length}");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // Every earlier batch observes its own failures. This is a final
            // diagnostic boundary, never permission to replay a failed action.
            AppLog.Warning(exception, "Jarvis tool queue ended");
        }
        finally
        {
            lock (_sync)
            {
                if (input != null && ReferenceEquals(input, _awaitingInput))
                {
                    input.Invalidated = true;
                    input.Ready.TrySetResult(false);
                    _awaitingInput = null;
                }
                foreach (var call in calls)
                {
                    _pending.Remove(call.Id);
                    call.Cancellation.Dispose();
                }
            }
        }
    }

    internal static void AnnotateReferentialGroup(
        IEnumerable<Dictionary<string, object>> responses)
    {
        var owners = (responses ?? [])
            .Where(response => response != null
                && !response.ContainsKey("duplicate_suppressed"))
            .ToArray();
        var targets = owners.SelectMany(response =>
            response.TryGetValue("referential_targets", out var many) && many is IEnumerable<object> list
                ? list
                : response.TryGetValue("referential_target", out var one) && one != null
                    ? new[] { one }
                    : Array.Empty<object>()).ToArray();
        if (targets.Length <= 1)
            return;

        foreach (var response in owners)
        {
            response["referential_group"] = new Dictionary<string, object>
            {
                ["cardinality"] = "multiple",
                ["target_count"] = targets.Length,
                // The individual/workflow results already contain the exact
                // targets. Do not replicate the whole set into every response.
                ["instruction"] = "A later plural reference to this result means the full returned target set: use every exact target_id, not just the last one. A singular reference is ambiguous unless the user identifies one target.",
            };
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        _lifetime.Dispose();
    }

    private sealed class InputObservation
    {
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<string, CancellationToken> Observer;
        internal string Transcript;
        internal bool Applied;
        internal bool Invalidated;
    }

    private sealed record Call(string Id, string Name, JsonElement Args, CancellationTokenSource Cancellation);
    private sealed class TurnBatch(ToolAttemptGuard guard,
        Action<IReadOnlyList<Dictionary<string, object>>> receiptObserver)
    {
        internal readonly List<Call> Calls = new();
        internal readonly ToolAttemptGuard Guard = guard;
        internal readonly Action<IReadOnlyList<Dictionary<string, object>>> ReceiptObserver = receiptObserver;
        internal readonly TaskCompletionSource<bool> Ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
