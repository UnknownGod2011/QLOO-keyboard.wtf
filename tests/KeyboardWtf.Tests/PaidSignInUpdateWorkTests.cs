using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Passive original-task and compiled/source boundary proof ONLY. The fixtures
// do not call the account constructor or RunSignIn, create an HTTP listener,
// launch a browser, open a profile/session store, or dispatch network/provider
// work. Frozen/shared/terminal public controls are invoked only after proving
// the compiled new-attempt boundary. This is not OAuth/Windows acceptance.
public sealed class PaidSignInUpdateWorkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Required_private_account_seams_resolve_before_synthetic_work()
    {
        var binding = Binding.Require();
        Assert.Equal(typeof(Task<PaidAccountSnapshot>), binding.Start.ReturnType);
        Assert.Equal(typeof(IDisposable), binding.Acquire.ReturnType);
        Assert.Empty(binding.Acquire.GetParameters());
        Assert.True(binding.Start.IsPrivate);
        Assert.True(binding.Acquire.IsPrivate);
    }

    [Fact]
    public void Compiled_new_attempt_admits_before_all_existing_state_and_cts_effects()
    {
        var binding = Binding.Require();
        AssertNewAttemptBoundary(binding);
        var instructions = Instructions(binding.Start);
        var admission = Assert.Single(instructions, instruction => IsCall(instruction, binding.Acquire));
        var invokes = instructions.Where(instruction => instruction.Member is MethodBase method
            && method.Name == "Invoke" && method.DeclaringType == typeof(Func<Task<PaidAccountSnapshot>>)).ToArray();
        Assert.Equal(2, invokes.Length);
        // The early null-gate branch remains the original direct factory call.
        // Only the non-null-gate invocation is admitted; behavioral controls
        // below prove that frozen admission never reaches either factory.
        Assert.Single(invokes, invoke => invoke.Offset < admission.Offset);
        Assert.Single(invokes, invoke => invoke.Offset > admission.Offset);
        Assert.DoesNotContain(instructions, instruction => instruction.Member is MethodBase method
            && method.DeclaringType == typeof(Task) && (method.Name is "Run" or "WaitAsync"));
        Assert.Single(instructions, instruction => instruction.Member is MethodBase method
            && method.Name == "ContinueWith" && typeof(Task).IsAssignableFrom(method.DeclaringType));
    }

    [Fact]
    public void Existing_sign_in_body_and_shared_wait_are_byte_equivalent_to_accepted_source()
    {
        _ = Binding.Require();
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Services", "PaidStaging", "PaidAccountService.cs"));
        var begin = source.IndexOf("    private async Task<PaidAccountSnapshot> RunSignInAsync(", StringComparison.Ordinal);
        Assert.True(begin >= 0);
        var end = source.IndexOf("    public async Task<PaidAccountSnapshot> RefreshAccountAsync(", begin, StringComparison.Ordinal);
        Assert.True(end > begin);
        var protectedSource = source[begin..end].ReplaceLineEndings("\n");
        Assert.Equal("17C6986B7E36DC381EC7FBB29DDB526DCD838BA40D190E494B5B2361E4B0A4B1",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(protectedSource))));
        // This protects existing callback validation, tokens, listener timeout,
        // state catches, semaphore release and caller WaitAsync. It intentionally
        // does not claim that the OAuth exchange is exercised by these tests.
    }

    [Fact]
    public async Task Helper_invokes_factory_synchronously_and_returns_the_exact_original_task()
    {
        await using var fixture = new Fixture();
        var finish = fixture.Hold();
        var callingThread = Environment.CurrentManagedThreadId;
        var factoryThread = 0;
        var returned = fixture.Start(() =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            AssertBusy(fixture.Gate);
            return finish.Task;
        });
        Assert.Equal(callingThread, factoryThread);
        Assert.Same(finish.Task, returned);
        Assert.False(returned.IsCompleted);
        var result = Snapshot();
        finish.TrySetResult(result);
        Assert.Same(result, await returned.WaitAsync(TestTimeout));
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Cancelled_caller_waiter_cannot_release_the_held_original_sign_in()
    {
        await using var fixture = new Fixture();
        var finish = fixture.Hold();
        var original = fixture.Start(() => finish.Task);
        using var cancellation = new CancellationTokenSource();
        var waiter = original.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        finish.TrySetResult(Snapshot());
        await original.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Held_fault_preserves_original_exception_and_releases_only_after_completion()
    {
        await using var fixture = new Fixture();
        var finish = fixture.Hold();
        var error = new IOException("Synthetic sign-in failure");
        var original = fixture.Start(() => finish.Task);
        AssertBusy(fixture.Gate);
        finish.TrySetException(error);
        Assert.Same(error, await Record.ExceptionAsync(() => original));
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Held_original_cancellation_releases_only_when_original_becomes_terminal()
    {
        await using var fixture = new Fixture();
        var finish = fixture.Hold();
        using var cancellation = new CancellationTokenSource();
        var original = fixture.Start(() => finish.Task);
        cancellation.Cancel();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        finish.TrySetCanceled(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Already_cancelled_original_task_retains_identity_and_cannot_leak_owner()
    {
        await using var fixture = new Fixture();
        var original = Task.FromCanceled<PaidAccountSnapshot>(new CancellationToken(canceled: true));
        var returned = fixture.Start(() => original);
        Assert.Same(original, returned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returned);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Synchronous_factory_failure_preserves_exception_and_releases_temporary_owner()
    {
        await using var fixture = new Fixture();
        var error = new IOException("Synthetic sign-in factory failure");
        Assert.Same(error, Record.Exception(() => { _ = fixture.Start(() => throw error); }));
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Null_original_task_fails_truthfully_and_releases_only_the_attempt_owner()
    {
        await using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => { _ = fixture.Start(() => null); });
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_or_committed_helper_refuses_before_factory_effects(bool committed)
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        var factories = 0;
        try
        {
            if (committed) frozen.Commit();
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = fixture.Start(() => { factories++; return Task.FromResult(Snapshot()); });
            });
            Assert.Equal(0, factories);
        }
        finally { frozen.Dispose(); }
        if (committed) Assert.False(fixture.Gate.TryAcquire(out _));
        else AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_new_sign_in_and_retry_preserve_existing_cts_state_and_last_error(bool retry)
    {
        var binding = Binding.Require();
        AssertNewAttemptBoundary(binding); // Prevent real RunSignIn before any public invocation.
        await using var fixture = new Fixture();
        var previous = fixture.SeedCancellation();
        fixture.Set("_signInState", "idle");
        fixture.Set("_lastError", "Synthetic preserved error");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        fixture.Set("_signInExpiresAt", expiresAt);
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            Func<Task> attempt = () => retry ? fixture.Account.RetrySignInAsync() : fixture.Account.SignInAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(attempt);
            Assert.Same(previous, fixture.Read("_activeSignInCancellation"));
            Assert.Equal("idle", fixture.Account.SignInState);
            Assert.Equal(expiresAt, fixture.Account.SignInExpiresAt);
            Assert.Equal("Synthetic preserved error", fixture.Account.LastError);
            Assert.Null(fixture.Read("_activeSignInTask"));
            Assert.False(previous.IsCancellationRequested);
            using var registration = previous.Token.Register(() => { }); // It was not disposed.
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("failed")]
    public async Task Terminal_shared_control_remains_unchanged_and_does_not_admit_a_new_attempt(string state)
    {
        var binding = Binding.Require();
        AssertNewAttemptBoundary(binding);
        await using var fixture = new Fixture();
        var previous = fixture.SeedCancellation();
        fixture.Set("_signInState", state);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Account.SignInAsync());
        Assert.Contains("Try again", error.Message, StringComparison.Ordinal);
        Assert.Equal(state, fixture.Account.SignInState);
        Assert.Same(previous, fixture.Read("_activeSignInCancellation"));
        Assert.Null(fixture.Read("_activeSignInTask"));
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Shared_waiters_do_not_create_a_second_original_and_one_cancelled_waiter_does_not_retire_it()
    {
        var binding = Binding.Require();
        AssertNewAttemptBoundary(binding);
        await using var fixture = new Fixture();
        var finish = fixture.Hold();
        var factories = 0;
        var original = fixture.Start(() => { factories++; return finish.Task; });
        fixture.Set("_activeSignInTask", original);
        fixture.Set("_signInState", "waiting_for_google");
        var ownCancellation = fixture.SeedCancellation();
        using var callerCancellation = new CancellationTokenSource();
        var first = fixture.Track(fixture.Account.SignInAsync(callerCancellation.Token));
        var second = fixture.Track(fixture.Account.SignInAsync());
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, factories);
        Assert.Same(original, fixture.Read("_activeSignInTask"));
        Assert.True(fixture.Account.SignInPending);
        Assert.False(ownCancellation.IsCancellationRequested);
        Assert.False(second.IsCompleted);
        AssertBusy(fixture.Gate);
        var result = Snapshot();
        finish.TrySetResult(result);
        Assert.Same(result, await second.WaitAsync(TestTimeout));
        await original.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Explicit_cancel_shared_control_remains_reachable_and_original_owned_until_cleanup()
    {
        var binding = Binding.Require();
        AssertNewAttemptBoundary(binding);
        await using var fixture = new Fixture();
        var finish = fixture.Hold();
        var original = fixture.Start(() => finish.Task);
        fixture.Set("_activeSignInTask", original);
        var ownCancellation = fixture.SeedCancellation();
        var cancel = fixture.Track(fixture.Account.CancelSignInAsync());
        Assert.True(ownCancellation.IsCancellationRequested);
        Assert.Equal("cancelling", fixture.Account.SignInState);
        Assert.False(cancel.IsCompleted);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        finish.TrySetCanceled(ownCancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
        Assert.True(await cancel.WaitAsync(TestTimeout));
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Replacing_current_field_cannot_retire_an_old_original_task()
    {
        await using var fixture = new Fixture();
        var firstFinish = fixture.Hold();
        var secondFinish = fixture.Hold();
        var first = fixture.Start(() => firstFinish.Task);
        fixture.Set("_activeSignInTask", first);
        var second = fixture.Start(() => secondFinish.Task);
        fixture.Set("_activeSignInTask", second);
        fixture.Set("_signInState", "completed"); // UI state is not worker completion.
        secondFinish.TrySetResult(Snapshot());
        await second.WaitAsync(TestTimeout);
        Assert.False(first.IsCompleted);
        AssertBusy(fixture.Gate);
        firstFinish.TrySetResult(Snapshot());
        await first.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Failed_second_factory_does_not_release_the_first_original_owner()
    {
        await using var fixture = new Fixture();
        var finish = fixture.Hold();
        var original = fixture.Start(() => finish.Task);
        Assert.Throws<IOException>(() => { _ = fixture.Start(() => throw new IOException("Synthetic second admission failure")); });
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        finish.TrySetResult(Snapshot());
        await original.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Sixteen_independent_factory_owners_remain_busy_until_every_original_completes()
    {
        await using var fixture = new Fixture();
        var originals = new List<Task<PaidAccountSnapshot>>();
        var finishes = Enumerable.Range(0, 16).Select(_ => fixture.Hold()).ToArray();
        foreach (var finish in finishes) originals.Add(fixture.Start(() => finish.Task));
        AssertBusy(fixture.Gate);
        for (var index = 0; index < finishes.Length - 1; index++)
        {
            finishes[index].TrySetResult(Snapshot());
            await originals[index].WaitAsync(TestTimeout);
            AssertBusy(fixture.Gate);
        }
        finishes[^1].TrySetResult(Snapshot());
        await originals[^1].WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public async Task Null_gate_preserves_direct_factory_task_null_and_synchronous_exception_behavior()
    {
        await using var fixture = new Fixture(useGate: false);
        Assert.Null(fixture.Acquire());
        var original = fixture.Hold();
        Assert.Same(original.Task, fixture.Start(() => original.Task));
        Assert.Null(fixture.Start(() => null));
        var error = new IOException("Synthetic legacy factory error");
        Assert.Same(error, Record.Exception(() => { _ = fixture.Start(() => throw error); }));
        AssertIdle(fixture.Gate);
        original.TrySetResult(Snapshot());
        await original.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Authentication_acquire_uses_same_gate_and_null_legacy_scope_is_direct()
    {
        await using var fixture = new Fixture();
        var lease = fixture.Acquire();
        Assert.NotNull(lease);
        AssertBusy(fixture.Gate);
        lease.Dispose();
        lease.Dispose();
        AssertIdle(fixture.Gate);
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try { Assert.Throws<InvalidOperationException>(() => { _ = fixture.Acquire(); }); }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Fifty_local_cycles_preserve_exact_tasks_and_never_retire_for_cancelled_waiters()
    {
        await using var fixture = new Fixture();
        for (var index = 0; index < 50; index++)
        {
            var finish = fixture.Hold();
            var original = fixture.Start(() => finish.Task);
            Assert.Same(finish.Task, original);
            using var cancellation = new CancellationTokenSource();
            var waiter = original.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            Assert.False(original.IsCompleted);
            AssertBusy(fixture.Gate);
            finish.TrySetResult(Snapshot());
            await original.WaitAsync(TestTimeout);
            await AssertEventuallyIdle(fixture.Gate);
        }
    }

    private static PaidAccountSnapshot Snapshot() => new("synthetic-user", "person@example.test", "Synthetic User", "active",
        "none", 123, 0, 0, null, [], [], []);

    private static void AssertNewAttemptBoundary(Binding binding)
    {
        var startSignIn = typeof(PaidAccountService).GetMethod("StartSignInAsync", PrivateInstance)!;
        var outer = Instructions(startSignIn);
        Assert.Single(outer, instruction => IsCall(instruction, binding.Start));
        Assert.DoesNotContain(outer, instruction => instruction.Member is MethodBase method
            && method.Name == "RunSignInAsync" && method.DeclaringType == typeof(PaidAccountService));
        Assert.DoesNotContain(outer, instruction => (instruction.Code == OpCodes.Stfld || instruction.Code == OpCodes.Ldflda)
            && instruction.Member is FieldInfo field && field.DeclaringType == typeof(PaidAccountService)
            && (field.Name is "_activeSignInCancellation" or "_signInState" or "_signInExpiresAt" or "_lastError"));
        Assert.DoesNotContain(outer, instruction => instruction.Member is ConstructorInfo constructor
            && constructor.DeclaringType == typeof(CancellationTokenSource));
        Assert.DoesNotContain(outer, instruction => instruction.Member is MethodBase method
            && method.DeclaringType == typeof(CancellationTokenSource) && method.Name == "Dispose");
        var factory = Assert.Single(outer, instruction => instruction.Code == OpCodes.Ldftn
            && instruction.Member is MethodInfo method && Instructions(method).Any(candidate => candidate.Member is MethodBase called
                && called.Name == "RunSignInAsync" && called.DeclaringType == typeof(PaidAccountService)));
        var inner = Instructions((MethodInfo)factory.Member);
        var run = Assert.Single(inner, instruction => instruction.Member is MethodBase method
            && method.Name == "RunSignInAsync" && method.DeclaringType == typeof(PaidAccountService));
        foreach (var fieldName in new[] { "_activeSignInCancellation", "_signInState", "_lastError" })
        {
            var publish = Assert.Single(inner, instruction => instruction.Code == OpCodes.Stfld
                && instruction.Member is FieldInfo field && field.DeclaringType == typeof(PaidAccountService) && field.Name == fieldName);
            Assert.True(publish.Offset < run.Offset, "Original initialization must precede RunSignIn: " + fieldName);
        }
        // Nullable null assignment is an address + initobj, not stfld. Prove
        // that the exact expires-at field is reset to its exact nullable type
        // inside the admitted factory before RunSignIn; do not omit its effect.
        var expiresAddress = Assert.Single(inner, instruction => instruction.Code == OpCodes.Ldflda
            && instruction.Member is FieldInfo field && field.DeclaringType == typeof(PaidAccountService)
            && field.Name == "_signInExpiresAt");
        var expiresField = (FieldInfo)expiresAddress.Member;
        Assert.Equal(typeof(DateTimeOffset?), expiresField.FieldType);
        var addressIndex = inner.ToList().IndexOf(expiresAddress);
        Assert.True(addressIndex + 1 < inner.Count);
        var expiresReset = inner[addressIndex + 1];
        Assert.Equal(OpCodes.Initobj, expiresReset.Code);
        Assert.Equal(expiresField.FieldType, expiresReset.Member);
        Assert.True(expiresAddress.Offset < expiresReset.Offset && expiresReset.Offset < run.Offset,
            "Original nullable initialization must precede RunSignIn: _signInExpiresAt");
        var cancelSource = Assert.Single(inner, instruction => instruction.Code == OpCodes.Newobj
            && instruction.Member is ConstructorInfo constructor && constructor.DeclaringType == typeof(CancellationTokenSource));
        var disposePrevious = Assert.Single(inner, instruction => instruction.Member is MethodBase method
            && method.DeclaringType == typeof(CancellationTokenSource) && method.Name == "Dispose");
        Assert.True(disposePrevious.Offset < cancelSource.Offset);
        Assert.True(cancelSource.Offset < run.Offset);
        var wait = Assert.Single(outer, instruction => instruction.Member is MethodBase method
            && method.Name == "WaitForSharedSignInAsync" && method.DeclaringType == typeof(PaidAccountService));
        var admitted = Assert.Single(outer, instruction => IsCall(instruction, binding.Start));
        Assert.True(admitted.Offset < wait.Offset);
    }

    private static bool IsCall(Instruction instruction, MethodInfo method) =>
        instruction.Member is MethodBase called && called.Module == method.Module && called.MetadataToken == method.MetadataToken;

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
        // Original Completion doesn't join its accounting continuation. Only
        // this already-terminal inert tail is allowed a bounded eventual check.
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (gate.TryFreeze(out var frozen)) { frozen.Dispose(); return; }
            await Task.Delay(5);
        }
        AssertIdle(gate);
    }

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "src", "Services", "PaidStaging", "PaidAccountService.cs")))
                return current.FullName;
        throw new InvalidOperationException("The source checkout required for sign-in boundary proof was not found.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Binding _binding = Binding.Require(); // Before ANY fixture fields/resources/work.
        private readonly ConcurrentBag<TaskCompletionSource<PaidAccountSnapshot>> _held = new();
        private readonly ConcurrentBag<Task> _originals = new();
        private readonly List<CancellationTokenSource> _syntheticSources = [];
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal PaidAccountService Account { get; }

        internal Fixture(bool useGate = true)
        {
            // Deliberately bypass constructor/default profile/HTTP/timer. Only
            // fields read by frozen/shared/terminal controls are initialized.
            Account = (PaidAccountService)RuntimeHelpers.GetUninitializedObject(typeof(PaidAccountService));
            Set("_configuration", new PaidStagingConfiguration(true, false,
                new Uri("https://paid-api.example.test/"), new Uri("https://paid.example.test/")));
            Set("_signInSync", new object());
            Set("_updateWorkAdmission", useGate ? Gate : null);
            Set("_signInState", "idle");
            Set("_lastError", "");
        }

        internal void Set(string name, object value) => typeof(PaidAccountService).GetField(name, PrivateInstance)!.SetValue(Account, value);
        internal object Read(string name) => typeof(PaidAccountService).GetField(name, PrivateInstance)!.GetValue(Account);
        internal CancellationTokenSource SeedCancellation()
        {
            var source = new CancellationTokenSource();
            _syntheticSources.Add(source);
            Set("_activeSignInCancellation", source);
            return source;
        }

        internal TaskCompletionSource<PaidAccountSnapshot> Hold()
        {
            var finish = new TaskCompletionSource<PaidAccountSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add(finish);
            return finish;
        }

        internal IDisposable Acquire() => (IDisposable)Binding.Invoke(() => _binding.Acquire.Invoke(Account, null));
        internal Task<PaidAccountSnapshot> Start(Func<Task<PaidAccountSnapshot>> factory)
        {
            Func<Task<PaidAccountSnapshot>> originalFactory = () =>
            {
                var task = factory();
                if (task is not null) _originals.Add(task);
                return task;
            };
            return (Task<PaidAccountSnapshot>)Binding.Invoke(() => _binding.Start.Invoke(Account, [originalFactory]));
        }

        internal T Track<T>(T task) where T : Task { _originals.Add(task); return task; }

        public async ValueTask DisposeAsync()
        {
            foreach (var finish in _held) finish.TrySetResult(Snapshot());
            var completion = Task.WhenAll(_originals);
            try { await completion.WaitAsync(TestTimeout); }
            catch (Exception) when (completion.IsCompleted) { /* Original terminal failures are separately asserted. */ }
            // Do not call production Dispose on an unconstructed service. Its
            // only resources here are our synthetic CTSs, retired AFTER joins.
            foreach (var source in _syntheticSources) source.Dispose();
        }
    }

    private sealed class Binding
    {
        internal MethodInfo Start { get; }
        internal MethodInfo Acquire { get; }
        private Binding()
        {
            Start = typeof(PaidAccountService).GetMethod("StartUpdateOwnedSignInWork", PrivateInstance, null,
                [typeof(Func<Task<PaidAccountSnapshot>>)], null);
            Assert.True(Start is not null, "The exact original sign-in accounting seam is missing.");
            Acquire = typeof(PaidAccountService).GetMethod("AcquireAuthenticationUpdateWork", PrivateInstance, null,
                Type.EmptyTypes, null);
            Assert.True(Acquire is not null, "The account authentication acquisition seam is missing.");
            Assert.Equal(typeof(Task<PaidAccountSnapshot>), Start.ReturnType);
            Assert.Equal(typeof(IDisposable), Acquire.ReturnType);
        }
        internal static Binding Require() => new();
        internal static object Invoke(Func<object> action)
        {
            try { return action(); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed record Instruction(int Offset, OpCode Code, MemberInfo Member);
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
            MemberInfo member = null;
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField)
                member = method.Module.ResolveMember(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method is MethodInfo info ? info.GetGenericArguments() : Type.EmptyTypes);
            else if (code.OperandType == OperandType.InlineType)
                member = method.Module.ResolveType(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method is MethodInfo typeMethod ? typeMethod.GetGenericArguments() : Type.EmptyTypes);
            result.Add(new Instruction(start, code, member));
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
                _ => throw new InvalidOperationException("Unsupported IL operand in account admission proof."),
            };
        }
        return result;
    }
}
