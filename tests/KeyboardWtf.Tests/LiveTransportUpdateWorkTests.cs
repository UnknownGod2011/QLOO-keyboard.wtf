using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Original worker lifetime accounting only. Reflection invokes a factory
// seam on a null-dependency service, never Start/Stop, actual transport,
// microphone, playback, provider, application singleton or a profile.
// This does NOT establish complete logical/root/native update readiness.
public sealed class LiveTransportUpdateWorkTests
{
    private const string HelperName = "StartUpdateOwnedTransportWorker";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Held_original_task_is_returned_exactly_and_factory_is_admitted_before_effects()
    {
        await using var fixture = new Fixture();
        var original = fixture.HeldTask();
        var effects = 0;
        var returned = fixture.Start(() =>
        {
            AssertBusy(fixture.Gate);
            Interlocked.Increment(ref effects);
            return original.Task;
        });
        Assert.Same(original.Task, returned);
        Assert.Equal(1, effects);
        Assert.False(returned.IsCompleted);
        AssertBusy(fixture.Gate);
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_timed_out_caller_waiter_does_not_release_the_actual_original(bool timeout)
    {
        await using var fixture = new Fixture();
        var original = fixture.HeldTask();
        var returned = fixture.Start(() => original.Task);
        Assert.Same(original.Task, returned);
        if (timeout)
            await Assert.ThrowsAsync<TimeoutException>(
                () => returned.WaitAsync(TimeSpan.FromMilliseconds(25)));
        else
        {
            using var cancellation = new CancellationTokenSource();
            var waiter = returned.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        Assert.False(original.Task.IsCompleted);
        AssertBusy(fixture.Gate);
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task Original_terminal_result_identity_and_exception_are_preserved(string terminal)
    {
        await using var fixture = new Fixture();
        var original = fixture.HeldTask();
        var returned = fixture.Start(() => original.Task);
        Assert.Same(original.Task, returned);
        AssertBusy(fixture.Gate);
        var failure = new InvalidOperationException("Synthetic original worker failure");
        if (terminal == "fault") original.TrySetException(failure);
        else if (terminal == "cancel") original.TrySetCanceled(new CancellationToken(canceled: true));
        else original.TrySetResult(true);

        if (terminal == "fault")
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => returned));
        else if (terminal == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returned);
        else await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Original_TaskRun_cancelled_before_delegate_entry_still_releases_admission()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var delegateEntries = 0;
        Task original = null;
        var returned = fixture.Start(() =>
        {
            AssertBusy(fixture.Gate);
            original = Task.Run(() => Interlocked.Increment(ref delegateEntries), cancellation.Token);
            return original;
        });
        Assert.Same(original, returned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returned.WaitAsync(TestTimeout));
        Assert.Equal(0, Volatile.Read(ref delegateEntries));
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Synchronous_factory_failure_releases_admission_and_preserves_exact_exception()
    {
        await using var fixture = new Fixture();
        var failure = new InvalidOperationException("Synthetic factory failure");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => { _ = fixture.Start(() =>
        {
            AssertBusy(fixture.Gate);
            throw failure;
        }); }));
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Null_original_task_is_truthfully_refused_and_releases_admission()
    {
        await using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => { _ = fixture.Start(() =>
        {
            AssertBusy(fixture.Gate);
            return null;
        }); });
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Frozen_admission_does_not_invoke_factory_or_publish_its_effect_then_thaw_allows_work()
    {
        await using var fixture = new Fixture();
        var effects = 0;
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => { _ = fixture.Start(() =>
            {
                Interlocked.Increment(ref effects);
                return Task.CompletedTask;
            }); });
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, Volatile.Read(ref effects));
        }
        finally { frozen.Dispose(); }

        var original = fixture.HeldTask();
        var returned = fixture.Start(() =>
        {
            AssertBusy(fixture.Gate);
            Interlocked.Increment(ref effects);
            return original.Task;
        });
        Assert.Same(original.Task, returned);
        Assert.Equal(1, Volatile.Read(ref effects));
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("_receiveTask")]
    [InlineData("_paidActivationTask")]
    [InlineData("_paidSoftRolloverTask")]
    [InlineData("_paidReconnectTask")]
    public async Task Replacing_a_visible_worker_field_cannot_release_its_old_original_lifetime(string fieldName)
    {
        await using var fixture = new Fixture();
        var field = typeof(GeminiLiveConversationService)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Equal(typeof(Task), field.FieldType);
        var original = fixture.HeldTask();
        var returned = fixture.Start(() => original.Task);
        Assert.Same(original.Task, returned);
        field.SetValue(fixture.Service, returned);
        var replacement = Task.CompletedTask;
        field.SetValue(fixture.Service, replacement); // Mirrors reset/replacement, not actual completion.
        Assert.Same(replacement, field.GetValue(fixture.Service));
        Assert.False(original.Task.IsCompleted);
        AssertBusy(fixture.Gate);
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Overlapping_old_and_new_original_workers_own_independent_lifetimes()
    {
        await using var fixture = new Fixture();
        var first = fixture.HeldTask();
        var second = fixture.HeldTask();
        var firstReturned = fixture.Start(() => first.Task);
        var secondReturned = fixture.Start(() => second.Task);
        Assert.Same(first.Task, firstReturned);
        Assert.Same(second.Task, secondReturned);
        AssertBusy(fixture.Gate);
        first.TrySetResult(true);
        await firstReturned.WaitAsync(TestTimeout);
        Assert.False(second.Task.IsCompleted);
        AssertBusy(fixture.Gate);
        second.TrySetResult(true);
        await secondReturned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Null_gate_invokes_factory_directly_and_returns_the_exact_original_without_ownership()
    {
        await using var fixture = new Fixture(useGate: false);
        var original = fixture.HeldTask();
        var factoryCalls = 0;
        var returned = fixture.Start(() =>
        {
            AssertIdle(fixture.Gate);
            Interlocked.Increment(ref factoryCalls);
            return original.Task;
        });
        Assert.Same(original.Task, returned);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
        AssertIdle(fixture.Gate);
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Null_gate_preserves_direct_synchronous_factory_failure()
    {
        await using var fixture = new Fixture(useGate: false);
        var failure = new InvalidOperationException("Synthetic direct factory failure");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => { _ = fixture.Start(() => throw failure); }));
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Fifty_local_original_worker_cycles_have_no_early_release_or_accumulated_owner()
    {
        await using var fixture = new Fixture();
        var factoryCalls = 0;
        for (var index = 0; index < 50; index++)
        {
            var original = fixture.HeldTask();
            var returned = fixture.Start(() =>
            {
                AssertBusy(fixture.Gate);
                Interlocked.Increment(ref factoryCalls);
                return original.Task;
            });
            Assert.Same(original.Task, returned);
            AssertBusy(fixture.Gate);
            original.TrySetResult(true);
            await returned.WaitAsync(TestTimeout);
            await AssertEventuallyIdle(fixture.Gate);
        }
        Assert.Equal(50, Volatile.Read(ref factoryCalls));
    }

    [Theory]
    [InlineData("ConnectAndSetupLiveAsync")]
    [InlineData("EnsurePaidLiveActivatedAsync")]
    [InlineData("ScheduleProviderSoftRollover")]
    [InlineData("ScheduleProviderReconnect")]
    public void Four_original_worker_factory_callers_use_the_exact_shared_accounting_helper(string callerName)
    {
        var helper = RequiredHelper();
        var caller = typeof(GeminiLiveConversationService)
            .GetMethod(callerName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(caller);
        if (caller.GetCustomAttribute<AsyncStateMachineAttribute>() is { } asyncState)
        {
            caller = asyncState.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(caller);
        }
        var calls = CalledMethods(caller);
        Assert.Single(calls, call => call.Module == helper.Module && call.MetadataToken == helper.MetadataToken);
    }

    private static MethodInfo RequiredHelper()
    {
        var helper = typeof(GeminiLiveConversationService).GetMethod(HelperName,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null, types: [typeof(Func<Task>)], modifiers: null);
        Assert.True(helper is not null, $"The original-worker accounting seam {HelperName}(Func<Task>) is missing.");
        Assert.Equal(typeof(Task), helper.ReturnType);
        return helper;
    }

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
        // Awaiting the original task does not contractually join its release
        // continuation. Allow that terminal accounting tail, never early release.
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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MethodInfo _helper = RequiredHelper(); // Assert before any original worker exists.
        private readonly List<TaskCompletionSource<bool>> _held = [];
        private readonly List<Task> _originals = [];
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal GeminiLiveConversationService Service { get; }

        internal Fixture(bool useGate = true) =>
            Service = new GeminiLiveConversationService(null, null, null, null, null, useGate ? Gate : null);

        internal TaskCompletionSource<bool> HeldTask()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add(signal);
            return signal;
        }

        internal Task Start(Func<Task> factory)
        {
            Func<Task> originalFactory = () =>
            {
                var task = factory();
                if (task is not null) _originals.Add(task);
                return task;
            };
            try { return Assert.IsAssignableFrom<Task>(_helper.Invoke(Service, [originalFactory])); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var signal in _held) signal.TrySetResult(true);
            var completion = Task.WhenAll(_originals);
            try { await completion.WaitAsync(TestTimeout); }
            catch (Exception) when (completion.IsCompleted) { /* Original terminal failure is separately asserted. */ }
            // An unfinished timeout is not swallowed and does not authorize
            // disposing the service beneath its original synthetic workers.
            Service.Dispose();
        }
    }

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null))
        .GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    private static IReadOnlyList<MethodBase> CalledMethods(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var calls = new List<MethodBase>();
        for (var offset = 0; offset < il.Length;)
        {
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(OpCodesByValue.TryGetValue(value, out var code), "Unknown IL opcode in worker wiring check.");
            if (code.OperandType == OperandType.InlineMethod)
            {
                calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments()));
            }
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
                    or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok
                    or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(il, offset)),
                _ => throw new InvalidOperationException("Unsupported IL operand in worker wiring check."),
            };
        }
        return calls;
    }
}
