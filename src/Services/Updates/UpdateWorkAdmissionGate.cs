namespace KeyboardWtf.Services.Updates;

// Passive lifetime accounting, not a feature/task state machine. This core is
// not sufficient until all production work owners and ingress are connected.
internal sealed class UpdateWorkAdmissionGate
{
    private readonly object _sync = new();
    private long _owners;
    private FreezeLease _freeze;
    private bool _committed;

    internal bool TryAcquire(out IDisposable lease)
    {
        lock (_sync)
        {
            lease = null;
            if (_freeze is not null || _committed) return false;
            _owners = checked(_owners + 1);
            lease = new WorkLease(this);
            return true;
        }
    }

    internal bool TryFreeze(out FreezeLease freeze)
    {
        lock (_sync)
        {
            freeze = null;
            if (_owners != 0 || _freeze is not null || _committed) return false;
            freeze = _freeze = new FreezeLease(this);
            return true;
        }
    }

    internal async Task TrackAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryAcquire(out var lease))
            throw new InvalidOperationException("An update is preparing installation. Please wait.");
        using (lease)
        {
            // Acquire BEFORE invocation. Release only after the actual worker
            // unwinds, never when its cancellation/status is merely published.
            var task = work() ?? throw new InvalidOperationException("The work did not return a completion task.");
            await task.ConfigureAwait(false);
        }
    }

    private void ReleaseWork()
    {
        lock (_sync)
        {
            if (_owners <= 0) throw new InvalidOperationException("Invalid update work ownership.");
            _owners--;
        }
    }

    private void Commit(FreezeLease freeze)
    {
        lock (_sync)
        {
            // A stale/disposed token can never acquire another freeze's
            // authority. Repeated commit of the same committed token is safe.
            if (!ReferenceEquals(_freeze, freeze)) return;
            _committed = true;
        }
    }

    private void Thaw(FreezeLease freeze)
    {
        lock (_sync)
        {
            if (!_committed && ReferenceEquals(_freeze, freeze)) _freeze = null;
        }
    }

    private sealed class WorkLease(UpdateWorkAdmissionGate gate) : IDisposable
    {
        private UpdateWorkAdmissionGate _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.ReleaseWork();
    }

    internal sealed class FreezeLease(UpdateWorkAdmissionGate gate) : IDisposable
    {
        internal void Commit() => gate.Commit(this);
        public void Dispose() => gate.Thaw(this);
    }
}
