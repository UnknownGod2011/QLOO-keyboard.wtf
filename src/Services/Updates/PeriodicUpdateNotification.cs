namespace KeyboardWtf.Services.Updates;

// Notification-only orchestration, not consent, download, installation or
// manifest verification. The public host must supply the signed
// checker, exclude private/staging builds, and recheck shutdown/channel/token on
// actual UI dispatch before returning true. This core owns no timer or resource.
internal sealed class PeriodicUpdateNotification : IDisposable
{
    private readonly UpdateWorkAdmissionGate _gate;
    private readonly Func<bool> _eligibility;
    private readonly Func<string> _currentChannel;
    private readonly Func<string, CancellationToken, Task<VerifiedUpdate>> _check;
    private readonly Func<VerifiedUpdate, CancellationToken, Task<bool>> _notify;
    private VerifiedUpdate _lastStable;
    private VerifiedUpdate _lastBeta;
    private int _checking;
    private int _disposed;

    internal PeriodicUpdateNotification(UpdateWorkAdmissionGate gate,
        Func<bool> eligibility, Func<string> currentChannel,
        Func<string, CancellationToken, Task<VerifiedUpdate>> check,
        Func<VerifiedUpdate, CancellationToken, Task<bool>> notify)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _currentChannel = currentChannel ?? throw new ArgumentNullException(nameof(currentChannel));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    internal async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        IDisposable owner = null;
        try
        {
            if (Stopped(cancellationToken) || !_eligibility()) return;
            if (!_gate.TryAcquire(out owner)) return; // Quiet deferral, no replay.
            if (Stopped(cancellationToken)) return;
            var channel = SecureUpdateService.NormalizeChannel(_currentChannel());
            var originalCheck = _check(channel, cancellationToken);
            if (originalCheck is null) return;
            // Never release for a timed/cancelled waiter while real work runs.
            var update = await originalCheck.ConfigureAwait(false);
            if (update is null || !string.Equals(update.Channel, channel, StringComparison.Ordinal)
                || !CanPublish(channel, cancellationToken)) return;

            var last = channel == "beta" ? _lastBeta : _lastStable;
            if (update == last) return; // Full verified-record equality; two bounded slots.
            if (!CanPublish(channel, cancellationToken)) return;
            var originalNotice = _notify(update, cancellationToken);
            if (originalNotice is null) return;
            if (await originalNotice.ConfigureAwait(false))
            {
                // A positive actual-dispatch receipt remains authoritative even
                // if its caller cancels afterward; don't duplicate that notice.
                if (channel == "beta") _lastBeta = update;
                else _lastStable = update;
            }
        }
        catch
        {
            // Background checks are best effort. The host must provide content-
            // free diagnostics; manual Settings checking retains its own UX.
            // No consent is touched and no implicit retry/download is started.
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
            owner?.Dispose(); // LAST, after original check/notice and cleanup.
        }
    }

    private bool CanPublish(string channel, CancellationToken cancellationToken) =>
        !Stopped(cancellationToken) && _eligibility()
        && string.Equals(channel, SecureUpdateService.NormalizeChannel(_currentChannel()), StringComparison.Ordinal)
        && !Stopped(cancellationToken);

    private bool Stopped(CancellationToken cancellationToken) =>
        Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested;

    // Disposal suppresses later dispatch; it cannot cancel/retire an original
    // checker or queued notifier. Host-owned resources need their own drainage.
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
