using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Synthetic monitor/queue ownership proof only. No timer, Control, application,
// NotifyIcon, profile, installer, network or provider is opened. The required
// production seam is resolved BEFORE constructing held work or fixture resources.
// Explicitly running our queue is not proof of real Windows UI dispatch.
public sealed class PublicUpdateNotificationMonitorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Required_monitor_contract_has_only_notification_dependencies()
    {
        var binding = Binding.Require();
        Assert.Equal(typeof(Task), binding.CheckOnce.ReturnType);
        Assert.Empty(binding.CheckOnce.GetParameters());
        Assert.Equal(typeof(Task), binding.Stop.ReturnType);
        Assert.Empty(binding.Stop.GetParameters());
        Assert.True(typeof(IDisposable).IsAssignableFrom(binding.Type));
        Assert.Equal(new[] { typeof(UpdateWorkAdmissionGate), typeof(Func<bool>), typeof(Func<string>),
            typeof(Func<string, CancellationToken, Task<VerifiedUpdate>>), typeof(Action<Action>),
            typeof(Func<VerifiedUpdate, bool>) },
            binding.Constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Every_constructor_dependency_is_required_without_callback_effects()
    {
        var binding = Binding.Require();
        var effects = 0;
        var args = new object[] { new UpdateWorkAdmissionGate(), (Func<bool>)(() => { effects++; return true; }),
            (Func<string>)(() => { effects++; return "stable"; }),
            (Func<string, CancellationToken, Task<VerifiedUpdate>>)((_, _) => { effects++; return Task.FromResult(Update()); }),
            (Action<Action>)(_ => { effects++; }), (Func<VerifiedUpdate, bool>)(_ => { effects++; return true; }) };
        for (var index = 0; index < args.Length; index++)
        {
            var missing = (object[])args.Clone();
            missing[index] = null;
            Assert.Throws<ArgumentNullException>(() => binding.Create(missing));
        }
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task Ineligible_private_or_staging_boundary_performs_no_check_or_queue()
    {
        await using var fixture = new Fixture();
        fixture.Eligibility = () => false;
        await fixture.Start().WaitAsync(TestTimeout);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Checks);
        Assert.Equal(0, fixture.Posts);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Frozen_gate_defers_without_check_queue_or_implicit_replay()
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(0, fixture.Checks);
            Assert.Equal(0, fixture.Posts);
            Assert.Equal(0, fixture.Notices);
        }
        finally { frozen.Dispose(); }
        Assert.Equal(0, fixture.Checks);
        var original = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        queued();
        await original.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Stopped_or_disposed_monitor_never_starts_new_callbacks()
    {
        await using var fixture = new Fixture();
        var stopped = fixture.Stop();
        Assert.Same(stopped, fixture.Stop());
        await stopped.WaitAsync(TestTimeout);
        fixture.DisposeHost();
        fixture.DisposeHost();
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Checks);
        Assert.Equal(0, fixture.Posts);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Stop_cancels_but_retains_original_check_and_token_until_actual_cleanup()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        CancellationToken captured = default;
        fixture.Check = async (_, token) =>
        {
            captured = token;
            entered.TrySetResult(true);
            await finish.Task; // Deliberately ignore cancellation until real cleanup.
            return Update();
        };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        var stopping = fixture.Stop();
        Assert.Same(stopping, fixture.Stop());
        Assert.True(captured.IsCancellationRequested);
        Assert.False(original.IsCompleted);
        Assert.False(stopping.IsCompleted);
        AssertBusy(fixture.Gate);
        // CTS disposal is observable without access to the monitor's fields.
        Assert.True(captured.WaitHandle.WaitOne(0));
        var registrations = 0;
        using (captured.Register(() => Interlocked.Increment(ref registrations))) { }
        Assert.Equal(1, registrations);
        finish.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        Assert.Throws<ObjectDisposedException>(() => { _ = captured.WaitHandle; });
        Assert.Equal(0, fixture.Posts);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Sixteen_overlapping_checks_return_the_same_published_original_completion()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        fixture.Check = async (_, _) => { entered.TrySetResult(true); await finish.Task; return Update(); };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        var returned = new ConcurrentBag<Task>();
        var contenders = Enumerable.Range(0, 16).Select(_ => Task.Run(() => { returned.Add(fixture.Start()); })).ToArray();
        foreach (var contender in contenders) fixture.Track(contender);
        await Task.WhenAll(contenders).WaitAsync(TestTimeout);
        Assert.Equal(16, returned.Count);
        Assert.All(returned, task => Assert.Same(original, task));
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(0, fixture.Posts);
        AssertBusy(fixture.Gate);
        var stopping = fixture.Stop();
        Assert.False(stopping.IsCompleted);
        finish.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Caller_waiter_cancellation_cannot_retire_a_held_original_check()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        fixture.Check = async (_, _) => { entered.TrySetResult(true); await finish.Task; return Update(); };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var waiter = original.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(original.IsCompleted);
        Assert.Same(original, fixture.Start());
        AssertBusy(fixture.Gate);
        finish.TrySetResult(true);
        var queued = await fixture.TakePostedAsync();
        queued();
        await original.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Notice_only_executes_when_the_injected_ui_queue_runs_on_its_chosen_thread()
    {
        await using var fixture = new Fixture();
        var callbackThread = 0;
        fixture.Notify = _ => { callbackThread = Environment.CurrentManagedThreadId; return true; };
        var original = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        Assert.Equal(0, fixture.Notices);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        var queueThread = Environment.CurrentManagedThreadId;
        queued();
        await original.WaitAsync(TestTimeout);
        Assert.Equal(queueThread, callbackThread);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Pending_never_run_notice_is_suppressed_on_stop_and_late_callback_is_inert()
    {
        await using var fixture = new Fixture();
        var original = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        Assert.Equal(0, fixture.Notices);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        var stopping = fixture.Stop();
        await stopping.WaitAsync(TestTimeout); // No UI callback is run to finish shutdown.
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        queued();
        queued();
        Assert.Equal(0, fixture.Notices);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(1, fixture.Posts);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Selected_channel_is_rechecked_at_actual_queued_dispatch()
    {
        await using var fixture = new Fixture();
        var selected = "stable";
        fixture.Channel = () => selected;
        var original = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        selected = "beta";
        queued();
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        selected = "stable";
        var next = fixture.Start();
        (await fixture.TakePostedAsync())();
        await next.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices); // Suppression did not poison dedupe.
    }

    [Fact]
    public async Task Eligibility_is_rechecked_at_actual_queued_dispatch()
    {
        await using var fixture = new Fixture();
        var eligible = true;
        fixture.Eligibility = () => eligible;
        var original = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        eligible = false;
        queued();
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        eligible = true;
        var next = fixture.Start();
        (await fixture.TakePostedAsync())();
        await next.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Running_notice_cannot_be_retired_by_stop_or_cancelled_waiter()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        fixture.Notify = _ => { entered.TrySetResult(true); finish.Task.GetAwaiter().GetResult(); return true; };
        var original = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        var actualCallback = Task.Run(queued);
        fixture.Track(actualCallback);
        await entered.Task.WaitAsync(TestTimeout);
        var stopping = fixture.Stop();
        Assert.Same(stopping, fixture.Stop());
        Assert.False(original.IsCompleted);
        Assert.False(stopping.IsCompleted);
        AssertBusy(fixture.Gate);
        using var cancellation = new CancellationTokenSource();
        var waiter = stopping.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(stopping.IsCompleted);
        AssertBusy(fixture.Gate);
        finish.TrySetResult(true);
        await actualCallback.WaitAsync(TestTimeout);
        await original.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Synchronous_dispose_never_joins_the_running_ui_callback()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        fixture.Notify = _ => { entered.TrySetResult(true); finish.Task.GetAwaiter().GetResult(); return true; };
        var original = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        var actualCallback = Task.Run(queued);
        fixture.Track(actualCallback);
        await entered.Task.WaitAsync(TestTimeout);
        var disposal = Task.Run(fixture.DisposeHost);
        fixture.Track(disposal);
        await disposal.WaitAsync(TestTimeout);
        var stopping = fixture.Stop();
        Assert.False(stopping.IsCompleted);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        finish.TrySetResult(true);
        await actualCallback.WaitAsync(TestTimeout);
        await original.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Stop_completion_is_published_once_and_reused_before_and_after_cleanup()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        fixture.Check = async (_, _) => { entered.TrySetResult(true); await finish.Task; return Update(); };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        var stopping = fixture.Stop();
        var stops = new ConcurrentBag<Task>();
        var contenders = Enumerable.Range(0, 16).Select(_ => Task.Run(() => { stops.Add(fixture.Stop()); })).ToArray();
        foreach (var contender in contenders) fixture.Track(contender);
        await Task.WhenAll(contenders).WaitAsync(TestTimeout);
        Assert.Equal(16, stops.Count);
        Assert.All(stops, task => Assert.Same(stopping, task));
        Assert.False(stopping.IsCompleted);
        finish.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        Assert.Same(stopping, fixture.Stop());
        fixture.DisposeHost();
        Assert.Same(stopping, fixture.Stop());
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Check_completion_is_published_before_reentrant_checker_callback()
    {
        await using var fixture = new Fixture();
        var finish = fixture.Signal();
        Task reentered = null;
        fixture.Check = (_, _) => { reentered = fixture.Start(); return ResultAfter(finish.Task); };
        var original = fixture.Start();
        Assert.NotNull(reentered);
        Assert.Same(original, reentered);
        Assert.False(original.IsCompleted);
        Assert.Equal(1, fixture.Checks);
        AssertBusy(fixture.Gate);
        finish.TrySetResult(true);
        (await fixture.TakePostedAsync())();
        await original.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Reentrant_stop_inside_checker_before_original_task_return_cannot_dispose_its_token()
    {
        await using var fixture = new Fixture();
        var finish = fixture.Signal();
        Task reenteredStop = null;
        CancellationToken captured = default;
        var stopWasCompletedInsideCallback = true;
        var tokenUsableInsideCallback = false;
        fixture.Check = (_, token) =>
        {
            captured = token;
            reenteredStop = fixture.Stop(); // The factory has not returned its Task yet.
            stopWasCompletedInsideCallback = reenteredStop.IsCompleted;
            tokenUsableInsideCallback = token.WaitHandle.WaitOne(0);
            return ResultAfter(finish.Task);
        };
        var original = fixture.Start();
        Assert.NotNull(reenteredStop);
        Assert.Same(reenteredStop, fixture.Stop());
        Assert.False(stopWasCompletedInsideCallback);
        Assert.True(tokenUsableInsideCallback);
        Assert.True(captured.IsCancellationRequested);
        Assert.False(original.IsCompleted);
        Assert.False(reenteredStop.IsCompleted);
        AssertBusy(fixture.Gate);
        finish.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        await reenteredStop.WaitAsync(TestTimeout);
        Assert.Throws<ObjectDisposedException>(() => { _ = captured.WaitHandle; });
        Assert.Equal(0, fixture.Posts);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Running_notice_may_reenter_check_and_stop_without_early_completion_or_deadlock()
    {
        await using var fixture = new Fixture();
        Task reenteredCheck = null;
        Task reenteredStop = null;
        var stopWasCompletedInsideNotice = true;
        fixture.Notify = _ =>
        {
            reenteredCheck = fixture.Start();
            reenteredStop = fixture.Stop();
            stopWasCompletedInsideNotice = reenteredStop.IsCompleted;
            return true; // Positive actual attempt remains positive after stopping.
        };
        var original = fixture.Start();
        (await fixture.TakePostedAsync())();
        await original.WaitAsync(TestTimeout);
        Assert.Same(original, reenteredCheck);
        Assert.NotNull(reenteredStop);
        Assert.False(stopWasCompletedInsideNotice);
        Assert.Same(reenteredStop, fixture.Stop());
        await reenteredStop.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Cancellation_callback_can_reenter_stop_and_observe_the_same_shutdown_completion()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        CancellationToken captured = default;
        fixture.Check = async (_, token) => { captured = token; entered.TrySetResult(true); await finish.Task; return Update(); };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        Task reentered = null;
        var reenteredWasCompleted = true;
        using var registration = captured.Register(() =>
        {
            reentered = fixture.Stop();
            reenteredWasCompleted = reentered.IsCompleted;
        });
        var stopping = fixture.Stop();
        Assert.Same(stopping, reentered);
        Assert.False(reenteredWasCompleted);
        Assert.False(stopping.IsCompleted);
        finish.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Throwing_cancellation_registration_cannot_bypass_original_shutdown_drainage()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        CancellationToken captured = default;
        fixture.Check = async (_, token) => { captured = token; entered.TrySetResult(true); await finish.Task; return Update(); };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        var registration = captured.Register(() => throw new IOException("Synthetic cancellation observer failure"));
        Task stopping = null;
        try
        {
            stopping = fixture.Stop();
            Assert.Same(stopping, fixture.Stop());
            Assert.True(captured.IsCancellationRequested);
            Assert.True(captured.WaitHandle.WaitOne(0));
            Assert.False(original.IsCompleted);
            Assert.False(stopping.IsCompleted);
            AssertBusy(fixture.Gate);
            finish.TrySetResult(true);
            await original.WaitAsync(TestTimeout);
            await stopping.WaitAsync(TestTimeout);
            Assert.Throws<ObjectDisposedException>(() => { _ = captured.WaitHandle; });
            Assert.Equal(0, fixture.Posts);
            Assert.Equal(0, fixture.Notices);
            AssertOpen(fixture.Gate);
        }
        finally
        {
            finish.TrySetResult(true);
            await Task.WhenAll(original, stopping ?? fixture.Stop()).WaitAsync(TestTimeout);
            registration.Dispose(); // Only after both original work and shutdown positively finish.
        }
    }

    [Fact]
    public async Task Post_failure_before_queue_is_contained_and_does_not_poison_the_next_attempt()
    {
        await using var fixture = new Fixture();
        fixture.Post = _ => throw new InvalidOperationException("Synthetic missing UI handle");
        var first = fixture.Start();
        await first.WaitAsync(TestTimeout);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal(1, fixture.Posts);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        fixture.Post = fixture.Enqueue;
        var second = fixture.Start();
        (await fixture.TakePostedAsync())();
        await second.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Accepted_queue_then_post_exception_suppresses_pending_callback_before_completion()
    {
        await using var fixture = new Fixture();
        fixture.Post = action => { fixture.Enqueue(action); throw new IOException("Synthetic post acknowledgement failure"); };
        var original = fixture.Start();
        var late = await fixture.TakePostedAsync();
        await original.WaitAsync(TestTimeout); // It must not depend on a future queue run.
        Assert.True(original.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        late();
        late();
        Assert.Equal(0, fixture.Notices);
        fixture.Post = fixture.Enqueue;
        var next = fixture.Start();
        (await fixture.TakePostedAsync())();
        await next.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Started_queue_then_post_exception_cannot_retire_a_running_actual_notice()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var finish = fixture.Signal();
        fixture.Notify = _ => { entered.TrySetResult(true); finish.Task.GetAwaiter().GetResult(); return true; };
        Task actualCallback = null;
        fixture.Post = action =>
        {
            actualCallback = Task.Run(action);
            fixture.Track(actualCallback);
            entered.Task.GetAwaiter().GetResult();
            throw new IOException("Synthetic exception after real dispatch began");
        };
        var original = fixture.Start();
        Assert.NotNull(actualCallback);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        var stopping = fixture.Stop();
        Assert.False(stopping.IsCompleted);
        finish.TrySetResult(true);
        await actualCallback.WaitAsync(TestTimeout);
        await original.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData("eligibility")]
    [InlineData("channel")]
    [InlineData("check-throw")]
    [InlineData("check-fault")]
    [InlineData("check-null-task")]
    [InlineData("no-update")]
    public async Task Pre_notice_callback_failures_are_contained_and_release_original_ownership(string mode)
    {
        await using var fixture = new Fixture();
        if (mode == "eligibility") fixture.Eligibility = () => throw new IOException("Synthetic eligibility error");
        else if (mode == "channel") fixture.Channel = () => throw new IOException("Synthetic channel error");
        else fixture.Check = (_, _) => mode switch
        {
            "check-throw" => throw new IOException("Synthetic checker error"),
            "check-fault" => Task.FromException<VerifiedUpdate>(new IOException("Synthetic checker fault")),
            "check-null-task" => null,
            _ => Task.FromResult<VerifiedUpdate>(null),
        };
        var original = fixture.Start();
        await original.WaitAsync(TestTimeout);
        Assert.True(original.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.Posts);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        fixture.Eligibility = () => true;
        fixture.Channel = () => "stable";
        fixture.Check = (_, _) => Task.FromResult(Update());
        var next = fixture.Start();
        (await fixture.TakePostedAsync())();
        await next.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task False_or_throwing_actual_notice_is_contained_and_not_deduped(bool throws)
    {
        await using var fixture = new Fixture();
        fixture.Notify = _ => throws ? throw new IOException("Synthetic notice failure") : false;
        var first = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        Assert.Null(Record.Exception(queued)); // Never throw from the UI entry point.
        await first.WaitAsync(TestTimeout);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
        fixture.Notify = _ => true;
        var second = fixture.Start();
        (await fixture.TakePostedAsync())();
        await second.WaitAsync(TestTimeout);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.Notices);
        Assert.Equal(2, fixture.Posts);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Completed_notice_is_deduped_but_a_different_verified_tuple_can_notify_once()
    {
        await using var fixture = new Fixture();
        var first = fixture.Start();
        var queued = await fixture.TakePostedAsync();
        Assert.Same(first, fixture.Start()); // A queued dispatch still belongs to the same run.
        queued();
        await first.WaitAsync(TestTimeout);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        Assert.Equal(1, fixture.Posts);
        fixture.Check = (_, _) => Task.FromResult(Update() with { Sha256 = new string('b', 64) });
        var changed = fixture.Start();
        (await fixture.TakePostedAsync())();
        await changed.WaitAsync(TestTimeout);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.Notices);
        Assert.Equal(2, fixture.Posts);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Notification_monitor_cannot_replace_or_consume_manual_verified_consent()
    {
        await using var fixture = new Fixture();
        var consent = new VerifiedUpdateConsent();
        var approved = Update() with { Version = new Version(8, 0, 0), ReleaseNotes = "Synthetic manual offer" };
        var token = consent.Issue(approved);
        var original = fixture.Start();
        (await fixture.TakePostedAsync())();
        await original.WaitAsync(TestTimeout);
        await fixture.Stop().WaitAsync(TestTimeout);
        Assert.Same(approved, consent.Take(token, "stable"));
        // Absence of a consent dependency is not proof of future app wiring.
    }

    [Fact]
    public async Task Fifty_local_cycles_preserve_exact_queue_ownership_and_once_only_notification()
    {
        await using var fixture = new Fixture();
        for (var index = 0; index < 50; index++)
        {
            var tuple = Update() with { Version = new Version(9, 0, index), ReleaseNotes = "Synthetic monitor cycle " + index };
            fixture.Check = (_, _) => Task.FromResult(tuple);
            var first = fixture.Start();
            var queued = await fixture.TakePostedAsync();
            Assert.Same(first, fixture.Start());
            Assert.False(first.IsCompleted);
            AssertBusy(fixture.Gate);
            queued();
            queued(); // Duplicate delivery of the SAME posted delegate is inert.
            await first.WaitAsync(TestTimeout);
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(index + 1, fixture.Notices);
            Assert.Equal(index + 1, fixture.Posts);
            AssertOpen(fixture.Gate);
        }
        Assert.Equal(100, fixture.Checks);
    }

    [Fact]
    public async Task Fifty_start_stop_races_never_leave_effectful_queue_or_unfinished_originals()
    {
        _ = Binding.Require(); // Resolve before repeated fixtures and contenders.
        for (var index = 0; index < 50; index++)
        {
            await using var fixture = new Fixture();
            var start = Task.Run(() => fixture.Start());
            var stop = Task.Run(() => fixture.Stop());
            fixture.Track(start);
            fixture.Track(stop);
            await Task.WhenAll(start, stop).WaitAsync(TestTimeout);
            Assert.Same(fixture.Stop(), fixture.Stop());
            Assert.Equal(0, fixture.Notices); // Our UI queue has intentionally never run.
            fixture.RunAllQueued();
            Assert.Equal(0, fixture.Notices);
            Assert.InRange(fixture.Checks, 0, 1);
            Assert.InRange(fixture.Posts, 0, 1);
            AssertOpen(fixture.Gate);
        }
    }

    [Fact]
    public void Monitor_does_not_enable_the_unfinished_installer()
    {
        _ = Binding.Require();
        var readiness = typeof(WebSettingsService).GetProperty("SafeUpdateInstallationReady", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(readiness);
        Assert.False((bool)readiness.GetValue(null));
    }

    private static async Task<VerifiedUpdate> ResultAfter(Task completion)
    {
        await completion.ConfigureAwait(false);
        return Update();
    }

    private static VerifiedUpdate Update() => new(new Version(9, 0, 0), new Version(1, 4, 19), "stable",
        new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/keyboard.wtf-setup.exe"),
        new string('a', 64), 1024, "Synthetic verified tuple", UpdateManifestVerifier.CurrentKeyId);

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
        // Declaration order is deliberate: no test resource/worker precedes binding.
        private readonly Binding _binding = Binding.Require();
        private readonly ConcurrentBag<TaskCompletionSource<bool>> _signals = new();
        private readonly ConcurrentBag<Task> _originals = new();
        private readonly ConcurrentBag<Task> _dependencies = new();
        private readonly ConcurrentQueue<Action> _queued = new();
        private readonly SemaphoreSlim _posted = new(0);
        private readonly object _monitor;
        private int _checks;
        private int _posts;
        private int _notices;
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal Func<bool> Eligibility { get; set; } = () => true;
        internal Func<string> Channel { get; set; } = () => "stable";
        internal Func<string, CancellationToken, Task<VerifiedUpdate>> Check { get; set; } = (_, _) => Task.FromResult(Update());
        internal Action<Action> Post { get; set; }
        internal Func<VerifiedUpdate, bool> Notify { get; set; } = _ => true;
        internal int Checks => Volatile.Read(ref _checks);
        internal int Posts => Volatile.Read(ref _posts);
        internal int Notices => Volatile.Read(ref _notices);

        internal Fixture()
        {
            Post = Enqueue;
            _monitor = _binding.Create([Gate, (Func<bool>)(() => Eligibility()), (Func<string>)(() => Channel()),
                (Func<string, CancellationToken, Task<VerifiedUpdate>>)((channel, token) =>
                {
                    Interlocked.Increment(ref _checks);
                    var task = Check(channel, token);
                    if (task is not null) _dependencies.Add(task);
                    return task;
                }),
                (Action<Action>)(action =>
                {
                    Interlocked.Increment(ref _posts);
                    Post(action);
                }),
                (Func<VerifiedUpdate, bool>)(update =>
                {
                    Interlocked.Increment(ref _notices);
                    return Notify(update);
                })]);
        }

        internal Task Start()
        {
            var original = _binding.Start(_monitor);
            _originals.Add(original);
            return original;
        }

        internal Task Stop()
        {
            var original = _binding.StopMonitor(_monitor);
            _originals.Add(original);
            return original;
        }

        internal void DisposeHost() => ((IDisposable)_monitor).Dispose();
        internal void Track(Task task) => _originals.Add(task);
        internal void Enqueue(Action action) { _queued.Enqueue(action); _posted.Release(); }
        internal async Task<Action> TakePostedAsync()
        {
            Assert.True(await _posted.WaitAsync(TestTimeout));
            Assert.True(_queued.TryDequeue(out var action));
            return action;
        }

        internal void RunAllQueued()
        {
            while (_queued.TryDequeue(out var action)) action();
        }

        internal TaskCompletionSource<bool> Signal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals.Add(signal);
            return signal;
        }

        public async ValueTask DisposeAsync()
        {
            // Stop is nonblocking; release held synthetic bodies and join their
            // ORIGINAL completions before touching any fixture resource.
            var stopped = Stop();
            foreach (var signal in _signals) signal.TrySetResult(true);
            RunAllQueued();
            var originals = Task.WhenAll(_originals);
            try { await originals.WaitAsync(TestTimeout); }
            catch (Exception) when (originals.IsCompleted) { /* Completed asserted failures are safe to observe. */ }
            var dependencies = Task.WhenAll(_dependencies);
            try { await dependencies.WaitAsync(TestTimeout); }
            catch (Exception) when (dependencies.IsCompleted) { /* Never retire an unfinished callback timeout. */ }
            await stopped.WaitAsync(TestTimeout);
            DisposeHost();
            _posted.Dispose();
        }
    }

    private sealed class Binding
    {
        internal Type Type { get; }
        internal ConstructorInfo Constructor { get; }
        internal MethodInfo CheckOnce { get; }
        internal MethodInfo Stop { get; }

        private Binding()
        {
            Type = typeof(UpdateWorkAdmissionGate).Assembly.GetType("KeyboardWtf.Services.Updates.PublicUpdateNotificationMonitor");
            Assert.True(Type is not null, "The public notification monitor lifecycle seam is missing.");
            Constructor = Type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(UpdateWorkAdmissionGate), typeof(Func<bool>), typeof(Func<string>),
                    typeof(Func<string, CancellationToken, Task<VerifiedUpdate>>), typeof(Action<Action>),
                    typeof(Func<VerifiedUpdate, bool>)], null);
            Assert.NotNull(Constructor);
            CheckOnce = Type.GetMethod("CheckOnceAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, System.Type.EmptyTypes, null);
            Stop = Type.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, System.Type.EmptyTypes, null);
            Assert.NotNull(CheckOnce);
            Assert.NotNull(Stop);
            Assert.Equal(typeof(Task), CheckOnce.ReturnType);
            Assert.Equal(typeof(Task), Stop.ReturnType);
        }

        internal static Binding Require() => new();
        internal object Create(object[] arguments) => Invoke(() => Constructor.Invoke(arguments));
        internal Task Start(object monitor) => Assert.IsAssignableFrom<Task>(Invoke(() => CheckOnce.Invoke(monitor, null)));
        internal Task StopMonitor(object monitor) => Assert.IsAssignableFrom<Task>(Invoke(() => Stop.Invoke(monitor, null)));

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
