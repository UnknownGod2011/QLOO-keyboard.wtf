using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using KeyboardWtf.Models;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class JarvisServerEventOrderingTests
{
    [Theory]
    [InlineData("goodbye")]
    [InlineData("go away")]
    [InlineData("fuck off")]
    [InlineData("end the conversation")]
    [InlineData("End the conversation now.")]
    [InlineData("Could you please end the conversation now?")]
    [InlineData("bye Jarvis")]
    [InlineData("goodbye Jarvis")]
    [InlineData("that's all, goodbye")]
    [InlineData("thanks, goodbye")]
    public async Task Recognized_terminal_input_is_not_queued_behind_an_inflight_tool(string farewell)
    {
        await using var run = new MessageRun();
        var reader = run.ReadAsync(
            ToolCall("action-1"),
            JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text = farewell } } }));

        try
        {
            await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await reader.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.LocalTerminationRequested() || run.Ended.Task.IsCompletedSuccessfully);
            await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.Session.IsCancellationRequested);
        }
        finally
        {
            run.Release();
            await IgnoreCancelledAsync(reader);
        }
    }

    [Theory]
    [InlineData("inputTranscription")]
    [InlineData("interimInputTranscription")]
    public async Task Terminal_input_owns_a_combined_frame_before_tools_or_context_observers(string transcriptionKey)
    {
        await using var run = new MessageRun();
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new Dictionary<string, object>
            {
                [transcriptionKey] = new { text = "End this conversation." },
                ["outputTranscription"] = new { text = "This response must not win." },
            },
            toolCall = new { functionCalls = new[] { new { id = "late-action", name = "test_pending_action", args = new { } } } },
        }));

        await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.Session.IsCancellationRequested);
        Assert.False(run.ToolEntered.Task.IsCompleted);
        Assert.False(run.UserObserved.Task.IsCompleted);
        Assert.Equal(0, run.PendingToolCount());
    }

    [Theory]
    [InlineData("inputTranscription", "Don't say goodbye yet.")]
    [InlineData("interimInputTranscription", "Don't say goodbye yet.")]
    [InlineData("inputTranscription", "After filling the form, say goodbye.")]
    [InlineData("interimInputTranscription", "After filling the form, say goodbye.")]
    public async Task Mentioning_a_future_or_negated_farewell_does_not_cancel_the_current_action(
        string transcriptionKey,
        string text)
    {
        await using var run = new MessageRun();
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new Dictionary<string, object> { [transcriptionKey] = new { text } },
            toolCall = new { functionCalls = new[] { new { id = "ordinary-action", name = "test_pending_action", args = new { } } } },
        }));

        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.Session.IsCancellationRequested);
        Assert.False(run.Ended.Task.IsCompleted);
        run.FinishTool();
        await run.DrainAsync();
        Assert.False(run.Session.IsCancellationRequested);
    }

    [Theory]
    [InlineData("Can you hear me?")]
    [InlineData("What can you help me with?")]
    [InlineData("Explain this selected text.")]
    public async Task Conversational_input_alone_is_observed_without_invoking_an_action(string text)
    {
        await using var run = new MessageRun();
        run.Push(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text } } }));
        await run.UserObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.ToolEntered.Task.IsCompleted);
        Assert.False(run.Session.IsCancellationRequested);
    }

    [Theory]
    [InlineData("Open Dis", "cord.", "Open Discord.")]
    [InlineData("Open", " Discord.", "Open Discord.")]
    [InlineData("Dev", "post, please.", "Devpost, please.")]
    public async Task Personal_input_ui_shows_the_assembled_turn_not_only_its_last_fragment(
        string first,
        string second,
        string expected)
    {
        await using var run = new MessageRun(usesPersonal: true);
        run.Push(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text = first } } }));
        run.Push(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text = second } } }));

        Assert.Equal("You", KeyboardWtfState.UiTitle);
        Assert.Equal(expected, KeyboardWtfState.UiDetail);
    }

    [Fact]
    public async Task Managed_input_ui_keeps_its_existing_latest_fragment_behavior()
    {
        await using var run = new MessageRun();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"First"}}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"second"}}}""");

        Assert.Equal("second", KeyboardWtfState.UiDetail);
    }

    [Fact]
    public async Task Explicit_browser_cancel_reaches_its_owner_before_an_unrelated_tool_finishes()
    {
        var coordinator = new BrowserAutomationCoordinator(null, null, null, null);
        var runtimeType = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Executing);
        typeof(BrowserAutomationCoordinator).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, runtime);
        var commands = new CommandRegistry(null, null, null, null, null, browserAutomation: coordinator);
        await using var run = new MessageRun();
        run.ObserveControls(commands.ObserveImmediateJarvisControl);
        run.Push(ToolCall("blocked-unrelated-action"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        run.Push("""{"serverContent":{"inputTranscription":{"text":"Okay stop, cancel browser automation."}}}""");
        Assert.Equal(BrowserTaskState.Cancelled, coordinator.Snapshot().State);
        Assert.False(run.ToolToken.IsCancellationRequested); // Do not cancel the unrelated request.
        Assert.False(run.Session.IsCancellationRequested);
        Assert.False(run.UserObserved.Task.IsCompleted); // Its receipt is still ordered after the tool.
        Assert.Null(commands.ObserveImmediateJarvisControl("cancel browser automation", run.Session.Token));

        run.FinishTool();
        await run.DrainAsync();
        var field = typeof(CommandRegistry).GetField("_browserPreRouteReceipt", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var receipt = Assert.IsType<BrowserPreRouteReceipt>(field.GetValue(commands));
        Assert.True(receipt.Ok);
        Assert.Contains("cancelled", receipt.Message);
        Assert.True(receipt.TryAcknowledgeOnce());
        Assert.False(receipt.TryAcknowledgeOnce());
        commands.ObserveJarvisTranscript("Okay stop, cancel browser automation.", run.Session.Token);
        Assert.Same(receipt, field.GetValue(commands));
        Assert.False(receipt.TryAcknowledgeOnce());
        ((CancellationTokenSource)runtimeType.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Fact]
    public async Task Shutdown_drops_a_cancel_receipt_waiting_behind_a_tool()
    {
        await using var run = new MessageRun();
        var committed = false;
        run.ObserveControls((_, _) => () => committed = true);
        run.Push(ToolCall("before-shutdown"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push("""{"serverContent":{"inputTranscription":{"text":"cancel browser automation"}}}""");
        run.Release();
        await run.DrainAsync();
        Assert.False(committed);
    }

    [Fact]
    public async Task Combined_frame_keeps_authoritative_input_before_its_tool_execution()
    {
        await using var run = new MessageRun();
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new { inputTranscription = new { text = "Open the requested folder." } },
            toolCall = new { functionCalls = new[] { new { id = "combined", name = "test_pending_action", args = new { } } } },
        }));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.UserObservedBeforeTool);
    }

    [Fact]
    public async Task Provider_cancellation_reaches_the_matching_inflight_tool()
    {
        await using var run = new MessageRun();
        var reader = run.ReadAsync(
            // Deliver the provider's ordinary completion boundary with this
            // proposal. This cancellation test must not first consume the
            // Managed missing-completion fallback's two-second grace period.
            """{"toolCall":{"functionCalls":[{"id":"action-2","name":"test_pending_action","args":{}}]},"serverContent":{"turnComplete":true}}""",
            """{"toolCallCancellation":{"ids":["action-2"]}}""");

        try
        {
            await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // ReadAsync and this assertion both resume from ToolEntered. First
            // prove that the cancellation frame was delivered; otherwise a
            // loaded test runner can race its 500ms timer against its own sender.
            await reader.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.ToolToken.IsCancellationRequested);
            await run.ToolCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(run.Session.IsCancellationRequested);
        }
        finally
        {
            run.Release();
            await IgnoreCancelledAsync(reader);
        }
    }

    [Fact]
    public async Task End_tool_waits_for_the_authoritative_transcript_instead_of_rejecting_early()
    {
        await using var run = new MessageRun();
        run.Push(EndCall("end-before-transcript"));

        await Task.Delay(100);
        Assert.Equal(1, run.PendingToolCount());

        run.Push("""{"serverContent":{"inputTranscription":{"text":"That's all, goodbye."}}}""");
        Assert.True(run.LocalTerminationRequested() || run.Ended.Task.IsCompletedSuccessfully);
        await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.Session.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Untranscribed_end_tool_cannot_end_a_conversation_after_a_direct_action(bool actionSucceeded)
    {
        await using var run = new MessageRun(actionSucceeded);
        run.Push(ToolCall("close-window"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.FinishTool();
        await run.DrainAsync();
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push(EndCall("end-without-transcript"));

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.False(run.LocalTerminationRequested());
        Assert.False(run.Session.IsCancellationRequested);
        Assert.False(run.Ended.Task.IsCompleted);

        run.Push("""{"serverContent":{"inputTranscription":{"text":"Can we keep talking?"}}}""");
        await run.UserObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.Session.IsCancellationRequested);
    }

    [Fact]
    public async Task Dedicated_end_tool_cannot_override_contradictory_user_transcription()
    {
        await using var run = new MessageRun();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Close the current browser tab."}}}""");
        await run.UserObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push(EndCall("incorrect-end"));

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.False(run.LocalTerminationRequested());
        Assert.False(run.Session.IsCancellationRequested);
    }

    private static string ToolCall(string id) => JsonSerializer.Serialize(new
    {
        toolCall = new { functionCalls = new[] { new { id, name = "test_pending_action", args = new { } } } },
    });

    private static string EndCall(string id) => JsonSerializer.Serialize(new
    {
        toolCall = new
        {
            functionCalls = new[]
            {
                new { id, name = "end_conversation", args = new { reason = "User ended the conversation." } },
            },
        },
    });

    private static async Task IgnoreCancelledAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Completed_turn_releases_authority_after_its_tools_before_the_next_observation()
    {
        await using var run = new MessageRun();
        var events = new List<string>();
        run.ObserveTurnOrder(events);
        run.Push("""{"serverContent":{"inputTranscription":{"text":"First request."}}}""");
        run.Push(ToolCall("slow-first-action"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Next request."}}}""");
        Assert.Equal(new[] { "First request." }, events);
        run.FinishTool();
        await run.DrainAsync();
        Assert.Equal(new[] { "First request.", "completed", "Next request." }, events);
    }

    [Fact]
    public async Task Tool_only_completion_is_distinct_from_the_later_spoken_answer_completion()
    {
        await using var run = new MessageRun();
        var completions = new List<bool>();
        run.ObserveTurnCompletions(completions);
        run.Push(ToolCall("owned-action"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.FinishTool();
        await run.DrainAsync();
        Assert.Equal(new[] { false }, completions);

        run.Push("""{"serverContent":{"outputTranscription":{"text":"The action finished."},"turnComplete":true}}""");
        await run.DrainAsync();
        Assert.Equal(new[] { false, true }, completions);
    }

    [Fact]
    public async Task Tool_only_turn_and_late_transcript_cannot_reauthorize_the_same_effect()
    {
        await using var run = new MessageRun();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Take one picture."}}}""");
        run.Push(ToolCall("original-effect"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"picture"}}}""");
        run.Push(ToolCall("late-duplicate"));
        run.FinishTool();
        await run.DrainAsync();
        Assert.Equal(1, run.ToolExecutionCount);
        Assert.False(run.Session.IsCancellationRequested);
    }

    [Fact]
    public async Task Spoken_output_while_a_tool_is_pending_does_not_release_its_effect_scope()
    {
        await using var run = new MessageRun();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Take one picture."}}}""");
        run.Push(ToolCall("original-effect"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push("""{"serverContent":{"outputTranscription":{"text":"Working on it."},"turnComplete":true}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"a trailing fragment"}}}""");
        run.Push(ToolCall("late-duplicate"));
        run.FinishTool();
        await run.DrainAsync();
        Assert.Equal(1, run.ToolExecutionCount);
    }

    [Fact]
    public async Task A_completed_answer_then_new_request_can_repeat_an_effect()
    {
        await using var run = new MessageRun();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Open the folder."}}}""");
        run.Push(ToolCall("first-effect"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.FinishTool();
        await run.DrainAsync();
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push("""{"serverContent":{"outputTranscription":{"text":"The folder is open."}}}""");
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Open the folder again."}}}""");
        run.Push(ToolCall("second-effect"));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.DrainAsync();
        Assert.Equal(2, run.ToolExecutionCount);
    }

    [Fact]
    public async Task Split_function_call_frames_wait_for_their_shared_provider_turn_completion()
    {
        await using var run = new MessageRun();
        run.Push(ToolCall("first-split-call"));
        run.Push(ToolCall("second-split-call"));
        Assert.Equal(2, run.PendingToolCount());
        Assert.False(run.ToolEntered.Task.IsCompleted);

        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.FinishTool();
        await run.DrainAsync();
        Assert.Equal(0, run.PendingToolCount());
    }

    [Fact]
    public async Task Tool_retries_after_an_answer_keep_the_old_effect_scope_despite_later_transcription()
    {
        await using var run = new MessageRun();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Take one picture."}}}""");
        run.Push(ToolCall("first-effect"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.FinishTool();
        await run.DrainAsync();
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push("""{"serverContent":{"outputTranscription":{"text":"Picture saved."}}}""");
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push(ToolCall("uninvited-retry"));
        await run.DrainAsync();
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"a late fragment"}}}""");
        run.Push(ToolCall("after-fragment"));
        await run.DrainAsync();
        Assert.Equal(1, run.ToolExecutionCount);
    }

    [Fact]
    public async Task Provider_turn_complete_does_not_claim_listening_while_spoken_audio_remains_buffered()
    {
        await using var run = new MessageRun();
        var buffer = run.InstallPlaybackBuffer();
        var audio = new byte[48000]; // One second of 24 kHz PCM16 output.
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new
            {
                modelTurn = new { parts = new[] { new { inlineData = new { data = Convert.ToBase64String(audio) } } } },
            },
        }));
        Assert.True(buffer.BufferedBytes > 0);
        Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);

        run.Push("""{"serverContent":{"turnComplete":true}}""");
        Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);

        buffer.Read(new byte[audio.Length], 0, audio.Length);
        await Task.Delay(180);
        Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);
        await WaitUntilAsync(() => KeyboardWtfState.UiPhase == VoiceUiPhase.Listening);
    }

    [Fact]
    public async Task Tool_completion_speech_waits_for_the_verified_tool_receipt()
    {
        await using var run = new MessageRun();
        var buffer = run.InstallPlaybackBuffer();
        var audio = new byte[4800];
        var premature = JsonSerializer.Serialize(new
        {
            serverContent = new
            {
                outputTranscription = new { text = "The action is complete." },
                modelTurn = new { parts = new[] { new { inlineData = new { data = Convert.ToBase64String(audio) } } } },
            },
        });

        run.Push(ToolCall("one-effect"));
        run.Push(premature);
        Assert.Equal(0, buffer.BufferedBytes);
        Assert.NotEqual(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);

        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.FinishTool();
        await run.DrainAsync();

        run.Push(premature);
        Assert.True(buffer.BufferedBytes > 0);
        Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);
    }

    [Fact]
    public async Task A_new_user_request_can_receive_speech_while_an_older_tool_is_pending()
    {
        await using var run = new MessageRun();
        var buffer = run.InstallPlaybackBuffer();
        run.Push(ToolCall("older-slow-effect"));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        run.Push("""{"serverContent":{"inputTranscription":{"text":"Tell me a joke."}}}""");
        var audio = new byte[4800];
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new
            {
                outputTranscription = new { text = "A fresh answer." },
                modelTurn = new { parts = new[] { new { inlineData = new { data = Convert.ToBase64String(audio) } } } },
            },
        }));

        Assert.True(buffer.BufferedBytes > 0);
        Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);
        run.FinishTool();
        await run.DrainAsync();
    }

    [Fact]
    public async Task Late_fragment_of_tool_request_cannot_release_pre_receipt_speech()
    {
        await using var run = new MessageRun();
        var buffer = run.InstallPlaybackBuffer();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Open the folder."}}}""");
        run.Push(ToolCall("older-slow-effect"));
        run.Push("""{"serverContent":{"inputTranscription":{"text":"please"}}}""");
        run.Push(SpokenFrame("The folder is open."));
        Assert.Equal(0, buffer.BufferedBytes);

        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.FinishTool();
        await run.DrainAsync();
    }

    [Fact]
    public async Task Interruption_without_a_new_turn_cannot_release_pre_receipt_speech()
    {
        await using var run = new MessageRun();
        var buffer = run.InstallPlaybackBuffer();
        run.Push(ToolCall("older-slow-effect"));
        run.Push("""{"serverContent":{"interrupted":true}}""");
        run.Push(SpokenFrame("The action is complete."));
        Assert.Equal(0, buffer.BufferedBytes);

        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.FinishTool();
        await run.DrainAsync();
    }

    [Fact]
    public async Task New_answer_before_its_transcript_is_not_silenced_by_an_older_tool()
    {
        await using var run = new MessageRun();
        var buffer = run.InstallPlaybackBuffer();
        run.Push(ToolCall("older-slow-effect"));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        run.Push(SpokenFrame("A fresh answer."));
        Assert.True(buffer.BufferedBytes > 0);
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Tell me a joke."}}}""");
        run.FinishTool();
        await run.DrainAsync();
    }

    private static string SpokenFrame(string text) => JsonSerializer.Serialize(new
    {
        serverContent = new
        {
            outputTranscription = new { text },
            modelTurn = new
            {
                parts = new[]
                {
                    new { inlineData = new { data = Convert.ToBase64String(new byte[4800]) } },
                },
            },
        },
    });

    [Fact]
    public async Task Three_heard_but_unanswered_turns_request_existing_safe_socket_recovery()
    {
        await using var run = new MessageRun();
        for (var index = 0; index < 2; index++)
        {
            run.Push(JsonSerializer.Serialize(new
            {
                serverContent = new { inputTranscription = new { text = $"Request {index + 1}." } },
            }));
            run.Push("""{"serverContent":{"turnComplete":true}}""");
            Assert.False(run.EmptyTurnRecoveryRequested());
        }

        run.Push("""{"serverContent":{"inputTranscription":{"text":"Request 3."}}}""");
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        Assert.True(run.EmptyTurnRecoveryRequested());
    }

    [Fact]
    public async Task Playback_drain_cannot_replace_newer_user_turn_ui()
    {
        await using var run = new MessageRun();
        var buffer = run.InstallPlaybackBuffer();
        var audio = new byte[48000];
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new
            {
                modelTurn = new { parts = new[] { new { inlineData = new { data = Convert.ToBase64String(audio) } } } },
            },
        }));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "You", "New user turn");

        buffer.Read(new byte[audio.Length], 0, audio.Length);
        await Task.Delay(300);
        Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);
        Assert.Equal("You", KeyboardWtfState.UiTitle);
        Assert.Equal("New user turn", KeyboardWtfState.UiDetail);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(25);
        Assert.True(condition());
    }

    private sealed class MessageRun : IAsyncDisposable
    {
        private readonly GeminiLiveConversationService _service = new(null, null, null);
        private readonly bool _toolSucceeds;
        private readonly MethodInfo _handle = typeof(GeminiLiveConversationService).GetMethod(
            "HandleServerMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly TaskCompletionSource<bool> _release = NewSignal();
        private readonly object _diagnostics;
        public CancellationTokenSource Session { get; } = new();
        public TaskCompletionSource<bool> ToolEntered { get; } = NewSignal();
        public TaskCompletionSource<bool> ToolCancelled { get; } = NewSignal();
        public CancellationToken ToolToken { get; private set; }
        public bool UserObservedBeforeTool { get; private set; }
        public TaskCompletionSource<bool> Ended { get; } = NewSignal();
        public TaskCompletionSource<bool> UserObserved { get; } = NewSignal();
        public int ToolExecutionCount;

        public MessageRun(bool toolSucceeds = true, bool usesPersonal = false)
        {
            _toolSucceeds = toolSucceeds;
            typeof(GeminiLiveConversationService).GetField(
                "_sessionCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_service, Session);
            var diagnosticsType = typeof(GeminiLiveConversationService).GetNestedType(
                "ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
            _diagnostics = Activator.CreateInstance(diagnosticsType,
                "ordering-test", 1, DateTimeOffset.UtcNow, 0, false)!;
            diagnosticsType.GetProperty("UsesPersonalToolProtocol")!.SetValue(_diagnostics, usesPersonal);
            _service.SetSessionEndingObserver(() =>
            {
                Ended.TrySetResult(true);
                return Task.CompletedTask;
            });
            _service.SetUserTranscriptObserver((_, _) => UserObserved.TrySetResult(true));
            _service.SetToolExecutor(async (_, _, token) =>
            {
                Interlocked.Increment(ref ToolExecutionCount);
                UserObservedBeforeTool = UserObserved.Task.IsCompletedSuccessfully;
                ToolToken = token;
                ToolEntered.TrySetResult(true);
                try { await _release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    ToolCancelled.TrySetResult(true);
                    throw;
                }
                token.ThrowIfCancellationRequested();
                return new Dictionary<string, object> { ["ok"] = _toolSucceeds };
            });
        }

        public async Task ReadAsync(params string[] messages)
        {
            // Same sequential message dispatch as the production receive loop.
            for (var index = 0; index < messages.Length; index++)
            {
                _handle.Invoke(_service, [messages[index], NewSignal(), _diagnostics, Session.Token]);
                if (index == 0)
                    await ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        public void Push(string message) =>
            _handle.Invoke(_service, [message, NewSignal(), _diagnostics, Session.Token]);

        public BufferedWaveProvider InstallPlaybackBuffer()
        {
            var buffer = GeminiLiveConversationService.CreatePlaybackBuffer();
            typeof(GeminiLiveConversationService).GetField(
                "_playbackBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_service, buffer);
            return buffer;
        }

        public void ObserveControls(Func<string, CancellationToken, Action> observer) =>
            _service.SetUserTranscriptObserver((_, _) => UserObserved.TrySetResult(true), observer);

        public void ObserveTurnOrder(List<string> events) =>
            _service.SetUserTranscriptObserver((text, _) => events.Add(text), userTurnCompletedObserver: _ => events.Add("completed"));

        public void ObserveTurnCompletions(List<bool> completions) =>
            _service.SetUserTranscriptObserver((_, _) => { }, userTurnCompletedObserver: completed => completions.Add(completed));

        public void FinishTool() => _release.TrySetResult(true);

        public async Task DrainAsync()
        {
            var dispatcher = (LiveToolCallDispatcher)_diagnostics.GetType().GetProperty("ToolCalls")!.GetValue(_diagnostics);
            if (dispatcher != null)
                await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public int PendingToolCount()
        {
            var dispatcher = _diagnostics.GetType().GetProperty(
                "ToolCalls", BindingFlags.Instance | BindingFlags.Public)!.GetValue(_diagnostics);
            return dispatcher == null
                ? 0
                : (int)dispatcher.GetType().GetProperty(
                    "PendingCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dispatcher)!;
        }

        public bool LocalTerminationRequested() => (int)typeof(GeminiLiveConversationService).GetField(
            "_autoEnding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)! == 1;

        public bool EmptyTurnRecoveryRequested() => ((TaskCompletionSource<bool>)_diagnostics.GetType()
            .GetProperty("EmptyTurnRecovery")!.GetValue(_diagnostics)!).Task.IsCompletedSuccessfully;

        public void Release()
        {
            Session.Cancel();
            _release.TrySetResult(true);
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            await _service.StopAsync(showDone: false);
            _service.Dispose();
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// These exercise real queued receive/tool handlers and the process-wide UI
// publisher. Run without concurrent native overlay/hotkey integration tests;
// otherwise their global UI activity and blocking waits contaminate this timing.
[CollectionDefinition("Live event integration", DisableParallelization = true)]
public sealed class LiveEventIntegrationCollection { }
