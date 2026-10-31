namespace KeyboardWtf.Services.Updates;

// Notification-only host lifetime. Owns no timer/control or consent. The public
// app host posts to its UI and supplies the signed checker.
internal sealed class PublicUpdateNotificationMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<bool> _eligibility;
    private readonly Func<string> _channel;
    private readonly Action<Action> _post;
    private readonly Func<VerifiedUpdate, bool> _notify;
    private readonly PeriodicUpdateNotification _core;
    private readonly CancellationTokenSource _shutdown;
    private CheckRun _active;
    private UpdateNotificationDispatchReceipt _pending;
    private TaskCompletionSource<bool> _stoppedCompletion;
    private Task _drainWorker;
    private bool _stopped;

    internal PublicUpdateNotificationMonitor(UpdateWorkAdmissionGate gate,
        Func<bool> eligibility, Func<string> channel,
        Func<string, CancellationToken, Task<VerifiedUpdate>> check,
        Action<Action> post, Func<VerifiedUpdate, bool> notify)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(check);
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _core = new(gate, eligibility, channel, check, PostNoticeAsync);
        _shutdown = new(); // Only after ALL dependencies are validated.
    }

    internal Task CheckOnceAsync()
    {
        CheckRun run;
        lock (_sync)
        {
            if (_stopped) return Task.CompletedTask;
            if (_active is not null) return _active.Completion.Task;
            _active = run = new(); // Publish BEFORE any reentrant external effect.
        }
        run.Worker = ExecuteAsync(run);
        return run.Completion.Task;
    }

    private async Task ExecuteAsync(CheckRun run)
    {
        try
        {
            run.Original = _core.CheckOnceAsync(_shutdown.Token);
            await run.Original.ConfigureAwait(false); // No waiter timeout/early retirement.
        }
        catch { /* Background metadata checks never fail a feature or UI event. */ }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_active, run)) _active = null; }
            run.Completion.TrySetResult(true); // LAST, after original cleanup/bookkeeping.
        }
    }

    private Task<bool> PostNoticeAsync(VerifiedUpdate update, CancellationToken token)
    {
        var receipt = new UpdateNotificationDispatchReceipt(() =>
            CanNotify(update, token) && _notify(update));
        lock (_sync)
        {
            if (_stopped) return Task.FromResult(false);
            _pending = receipt; // Register BEFORE posting, including reentrant Stop.
        }
        try { _post(receipt.Run); }
        catch { receipt.TrySuppress(); } // Never retire a callback which already started.
        return ObserveNoticeAsync(receipt);
    }

    private bool CanNotify(VerifiedUpdate update, CancellationToken token) =>
        !IsStopped() && !token.IsCancellationRequested && _eligibility()
        && string.Equals(update.Channel, SecureUpdateService.NormalizeChannel(_channel()), StringComparison.Ordinal)
        && !IsStopped() && !token.IsCancellationRequested;

    private bool IsStopped() { lock (_sync) return _stopped; }

    private async Task<bool> ObserveNoticeAsync(UpdateNotificationDispatchReceipt receipt)
    {
        try { return await receipt.Completion.ConfigureAwait(false); }
        finally { lock (_sync) { if (ReferenceEquals(_pending, receipt)) _pending = null; } }
    }

    internal Task StopAsync()
    {
        CheckRun active;
        UpdateNotificationDispatchReceipt pending;
        TaskCompletionSource<bool> completion;
        lock (_sync)
        {
            if (_stoppedCompletion is not null) return _stoppedCompletion.Task;
            _stopped = true;
            active = _active;
            pending = _pending;
            // Publish ONE stop lifetime before cancellation callbacks can reenter.
            _stoppedCompletion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _core.Dispose();
        pending?.TrySuppress();
        try { _shutdown.Cancel(); }
        catch { /* A cancellation callback failure cannot bypass original drainage. */ }
        _drainWorker = DrainAsync(active, completion);
        return completion.Task;
    }

    private async Task DrainAsync(CheckRun active, TaskCompletionSource<bool> completion)
    {
        try
        {
            if (active is not null) await active.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose(); // Only AFTER the actual original task/notice unwinds.
            completion.TrySetResult(true);
        }
    }

    // Never block the UI on a callback queued to that same UI. The retained
    // Stop task remains incomplete until original work and token cleanup finish.
    public void Dispose() => _ = StopAsync();

    private sealed class CheckRun
    {
        internal TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Original { get; set; }
        internal Task Worker { get; set; }
    }
}
