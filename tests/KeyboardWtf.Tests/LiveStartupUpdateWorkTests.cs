using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Finite scope proof and compiled caller wiring only. These tests NEVER
// invoke real StartCoreAsync/ConnectAndSetupLiveAsync/StopAsync, create a
// socket/microphone/application, or access a profile. Logical-session and
// native lifetime proof is explicitly outside this finite startup batch.
public sealed class LiveStartupUpdateWorkTests
{
    private const string AcquireName = "AcquirePaidMaintenanceUpdateWork";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Accepted_start_acquires_after_eligibility_before_Stop_or_CTS_and_releases_after_final_flag_reset()
    {
        var instructions = Instructions(AsyncBody("StartCoreAsync"));
        var admission = Assert.Single(Calls(instructions), call => IsAcquire(call.Method));
        var warnings = Calls(instructions).Where(call => call.Method.DeclaringType == typeof(NotificationService)
            && call.Method.Name == "Warning").ToArray();
        Assert.NotEmpty(warnings);
        Assert.All(warnings, warning => Assert.True(warning.Offset < admission.Offset,
            "Existing eligibility failures precede accepted-start ownership."));
        var firstStop = Calls(instructions).First(call => call.Method.DeclaringType == typeof(GeminiLiveConversationService)
            && call.Method.Name == "StopAsync");
        var sessionConstruction = Calls(instructions).First(call => call.Method.DeclaringType == typeof(CancellationTokenSource)
            && call.Method.IsConstructor);
        Assert.True(admission.Offset < firstStop.Offset, "Admission must precede the accepted start's first Stop effect.");
        Assert.True(admission.Offset < sessionConstruction.Offset, "Admission must precede CTS creation/publication.");

        var flagReset = Assert.Single(Calls(instructions), call => call.Method.DeclaringType == typeof(Volatile)
            && call.Method.Name == "Write");
        Assert.Contains(instructions, instruction => instruction.Offset < flagReset.Offset
            && instruction.Offset > flagReset.Offset - 40
            && instruction.Member is FieldInfo { Name: "_startInProgress" });
        var release = Assert.Single(Calls(instructions), call => call.Method.DeclaringType == typeof(IDisposable)
            && call.Method.Name == "Dispose");
        Assert.True(release.Offset > flagReset.Offset, "The startup lease must survive the existing outer-finally flag reset.");
        Assert.DoesNotContain(Calls(instructions), call => call.Offset > release.Offset
            && call.Method.DeclaringType == typeof(GeminiLiveConversationService));
    }

    [Fact]
    public void Connect_acquires_as_its_first_user_call_before_backend_scope_socket_or_handshake()
    {
        var instructions = Instructions(AsyncBody("ConnectAndSetupLiveAsync"));
        var calls = Calls(instructions).ToArray();
        var admission = Assert.Single(calls, call => IsAcquire(call.Method));
        // Roslyn allocates this method's generated closure before its first
        // source statement. Ignore only a proven inert DisplayClass constructor,
        // not arbitrary framework/user calls or network/session effects.
        var firstUserCall = calls.First(call => !IsInertGeneratedClosureConstructor(call.Method));
        Assert.Equal(firstUserCall.Offset, admission.Offset);
        var backendScope = Assert.Single(calls, call => call.Method.DeclaringType == typeof(PaidAccountService)
            && call.Method.Name == "EnterAiOperation");
        Assert.True(admission.Offset < backendScope.Offset);
        var network = calls.Where(call => call.Method.Name is "ConnectLiveAsync" or "ConnectAsync"
            || (call.Method.DeclaringType == typeof(System.Net.WebSockets.ClientWebSocket) && call.Method.IsConstructor)).ToArray();
        Assert.NotEmpty(network);
        Assert.All(network, call => Assert.True(admission.Offset < call.Offset,
            "No socket creation/connection may precede finite connection admission."));
        // The backend-scope Dispose remains; the new finite scope must also
        // have real finally disposal rather than relying on visible task state.
        Assert.True(calls.Count(call => call.Method.DeclaringType == typeof(IDisposable)
            && call.Method.Name == "Dispose") >= 2);
    }

