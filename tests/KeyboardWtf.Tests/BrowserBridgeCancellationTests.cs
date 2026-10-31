using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

// These tests assert sub-idle-window delivery. Run in the existing isolated
// timing collection so unrelated synchronous integration tests cannot starve
// xUnit's continuation scheduler and masquerade as a lost queue wake-up.
[Collection("Live event integration")]
public sealed class BrowserBridgeCancellationTests
{
    [Fact]
    public async Task A_waiting_poll_cannot_restore_a_newer_revocation_from_its_old_heartbeat()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        var polling = ProcessAsync(bridge, "poll", waitForCommand: true);
        Assert.False(polling.IsCompleted);
        Process(bridge, "heartbeat", supportsAuthority: false);
        Assert.Equal(JsonValueKind.Null, (await polling).GetProperty("command").ValueKind);
        Assert.False(bridge.Status.CommandAuthoritySupported);
    }

    [Fact]
    public async Task Connection_cancellation_stops_a_waiting_pull_without_leaking_a_waiter()
    {
        var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        var method = typeof(BrowserHelperBridge).GetMethod("ProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var cancel = new CancellationTokenSource();
        var polling = (Task<string>)method.Invoke(bridge, new object[] {
            JsonSerializer.Serialize(new { type = "poll", waitForCommand = true, helper = new {
                extensionId = "gojdebdikepkpapmnggeljneamjdigbh", serviceWorkerVersion = "2.1.21",
                commandAuthoritySupported = true, sessionId = "test-session" } }), cancel.Token })!;
        Assert.False(polling.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling);
        bridge.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_poll_started_before_the_next_tool_is_queued_wakes_on_enqueue(int count)
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        var ids = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            // Async processing reaches its empty-queue await synchronously.
            // Do not occupy a thread-pool thread or rely on wall-clock sleeps.
            var signal = (TaskCompletionSource)typeof(BrowserHelperBridge)
                .GetField("_commandAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(bridge)!;
            var polling = ProcessAsync(bridge, "poll", waitForCommand: true);
            Assert.False(polling.IsCompleted);
            var request = new BrowserActionRequest { TaskId = "sequential", Type = "open_direct_tab" };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var execution = bridge.ExecuteAsync(request, cancellation.Token);
            Assert.True(signal.Task.IsCompletedSuccessfully); // enqueue wakes the actual held pull
            var response = await polling.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(JsonValueKind.Object, response.GetProperty("command").ValueKind);
            Assert.Equal(request.CommandId, response.GetProperty("command").GetProperty("commandId").GetString());
            Assert.True(ids.Add(request.CommandId));
            Process(bridge, "command_result", request, new { ok = true });
            Assert.True((await execution).Ok);
            Assert.False(Process(bridge, "command_check", request).GetProperty("commandAuthorized").GetBoolean());
        }
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll").GetProperty("command").ValueKind);
    }

    [Fact]
    public void Legacy_helper_is_connected_but_never_advertised_as_action_ready()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello", supportsAuthority: false);
        Assert.True(bridge.Status.Connected);
        Assert.False(bridge.Status.CommandChannelReady);
        Assert.Contains("Update the Chrome Helper", bridge.Status.LastError);

        Process(bridge, "heartbeat");
        Assert.True(bridge.Status.CommandChannelReady);
        Assert.Empty(bridge.Status.LastError);

        Process(bridge, "heartbeat", supportsAuthority: false);
        Assert.False(bridge.Status.CommandChannelReady);
        Assert.Contains("Update the Chrome Helper", bridge.Status.LastError);
    }

    [Fact]
    public async Task Legacy_helper_fails_before_queuing_a_mutation_with_an_actionable_update_message()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello", supportsAuthority: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExecuteAsync(
            new BrowserActionRequest { TaskId = "task", Type = "click" }, CancellationToken.None));
        Assert.Contains("Update the Chrome Helper", error.Message);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll", supportsAuthority: false).GetProperty("command").ValueKind);
    }

    [Fact]
    public async Task Capability_loss_cannot_take_or_complete_an_existing_command()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        var action = new BrowserActionRequest { TaskId = "task", Type = "observe" };
        var pending = bridge.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal(JsonValueKind.Null,
            Process(bridge, "poll", supportsAuthority: false).GetProperty("command").ValueKind);
        Assert.False(Process(bridge, "command_check", action, supportsAuthority: false)
            .GetProperty("commandAuthorized").GetBoolean());
        Process(bridge, "command_result", action, new { ok = true }, supportsAuthority: false);
        Assert.False(pending.IsCompleted);

        Assert.Equal(action.CommandId,
            Process(bridge, "poll").GetProperty("command").GetProperty("commandId").GetString());
        Process(bridge, "command_result", action, new { ok = true });
        Assert.True((await pending).Ok);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll").GetProperty("command").ValueKind);
    }

    [Fact]
    public async Task Cancelled_queued_action_is_never_returned_to_the_helper_after_it_recovers()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var action = bridge.ExecuteAsync(new BrowserActionRequest
        {
            TaskId = "old-task", Type = "click", ControlId = "old-field",
        }, cancellation.Token);
        Assert.False(action.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll").GetProperty("command").ValueKind);
    }

    [Fact]
    public async Task Dispatched_command_loses_mutation_authority_when_cancelled()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var action = new BrowserActionRequest { TaskId = "task", Type = "click", ControlId = "field" };
        var pending = bridge.ExecuteAsync(action, cancellation.Token);
        var returned = Process(bridge, "poll").GetProperty("command");
        Assert.True(returned.GetProperty("requiresAuthorityCheck").GetBoolean());
        Assert.True(Process(bridge, "command_check", action).GetProperty("commandAuthorized").GetBoolean());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Process(bridge, "command_check", action).GetProperty("commandAuthorized").GetBoolean());
    }

    [Fact]
    public async Task Completed_command_cannot_regain_authority_and_heartbeat_does_not_take_next_action()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        var action = new BrowserActionRequest { TaskId = "task", Type = "observe" };
        var pending = bridge.ExecuteAsync(action, CancellationToken.None);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "heartbeat").GetProperty("command").ValueKind);
        Assert.Equal(action.CommandId, Process(bridge, "poll").GetProperty("command").GetProperty("commandId").GetString());
        Process(bridge, "command_result", action, new { ok = true });
        await pending;
        Assert.False(Process(bridge, "command_check", action).GetProperty("commandAuthorized").GetBoolean());
    }

    [Fact]
    public async Task Discarding_old_work_keeps_the_next_live_request_and_its_result()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var old = bridge.ExecuteAsync(new BrowserActionRequest { TaskId = "old", Type = "click" }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        var fresh = new BrowserActionRequest { TaskId = "new", Type = "observe" };
        var result = bridge.ExecuteAsync(fresh, CancellationToken.None);
        var returned = Process(bridge, "poll").GetProperty("command");
        Assert.Equal(fresh.CommandId, returned.GetProperty("commandId").GetString());
        Process(bridge, "command_result", fresh, new { ok = true });
        Assert.True((await result).Ok);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll").GetProperty("command").ValueKind);
    }

    private static JsonElement Process(BrowserHelperBridge bridge, string type,
        BrowserActionRequest action = null, object result = null, bool supportsAuthority = true,
        bool waitForCommand = false) =>
        ProcessAsync(bridge, type, action, result, supportsAuthority, waitForCommand).GetAwaiter().GetResult();

    private static async Task<JsonElement> ProcessAsync(BrowserHelperBridge bridge, string type,
        BrowserActionRequest action = null, object result = null, bool supportsAuthority = true,
        bool waitForCommand = false)
    {
        var json = JsonSerializer.Serialize(new
        {
            type, taskId = action?.TaskId, commandId = action?.CommandId, result, waitForCommand,
            helper = new
            {
                extensionId = "gojdebdikepkpapmnggeljneamjdigbh",
                serviceWorkerVersion = "2.1.21", contentScriptVersion = "2.1.21",
                activeTabFresh = true, commandChannelReady = true,
                commandAuthoritySupported = supportsAuthority,
                activeUrl = "https://example.org/", sessionId = "test-session",
            },
        });
        var method = typeof(BrowserHelperBridge).GetMethod("ProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var response = await (Task<string>)method.Invoke(bridge, new object[] { json, CancellationToken.None })!;
        using var document = JsonDocument.Parse(response);
        return document.RootElement.Clone();
    }
}
