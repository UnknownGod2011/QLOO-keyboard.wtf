using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.BrowserAutomationV2;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Real coordinator Start/Execution and existing in-memory bridge protocol only.
// No bridge.Start(), named pipe, browser, provider, real profile, or Windows UI.
// Every begin_task reply is deliberately null: execution fails before planning.
// Separate cancellation cleanup, tab activation, CTS/planner tails and detached
// announcement descendants remain outside this passive lifetime proof.
[Collection("Live event integration")]
public sealed class BrowserTaskUpdateWorkTests
{
    private const string Request = "Use Browser Automation to fill this form.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_or_committed_gate_refuses_before_runtime_planner_status_or_commands(bool commit)
    {
        await using var fixture = new Fixture();
        var statuses = 0;
        fixture.Coordinator.StatusChanged += _ => statuses++;
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            if (commit) frozen.Commit();
            var result = fixture.Start();
            Assert.False(result.Started);
            Assert.False(result.NeedsConsent);
            Assert.False(result.NeedsClarification);
            Assert.Contains("update", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wait", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("", result.TaskId);
            Assert.Null(fixture.Active);
            Assert.Equal(0, statuses);
            Assert.Empty(fixture.Notices);
            Assert.Equal("", fixture.PlannerTaskId);
            Assert.Null(fixture.Poll());
        }
        finally { frozen.Dispose(); }
        if (!commit) AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Duplicate_running_attempt_releases_only_its_attempt_lease()
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Start().Started);
        var runtime = fixture.Active;
        var execution = Execution(runtime);
        var begin = await fixture.Next("begin_task");

        var duplicate = fixture.Start();
        Assert.False(duplicate.Started);
        Assert.Contains("already running", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(runtime, fixture.Active);
        Assert.Same(execution, Execution(runtime));
        Assert.Null(fixture.Poll());
        AssertBusy(fixture.Gate);

        fixture.Reply(begin, null);
        var end = await fixture.Next("end_task");
        fixture.Reply(end, new { ok = true });
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.AssertEventuallyIdle();
        Assert.Equal(2, fixture.DispatchedCommands);
    }

