namespace KeyboardWtf.Services.Updates;

// Data-only binding, NOT proof that the artifact is safe to execute. The future
// adapter must verify this exact approved tuple/path under its held byte lease.
internal sealed record PreparedUpdate(VerifiedUpdate Update, string InstallerPath);

internal enum PreparedUpdateProbeOutcome { Unknown = 0, Ready = 1, Busy = 2 }
internal enum PreparedUpdateLaunchOutcome { Unknown = 0, DefinitelyNotStarted = 1, Started = 2 }
internal enum PreparedUpdateCommitOutcome { Deferred, Cancelled, NotStarted, Started, LaunchUnknown }

internal sealed record PreparedUpdateCommitResult(
    PreparedUpdateCommitOutcome Outcome,
    bool ExitRequestFailed = false,
    bool AcknowledgementFailed = false,
    bool Thawed = false);

// Initially UNWIRED. Does not download, validate, launch or exit anything itself.
// Complete owner coverage, honest final probes, exact launch evidence and safe
// installer/relaunch integration remain prerequisites before enabling updates.
internal sealed class PreparedUpdateCommitCoordinator(UpdateWorkAdmissionGate gate)
{
    private readonly UpdateWorkAdmissionGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    internal async Task<PreparedUpdateCommitResult> CommitAsync(
        PreparedUpdate prepared,
        Func<PreparedUpdate, CancellationToken, Task<PreparedUpdateProbeOutcome>> probe,
        Func<PreparedUpdate, PreparedUpdateLaunchOutcome> launch,
        Action requestExit,
        Func<PreparedUpdate, Task> acknowledge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(prepared.Update);
        if (string.IsNullOrWhiteSpace(prepared.InstallerPath))
            throw new ArgumentException("The prepared installer path is missing.", nameof(prepared));
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(requestExit);
        ArgumentNullException.ThrowIfNull(acknowledge);

        if (cancellationToken.IsCancellationRequested)
            return new(PreparedUpdateCommitOutcome.Cancelled);
        if (!_gate.TryFreeze(out var freeze))
            return new(PreparedUpdateCommitOutcome.Deferred);

        using (freeze)
        {
            PreparedUpdateProbeOutcome ready;
            try
            {
                // Callbacks are OUTSIDE the gate's lock. Cancellation of a
                // waiter must not thaw underneath the original probe's work.
                var original = probe(prepared, cancellationToken)
                    ?? throw new InvalidOperationException("The update probe returned no completion task.");
                ready = await original.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new(PreparedUpdateCommitOutcome.Cancelled, Thawed: true);
            }
            catch
            {
                return new(PreparedUpdateCommitOutcome.Deferred, Thawed: true);
            }

            if (cancellationToken.IsCancellationRequested)
                return new(PreparedUpdateCommitOutcome.Cancelled, Thawed: true);
            if (ready != PreparedUpdateProbeOutcome.Ready)
                return new(PreparedUpdateCommitOutcome.Deferred, Thawed: true);

            // Entry into launch is the cancellation cut-off. No cancellation,
            // exception or lost ACK can undo a possible irreversible launch.
            PreparedUpdateLaunchOutcome launched;
            try { launched = launch(prepared); }
            catch { launched = PreparedUpdateLaunchOutcome.Unknown; }

            // Only positive adapter evidence that NO launch effect occurred
            // permits reopening admission. A default/undefined value is unknown.
            if (launched == PreparedUpdateLaunchOutcome.DefinitelyNotStarted)
                return new(PreparedUpdateCommitOutcome.NotStarted, Thawed: true);

            freeze.Commit();
            if (launched != PreparedUpdateLaunchOutcome.Started)
                return new(PreparedUpdateCommitOutcome.LaunchUnknown);

            // Started is NOT installed. Commit before requesting graceful exit;
            // client acknowledgement cannot delay or revoke that request.
            try { requestExit(); }
            catch { return new(PreparedUpdateCommitOutcome.Started, ExitRequestFailed: true); }
            try
            {
                var original = acknowledge(prepared)
                    ?? throw new InvalidOperationException("The update acknowledgement returned no completion task.");
                await original.ConfigureAwait(false);
            }
            catch { return new(PreparedUpdateCommitOutcome.Started, AcknowledgementFailed: true); }
            return new(PreparedUpdateCommitOutcome.Started);
        }
    }
}
