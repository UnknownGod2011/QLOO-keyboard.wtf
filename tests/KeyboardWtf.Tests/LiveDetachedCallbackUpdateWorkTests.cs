using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

// Passive original callback/Task accounting only. No microphone, playback
// device, websocket, provider, profile, app singleton or installer is started.
// This cannot certify a logical session or native audio shutdown boundary.
[Collection("Live event integration")]
public sealed class LiveDetachedCallbackUpdateWorkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_or_committed_microphone_callback_has_no_PCM_meter_or_pre_roll_effect(bool committed)
    {
        using var fixture = new Fixture();
        var oldMeter = KeyboardWtfState.InputLevelDb;
        KeyboardWtfState.InputLevelDb = -71;
        Assert.True(fixture.Gate.TryFreeze(out var freeze));
        try
        {
            if (committed) freeze.Commit();
            fixture.Emit([0xff, 0x7f, 0, 0]);
            Assert.Equal(-71, KeyboardWtfState.InputLevelDb);
            Assert.Empty(fixture.PreRoll);
            Assert.Equal(1, fixture.Context.Completed);
            Assert.Empty(fixture.Context.Unexpected);
        }
        finally
        {
            KeyboardWtfState.InputLevelDb = oldMeter;
            freeze.Dispose();
        }
        if (!committed) AssertIdle(fixture.Gate);
        else AssertBusy(fixture.Gate);
    }

    [Fact]
    public void Thawed_callback_preserves_original_meter_full_PCM_copy_and_pre_roll()
    {
        using var fixture = new Fixture();
        var oldMeter = KeyboardWtfState.InputLevelDb;
        var bytes = new byte[] { 0x00, 0x40, 0x00, 0x00 };
        Assert.True(fixture.Gate.TryFreeze(out var freeze));
        freeze.Dispose();
        try
        {
            fixture.Emit(bytes);
            var captured = Assert.Single(fixture.PreRoll);
            Assert.Equal(bytes, captured);
            Assert.NotSame(bytes, captured);
            Assert.InRange(KeyboardWtfState.InputLevelDb, -6.021, -6.020);
            Assert.Equal(1, fixture.Context.Completed);
            Assert.Empty(fixture.Context.Unexpected);
            AssertIdle(fixture.Gate);
        }
        finally { KeyboardWtfState.InputLevelDb = oldMeter; }
    }

    [Fact]
    public void Null_gate_preserves_legacy_microphone_callback_even_when_an_unrelated_gate_is_frozen()
    {
        using var fixture = new Fixture(useGate: false);
        var oldMeter = KeyboardWtfState.InputLevelDb;
        Assert.True(fixture.Gate.TryFreeze(out var freeze));
        try
        {
            fixture.Emit([0, 0, 0, 0]);
            Assert.Equal(-96, KeyboardWtfState.InputLevelDb);
            Assert.Single(fixture.PreRoll);
            Assert.Equal(1, fixture.Context.Completed);
            Assert.Empty(fixture.Context.Unexpected);
        }
        finally
        {
            KeyboardWtfState.InputLevelDb = oldMeter;
            freeze.Dispose();
        }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public void Fifty_original_microphone_callbacks_do_not_accumulate_owners_or_change_PCM_delivery()
    {
        using var fixture = new Fixture();
        var oldMeter = KeyboardWtfState.InputLevelDb;
        try
        {
            for (var index = 0; index < 50; index++)
            {
                var bytes = new byte[] { (byte)index, 0, 0, 0 };
                fixture.Emit(bytes);
                Assert.Equal(bytes, fixture.PreRoll.Last());
                Assert.Equal(index + 1, fixture.PreRoll.Count);
                AssertIdle(fixture.Gate);
            }
            Assert.Equal(50, fixture.Context.Completed);
            Assert.Empty(fixture.Context.Unexpected);
        }
        finally { KeyboardWtfState.InputLevelDb = oldMeter; }
    }

    [Theory]
    [InlineData("ScheduleReconnectUiIfNeeded")]
    [InlineData("HandleServerMessage")]
    [InlineData("ScheduleAutoEnd")]
    public void Each_detached_original_Task_is_wired_to_the_existing_exact_task_owner(string callerName)
    {
        var helper = RequiredMethod("StartUpdateOwnedTransportWorker");
        var calls = Instructions(RequiredMethod(callerName))
            .Where(instruction => instruction.Method is not null).Select(instruction => instruction.Method);
        Assert.Single(calls, call => call.Module == helper.Module && call.MetadataToken == helper.MetadataToken);
    }

    [Fact]
    public void Async_void_microphone_lease_is_hoisted_through_original_send_and_released_by_terminal_state_machine()
    {
        var callback = RequiredMethod("OnMicrophoneData");
        var state = Assert.IsType<AsyncStateMachineAttribute>(callback.GetCustomAttribute<AsyncStateMachineAttribute>());
        var moveNext = state.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(moveNext);
        Assert.Contains(state.StateMachineType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IDisposable) && field.Name.Contains("microphoneUpdateWork", StringComparison.Ordinal));
        var instructions = Instructions(moveNext);
        var admission = Assert.Single(instructions, instruction => instruction.Method?.DeclaringType == typeof(UpdateWorkAdmissionGate)
            && instruction.Method.Name == "TryAcquire");
        var bufferRead = Assert.Single(instructions, instruction => instruction.Method?.DeclaringType == typeof(WaveInEventArgs)
            && instruction.Method.Name == "get_Buffer");
        Assert.True(admission.Offset < bufferRead.Offset);
        var send = Assert.Single(instructions, instruction => instruction.Method?.DeclaringType == typeof(GeminiLiveConversationService)
            && instruction.Method.Name == "SendAudioChunkAsync");
        var disposal = Assert.Single(instructions, instruction => instruction.Method?.DeclaringType == typeof(IDisposable)
            && instruction.Method.Name == "Dispose");
        var completed = Assert.Single(instructions, instruction => instruction.Method?.DeclaringType == typeof(AsyncVoidMethodBuilder)
            && instruction.Method.Name == "SetResult");
        Assert.True(send.Offset < disposal.Offset);
        Assert.True(disposal.Offset < completed.Offset);
    }

    [Fact]
    public async Task Actual_playback_drain_Task_remains_owned_after_cancellation_until_original_queued_continuation_unwinds()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var context = new CallbackContext();
        var buffer = GeminiLiveConversationService.CreatePlaybackBuffer();
        buffer.AddSamples(new byte[4800], 0, 4800);
        Set(fixture.Service, "_playbackBuffer", buffer);
        Task original = null;
        Task returned = null;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            returned = fixture.Start(() => original = fixture.DrainPlayback(buffer, cancellation.Token));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            Assert.Same(original, returned);
            Assert.False(original.IsCompleted);
            AssertBusy(fixture.Gate);
            cancellation.Cancel();
            await context.Posted.Task.WaitAsync(Timeout);
            Assert.False(original.IsCompleted);
            AssertBusy(fixture.Gate);
        }
        finally
        {
            cancellation.Cancel();
            Set(fixture.Service, "_playbackBuffer", null);
            context.Drain();
            if (original is not null) await original.WaitAsync(Timeout);
        }
        Assert.Empty(context.Unexpected);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Detached_old_and_new_tasks_keep_separate_owners_after_visible_session_is_replaced()
    {
        using var fixture = new Fixture();
        using var oldSession = new CancellationTokenSource();
        using var newSession = new CancellationTokenSource();
        var first = Signal();
        var second = Signal();
        Set(fixture.Service, "_sessionCts", oldSession);
        var oldTask = fixture.Start(() => first.Task);
        oldSession.Cancel();
        Set(fixture.Service, "_sessionCts", newSession);
        var newTask = fixture.Start(() => second.Task);
        Assert.Same(first.Task, oldTask);
        Assert.Same(second.Task, newTask);
        try
        {
            AssertBusy(fixture.Gate);
            first.TrySetResult(true);
            await oldTask.WaitAsync(Timeout);
            Assert.False(newTask.IsCompleted);
            AssertBusy(fixture.Gate);
        }
        finally
        {
            first.TrySetResult(true);
            second.TrySetResult(true);
            await Task.WhenAll(oldTask, newTask).WaitAsync(Timeout);
            Set(fixture.Service, "_sessionCts", null);
        }
        await AssertEventuallyIdle(fixture.Gate);
    }

    private sealed class Fixture : IDisposable
    {
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal GeminiLiveConversationService Service { get; }
        internal CallbackContext Context { get; } = new();
        internal Queue<byte[]> PreRoll => (Queue<byte[]>)RequiredField("_paidActivationAudio").GetValue(Service);

        internal Fixture(bool useGate = true) =>
            Service = new GeminiLiveConversationService(null, null, null, null, null, useGate ? Gate : null);

        internal void Emit(byte[] bytes)
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(Context);
                Invoke(RequiredMethod("OnMicrophoneData"), Service, [null, new WaveInEventArgs(bytes, bytes.Length)]);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        internal Task Start(Func<Task> factory) => (Task)Invoke(RequiredMethod("StartUpdateOwnedTransportWorker"), Service, [factory]);
        internal Task DrainPlayback(BufferedWaveProvider buffer, CancellationToken cancellation) =>
            (Task)Invoke(RequiredMethod("ShowListeningAfterPlaybackDrainsAsync"), Service, [buffer, 0L, cancellation]);

        public void Dispose()
        {
            Set(Service, "_sessionCts", null);
            Set(Service, "_playbackBuffer", null);
            Service.Dispose();
        }
    }

    private sealed class CallbackContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> _pending = new();
        internal TaskCompletionSource<bool> Posted { get; } = Signal();
        internal List<Exception> Unexpected { get; } = [];
        internal int Completed;
        public override void OperationCompleted() => Interlocked.Increment(ref Completed);
        public override void Post(SendOrPostCallback callback, object state)
        {
            _pending.Enqueue((callback, state));
            Posted.TrySetResult(true);
        }
        internal void Drain()
        {
            while (_pending.TryDequeue(out var pending))
            {
                try { pending.Callback(pending.State); }
                catch (Exception error) { Unexpected.Add(error); }
            }
        }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MethodInfo RequiredMethod(string name) => typeof(GeminiLiveConversationService)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"Missing original method {name}.");
    private static FieldInfo RequiredField(string name) => typeof(GeminiLiveConversationService)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"Missing original field {name}.");
    private static void Set(object target, string name, object value) => RequiredField(name).SetValue(target, value);
    private static object Invoke(MethodInfo method, object target, object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
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
        var deadline = DateTime.UtcNow + Timeout;
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

    private sealed record Instruction(int Offset, MethodBase Method);
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)).GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());
    private static IReadOnlyList<Instruction> Instructions(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var instructions = new List<Instruction>();
        for (var offset = 0; offset < il.Length;)
        {
            var position = offset;
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(OpCodesByValue.TryGetValue(value, out var code));
            MethodBase called = null;
            if (code.OperandType == OperandType.InlineMethod)
                called = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments());
            instructions.Add(new(position, called));
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
                _ => throw new InvalidOperationException("Unsupported IL operand in original callback wiring check."),
            };
        }
        return instructions;
    }
}