    [Fact]
    public async Task Execution_owner_spans_begin_task_and_the_existing_awaited_end_task_cleanup()
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Start().Started);
        var runtime = fixture.Active;
        var execution = Execution(runtime);
        var begin = await fixture.Next("begin_task");
        Assert.False(execution.IsCompleted);
        AssertBusy(fixture.Gate);

        fixture.Reply(begin, null);
        var end = await fixture.Next("end_task");
        Assert.Equal(BrowserTaskState.Failed, State(runtime));
        Assert.False(execution.IsCompleted);
        Assert.Same(execution, Execution(runtime));
        AssertBusy(fixture.Gate); // Terminal UI is not actual execution cleanup.

        fixture.Reply(end, new { ok = true });
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(execution.IsCompletedSuccessfully);
        await fixture.AssertEventuallyIdle();
        Assert.Equal(2, fixture.DispatchedCommands);
    }

    [Fact]
    public async Task Cancellation_before_original_TaskRun_delegate_still_releases_through_backend_disposal()
    {
        await using var fixture = new Fixture();
        using var backend = SyntheticBackend();
        var backendDisposed = Signal();
        var ownedDuringDispose = false;
        using var registration = backend.CancellationToken.Register(() =>
        {
            ownedDuringDispose = IsBusy(fixture.Gate);
            backendDisposed.TrySetResult(true);
        });
        fixture.Coordinator.StatusChanged += snapshot =>
        {
            if (snapshot.State != BrowserTaskState.Queued) return;
            var runtime = fixture.Active;
            Set(runtime, "BackendOperation", backend);
            Cancellation(runtime).Cancel(); // Queued Publish precedes the unchanged Task.Run(token).
        };

        Assert.True(fixture.Start().Started);
        var execution = Execution(fixture.Active);
        await backendDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(ownedDuringDispose);
        Assert.True(execution.IsCanceled);
        Assert.Null(fixture.Poll()); // The cancelled Task.Run delegate never called BeginTask.
        Assert.Empty(fixture.Notices);
        await fixture.AssertEventuallyIdle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_backend_dispose_callback_is_owned_until_it_finishes_even_when_it_throws(bool throws)
    {
        await using var fixture = new Fixture();
        using var backend = SyntheticBackend();
        var entered = Signal();
        var release = Signal();
        var exited = Signal();
        using var registration = backend.CancellationToken.Register(() =>
        {
            entered.TrySetResult(true);
            try
            {
                release.Task.GetAwaiter().GetResult();
                if (throws) throw new IOException("Synthetic backend disposal callback failure");
            }
            finally { exited.TrySetResult(true); }
        });
        Assert.True(fixture.Start().Started);
        var runtime = fixture.Active;
        var execution = Execution(runtime);
        var begin = await fixture.Next("begin_task");
        Set(runtime, "BackendOperation", backend);
        fixture.Reply(begin, null);
        fixture.Reply(await fixture.Next("end_task"), new { ok = true });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(execution.IsCompletedSuccessfully);
            Assert.Same(execution, Execution(runtime));
            Assert.Equal(BrowserTaskState.Failed, State(runtime));
            Assert.False(exited.Task.IsCompleted);
            AssertBusy(fixture.Gate);
        }
        finally { release.TrySetResult(true); }
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.AssertEventuallyIdle();
        // Disposal remains the existing separate continuation, not a replacement
        // Execution task: even a disposal fault must not rewrite execution outcome.
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Dispose(); // Existing idempotence remains unchanged.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_setup_failure_before_scheduling_releases_attempt(bool observerThrows)
    {
        await using var fixture = new Fixture(nullPlanner: !observerThrows);
        if (observerThrows)
            fixture.Coordinator.StatusChanged += snapshot =>
            {
                if (snapshot.State == BrowserTaskState.Queued)
                    throw new IOException("Synthetic queued publication failure");
            };

        var error = Record.Exception(() => fixture.Start());
        if (observerThrows) Assert.IsType<IOException>(error);
        else Assert.IsType<NullReferenceException>(error); // Existing planner setup, not new admission semantics.
        Assert.NotNull(fixture.Active);
        Assert.Null(Execution(fixture.Active));
        Assert.Null(fixture.Poll());
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Faulted_execution_still_keeps_EndTask_and_releases_its_disposal_continuation_owner()
    {
        await using var fixture = new Fixture();
        fixture.Coordinator.StatusChanged += snapshot =>
        {
            if (snapshot.State != BrowserTaskState.Queued)
                throw new IOException("Synthetic execution observer failure");
        };
        Assert.True(fixture.Start().Started);
        var runtime = fixture.Active;
        var execution = Execution(runtime);
        // ReadingPage publication throws before any BeginTask; finally still awaits EndTask.
        var end = await fixture.Next("end_task");
        Assert.False(execution.IsCompleted);
        AssertBusy(fixture.Gate);
        fixture.Reply(end, new { ok = true });
        await Assert.ThrowsAsync<IOException>(() => execution);
        Assert.True(execution.IsFaulted);
        Assert.Same(execution, Execution(runtime));
        await fixture.AssertEventuallyIdle();
        Assert.Equal(1, fixture.DispatchedCommands);
    }

    [Fact]
    public async Task Replacement_has_an_independent_owner_while_old_terminal_execution_is_still_cleaning_up()
    {
        await using var fixture = new Fixture();
        var first = fixture.Start();
        Assert.True(first.Started);
        var previous = fixture.Active;
        var oldExecution = Execution(previous);
        fixture.Reply(await fixture.Next("begin_task"), null);
        var oldEnd = await fixture.Next("end_task");
        Assert.Equal(BrowserTaskState.Failed, State(previous));
        Assert.False(oldExecution.IsCompleted);

        var second = fixture.Start();
        Assert.True(second.Started);
        Assert.NotEqual(first.TaskId, second.TaskId);
        var replacement = fixture.Active;
        var newExecution = Execution(replacement);
        fixture.Reply(await fixture.Next("begin_task"), null);
        fixture.Reply(await fixture.Next("end_task"), new { ok = true });
        await newExecution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(oldExecution.IsCompleted);
        AssertBusy(fixture.Gate);

        fixture.Reply(oldEnd, new { ok = true });
        await oldExecution.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.AssertEventuallyIdle();
        Assert.Same(replacement, fixture.Active);
        Assert.Equal(second.TaskId, fixture.Coordinator.Snapshot().TaskId);
        Assert.Equal(4, fixture.DispatchedCommands);
    }

    [Fact]
    public async Task Disabled_feature_preserves_existing_eligibility_refusal_even_if_gate_is_frozen()
    {
        await using var fixture = new Fixture();
        fixture.Settings.Current.BrowserAutomationEnabled = false;
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        using (frozen)
        {
            var refused = fixture.Start();
            Assert.False(refused.Started);
            Assert.Equal("Browser Automation is disabled in Settings.", refused.Message);
            Assert.Null(fixture.Active);
            Assert.Null(fixture.Poll());
        }
        AssertIdle(fixture.Gate);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<object> _runtimes = new();
        private readonly MethodInfo _process;
        private readonly ConstructorInfo _constructor;
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal SettingsService Settings { get; }
        internal BrowserHelperBridge Bridge { get; }
        internal BrowserAutomationPlanner Planner { get; }
        internal BrowserAutomationCoordinator Coordinator { get; }
        internal List<string> Notices { get; } = new();
        internal int DispatchedCommands { get; private set; }
        internal object Active => typeof(BrowserAutomationCoordinator).GetField("_active", Flags).GetValue(Coordinator);
        internal string PlannerTaskId => (string)typeof(BrowserAutomationPlanner).GetField("_healthTaskId", Flags).GetValue(Planner);

        internal Fixture(bool nullPlanner = false)
        {
            // Missing planned seam gives behavioral RED before resources/fixtures exist.
            _constructor = typeof(BrowserAutomationCoordinator).GetConstructor(Flags, null,
                new[] { typeof(SettingsService), typeof(NotificationService), typeof(BrowserHelperBridge),
                    typeof(BrowserAutomationPlanner), typeof(PaidAccountService), typeof(UpdateWorkAdmissionGate) }, null);
            Assert.True(_constructor is not null, "The optional coordinator-owned updater gate is not implemented.");
            Assert.True(_constructor.GetParameters()[5].IsOptional);
            Assert.Null(_constructor.GetParameters()[5].DefaultValue);
            _process = typeof(BrowserHelperBridge).GetMethod("ProcessMessageAsync", Flags);
            Assert.NotNull(_process);
            // No Load/Save/ApplyToState: settings are task-specific in-memory only.
            Settings = new SettingsService(Path.Combine(Path.GetTempPath(), "keyboard-wtf-ba-update-tests", Guid.NewGuid().ToString("N"), "settings.json"));
            Settings.Current.BrowserAutomationConsentVersion = SettingsService.CurrentBrowserAutomationConsentVersion;
            Settings.Current.BrowserAutomationEnabled = true;
            Bridge = new BrowserHelperBridge();
            Planner = new BrowserAutomationPlanner();
            var notices = NotificationService.CreateForTesting((title, message, _) =>
            { lock (Notices) Notices.Add(title + ": " + message); });
            Coordinator = (BrowserAutomationCoordinator)_constructor.Invoke(new object[]
                { Settings, notices, Bridge, nullPlanner ? null : Planner, null, Gate });
            Process("hello").GetAwaiter().GetResult();
            Assert.True(Bridge.Status.CommandChannelReady);
        }

        internal BrowserAutomationStartResult Start()
        {
            try { return Coordinator.Start(Request); }
            finally
            {
                var active = Active;
                if (active is not null && !_runtimes.Contains(active)) _runtimes.Add(active);
            }
        }

        internal JsonElement? Poll() => ReadCommand(Process("poll").GetAwaiter().GetResult());

        internal async Task<JsonElement> Next(string type)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var command = ReadCommand(await Process("poll", wait: true).WaitAsync(TimeSpan.FromSeconds(2)));
                if (command is not null)
                {
                    Assert.Equal(type, command.Value.GetProperty("type").GetString());
                    return command.Value;
                }
            }
            throw new Xunit.Sdk.XunitException("No synthetic " + type + " command was queued.");
        }

        internal void Reply(JsonElement command, object result) =>
            Process("command_result", command, result).GetAwaiter().GetResult();

        private JsonElement? ReadCommand(JsonElement message)
        {
            var value = message.GetProperty("command");
            if (value.ValueKind == JsonValueKind.Null) return null;
            var type = value.GetProperty("type").GetString();
            Assert.Contains(type, new[] { "begin_task", "end_task", "cancel_task" });
            DispatchedCommands++;
            return value.Clone();
        }

        private async Task<JsonElement> Process(string type, JsonElement? command = null,
            object result = null, bool wait = false)
        {
            var json = JsonSerializer.Serialize(new
            {
                type, waitForCommand = wait,
                taskId = command?.GetProperty("taskId").GetString(),
                commandId = command?.GetProperty("commandId").GetString(), result,
                helper = new
                {
                    extensionId = "gojdebdikepkpapmnggeljneamjdigbh",
                    serviceWorkerVersion = BrowserHelperProtocol.Version,
                    contentScriptVersion = BrowserHelperProtocol.Version,
                    activeTabFresh = true, commandChannelReady = true, commandAuthoritySupported = true,
                    activeUrl = "https://example.org/synthetic-form", sessionId = "offline-update-test", windowId = 7, tabId = 42,
                },
            });
            var response = await (Task<string>)Invoke(_process, Bridge, new object[] { json, CancellationToken.None });
            using var document = JsonDocument.Parse(response);
            return document.RootElement.Clone();
        }

        internal async Task AssertEventuallyIdle()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (IsBusy(Gate) && DateTime.UtcNow < deadline) await Task.Delay(5);
            AssertIdle(Gate);
        }

        public async ValueTask DisposeAsync()
        {
            // Join bounded synthetic execution even when a test assertion fails.
            foreach (var runtime in _runtimes)
            {
                if (Execution(runtime)?.IsCompleted == false)
                {
                    Set(runtime, "CancellationReason", "Offline fixture cleanup");
                    Cancellation(runtime).Cancel();
                }
            }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_runtimes.Any(runtime => Execution(runtime)?.IsCompleted == false) && DateTime.UtcNow < deadline)
            {
                var command = Poll();
                if (command is not null) Reply(command.Value, null);
                await Task.Delay(5);
            }
            Bridge.Dispose();
            foreach (var runtime in _runtimes)
            {
                var execution = Execution(runtime);
                if (execution is not null)
                    _ = await Record.ExceptionAsync(async () => { await execution.WaitAsync(TimeSpan.FromSeconds(5)); });
                Cancellation(runtime).Dispose();
            }
            Planner.Dispose();
            // No Coordinator.Dispose: it includes separate cancellation cleanup,
            // deliberately outside this test boundary (and the null-planner case).
        }
    }

    private static AiBackendOperation SyntheticBackend()
    {
        var configuration = new AiBackendConfiguration(1, AiBackendPolicy.Managed);
        return new(Guid.NewGuid(), configuration, new AiBackendBinding(configuration, null), CancellationToken.None);
    }
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Task Execution(object runtime) => (Task)runtime.GetType().GetProperty("Execution").GetValue(runtime);
    private static BrowserTaskState State(object runtime) => (BrowserTaskState)runtime.GetType().GetProperty("State").GetValue(runtime);
    private static CancellationTokenSource Cancellation(object runtime) => (CancellationTokenSource)runtime.GetType().GetProperty("Cancellation").GetValue(runtime);
    private static void Set(object runtime, string name, object value) => runtime.GetType().GetProperty(name).SetValue(runtime, value);
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
    private static object Invoke(MethodInfo method, object owner, object[] arguments)
    {
        try { return method.Invoke(owner, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
