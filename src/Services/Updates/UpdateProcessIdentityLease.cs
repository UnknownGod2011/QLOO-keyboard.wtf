using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KeyboardWtf.Services.Updates;

// UNWIRED Windows identity primitive. OS-reported path/time is not executable
// byte trust, a READY handshake, installer authority or native ingress exclusion.
// The trusted image path must come from independent updater metadata, not a
// peer's claimed path. Never reopens this process by PID after authentication.
internal sealed class UpdateProcessIdentityLease : IDisposable
{
    private const uint SynchronizeAndQueryLimited = 0x00101000;
    private const uint WaitObject = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = 0xffffffff;
    private SafeProcessHandle _handle;

    internal int ProcessId { get; }
    internal long CreationTimeUtcTicks { get; }
    internal string ExecutablePath { get; }

    private UpdateProcessIdentityLease(SafeProcessHandle handle, DeclaredUpdateParent identity, string actualPath)
    {
        _handle = handle;
        ProcessId = identity.ProcessId;
        CreationTimeUtcTicks = identity.CreationTimeUtcTicks;
        ExecutablePath = actualPath;
    }

    internal static UpdateProcessIdentityLease Acquire(DeclaredUpdateParent identity, string trustedExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trustedExecutablePath);
        if (identity.ProcessId <= 0 || identity.CreationTimeUtcTicks <= 0
            || identity.CreationTimeUtcTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("The update process declaration is invalid.");
        UpdatePathIdentity.RequireCanonicalLocalPath(trustedExecutablePath);
        UpdatePathIdentity.RequireCanonicalLocalPath(identity.ExecutablePath);
        if (!string.Equals(identity.ExecutablePath, trustedExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The declared update process is not the trusted image.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Update process identity requires Windows.");

        var handle = Native.OpenProcess(SynchronizeAndQueryLimited, false, (uint)identity.ProcessId);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (DateTime.FromFileTimeUtc(created).Ticks != identity.CreationTimeUtcTicks)
                throw new InvalidDataException("The update process creation identity does not match.");

            var path = new StringBuilder(32768);
            var length = path.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, path, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var actual = path.ToString();
            UpdatePathIdentity.RequireCanonicalLocalPath(actual);
            if (!string.Equals(actual, trustedExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update process image identity does not match.");

            // This local handle is not yet published; nothing else can dispose
            // it during this final readiness check or ownership transfer.
            if (IsSignaled(handle.DangerousGetHandle()))
                throw new InvalidDataException("The update process has already exited.");
            var lease = new UpdateProcessIdentityLease(handle, identity, actual);
            handle = null;
            return lease;
        }
        finally { handle?.Dispose(); }
    }

    internal bool HasExited
    {
        get
        {
            var handle = RetainHandle();
            try { return IsSignaled(handle.DangerousGetHandle()); }
            finally { handle.DangerousRelease(); }
        }
    }

    internal async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var handle = RetainHandle();
        try
        {
            // Hold one explicit SafeHandle reference for the ORIGINAL async
            // wait. Even concurrent public Dispose cannot close this raw kernel
            // handle. Per-iteration P/Invoke SafeHandle marshalling would reject
            // the now-closed wrapper despite this retained original reference.
            var original = handle.DangerousGetHandle();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) return false;
                if (IsSignaled(original)) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(40, remaining.TotalMilliseconds))),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally { handle.DangerousRelease(); }
    }

    internal void AuthenticateClient(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected) throw new InvalidOperationException("The update pipe is not connected.");
        AuthenticatePeer(() =>
        {
            if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return peer;
        });
    }

    internal void AuthenticateServer(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected) throw new InvalidOperationException("The update pipe is not connected.");
        AuthenticatePeer(() =>
        {
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var peer))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return peer;
        });
    }

    private void AuthenticatePeer(Func<uint> readPeer)
    {
        var handle = RetainHandle();
        try
        {
            var original = handle.DangerousGetHandle();
            if (IsSignaled(original)) throw new InvalidDataException("The update pipe peer has exited.");
            if (readPeer() != (uint)ProcessId)
                throw new InvalidDataException("The update pipe belongs to a different process.");
            if (IsSignaled(original)) throw new InvalidDataException("The update pipe peer has exited.");
        }
        finally { handle.DangerousRelease(); }
    }

    private SafeProcessHandle RetainHandle()
    {
        var handle = Volatile.Read(ref _handle)
            ?? throw new ObjectDisposedException(nameof(UpdateProcessIdentityLease));
        var retained = false;
        handle.DangerousAddRef(ref retained);
        if (!retained) throw new ObjectDisposedException(nameof(UpdateProcessIdentityLease));
        return handle;
    }

    private static bool IsSignaled(IntPtr original)
    {
        var outcome = Native.WaitForSingleObject(original, 0);
        if (outcome == WaitObject) return true;
        if (outcome == WaitTimeout) return false;
        if (outcome == WaitFailed) throw new Win32Exception(Marshal.GetLastWin32Error());
        throw new InvalidDataException("The update process exit state is unknown.");
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process,
            out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process,
            uint flags, StringBuilder path, ref int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr original, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    }
}
