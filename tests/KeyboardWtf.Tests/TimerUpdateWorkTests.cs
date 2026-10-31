using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Passive ownership tests, not installed timer/audio/UI acceptance. Fake ITimer
// gives a deterministic positive drain signal; one real .NET Timer test below
// separately verifies callback-tail drainage. No app, provider or OS UI is used.
// Detached announcement/TTS descendants remain outside this owner's proof.
public sealed class TimerUpdateWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_or_committed_gate_rejects_before_factory_or_any_timer_effect(bool commit)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            if (commit) frozen.Commit();
            var error = Record.Exception(() => fixture.Service.Start(TimeSpan.FromSeconds(3), "refused"));
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wait", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.FactoryCalls);
            Assert.Empty(fixture.Service.ActiveTimers);
            Assert.Empty(fixture.Events);
        }
        finally { frozen.Dispose(); }
        if (!commit) AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Completed_runtime_is_owned_through_effects_and_positive_async_drain()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Service.Start(TimeSpan.FromSeconds(2), "completion");
        var timer = Assert.Single(fixture.Timers);
        await Task.Run(timer.Fire).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.Single(fixture.Events.Where(value => value == "notify:completion"));
        Assert.Single(fixture.Events.Where(value => value == "history:timer_finished:completion"));
        Assert.Single(fixture.Events.Where(value => value == "changed:Completed:" + snapshot.Id));
        Assert.Single(fixture.Events.Where(value => value == "speak:completion finished."));
        Assert.True(timer.DisposeCalls >= 1);
        Assert.Equal(1, timer.AsyncDisposeCalls);
        AssertBusy(fixture.Gate);

        timer.ReleaseDrain();
        await AssertEventuallyIdle(fixture.Gate);
        timer.Fire(); // A late stale callback cannot complete, announce or dispose twice.
        Assert.Equal(1, timer.AsyncDisposeCalls);
        Assert.Single(fixture.Events.Where(value => value == "notify:completion"));
    }

    [Fact]
    public async Task Cancelled_runtime_is_removed_but_remains_owned_until_positive_drain()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Service.Start(TimeSpan.FromSeconds(2), "cancel me");
        var timer = Assert.Single(fixture.Timers);
        Assert.Equal(1, fixture.Service.Cancel(snapshot.Id));
        Assert.Equal(0, fixture.Service.Cancel(snapshot.Id));
        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.Contains("changed:Cancelled:" + snapshot.Id, fixture.Events);
        Assert.Contains("history:timer_cancelled:cancel me", fixture.Events);
        Assert.Contains("speak:Timer cancelled.", fixture.Events);
        Assert.DoesNotContain(fixture.Events, value => value.StartsWith("notify:", StringComparison.Ordinal));
        Assert.Equal(1, timer.AsyncDisposeCalls);
        AssertBusy(fixture.Gate);
        timer.Fire();
        Assert.Equal(1, timer.AsyncDisposeCalls);
        timer.ReleaseDrain();
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Service_dispose_is_nonblocking_and_keeps_each_removed_runtime_until_drain()
    {
        using var fixture = new Fixture();
        fixture.Service.Start(TimeSpan.FromSeconds(4), "first");
        fixture.Service.Start(TimeSpan.FromSeconds(5), "second");
        fixture.Service.Dispose();
        fixture.Service.Dispose();
        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.All(fixture.Timers, timer => Assert.Equal(1, timer.AsyncDisposeCalls));
        Assert.DoesNotContain(fixture.Events, value => value.StartsWith("speak:", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Events, value => value.StartsWith("notify:", StringComparison.Ordinal));
        AssertBusy(fixture.Gate);
        fixture.Timers[0].ReleaseDrain();
        AssertBusy(fixture.Gate);
        fixture.Timers[1].ReleaseDrain();
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Start_publication_and_concurrent_dispose_keep_the_armed_runtime_owned_through_drain()
    {
        var factoryEntered = Signal();
        var releaseFactory = Signal();
        var disposeStarted = Signal();
        Fixture fixture = null;
        fixture = new Fixture(factory: (callback, state, due, period) =>
        {
            var timer = new FakeTimer(callback, state, due, period, autoDrain: false);
            fixture.Timers.Add(timer);
            factoryEntered.TrySetResult(true);
            releaseFactory.Task.GetAwaiter().GetResult();
            return timer;
        });
        using (fixture)
        {
            var starting = Task.Run(() => fixture.Service.Start(TimeSpan.FromSeconds(3), "publication race"));
            Task disposing = null;
            try
            {
                await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                disposing = Task.Run(() =>
                {
                    disposeStarted.TrySetResult(true);
                    fixture.Service.Dispose();
                });
                await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(fixture.FactoryPublicationLockHeld);
                Assert.False(starting.IsCompleted);
                Assert.False(disposing.IsCompleted); // Factory is still inside the actual publication lock.
                AssertBusy(fixture.Gate);
            }
            finally
            {
                releaseFactory.TrySetResult(true);
                await starting.WaitAsync(TimeSpan.FromSeconds(5));
                if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Empty(fixture.Service.ActiveTimers);
            var timer = Assert.Single(fixture.Timers);
            Assert.Equal(1, timer.AsyncDisposeCalls);
            AssertBusy(fixture.Gate);
            timer.ReleaseDrain();
            await AssertEventuallyIdle(fixture.Gate);
        }
    }

    [Fact]
    public async Task Ordinary_dispose_failure_preserves_throw_drains_other_removed_timers_and_stays_conservatively_busy()
    {
        using var fixture = new Fixture();
        var brokenSnapshot = fixture.Service.Start(TimeSpan.FromSeconds(3), "broken stop");
        var healthySnapshot = fixture.Service.Start(TimeSpan.FromSeconds(4), "healthy stop");
        var runtimes = (System.Collections.IDictionary)typeof(JarvisTimerService).GetField("_active", Flags).GetValue(fixture.Service);
        var brokenRuntime = runtimes[brokenSnapshot.Id];
        var healthyRuntime = runtimes[healthySnapshot.Id];
        var broken = fixture.Timers[0];
        var healthy = fixture.Timers[1];
        broken.ThrowOnDispose = true; // Persistent fake fault: neither ordinary stop attempt is trustworthy.

        var error = await Record.ExceptionAsync(() => Task.Run(() => fixture.Service.Cancel("all")));
        Assert.IsType<IOException>(error);
        Assert.Equal("Synthetic ordinary dispose failure", error.Message);
        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.True(broken.DisposeCalls >= 1);
        Assert.Equal(0, broken.AsyncDisposeCalls);
        Assert.True(healthy.DisposeCalls >= 1);
        Assert.Equal(1, healthy.AsyncDisposeCalls);
        healthy.ReleaseDrain();
        // Join both real drain attempts before attributing remaining ownership to
        // the failed stop (rather than the healthy continuation still unwinding).
        await Task.WhenAll(
            (Task)brokenRuntime.GetType().GetProperty("DrainTask").GetValue(brokenRuntime),
            (Task)healthyRuntime.GetType().GetProperty("DrainTask").GetValue(healthyRuntime))
            .WaitAsync(TimeSpan.FromSeconds(5));
        AssertBusy(fixture.Gate); // No positive drain exists for the failed timer; do not claim release.
    }

    [Fact]
    public async Task Start_parent_survives_runtime_completion_while_start_history_is_still_running()
    {
        var entered = Signal();
        var release = Signal();
        Fixture fixture = null;
        fixture = new Fixture(autoDrain: true, history: kind =>
        {
            if (kind != "timer_started") return;
            fixture.Timers[0].Fire(); // AFTER factory return and runtime publication, not inline factory firing.
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
        });
        using (fixture)
        {
            var starting = Task.Run(() => fixture.Service.Start(TimeSpan.FromSeconds(2), "overlap"));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(fixture.Service.ActiveTimers);
                Assert.Equal(1, fixture.Timers[0].AsyncDisposeCalls);
                Assert.False(starting.IsCompleted);
                AssertBusy(fixture.Gate);
            }
            finally { release.TrySetResult(true); }
            await starting.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertEventuallyIdle(fixture.Gate);
        }
    }

    [Fact]
    public async Task Group_cancel_retains_all_owners_through_one_event_and_overlaps_child_admission()
    {
        using var fixture = new Fixture();
        fixture.Service.Start(TimeSpan.FromSeconds(3), "one");
        fixture.Service.Start(TimeSpan.FromSeconds(4), "two");
        IDisposable child = null;
        var announced = new List<JarvisLifecycleAnnouncement>();
        var eventWasOwned = false;
        var drainsNotStartedAtEvent = false;
        var childAccepted = false;
        fixture.Service.AnnouncementRequested += announcement =>
        {
            announced.Add(announcement);
            eventWasOwned = IsBusy(fixture.Gate);
            drainsNotStartedAtEvent = fixture.Timers.All(timer => timer.AsyncDisposeCalls == 0);
            childAccepted = fixture.Gate.TryAcquire(out child);
        };
        try
        {
            Assert.Equal(2, fixture.Service.Cancel("all"));
            var message = Assert.Single(announced);
            Assert.Equal("cancelled", message.State);
            Assert.Equal("2 timers were cancelled.", message.Detail);
            Assert.True(eventWasOwned);
            Assert.True(drainsNotStartedAtEvent);
            Assert.True(childAccepted);
            Assert.DoesNotContain(fixture.Events, value => value.StartsWith("speak:", StringComparison.Ordinal));
            Assert.All(fixture.Timers, timer => Assert.Equal(1, timer.AsyncDisposeCalls));
            foreach (var timer in fixture.Timers) timer.ReleaseDrain();
            AssertBusy(fixture.Gate); // Child handoff independently owns its real completion.
        }
        finally { child?.Dispose(); }
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Cancel_subscriber_failure_stops_and_drains_every_already_removed_runtime()
    {
        using var fixture = new Fixture();
        fixture.Service.Start(TimeSpan.FromSeconds(3), "one");
        fixture.Service.Start(TimeSpan.FromSeconds(4), "two");
        fixture.Service.Changed += snapshot =>
        {
            if (snapshot.State == JarvisTimerState.Cancelled)
                throw new IOException("Synthetic cancelled observer failure");
        };
        var error = await Record.ExceptionAsync(() => Task.Run(() => fixture.Service.Cancel("all")));
        Assert.IsType<IOException>(error); // Do not swallow or invent successful effects.
        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.All(fixture.Timers, timer =>
        {
            Assert.True(timer.DisposeCalls >= 1);
            Assert.Equal(1, timer.AsyncDisposeCalls);
        });
        AssertBusy(fixture.Gate);
        foreach (var timer in fixture.Timers) timer.ReleaseDrain();
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("notify")]
    [InlineData("history")]
    [InlineData("changed")]
    [InlineData("speak")]
    public async Task Completion_effect_failure_still_drains_without_changing_existing_exception_policy(string effect)
    {
        using var fixture = new Fixture(
            notify: () => { if (effect == "notify") throw new IOException("Synthetic notification failure"); },
            history: kind => { if (effect == "history" && kind == "timer_finished") throw new IOException("Synthetic history failure"); },
            speak: () => { if (effect == "speak") throw new IOException("Synthetic speech failure"); });
        fixture.Service.Changed += snapshot =>
        {
            if (effect == "changed" && snapshot.State == JarvisTimerState.Completed)
                throw new IOException("Synthetic completed observer failure");
        };
        fixture.Service.Start(TimeSpan.FromSeconds(2), "failure");
        var timer = Assert.Single(fixture.Timers);
        var error = await Record.ExceptionAsync(() => Task.Run(timer.Fire));
        Assert.IsType<IOException>(error); // Deliberate faults occur ONLY on fake callbacks.
        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.Equal(1, timer.AsyncDisposeCalls);
        AssertBusy(fixture.Gate);
        timer.ReleaseDrain();
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_armed_timer_survives_start_history_or_changed_failure(bool changed)
    {
        using var fixture = new Fixture(history: kind =>
        {
            if (!changed && kind == "timer_started") throw new IOException("Synthetic start history failure");
        });
        fixture.Service.Changed += snapshot =>
        {
            if (changed && snapshot.State == JarvisTimerState.Running)
                throw new IOException("Synthetic start observer failure");
        };
        Assert.Throws<IOException>(() => fixture.Service.Start(TimeSpan.FromSeconds(3), "armed"));
        var snapshot = Assert.Single(fixture.Service.ActiveTimers);
        var timer = Assert.Single(fixture.Timers);
        Assert.Equal(0, timer.DisposeCalls);
        Assert.Equal(0, timer.AsyncDisposeCalls);
        AssertBusy(fixture.Gate); // Parent failed, but an accepted armed runtime remains.
        Assert.Equal(1, fixture.Service.Cancel(snapshot.Id));
        timer.ReleaseDrain();
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Factory_throw_or_null_return_releases_unarmed_attempts(bool returnsNull)
    {
        using var fixture = new Fixture(factory: (_, _, _, _) => returnsNull ? null : throw new IOException("Synthetic factory failure"));
        var error = Record.Exception(() => fixture.Service.Start(TimeSpan.FromSeconds(3), "unarmed"));
        if (returnsNull) Assert.IsType<InvalidOperationException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Equal(1, fixture.FactoryCalls);
        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.Empty(fixture.Events);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_async_drain_is_conservatively_busy_not_falsely_idle(bool synchronousThrow)
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Service.Start(TimeSpan.FromSeconds(3), "failed drain");
        var timer = Assert.Single(fixture.Timers);
        timer.DrainFailure = synchronousThrow ? "throw" : "faulted";
        Assert.Equal(1, fixture.Service.Cancel(snapshot.Id));
        Assert.Empty(fixture.Service.ActiveTimers);
        Assert.Equal(1, timer.AsyncDisposeCalls);
        AssertBusy(fixture.Gate); // Deliberately unreleased: positive drainage was not established.
    }

    [Fact]
    public void Disposed_start_preserves_existing_refusal_before_factory_or_effects()
    {
        using var fixture = new Fixture();
        fixture.Service.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Service.Start(TimeSpan.FromSeconds(3), "disposed"));
        Assert.Equal(0, fixture.FactoryCalls);
        Assert.Empty(fixture.Events);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(172800, 86400)]
    public async Task Clamp_label_and_factory_scheduling_arguments_remain_unchanged(double requestedSeconds, double expectedSeconds)
    {
        using var fixture = new Fixture();
        var label = requestedSeconds == 0 ? "" : new string('x', 120);
        var snapshot = fixture.Service.Start(TimeSpan.FromSeconds(requestedSeconds), label);
        var timer = Assert.Single(fixture.Timers);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timer.Due);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
        Assert.Null(timer.State);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), snapshot.DueAt - snapshot.StartedAt);
        Assert.Equal(requestedSeconds == 0 ? "1 second timer" : new string('x', 80), snapshot.Label);
        Assert.True(fixture.FactoryOwned);
        Assert.True(fixture.FactoryPublicationLockHeld);
        Assert.Equal(0, timer.ChangeCalls);
        fixture.Service.Dispose();
        timer.ReleaseDrain();
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Existing_matching_keeps_case_insensitive_label_id_prefix_latest_and_all_behavior()
    {
        using var fixture = new Fixture(autoDrain: true);
        var first = fixture.Service.Start(TimeSpan.FromSeconds(3), "Alpha one");
        fixture.Service.Start(TimeSpan.FromSeconds(4), "alpha two");
        fixture.Service.Start(TimeSpan.FromSeconds(5), "Beta");
        Assert.Equal(2, fixture.Service.Cancel("ALPHA"));
        Assert.Single(fixture.Service.ActiveTimers);
        Assert.Equal(1, fixture.Service.Cancel());
        Assert.Empty(fixture.Service.ActiveTimers);
        var byId = fixture.Service.Start(TimeSpan.FromSeconds(6), "Prefix");
        Assert.Equal(1, fixture.Service.Cancel(byId.Id[..12]));
        fixture.Service.Start(TimeSpan.FromSeconds(7), "All one");
        fixture.Service.Start(TimeSpan.FromSeconds(8), "All two");
        Assert.Equal(2, fixture.Service.Cancel("all"));
        Assert.Equal(0, fixture.Service.Cancel(first.Id));
        await AssertEventuallyIdle(fixture.Gate);
        Assert.All(fixture.Timers, timer => Assert.Equal(1, timer.AsyncDisposeCalls));
    }

    [Fact]
    public async Task Forty_completion_cancel_races_keep_one_terminal_effect_and_one_drain()
    {
        for (var cycle = 0; cycle < 40; cycle++)
        {
            using var fixture = new Fixture(autoDrain: true);
            var snapshot = fixture.Service.Start(TimeSpan.FromSeconds(3), "race");
            var timer = Assert.Single(fixture.Timers);
            await Task.WhenAll(Task.Run(timer.Fire), Task.Run(() => fixture.Service.Cancel(snapshot.Id)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(fixture.Service.ActiveTimers);
            Assert.Single(fixture.Events.Where(value => value.StartsWith("changed:Completed:", StringComparison.Ordinal)
                || value.StartsWith("changed:Cancelled:", StringComparison.Ordinal)));
            Assert.Single(fixture.Events.Where(value => value.StartsWith("history:timer_finished:", StringComparison.Ordinal)
                || value.StartsWith("history:timer_cancelled:", StringComparison.Ordinal)));
            Assert.Single(fixture.Events.Where(value => value.StartsWith("speak:", StringComparison.Ordinal)));
            Assert.Equal(1, timer.AsyncDisposeCalls);
            await AssertEventuallyIdle(fixture.Gate);
        }
        // These are local matching/lifecycle races, not real Timer drain proof.
    }

    [Fact]
    public async Task Actual_SystemTimer_remains_owned_after_business_completion_until_callback_tail_returns()
    {
        var serviceEffectsFinished = Signal();
        var releaseTail = Signal();
        var actualCallbackExited = Signal();
        Exception callbackError = null;
        System.Threading.Timer actual = null;
        using var fixture = new Fixture(factory: (callback, state, due, period) =>
        {
            actual = new System.Threading.Timer(value =>
            {
                try
                {
                    // No assertions or intentional faults inside a real Timer callback.
                    callback(value);
                    serviceEffectsFinished.TrySetResult(true);
                    releaseTail.Task.GetAwaiter().GetResult();
                }
                catch (Exception error) { callbackError = error; serviceEffectsFinished.TrySetResult(true); }
                finally { actualCallbackExited.TrySetResult(true); }
            }, state, due, period);
            return actual;
        });
        try
        {
            fixture.Service.Start(TimeSpan.FromMilliseconds(10), "real callback"); // Existing clamp: one second.
            await serviceEffectsFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(callbackError);
            Assert.Empty(fixture.Service.ActiveTimers);
            Assert.Single(fixture.Events.Where(value => value == "speak:real callback finished."));
            Assert.False(actualCallbackExited.Task.IsCompleted);
            AssertBusy(fixture.Gate); // Ordinary Dispose already ran; callback is still actually alive.
        }
        finally
        {
            releaseTail.TrySetResult(true);
            fixture.Service.Dispose();
            if (actual is not null) await actual.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await actualCallbackExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertEventuallyIdle(fixture.Gate);
    }

    private sealed class Fixture : IDisposable
    {
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal JarvisTimerService Service { get; }
        internal List<FakeTimer> Timers { get; } = new();
        internal ConcurrentQueue<string> Events { get; } = new();
        internal int FactoryCalls;
        internal bool FactoryOwned;
        internal bool FactoryPublicationLockHeld;

        internal Fixture(bool autoDrain = false, Action notify = null, Action<string> history = null, Action speak = null,
            Func<TimerCallback, object, TimeSpan, TimeSpan, ITimer> factory = null)
        {
            var constructor = typeof(JarvisTimerService).GetConstructor(Flags, null,
                new[] { typeof(Action<string, string>), typeof(Action<string>), typeof(Action<string, string, bool>),
                    typeof(UpdateWorkAdmissionGate), typeof(Func<TimerCallback, object, TimeSpan, TimeSpan, ITimer>) }, null);
            Assert.True(constructor is not null, "The optional timer admission/drain seam is not implemented.");
            Assert.True(constructor.GetParameters()[3].IsOptional);
            Assert.True(constructor.GetParameters()[4].IsOptional);
            Assert.Null(constructor.GetParameters()[3].DefaultValue);
            Assert.Null(constructor.GetParameters()[4].DefaultValue);
            Func<TimerCallback, object, TimeSpan, TimeSpan, ITimer> create = (callback, state, due, period) =>
            {
                Interlocked.Increment(ref FactoryCalls);
                FactoryOwned = IsBusy(Gate);
                var sync = typeof(JarvisTimerService).GetField("_sync", Flags).GetValue(Service);
                FactoryPublicationLockHeld = Monitor.IsEntered(sync);
                if (factory is not null) return factory(callback, state, due, period);
                var timer = new FakeTimer(callback, state, due, period, autoDrain);
                Timers.Add(timer);
                return timer;
            };
            Action<string, string> notification = (_, message) => { Events.Enqueue("notify:" + message); notify?.Invoke(); };
            Action<string> speech = message => { Events.Enqueue("speak:" + message); speak?.Invoke(); };
            Action<string, string, bool> addHistory = (kind, detail, _) =>
            { Events.Enqueue("history:" + kind + ":" + detail); history?.Invoke(kind); };
            Service = (JarvisTimerService)Invoke(constructor, new object[] { notification, speech, addHistory, Gate, create });
            Service.Changed += snapshot => Events.Enqueue("changed:" + snapshot.State + ":" + snapshot.Id);
        }

        public void Dispose()
        {
            Service.Dispose();
            foreach (var timer in Timers) timer.ReleaseDrain();
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object state, TimeSpan due, TimeSpan period, bool autoDrain) : ITimer
    {
        private readonly TaskCompletionSource<bool> _drain = CompletedOrPending(autoDrain);
        internal TimeSpan Due { get; } = due;
        internal TimeSpan Period { get; } = period;
        internal object State { get; } = state;
        internal int DisposeCalls;
        internal int AsyncDisposeCalls;
        internal int ChangeCalls;
        internal string DrainFailure;
        internal bool ThrowOnDispose;
        internal void Fire() => callback(state);
        internal void ReleaseDrain() => _drain.TrySetResult(true);
        public bool Change(TimeSpan dueTime, TimeSpan period) { Interlocked.Increment(ref ChangeCalls); return true; }
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCalls);
            if (ThrowOnDispose) throw new IOException("Synthetic ordinary dispose failure");
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref AsyncDisposeCalls);
            if (DrainFailure == "throw") throw new IOException("Synthetic synchronous drain failure");
            if (DrainFailure == "faulted") return ValueTask.FromException(new IOException("Synthetic asynchronous drain failure"));
            return new ValueTask(_drain.Task);
        }
        private static TaskCompletionSource<bool> CompletedOrPending(bool complete)
        {
            var result = Signal();
            if (complete) result.SetResult(true);
            return result;
        }
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Invoke(ConstructorInfo constructor, object[] arguments)
    {
        try { return constructor.Invoke(arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool IsBusy(UpdateWorkAdmissionGate gate)
    {
        var froze = gate.TryFreeze(out var frozen);
        frozen?.Dispose();
        return !froze;
    }
    private static void AssertBusy(UpdateWorkAdmissionGate gate) => Assert.True(IsBusy(gate));
    private static void AssertIdle(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
    }
    private static async Task AssertEventuallyIdle(UpdateWorkAdmissionGate gate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (IsBusy(gate) && DateTime.UtcNow < deadline) await Task.Delay(5);
        AssertIdle(gate);
    }
}
