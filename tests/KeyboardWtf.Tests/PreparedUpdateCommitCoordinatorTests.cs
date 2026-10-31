using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Unwired protocol composition ONLY. Reflection permits a behavioral RED before
// the new types exist. No installer/file/process/app/profile/provider is touched.
// A trusted caller must supply an already-approved, prepared artifact and honest
// probe/launch evidence; these tests cannot establish those integration facts.
public sealed class PreparedUpdateCommitCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Typed_contract_uses_the_shared_gate_and_only_the_already_prepared_artifact()
    {
        var binding = Binding.Require();
        Assert.Equal(new[] { "Unknown", "Ready", "Busy" }, Enum.GetNames(binding.ProbeType));
        Assert.Equal(new[] { "Unknown", "DefinitelyNotStarted", "Started" }, Enum.GetNames(binding.LaunchType));
        Assert.Equal(0, binding.EnumValue(binding.ProbeType, "Unknown"));
        Assert.Equal(0, binding.EnumValue(binding.LaunchType, "Unknown"));
        Assert.Equal(new[] { "Deferred", "Cancelled", "NotStarted", "Started", "LaunchUnknown" },
            Enum.GetNames(binding.CommitOutcomeType));
        Assert.True(binding.Commit.GetParameters()[5].IsOptional);
        Assert.Equal(typeof(VerifiedUpdate), binding.PreparedUpdate.PropertyType);
        Assert.Equal(typeof(string), binding.PreparedPath.PropertyType);
    }

    [Fact]
    public async Task Busy_real_owner_defers_without_probing_launching_exiting_or_acknowledging()
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Gate.TryAcquire(out var owner));
        try
        {
            var effects = new Effects();
            var result = await fixture.Begin(effects: effects).ReadAsync();
            AssertResult(result, "Deferred");
            Assert.Empty(effects.Events);
            AssertBusy(fixture.Gate);
        }
        finally { owner.Dispose(); }
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_tentative_or_committed_freeze_defers_without_effects(bool committed)
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        if (committed) frozen.Commit();
        try
        {
            var effects = new Effects();
            AssertResult(await fixture.Begin(effects: effects).ReadAsync(), "Deferred");
            Assert.Empty(effects.Events);
            AssertUnavailable(fixture.Gate);
        }
        finally { frozen.Dispose(); }
        if (committed) AssertUnavailable(fixture.Gate);
        else AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Pre_cancelled_request_has_no_callbacks_or_freeze_to_thaw()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var effects = new Effects();
        AssertResult(await fixture.Begin(effects: effects, token: cancellation.Token).ReadAsync(), "Cancelled");
        Assert.Empty(effects.Events);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Preflight_owns_the_tentative_freeze_until_its_original_task_really_completes()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        using var cancellation = new CancellationTokenSource();
        var effects = new Effects();
        var call = fixture.Begin(probe: async (prepared, token) =>
        {
            effects.Add("probe");
            Assert.Same(fixture.Prepared, prepared);
            Assert.Equal(cancellation.Token, token);
            AssertUnavailable(fixture.Gate);
            entered.TrySetResult(true);
            await release.Task; // Deliberately does not obey caller cancellation.
            return fixture.ProbeValue("Ready");
        }, effects: effects, token: cancellation.Token);
        await entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        Assert.False(call.Original.IsCompleted);
        AssertUnavailable(fixture.Gate);
        Assert.Equal(new[] { "probe" }, effects.Events.ToArray());
        release.TrySetResult(true);
        AssertResult(await call.ReadAsync(), "Cancelled", thawed: true);
        Assert.Equal(new[] { "probe" }, effects.Events.ToArray());
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_timed_out_caller_waiter_never_thaws_the_held_original(bool timeout)
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var effects = new Effects();
        var call = fixture.Begin(probe: async (_, _) =>
        {
            effects.Add("probe");
            entered.TrySetResult(true);
            await release.Task;
            return fixture.ProbeValue("Busy");
        }, effects: effects);
        await entered.Task.WaitAsync(TestTimeout);
        if (timeout)
            await Assert.ThrowsAsync<TimeoutException>(() => call.Original.WaitAsync(TimeSpan.FromMilliseconds(25)));
        else
        {
            using var waiterCancellation = new CancellationTokenSource();
            var waiter = call.Original.WaitAsync(waiterCancellation.Token);
            waiterCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        Assert.False(call.Original.IsCompleted);
        AssertUnavailable(fixture.Gate);
        Assert.Equal(new[] { "probe" }, effects.Events.ToArray());
        release.TrySetResult(true);
        AssertResult(await call.ReadAsync(), "Deferred", thawed: true);
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData("Busy")]
    [InlineData("Unknown")]
    [InlineData("undefined")]
    [InlineData("default")]
    public async Task Non_ready_preflight_is_fail_closed_without_launch_and_thaws(string outcome)
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        var call = fixture.Begin(probe: (_, _) =>
        {
            effects.Add("probe");
            AssertUnavailable(fixture.Gate);
            return Task.FromResult(outcome == "undefined" ? int.MaxValue
                : outcome == "default" ? 0 : fixture.ProbeValue(outcome));
        }, effects: effects);
        AssertResult(await call.ReadAsync(), "Deferred", thawed: true);
        Assert.Equal(new[] { "probe" }, effects.Events.ToArray());
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData("sync-fault", "Deferred")]
    [InlineData("async-fault", "Deferred")]
    [InlineData("sync-cancel", "Cancelled")]
    [InlineData("async-cancel", "Cancelled")]
    [InlineData("null-task", "Deferred")]
    public async Task Failed_preflight_thaws_only_after_its_original_completion(string mode, string expected)
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        var release = fixture.Signal();
        var entered = fixture.Signal();
        Task<int> Probe(object _, CancellationToken token)
        {
            effects.Add("probe");
            AssertUnavailable(fixture.Gate);
            if (mode == "sync-fault") throw new IOException("Synthetic preflight failure");
            if (mode == "sync-cancel") throw new OperationCanceledException("Synthetic preflight cancellation");
            if (mode == "null-task") return null;
            return HeldFailureAsync();
        }
        async Task<int> HeldFailureAsync()
        {
            entered.TrySetResult(true);
            await release.Task;
            if (mode == "async-cancel") throw new OperationCanceledException("Synthetic preflight cancellation");
            throw new IOException("Synthetic preflight failure");
        }
        var call = fixture.Begin(probe: Probe, effects: effects);
        if (mode.StartsWith("async", StringComparison.Ordinal))
        {
            await entered.Task.WaitAsync(TestTimeout);
            Assert.False(call.Original.IsCompleted);
            AssertUnavailable(fixture.Gate);
            release.TrySetResult(true);
        }
        AssertResult(await call.ReadAsync(), expected, thawed: true);
        Assert.Equal(new[] { "probe" }, effects.Events.ToArray());
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Cancellation_raised_by_completed_ready_probe_prevents_launch_and_thaws()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var effects = new Effects();
        var call = fixture.Begin(probe: (_, _) =>
        {
            effects.Add("probe");
            cancellation.Cancel();
            return Task.FromResult(fixture.ProbeValue("Ready"));
        }, effects: effects, token: cancellation.Token);
        AssertResult(await call.ReadAsync(), "Cancelled", thawed: true);
        Assert.Equal(new[] { "probe" }, effects.Events.ToArray());
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Positive_definitely_not_started_evidence_thaws_without_exit_or_acknowledgement()
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        var call = fixture.Begin(launch: prepared =>
        {
            effects.Add("launch");
            Assert.Same(fixture.Prepared, prepared);
            AssertUnavailable(fixture.Gate);
            // Contract evidence: this synthetic adapter has no launch effects.
            return fixture.LaunchValue("DefinitelyNotStarted");
        }, effects: effects);
        AssertResult(await call.ReadAsync(), "NotStarted", thawed: true);
        Assert.Equal(new[] { "probe", "launch" }, effects.Events.ToArray());
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("undefined")]
    [InlineData("default")]
    [InlineData("throw-after-effect")]
    public async Task Ambiguous_launch_permanently_closes_admission_and_never_claims_success_or_replays(string mode)
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        var launchEffects = 0;
        var call = fixture.Begin(launch: _ =>
        {
            effects.Add("launch");
            AssertUnavailable(fixture.Gate);
            Interlocked.Increment(ref launchEffects); // Possible irreversible side effect.
            if (mode == "throw-after-effect") throw new IOException("Synthetic launch acknowledgement loss");
            return mode == "undefined" ? int.MaxValue
                : mode == "default" ? 0 : fixture.LaunchValue("Unknown");
        }, effects: effects);
        AssertResult(await call.ReadAsync(), "LaunchUnknown");
        Assert.Equal(new[] { "probe", "launch" }, effects.Events.ToArray());
        Assert.Equal(1, Volatile.Read(ref launchEffects));
        AssertUnavailable(fixture.Gate);
        var replayEffects = new Effects();
        AssertResult(await fixture.Begin(effects: replayEffects).ReadAsync(), "Deferred");
        Assert.Empty(replayEffects.Events);
        Assert.Equal(1, Volatile.Read(ref launchEffects));
        AssertUnavailable(fixture.Gate);
    }

    [Fact]
    public async Task Any_launcher_exception_is_unknown_without_positive_no_start_evidence()
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        AssertResult(await fixture.Begin(launch: _ =>
        {
            effects.Add("launch");
            throw new OperationCanceledException("Synthetic launcher cannot prove whether effects happened");
        }, effects: effects).ReadAsync(), "LaunchUnknown");
        Assert.Equal(new[] { "probe", "launch" }, effects.Events.ToArray());
        AssertUnavailable(fixture.Gate);
    }

    [Fact]
    public async Task Started_commits_then_requests_exit_once_before_awaiting_acknowledgement()
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        var result = await fixture.Begin(exit: () =>
        {
            effects.Add("exit");
            AssertUnavailable(fixture.Gate);
        }, acknowledge: prepared =>
        {
            effects.Add("acknowledge");
            Assert.Same(fixture.Prepared, prepared);
            AssertUnavailable(fixture.Gate);
            return Task.CompletedTask;
        }, effects: effects).ReadAsync();
        AssertResult(result, "Started");
        Assert.Equal(new[] { "probe", "launch", "exit", "acknowledge" }, effects.Events.ToArray());
        AssertUnavailable(fixture.Gate);
    }

    [Fact]
    public async Task Post_launch_cancellation_cannot_reclassify_or_thaw_confirmed_start()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var effects = new Effects();
        var call = fixture.Begin(launch: _ =>
        {
            effects.Add("launch");
            cancellation.Cancel();
            return fixture.LaunchValue("Started");
        }, effects: effects, token: cancellation.Token);
        AssertResult(await call.ReadAsync(), "Started");
        Assert.Equal(new[] { "probe", "launch", "exit", "acknowledge" }, effects.Events.ToArray());
        AssertUnavailable(fixture.Gate);
    }

    [Fact]
    public async Task Stalled_acknowledgement_keeps_committed_admission_closed_after_exit_request()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var effects = new Effects();
        var call = fixture.Begin(acknowledge: async _ =>
        {
            effects.Add("acknowledge");
            entered.TrySetResult(true);
            await release.Task;
        }, effects: effects);
        await entered.Task.WaitAsync(TestTimeout);
        Assert.False(call.Original.IsCompleted);
        Assert.Equal(new[] { "probe", "launch", "exit", "acknowledge" }, effects.Events.ToArray());
        AssertUnavailable(fixture.Gate);
        using var cancelledWaiter = new CancellationTokenSource();
        var waiter = call.Original.WaitAsync(cancelledWaiter.Token);
        cancelledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(call.Original.IsCompleted);
        AssertUnavailable(fixture.Gate);
        var duplicate = new Effects();
        AssertResult(await fixture.Begin(effects: duplicate).ReadAsync(), "Deferred");
        Assert.Empty(duplicate.Events);
        release.TrySetResult(true);
        AssertResult(await call.ReadAsync(), "Started");
        AssertUnavailable(fixture.Gate);
    }

    [Theory]
    [InlineData("sync-fault")]
    [InlineData("async-fault")]
    [InlineData("cancel")]
    [InlineData("null-task")]
    public async Task Lost_acknowledgement_reports_confirmed_started_failure_without_thaw_or_replay(string mode)
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        var call = fixture.Begin(acknowledge: _ =>
        {
            effects.Add("acknowledge");
            if (mode == "sync-fault") throw new IOException("Synthetic disconnected Settings client");
            if (mode == "null-task") return null;
            return mode == "cancel"
                ? Task.FromCanceled(new CancellationToken(canceled: true))
                : Task.FromException(new IOException("Synthetic acknowledgement failure"));
        }, effects: effects);
        AssertResult(await call.ReadAsync(), "Started", acknowledgementFailed: true);
        Assert.Equal(new[] { "probe", "launch", "exit", "acknowledge" }, effects.Events.ToArray());
        AssertUnavailable(fixture.Gate);
        var duplicate = new Effects();
        AssertResult(await fixture.Begin(effects: duplicate).ReadAsync(), "Deferred");
        Assert.Empty(duplicate.Events);
    }

    [Fact]
    public async Task Exit_request_failure_reports_started_without_success_acknowledgement_or_replay()
    {
        await using var fixture = new Fixture();
        var effects = new Effects();
        var call = fixture.Begin(exit: () =>
        {
            effects.Add("exit");
            throw new IOException("Synthetic graceful exit scheduling failure");
        }, effects: effects);
        AssertResult(await call.ReadAsync(), "Started", exitRequestFailed: true);
        Assert.Equal(new[] { "probe", "launch", "exit" }, effects.Events.ToArray());
        AssertUnavailable(fixture.Gate);
        var duplicate = new Effects();
        AssertResult(await fixture.Begin(effects: duplicate).ReadAsync(), "Deferred");
        Assert.Empty(duplicate.Events);
    }

    [Fact]
    public async Task Independent_coordinator_instances_sharing_one_gate_allow_only_one_overlapping_launch()
    {
        await using var fixture = new Fixture();
        var other = fixture.Binding.CreateCoordinator(fixture.Gate);
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var launches = 0;
        var exits = 0;
        var acknowledgements = 0;
        var first = fixture.Begin(probe: async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return fixture.ProbeValue("Ready");
        }, launch: _ =>
        {
            Interlocked.Increment(ref launches);
            return fixture.LaunchValue("Started");
        }, exit: () => Interlocked.Increment(ref exits), acknowledge: _ =>
        {
            Interlocked.Increment(ref acknowledgements);
            return Task.CompletedTask;
        });
        await entered.Task.WaitAsync(TestTimeout);
        var blocked = new ConcurrentQueue<string>();
        var contenders = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            var effects = new Effects();
            var result = await fixture.Begin(effects: effects, coordinator: index % 2 == 0 ? other : null).ReadAsync();
            AssertResult(result, "Deferred");
            Assert.Empty(effects.Events);
            blocked.Enqueue(result.Outcome);
        })).ToArray();
        foreach (var contender in contenders) fixture.TrackOriginal(contender);
        await Task.WhenAll(contenders).WaitAsync(TestTimeout);
        Assert.Equal(16, blocked.Count);
        Assert.False(first.Original.IsCompleted);
        AssertUnavailable(fixture.Gate);
        release.TrySetResult(true);
        AssertResult(await first.ReadAsync(), "Started");
        Assert.Equal(1, Volatile.Read(ref launches));
        Assert.Equal(1, Volatile.Read(ref exits));
        Assert.Equal(1, Volatile.Read(ref acknowledgements));
        AssertUnavailable(fixture.Gate);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("launch")]
    [InlineData("exit")]
    [InlineData("acknowledge")]
    public async Task Reentrant_attempt_cannot_acquire_freeze_or_launch_a_second_artifact(string stage)
    {
        await using var fixture = new Fixture();
        Call reentrant = null;
        var duplicate = new Effects();
        void Reenter(string current)
        {
            if (stage == current) reentrant = fixture.Begin(effects: duplicate);
        }
        var effects = new Effects();
        var first = fixture.Begin(probe: (_, _) =>
        {
            effects.Add("probe");
            Reenter("probe");
            return Task.FromResult(fixture.ProbeValue("Ready"));
        }, launch: _ =>
        {
            effects.Add("launch");
            Reenter("launch");
            return fixture.LaunchValue("Started");
        }, exit: () => { effects.Add("exit"); Reenter("exit"); }, acknowledge: _ =>
        {
            effects.Add("acknowledge");
            Reenter("acknowledge");
            return Task.CompletedTask;
        }, effects: effects);
        AssertResult(await first.ReadAsync(), "Started");
        Assert.NotNull(reentrant);
        AssertResult(await reentrant.ReadAsync(), "Deferred");
        Assert.Empty(duplicate.Events);
        Assert.Equal(new[] { "probe", "launch", "exit", "acknowledge" }, effects.Events.ToArray());
        AssertUnavailable(fixture.Gate);
    }

    [Fact]
    public async Task Every_callback_receives_the_same_approved_tuple_and_exact_unopened_path()
    {
        await using var fixture = new Fixture();
        var observed = new List<object>();
        var path = "C:\\synthetic unopened package\\approved setup.exe";
        var approved = Update() with { Version = new Version(7, 8, 9), ReleaseNotes = "Original approval" };
        var prepared = fixture.Binding.CreatePrepared(approved, path);
        void Observe(object item)
        {
            observed.Add(item);
            Assert.Same(prepared, item);
            Assert.Same(approved, fixture.Binding.PreparedUpdate.GetValue(item));
            Assert.Equal(path, fixture.Binding.PreparedPath.GetValue(item));
        }
        AssertResult(await fixture.Begin(prepared: prepared, probe: (item, _) =>
        {
            Observe(item);
            return Task.FromResult(fixture.ProbeValue("Ready"));
        }, launch: item =>
        {
            Observe(item);
            return fixture.LaunchValue("Started");
        }, acknowledge: item => { Observe(item); return Task.CompletedTask; }).ReadAsync(), "Started");
        Assert.Equal(3, observed.Count);
        Assert.Same(approved, fixture.Binding.PreparedUpdate.GetValue(prepared));
        Assert.Equal(path, fixture.Binding.PreparedPath.GetValue(prepared));
        AssertUnavailable(fixture.Gate);
    }

    [Fact]
    public async Task Fifty_local_no_start_cycles_thaw_without_automatic_retry_or_owner_accumulation()
    {
        await using var fixture = new Fixture();
        for (var index = 0; index < 50; index++)
        {
            var effects = new Effects();
            var call = fixture.Begin(launch: _ =>
            {
                effects.Add("launch");
                return fixture.LaunchValue("DefinitelyNotStarted");
            }, effects: effects);
            AssertResult(await call.ReadAsync(), "NotStarted", thawed: true);
            Assert.Equal(new[] { "probe", "launch" }, effects.Events.ToArray());
            AssertOpen(fixture.Gate);
        }
    }

    [Fact]
    public void Public_install_readiness_remains_disabled_after_adding_the_unwired_core()
    {
        _ = Binding.Require();
        var readiness = typeof(WebSettingsService).GetProperty("SafeUpdateInstallationReady",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(readiness);
        Assert.False((bool)readiness.GetValue(null));
    }

    private static VerifiedUpdate Update() => new(new Version(2, 3, 4), new Version(1, 0, 0), "stable",
        new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/synthetic/approved.exe"),
        new string('a', 64), 1234, "Synthetic approval; no download", "synthetic-key");

    private static void AssertResult(Result result, string outcome, bool exitRequestFailed = false,
        bool acknowledgementFailed = false, bool thawed = false)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(exitRequestFailed, result.ExitRequestFailed);
        Assert.Equal(acknowledgementFailed, result.AcknowledgementFailed);
        Assert.Equal(thawed, result.Thawed);
    }

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryFreeze(out var frozen);
        frozen?.Dispose();
        Assert.False(accepted);
    }

    private static void AssertUnavailable(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryAcquire(out var work);
        work?.Dispose();
        Assert.False(accepted);
        AssertBusy(gate);
    }

    private static void AssertOpen(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryAcquire(out var work));
        AssertBusy(gate);
        work.Dispose();
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
    }

    private sealed record Result(string Outcome, bool ExitRequestFailed, bool AcknowledgementFailed, bool Thawed);

    private sealed class Effects
    {
        internal ConcurrentQueue<string> Events { get; } = new();
        internal void Add(string name) => Events.Enqueue(name);
    }

    private sealed class Call(Task original, Binding binding)
    {
        internal Task Original { get; } = original;
        internal async Task<Result> ReadAsync()
        {
            await Original.WaitAsync(TestTimeout);
            var value = Original.GetType().GetProperty("Result")?.GetValue(Original);
            Assert.NotNull(value);
            return binding.ReadResult(value);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        // Require the complete missing contract BEFORE creating signals/work.
        internal Binding Binding { get; } = PreparedUpdateCommitCoordinatorTests.Binding.Require();
        private readonly ConcurrentBag<TaskCompletionSource<bool>> _signals = new();
        private readonly ConcurrentBag<Task> _originals = new();
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal object Prepared { get; }
        private readonly object _coordinator;

        internal Fixture()
        {
            _coordinator = Binding.CreateCoordinator(Gate);
            Prepared = Binding.CreatePrepared(Update(), "C:\\synthetic-unopened\\approved-setup.exe");
        }

        internal int ProbeValue(string name) => Binding.EnumValue(Binding.ProbeType, name);
        internal int LaunchValue(string name) => Binding.EnumValue(Binding.LaunchType, name);
        internal void TrackOriginal(Task task) => _originals.Add(task);

        internal TaskCompletionSource<bool> Signal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals.Add(signal);
            return signal;
        }

        internal Call Begin(Func<object, CancellationToken, Task<int>> probe = null,
            Func<object, int> launch = null, Action exit = null, Func<object, Task> acknowledge = null,
            Effects effects = null, CancellationToken token = default, object prepared = null, object coordinator = null)
        {
            effects ??= new Effects();
            probe ??= (_, _) => { effects.Add("probe"); return Task.FromResult(ProbeValue("Ready")); };
            launch ??= _ => { effects.Add("launch"); return LaunchValue("Started"); };
            exit ??= () => effects.Add("exit");
            acknowledge ??= _ => { effects.Add("acknowledge"); return Task.CompletedTask; };
            var original = Binding.Start(coordinator ?? _coordinator, prepared ?? Prepared,
                probe, launch, exit, acknowledge, token);
            _originals.Add(original);
            return new Call(original, Binding);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var signal in _signals) signal.TrySetResult(true);
            var all = Task.WhenAll(_originals);
            try { await all.WaitAsync(TestTimeout); }
            catch (Exception) when (all.IsCompleted) { /* Asserted completed failures are safe to observe. */ }
            // An unfinished timeout is NOT swallowed. No abandoned original
            // callback may be hidden by disposing a waiter or clearing UI.
        }
    }

    private sealed class Binding
    {
        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal Type PreparedType { get; }
        internal Type ProbeType { get; }
        internal Type LaunchType { get; }
        internal Type CommitOutcomeType { get; }
        internal PropertyInfo PreparedUpdate { get; }
        internal PropertyInfo PreparedPath { get; }
        internal MethodInfo Commit { get; }
        private readonly Type _resultType;
        private readonly ConstructorInfo _preparedConstructor;
        private readonly ConstructorInfo _coordinatorConstructor;
        private readonly PropertyInfo _outcome;
        private readonly PropertyInfo _exitFailed;
        private readonly PropertyInfo _ackFailed;
        private readonly PropertyInfo _thawed;

        private Binding()
        {
            var assembly = typeof(UpdateWorkAdmissionGate).Assembly;
            Type RequireType(string name)
            {
                var type = assembly.GetType("KeyboardWtf.Services.Updates." + name);
                Assert.True(type is not null, "The unwired prepared-update contract is missing: " + name);
                return type;
            }
            PreparedType = RequireType("PreparedUpdate");
            ProbeType = RequireType("PreparedUpdateProbeOutcome");
            LaunchType = RequireType("PreparedUpdateLaunchOutcome");
            CommitOutcomeType = RequireType("PreparedUpdateCommitOutcome");
            _resultType = RequireType("PreparedUpdateCommitResult");
            var coordinatorType = RequireType("PreparedUpdateCommitCoordinator");
            Assert.True(ProbeType.IsEnum && LaunchType.IsEnum && CommitOutcomeType.IsEnum);
            _preparedConstructor = PreparedType.GetConstructor(AllInstance, null,
                [typeof(VerifiedUpdate), typeof(string)], null);
            _coordinatorConstructor = coordinatorType.GetConstructor(AllInstance, null,
                [typeof(UpdateWorkAdmissionGate)], null);
            Assert.NotNull(_preparedConstructor);
            Assert.NotNull(_coordinatorConstructor);
            PreparedUpdate = RequireProperty(PreparedType, "Update", typeof(VerifiedUpdate));
            PreparedPath = RequireProperty(PreparedType, "InstallerPath", typeof(string));
            _outcome = RequireProperty(_resultType, "Outcome", CommitOutcomeType);
            _exitFailed = RequireProperty(_resultType, "ExitRequestFailed", typeof(bool));
            _ackFailed = RequireProperty(_resultType, "AcknowledgementFailed", typeof(bool));
            _thawed = RequireProperty(_resultType, "Thawed", typeof(bool));
            Commit = coordinatorType.GetMethod("CommitAsync", AllInstance, null,
                [PreparedType,
                    typeof(Func<,,>).MakeGenericType(PreparedType, typeof(CancellationToken), typeof(Task<>).MakeGenericType(ProbeType)),
                    typeof(Func<,>).MakeGenericType(PreparedType, LaunchType), typeof(Action),
                    typeof(Func<,>).MakeGenericType(PreparedType, typeof(Task)), typeof(CancellationToken)], null);
            Assert.NotNull(Commit);
            Assert.Equal(typeof(Task<>).MakeGenericType(_resultType), Commit.ReturnType);
        }

        internal static Binding Require() => new();
        internal int EnumValue(Type type, string name) => Convert.ToInt32(Enum.Parse(type, name));
        internal object CreateCoordinator(UpdateWorkAdmissionGate gate) => Invoke(() => _coordinatorConstructor.Invoke([gate]));
        internal object CreatePrepared(VerifiedUpdate update, string path) => Invoke(() => _preparedConstructor.Invoke([update, path]));

        internal Task Start(object coordinator, object prepared, Func<object, CancellationToken, Task<int>> probe,
            Func<object, int> launch, Action exit, Func<object, Task> acknowledge, CancellationToken token)
        {
            var probeDelegate = Adapter(nameof(CreateProbeAdapter), [PreparedType, ProbeType], probe);
            var launchDelegate = Adapter(nameof(CreateLaunchAdapter), [PreparedType, LaunchType], launch);
            var ackDelegate = Adapter(nameof(CreateAcknowledgementAdapter), [PreparedType], acknowledge);
            return Assert.IsAssignableFrom<Task>(Invoke(() => Commit.Invoke(coordinator,
                [prepared, probeDelegate, launchDelegate, exit, ackDelegate, token])));
        }

        internal Result ReadResult(object result)
        {
            Assert.Equal(_resultType, result.GetType());
            return new Result(_outcome.GetValue(result).ToString(), (bool)_exitFailed.GetValue(result),
                (bool)_ackFailed.GetValue(result), (bool)_thawed.GetValue(result));
        }

        private static PropertyInfo RequireProperty(Type owner, string name, Type type)
        {
            var property = owner.GetProperty(name, AllInstance);
            Assert.NotNull(property);
            Assert.Equal(type, property.PropertyType);
            return property;
        }

        private static Delegate Adapter(string name, Type[] arguments, object callback)
        {
            var method = typeof(PreparedUpdateCommitCoordinatorTests).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsAssignableFrom<Delegate>(Invoke(() => method.MakeGenericMethod(arguments).Invoke(null, [callback])));
        }

        private static object Invoke(Func<object> action)
        {
            try { return action(); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    private static Delegate CreateProbeAdapter<TPrepared, TOutcome>(Func<object, CancellationToken, Task<int>> callback)
        where TOutcome : struct, Enum => (Func<TPrepared, CancellationToken, Task<TOutcome>>)((prepared, token) =>
        {
            var original = callback(prepared, token); // Preserve synchronous fault/null-task evidence.
            return original is null ? null : ConvertProbeOutcomeAsync<TOutcome>(original);
        });

    private static async Task<TOutcome> ConvertProbeOutcomeAsync<TOutcome>(Task<int> original) where TOutcome : struct, Enum =>
        (TOutcome)Enum.ToObject(typeof(TOutcome), await original);

    private static Delegate CreateLaunchAdapter<TPrepared, TOutcome>(Func<object, int> callback)
        where TOutcome : struct, Enum => (Func<TPrepared, TOutcome>)(prepared =>
            (TOutcome)Enum.ToObject(typeof(TOutcome), callback(prepared)));

    private static Delegate CreateAcknowledgementAdapter<TPrepared>(Func<object, Task> callback) =>
        (Func<TPrepared, Task>)(prepared => callback(prepared));
}
