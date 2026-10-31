using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Finite ORIGINAL Piper worker accounting only. No TtsService.Instance,
// constructor, COM, process, voice/model/profile, WaveOut or actual speech.
// SAPI async playback and positive native/process exit remain separate gates.
public sealed class PiperUpdateWorkTests
{
    private const string HelperName = "StartUpdateOwnedPiperWorker";
    private const BindingFlags AllMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Exact_original_is_admitted_before_factory_and_held_until_actual_completion()
    {
        await using var f = new Fixture();
        var original = f.HeldTask();
        var entries = 0;
        var returned = f.Start(() =>
        {
            AssertBusy(f.Gate);
            entries++;
            return original.Task;
        });
        Assert.Same(original.Task, returned);
        Assert.Equal(1, entries);
        AssertBusy(f.Gate);
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(f.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_timed_out_waiter_does_not_retire_original(bool timeout)
    {
        await using var f = new Fixture();
        var original = f.HeldTask();
        var returned = f.Start(() => original.Task);
        Assert.Same(original.Task, returned);
        if (timeout)
            await Assert.ThrowsAsync<TimeoutException>(() => returned.WaitAsync(TimeSpan.FromMilliseconds(25)));
        else
        {
            using var cancel = new CancellationTokenSource();
            var waiter = returned.WaitAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        Assert.False(original.Task.IsCompleted);
        AssertBusy(f.Gate);
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(f.Gate);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task Original_outcome_and_exception_identity_are_preserved(string outcome)
    {
        await using var f = new Fixture();
        var original = f.HeldTask();
        var returned = f.Start(() => original.Task);
        Assert.Same(original.Task, returned);
        var failure = new IOException("Synthetic worker failure");
        if (outcome == "fault") original.TrySetException(failure);
        else if (outcome == "cancel") original.TrySetCanceled(new CancellationToken(canceled: true));
        else original.TrySetResult(true);
        if (outcome == "fault")
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => returned));
        else if (outcome == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returned);
        else await returned.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(f.Gate);
    }

    [Fact]
    public async Task Disposed_CTS_token_fault_before_worker_body_still_retires_original()
    {
        await using var f = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        cancelled.Dispose();
        var bodyEntries = 0;
        Task original = null;
        var returned = f.Start(() =>
        {
            AssertBusy(f.Gate);
            original = Task.Run(() =>
            {
                _ = cancelled.Token; // Mirrors the existing outer scheduled lambda.
                Interlocked.Increment(ref bodyEntries);
            });
            return original;
        });
        Assert.Same(original, returned);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => returned.WaitAsync(TestTimeout));
        Assert.Equal(0, bodyEntries);
        await AssertEventuallyIdle(f.Gate);
    }

    [Fact]
    public async Task Pre_cancelled_original_scheduler_task_releases_without_delegate_entry()
    {
        await using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var bodyEntries = 0;
        Task original = null;
        var returned = f.Start(() =>
        {
            original = Task.Run(() => Interlocked.Increment(ref bodyEntries), cancel.Token);
            return original;
        });
        Assert.Same(original, returned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returned.WaitAsync(TestTimeout));
        Assert.Equal(0, bodyEntries);
        await AssertEventuallyIdle(f.Gate);
    }

    [Fact]
    public async Task Synchronous_factory_failure_preserves_exception_and_releases_only_attempt()
    {
        await using var f = new Fixture();
        var old = f.HeldTask();
        _ = f.Start(() => old.Task);
        var failure = new IOException("Synthetic before-scheduling failure");
        Assert.Same(failure, Assert.Throws<IOException>(() => { _ = f.Start(() => throw failure); }));
        AssertBusy(f.Gate);
        old.TrySetResult(true);
        await old.Task.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(f.Gate);
    }

    [Fact]
    public async Task Null_original_is_truthfully_rejected_and_releases_attempt()
    {
        await using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => { _ = f.Start(() => null); });
        await AssertEventuallyIdle(f.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_or_committed_gate_never_invokes_purge_or_publication_factory(bool committed)
    {
        await using var f = new Fixture();
        Assert.True(f.Gate.TryFreeze(out var freeze));
        if (committed) freeze.Commit();
        var effects = 0;
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => { _ = f.Start(() =>
            {
                effects++;
                return Task.CompletedTask;
            }); });
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, effects);
        }
        finally { freeze.Dispose(); }
        if (!committed)
        {
            Assert.Same(Task.CompletedTask, f.Start(() => Task.CompletedTask));
            await AssertEventuallyIdle(f.Gate);
        }
    }

    [Fact]
    public async Task Playback_generation_replacement_cannot_retire_cancelled_old_worker()
    {
        await using var f = new Fixture();
        var playback = new TtsPlaybackLease();
        var firstGeneration = playback.Begin();
        var first = f.HeldTask();
        var second = f.HeldTask();
        _ = f.Start(() => first.Task);
        Assert.Equal(firstGeneration, playback.CancelCurrent());
        var secondGeneration = playback.Begin();
        _ = f.Start(() => second.Task);
        Assert.False(playback.Complete(firstGeneration));
        Assert.True(playback.IsCurrent(secondGeneration));
        AssertBusy(f.Gate);
        second.TrySetResult(true);
        await second.Task.WaitAsync(TestTimeout);
        Assert.True(playback.Complete(secondGeneration));
        Assert.False(first.Task.IsCompleted);
        AssertBusy(f.Gate);
        first.TrySetResult(true);
        await first.Task.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(f.Gate);
    }

    [Fact]
    public async Task Sixteen_originals_remain_independently_owned_after_visible_replacement()
    {
        await using var f = new Fixture();
        var originals = Enumerable.Range(0, 16).Select(_ => f.HeldTask()).ToArray();
        foreach (var original in originals) Assert.Same(original.Task, f.Start(() => original.Task));
        for (var index = 0; index < originals.Length - 1; index++)
        {
            originals[index].TrySetResult(true);
            await originals[index].Task.WaitAsync(TestTimeout);
            AssertBusy(f.Gate);
        }
        originals[^1].TrySetResult(true);
        await originals[^1].Task.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(f.Gate);
    }

    [Fact]
    public async Task Null_gate_preserves_direct_original_factory_identity_and_context()
    {
        await using var f = new Fixture(useGate: false);
        var original = f.HeldTask();
        var callerThread = Environment.CurrentManagedThreadId;
        var returned = f.Start(() =>
        {
            Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
            AssertIdle(f.Gate);
            return original.Task;
        });
        Assert.Same(original.Task, returned);
        AssertIdle(f.Gate);
        original.TrySetResult(true);
        await returned.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Null_gate_preserves_legacy_null_and_exact_synchronous_failure()
    {
        await using var f = new Fixture(useGate: false);
        Assert.Null(f.Start(() => null));
        var failure = new IOException("Synthetic direct factory failure");
        Assert.Same(failure, Assert.Throws<IOException>(() => { _ = f.Start(() => throw failure); }));
        AssertIdle(f.Gate);
    }

    [Fact]
    public async Task Fifty_cycles_do_not_release_held_work_or_accumulate_owners()
    {
        await using var f = new Fixture();
        for (var cycle = 0; cycle < 50; cycle++)
        {
            var original = f.HeldTask();
            Assert.Same(original.Task, f.Start(() => original.Task));
            AssertBusy(f.Gate);
            original.TrySetResult(true);
            await original.Task.WaitAsync(TestTimeout);
            await AssertEventuallyIdle(f.Gate);
        }
    }

    [Fact]
    public void Helper_has_direct_null_gate_branch_and_preserves_original_scheduler()
    {
        var helper = RequiredHelper();
        var calls = Instructions(helper).Where(IsCall).ToArray();
        var invokes = calls.Where(i => i.Method?.DeclaringType == typeof(Func<Task>) && i.Method.Name == "Invoke").ToArray();
        Assert.Equal(2, invokes.Length);
        var admission = Assert.Single(calls, i => i.Method?.DeclaringType == typeof(UpdateWorkAdmissionGate) && i.Method.Name == "TryAcquire");
        Assert.True(invokes[0].Offset < admission.Offset); // Legacy direct branch.
        Assert.True(admission.Offset < invokes[1].Offset); // Admitted non-null branch.
        Assert.Single(calls, i => i.Method?.DeclaringType == typeof(Task) && i.Method.Name == "ContinueWith");
        Assert.Single(calls, i => i.Method?.DeclaringType == typeof(CancellationToken) && i.Method.Name == "get_None");
        Assert.Single(calls, i => i.Method?.DeclaringType == typeof(TaskScheduler) && i.Method.Name == "get_Default");
        Assert.DoesNotContain(calls, i => i.Method?.Name is "Run" or "WaitAsync");
        Assert.Null(helper.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public void Piper_acceptance_admits_factory_before_purge_CTS_and_current_generation_publication()
    {
        var helper = RequiredHelper();
        var speak = RequiredSpeechMethod();
        Assert.True(speak.GetParameters()[2].IsOptional);
        Assert.Null(speak.GetParameters()[2].DefaultValue);
        var piper = typeof(TtsService).GetMethod("PiperSpeak", AllMethods, null,
            [typeof(string), typeof(string), typeof(UpdateWorkAdmissionGate)], null);
        Assert.NotNull(piper);
        var outer = Instructions(piper);
        var choose = Assert.Single(outer, i => IsCall(i) && i.Method?.Name == "ChoosePiperVoice");
        var admitted = Assert.Single(outer, i => IsCall(i) && SameMethod(i.Method, helper));
        Assert.True(choose.Offset < admitted.Offset);
        Assert.DoesNotContain(outer, i => IsCall(i) && i.Method?.Name is "PiperStop" or "Run" or "Begin");
        Assert.DoesNotContain(outer, i => i.Field?.Name == "_piperCts");
        var factory = Assert.Single(outer, i => i.Code == OpCodes.Ldftn
            && i.Method?.Name.Contains("<PiperSpeak>", StringComparison.Ordinal) == true).Method as MethodInfo;
        Assert.NotNull(factory);
        var body = Instructions(factory);
        var purge = Assert.Single(body, i => IsCall(i) && i.Method?.Name == "PiperStop");
        var cts = Assert.Single(body, i => i.Code == OpCodes.Newobj && i.Method?.DeclaringType == typeof(CancellationTokenSource));
        var publish = Assert.Single(body, i => i.Code == OpCodes.Stfld && i.Field?.DeclaringType == typeof(TtsService) && i.Field.Name == "_piperCts");
        var generation = Assert.Single(body, i => IsCall(i) && i.Method?.DeclaringType == typeof(TtsPlaybackLease) && i.Method.Name == "Begin");
        var run = Assert.Single(body, i => IsCall(i) && i.Method?.DeclaringType == typeof(Task) && i.Method.Name == "Run");
        Assert.True(purge.Offset < cts.Offset && cts.Offset < publish.Offset && publish.Offset < generation.Offset && generation.Offset < run.Offset);
    }

    [Theory]
    [InlineData("lifecycle")]
    [InlineData("plan")]
    [InlineData("readback")]
    [InlineData("timer")]
    [InlineData("settings-test")]
    public void Five_existing_production_speech_sites_pass_the_same_app_gate_without_eager_COM(string site)
    {
        _ = RequiredHelper(); // Missing-seam RED before any resource or application.
        var speech = RequiredSpeechMethod();
        MethodInfo caller;
        if (site == "timer")
        {
            var candidates = typeof(JarvisTimerService).GetNestedTypes(BindingFlags.NonPublic)
                .SelectMany(type => type.GetMethods(AllMethods | BindingFlags.DeclaredOnly));
            caller = Assert.Single(candidates, method => Instructions(method).Any(i => IsCall(i) && SameMethod(i.Method, speech)));
            var gateField = Assert.Single(Instructions(caller), i => i.Code == OpCodes.Ldfld && i.Field?.FieldType == typeof(UpdateWorkAdmissionGate)).Field;
            var constructor = typeof(JarvisTimerService).GetConstructor(AllMethods, null,
                [typeof(NotificationService), typeof(JarvisActionHistoryService), typeof(UpdateWorkAdmissionGate)], null);
            Assert.NotNull(constructor);
            var constructorBody = Instructions(constructor);
            var store = Assert.Single(constructorBody, i => i.Code == OpCodes.Stfld && SameField(i.Field, gateField));
            var storeIndex = constructorBody.ToList().FindIndex(i => i.Offset == store.Offset);
            Assert.True(storeIndex > 0);
            Assert.Equal(OpCodes.Ldarg_3, constructorBody[storeIndex - 1].Code);
        }
        else
        {
            var (type, name) = site switch
            {
                "lifecycle" => (typeof(KeyboardWtfApp), "DeliverLifecycleAnnouncementAsync"),
                "plan" => (typeof(CommandRegistry), "ExecutePlanAsync"),
                "readback" => (typeof(CommandRegistry), "ReadBack"),
                "settings-test" => (typeof(WebSettingsService), "TestTts"),
                _ => throw new InvalidOperationException("Unknown synthetic wiring site."),
            };
            caller = type.GetMethod(name, AllMethods);
            Assert.NotNull(caller);
            caller = AsyncBody(caller);
            var getter = typeof(KeyboardWtfApp).GetProperty("UpdateWorkAdmission", AllMethods)?.GetMethod;
            Assert.NotNull(getter);
            var calls = Instructions(caller).Where(IsCall).ToArray();
            var speechCall = Assert.Single(calls, i => SameMethod(i.Method, speech));
            Assert.Contains(calls, i => SameMethod(i.Method, getter) && i.Offset < speechCall.Offset);
        }
        Assert.Single(Instructions(caller), i => IsCall(i) && SameMethod(i.Method, speech));
        Assert.DoesNotContain(Instructions(caller), i => i.Code == OpCodes.Newobj && i.Method?.DeclaringType == typeof(UpdateWorkAdmissionGate));
    }

    [Fact]
    public void Synthesis_playback_SAPI_voice_selection_stop_and_initialization_source_remain_exact()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "Services", "TtsService.cs"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "Services", "TtsService.cs"));
        var regions = new[]
        {
            ("    private void TryInitializeSapi()", "    public void Speak("),
            ("    private void SapiSpeak(", "    private void PiperSpeak("),
            ("    private static string ChoosePiperVoice(", ""),
        };
        var protectedSource = new StringBuilder();
        foreach (var (startMarker, endMarker) in regions)
        {
            var start = source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.True(start >= 0);
            var end = endMarker.Length == 0 ? source.Length : source.IndexOf(endMarker, start, StringComparison.Ordinal);
            Assert.True(end > start);
            protectedSource.Append(source.AsSpan(start, end - start));
        }
        var normalized = Regex.Replace(protectedSource.ToString(), @"\s+", "");
        Assert.Equal("98BE13294AA726DB91798A5601F366F2B228CCB9C68ECFA847F901327D6BCCDA",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    private static MethodInfo RequiredHelper()
    {
        var method = typeof(TtsService).GetMethod(HelperName, AllMethods, null,
            [typeof(UpdateWorkAdmissionGate), typeof(Func<Task>)], null);
        Assert.True(method is not null, "The passive original Piper worker accounting seam is missing.");
        Assert.True(method.IsStatic);
        Assert.Equal(typeof(Task), method.ReturnType);
        return method;
    }

    private static MethodInfo RequiredSpeechMethod()
    {
        var method = typeof(TtsService).GetMethod("Speak", AllMethods, null,
            [typeof(string), typeof(string), typeof(UpdateWorkAdmissionGate)], null);
        Assert.NotNull(method);
        return method;
    }

    private static MethodInfo AsyncBody(MethodInfo method) => method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } state
        ? state.StateMachineType.GetMethod("MoveNext", AllMethods)! : method;

    private static bool SameMethod(MethodBase a, MethodBase b) => a is not null && b is not null && a.Module == b.Module && a.MetadataToken == b.MetadataToken;
    private static bool SameField(FieldInfo a, FieldInfo b) => a is not null && b is not null && a.Module == b.Module && a.MetadataToken == b.MetadataToken;
    private static bool IsCall(Instruction i) => i.Code == OpCodes.Call || i.Code == OpCodes.Callvirt || i.Code == OpCodes.Newobj;

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
            if (gate.TryFreeze(out var freeze)) { freeze.Dispose(); return; }
            await Task.Delay(5);
        }
        AssertIdle(gate);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MethodInfo _helper = RequiredHelper(); // Before workers/resources.
        private readonly bool _useGate;
        private readonly List<TaskCompletionSource<bool>> _held = [];
        private readonly List<Task> _originals = [];
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal Fixture(bool useGate = true) => _useGate = useGate;
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
            try { return (Task)_helper.Invoke(null, [_useGate ? Gate : null, originalFactory]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var signal in _held) signal.TrySetResult(true);
            var joined = Task.WhenAll(_originals);
            try { await joined.WaitAsync(TestTimeout); }
            catch (Exception) when (joined.IsCompleted) { /* Original terminal errors are asserted separately. */ }
            // A still-running timeout is never swallowed. There is no native service to dispose.
        }
    }

    private sealed record Instruction(int Offset, OpCode Code, MethodBase Method, FieldInfo Field);
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)).GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    private static IReadOnlyList<Instruction> Instructions(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var result = new List<Instruction>();
        for (var offset = 0; offset < il.Length;)
        {
            var start = offset;
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(OpCodesByValue.TryGetValue(value, out var code));
            var declaringArguments = method.DeclaringType?.GetGenericArguments();
            var methodArguments = method is MethodInfo info ? info.GetGenericArguments() : null;
            var called = code.OperandType == OperandType.InlineMethod
                ? method.Module.ResolveMethod(BitConverter.ToInt32(il, offset), declaringArguments, methodArguments) : null;
            var field = code.OperandType == OperandType.InlineField
                ? method.Module.ResolveField(BitConverter.ToInt32(il, offset), declaringArguments, methodArguments) : null;
            result.Add(new(start, code, called, field));
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
                _ => throw new InvalidOperationException("Unsupported IL operand in Piper wiring proof."),
            };
        }
        return result;
    }
}
