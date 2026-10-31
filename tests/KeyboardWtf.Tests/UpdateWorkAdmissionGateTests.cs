using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Pure, in-memory admission-core checks only. Passing these does NOT prove
// updater safety: the production ingress and every actual work owner must also
// be wired and verified. Reflection permits honest behavioral red before the
// core exists; no installer, application, provider, timer or OS work is started.
public sealed class UpdateWorkAdmissionGateTests
{
    [Fact]
    public void Idle_open_gate_accepts_work_and_refuses_freeze_until_its_lease_finishes()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryAcquire(out var work));
        Assert.False(gate.TryFreeze(out _));

        work.Dispose();
        Assert.True(gate.TryFreeze(out var frozen));
        Assert.False(gate.TryAcquire(out _));
        frozen.Dispose();
        AssertCanAcquire(gate);
    }

    [Fact]
    public void Every_accepted_owner_must_finish_before_freeze()
    {
        var gate = Gate.Create();
        var owners = new List<IDisposable>();
        for (var index = 0; index < 32; index++)
        {
            Assert.True(gate.TryAcquire(out var owner));
            owners.Add(owner);
        }

        for (var index = 0; index < owners.Count - 1; index++)
        {
            owners[index].Dispose();
            Assert.False(gate.TryFreeze(out _));
        }
        owners[^1].Dispose();
        AssertCanFreeze(gate);
    }

    [Fact]
    public void Disposing_the_same_owner_twice_does_not_release_another_owner()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryAcquire(out var first));
        Assert.True(gate.TryAcquire(out var second));

        first.Dispose();
        first.Dispose();
        Assert.False(gate.TryFreeze(out _));
        second.Dispose();
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Concurrent_duplicate_owner_disposal_is_exactly_once()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryAcquire(out var first));
        Assert.True(gate.TryAcquire(out var second));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(first.Dispose)));

        Assert.False(gate.TryFreeze(out _));
        second.Dispose();
        AssertCanFreeze(gate);
    }

    [Fact]
    public void Old_disposed_owner_cannot_decrement_a_new_owner()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryAcquire(out var old));
        old.Dispose();
        Assert.True(gate.TryAcquire(out var current));

        old.Dispose();
        Assert.False(gate.TryFreeze(out _));
        current.Dispose();
        AssertCanFreeze(gate);
    }

    [Fact]
    public void Tentative_freeze_blocks_work_and_a_second_freeze_then_thaws_on_disposal()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var frozen));
        Assert.False(gate.TryAcquire(out _));
        Assert.False(gate.TryFreeze(out _));

        frozen.Dispose();
        AssertCanAcquire(gate);
        AssertCanFreeze(gate);
    }

    [Fact]
    public void Repeated_tentative_disposal_is_harmless()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
        frozen.Dispose();
        frozen.Dispose();

        AssertCanAcquire(gate);
        AssertCanFreeze(gate);
    }

    [Fact]
    public void Stale_freeze_token_cannot_thaw_a_new_freeze()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var old));
        old.Dispose();
        Assert.True(gate.TryFreeze(out var current));

        old.Dispose();
        Assert.False(gate.TryAcquire(out _));
        Assert.False(gate.TryFreeze(out _));
        current.Dispose();
        AssertCanAcquire(gate);
    }

    [Fact]
    public void Stale_freeze_commit_cannot_commit_a_new_freeze()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var old));
        old.Dispose();
        Assert.True(gate.TryFreeze(out var current));

        // A disposed Commit may reject or be a no-op, but cannot acquire the
        // newer token's authority. Verify behavior, not exception wording.
        _ = Record.Exception(old.Commit);
        current.Dispose();
        AssertCanAcquire(gate);
        AssertCanFreeze(gate);
    }

    [Fact]
    public void Stale_freeze_commit_cannot_lock_an_open_gate()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var old));
        old.Dispose();

        _ = Record.Exception(old.Commit);
        AssertCanAcquire(gate);
        AssertCanFreeze(gate);
    }

    [Fact]
    public void Committed_freeze_stays_closed_after_disposal()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Commit();
        frozen.Dispose();

        Assert.False(gate.TryAcquire(out _));
        Assert.False(gate.TryFreeze(out _));
    }

    [Fact]
    public void Committed_freeze_commit_and_dispose_are_idempotent()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Commit();
        frozen.Commit();
        frozen.Dispose();
        frozen.Commit();
        frozen.Dispose();

        Assert.False(gate.TryAcquire(out _));
        Assert.False(gate.TryFreeze(out _));
    }

    [Fact]
    public async Task Committed_freeze_remains_closed_after_lost_install_acknowledgement()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var frozen));
        var error = new IOException("Synthetic acknowledgement disconnect");
        var observed = await Record.ExceptionAsync(async () =>
        {
            using (frozen)
            {
                // Synthetic Process.Start commit: no process is started.
                frozen.Commit();
                await Task.FromException(error);
            }
        });

        Assert.Same(error, observed);
        Assert.False(gate.TryAcquire(out _));
        Assert.False(gate.TryFreeze(out _));
    }

    [Fact]
    public void Failed_prelaunch_attempt_thaws_without_canceling_or_starting_work()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var frozen));
        var error = new InvalidOperationException("Synthetic prelaunch failure");
        var observed = Record.Exception((Action)(() =>
        {
            using (frozen)
                throw error;
        }));

        Assert.Same(error, observed);
        AssertCanAcquire(gate);
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Tracked_work_is_admitted_before_its_factory_is_invoked()
    {
        var gate = Gate.Create();
        var calls = 0;
        await gate.TrackAsync(() =>
        {
            calls++;
            Assert.False(gate.TryFreeze(out _));
            return Task.CompletedTask;
        });

        Assert.Equal(1, calls);
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Tracked_worker_is_not_released_until_actual_task_completion()
    {
        var gate = Gate.Create();
        var entered = Signal();
        var finish = Signal();
        var tracked = gate.TrackAsync(() =>
        {
            entered.SetResult(true);
            return finish.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(tracked.IsCompleted);
        Assert.False(gate.TryFreeze(out _));
        finish.SetResult(true);
        await tracked.WaitAsync(TimeSpan.FromSeconds(5));
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task External_cancellation_request_does_not_release_a_worker_that_is_still_unwinding()
    {
        var gate = Gate.Create();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var finishCleanup = Signal();
        var tracked = gate.TrackAsync(async () =>
        {
            entered.SetResult(true);
            // Simulate a provider/cleanup task that has observed cancellation
            // but has not actually completed. No production work is involved.
            await finishCleanup.Task;
            cancellation.Token.ThrowIfCancellationRequested();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(tracked.IsCompleted);
        Assert.False(gate.TryFreeze(out _));
        finishCleanup.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracked);
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Synchronously_throwing_factory_releases_its_owner_and_preserves_error()
    {
        var gate = Gate.Create();
        var error = new IOException("Synthetic factory failure");
        var observed = await Record.ExceptionAsync(() => gate.TrackAsync(() => throw error));

        Assert.Same(error, observed);
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Asynchronously_failed_worker_releases_its_owner_and_preserves_error()
    {
        var gate = Gate.Create();
        var error = new IOException("Synthetic async failure");
        var finish = Signal();
        var tracked = gate.TrackAsync(async () =>
        {
            await finish.Task;
            throw error;
        });
        Assert.False(gate.TryFreeze(out _));
        finish.SetResult(true);

        Assert.Same(error, await Record.ExceptionAsync(() => tracked));
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Already_canceled_worker_releases_its_owner()
    {
        var gate = Gate.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.TrackAsync(() => Task.FromCanceled(cancellation.Token)));
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Failed_one_of_two_tracked_workers_does_not_release_the_other()
    {
        var gate = Gate.Create();
        var finishOther = Signal();
        var other = gate.TrackAsync(() => finishOther.Task);
        var error = new IOException("Synthetic second worker failure");

        Assert.Same(error, await Record.ExceptionAsync(() =>
            gate.TrackAsync(() => Task.FromException(error))));
        Assert.False(gate.TryFreeze(out _));
        finishOther.SetResult(true);
        await other.WaitAsync(TimeSpan.FromSeconds(5));
        AssertCanFreeze(gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_or_committed_gate_rejects_tracked_work_without_invoking_factory(bool commit)
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var frozen));
        if (commit) frozen.Commit();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.TrackAsync(() =>
        {
            calls++;
            return Task.CompletedTask;
        }));
        Assert.Equal(0, calls);
        Assert.False(gate.TryAcquire(out _));
        Assert.False(gate.TryFreeze(out _));
        frozen.Dispose();
        if (commit)
            Assert.False(gate.TryAcquire(out _));
        else
            AssertCanAcquire(gate);
    }

    [Fact]
    public async Task Multiple_tracked_workers_release_independently_of_completion_order()
    {
        var gate = Gate.Create();
        var finishes = Enumerable.Range(0, 12).Select(_ => Signal()).ToArray();
        var tasks = finishes.Select(finish => gate.TrackAsync(() => finish.Task)).ToArray();

        for (var index = finishes.Length - 1; index > 0; index--)
        {
            finishes[index].SetResult(true);
            await tasks[index].WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gate.TryFreeze(out _));
        }
        finishes[0].SetResult(true);
        await tasks[0].WaitAsync(TimeSpan.FromSeconds(5));
        AssertCanFreeze(gate);
    }

    [Fact]
    public async Task Acquisition_and_freeze_have_exactly_one_winner_across_fifty_concurrent_cycles()
    {
        for (var cycle = 0; cycle < 50; cycle++)
        {
            var gate = Gate.Create();
            var start = Signal();
            var acquire = Task.Run(async () =>
            {
                await start.Task;
                var accepted = gate.TryAcquire(out var owner);
                return (Accepted: accepted, Owner: owner);
            });
            var freeze = Task.Run(async () =>
            {
                await start.Task;
                var accepted = gate.TryFreeze(out var token);
                return (Accepted: accepted, Token: token);
            });
            start.SetResult(true);
            await Task.WhenAll(acquire, freeze).WaitAsync(TimeSpan.FromSeconds(5));
            var acquireResult = await acquire;
            var freezeResult = await freeze;

            Assert.NotEqual(acquireResult.Accepted, freezeResult.Accepted);
            if (acquireResult.Accepted)
            {
                Assert.False(gate.TryFreeze(out _));
                acquireResult.Owner.Dispose();
            }
            else
            {
                Assert.False(gate.TryAcquire(out _));
                freezeResult.Token.Dispose();
            }
            AssertCanAcquire(gate);
            AssertCanFreeze(gate);
        }
    }

    [Fact]
    public async Task Concurrent_freeze_attempts_produce_one_authoritative_token()
    {
        var gate = Gate.Create();
        var start = Signal();
        var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var accepted = gate.TryFreeze(out var token);
            return (Accepted: accepted, Token: token);
        })).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));

        var accepted = results.Where(attempt => attempt.Accepted).ToArray();
        Assert.Single(accepted);
        Assert.False(gate.TryAcquire(out _));
        accepted[0].Token.Dispose();
        AssertCanAcquire(gate);
    }

    [Fact]
    public async Task Duplicate_tentative_disposal_cannot_thaw_a_subsequent_committed_freeze()
    {
        var gate = Gate.Create();
        Assert.True(gate.TryFreeze(out var stale));
        stale.Dispose();
        Assert.True(gate.TryFreeze(out var current));
        current.Commit();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(stale.Dispose)));
        current.Dispose();

        Assert.False(gate.TryAcquire(out _));
        Assert.False(gate.TryFreeze(out _));
    }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertCanAcquire(Gate gate)
    {
        Assert.True(gate.TryAcquire(out var owner));
        owner.Dispose();
    }

    private static void AssertCanFreeze(Gate gate)
    {
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
    }

    private sealed class Gate
    {
        private readonly object _value;
        private readonly MethodInfo _acquire;
        private readonly MethodInfo _freeze;
        private readonly MethodInfo _track;

        private Gate(object value)
        {
            _value = value;
            _acquire = RequiredMethod(value.GetType(), "TryAcquire");
            _freeze = RequiredMethod(value.GetType(), "TryFreeze");
            _track = RequiredMethod(value.GetType(), "TrackAsync");
        }

        internal static Gate Create()
        {
            var type = typeof(SecureUpdateService).Assembly.GetType(
                "KeyboardWtf.Services.Updates.UpdateWorkAdmissionGate");
            Assert.True(type is not null, "The actual-work update admission core has not been implemented.");
            var value = Activator.CreateInstance(type, nonPublic: true);
            Assert.NotNull(value);
            return new Gate(value);
        }

        internal bool TryAcquire(out IDisposable owner)
        {
            var arguments = new object[] { null };
            var accepted = (bool)Invoke(_value, _acquire, arguments);
            owner = arguments[0] as IDisposable;
            if (accepted) Assert.NotNull(owner);
            return accepted;
        }

        internal bool TryFreeze(out Freeze token)
        {
            var arguments = new object[] { null };
            var accepted = (bool)Invoke(_value, _freeze, arguments);
            token = accepted ? new Freeze(arguments[0]) : null;
            return accepted;
        }

        internal Task TrackAsync(Func<Task> factory) =>
            Assert.IsAssignableFrom<Task>(Invoke(_value, _track, new object[] { factory }));
    }

    private sealed class Freeze : IDisposable
    {
        private readonly object _value;
        private readonly IDisposable _dispose;
        private readonly MethodInfo _commit;

        internal Freeze(object value)
        {
            Assert.NotNull(value);
            _value = value;
            _dispose = Assert.IsAssignableFrom<IDisposable>(value);
            _commit = RequiredMethod(value.GetType(), "Commit");
        }

        internal void Commit() => Invoke(_value, _commit, Array.Empty<object>());
        public void Dispose() => _dispose.Dispose();
    }

    private static MethodInfo RequiredMethod(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(method is not null, $"{type.Name}.{name} is missing.");
        return method;
    }

    private static object Invoke(object target, MethodInfo method, object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
