using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class LifecyclePublicationTests
{
    [Theory]
    [InlineData("Fail", "cancelled")]
    [InlineData("Complete", "cancelled")]
    [InlineData("Fail", "completed")]
    [InlineData("Complete", "completed")]
    [InlineData("Fail", "replaced")]
    [InlineData("Complete", "replaced")]
    public void Late_terminal_callback_cannot_publish_notifications_or_change_a_finished_task(string method, string ending)
    {
        var notices = 0;
        var owner = new BrowserAutomationCoordinator(null,
            NotificationService.CreateForTesting((_, _, _) => notices++), null, null);
        var type = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(type, nonPublic: true)!;
        var originalState = ending == "cancelled" ? BrowserTaskState.Cancelled
            : ending == "completed" ? BrowserTaskState.Completed : BrowserTaskState.Executing;
        type.GetProperty("State")!.SetValue(runtime, originalState);
        typeof(BrowserAutomationCoordinator).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(owner, ending == "replaced" ? null : runtime);
        if (ending == "cancelled")
        {
            type.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Partial);
            Assert.True(owner.RequestCancel(announceOutcome: false));
        }
        var announcements = 0;
        owner.AnnouncementRequested += _ => announcements++;
        var callback = typeof(BrowserAutomationCoordinator).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        callback.Invoke(owner, method == "Complete"
            ? new[] { runtime, BrowserAutomationOutcome.Completed, "Late success", "Late evidence" }
            : new[] { runtime, (object)BrowserAutomationOutcome.Failed, "Late failure" });
        Assert.Equal(originalState, type.GetProperty("State")!.GetValue(runtime));
        Assert.Equal(0, notices);
        Assert.Equal(0, announcements);
        ((CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Theory]
    [InlineData("state-changed")]
    [InlineData("task-replaced")]
    [InlineData("intervention-replaced")]
    public void Browser_notice_keeps_its_original_task_state_and_intervention_authority(string change)
    {
        var owner = new BrowserAutomationCoordinator(null, null, null, null);
        var type = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(type, nonPublic: true)!;
        var active = typeof(BrowserAutomationCoordinator).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!;
        active.SetValue(owner, runtime);
        type.GetProperty("State")!.SetValue(runtime, BrowserTaskState.NeedsUserAction);
        type.GetProperty("Intervention")!.SetValue(runtime, new BrowserUserIntervention { Missing = "Email", Required = true });
        JarvisLifecycleAnnouncement notice = null;
        owner.AnnouncementRequested += value => notice = value;
        typeof(BrowserAutomationCoordinator).GetMethod("AnnounceOutcome", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(owner, new[] { runtime, (object)BrowserTaskState.NeedsUserAction });
        Assert.NotNull(notice);
        Assert.True(notice.CanPublish);
        Assert.Empty(notice.FallbackSpeech); // No independent TTS owner after goodbye.
        if (change == "state-changed") type.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Cancelled);
        if (change == "task-replaced") active.SetValue(owner, null);
        if (change == "intervention-replaced") type.GetProperty("Intervention")!.SetValue(runtime, new BrowserUserIntervention());
        Assert.False(notice.CanPublish);
        ((CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Fact]
    public void Partial_browser_notice_states_that_the_full_task_did_not_finish()
    {
        var owner = new BrowserAutomationCoordinator(null, null, null, null);
        var type = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(type, nonPublic: true)!;
        typeof(BrowserAutomationCoordinator).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(owner, runtime);
        type.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Partial);
        type.GetProperty("LastError")!.SetValue(runtime,
            "A final field check needs an exact field identity and a state type appropriate to its control role.");
        JarvisLifecycleAnnouncement notice = null;
        owner.AnnouncementRequested += value => notice = value;

        typeof(BrowserAutomationCoordinator).GetMethod("AnnounceOutcome", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(owner, new[] { runtime, (object)BrowserTaskState.Partial });

        Assert.NotNull(notice);
        Assert.Contains("stopped before the full result", notice.Detail);
        Assert.Contains("final field check", notice.Detail);
        Assert.Empty(notice.FallbackSpeech);
        ((CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Fact]
    public async Task Current_notice_publishes_once_on_its_originating_socket()
    {
        await using var run = await Connection.CreateAsync();
        Assert.True(await run.Live.TryAnnounceLifecycleAsync(Notice()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new byte[8192];
        var received = await run.Server.ReceiveAsync(bytes, timeout.Token);
        var text = Encoding.UTF8.GetString(bytes, 0, received.Count);
        Assert.Contains("KEYBOARD.WTF_BACKGROUND_LIFECYCLE_EVENT", text);
        Assert.Contains("Captured test task failed", text);
    }

    [Theory]
    [InlineData("new-user-turn")]
    [InlineData("task-invalidated")]
    [InlineData("socket-replaced")]
    [InlineData("session-ended")]
    public async Task Waiting_notice_cannot_outlive_its_authority(string change)
    {
        await using var run = await Connection.CreateAsync();
        var valid = true;
        var sendLock = run.Field<SemaphoreSlim>("_sendLock");
        await sendLock.WaitAsync();
        var oldTurn = run.Field<TaskCompletionSource<bool>>("_turnComplete");
        var delivery = run.Live.TryAnnounceLifecycleAsync(Notice(() => valid));
        Assert.False(delivery.IsCompleted);
        Assert.Same(oldTurn, run.Field<TaskCompletionSource<bool>>("_turnComplete"));
        switch (change)
        {
            case "new-user-turn": run.Set("_userTranscriptRevision", 1L); break;
            case "task-invalidated": valid = false; break;
            case "socket-replaced": run.Set("_socket", null); break;
            case "session-ended": run.Session.Cancel(); break;
        }
        sendLock.Release();
        Assert.False(await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(oldTurn, run.Field<TaskCompletionSource<bool>>("_turnComplete"));
        Assert.True(oldTurn.Task.IsCompletedSuccessfully);
        Assert.False(run.HasIncomingBytes);
    }

    [Fact]
    public async Task Background_notice_does_not_break_an_unfinished_user_turn_after_grace_expires()
    {
        await using var run = await Connection.CreateAsync();
        var pendingTurn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", pendingTurn);
        Assert.False(await run.Live.TryAnnounceLifecycleAsync(Notice()).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(pendingTurn.Task.IsCompleted);
        Assert.False(run.HasIncomingBytes);
    }

    [Theory]
    [InlineData("selected_text_translation")]
    [InlineData("connector")]
    public async Task Fast_owned_completion_cannot_overtake_its_tool_receipt(string source)
    {
        await using var run = await Connection.CreateAsync();
        var receiptEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = new LiveToolCallDispatcher(run.Session.Token,
            (_, _, _) => Task.FromResult(new Dictionary<string, object>
            {
                ["ok"] = true, ["started"] = true, ["completed"] = true,
                ["announcement_owner"] = "selected_text_translation_lifecycle",
            }), async (build, onSent, token) =>
            {
                receiptEntered.TrySetResult(true);
                await releaseReceipt.Task.WaitAsync(token);
                build();
                onSent();
            });
        run.Set("_toolCalls", dispatcher);
        dispatcher.Enqueue(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            functionCalls = new[] { new { id = "translation", name = "get_selected_text", args = new { } } },
        }), new ToolAttemptGuard());
        await receiptEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var terminal = run.Live.TryAnnounceLifecycleAsync(new()
        {
            Source = source, State = "Completed", Detail = "Copied and verified.",
        });
        try
        {
            Assert.False(terminal.IsCompleted);
            Assert.False(run.HasIncomingBytes);
        }
        finally { releaseReceipt.TrySetResult(true); }
        Assert.True(await terminal.WaitAsync(TimeSpan.FromSeconds(3)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var bytes = new byte[8192];
        var received = await run.Server.ReceiveAsync(bytes, timeout.Token);
        Assert.Contains("Copied and verified.", Encoding.UTF8.GetString(bytes, 0, received.Count));
    }

    [Fact]
    public async Task Connector_receipt_waits_for_actual_reply_end_beyond_ordinary_notice_grace()
    {
        await using var run = await Connection.CreateAsync();
        var original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", original);
        var notice = run.Live.TryAnnounceLifecycleAsync(new() { Source = "connector", State = "settled", Detail = "Retained result ready." });
        await Task.Delay(TimeSpan.FromSeconds(8.3));
        Assert.False(notice.IsCompleted);
        Assert.False(run.HasIncomingBytes);
        original.TrySetResult(true);
        Assert.True(await notice.WaitAsync(TimeSpan.FromSeconds(3)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var bytes = new byte[8192];
        var result = await run.Server.ReceiveAsync(bytes, timeout.Token);
        Assert.Contains("Retained result ready", Encoding.UTF8.GetString(bytes, 0, result.Count));
    }

    [Fact]
    public async Task Settled_connector_can_replace_only_its_unheard_owned_reply_without_waiting_for_playback_time()
    {
        await using var run = await Connection.CreateAsync();
        var original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", original);
        run.Live.SetConnectorReplyOwnership(() => true, _ => { });
        var type = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
        var diagnostics = Activator.CreateInstance(type, "held-connector-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
        type.GetProperty("UsesPersonalToolProtocol")!.SetValue(diagnostics, true);
        run.Set("_providerDiagnostics", diagnostics);
        ((TaskCompletionSource<bool>)type.GetField("ReplyResponseStarted")!.GetValue(diagnostics)!).TrySetResult(true); // Existing unheard response has started.
        var notice = run.Live.TryAnnounceLifecycleAsync(new() { Source = "connector", State = "settled", Detail = "Exact opening completed.", OwnsCurrentReply = () => true });
        Assert.True(await notice.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(original.Task.IsCompleted); // No fabricated completion or playback cancellation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var bytes = new byte[8192];
        var result = await run.Server.ReceiveAsync(bytes, timeout.Token);
        Assert.Contains("Exact opening completed", Encoding.UTF8.GetString(bytes, 0, result.Count));
    }

    [Theory]
    [InlineData("audible-output")]
    [InlineData("queued-audio")]
    [InlineData("new-speech")]
    [InlineData("unowned")]
    [InlineData("managed")]
    public async Task Connector_completion_cannot_replace_audible_or_unowned_turn(string boundary)
    {
        await using var run = await Connection.CreateAsync();
        var original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", original);
        run.Live.SetConnectorReplyOwnership(() => boundary != "unowned", _ => { });
        var type = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
        var diagnostics = Activator.CreateInstance(type, "held-connector-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
        type.GetProperty("UsesPersonalToolProtocol")!.SetValue(diagnostics, boundary != "managed");
        type.GetField("ConnectorSpeechActive")!.SetValue(diagnostics, boundary == "new-speech");
        run.Set("_providerDiagnostics", diagnostics);
        ((TaskCompletionSource<bool>)type.GetField("ReplyResponseStarted")!.GetValue(diagnostics)!).TrySetResult(true); // Existing unheard response has started.
        run.Set("_assistantOutputSinceLastTool", boundary == "audible-output");
        if (boundary == "queued-audio")
        {
            var buffer = (NAudio.Wave.BufferedWaveProvider)typeof(GeminiLiveConversationService)
                .GetMethod("CreatePlaybackBuffer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            buffer.AddSamples(new byte[480], 0, 480);
            run.Set("_playbackBuffer", buffer);
        }
        var notice = run.Live.TryAnnounceLifecycleAsync(new() { Source = "connector", State = "settled", Detail = "Exact opening completed.", OwnsCurrentReply = () => true });
        await Task.Delay(100);
        Assert.False(notice.IsCompleted);
        Assert.False(run.HasIncomingBytes);
        original.TrySetResult(true);
        Assert.True(await notice.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData("new-user-turn")]
    [InlineData("task-invalidated")]
    [InlineData("new-reply")]
    [InlineData("new-speech")]
    [InlineData("audible-output")]
    [InlineData("owner-released")]
    [InlineData("different-task")]
    public async Task Unheard_connector_replacement_rechecks_authority_under_send_lock(string change)
    {
        await using var run = await Connection.CreateAsync();
        var valid = true; var owned = true; var sameTask = true;
        var original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", original);
        run.Live.SetConnectorReplyOwnership(() => owned, _ => { });
        var type = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
        var diagnostics = Activator.CreateInstance(type, "held-connector-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
        type.GetProperty("UsesPersonalToolProtocol")!.SetValue(diagnostics, true);
        run.Set("_providerDiagnostics", diagnostics);
        ((TaskCompletionSource<bool>)type.GetField("ReplyResponseStarted")!.GetValue(diagnostics)!).TrySetResult(true); // Existing unheard response has started.
        var sendLock = run.Field<SemaphoreSlim>("_sendLock");
        await sendLock.WaitAsync();
        var notice = run.Live.TryAnnounceLifecycleAsync(new()
            { Source = "connector", State = "settled", Detail = "Exact opening completed.", IsCurrent = () => valid, OwnsCurrentReply = () => sameTask });
        Assert.False(notice.IsCompleted);
        switch (change)
        {
            case "new-user-turn": run.Set("_userTranscriptRevision", 1L); break;
            case "task-invalidated": valid = false; break;
            case "new-reply": type.GetMethod("BeginReply")!.Invoke(diagnostics, null); break;
            case "new-speech": type.GetField("ConnectorSpeechActive")!.SetValue(diagnostics, true); break;
            case "audible-output": run.Set("_assistantOutputSinceLastTool", true); break;
            case "owner-released": owned = false; break;
            case "different-task": sameTask = false; break;
        }
        sendLock.Release();
        Assert.False(await notice.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(original.Task.IsCompleted);
        Assert.False(run.HasIncomingBytes);
    }

    [Theory]
    [InlineData("new-speech")]
    [InlineData("new-reply")]
    public async Task Completed_captured_turn_does_not_allow_connector_notice_to_interrupt_new_input_under_send_lock(string change)
    {
        await using var run = await Connection.CreateAsync();
        var original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", original);
        run.Live.SetConnectorReplyOwnership(() => true, _ => { });
        var type = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
        var diagnostics = Activator.CreateInstance(type, "held-connector-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
        type.GetProperty("UsesPersonalToolProtocol")!.SetValue(diagnostics, true);
        run.Set("_providerDiagnostics", diagnostics);
        ((TaskCompletionSource<bool>)type.GetField("ReplyResponseStarted")!.GetValue(diagnostics)!).TrySetResult(true); // Existing unheard response has started.
        var sendLock = run.Field<SemaphoreSlim>("_sendLock");
        await sendLock.WaitAsync();
        var notice = run.Live.TryAnnounceLifecycleAsync(new()
        {
            Source = "connector", State = "settled", Detail = "Exact opening completed.",
            OwnsCurrentReply = () => true
        });
        Assert.False(notice.IsCompleted);
        // The older output can finish before the new input has produced any
        // transcription. Its completion must not bypass the input/generation
        // recheck while the pending connector notice waits for the send lock.
        original.TrySetResult(true);
        if (change == "new-speech") type.GetField("ConnectorSpeechActive")!.SetValue(diagnostics, true);
        else type.GetMethod("BeginReply")!.Invoke(diagnostics, null);
        sendLock.Release();
        Assert.False(await notice.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(original.Task.IsCompleted);
        Assert.False(run.HasIncomingBytes);
    }

    [Theory]
    [InlineData("Close the first two tabs.")]
    [InlineData("Close the last two tabs.")]
    [InlineData("Close the second and third tabs.")]
    public async Task Explicit_text_tab_close_keeps_existing_immediate_dispatch_without_ASR(string request)
    {
        await using var run = await Connection.CreateAsync();
        using var replay = new TabReplay(run);
        // The desktop has already observed the submitted command. Sending it to
        // Live does not produce microphone/ASR state or a second user request.
        replay.SubmittedRequest = request;
        await run.Live.SendTextTurnAsync(request);
        Assert.Equal(request, await ReadClientTextAsync(run));
        Assert.Equal("", run.Field<StringBuilder>("_currentUserTranscript").ToString());
        replay.PushClose();
        await replay.Drain();
        Assert.Equal(request, Assert.Single(replay.ExecutedRequests));
        Assert.True(Assert.Single(replay.Receipts).GetProperty("toolResponse")
            .GetProperty("functionResponses")[0].GetProperty("response").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Lifecycle_text_cannot_authorize_a_tab_close_without_user_input()
    {
        await using var run = await Connection.CreateAsync();
        using var replay = new TabReplay(run);
        Assert.True(await run.Live.TryAnnounceLifecycleAsync(Notice()));
        Assert.Contains("KEYBOARD.WTF_BACKGROUND_LIFECYCLE_EVENT", await ReadClientTextAsync(run));
        replay.PushClose();
        await replay.Drain();
        Assert.Empty(replay.ExecutedRequests);
        Assert.True(Assert.Single(replay.Receipts).GetProperty("toolResponse")
            .GetProperty("functionResponses")[0].GetProperty("response").GetProperty("no_tab_closed").GetBoolean());
    }

    [Fact]
    public async Task New_speech_cannot_reuse_explicit_text_tab_close_authority()
    {
        await using var run = await Connection.CreateAsync();
        using var replay = new TabReplay(run);
        replay.SubmittedRequest = "Close the first two tabs.";
        await run.Live.SendTextTurnAsync(replay.SubmittedRequest);
        await ReadClientTextAsync(run);
        replay.Push("{\"voiceActivity\":{\"type\":\"ACTIVITY_START\"}}");
        replay.PushClose();
        await replay.Drain();
        Assert.Empty(replay.ExecutedRequests);
        Assert.True(Assert.Single(replay.Receipts).GetProperty("toolResponse")
            .GetProperty("functionResponses")[0].GetProperty("response").GetProperty("no_tab_closed").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connector_notice_does_not_overtake_the_first_response_to_recorded_input(bool priorResponseFlag)
    {
        await using var run = await Connection.CreateAsync();
        using var replay = new TabReplay(run);
        run.Live.SetConnectorReplyOwnership(() => true, _ => { });
        var original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", original);
        var diagnostics = run.Field<object>("_providerDiagnostics");
        diagnostics.GetType().GetField("CurrentTurnHadResponse")!.SetValue(diagnostics, priorResponseFlag);
        var notice = run.Live.TryAnnounceLifecycleAsync(new()
        {
            Source = "connector", State = "settled", Detail = "Exact opening completed.",
            OwnsCurrentReply = () => true
        });
        await Task.Delay(100);
        Assert.False(notice.IsCompleted);
        Assert.False(run.HasIncomingBytes);
        replay.Push("{\"serverContent\":{\"outputTranscription\":{\"text\":\"Unverified original reply.\"}}}");
        Assert.True(await notice.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("Exact opening completed", await ReadClientTextAsync(run));
        Assert.False(original.Task.IsCompleted);
        Assert.Equal("", run.Field<StringBuilder>("_currentAssistantTranscript").ToString());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("new-reply")]
    [InlineData("completion")]
    public async Task Waiting_connector_response_boundary_releases_on_cancellation_replacement_or_completion(string boundary)
    {
        await using var run = await Connection.CreateAsync();
        using var replay = new TabReplay(run);
        run.Live.SetConnectorReplyOwnership(() => true, _ => { });
        var original = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", original);
        var notice = run.Live.TryAnnounceLifecycleAsync(new()
        {
            Source = "connector", State = "settled", Detail = "Exact opening completed.",
            OwnsCurrentReply = () => true
        });
        Assert.False(notice.IsCompleted);
        if (boundary == "cancel") run.Session.Cancel();
        else if (boundary == "completion") replay.Push("{\"serverContent\":{\"turnComplete\":true}}");
        else
        {
            var diagnostics = run.Field<object>("_providerDiagnostics");
            diagnostics.GetType().GetMethod("BeginReply")!.Invoke(diagnostics, null);
        }
        var delivered = await notice.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(boundary == "completion", delivered);
        if (delivered) Assert.Contains("Exact opening completed", await ReadClientTextAsync(run));
        else Assert.False(run.HasIncomingBytes);
    }

    private static async Task<string> ReadClientTextAsync(Connection run)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var bytes = new byte[8192];
        var result = await run.Server.ReceiveAsync(bytes, timeout.Token);
        using var document = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0, result.Count));
        return document.RootElement.GetProperty("clientContent").GetProperty("turns")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString();
    }

    private sealed class TabReplay : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Connection _connection;
        private readonly object _diagnostics;
        private readonly LiveToolCallDispatcher _dispatcher;
        public string SubmittedRequest = "";
        public System.Collections.Concurrent.ConcurrentQueue<string> ExecutedRequests { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<System.Text.Json.JsonElement> Receipts { get; } = new();

        public TabReplay(Connection connection)
        {
            _connection = connection;
            var type = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
            _diagnostics = Activator.CreateInstance(type, "submitted-tab-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
            type.GetProperty("UsesPersonalToolProtocol")!.SetValue(_diagnostics, true);
            _dispatcher = new(connection.Session.Token, (_, _, _) =>
            {
                ExecutedRequests.Enqueue(SubmittedRequest);
                return Task.FromResult(new Dictionary<string, object> { ["ok"] = true });
            }, (build, onSent, _) =>
            {
                var response = build();
                if (response is not null) Receipts.Enqueue(System.Text.Json.JsonSerializer.SerializeToElement(response));
                onSent();
                return Task.CompletedTask;
            });
            type.GetProperty("ToolCalls")!.SetValue(_diagnostics, _dispatcher);
            connection.Set("_providerDiagnostics", _diagnostics);
            connection.Set("_toolCalls", _dispatcher);
        }

        public void Push(string json) => typeof(GeminiLiveConversationService)
            .GetMethod("HandleServerMessage", PrivateInstance)!.Invoke(_connection.Live,
                [json, new TaskCompletionSource<bool>(), _diagnostics, _connection.Session.Token]);
        public void PushClose() => Push("{\"toolCall\":{\"functionCalls\":[{\"id\":\"typed-close\",\"name\":\"browser_action\",\"args\":{\"action\":\"close tabs\",\"last_tab_count\":2}}]}}");
        public Task Drain() => _dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose() => _dispatcher.Dispose();
    }

    private static JarvisLifecycleAnnouncement Notice(Func<bool> valid = null) => new()
    {
        Source = "browser_automation", State = "Failed", Detail = "Captured test task failed",
        IsCurrent = valid,
    };

    // A real loopback WebSocket transport, with no provider, auth, microphone,
    // billing, or app launch. TcpListener port 0 avoids shared-port test races.
    private sealed class Connection : IAsyncDisposable
    {
        public GeminiLiveConversationService Live { get; } = new(null, null, null);
        public CancellationTokenSource Session { get; } = new();
        public WebSocket Server { get; private set; }
        private readonly ClientWebSocket _client = new();
        private TcpClient _peer;
        public bool HasIncomingBytes => _peer.Available > 0;

        public static async Task<Connection> CreateAsync()
        {
            var run = new Connection();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var connecting = run._client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
                run._peer = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = run._peer.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (header.Length >= 8192 || await stream.ReadAsync(one, timeout.Token) != 1)
                        throw new IOException("Incomplete loopback WebSocket handshake.");
                    header.Append((char)one[0]);
                }
                var key = header.ToString().Split("\r\n")
                    .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    .Split(':', 2)[1].Trim();
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token);
                run.Server = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
                await connecting;
                run.Set("_socket", run._client);
                run.Set("_sessionCts", run.Session);
                run.Set("_paidLiveReady", true);
                return run;
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }
        }

        public T Field<T>(string name) => (T)typeof(GeminiLiveConversationService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Live);
        public void Set(string name, object value) => typeof(GeminiLiveConversationService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Live, value);

        public ValueTask DisposeAsync()
        {
            Session.Cancel();
            // Prevent the app shutdown routine from waiting for a mock provider close.
            Set("_socket", null);
            _client.Dispose();
            Server?.Dispose();
            _peer?.Dispose();
            Live.Dispose();
            Session.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
