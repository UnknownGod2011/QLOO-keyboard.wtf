using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Pure, UNWIRED dispatch receipt. Task.Run delegates simulate a queued callback;
// they do not prove WinForms/OS notification delivery, native drainage or host wiring.
// Every held original callback is released and joined before fixture cleanup.
public sealed class UpdateNotificationDispatchReceiptTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Missing_contract_is_required_before_work_and_exposes_only_dispatch_completion()
    {
        var binding = Binding.Require();
        Assert.Equal(new[] { typeof(Func<bool>) }, binding.Constructor.GetParameters().Select(item => item.ParameterType));
        Assert.Equal(typeof(Task<bool>), binding.Completion.PropertyType);
        Assert.False(binding.Completion.CanWrite);
        Assert.Equal(typeof(void), binding.Run.ReturnType);
        Assert.Equal(typeof(bool), binding.TrySuppress.ReturnType);
        Assert.Empty(binding.Run.GetParameters());
        Assert.Empty(binding.TrySuppress.GetParameters());
    }

    [Fact]
    public void Null_dispatch_is_rejected_before_any_callback()
    {
        var binding = Binding.Require();
        Assert.Throws<ArgumentNullException>(() => { _ = binding.Create(null); });
    }

    [Fact]
    public async Task Pending_receipt_has_one_incomplete_task_with_asynchronous_continuations()
    {
        await using var fixture = new Fixture();
        var calls = 0;
        var receipt = fixture.Create(() => { calls++; return true; });
        var completion = fixture.Completion(receipt);
        Assert.Same(completion, fixture.Completion(receipt));
        Assert.False(completion.IsCompleted);
        Assert.True((completion.CreationOptions & TaskCreationOptions.RunContinuationsAsynchronously)
            == TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(0, calls);
        Assert.True(fixture.Suppress(receipt));
        Assert.False(await completion.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Positive_dispatch_completes_true_only_once_and_cannot_be_retroactively_suppressed()
    {
        await using var fixture = new Fixture();
        var calls = 0;
        var receipt = fixture.Create(() => { calls++; return true; });
        fixture.Run(receipt);
        Assert.True(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        Assert.False(fixture.Suppress(receipt));
        fixture.Run(receipt);
        fixture.Run(receipt);
        Assert.Equal(1, calls);
        Assert.True(await fixture.Completion(receipt).WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Negative_dispatch_completes_false_without_being_replayed()
    {
        await using var fixture = new Fixture();
        var calls = 0;
        var receipt = fixture.Create(() => { calls++; return false; });
        fixture.Run(receipt);
        Assert.False(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        Assert.False(fixture.Suppress(receipt));
        fixture.Run(receipt);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Dispatch_exception_is_contained_and_completes_false_without_retry()
    {
        await using var fixture = new Fixture();
        var calls = 0;
        var receipt = fixture.Create(() => { calls++; throw new InvalidOperationException("Synthetic dispatch failure."); });
        var exception = Record.Exception(() => fixture.Run(receipt));
        Assert.Null(exception);
        Assert.False(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        Assert.False(fixture.Suppress(receipt));
        fixture.Run(receipt);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Pending_suppression_completes_false_and_every_late_Run_is_inert()
    {
        await using var fixture = new Fixture();
        var effects = 0;
        var receipt = fixture.Create(() => { effects++; throw new InvalidOperationException("A suppressed callback must never enter."); });
        var originalCompletion = fixture.Completion(receipt);
        Assert.True(fixture.Suppress(receipt));
        Assert.False(fixture.Suppress(receipt));
        Assert.False(await originalCompletion.WaitAsync(TestTimeout));
        for (var index = 0; index < 16; index++) fixture.Run(receipt);
        Assert.Equal(0, effects);
        Assert.Same(originalCompletion, fixture.Completion(receipt));
        Assert.False(await originalCompletion.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Running_callback_cannot_be_suppressed_or_completed_before_its_body_exits()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var effects = 0;
        var receipt = fixture.Create(() =>
        {
            Interlocked.Increment(ref effects);
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return true;
        });
        var originalWorker = fixture.RunAsync(receipt);
        await entered.Task.WaitAsync(TestTimeout);
        Assert.False(fixture.Suppress(receipt));
        Assert.False(fixture.Completion(receipt).IsCompleted);
        Assert.False(originalWorker.IsCompleted);
        release.TrySetResult(true);
        await originalWorker.WaitAsync(TestTimeout);
        Assert.True(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task Cancelled_completion_waiter_cannot_retire_running_dispatch()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var receipt = fixture.Create(() => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); return true; });
        var originalWorker = fixture.RunAsync(receipt);
        await entered.Task.WaitAsync(TestTimeout);
        var originalCompletion = fixture.Completion(receipt);
        using var cancellation = new CancellationTokenSource();
        var waiter = originalCompletion.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(originalCompletion.IsCompleted);
        Assert.False(originalWorker.IsCompleted);
        Assert.False(fixture.Suppress(receipt));
        release.TrySetResult(true);
        await originalWorker.WaitAsync(TestTimeout);
        Assert.True(await originalCompletion.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Reentrant_Run_and_suppression_are_inert_inside_the_claimed_callback()
    {
        await using var fixture = new Fixture();
        object receipt = null;
        var calls = 0;
        var suppression = true;
        var completedInsideBody = true;
        receipt = fixture.Create(() =>
        {
            calls++;
            suppression = fixture.Suppress(receipt);
            fixture.Run(receipt);
            completedInsideBody = fixture.Completion(receipt).IsCompleted;
            return true;
        });
        fixture.Run(receipt);
        Assert.False(suppression);
        Assert.False(completedInsideBody);
        Assert.Equal(1, calls);
        Assert.True(await fixture.Completion(receipt).WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Faulting_held_callback_remains_owned_until_its_actual_throw()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var receipt = fixture.Create(() =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("Synthetic late callback failure.");
        });
        var originalWorker = fixture.RunAsync(receipt);
        await entered.Task.WaitAsync(TestTimeout);
        Assert.False(fixture.Suppress(receipt));
        Assert.False(fixture.Completion(receipt).IsCompleted);
        release.TrySetResult(true);
        await originalWorker.WaitAsync(TestTimeout);
        Assert.True(originalWorker.IsCompletedSuccessfully);
        Assert.False(await fixture.Completion(receipt).WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Sixteen_Run_contenders_invoke_the_callback_exactly_once()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var start = fixture.Signal();
        var effects = 0;
        var receipt = fixture.Create(() =>
        {
            Interlocked.Increment(ref effects);
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return true;
        });
        var originalWorker = fixture.RunAsync(receipt);
        await entered.Task.WaitAsync(TestTimeout);
        var contenders = Enumerable.Range(0, 15).Select(_ => fixture.Track(Task.Run(async () =>
        {
            await start.Task;
            fixture.Run(receipt);
        }))).ToArray();
        start.TrySetResult(true);
        await Task.WhenAll(contenders).WaitAsync(TestTimeout);
        Assert.Equal(1, effects);
        Assert.False(fixture.Completion(receipt).IsCompleted);
        release.TrySetResult(true);
        await originalWorker.WaitAsync(TestTimeout);
        Assert.True(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        Assert.False(fixture.Suppress(receipt));
    }

    [Fact]
    public async Task Sixteen_suppression_contenders_have_one_winner_and_no_late_callback_effects()
    {
        await using var fixture = new Fixture();
        var start = fixture.Signal();
        var effects = 0;
        var receipt = fixture.Create(() => { Interlocked.Increment(ref effects); return true; });
        var contenders = Enumerable.Range(0, 16).Select(_ => fixture.Track(Task.Run(async () =>
        {
            await start.Task;
            return fixture.Suppress(receipt);
        }))).ToArray();
        start.TrySetResult(true);
        var outcomes = await Task.WhenAll(contenders).WaitAsync(TestTimeout);
        Assert.Equal(1, outcomes.Count(outcome => outcome));
        Assert.False(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        var lateWorkers = Enumerable.Range(0, 16).Select(_ => fixture.RunAsync(receipt)).ToArray();
        await Task.WhenAll(lateWorkers).WaitAsync(TestTimeout);
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task Fifty_Run_suppression_races_never_report_a_false_dispatch_or_duplicate_effect()
    {
        await using var fixture = new Fixture();
        for (var index = 0; index < 50; index++)
        {
            var start = fixture.Signal();
            var effects = 0;
            var receipt = fixture.Create(() => { Interlocked.Increment(ref effects); return true; });
            var runner = fixture.Track(Task.Run(async () => { await start.Task; fixture.Run(receipt); }));
            var suppressor = fixture.Track(Task.Run(async () => { await start.Task; return fixture.Suppress(receipt); }));
            start.TrySetResult(true);
            await Task.WhenAll(runner, suppressor).WaitAsync(TestTimeout);
            var suppressed = await suppressor.WaitAsync(TestTimeout);
            var dispatched = await fixture.Completion(receipt).WaitAsync(TestTimeout);
            Assert.Equal(!suppressed, dispatched);
            Assert.Equal(suppressed ? 0 : 1, effects);
            fixture.Run(receipt);
            Assert.Equal(suppressed ? 0 : 1, effects);
        }
    }

    [Fact]
    public async Task Independent_receipts_do_not_share_completion_or_suppression_state()
    {
        await using var fixture = new Fixture();
        var firstCalls = 0;
        var secondCalls = 0;
        var first = fixture.Create(() => { firstCalls++; return true; });
        var second = fixture.Create(() => { secondCalls++; return true; });
        Assert.NotSame(fixture.Completion(first), fixture.Completion(second));
        Assert.True(fixture.Suppress(first));
        Assert.False(fixture.Completion(second).IsCompleted);
        fixture.Run(second);
        Assert.False(await fixture.Completion(first).WaitAsync(TestTimeout));
        Assert.True(await fixture.Completion(second).WaitAsync(TestTimeout));
        Assert.Equal(0, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public async Task Queued_receipt_keeps_the_same_periodic_gate_busy_until_positive_dispatch_and_then_dedupes()
    {
        await using var fixture = new Fixture();
        object receipt = null;
        var queued = fixture.Signal();
        var notices = 0;
        var attempts = 0;
        var periodic = fixture.Periodic((_, _) =>
        {
            attempts++;
            receipt = fixture.Create(() => { notices++; return true; });
            queued.TrySetResult(true);
            return fixture.Completion(receipt);
        });
        var original = fixture.Start(periodic);
        await queued.Task.WaitAsync(TestTimeout);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Equal(0, notices);
        fixture.Run(receipt);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(1, notices);
        AssertOpen(fixture.Gate);
        await fixture.Start(periodic).WaitAsync(TestTimeout);
        Assert.Equal(1, attempts);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task Suppressed_pending_periodic_receipt_releases_only_after_false_completion_and_allows_retry()
    {
        await using var fixture = new Fixture();
        object receipt = null;
        var queued = fixture.Signal();
        var notices = 0;
        var attempts = 0;
        var periodic = fixture.Periodic((_, _) =>
        {
            attempts++;
            receipt = fixture.Create(() => { notices++; return true; });
            queued.TrySetResult(true);
            return fixture.Completion(receipt);
        });
        var original = fixture.Start(periodic);
        await queued.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        var suppressed = receipt;
        Assert.True(fixture.Suppress(suppressed));
        await original.WaitAsync(TestTimeout);
        AssertOpen(fixture.Gate);
        fixture.Run(suppressed);
        Assert.Equal(0, notices);
        var retry = fixture.Start(periodic);
        Assert.NotSame(suppressed, receipt);
        AssertBusy(fixture.Gate);
        fixture.Run(receipt);
        await retry.WaitAsync(TestTimeout);
        Assert.Equal(2, attempts);
        Assert.Equal(1, notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Cancelled_waiter_and_periodic_Dispose_cannot_release_a_running_dispatch_or_erase_its_true_receipt()
    {
        await using var fixture = new Fixture();
        object receipt = null;
        var queued = fixture.Signal();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var notices = 0;
        var periodic = fixture.Periodic((_, _) =>
        {
            receipt = fixture.Create(() =>
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                Interlocked.Increment(ref notices);
                return true;
            });
            queued.TrySetResult(true);
            return fixture.Completion(receipt);
        });
        var original = fixture.Start(periodic);
        await queued.Task.WaitAsync(TestTimeout);
        var originalWorker = fixture.RunAsync(receipt);
        await entered.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var waiter = original.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        periodic.Dispose();
        Assert.False(fixture.Suppress(receipt));
        Assert.False(original.IsCompleted);
        Assert.False(fixture.Completion(receipt).IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await originalWorker.WaitAsync(TestTimeout);
        await original.WaitAsync(TestTimeout);
        Assert.True(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        Assert.Equal(1, notices);
        AssertOpen(fixture.Gate);
        await fixture.Start(periodic).WaitAsync(TestTimeout);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task Accepted_post_then_throw_cannot_manufacture_false_completion_while_the_callback_runs()
    {
        await using var fixture = new Fixture();
        object receipt = null;
        Task originalWorker = null;
        var entered = fixture.Signal();
        var release = fixture.Signal();
        var postFailureObserved = fixture.Signal();
        var suppressedAfterThrow = true;
        var notices = 0;
        var periodic = fixture.Periodic((_, _) =>
        {
            receipt = fixture.Create(() =>
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                Interlocked.Increment(ref notices);
                return true;
            });
            try
            {
                originalWorker = fixture.RunAsync(receipt); // Simulated queue acceptance.
                entered.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Synthetic post error AFTER callback acceptance.");
            }
            catch (InvalidOperationException)
            {
                suppressedAfterThrow = fixture.Suppress(receipt);
                postFailureObserved.TrySetResult(true);
            }
            return fixture.Completion(receipt); // Never replace an already running original receipt.
        });
        // The synthetic poster waits synchronously for entry, so invoke the
        // existing core off-thread and track that ORIGINAL outer worker too.
        var original = fixture.Track(Task.Run(() => fixture.Start(periodic)));
        await postFailureObserved.Task.WaitAsync(TestTimeout);
        Assert.False(suppressedAfterThrow);
        Assert.False(fixture.Completion(receipt).IsCompleted);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await originalWorker.WaitAsync(TestTimeout);
        await original.WaitAsync(TestTimeout);
        Assert.True(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        Assert.Equal(1, notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Post_rejection_before_dispatch_suppresses_pending_receipt_and_late_callback_without_fake_success()
    {
        await using var fixture = new Fixture();
        object receipt = null;
        var notices = 0;
        var periodic = fixture.Periodic((_, _) =>
        {
            receipt = fixture.Create(() => { notices++; return true; });
            try { throw new InvalidOperationException("Synthetic rejected post."); }
            catch (InvalidOperationException) { _ = fixture.Suppress(receipt); }
            return fixture.Completion(receipt);
        });
        await fixture.Start(periodic).WaitAsync(TestTimeout);
        Assert.False(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        fixture.Run(receipt);
        Assert.Equal(0, notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Actual_dispatch_recheck_can_reject_a_changed_channel_and_retry_the_same_tuple_later()
    {
        await using var fixture = new Fixture();
        var channel = "stable";
        object receipt = null;
        var notices = 0;
        var attempts = 0;
        var periodic = fixture.Periodic((update, token) =>
        {
            attempts++;
            receipt = fixture.Create(() =>
            {
                if (token.IsCancellationRequested || channel != update.Channel) return false;
                notices++;
                return true;
            });
            return fixture.Completion(receipt);
        }, currentChannel: () => channel);
        var original = fixture.Start(periodic);
        AssertBusy(fixture.Gate);
        channel = "beta";
        fixture.Run(receipt);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, notices);
        AssertOpen(fixture.Gate);
        channel = "stable";
        var retry = fixture.Start(periodic);
        fixture.Run(receipt);
        await retry.WaitAsync(TestTimeout);
        Assert.Equal(2, attempts);
        Assert.Equal(1, notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Actual_dispatch_recheck_can_reject_a_cancelled_attempt_without_early_gate_release()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        object receipt = null;
        var notices = 0;
        var periodic = fixture.Periodic((_, token) =>
        {
            receipt = fixture.Create(() =>
            {
                if (token.IsCancellationRequested) return false;
                notices++;
                return true;
            });
            return fixture.Completion(receipt);
        });
        var original = fixture.Start(periodic, cancellation.Token);
        cancellation.Cancel();
        Assert.False(original.IsCompleted);
        Assert.False(fixture.Completion(receipt).IsCompleted);
        AssertBusy(fixture.Gate);
        fixture.Run(receipt);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, notices);
        Assert.False(await fixture.Completion(receipt).WaitAsync(TestTimeout));
        AssertOpen(fixture.Gate);
        var retry = fixture.Start(periodic);
        fixture.Run(receipt);
        await retry.WaitAsync(TestTimeout);
        Assert.Equal(1, notices);
    }

    private static VerifiedUpdate Update(string channel) => new(new Version(9, 0, 0), new Version(1, 4, 19), channel,
        new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/keyboard.wtf-setup.exe"),
        new string('a', 64), 1024, "Synthetic notification receipt", UpdateManifestVerifier.CurrentKeyId);

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
        // Required production seam is resolved before signals or synthetic work.
        private readonly Binding _binding = Binding.Require();
        private readonly ConcurrentBag<TaskCompletionSource<bool>> _signals = new();
        private readonly ConcurrentBag<Task> _workers = new();
        private readonly ConcurrentBag<object> _receipts = new();
        private readonly ConcurrentBag<PeriodicUpdateNotification> _periodic = new();
        internal UpdateWorkAdmissionGate Gate { get; } = new();

        internal object Create(Func<bool> dispatch)
        {
            var receipt = _binding.Create(dispatch);
            _receipts.Add(receipt);
            return receipt;
        }

        internal Task<bool> Completion(object receipt) => _binding.GetCompletion(receipt);
        internal void Run(object receipt) => _binding.Execute(receipt);
        internal bool Suppress(object receipt) => _binding.Suppress(receipt);
        internal Task RunAsync(object receipt) => Track(Task.Run(() => Run(receipt)));
        internal Task Track(Task original) { _workers.Add(original); return original; }
        internal Task<T> Track<T>(Task<T> original) { _workers.Add(original); return original; }

        internal TaskCompletionSource<bool> Signal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals.Add(signal);
            return signal;
        }

        internal PeriodicUpdateNotification Periodic(Func<VerifiedUpdate, CancellationToken, Task<bool>> notify,
            Func<string> currentChannel = null)
        {
            var core = new PeriodicUpdateNotification(Gate, () => true, currentChannel ?? (() => "stable"),
                (channel, _) => Task.FromResult(Update(channel)), notify);
            _periodic.Add(core);
            return core;
        }

        internal Task Start(PeriodicUpdateNotification core, CancellationToken token = default) => Track(core.CheckOnceAsync(token));

        public async ValueTask DisposeAsync()
        {
            foreach (var core in _periodic) core.Dispose();
            foreach (var signal in _signals) signal.TrySetResult(true);
            foreach (var receipt in _receipts) _ = Suppress(receipt);
            var workers = Task.WhenAll(_workers);
            try { await workers.WaitAsync(TestTimeout); }
            catch (Exception) when (workers.IsCompleted) { /* Completed asserted errors are observed, not unfinished work. */ }
            // Workers may have created receipts while entering the test core.
            // Their original child Run Tasks must also finish, not just the
            // Completion waiters that can resume just before an inert Run tail.
            var children = Task.WhenAll(_workers);
            try { await children.WaitAsync(TestTimeout); }
            catch (Exception) when (children.IsCompleted) { /* Never swallow unfinished original child work. */ }
            foreach (var receipt in _receipts) _ = Suppress(receipt);
            var completions = Task.WhenAll(_receipts.Select(Completion));
            try { await completions.WaitAsync(TestTimeout); }
            catch (Exception) when (completions.IsCompleted) { /* An unfinished completion timeout must escape. */ }
        }
    }

    private sealed class Binding
    {
        internal ConstructorInfo Constructor { get; }
        internal PropertyInfo Completion { get; }
        internal MethodInfo Run { get; }
        internal MethodInfo TrySuppress { get; }

        private Binding()
        {
            var type = typeof(UpdateWorkAdmissionGate).Assembly.GetType("KeyboardWtf.Services.Updates.UpdateNotificationDispatchReceipt");
            Assert.True(type is not null, "The unwired original-callback notification dispatch receipt is missing.");
            Constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [typeof(Func<bool>)], null);
            Assert.NotNull(Constructor);
            Completion = type.GetProperty("Completion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(Completion);
            Assert.Equal(typeof(Task<bool>), Completion.PropertyType);
            Run = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            Assert.NotNull(Run);
            TrySuppress = type.GetMethod("TrySuppress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            Assert.NotNull(TrySuppress);
        }

        internal static Binding Require() => new();
        internal object Create(Func<bool> callback) => Invoke(() => Constructor.Invoke([callback]));
        internal Task<bool> GetCompletion(object receipt) => Assert.IsType<Task<bool>>(Invoke(() => Completion.GetValue(receipt)));
        internal void Execute(object receipt) { _ = Invoke(() => Run.Invoke(receipt, null)); }
        internal bool Suppress(object receipt) => Assert.IsType<bool>(Invoke(() => TrySuppress.Invoke(receipt, null)));

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
}
