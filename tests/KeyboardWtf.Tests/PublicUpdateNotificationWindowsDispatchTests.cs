using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Actual Windows/WinForms hidden-handle + Application.Run + BeginInvoke proof.
// No Form is shown, focus is not changed, and no app/profile/NotifyIcon/timer,
// network, provider, installer or global Application.Exit is used. A guarded
// callback attempt is NOT proof of a visible OS toast or the installed app.
public sealed class PublicUpdateNotificationWindowsDispatchTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Actual_BeginInvoke_dispatch_runs_the_notice_attempt_on_the_owned_STA_pump()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Start();
        await original.WaitAsync(TestTimeout);
        await fixture.DrainPostedAsync();
        Assert.Equal(1, fixture.AcceptedNoticePosts);
        Assert.Equal(1, fixture.Notices);
        Assert.Equal(fixture.PumpThreadId, fixture.NoticeThreadId);
        Assert.Equal(ApartmentState.STA, fixture.NoticeApartment);
        Assert.NotEqual(Environment.CurrentManagedThreadId, fixture.NoticeThreadId);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Channel_change_before_real_UI_dispatch_suppresses_then_the_same_tuple_can_retry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var release = await fixture.HoldPumpAsync();
        var original = fixture.Start();
        Assert.Equal(1, fixture.AcceptedNoticePosts);
        Assert.False(original.IsCompleted);
        Assert.Equal(0, fixture.Notices);
        AssertBusy(fixture.Gate);
        fixture.Channel = "beta";
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        await fixture.DrainPostedAsync();
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);

        fixture.Channel = "stable";
        await fixture.Start().WaitAsync(TestTimeout);
        await fixture.DrainPostedAsync();
        Assert.Equal(2, fixture.AcceptedNoticePosts);
        Assert.Equal(1, fixture.Notices);
        Assert.Equal(fixture.PumpThreadId, fixture.NoticeThreadId);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Owner_STA_Stop_suppresses_a_real_pending_BeginInvoke_and_drains_without_UI_self_join()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hold = await fixture.HoldPumpForStopAsync();
        // The real UI loop is currently inside our tracked blocker callback;
        // BeginInvoke accepts the notice behind it on that same Control queue.
        var original = fixture.Start();
        Assert.Equal(1, fixture.AcceptedNoticePosts);
        Assert.False(original.IsCompleted);
        Assert.False(fixture.LastNoticeCallbackCompleted);
        Assert.False(fixture.LoopEnded);
        Assert.Equal(0, fixture.Notices);
        AssertBusy(fixture.Gate);

        hold.RequestStop.TrySetResult(true);
        var stopping = await hold.StopReturned.Task.WaitAsync(TestTimeout);
        Assert.Equal(fixture.PumpThreadId, fixture.StopThreadId);
        await stopping.WaitAsync(TestTimeout);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        Assert.False(hold.OriginalCallback.IsCompleted); // UI is STILL held after synchronous Dispose/Stop.
        Assert.False(fixture.LastNoticeCallbackCompleted);
        AssertOpen(fixture.Gate);

        // Release the callback and let the REAL WinForms queue deliver the
        // suppressed notice. No manual callback replay is used for this proof.
        hold.Release.TrySetResult(true);
        await hold.OriginalCallback.WaitAsync(TestTimeout);
        await fixture.DrainPostedAsync();
        Assert.True(fixture.LastNoticeCallbackCompleted);
        Assert.Equal(fixture.PumpThreadId, fixture.LastNoticeCallbackThreadId);
        Assert.True(fixture.AllPostedCompleted);
        Assert.Equal(0, fixture.Notices);
        await fixture.EndLoopAsync();
        await fixture.CloseAndJoinAsync();
        Assert.Equal(0, fixture.LateReplayThreadId);
        Assert.True(fixture.PumpJoined);
        AssertOpen(fixture.Gate);
    }

    private static VerifiedUpdate Update() => new(new Version(9, 0, 0), new Version(1, 4, 19), "stable",
        new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/keyboard.wtf-setup.exe"),
        new string('a', 64), 1024, "Synthetic Windows dispatch tuple", UpdateManifestVerifier.CurrentKeyId);

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryFreeze(out var frozen);
        frozen?.Dispose();
        Assert.False(accepted);
    }

    private static void AssertOpen(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TaskCompletionSource<bool> _ready = Signal();
        private readonly TaskCompletionSource<bool> _loopEnded = Signal();
        private readonly TaskCompletionSource<bool> _finish = Signal();
        private readonly TaskCompletionSource<bool> _exited = Signal();
        private readonly ConcurrentBag<TaskCompletionSource<bool>> _releases = new();
        private readonly ConcurrentBag<Task> _originals = new();
        private readonly ConcurrentQueue<PostedCallback> _posted = new();
        private readonly Thread _thread;
        private Control _control;
        private ApplicationContext _context;
        private PublicUpdateNotificationMonitor _monitor;
        private string _channel = "stable";
        private int _acceptedPosts;
        private int _notices;
        private int _noticeThreadId;
        private ApartmentState _noticeApartment;
        private int _lateReplayThreadId;
        private int _stopThreadId;
        private PostedCallback _lastNoticeCallback;
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal string Channel { get => Volatile.Read(ref _channel); set => Volatile.Write(ref _channel, value); }
        internal int AcceptedNoticePosts => Volatile.Read(ref _acceptedPosts);
        internal int Notices => Volatile.Read(ref _notices);
        internal int NoticeThreadId => Volatile.Read(ref _noticeThreadId);
        internal ApartmentState NoticeApartment => _noticeApartment;
        internal int PumpThreadId { get; private set; }
        internal int LateReplayThreadId => Volatile.Read(ref _lateReplayThreadId);
        internal bool LoopEnded => _loopEnded.Task.IsCompletedSuccessfully;
        internal bool AllPostedCompleted => _posted.All(callback => callback.Completion.IsCompleted);
        internal bool PumpJoined { get; private set; }
        internal int StopThreadId => Volatile.Read(ref _stopThreadId);
        internal bool LastNoticeCallbackCompleted => _lastNoticeCallback?.Completion.IsCompleted == true;
        internal int LastNoticeCallbackThreadId => _lastNoticeCallback?.ExecutionThreadId ?? 0;

        private Fixture()
        {
            _thread = new Thread(RunPump) { IsBackground = true, Name = "keyboard.wtf isolated update-dispatch test" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        internal static async Task<Fixture> CreateAsync()
        {
            // Required production contracts precede ANY owned thread/handle.
            Assert.True(OperatingSystem.IsWindows(), "These are actual Windows queue tests, not platform-skipped simulations.");
            Assert.NotNull(typeof(PublicUpdateNotificationMonitor).GetMethod("CheckOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(typeof(PublicUpdateNotificationMonitor).GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.NotNull(typeof(UpdateNotificationDispatchReceipt).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic));
            var fixture = new Fixture();
            try
            {
                await fixture._ready.Task.WaitAsync(TestTimeout);
                fixture._monitor = new(fixture.Gate, () => true, () => fixture.Channel,
                    (_, _) => Task.FromResult(Update()), fixture.PostNotice, _ =>
                    {
                        fixture._noticeApartment = Thread.CurrentThread.GetApartmentState();
                        Volatile.Write(ref fixture._noticeThreadId, Environment.CurrentManagedThreadId);
                        Interlocked.Increment(ref fixture._notices);
                        return true;
                    });
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private void RunPump()
        {
            try
            {
                // Neither creating this hidden standalone Control handle nor
                // running a context with NO MainForm makes a visible window.
                using var control = new Control { Visible = false };
                using var context = new ApplicationContext();
                _control = control;
                _context = context;
                _ = control.Handle;
                PumpThreadId = Environment.CurrentManagedThreadId;
                _ready.TrySetResult(true);
                Application.Run(context);
                _loopEnded.TrySetResult(true);
                _finish.Task.GetAwaiter().GetResult();
                foreach (var callback in _posted)
                {
                    if (!callback.Completion.IsCompleted)
                        Volatile.Write(ref _lateReplayThreadId, Environment.CurrentManagedThreadId);
                    callback.Run(); // Inert suppressed receipts; not a restarted pump.
                }
                // using disposal occurs on the owning STA AFTER managed
                // operation drainage. Thread.Join then proves this tail ended.
            }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
                _loopEnded.TrySetException(exception);
                _exited.TrySetException(exception);
                return;
            }
            finally
            {
                _exited.TrySetResult(true);
            }
        }

        internal Task Start()
        {
            var original = _monitor.CheckOnceAsync();
            _originals.Add(original);
            return original;
        }

        internal Task Stop()
        {
            var original = _monitor?.StopAsync() ?? Task.CompletedTask;
            _originals.Add(original);
            return original;
        }

        private void PostNotice(Action action)
        {
            _ = Post(action, notice: true);
            Interlocked.Increment(ref _acceptedPosts); // Only AFTER real BeginInvoke accepts.
        }

        private Task Post(Action action, bool notice = false)
        {
            var callback = new PostedCallback(action);
            _posted.Enqueue(callback);
            if (notice) _lastNoticeCallback = callback;
            _ = _control.BeginInvoke((Action)callback.Run);
            return callback.Completion;
        }

        internal async Task<TaskCompletionSource<bool>> HoldPumpAsync()
        {
            var entered = Signal();
            var release = Signal();
            _releases.Add(release);
            _ = Post(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); });
            await entered.Task.WaitAsync(TestTimeout);
            return release;
        }

        internal async Task<ShutdownHold> HoldPumpForStopAsync()
        {
            var entered = Signal();
            var requestStop = Signal();
            var release = Signal();
            var stopReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releases.Add(requestStop);
            _releases.Add(release);
            var originalCallback = Post(() =>
            {
                entered.TrySetResult(true);
                requestStop.Task.GetAwaiter().GetResult();
                _monitor.Dispose(); // Synchronous, nonblocking, on the ACTUAL owning UI thread.
                var stopping = Stop(); // Reuses that same published original shutdown lifetime.
                Volatile.Write(ref _stopThreadId, Environment.CurrentManagedThreadId);
                stopReturned.TrySetResult(stopping);
                release.Task.GetAwaiter().GetResult(); // Keep the UI held to expose self-join bugs.
            });
            await entered.Task.WaitAsync(TestTimeout);
            return new ShutdownHold(requestStop, release, stopReturned, originalCallback);
        }

        internal Task DrainPostedAsync() => Task.WhenAll(_posted.Select(callback => callback.Completion)).WaitAsync(TestTimeout);

        internal async Task EndLoopAsync()
        {
            if (!_loopEnded.Task.IsCompleted)
                _ = Post(() => _context.ExitThread()); // This context only, on its OWN UI thread.
            await _loopEnded.Task.WaitAsync(TestTimeout);
        }

        internal async Task CloseAndJoinAsync()
        {
            _finish.TrySetResult(true);
            Exception failure = null;
            try { await _exited.Task.WaitAsync(TestTimeout); }
            catch (Exception exception) { failure = exception; }
            // Never force-dispose a Control from a foreign thread or claim an
            // unknown worker stopped. Failed positive join remains a failure.
            PumpJoined = _thread.Join(TestTimeout);
            Assert.True(PumpJoined, "The owned hidden UI thread did not positively join; no foreign-thread cleanup was attempted.");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            await DrainPostedAsync();
        }

        public async ValueTask DisposeAsync()
        {
            var stopping = Stop();
            foreach (var release in _releases) release.TrySetResult(true);
            Exception failure = null;
            try
            {
                var originals = Task.WhenAll(_originals);
                try { await originals.WaitAsync(TestTimeout); }
                catch (Exception) when (originals.IsCompleted) { /* Observe only completed failures; unfinished work fails cleanup. */ }
                await stopping.WaitAsync(TestTimeout);
            }
            catch (Exception exception) { failure = exception; }
            if (!PumpJoined)
            {
                try { await EndLoopAsync(); }
                catch (Exception exception) { failure ??= exception; }
                // Initialization or loop-exit failure must not bypass positive
                // thread joining. No foreign-thread forced disposal is used.
                try { await CloseAndJoinAsync(); }
                catch (Exception exception) { failure ??= exception; }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            _monitor?.Dispose(); // Idempotent, already positively drained.
        }
    }

    private sealed record ShutdownHold(TaskCompletionSource<bool> RequestStop,
        TaskCompletionSource<bool> Release, TaskCompletionSource<Task> StopReturned, Task OriginalCallback);

    private sealed class PostedCallback(Action action)
    {
        private readonly TaskCompletionSource<bool> _completion = Signal();
        private int _running;
        private int _executionThreadId;
        internal Task Completion => _completion.Task;
        internal int ExecutionThreadId => Volatile.Read(ref _executionThreadId);
        internal void Run()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            Volatile.Write(ref _executionThreadId, Environment.CurrentManagedThreadId);
            Exception failure = null;
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (failure is null) _completion.TrySetResult(true);
                else _completion.TrySetException(failure);
            }
        }
    }
}
