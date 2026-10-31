using System.Reflection;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using System.Text.Json;
using Xunit;

namespace KeyboardWtf.Tests;

// Passive dispatcher ownership only: no socket, microphone, provider, UI,
// application construction, installer, or actual command execution.
public sealed class LiveToolDispatcherUpdateWorkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_tool_admission_refuses_before_registration_and_same_id_can_run_after_thaw(bool staged)
    {
        var gate = new UpdateWorkAdmissionGate();
        var effects = 0;
        using var dispatcher = Queue(gate, (_, _, _) =>
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(Ok());
        });
        Assert.True(gate.TryFreeze(out var frozen));
        try
        {
            var error = Record.Exception(() => Submit(dispatcher, staged, Call("same", "value")));
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, dispatcher.PendingCount);
            Assert.False(dispatcher.HasUnpublishedCalls);
            Assert.Equal(0, Volatile.Read(ref effects));
        }
        finally { frozen.Dispose(); }

        Submit(dispatcher, staged, Call("same", "value"));
        dispatcher.FlushCurrentTurn();
        await dispatcher.Completion.WaitAsync(TestTimeout);
        Assert.Equal(1, Volatile.Read(ref effects));
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Frozen_observer_admission_refuses_before_pending_or_callback_effects()
    {
        var gate = new UpdateWorkAdmissionGate();
        var calls = 0;
        using var dispatcher = Queue(gate, (_, _, _) => Task.FromResult(Ok()));
        Assert.True(gate.TryFreeze(out var frozen));
        try
        {
            var error = Record.Exception(() => dispatcher.Observe((_, _) => Interlocked.Increment(ref calls), "fixture"));
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(0, dispatcher.PendingCount);
            Assert.Equal(0, calls);
        }
        finally { frozen.Dispose(); }
        dispatcher.Observe((_, _) => Interlocked.Increment(ref calls), "fixture");
        await dispatcher.Completion.WaitAsync(TestTimeout);
        Assert.Equal(1, calls);
        await AssertEventuallyIdle(gate);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task Cancellation_or_disposal_does_not_release_a_dependency_that_has_not_unwound(string control)
    {
        var gate = new UpdateWorkAdmissionGate();
        var entered = Signal();
        var release = Signal();
        using var dispatcher = Queue(gate, async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task; // Deliberately ignores cancellation.
            return Ok();
        });
        dispatcher.Enqueue(Call("held", "value"), new());
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            AssertBusy(gate);
            if (control == "cancel") dispatcher.Cancel(JsonSerializer.SerializeToElement(new[] { "held" }));
            else if (control == "stop") dispatcher.Stop();
            else dispatcher.Dispose();
            Assert.False(dispatcher.Completion.IsCompleted);
            AssertBusy(gate);
        }
        finally
        {
            release.TrySetResult(true);
            await dispatcher.Completion.WaitAsync(TestTimeout);
        }
        Assert.Equal(0, dispatcher.PendingCount);
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Published_pending_state_does_not_release_a_still_active_response_worker()
    {
        var gate = new UpdateWorkAdmissionGate();
        var entered = Signal();
        var release = Signal();
        using var dispatcher = Queue(gate, (_, _, _) => Task.FromResult(Ok()), async (build, onSent, _) =>
        {
            Assert.NotNull(build());
            onSent();
            entered.TrySetResult(true);
            await release.Task;
        });
        dispatcher.Enqueue(Call("respond", "value"), new());
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            Assert.Equal(0, dispatcher.PendingCount);
            Assert.False(dispatcher.Completion.IsCompleted);
            AssertBusy(gate);
        }
        finally
        {
            release.TrySetResult(true);
            await dispatcher.Completion.WaitAsync(TestTimeout);
        }
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Stopped_observer_remains_owned_until_synchronous_callback_returns()
    {
        var gate = new UpdateWorkAdmissionGate();
        var entered = Signal();
        var release = Signal();
        using var dispatcher = Queue(gate, (_, _, _) => Task.FromResult(Ok()));
        dispatcher.Observe((_, _) =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
        }, "held observer");
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            dispatcher.Stop();
            AssertBusy(gate);
            Assert.False(dispatcher.Completion.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            await dispatcher.Completion.WaitAsync(TestTimeout);
        }
        Assert.Equal(0, dispatcher.PendingCount);
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Old_connection_worker_remains_owned_when_replacement_connection_finishes()
    {
        var gate = new UpdateWorkAdmissionGate();
        var entered = Signal();
        var release = Signal();
        using var old = Queue(gate, async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return Ok();
        });
        using var replacement = Queue(gate, (_, _, _) => Task.FromResult(Ok()));
        old.Enqueue(Call("old", "value"), new());
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            old.Stop();
            replacement.Enqueue(Call("new", "value"), new());
            await replacement.Completion.WaitAsync(TestTimeout);
            Assert.False(old.Completion.IsCompleted);
            AssertBusy(gate);
        }
        finally
        {
            release.TrySetResult(true);
            await old.Completion.WaitAsync(TestTimeout);
        }
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Staged_calls_are_owned_before_flush_and_manual_flush_does_not_wait_for_fallback_timer()
    {
        var gate = new UpdateWorkAdmissionGate();
        var effects = 0;
        using var dispatcher = Queue(gate, (_, _, _) =>
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(Ok());
        });
        dispatcher.StageCurrentTurn(Call("first", "one"), new());
        dispatcher.StageCurrentTurn(Call("second", "two"), new());
        Assert.Equal(2, dispatcher.PendingCount);
        Assert.Equal(0, Volatile.Read(ref effects));
        AssertBusy(gate);
        dispatcher.FlushCurrentTurn();
        await dispatcher.Completion.WaitAsync(TestTimeout);
        Assert.Equal(2, effects);
        // The old fallback delay cannot cause effects once its batch is no
        // longer current, so it must not extend this owner's active lifetime.
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Duplicate_empty_null_observer_and_stopped_admission_leave_no_owned_work()
    {
        var gate = new UpdateWorkAdmissionGate();
        var effects = 0;
        using var dispatcher = Queue(gate, (_, _, _) =>
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(Ok());
        });
        dispatcher.Enqueue(Call("only", "value"), new());
        await dispatcher.Completion.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(gate);
        dispatcher.Enqueue(Call("only", "value"), new());
        dispatcher.StageCurrentTurn(JsonSerializer.SerializeToElement(new { functionCalls = Array.Empty<object>() }), new());
        dispatcher.Observe(null, "ignored");
        Assert.Equal(1, effects);
        Assert.Equal(0, dispatcher.PendingCount);
        AssertIdle(gate);

        dispatcher.Stop();
        Assert.True(gate.TryFreeze(out var frozen));
        try
        {
            dispatcher.Enqueue(Call("stopped", "value"), new());
            dispatcher.StageCurrentTurn(Call("stopped-staged", "value"), new());
            dispatcher.Observe((_, _) => throw new Xunit.Sdk.XunitException("Stopped observer executed."), "ignored");
            dispatcher.Cancel(JsonSerializer.SerializeToElement(new[] { "only" }));
            dispatcher.FlushCurrentTurn();
            Assert.Equal(0, dispatcher.PendingCount);
        }
        finally { frozen.Dispose(); }
        AssertIdle(gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unscheduled_registration_failure_releases_only_the_new_work_lease(bool staged)
    {
        var gate = new UpdateWorkAdmissionGate();
        var effects = 0;
        using var dispatcher = Queue(gate, (_, _, _) =>
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(Ok());
        });
        var malformed = JsonSerializer.SerializeToElement(new
        {
            functionCalls = new object[]
            {
                new { id = "recoverable", name = "test_action", args = new { value = "value" } },
                new { id = "bad", name = 123, args = new { value = "bad" } },
            },
        });
        Assert.NotNull(Record.Exception(() => Submit(dispatcher, staged, malformed)));
        // Preserve the legacy malformed-frame registration behavior; this
        // updater batch does not rewrite ID deduplication or tool parsing.
        Assert.Equal(1, dispatcher.PendingCount);
        Assert.Equal(0, effects);
        AssertIdle(gate);
    }

    [Fact]
    public async Task Dispatcher_parent_overlaps_a_detached_child_that_outlives_response_completion()
    {
        var gate = new UpdateWorkAdmissionGate();
        IDisposable child = null;
        using var dispatcher = Queue(gate, (_, _, _) =>
        {
            AssertBusy(gate);
            Assert.True(gate.TryAcquire(out child));
            return Task.FromResult(Ok());
        });
        try
        {
            dispatcher.Enqueue(Call("handoff", "value"), new());
            await dispatcher.Completion.WaitAsync(TestTimeout);
            Assert.NotNull(child);
            AssertBusy(gate);
        }
        finally { child?.Dispose(); }
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Fifty_local_dispatch_and_observer_cycles_have_no_early_release_or_accumulated_owner()
    {
        var gate = new UpdateWorkAdmissionGate();
        var effects = 0;
        var observations = 0;
        using var dispatcher = Queue(gate, (_, _, _) =>
        {
            AssertBusy(gate);
            Interlocked.Increment(ref effects);
            return Task.FromResult(Ok());
        });
        for (var index = 0; index < 50; index++)
        {
            dispatcher.Observe((_, _) =>
            {
                AssertBusy(gate);
                Interlocked.Increment(ref observations);
            }, "synthetic transcript");
            Submit(dispatcher, index % 2 == 0, Call($"cycle-{index}", $"value-{index}"));
            dispatcher.FlushCurrentTurn();
            await dispatcher.Completion.WaitAsync(TestTimeout);
            Assert.Equal(0, dispatcher.PendingCount);
            await AssertEventuallyIdle(gate);
        }
        Assert.Equal(50, effects);
        Assert.Equal(50, observations);
    }

    [Fact]
    public void Conversation_dispatcher_factory_uses_its_constructor_supplied_gate_without_starting_a_session()
    {
        var gate = new UpdateWorkAdmissionGate();
        var serviceType = typeof(GeminiLiveConversationService);
        var constructor = serviceType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.GetParameters().Length == 6
                && candidate.GetParameters()[5].ParameterType == typeof(UpdateWorkAdmissionGate));
        using var service = constructor is null
            ? new GeminiLiveConversationService(null, null, null)
            : (GeminiLiveConversationService)constructor.Invoke(new object[] { null, null, null, null, null, gate });
        var diagnosticsType = serviceType.GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
        var diagnostics = Activator.CreateInstance(diagnosticsType, "update-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
        var factory = serviceType.GetMethod("GetToolDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var dispatcher = (LiveToolCallDispatcher)factory.Invoke(service, new object[] { diagnostics, CancellationToken.None })!;
        Assert.True(gate.TryFreeze(out var frozen));
        try
        {
            Assert.IsType<InvalidOperationException>(Record.Exception(() => dispatcher.Enqueue(Call("production-wiring", "value"), new())));
            Assert.Equal(0, dispatcher.PendingCount);
        }
        finally { frozen.Dispose(); }
    }

    private static LiveToolCallDispatcher Queue(UpdateWorkAdmissionGate gate,
        Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> execute,
        Func<Func<object>, Action, CancellationToken, Task> respond = null)
    {
        respond ??= (build, onSent, token) =>
        {
            token.ThrowIfCancellationRequested();
            _ = build();
            onSent();
            return Task.CompletedTask;
        };
        var constructor = typeof(LiveToolCallDispatcher).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.GetParameters().Length == 4
                && candidate.GetParameters()[2].ParameterType == typeof(Func<Func<object>, Action, CancellationToken, Task>)
                && candidate.GetParameters()[3].ParameterType == typeof(UpdateWorkAdmissionGate));
        // Demonstrate current missing admission on the real legacy constructor
        // before the additive constructor exists; no fake production hook.
        return constructor is null
            ? new(CancellationToken.None, execute, respond)
            : (LiveToolCallDispatcher)constructor.Invoke(new object[] { CancellationToken.None, execute, respond, gate });
    }

    private static void Submit(LiveToolCallDispatcher dispatcher, bool staged, JsonElement calls)
    {
        if (staged) dispatcher.StageCurrentTurn(calls, new());
        else dispatcher.Enqueue(calls, new());
    }
    private static JsonElement Call(string id, string value) => JsonSerializer.SerializeToElement(new
    {
        functionCalls = new[] { new { id, name = "test_action", args = new { value } } },
    });
    private static Dictionary<string, object> Ok() => new() { ["ok"] = true };
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var froze = gate.TryFreeze(out var frozen);
        frozen?.Dispose();
        Assert.False(froze);
    }
    private static void AssertIdle(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
    }
    private static async Task AssertEventuallyIdle(UpdateWorkAdmissionGate gate)
    {
        // Awaiting the original worker is not a join of its independently
        // registered release continuation. Preserve runtime task identity;
        // tolerate only that safe, already-completed accounting tail here.
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (gate.TryFreeze(out var frozen))
            {
                frozen.Dispose();
                return;
            }
            await Task.Delay(5);
        }
        AssertIdle(gate);
    }
}
