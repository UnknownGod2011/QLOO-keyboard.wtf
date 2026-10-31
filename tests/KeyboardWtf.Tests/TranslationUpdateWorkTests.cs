using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Actual translation-operation/capture admission boundaries, synthetic delegates
// only. No provider, microphone, Windows clipboard, installer or desktop startup.
// The detached CTS-disposal continuation is deliberately NOT claimed by this batch.
[Collection("Live event integration")]
public sealed class TranslationUpdateWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_or_committed_gate_rejects_start_synchronously_before_any_effect(bool commit)
    {
        var gate = new UpdateWorkAdmissionGate();
        using var operation = Create(gate);
        Assert.True(gate.TryFreeze(out var frozen));
        if (commit) frozen.Commit();
        var events = new ConcurrentQueue<string>();
        var failure = Record.Exception(() => Start(operation, events));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(events);
        Assert.True(operation.Completion.IsCompletedSuccessfully);
        frozen.Dispose();
        if (commit)
            Assert.False(gate.TryAcquire(out _));
        else
        {
            // A refusal must not consume the operation's existing one-start guard.
            Start(operation, events);
            await Finish(operation);
            Assert.Equal(new[] { "Translating...", "provider", "Copying...", "copy", "clear", "announce" }, events);
            AssertIdle(gate);
        }
    }

    [Fact]
    public async Task Duplicate_start_releases_only_its_attempt_not_the_existing_worker()
    {
        var gate = new UpdateWorkAdmissionGate();
        using var operation = Create(gate);
        var entered = Signal();
        var release = Signal();
        var copies = 0;
        var announcements = 0;
        operation.Start("source", async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return "translated";
        }, _ => { Interlocked.Increment(ref copies); return Success(); }, _ =>
        { Interlocked.Increment(ref announcements); return Task.CompletedTask; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(() => operation.Start("other",
                (_, _, _) => throw new InvalidOperationException("Duplicate provider must not run"),
                _ => throw new InvalidOperationException("Duplicate copy must not run"),
                _ => throw new InvalidOperationException("Duplicate announcement must not run")));
            AssertBusy(gate);
            Assert.False(operation.Completion.IsCompleted);
        }
        finally { release.TrySetResult(true); await Finish(operation); }
        Assert.Equal(1, copies);
        Assert.Equal(1, announcements);
        AssertIdle(gate);
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("session")]
    [InlineData("dispose")]
    public async Task Cancellation_clears_feedback_but_ignored_provider_retains_owner_until_worker_finishes(string kind)
    {
        var gate = new UpdateWorkAdmissionGate();
        using var session = new CancellationTokenSource();
        using var operation = Create(gate, session.Token);
        var entered = Signal();
        var release = Signal();
        var states = new ConcurrentQueue<string>();
        var copies = 0;
        var announcements = 0;
        operation.Start("source", async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task; // Intentionally ignores cancellation, like a late external response.
            return "late translated output";
        }, _ => { Interlocked.Increment(ref copies); return Success(); }, _ =>
        { Interlocked.Increment(ref announcements); return Task.CompletedTask; }, states.Enqueue);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (kind)
            {
                case "explicit": Assert.True(operation.Cancel()); break;
                case "session": session.Cancel(); break;
                case "dispose": operation.Dispose(); break;
            }
            Assert.False(operation.IsRunning);
            Assert.True(operation.IsCancelled);
            Assert.Null(states.Last());
            Assert.False(operation.Completion.IsCompleted);
            AssertBusy(gate);
        }
        finally { release.TrySetResult(true); await Finish(operation); }
        Assert.Equal(0, copies);
        Assert.Equal(0, announcements);
        Assert.DoesNotContain("Copying...", states);
        AssertIdle(gate);
    }

    [Fact]
    public async Task Verified_copy_and_cleared_status_do_not_release_a_blocked_announcement()
    {
        var gate = new UpdateWorkAdmissionGate();
        using var operation = Create(gate);
        var entered = Signal();
        var release = Signal();
        var states = new ConcurrentQueue<string>();
        var copies = 0;
        operation.Start("source", (_, _, _) => Task.FromResult("translated"), _ =>
        { Interlocked.Increment(ref copies); return Success(); }, async _ =>
        { entered.TrySetResult(true); await release.Task; }, states.Enqueue);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsRunning);
            Assert.True((bool)operation.Receipt()["completed"]);
            Assert.True((bool)operation.Receipt()["clipboard_verified"]);
            Assert.Null(states.Last());
            Assert.Equal(1, copies);
            Assert.False(operation.Completion.IsCompleted);
            AssertBusy(gate);
        }
        finally { release.TrySetResult(true); await Finish(operation); }
        AssertIdle(gate);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("empty")]
    [InlineData("copy_throw")]
    [InlineData("copy_refused")]
    [InlineData("announce")]
    public async Task Existing_failure_paths_finish_and_release_without_changing_result_semantics(string failure)
    {
        var gate = new UpdateWorkAdmissionGate();
        using var operation = Create(gate);
        var observations = new ConcurrentQueue<bool>();
        var states = new ConcurrentQueue<string>();
        var copies = 0;
        var announcements = 0;
        operation.Start("source", (_, _, _) =>
        {
            observations.Enqueue(IsBusy(gate));
            if (failure == "provider") throw new IOException("Synthetic provider failure");
            return Task.FromResult(failure == "empty" ? "" : "translated");
        }, _ =>
        {
            observations.Enqueue(IsBusy(gate));
            Interlocked.Increment(ref copies);
            if (failure == "copy_throw") throw new IOException("Synthetic clipboard failure");
            return failure == "copy_refused"
                ? new() { ["ok"] = false, ["clipboard_verified"] = false }
                : Success();
        }, _ =>
        {
            observations.Enqueue(IsBusy(gate));
            Interlocked.Increment(ref announcements);
            if (failure == "announce") throw new IOException("Synthetic announcement failure");
            return Task.CompletedTask;
        }, states.Enqueue);

        await Finish(operation);
        Assert.NotEmpty(observations);
        Assert.All(observations, value => Assert.True(value)); // Outside intentional exception swallowing.
        Assert.Equal(failure == "announce", (bool)operation.Receipt()["ok"]);
        Assert.Equal(failure is "provider" or "empty" ? 0 : 1, copies);
        Assert.Equal(1, announcements);
        Assert.Null(states.Last());
        AssertIdle(gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_source_is_rejected_but_large_source_is_processed_under_one_update_owner(bool oversized)
    {
        var gate = new UpdateWorkAdmissionGate();
        using var operation = Create(gate);
        var providers = 0;
        var copies = 0;
        var announcements = 0;
        operation.Start(oversized ? new string('x', 50_001) : "", (_, _, _) =>
        { Interlocked.Increment(ref providers); return Task.FromResult("translated"); }, _ =>
        { Interlocked.Increment(ref copies); return Success(); }, _ =>
        { Interlocked.Increment(ref announcements); return Task.CompletedTask; });
        await Finish(operation);
        Assert.Equal(oversized ? 2 : 0, providers);
        Assert.Equal(oversized ? 1 : 0, copies);
        Assert.Equal(1, announcements);
        Assert.Equal(oversized, (bool)operation.Receipt()["ok"]);
        AssertIdle(gate);
    }

    [Fact]
    public async Task Replaced_cancelled_operation_keeps_its_independent_owner_after_replacement_finishes()
    {
        var gate = new UpdateWorkAdmissionGate();
        using var previous = Create(gate);
        using var replacement = Create(gate);
        var entered = Signal();
        var release = Signal();
        var oldCopies = 0;
        previous.Start("old", async (_, _, _) =>
        { entered.TrySetResult(true); await release.Task; return "late old output"; }, _ =>
        { Interlocked.Increment(ref oldCopies); return Success(); }, _ => Task.CompletedTask);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            previous.Dispose();
            Assert.False(previous.IsRunning);
            var events = new ConcurrentQueue<string>();
            Start(replacement, events);
            await Finish(replacement);
            Assert.Equal(1, events.Count(value => value == "copy"));
            Assert.False(previous.Completion.IsCompleted);
            AssertBusy(gate);
        }
        finally { release.TrySetResult(true); await Finish(previous); }
        Assert.Equal(0, oldCopies);
        AssertIdle(gate);
    }

    [Fact]
    public async Task Fifty_immediate_operations_match_the_uninstrumented_executor_with_one_dispatch_copy_and_announcement()
    {
        for (var cycle = 0; cycle < 50; cycle++)
        {
            var gate = new UpdateWorkAdmissionGate();
            using var baseline = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
            using var tracked = Create(gate);
            var before = new ConcurrentQueue<string>();
            var after = new ConcurrentQueue<string>();
            Start(baseline, before);
            Start(tracked, after);
            await Task.WhenAll(Finish(baseline), Finish(tracked));
            Assert.Equal(new[] { "Translating...", "provider", "Copying...", "copy", "clear", "announce" }, after);
            Assert.Equal(before.ToArray(), after.ToArray());
            Assert.Equal(baseline.Receipt()["ok"], tracked.Receipt()["ok"]);
            Assert.Equal(baseline.Receipt()["clipboard_verified"], tracked.Receipt()["clipboard_verified"]);
            AssertIdle(gate);
        }
        // This is lifecycle/control equivalence, not a real-provider latency benchmark.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_registry_rejects_frozen_admission_before_selection_capture_or_ui_mutation(bool commit)
    {
        var gate = new UpdateWorkAdmissionGate();
        var captures = 0;
        var commands = Registry(gate, _ =>
        {
            Interlocked.Increment(ref captures);
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false });
        });
        var phase = KeyboardWtfState.UiPhase;
        var title = KeyboardWtfState.UiTitle;
        var detail = KeyboardWtfState.UiDetail;
        var longTask = KeyboardWtfState.LongTaskDetail;
        Assert.True(gate.TryFreeze(out var frozen));
        try
        {
            if (commit) frozen.Commit();
            var result = await StartRegistry(commands, CancellationToken.None);
            Assert.False((bool)result["ok"]);
            Assert.Contains("update", result["message"].ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wait", result["message"].ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(result.ContainsKey("operation_id"));
            Assert.Equal(0, captures);
            Assert.Null(SelectedOperation(commands));
            Assert.Equal(phase, KeyboardWtfState.UiPhase);
            Assert.Equal(title, KeyboardWtfState.UiTitle);
            Assert.Equal(detail, KeyboardWtfState.UiDetail);
            Assert.Equal(longTask, KeyboardWtfState.LongTaskDetail);
        }
        finally { frozen.Dispose(); }
        if (!commit) AssertIdle(gate);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task Registry_capture_parent_owns_awaited_capture_through_failure_or_cancellation(string outcome)
    {
        var gate = new UpdateWorkAdmissionGate();
        var entered = Signal();
        var release = Signal();
        using var cancellation = new CancellationTokenSource();
        var commands = Registry(gate, async _ =>
        {
            entered.TrySetResult(true);
            await release.Task; // Simulate capture completion/cleanup ignoring cancellation.
            if (outcome == "throw") throw new IOException("Synthetic capture failure");
            return new Dictionary<string, object> { ["ok"] = false, ["message"] = "No current selection" };
        });
        var phase = KeyboardWtfState.UiPhase;
        var title = KeyboardWtfState.UiTitle;
        var detail = KeyboardWtfState.UiDetail;
        var pending = StartRegistry(commands, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (outcome == "cancel") cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            AssertBusy(gate);
        }
        finally
        {
            release.TrySetResult(true);
            // Keep original capture exception/cancellation semantics; this owner is passive.
            var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            KeyboardWtfState.SetUi(phase, title, detail);
            if (outcome == "throw") Assert.IsType<IOException>(error);
            else if (outcome == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.Null(error);
        }
        if (outcome == "failure") Assert.False((bool)(await pending)["ok"]);
        Assert.Null(SelectedOperation(commands));
        AssertIdle(gate);
    }

    private static SelectedTextTranslationOperation Create(UpdateWorkAdmissionGate gate,
        CancellationToken session = default)
    {
        // Reflection keeps honest behavioral red compilable before the optional seam exists.
        var constructor = typeof(SelectedTextTranslationOperation).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            types: new[] { typeof(string), typeof(string), typeof(CancellationToken), typeof(UpdateWorkAdmissionGate) },
            modifiers: null);
        Assert.True(constructor is not null, "The optional operation-owned update gate is not implemented.");
        Assert.True(constructor.GetParameters()[3].IsOptional);
        Assert.Null(constructor.GetParameters()[3].DefaultValue);
        return (SelectedTextTranslationOperation)constructor.Invoke(new object[] { "request", "French", session, gate });
    }

    private static CommandRegistry Registry(UpdateWorkAdmissionGate gate,
        Func<CancellationToken, Task<Dictionary<string, object>>> capture)
    {
        var field = typeof(KeyboardWtfApp).GetField("<UpdateWorkAdmission>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var app = (KeyboardWtfApp)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardWtfApp));
        field.SetValue(app, gate);
        // Null services are deliberate: rejected admission/failed capture cannot use Live,
        // clipboard or notifications. No real app constructor or OS registration occurs.
        return new CommandRegistry(app, null, null, null, null,
            currentSelectionContextId: () => "synthetic-foreground-document", captureSelectedText: capture);
    }

    private static async Task<Dictionary<string, object>> StartRegistry(CommandRegistry commands, CancellationToken token)
    {
        var method = typeof(CommandRegistry).GetMethod("StartSelectedTranslationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try
        {
            return await ((Task<Dictionary<string, object>>)method.Invoke(commands,
                new object[] { "Translate the selected text to French and copy it to clipboard.", "French", token }));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static object SelectedOperation(CommandRegistry commands) => typeof(CommandRegistry)
        .GetField("_selectedTranslation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(commands);

    private static void Start(SelectedTextTranslationOperation operation, ConcurrentQueue<string> events) =>
        operation.Start("source", (_, _, _) =>
        { events.Enqueue("provider"); return Task.FromResult("translated"); }, _ =>
        { events.Enqueue("copy"); return Success(); }, _ =>
        { events.Enqueue("announce"); return Task.CompletedTask; }, state => events.Enqueue(state ?? "clear"));

    private static Dictionary<string, object> Success() => new()
    { ["ok"] = true, ["clipboard_verified"] = true };
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Finish(SelectedTextTranslationOperation operation) => operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
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
}
