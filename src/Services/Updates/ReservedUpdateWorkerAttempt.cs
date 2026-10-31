using System.Diagnostics;
using System.IO.Pipes;

namespace KeyboardWtf.Services.Updates;

// UNWIRED, inert boundary only: known ORIGINAL exits + cooperative exclusion.
// Binding and physical installation identity must be independently trusted,
// never adopted from a pipe/command line. This does NOT inventory every process,
// authenticate executable bytes, establish path-to-launch authority, execute an
// installer or authorize relaunch. A real installer adapter must separately own
// its original process through terminal completion/unknown reconciliation.
internal sealed class ReservedUpdateWorkerAttempt
{
    private readonly UpdateWorkerHandshakeBinding _binding;
    private readonly UpdateInstallationIngressLease.InstallationFileIdentity _installation;
    private int _started;

    internal ReservedUpdateWorkerAttempt(UpdateWorkerHandshakeBinding binding,
        UpdateInstallationIngressLease.InstallationFileIdentity installation)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (installation.Low == 0 && installation.High == 0)
            throw new InvalidDataException("The expected installation identity is unknown.");
        _installation = installation;
    }

    internal async Task<bool> RunAsync(NamedPipeClientStream pipe,
        IReadOnlyList<DeclaredUpdateParent> additionalOriginals,
        TimeSpan handshakeTimeout, TimeSpan exitTimeout,
        Func<Task> afterOriginalExitAndExclusive, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The reserved update attempt has already been consumed.");
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(additionalOriginals);
        ArgumentNullException.ThrowIfNull(afterOriginalExitAndExclusive);
        if (handshakeTimeout <= TimeSpan.Zero || handshakeTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        if (exitTimeout <= TimeSpan.Zero || exitTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(exitTimeout));
        var declarations = SnapshotOriginals(additionalOriginals);
        cancellationToken.ThrowIfCancellationRequested();

        // Reserve BEFORE the first READY byte; never release/reacquire between
        // handshake, original exit waits, exclusive activity and boundary work.
        using var reservation = UpdateInstallationIngressLease.TryReserve(_binding.TargetExecutablePath);
        if (reservation is null) return false;
        if (reservation.InstallationIdentity != _installation)
            throw new InvalidDataException("The reserved installation identity does not match this attempt.");
        var originals = new List<UpdateProcessIdentityLease>();
        try
        {
            originals.Add(UpdateProcessIdentityLease.Acquire(_binding.Parent, _binding.TargetExecutablePath));
            foreach (var declaration in declarations)
                originals.Add(UpdateProcessIdentityLease.Acquire(declaration, _binding.TargetExecutablePath));

            // The parent legitimately holds shared activity during exchange.
            // Taking exclusive activity before READY would deadlock that parent.
            await new UpdateWorkerHandshake(_binding).ExchangeWorkerAsync(pipe, originals[0],
                handshakeTimeout, cancellationToken).ConfigureAwait(false);

            var clock = Stopwatch.StartNew();
            foreach (var original in originals)
            {
                var remaining = exitTimeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero
                    || !await original.WaitForExitAsync(remaining, cancellationToken).ConfigureAwait(false))
                    return false; // Timeout is never positive OS-exit evidence.
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!reservation.TryAcquireExclusive()) return false; // Unknown/unlisted activity defers.
            cancellationToken.ThrowIfCancellationRequested();

            var originalBoundary = afterOriginalExitAndExclusive()
                ?? throw new InvalidOperationException("The inert boundary did not return an owned task.");
            // No cancellable external waiter: if caller cancellation arrives
            // during boundary work, the ORIGINAL task still owns exclusion until
            // it actually succeeds/faults/cancels. Not permission for a real launch.
            await originalBoundary.ConfigureAwait(false);
            return true; // This inert boundary completed, NOT an installation.
        }
        finally
        {
            foreach (var original in originals) original.Dispose();
        }
    }

    private DeclaredUpdateParent[] SnapshotOriginals(IReadOnlyList<DeclaredUpdateParent> additional)
    {
        var count = additional.Count;
        if (count is < 0 or > 64)
            throw new InvalidDataException("The additional original-process set is unbounded.");
        var snapshot = new DeclaredUpdateParent[count];
        for (var index = 0; index < count; index++) snapshot[index] = additional[index];
        var pids = new HashSet<int>();
        RequireDeclaration(_binding.Parent, pids);
        foreach (var declaration in snapshot) RequireDeclaration(declaration, pids);
        return snapshot;
    }

    private void RequireDeclaration(DeclaredUpdateParent declaration, HashSet<int> pids)
    {
        if (declaration is null || declaration.ProcessId <= 0 || declaration.ProcessId == Environment.ProcessId
            || declaration.CreationTimeUtcTicks <= 0 || declaration.CreationTimeUtcTicks > DateTime.MaxValue.Ticks
            || !pids.Add(declaration.ProcessId))
            throw new InvalidDataException("The original-process set is invalid or ambiguous.");
        UpdatePathIdentity.RequireCanonicalLocalPath(declaration.ExecutablePath);
        if (!string.Equals(declaration.ExecutablePath, _binding.TargetExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The original process is not the trusted installation image.");
    }
}
