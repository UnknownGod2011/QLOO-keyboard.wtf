using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace KeyboardWtf.Services.Updates;

// UNWIRED protocol binding, not manifest verification or installer permission.
// Both endpoints must receive independently trusted attempt metadata; expected
// nonce/digest are NEVER adopted from an incoming pipe frame.
internal sealed class UpdateWorkerHandshakeBinding
{
    internal const int FrameLength = 70;
    private readonly byte[] _nonce;
    private readonly byte[] _digest;
    internal DeclaredUpdateParent Parent { get; }
    internal string TargetExecutablePath { get; }

    private UpdateWorkerHandshakeBinding(UpdateInstallerInvocation invocation, byte[] nonce, byte[] digest)
    {
        Parent = invocation.Parent;
        TargetExecutablePath = invocation.TargetExecutablePath;
        _nonce = nonce;
        _digest = digest;
    }

    internal static UpdateWorkerHandshakeBinding Create(UpdateInstallerInvocation invocation, string nonceHex)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (nonceHex is null || nonceHex.Length != 64 || !nonceHex.All(Uri.IsHexDigit))
            throw new InvalidDataException("The update attempt nonce is invalid.");
        var update = invocation.Prepared.Update;
        if (update.Sha256 is null || update.Sha256.Length != 64 || !update.Sha256.All(Uri.IsHexDigit)
            || update.SizeBytes is <= 0 or > UpdateManifestVerifier.MaximumPackageBytes
            || update.MinimumVersion is null || update.PackageUri is null
            || update.ReleaseNotes is null || update.ReleaseNotes.Length > 20_000
            || string.IsNullOrWhiteSpace(update.KeyId) || update.KeyId.Length > 128)
            throw new InvalidDataException("The update handshake artifact binding is invalid.");

        // Length-safe deterministic serialization of ALL verified fields plus
        // the immutable invocation vector/path. No raw content enters frames.
        // This digest binds metadata, not executable bytes or filesystem identity.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = update.Version.ToString(),
            MinimumVersion = update.MinimumVersion.ToString(),
            update.Channel,
            update.PublishedAt,
            PackageUri = update.PackageUri.AbsoluteUri,
            update.Sha256,
            update.SizeBytes,
            update.ReleaseNotes,
            update.KeyId,
            invocation.Prepared.InstallerPath,
            invocation.TargetExecutablePath,
            invocation.Parent,
            invocation.Arguments,
        });
        return new(invocation, Convert.FromHexString(nonceHex), SHA256.HashData(canonical));
    }

    internal byte[] CreateFrame(byte phase)
    {
        RequirePhase(phase);
        var frame = new byte[FrameLength];
        "KWTU"u8.CopyTo(frame);
        frame[4] = 1;
        frame[5] = phase;
        _nonce.CopyTo(frame, 6);
        _digest.CopyTo(frame, 38);
        return frame;
    }

    internal void ValidateFrame(byte[] frame, byte phase)
    {
        RequirePhase(phase);
        if (frame is null || frame.Length != FrameLength
            || !frame.AsSpan(0, 4).SequenceEqual("KWTU"u8)
            || frame[4] != 1 || frame[5] != phase
            || !CryptographicOperations.FixedTimeEquals(_nonce, frame.AsSpan(6, 32))
            || !CryptographicOperations.FixedTimeEquals(_digest, frame.AsSpan(38, 32)))
            throw new InvalidDataException("The update handshake receipt does not match this attempt.");
    }

    internal void RequireParent(UpdateProcessIdentityLease parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.ProcessId != Parent.ProcessId || parent.CreationTimeUtcTicks != Parent.CreationTimeUtcTicks
            || !string.Equals(parent.ExecutablePath, TargetExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The held update parent does not match this attempt.");
    }

    private static void RequirePhase(byte phase)
    {
        if (phase is not (1 or 2 or 3))
            throw new InvalidDataException("The update handshake phase is invalid.");
    }
}

