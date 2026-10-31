using System.ComponentModel;
using System.Runtime.InteropServices;
using KeyboardWtf.Services.Updates;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Tests;

// Actual local Windows oplocks on GUID-owned inert files/directories only.
// No GUI, native host, installer, profile, provider or external process runs.
// A deadline observes the original call; it never cancels/abandons that call.
// Every finally ACKs/closes our oplock and positively joins the original call.
// Contracts: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/breaking-oplocks
// https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_request_oplock
public sealed class UpdateIngressOplockTests
{
    private static readonly TimeSpan BreakLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AdmissionLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeldObservation = TimeSpan.FromMilliseconds(250);
    private readonly ITestOutputHelper _output;

    public UpdateIngressOplockTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("enter-reservation")]
    [InlineData("reserve-reservation")]
    [InlineData("enter-activity")]
    [InlineData("exclusive-activity")]
    public async Task Marker_oplock_cannot_hold_original_ingress_until_owner_ACK(string mode)
    {
        var fixture = new Fixture();
        using var reservation = mode == "exclusive-activity"
            ? UpdateInstallationIngressLease.TryReserve(fixture.Executable) : null;
        if (mode == "exclusive-activity") Assert.NotNull(reservation);
        var marker = mode.EndsWith("activity", StringComparison.Ordinal)
            ? ".keyboard.wtf-update-activity" : ".keyboard.wtf-update-reservation";
        using var oplock = OplockHolder.Acquire(Path.Combine(fixture.Installation, marker), directory: false);
        _output.WriteLine("OPLOCK_GRANTED|{0}|OS_ISSUING_THREAD={1}|OWNER_ALIVE={2}",
            mode, oplock.IssuingThreadId, oplock.OwnerThreadAlive);
        var original = StartOriginal(() => mode switch
        {
            "enter-reservation" or "enter-activity" => CoreResult.From(UpdateInstallationIngressLease.TryEnter(fixture.Executable)),
            "reserve-reservation" => CoreResult.From(UpdateInstallationIngressLease.TryReserve(fixture.Executable)),
            "exclusive-activity" => new(null, reservation.TryAcquireExclusive(), null),
            _ => throw new InvalidOperationException("Unknown isolated oplock case."),
        });
        CoreResult result = null;
        try
        {
            Assert.True(await CompletesWithinAsync(original, AdmissionLimit),
                "Original marker admission awaited the deliberately withheld oplock ACK.");
            result = await original;
            AssertRefused(result);
            // COMPLETE_IF_OPLOCKED may refuse a sharing conflict without first
            // breaking the granted oplock. Prompt refusal needs no break/ACK.
            // If Windows did publish a break, its real ACK contract is checked.
            var refusal = result.Error switch
            {
                null => "known-sharing-busy",
                Win32Exception native => "Win32Exception:" + native.NativeErrorCode,
                _ => result.Error.GetType().Name,
            };
            _output.WriteLine("MARKER_REFUSAL|{0}|{1}|OWNER_ALIVE={2}", mode, refusal, oplock.OwnerThreadAlive);
            var broke = oplock.ObserveAckRequiredBreakIfPresent();
            _output.WriteLine("MARKER_BREAK|{0}|ACK_REQUIRED_BREAK={1}", mode, broke);
        }
        finally
        {
            try { oplock.AcknowledgeIfBroken(); }
            finally
            {
                try { oplock.Dispose(); } // Releases our sharing handle on assertion/ACK failure.
                finally
                {
                    result ??= await original; // Join even if oplock cleanup reported an error.
                    result.Lease?.Dispose();
                }
            }
        }
        reservation?.Dispose();
        using var afterRelease = UpdateInstallationIngressLease.TryEnter(fixture.Executable);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public async Task Directory_RH_oplock_cannot_hold_original_namespace_pin_until_owner_ACK()
    {
        var fixture = new Fixture();
        using var oplock = OplockHolder.Acquire(fixture.Installation, directory: true);
        var original = StartOriginal(() => CoreResult.From(UpdateInstallationIngressLease.TryEnter(fixture.Executable)));
        CoreResult result = null;
        try
        {
            oplock.RequireActualAckRequiredBreak();
            Assert.True(await CompletesWithinAsync(original, AdmissionLimit),
                "Original directory pin awaited the deliberately withheld RH oplock ACK.");
            result = await original;
            AssertRefused(result);
        }
        finally
        {
            try { oplock.AcknowledgeIfBroken(); }
            finally
            {
                try { oplock.Dispose(); }
                finally
                {
                    result ??= await original;
                    result.Lease?.Dispose();
                }
            }
        }
        using var afterRelease = UpdateInstallationIngressLease.TryEnter(fixture.Executable);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public async Task Ordinary_file_create_positive_control_waits_for_real_ACK_then_succeeds()
    {
        var fixture = new Fixture();
        var marker = Path.Combine(fixture.Installation, ".keyboard.wtf-update-reservation");
        using var oplock = OplockHolder.Acquire(marker, directory: false);
        var original = StartOriginalOpen(() => Open(marker, Native.GenericRead, Native.ShareAll, directory: false));
        OpenResult result = null;
        try
        {
            oplock.RequireActualAckRequiredBreak();
            Assert.False(await CompletesWithinAsync(original, HeldObservation),
                "The positive control did not actually wait for its required ACK.");
            Assert.True(oplock.AcknowledgeIfBroken());
            result = await original;
            Assert.Equal(0, result.Error);
            Assert.False(result.Handle.IsInvalid);
            // Success occurs with the original oplock owner's file handle still open.
        }
        finally
        {
            try { oplock.AcknowledgeIfBroken(); }
            finally
            {
                try { oplock.Dispose(); }
                finally
                {
                    result ??= await original;
                    result.Handle?.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task Ordinary_directory_create_positive_control_waits_for_RH_ACK_before_sharing_refusal()
    {
        var fixture = new Fixture();
        using var oplock = OplockHolder.Acquire(fixture.Installation, directory: true);
        // Match the original namespace Pin access/sharing: the owner's DELETE
        // access conflicts with this omission of FILE_SHARE_DELETE.
        var original = StartOriginalOpen(() => Open(fixture.Installation, Native.GenericRead, 3, directory: true));
        OpenResult result = null;
        try
        {
            oplock.RequireActualAckRequiredBreak();
            Assert.False(await CompletesWithinAsync(original, HeldObservation),
                "The directory control did not actually wait for its required ACK.");
            Assert.True(oplock.AcknowledgeIfBroken());
            result = await original;
            Assert.True(result.Handle.IsInvalid);
            Assert.Equal(32, result.Error); // ACK does not waive the still-held DELETE sharing conflict.
        }
        finally
        {
            try { oplock.AcknowledgeIfBroken(); }
            finally
            {
                try { oplock.Dispose(); }
                finally
                {
                    result ??= await original;
                    result.Handle?.Dispose();
                }
            }
        }
        using var afterClose = Open(fixture.Installation, Native.GenericRead, 3, directory: true).Handle;
        Assert.False(afterClose.IsInvalid);
    }

    private static void AssertRefused(CoreResult result)
    {
        Assert.False(result.Accepted);
        Assert.Null(result.Lease);
        // The core may report known sharing busy or reject the alternate
        // STATUS_OPLOCK_BREAK_IN_PROGRESS as unknown. Neither can be admission.
        Assert.True(result.Error is null or InvalidDataException
            || result.Error is Win32Exception { NativeErrorCode: 32 or 742 },
            "Unexpected refusal: " + result.Error);
    }

    private static Task<CoreResult> StartOriginal(Func<CoreResult> call) =>
        Task.Factory.StartNew(() =>
        {
            try { return call(); }
            catch (Exception error) { return new CoreResult(null, false, error); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static Task<OpenResult> StartOriginalOpen(Func<OpenResult> call) =>
        Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task<bool> CompletesWithinAsync(Task original, TimeSpan bound) =>
        ReferenceEquals(original, await Task.WhenAny(original, Task.Delay(bound)));

    private static OpenResult Open(string path, uint access, uint share, bool directory)
    {
        var handle = Native.CreateFile(path, access, share, IntPtr.Zero, 3,
            Native.OpenReparse | (directory ? Native.BackupSemantics : 0), IntPtr.Zero);
        return new(handle, handle.IsInvalid ? Marshal.GetLastWin32Error() : 0);
    }

    private sealed record CoreResult(IDisposable Lease, bool Accepted, Exception Error)
    {
        internal static CoreResult From(IDisposable lease) => new(lease, lease is not null, null);
    }

    private sealed record OpenResult(SafeFileHandle Handle, int Error);

    private sealed class Fixture
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "kwt-ingress-oplock-" + Guid.NewGuid().ToString("N"));
        internal string Installation => Path.Combine(Root, "installed");
        internal string Executable => Path.Combine(Installation, "keyboard.wtf.exe");

        internal Fixture()
        {
            Directory.CreateDirectory(Installation);
            File.WriteAllBytes(Executable, []); // Inert data, never executed.
            using var initial = UpdateInstallationIngressLease.TryEnter(Executable);
            Assert.NotNull(initial); // Create both empty markers before requesting any directory oplock.
            // Retain this unique local evidence; do not recursively delete a namespace fixture.
        }
    }

    private sealed class OplockHolder : IDisposable
    {
        private readonly SafeFileHandle _file;
        private readonly OplockIo _request;
        private readonly Thread _ownerThread;
        private readonly ManualResetEventSlim _releaseOwner;
        private bool _acknowledged;
        private int _disposed;
        internal uint IssuingThreadId => _request.IssuingThreadId;
        internal bool OwnerThreadAlive => _ownerThread.IsAlive;

        private OplockHolder(SafeFileHandle file, OplockIo request, Thread ownerThread, ManualResetEventSlim releaseOwner)
        {
            _file = file;
            _request = request;
            _ownerThread = ownerThread;
            _releaseOwner = releaseOwner;
        }

        internal static OplockHolder Acquire(string path, bool directory)
        {
            // Pending event-backed Windows IO is canceled when its issuing
            // thread exits. Issue the grant on a retained dedicated thread,
            // not a yielding xUnit execution thread. It cannot exit until
            // Dispose has positively drained the request and closed its file.
            // https://learn.microsoft.com/en-us/windows/win32/sync/asynchronous-procedure-calls
            var releaseOwner = new ManualResetEventSlim(false);
            var ready = new TaskCompletionSource<(SafeFileHandle File, OplockIo Request)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerThread = new Thread(() =>
            {
                try
                {
                    ready.SetResult(CreateGrantedOwner(path, directory));
                    releaseOwner.Wait();
                }
                catch (Exception error) { ready.TrySetException(error); }
            }) { Name = "kwt-oplock-owner-" + Guid.NewGuid().ToString("N") };
            var started = false;
            try
            {
                ownerThread.Start();
                started = true;
                var resources = ready.Task.GetAwaiter().GetResult();
                return new(resources.File, resources.Request, ownerThread, releaseOwner);
            }
            catch
            {
                releaseOwner.Set();
                if (started) ownerThread.Join();
                releaseOwner.Dispose();
                throw;
            }
        }

        private static (SafeFileHandle File, OplockIo Request) CreateGrantedOwner(string path, bool directory)
        {
            // File RWH requires an ACK even for a compatible second read open.
            // A directory supports RH; DELETE access makes the core's share-3
            // directory pin a sharing conflict requiring a real RH-break ACK.
            var file = Native.CreateFile(path, Native.GenericRead | (directory ? Native.Delete : Native.GenericWrite),
                Native.ShareAll, IntPtr.Zero, 3,
                Native.Overlapped | Native.OpenReparse | (directory ? Native.BackupSemantics : 0), IntPtr.Zero);
            if (file.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                file.Dispose();
                throw new Win32Exception(error, "The isolated oplock owner could not be opened. NativeErrorCode=" + error + ".");
            }
            OplockIo request = null;
            try
            {
                request = new OplockIo(file, directory ? 3u : 7u, requestFlags: 1);
                Assert.True(request.IsPending, "A real Windows oplock was not granted as ERROR_IO_PENDING.");
                return (file, request);
            }
            catch
            {
                try { request?.Dispose(); }
                finally { file.Dispose(); }
                throw;
            }
        }

        internal void RequireActualAckRequiredBreak()
        {
            Assert.True(OwnerThreadAlive, "The real oplock issuing owner thread exited before terminal cleanup.");
            Assert.True(_request.WaitForSignal(BreakLimit), "No actual owned oplock break was observed.");
            var output = _request.Complete();
            Assert.Equal((ushort)1, output.StructureVersion);
            Assert.True((output.Flags & 1) != 0, "The observed oplock break did not require an ACK.");
        }

        internal bool ObserveAckRequiredBreakIfPresent()
        {
            Assert.True(OwnerThreadAlive, "The real oplock issuing owner thread exited before refusal observation.");
            if (!_request.WaitForSignal(TimeSpan.Zero)) return false;
            RequireActualAckRequiredBreak();
            return true;
        }

        internal bool AcknowledgeIfBroken()
        {
            Assert.True(OwnerThreadAlive, "The real oplock issuing owner thread exited before ACK/drain.");
            if (_acknowledged) return true;
            if (!_request.WaitForSignal(TimeSpan.Zero)) return false;
            var output = _request.Complete();
            if ((output.Flags & 1) == 0) return false;
            // Explicitly ACK to None, using a distinct OVERLAPPED/event whose
            // buffers also remain alive until their ORIGINAL IO is terminal.
            using var ack = new OplockIo(_file, requestedLevel: 0, requestFlags: 2);
            Assert.True(ack.WaitForSignal(BreakLimit), "The actual oplock ACK did not complete.");
            ack.Complete();
            _acknowledged = true;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _request.Dispose(); }
            finally
            {
                try { _file.Dispose(); }
                finally
                {
                    // Exit/join only AFTER the original pending IO is terminal
                    // and its file is closed, never as a cancellation shortcut.
                    _releaseOwner.Set();
                    _ownerThread.Join();
                    _releaseOwner.Dispose();
                }
            }
        }
    }

    private sealed class OplockIo : IDisposable
    {
        private readonly SafeFileHandle _file;
        private readonly EventWaitHandle _event = new(false, EventResetMode.ManualReset);
        private IntPtr _input;
        private IntPtr _output;
        private IntPtr _overlapped;
        private bool _terminal;
        private bool _started;
        private bool _disposed;
        internal bool IsPending { get; private set; }
        internal uint IssuingThreadId { get; }

        internal OplockIo(SafeFileHandle file, uint requestedLevel, uint requestFlags)
        {
            _file = file;
            IssuingThreadId = Native.GetCurrentThreadId();
            try
            {
                _input = Marshal.AllocHGlobal(Marshal.SizeOf<OplockInput>());
                _output = Marshal.AllocHGlobal(Marshal.SizeOf<OplockOutput>());
                _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
                Marshal.StructureToPtr(new OplockInput(1, (ushort)Marshal.SizeOf<OplockInput>(), requestedLevel, requestFlags), _input, false);
                Marshal.StructureToPtr(default(OplockOutput), _output, false);
                Marshal.StructureToPtr(new NativeOverlappedData { Event = _event.SafeWaitHandle.DangerousGetHandle() }, _overlapped, false);
                var completed = Native.DeviceIoControl(file, 0x00090240, _input, (uint)Marshal.SizeOf<OplockInput>(),
                    _output, (uint)Marshal.SizeOf<OplockOutput>(), out _, _overlapped);
                var error = completed ? 0 : Marshal.GetLastWin32Error();
                if (!completed && error != 997)
                    throw new Win32Exception(error, "The isolated Windows oplock IO was not accepted. NativeErrorCode=" + error + ".");
                _started = true;
                _terminal = completed;
                IsPending = !completed;
            }
            catch { Dispose(); throw; }
        }

        internal bool WaitForSignal(TimeSpan limit) => _terminal || _event.WaitOne(limit);

        internal OplockOutput Complete()
        {
            if (!_terminal)
            {
                var completed = Native.GetOverlappedResult(_file, _overlapped, out _, wait: true);
                var error = completed ? 0 : Marshal.GetLastWin32Error();
                _terminal = true;
                if (!completed) throw new Win32Exception(error, CompletionDiagnostic(
                    "The original isolated oplock IO did not complete successfully.", error));
            }
            return Marshal.PtrToStructure<OplockOutput>(_output);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_started && !_terminal)
                {
                    Native.CancelIoEx(_file, _overlapped);
                    // Cancellation is only requested above; THIS original-IO
                    // result positively joins terminal cancellation/completion.
                    var completed = Native.GetOverlappedResult(_file, _overlapped, out _, wait: true);
                    var error = completed ? 0 : Marshal.GetLastWin32Error();
                    _terminal = true;
                    if (!completed && error != 995)
                        throw new Win32Exception(error, CompletionDiagnostic(
                            "The isolated oplock did not drain after cancellation.", error));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(_overlapped);
                Marshal.FreeHGlobal(_output);
                Marshal.FreeHGlobal(_input);
                _overlapped = _output = _input = IntPtr.Zero;
                _event.Dispose();
            }
        }

        private string CompletionDiagnostic(string description, int error)
        {
            var status = unchecked((uint)Marshal.PtrToStructure<NativeOverlappedData>(_overlapped).Internal.ToUInt64());
            return description + " NativeErrorCode=" + error + "; OVERLAPPED.Internal NTSTATUS=0x" + status.ToString("X8") + ".";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct OplockInput(ushort StructureVersion, ushort StructureLength, uint RequestedLevel, uint Flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct OplockOutput
    {
        internal ushort StructureVersion;
        internal ushort StructureLength;
        internal uint OriginalLevel;
        internal uint NewLevel;
        internal uint Flags;
        internal uint AccessMode;
        internal ushort ShareMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedData
    {
        internal UIntPtr Internal;
        internal UIntPtr InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal IntPtr Event;
    }

    private static class Native
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint Delete = 0x00010000;
        internal const uint ShareAll = 7;
        internal const uint Overlapped = 0x40000000;
        internal const uint BackupSemantics = 0x02000000;
        internal const uint OpenReparse = 0x00200000;

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
            IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint transferred,
            [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
    }
}