    [Fact]
    public async Task Synthetic_scope_remains_busy_through_held_body_and_final_cleanup_then_flag_reset()
    {
        await using var fixture = new Fixture();
        var body = fixture.Signal();
        var cleanupEntered = fixture.Signal();
        var cleanupRelease = fixture.Signal();
        var startFlag = typeof(GeminiLiveConversationService).GetField("_startInProgress", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(startFlag);
        startFlag.SetValue(fixture.Service, 1);
        var original = fixture.Scope(() =>
        {
            AssertBusy(fixture.Gate);
            return body.Task;
        }, async () =>
        {
            AssertBusy(fixture.Gate);
            cleanupEntered.TrySetResult(true);
            await cleanupRelease.Task;
            AssertBusy(fixture.Gate);
            startFlag.SetValue(fixture.Service, 0);
        });
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        body.TrySetResult(true);
        await cleanupEntered.Task.WaitAsync(TestTimeout);
        Assert.False(original.IsCompleted);
        Assert.Equal(1, startFlag.GetValue(fixture.Service));
        AssertBusy(fixture.Gate);
        cleanupRelease.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, startFlag.GetValue(fixture.Service));
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancel_or_timeout_cannot_release_a_held_original_scope(bool timeout)
    {
        await using var fixture = new Fixture();
        var body = fixture.Signal();
        var original = fixture.Scope(() => body.Task);
        if (timeout)
            await Assert.ThrowsAsync<TimeoutException>(() => original.WaitAsync(TimeSpan.FromMilliseconds(25)));
        else
        {
            using var caller = new CancellationTokenSource();
            var waiter = original.WaitAsync(caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        body.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task Original_scope_outcome_preserves_exception_and_releases_after_real_cleanup(string outcome)
    {
        await using var fixture = new Fixture();
        var body = fixture.Signal();
        var cleanupCount = 0;
        var failure = new InvalidOperationException("Synthetic startup failure");
        var original = fixture.Scope(() => body.Task, () =>
        {
            AssertBusy(fixture.Gate);
            Interlocked.Increment(ref cleanupCount);
            return Task.CompletedTask;
        });
        AssertBusy(fixture.Gate);
        if (outcome == "fault") body.TrySetException(failure);
        else if (outcome == "cancel") body.TrySetCanceled(new CancellationToken(canceled: true));
        else body.TrySetResult(true);
        if (outcome == "fault")
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => original));
        else if (outcome == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
        else await original.WaitAsync(TestTimeout);
        Assert.Equal(1, Volatile.Read(ref cleanupCount));
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Frozen_scope_refusal_happens_before_factory_or_cleanup_effects()
    {
        await using var fixture = new Fixture();
        var effects = 0;
        Assert.True(fixture.Gate.TryFreeze(out var freeze));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope(() =>
            {
                Interlocked.Increment(ref effects);
                return Task.CompletedTask;
            }, () =>
            {
                Interlocked.Increment(ref effects);
                return Task.CompletedTask;
            }));
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, Volatile.Read(ref effects));
        }
        finally { freeze.Dispose(); }
        await fixture.Scope(() => Task.CompletedTask).WaitAsync(TestTimeout);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Finite_scope_completion_does_not_release_an_independent_original_transport_worker()
    {
        await using var fixture = new Fixture();
        var body = fixture.Signal();
        var worker = fixture.Signal();
        var originalScope = fixture.Scope(() => body.Task);
        var originalWorker = fixture.TransportWorker(() => worker.Task);
        Assert.Same(worker.Task, originalWorker);
        AssertBusy(fixture.Gate);
        body.TrySetResult(true);
        await originalScope.WaitAsync(TestTimeout);
        Assert.False(originalWorker.IsCompleted);
        AssertBusy(fixture.Gate);
        worker.TrySetResult(true);
        await originalWorker.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Null_gate_returns_null_lease_and_keeps_synthetic_scope_execution_unchanged()
    {
        await using var fixture = new Fixture(useGate: false);
        Assert.Null(fixture.Acquire());
        var effects = 0;
        await fixture.Scope(() =>
        {
            AssertIdle(fixture.Gate);
            Interlocked.Increment(ref effects);
            return Task.CompletedTask;
        }, () =>
        {
            AssertIdle(fixture.Gate);
            Interlocked.Increment(ref effects);
            return Task.CompletedTask;
        }).WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref effects));
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Synchronous_factory_failure_still_runs_final_cleanup_before_scope_release()
    {
        await using var fixture = new Fixture();
        var failure = new InvalidOperationException("Synthetic synchronous startup failure");
        var cleanupCount = 0;
        var original = fixture.Scope(() => throw failure, () =>
        {
            AssertBusy(fixture.Gate);
            Interlocked.Increment(ref cleanupCount);
            return Task.CompletedTask;
        });
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => original));
        Assert.Equal(1, Volatile.Read(ref cleanupCount));
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Fifty_local_finite_scopes_release_without_early_cleanup_or_owner_accumulation()
    {
        await using var fixture = new Fixture();
        var cleanupCount = 0;
        for (var index = 0; index < 50; index++)
        {
            var body = fixture.Signal();
            var original = fixture.Scope(() =>
            {
                AssertBusy(fixture.Gate);
                return body.Task;
            }, () =>
            {
                AssertBusy(fixture.Gate);
                Interlocked.Increment(ref cleanupCount);
                return Task.CompletedTask;
            });
            AssertBusy(fixture.Gate);
            body.TrySetResult(true);
            await original.WaitAsync(TestTimeout);
            AssertIdle(fixture.Gate);
        }
        Assert.Equal(50, Volatile.Read(ref cleanupCount));
    }

    private static MethodInfo AsyncBody(string name)
    {
        var method = RequiredMethod(name);
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        Assert.NotNull(state);
        var moveNext = state.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(moveNext);
        return moveNext;
    }

    private static MethodInfo RequiredMethod(string name)
    {
        var method = typeof(GeminiLiveConversationService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method;
    }

    private static bool IsAcquire(MethodBase method) => method.DeclaringType == typeof(GeminiLiveConversationService)
        && method.Name == AcquireName;

    private static bool IsInertGeneratedClosureConstructor(MethodBase method)
    {
        var type = method.DeclaringType;
        if (!method.IsConstructor || type?.DeclaringType != typeof(GeminiLiveConversationService)
            || !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || !type.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
            return false;
        var body = Instructions(method);
        var calls = Calls(body).ToArray();
        // Verify allocation has no field mutation or user code: only loading
        // this, invoking Object's trivial base constructor, optional NOP, RET.
        return calls.Length == 1 && calls[0].Method.DeclaringType == typeof(object) && calls[0].Method.IsConstructor
            && body.All(instruction => instruction.Code == OpCodes.Ldarg_0 || instruction.Code == OpCodes.Call
                || instruction.Code == OpCodes.Nop || instruction.Code == OpCodes.Ret);
    }

    private static object Invoke(GeminiLiveConversationService service, MethodInfo method, object[] arguments)
    {
        try { return method.Invoke(service, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var frozen = gate.TryFreeze(out var freeze);
        freeze?.Dispose();
        Assert.False(frozen);
    }

    private static void AssertIdle(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var freeze));
        freeze.Dispose();
    }

    private static async Task AssertEventuallyIdle(UpdateWorkAdmissionGate gate)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (gate.TryFreeze(out var freeze))
            {
                freeze.Dispose();
                return;
            }
            await Task.Delay(5);
        }
        AssertIdle(gate);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MethodInfo _acquire = RequiredMethod(AcquireName);
        private readonly MethodInfo _transport = RequiredMethod("StartUpdateOwnedTransportWorker");
        private readonly List<TaskCompletionSource<bool>> _held = [];
        private readonly List<Task> _originals = [];
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal GeminiLiveConversationService Service { get; }

        internal Fixture(bool useGate = true) =>
            Service = new GeminiLiveConversationService(null, null, null, null, null, useGate ? Gate : null);

        internal IDisposable Acquire() => (IDisposable)Invoke(Service, _acquire, []);

        internal TaskCompletionSource<bool> Signal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add(signal);
            return signal;
        }

        internal Task Scope(Func<Task> body, Func<Task> finalCleanup = null)
        {
            var original = RunScopeAsync(body, finalCleanup);
            _originals.Add(original);
            return original;
        }

        private async Task RunScopeAsync(Func<Task> body, Func<Task> finalCleanup)
        {
            var lease = Acquire();
            try { await body(); }
            finally
            {
                try { if (finalCleanup is not null) await finalCleanup(); }
                finally { lease?.Dispose(); }
            }
        }

        internal Task TransportWorker(Func<Task> factory)
        {
            Func<Task> trackOriginal = () =>
            {
                var original = factory();
                _originals.Add(original);
                return original;
            };
            return Assert.IsAssignableFrom<Task>(Invoke(Service, _transport, [trackOriginal]));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var signal in _held) signal.TrySetResult(true);
            var completion = Task.WhenAll(_originals);
            try { await completion.WaitAsync(TestTimeout); }
            catch (Exception) when (completion.IsCompleted) { /* Original failure/cancellation is asserted by the case. */ }
            // An unfinished timeout remains a failure and cannot authorize
            // disposing the service beneath original synthetic work.
            Service.Dispose();
        }
    }

    private sealed record Instruction(int Offset, OpCode Code, MemberInfo Member);
    private sealed record Call(int Offset, MethodBase Method);

    private static IEnumerable<Call> Calls(IEnumerable<Instruction> instructions) =>
        instructions.Where(instruction => instruction.Member is MethodBase)
            .Select(instruction => new Call(instruction.Offset, (MethodBase)instruction.Member));

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null))
        .GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    private static IReadOnlyList<Instruction> Instructions(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var instructions = new List<Instruction>();
        for (var offset = 0; offset < il.Length;)
        {
            var start = offset;
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(OpCodesByValue.TryGetValue(value, out var code), "Unknown IL opcode in startup wiring check.");
            MemberInfo member = null;
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField)
                member = method.Module.ResolveMember(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method is MethodInfo info ? info.GetGenericArguments() : Type.EmptyTypes);
            instructions.Add(new Instruction(start, code, member));
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
                _ => throw new InvalidOperationException("Unsupported IL operand in startup wiring check."),
            };
        }
        return instructions;
    }
}