// UNWIRED one-shot READY -> ARM -> ARMED exchange. Successful completion proves
// this bounded authenticated exchange only, NOT installation/exclusion/exit.
// Caller owns both leases and MUST keep the same original parent lease through
// its later actual exit wait. No process launch, app exit, installer or replay.
internal sealed class UpdateWorkerHandshake(UpdateWorkerHandshakeBinding binding)
{
    private readonly UpdateWorkerHandshakeBinding _binding = binding ?? throw new ArgumentNullException(nameof(binding));
    private int _started;

    internal static NamedPipeServerStream CreateParentPipe(out string name)
    {
        // Create BEFORE launching the known worker. Random name, one instance,
        // local-current-user ACL, no application/native-host channel reuse.
        name = "kwt-update-worker-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    }

    internal async Task ExchangeParentAsync(NamedPipeServerStream pipe, UpdateProcessIdentityLease worker,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = Begin(timeout, cancellationToken);
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(worker);
        RequireAsync(pipe);
        if (_binding.Parent.ProcessId != Environment.ProcessId)
            throw new InvalidDataException("The update parent binding is not this process.");
        using var self = UpdateProcessIdentityLease.Acquire(_binding.Parent, _binding.TargetExecutablePath);
        _binding.RequireParent(self);
        if (!pipe.IsConnected) await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        RequireDuplex(pipe);
        worker.AuthenticateClient(pipe);
        await ReadAsync(pipe, 1, deadline.Token).ConfigureAwait(false);
        worker.AuthenticateClient(pipe);
        await WriteAsync(pipe, 2, deadline.Token).ConfigureAwait(false);
        await ReadAsync(pipe, 3, deadline.Token).ConfigureAwait(false);
        worker.AuthenticateClient(pipe);
        deadline.Token.ThrowIfCancellationRequested();
    }

    internal async Task ExchangeWorkerAsync(NamedPipeClientStream pipe, UpdateProcessIdentityLease parent,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = Begin(timeout, cancellationToken);
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(parent);
        _binding.RequireParent(parent);
        if (!pipe.IsConnected) await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        // .NET initializes client IsAsync only when the native connection
        // handle is installed. Check the actual handle BEFORE any frame IO,
        // not an unconnected client's default false property.
        RequireAsync(pipe);
        RequireDuplex(pipe);
        parent.AuthenticateServer(pipe);
        await WriteAsync(pipe, 1, deadline.Token).ConfigureAwait(false);
        await ReadAsync(pipe, 2, deadline.Token).ConfigureAwait(false);
        // Finish all alive-parent authentication BEFORE terminal ARMED. A
        // post-send alive check would race a later legitimate parent exit.
        parent.AuthenticateServer(pipe);
        deadline.Token.ThrowIfCancellationRequested();
        await WriteAsync(pipe, 3, deadline.Token).ConfigureAwait(false);
    }

    private CancellationTokenSource Begin(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The update handshake attempt has already been consumed.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private async Task ReadAsync(Stream pipe, byte phase, CancellationToken token)
    {
        var prefix = new byte[2];
        await pipe.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt16BigEndian(prefix) != UpdateWorkerHandshakeBinding.FrameLength)
            throw new InvalidDataException("The update handshake frame length is invalid.");
        var frame = new byte[UpdateWorkerHandshakeBinding.FrameLength];
        await pipe.ReadExactlyAsync(frame, token).ConfigureAwait(false);
        _binding.ValidateFrame(frame, phase);
    }

    private async Task WriteAsync(Stream pipe, byte phase, CancellationToken token)
    {
        var frame = _binding.CreateFrame(phase);
        var packet = new byte[frame.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)frame.Length);
        frame.CopyTo(packet, 2);
        await pipe.WriteAsync(packet, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    private static void RequireAsync(PipeStream pipe)
    {
        if (!pipe.IsAsync) throw new InvalidDataException("The update handshake pipe must support bounded asynchronous IO.");
    }

    private static void RequireDuplex(PipeStream pipe)
    {
        if (!pipe.CanRead || !pipe.CanWrite)
            throw new InvalidDataException("The update handshake requires a connected duplex pipe.");
    }
}
