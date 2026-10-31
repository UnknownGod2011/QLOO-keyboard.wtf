using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Tests;

// Replay only: no socket, microphone, real actions, credentials, or provider calls.
[Collection("Live event integration")]
public sealed class PersonalLiveToolProtocolTests
{
    private readonly ITestOutputHelper _output;

    public PersonalLiveToolProtocolTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Personal_provider_error_fails_initial_setup_or_leaves_receive_loop_for_bounded_recovery(bool setupCompleted)
    {
        await using var run = new Replay(personal: true);
        var setup = new TaskCompletionSource<bool>();
        if (setupCompleted) setup.SetResult(true);
        const string frame = """{"error":{"message":"private-provider-content-key","code":503}}""";
        if (setupCompleted)
        {
            var error = Assert.Throws<TargetInvocationException>(() => run.Push(frame, setup)).InnerException;
            Assert.IsType<System.Net.WebSockets.WebSocketException>(error);
            Assert.DoesNotContain("private-provider", error.Message);
            Assert.Null(error.InnerException);
        }
        else
        {
            run.Push(frame, setup);
            var error = await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(async () => await setup.Task);
            Assert.DoesNotContain("private-provider", error.Message);
        }
        Assert.Empty(run.Executions);
        Assert.Empty(run.Receipts);
    }

    [Fact]
    public async Task Managed_post_setup_error_retains_existing_protocol_behavior()
    {
        await using var run = new Replay(personal: false);
        var setup = new TaskCompletionSource<bool>();
        setup.SetResult(true);
        run.Push("""{"error":{"message":"test-managed-error"}}""", setup);
        Assert.True(setup.Task.IsCompletedSuccessfully);
        Assert.Empty(run.Executions);
    }

    [Fact]
    public async Task Personal_transcription_fragments_preserve_provider_word_boundaries_for_routing()
    {
        await using var run = new Replay(personal: true);
        foreach (var chunk in new[] { "Open", " Gmail", ",", " Instagram", ",", " Dis", "cord", " and", " Dev", "post." })
            run.Input(chunk);
        await run.Drain();
        Assert.Equal("Open Gmail, Instagram, Discord and Devpost.", run.CurrentTranscript);

        foreach (var chunk in new[] { "I", " opened", " Discord", "." })
            run.Push(JsonSerializer.Serialize(new { serverContent = new { outputTranscription = new { text = chunk } } }));
        Assert.Equal("I opened Discord.", run.CurrentAssistantTranscript);
    }

    [Fact]
    public async Task Managed_transcription_joining_remains_the_baseline_behavior()
    {
        await using var run = new Replay(personal: false);
        run.Input("Open Gmail");
        run.Input("and Instagram");
        await run.Drain();
        Assert.Equal("Open Gmail and Instagram", run.CurrentTranscript);
    }

    [Theory]
    [InlineData("File", " ", "Explorer", "File Explorer")]
    [InlineData("Penguin", " ", "Run", "Penguin Run")]
    [InlineData("Cursivis", " ", "Copy", "Cursivis Copy")]
    [InlineData("Dis", "", "cord", "Discord")]
    [InlineData("Dev", "", "post", "Devpost")]
    [InlineData("Hello,", "  ", "world.", "Hello,  world.")]
    [InlineData("First.", "\n", "Next.", "First. Next.")]
    public async Task Personal_standalone_separator_fragments_preserve_input_and_output_boundaries(
        string first, string separator, string last, string expected)
    {
        await using var run = new Replay(personal: true);
        foreach (var fragment in new[] { first, separator, last })
        {
            run.Input(fragment);
            run.Push(JsonSerializer.Serialize(new { serverContent = new { outputTranscription = new { text = fragment } } }));
        }
        await run.Drain();
        Assert.Equal(expected, run.CurrentTranscript);
        Assert.Equal(expected, run.CurrentAssistantTranscript);
        Assert.Equal(2, run.InputObservations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Blank_only_frames_do_not_establish_user_authority_or_a_response(bool personal)
    {
        await using var run = new Replay(personal);
        run.Input(" ");
        run.Push("""{"serverContent":{"outputTranscription":{"text":" "}}}""");
        Assert.Equal("", run.CurrentTranscript);
        Assert.Equal("", run.CurrentAssistantTranscript);
        Assert.Equal(0, run.InputObservations);
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        Assert.Equal(0, run.EmptyUserTurnStreak);
        await run.Drain();
    }

    [Fact]
    public async Task Personal_separator_frame_does_not_discard_coexisting_tool_or_audio()
    {
        await using var run = new Replay(personal: true, blockActions: true);
        var playback = run.InstallPlayback();
        run.Input("Open");
        run.Push("""{"serverContent":{"outputTranscription":{"text":"Done"}}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":" "},"outputTranscription":{"text":" "},"modelTurn":{"parts":[{"inlineData":{"data":"AAA="}}]}},"toolCall":{"functionCalls":[{"id":"separator-tool","name":"test_action","args":{"target":"alpha"}}]}}""");
        await run.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        run.ReleaseAction();
        await run.Drain();
        Assert.Single(run.Executions);
        Assert.Single(run.Receipts);
        // Pre-receipt audio still obeys the existing receipt ownership rule.
        Assert.Equal(0, playback.BufferedBytes);
        Assert.Equal("Done", run.CurrentAssistantTranscript);
        Assert.Equal(1, run.InputObservations);
        run.Push("""{"serverContent":{"outputTranscription":{"text":" "},"modelTurn":{"parts":[{"inlineData":{"data":"AAA="}}]}}}""");
        Assert.Equal("Done ", run.CurrentAssistantTranscript);
        Assert.Equal(2, playback.BufferedBytes);
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(1, run.TurnCompletions);
    }

    [Fact]
    public async Task Personal_audio_without_a_playback_sink_does_not_claim_buffer_publication()
    {
        await using var run = new Replay(personal: true);
        run.Push("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAA="}}]}}}""");
        Assert.Equal(0, run.PlaybackPublications);
        Assert.Equal(1, run.MissingPlaybackSinkParts);
        var playback = run.InstallPlayback();
        run.Push("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAA="}}]}}}""");
        Assert.Equal(1, run.PlaybackPublications);
        Assert.Equal(1, run.MissingPlaybackSinkParts);
        Assert.Equal(2, playback.BufferedBytes);
        await run.Drain();
    }

    [Fact]
    public void Personal_receive_shape_contains_only_declared_flags_and_counts()
    {
        using var frame = JsonDocument.Parse("""
            {"sessionResumptionUpdate":{"newHandle":"private-handle"},"serverContent":{
              "generationComplete":true,"waitingForInput":true,
              "inputTranscription":{"text":"private utterance","finished":true},
              "outputTranscription":{"text":"private answer","finished":true},
              "modelTurn":{"parts":[{"inlineData":{"data":"private-audio"}},
                {"text":"private output"},{"text":"private reasoning","thought":true}]}}}
            """);
        var shape = GeminiLiveConversationService.BuildPersonalReceiveShapeDiagnostics(frame.RootElement);
        Assert.Equal("serverContent=True resumptionUpdate=True coexistingContentResumption=True " +
            "generationComplete=True waitingForInput=True inputFinished=True outputFinished=True voiceActivity=none inputOffsets=False " +
            "modelParts=3 audioParts=1 textParts=2 thoughtParts=1", shape);
        Assert.DoesNotContain("private", shape);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"voiceActivity\":null}")]
    [InlineData("{\"voiceActivity\":{\"type\":\"private-value\"}}")]
    [InlineData("{\"serverContent\":null}")]
    [InlineData("{\"serverContent\":{\"inputTranscription\":null,\"modelTurn\":null}}")]
    [InlineData("{\"serverContent\":{\"generationComplete\":\"private-value\",\"modelTurn\":{\"parts\":[null,\"private-value\"]}}}")]
    public void Personal_receive_shape_does_not_echo_arbitrary_or_malformed_values(string json)
    {
        using var frame = JsonDocument.Parse(json);
        var shape = GeminiLiveConversationService.BuildPersonalReceiveShapeDiagnostics(frame.RootElement);
        Assert.DoesNotContain("private", shape);
        Assert.Contains("generationComplete=False", shape);
        Assert.Contains("audioParts=0 textParts=0 thoughtParts=0", shape);
    }

    [Fact]
    public async Task Personal_empty_search_turn_after_hello_does_not_block_a_subsequent_tool()
    {
        await using var run = new Replay(personal: true);
        var buffer = run.InstallPlayback();
        run.Input("Hello Jarvis, what's up?");
        run.Push("""{"serverContent":{"outputTranscription":{"text":"Hello."},"modelTurn":{"parts":[{"inlineData":{"data":"AAA="}}]}}}""");
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(1, run.TurnCompletions);
        Assert.Equal(2, buffer.BufferedBytes);
        buffer.ClearBuffer();

        foreach (var fragment in new[] { "Can", " you", " please", " search", " for", " platypus", " on Google?" })
            run.Input(fragment);
        run.Push("""{"serverContent":{"generationComplete":true,"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(2, run.TurnCompletions);
        Assert.Equal(1, run.EmptyUserTurnStreak);
        Assert.Empty(run.CurrentTranscript);
        Assert.Empty(run.Executions);
        Assert.Empty(run.Receipts);
        Assert.Equal(0, buffer.BufferedBytes);
        Assert.False(run.SessionCancelled);

        run.Input("Search Google for platypus.");
        run.Push(Calls(("next-search", "platypus")));
        await run.Drain();
        Assert.Equal("platypus", Assert.Single(run.Executions).Target);
        Assert.Single(run.Receipts);
        run.Push("""{"serverContent":{"outputTranscription":{"text":"Search opened."},"modelTurn":{"parts":[{"inlineData":{"data":"AAA="}}]},"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(3, run.TurnCompletions);
        Assert.Equal(0, run.EmptyUserTurnStreak);
        Assert.Equal(2, buffer.BufferedBytes);
        Assert.False(run.SessionCancelled);
    }

    [Fact]
    public async Task Personal_split_goodbye_ends_the_session_once()
    {
        await using var run = new Replay(personal: true);
        run.Input("good");
        Assert.False(run.Ended.Task.IsCompleted);

        run.Input("bye");
        Assert.False(run.EndRequested);
        run.Push(EndCall("split-farewell"));
        await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.SessionCancelled);
        Assert.Equal(1, run.EndCount);
    }

    [Fact]
    public async Task Personal_split_negated_goodbye_does_not_end_the_session()
    {
        await using var run = new Replay(personal: true);
        run.Input("Don't say good");
        run.Input("bye");
        run.Input(" yet.");

        await Task.WhenAny(run.Ended.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.False(run.Ended.Task.IsCompleted);
        Assert.False(run.SessionCancelled);
        Assert.Equal("Don't say goodbye yet.", run.CurrentTranscript);
    }

    [Theory]
    [InlineData("b", "ye", "")]
    [InlineData("good", "bye", "")]
    [InlineData("Okay, ", "good", "bye.")]
    [InlineData("End the conversation now.", "", "")]
    [InlineData("Could you ", "end the conversation ", "now?")]
    public async Task Personal_intentional_farewell_with_an_end_proposal_ends_once(
        string first,
        string second,
        string third)
    {
        await using var run = new Replay(personal: true);
        foreach (var fragment in new[] { first, second, third })
            run.Input(fragment);

        run.Push(EndCall("intentional-end"));
        await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.SessionCancelled);
        Assert.Equal(1, run.EndCount);
    }

    [Theory]
    [InlineData("Don't say good", "bye", " yet.", "Don't say goodbye yet.")]
    [InlineData("What does ", "goodbye", " mean?", "What does goodbye mean?")]
    [InlineData("good", "bye", " in French.", "goodbye in French.")]
    [InlineData("b", "ye", " the way.", "bye the way.")]
    public async Task Personal_farewell_mention_fragments_do_not_end_an_ordinary_request(
        string first,
        string second,
        string third,
        string expected)
    {
        await using var run = new Replay(personal: true);
        foreach (var fragment in new[] { first, second, third })
        {
            run.Input(fragment);
            Assert.False(run.EndRequested);
        }

        Assert.Equal(expected, run.CurrentTranscript);
        run.Push("""{"serverContent":{"outputTranscription":{"text":"The ordinary answer continues."}}}""");
        Assert.False(run.EndRequested);
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        Assert.False(run.EndRequested);
        Assert.False(run.SessionCancelled);
    }

    [Theory]
    [InlineData("Don't say goodbye yet.")]
    [InlineData("What does goodbye mean?")]
    [InlineData("goodbye in French.")]
    [InlineData("bye the way.")]
    [InlineData("Don't end the conversation now.")]
    [InlineData("Translate 'end the conversation now' into Hindi.")]
    [InlineData("Find Daisy's email. End the conversation now.")]
    public async Task Personal_end_proposal_cannot_override_a_complete_farewell_mention(string request)
    {
        await using var run = new Replay(personal: true);
        run.Input(request);

        run.Push(EndCall("incorrect-end"));
        await run.Drain();
        Assert.False(run.EndRequested);
        Assert.False(run.SessionCancelled);
        Assert.False(Assert.Single(run.Receipts).GetProperty("toolResponse")
            .GetProperty("functionResponses")[0].GetProperty("response").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Personal_serial_calls_reply_without_waiting_for_turn_complete_and_keep_user_authority()
    {
        await using var run = new Replay(personal: true);
        const string request = "Open the four named destinations.";
        run.Input(request);
        var localReceiptTimes = new List<TimeSpan>();
        foreach (var index in Enumerable.Range(1, 4))
        {
            // Actual Personal shape: the provider sends the next call only
            // after its predecessor's receipt, with no turnComplete between.
            var start = Stopwatch.GetTimestamp();
            run.Push(Calls(("call-" + index, "target-" + index)));
            Assert.False(run.HasStagedBatch); // Structural assertion, not a flaky latency threshold.
            await run.Drain();
            localReceiptTimes.Add(Stopwatch.GetElapsedTime(start));
            Assert.Equal(index, run.Receipts.Count);
            Assert.Equal(request, run.CurrentTranscript);
        }
        Assert.Equal(4, run.Executions.Count);
        _output.WriteLine($"Fake local proposal-to-receipt: one={localReceiptTimes[0].TotalMilliseconds:F1}ms; four-sum={localReceiptTimes.Sum(time => time.TotalMilliseconds):F1}ms. Provider inference excluded.");
        Assert.All(run.Executions, action => Assert.Equal(request, action.UserTurn));
        Assert.Equal(0, run.TurnCompletions);
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(1, run.TurnCompletions);
        Assert.Empty(run.CurrentTranscript);
    }

    [Fact]
    public async Task Personal_batched_four_targets_then_combined_audio_transcript_completion_publish_once()
    {
        await using var run = new Replay(personal: true);
        var playback = run.InstallPlayback();
        run.Input("Open the four requested sites.");
        run.Push(Calls(("one", "alpha"), ("two", "beta"), ("three", "gamma"), ("four", "delta")));
        Assert.False(run.HasStagedBatch);
        await run.Drain();
        Assert.Equal(4, run.Executions.Count);
        Assert.Equal(4, Assert.Single(run.Receipts).GetProperty("toolResponse")
            .GetProperty("functionResponses").GetArrayLength());
        // 3.1 may combine transcription, multiple audio parts and completion.
        // None may be discarded by treating the first field as the whole event.
        run.Push("""{"serverContent":{"outputTranscription":{"text":"All four opened."},"modelTurn":{"parts":[{"inlineData":{"data":"AAA="}},{"inlineData":{"data":"AAA="}}]},"generationComplete":true,"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(4, playback.BufferedBytes);
        Assert.Equal(1, run.TurnCompletions);
        Assert.Equal(4, run.Executions.Count);
        Assert.Single(run.Receipts);
        Assert.False(run.SessionCancelled);
    }

    [Fact]
    public async Task Personal_multi_call_message_publishes_one_ordered_receipt()
    {
        await using var run = new Replay(personal: true);
        run.Push(Calls(("one", "alpha"), ("two", "beta")));
        Assert.False(run.HasStagedBatch);
        await run.Drain();
        var response = Assert.Single(run.Receipts).GetProperty("toolResponse").GetProperty("functionResponses");
        Assert.Equal(new[] { "one", "two" }, response.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(new[] { "alpha", "beta" }, run.Executions.Select(action => action.Target));
    }

    [Fact]
    public async Task Personal_duplicate_ids_and_semantic_retries_do_not_repeat_an_effect()
    {
        await using var run = new Replay(personal: true);
        var original = Calls(("original", "alpha"));
        run.Push(original);
        Assert.False(run.HasStagedBatch);
        await run.Drain();
        run.Push(original);
        run.Push(Calls(("new-id-same-effect", "alpha")));
        await run.Drain();
        Assert.Single(run.Executions);
        Assert.Equal(2, run.Receipts.Count);
        Assert.True(run.Receipts.Last().GetProperty("toolResponse").GetProperty("functionResponses")[0]
            .GetProperty("response").GetProperty("duplicate_suppressed").GetBoolean());
    }

    [Fact]
    public async Task Personal_provider_cancel_reaches_inflight_action_and_drops_its_receipt()
    {
        await using var run = new Replay(personal: true, blockActions: true);
        run.Push(Calls(("cancel-me", "alpha")));
        Assert.False(run.HasStagedBatch);
        await run.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        run.Push("""{"toolCallCancellation":{"ids":["cancel-me"]}}""");
        await run.Drain();
        Assert.True(run.ActionCancelled);
        Assert.Empty(run.Receipts);
    }

    [Fact]
    public async Task Personal_stop_cancels_pending_work_and_cannot_publish_late_receipt()
    {
        await using var run = new Replay(personal: true, blockActions: true);
        run.Push(Calls(("stop-me", "alpha")));
        Assert.False(run.HasStagedBatch);
        await run.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        run.Stop();
        run.ReleaseAction();
        await run.Drain();
        Assert.Empty(run.Receipts);
        run.Push(Calls(("late", "beta")));
        await run.Drain();
        Assert.Single(run.Executions);
    }

    [Fact]
    public async Task Personal_speech_stays_suppressed_until_actual_receipt_then_plays_once()
    {
        await using var run = new Replay(personal: true, blockActions: true);
        var buffer = run.InstallPlayback();
        run.Push(Calls(("effect", "alpha")));
        Assert.False(run.HasStagedBatch);
        await run.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var frame = JsonSerializer.Serialize(new
        {
            serverContent = new
            {
                outputTranscription = new { text = "Done." },
                modelTurn = new { parts = new[] { new { inlineData = new { data = Convert.ToBase64String(new byte[4800]) } } } },
            },
        });
        run.Push(frame);
        Assert.Equal(0, buffer.BufferedBytes);
        run.ReleaseAction();
        await run.Drain();
        run.Push(frame);
        Assert.Equal(4800, buffer.BufferedBytes);
        Assert.Single(run.Receipts);
        Assert.Equal(0, run.TurnCompletions); // No fabricated provider turn boundary.
    }

    [Fact]
    public async Task Managed_split_messages_still_wait_and_publish_one_combined_receipt()
    {
        await using var run = new Replay(personal: false);
        run.Push(Calls(("one", "alpha")));
        run.Push(Calls(("two", "beta")));
        Assert.True(run.HasStagedBatch);
        Assert.Empty(run.Executions);
        Assert.Empty(run.Receipts);
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(2, run.Executions.Count);
        Assert.Equal(2, Assert.Single(run.Receipts).GetProperty("toolResponse").GetProperty("functionResponses").GetArrayLength());
    }

    [Theory]
    [InlineData("Close the first two tabs.")]
    [InlineData("Close the last two tabs.")]
    public async Task Personal_early_tab_close_binds_completed_spoken_input_before_execution(string request)
    {
        await using var run = new Replay(personal: true);
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_START"}}""");
        // Real installed trace: a contradictory last-two proposal precedes ASR.
        run.Push("""{"toolCall":{"functionCalls":[{"id":"early-close","name":"browser_action","args":{"action":"close tabs","last_tab_count":2,"target":"tabs"}}]}}""");
        await Task.WhenAny(run.ActionEntered.Task, Task.Delay(100));
        Assert.False(run.ActionEntered.Task.IsCompleted);
        run.Input(request);
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_END"}}""");
        await run.Drain();
        Assert.Equal(request, Assert.Single(run.Executions).UserTurn);
        Assert.Equal(request, Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
        Assert.Equal(1, run.InputObservations);
    }

    [Fact]
    public async Task Personal_early_tab_close_waits_for_all_input_fragments()
    {
        await using var run = new Replay(personal: true);
        run.Push(CloseCall("partial"));
        run.Input("Close the ");
        run.Input("first two");
        await Task.WhenAny(run.ActionEntered.Task, Task.Delay(100));
        Assert.False(run.ActionEntered.Task.IsCompleted);
        run.Input(" tabs.");
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_END"}}""");
        await run.Drain();
        Assert.Equal("Close the first two tabs.", Assert.Single(run.ObservedAtExecution));
        Assert.Equal(1, run.InputObservations);
    }

    [Fact]
    public async Task Personal_early_tab_close_can_bind_finished_input_without_activity_events()
    {
        await using var run = new Replay(personal: true);
        run.Push(CloseCall("finished"));
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Close the first two tabs.","finished":true}}}""");
        await run.Drain();
        Assert.Equal("Close the first two tabs.", Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
    }

    [Fact]
    public async Task Personal_early_tab_close_binds_late_transcript_after_activity_end()
    {
        await using var run = new Replay(personal: true);
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_START"}}""");
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_END"}}""");
        run.Push(CloseCall("ended-input"));
        run.Input("Close the first two tabs.");
        await run.Drain();
        Assert.Equal("Close the first two tabs.", Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
    }

    [Fact]
    public async Task Personal_new_speech_tab_proposal_cannot_use_an_older_known_transcript()
    {
        await using var run = new Replay(personal: true);
        run.Input("Close the last two tabs.");
        await run.Drain();
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_START"}}""");
        run.Push(CloseCall("next-input"));
        await Task.WhenAny(run.ActionEntered.Task, Task.Delay(100));
        Assert.False(run.ActionEntered.Task.IsCompleted);
        run.Stop();
        await run.Drain();
        Assert.Empty(run.Executions);
    }

    [Fact]
    public async Task Personal_early_tab_close_without_input_returns_no_close_receipt()
    {
        await using var run = new Replay(personal: true);
        run.Push(CloseCall("missing"));
        await run.Drain();
        Assert.Empty(run.Executions);
        var response = Assert.Single(run.Receipts).GetProperty("toolResponse").GetProperty("functionResponses")[0].GetProperty("response");
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.True(response.GetProperty("no_tab_closed").GetBoolean());
        run.Input("Open Gmail.");
        await run.Drain();
        Assert.Equal(1, run.InputObservations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Personal_early_tab_close_cancellation_or_stop_cannot_execute(bool cancelCall)
    {
        await using var run = new Replay(personal: true);
        run.Push(CloseCall("cancel-close"));
        if (cancelCall) run.Push("""{"toolCallCancellation":{"ids":["cancel-close"]}}""");
        else run.Stop();
        await run.Drain();
        Assert.Empty(run.Executions);
        Assert.Empty(run.Receipts);
    }

    [Fact]
    public async Task Personal_new_speech_cannot_authorize_an_older_unbound_tab_close()
    {
        await using var run = new Replay(personal: true);
        run.Push(CloseCall("older-close"));
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_START"}}""");
        run.Input("Open Gmail.");
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_END"}}""");
        await run.Drain();
        Assert.Empty(run.Executions);
        Assert.True(Assert.Single(run.Receipts).GetProperty("toolResponse").GetProperty("functionResponses")[0]
            .GetProperty("response").GetProperty("no_tab_closed").GetBoolean());
        Assert.Equal(1, run.InputObservations);
    }

    [Fact]
    public async Task Personal_tab_close_with_known_input_keeps_immediate_existing_dispatch()
    {
        await using var run = new Replay(personal: true);
        run.Input("Close the first two tabs.");
        run.Push(CloseCall("known-close"));
        await run.Drain();
        Assert.Equal("Close the first two tabs.", Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
        Assert.False(run.HasStagedBatch);
    }

    [Fact]
    public async Task Older_reply_completion_cannot_supply_missing_input_to_early_tab_close()
    {
        await using var run = new Replay(personal: true);
        run.Push(CloseCall("waiting-for-human"));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await Task.WhenAny(run.ActionEntered.Task, Task.Delay(100));
        Assert.False(run.ActionEntered.Task.IsCompleted);
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Close the first two tabs.","finished":true}}}""");
        await run.Drain();
        Assert.Equal("Close the first two tabs.", Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
        Assert.Equal(1, run.TurnCompletions);
    }

    [Theory]
    [InlineData("connector_resolve")]
    [InlineData("connector_execute")]
    [InlineData("connector_control")]
    public async Task Personal_early_connector_binds_late_completed_input(string name)
    {
        await using var run = new Replay(personal: true);
        // Real provider trace: tool at19:56:44.424; transcript at19:56:44.491.
        run.Push(JsonSerializer.Serialize(new { toolCall = new { functionCalls = new[] {
            new { id = "early-connector", name, args = new { target = "gmail" } } } } }));
        await Task.WhenAny(run.ActionEntered.Task, Task.Delay(100));
        Assert.False(run.ActionEntered.Task.IsCompleted);
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Find my email from Daisy Edwards.","finished":true}}}""");
        await run.Drain();
        Assert.Equal("Find my email from Daisy Edwards.", Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
        Assert.Equal(1, run.InputObservations);
    }

    [Fact]
    public async Task Personal_early_connector_without_input_cannot_execute()
    {
        await using var run = new Replay(personal: true);
        run.Push(JsonSerializer.Serialize(new { toolCall = new { functionCalls = new[] {
            new { id = "missing-connector", name = "connector_resolve", args = new { target = "gmail" } } } } }));
        await run.Drain();
        Assert.Empty(run.Executions);
        Assert.False(Assert.Single(run.Receipts).GetProperty("toolResponse").GetProperty("functionResponses")[0]
            .GetProperty("response").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Personal_known_connector_input_keeps_existing_immediate_dispatch()
    {
        await using var run = new Replay(personal: true);
        run.Input("Find my email from Daisy Edwards.");
        run.Push(JsonSerializer.Serialize(new { toolCall = new { functionCalls = new[] {
            new { id = "known-connector", name = "connector_resolve", args = new { target = "gmail" } } } } }));
        await run.Drain();
        Assert.Equal("Find my email from Daisy Edwards.", Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
    }

    [Fact]
    public async Task Personal_connector_continuation_after_tool_only_completion_keeps_current_authority()
    {
        await using var run = new Replay(personal: true);
        run.Input("Find my email from Daisy Edwards.");
        await run.Drain();
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        run.Push(JsonSerializer.Serialize(new { toolCall = new { functionCalls = new[] {
            new { id = "continuation", name = "connector_execute", args = new { target = "gmail" } } } } }));
        await run.Drain();
        Assert.Equal("Find my email from Daisy Edwards.", Assert.Single(run.ObservedAtExecution));
        Assert.Single(run.Receipts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Personal_early_connector_cancellation_or_stop_cannot_execute(bool cancelCall)
    {
        await using var run = new Replay(personal: true);
        run.Push(JsonSerializer.Serialize(new { toolCall = new { functionCalls = new[] {
            new { id = "cancel-connector", name = "connector_resolve", args = new { target = "gmail" } } } } }));
        if (cancelCall) run.Push("""{"toolCallCancellation":{"ids":["cancel-connector"]}}""");
        else run.Stop();
        await run.Drain();
        Assert.Empty(run.Executions);
        Assert.Empty(run.Receipts);
    }

    [Fact]
    public async Task Personal_activity_feedback_waits_for_final_input_and_selected_receipt_then_clears_on_output()
    {
        await using var run = new Replay(personal: true, selectedSourceReceipt: true);
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_START"}}""");
        run.Input("Find the requested section in the selected text.");
        Assert.Equal(0, run.ActivityFeedback.TryPublish(FutureActivityTime(), true, _ => 1));
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_END"}}""");
        string ordinaryDetail = null;
        Assert.Equal(1, run.ActivityFeedback.TryPublish(FutureActivityTime(), true,
            text => { ordinaryDetail = text; return 1; }));
        Assert.Equal("Working on your request…", ordinaryDetail);
        run.Push("""{"toolCall":{"functionCalls":[{"id":"selection-ui","name":"get_selected_text","args":{}}]}}""");
        await run.Drain();
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        string selectionDetail = null;
        Assert.Equal(2, run.ActivityFeedback.TryPublish(FutureActivityTime(), true,
            text => { selectionDetail = text; return 2; }));
        Assert.Equal("Processing selected text…", selectionDetail);
        run.Push("""{"serverContent":{"outputTranscription":{"text":"The requested section is available."}}}""");
        Assert.Equal(0, run.ActivityFeedback.TryPublish(FutureActivityTime(), true, _ => 3));
        Assert.Single(run.Receipts);
    }

    [Fact]
    public async Task Personal_activity_feedback_old_receipt_and_completion_cannot_label_overlapping_input()
    {
        await using var run = new Replay(personal: true, blockActions: true, selectedSourceReceipt: true);
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_START"}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Read the selected text.","finished":true}}}""");
        run.Push("""{"toolCall":{"functionCalls":[{"id":"old-selection-ui","name":"get_selected_text","args":{}}]}}""");
        await run.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Push("""{"voiceActivity":{"type":"ACTIVITY_START"}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Tell me a different answer.","finished":true}}}""");
        run.ReleaseAction();
        await run.Drain();
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        await run.Drain();
        Assert.Equal(0, run.ActivityFeedback.TryPublish(FutureActivityTime(), true, _ => 1));
        Assert.Single(run.Receipts);
    }

    [Fact]
    public async Task Personal_activity_feedback_same_frame_final_input_output_and_completion_does_not_leave_waiting()
    {
        await using var run = new Replay(personal: true);
        run.Push("""{"serverContent":{"inputTranscription":{"text":"What is two plus two?","finished":true},"outputTranscription":{"text":"Four."},"turnComplete":true}}""");
        await run.Drain();
        Assert.Contains("Jarvis: Four.", run.CommittedContext);
        Assert.Equal(0, run.ActivityFeedback.TryPublish(FutureActivityTime(), true, _ => 1));
        Assert.Empty(run.Executions);
    }

    private static long FutureActivityTime() => Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;

    private static string CloseCall(string id) => JsonSerializer.Serialize(new
    {
        toolCall = new { functionCalls = new[] { new { id, name = "browser_action",
            args = new { action = "close tabs", last_tab_count = 2, target = "tabs" } } } },
    });

    private static string Calls(params (string Id, string Target)[] calls) => JsonSerializer.Serialize(new
    {
        toolCall = new { functionCalls = calls.Select(call => new { id = call.Id, name = "test_action", args = new { target = call.Target } }) },
    });

    private static string EndCall(string id) => JsonSerializer.Serialize(new
    {
        toolCall = new { functionCalls = new[] { new { id, name = "end_conversation", args = new { reason = "User ended the conversation." } } } },
    });

    private sealed class Replay : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly GeminiLiveConversationService _service = new(null, null, null);
        private readonly CancellationTokenSource _session = new();
        private readonly object _diagnostics;
        private readonly LiveToolCallDispatcher _dispatcher;
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MethodInfo _handle = typeof(GeminiLiveConversationService).GetMethod("HandleServerMessage", PrivateInstance)!;
        private readonly MethodInfo _executeTool = typeof(GeminiLiveConversationService).GetMethod("ExecuteToolAsync", PrivateInstance)!;
        public ConcurrentQueue<(string Target, string UserTurn)> Executions { get; } = new();
        public ConcurrentQueue<JsonElement> Receipts { get; } = new();
        public ConcurrentQueue<string> ObservedAtExecution { get; } = new();
        private string _observedTranscript = "";
        public TaskCompletionSource<bool> ActionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ActionCancelled { get; private set; }
        public int EndCount;
        public int TurnCompletions;
        public int InputObservations;

        public Replay(bool personal, bool blockActions = false, bool selectedSourceReceipt = false)
        {
            typeof(GeminiLiveConversationService).GetField("_sessionCts", PrivateInstance)!.SetValue(_service, _session);
            var type = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
            _diagnostics = Activator.CreateInstance(type, "personal-protocol-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
            // Optional lookup allows the replay regression to demonstrate the
            // old staged behavior before the production compatibility fix.
            type.GetProperty("UsesPersonalToolProtocol")?.SetValue(_diagnostics, personal);
            type.GetProperty("ActivityFeedback")?.SetValue(_diagnostics, personal ? new LiveActivityFeedback() : null);
            _service.SetSessionEndingObserver(() =>
            {
                Interlocked.Increment(ref EndCount);
                Ended.TrySetResult(true);
                return Task.CompletedTask;
            });
            _service.SetUserTranscriptObserver((text, _) =>
                { _observedTranscript = text; Interlocked.Increment(ref InputObservations); },
                userTurnCompletedObserver: _ => Interlocked.Increment(ref TurnCompletions));
            _dispatcher = new(_session.Token, async (name, args, token) =>
            {
                if (name == "end_conversation")
                    return await (Task<Dictionary<string, object>>)_executeTool.Invoke(_service, [name, args, token]);
                Executions.Enqueue((args.TryGetProperty("target", out var target) ? target.GetString() : name, CurrentTranscript));
                ObservedAtExecution.Enqueue(_observedTranscript);
                ActionEntered.TrySetResult(true);
                if (blockActions)
                {
                    try { await _release.Task.WaitAsync(token); }
                    catch (OperationCanceledException) { ActionCancelled = true; throw; }
                }
                token.ThrowIfCancellationRequested();
                if (selectedSourceReceipt && name == "get_selected_text")
                    return new Dictionary<string, object> { ["ok"] = true, ["text"] = "Fixture selected source.", ["capture_id"] = "selection-ui-fixture" };
                return new Dictionary<string, object> { ["ok"] = true };
            }, (build, onSent, token) =>
            {
                token.ThrowIfCancellationRequested();
                var response = build();
                if (response is not null) Receipts.Enqueue(JsonSerializer.SerializeToElement(response));
                onSent();
                return Task.CompletedTask;
            });
            type.GetProperty("ToolCalls")!.SetValue(_diagnostics, _dispatcher);
        }

        public bool HasStagedBatch => typeof(LiveToolCallDispatcher).GetField("_currentTurnBatch", PrivateInstance)!.GetValue(_dispatcher) is not null;
        public LiveActivityFeedback ActivityFeedback => (LiveActivityFeedback)_diagnostics.GetType().GetProperty("ActivityFeedback")!.GetValue(_diagnostics);
        public int EmptyUserTurnStreak => (int)_diagnostics.GetType().GetField("ConsecutiveEmptyUserTurns")!.GetValue(_diagnostics);
        public bool SessionCancelled => _session.IsCancellationRequested;
        public int PlaybackPublications => ((int[])_diagnostics.GetType().GetField("PersonalPlaybackParts")!.GetValue(_diagnostics)!).Sum();
        public int MissingPlaybackSinkParts => ((int[])_diagnostics.GetType().GetField("PersonalMissingPlaybackSinkParts")!.GetValue(_diagnostics)!).Sum();
        public bool EndRequested => Ended.Task.IsCompleted
            || (int)typeof(GeminiLiveConversationService).GetField("_autoEnding", PrivateInstance)!.GetValue(_service)! == 1;
        public string CurrentTranscript => (string)typeof(GeminiLiveConversationService).GetMethod("CurrentUserTranscript", PrivateInstance,
            binder: null, Type.EmptyTypes, modifiers: null)!.Invoke(_service, null);
        public string CurrentAssistantTranscript => ((System.Text.StringBuilder)typeof(GeminiLiveConversationService)
            .GetField("_currentAssistantTranscript", PrivateInstance)!.GetValue(_service)!).ToString();
        public string CommittedContext => (string)typeof(GeminiLiveConversationService)
            .GetMethod("BuildRolloverContextSnapshot", PrivateInstance)!.Invoke(_service, null);
        public void Input(string text) => Push(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text } } }));
        public void Push(string frame) => _handle.Invoke(_service, [frame, new TaskCompletionSource<bool>(), _diagnostics, _session.Token]);
        public void Push(string frame, TaskCompletionSource<bool> setup) => _handle.Invoke(_service, [frame, setup, _diagnostics, _session.Token]);
        public Task Drain() => _dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        public void ReleaseAction() => _release.TrySetResult(true);
        public void Stop() { _session.Cancel(); _dispatcher.Stop(); }
        public BufferedWaveProvider InstallPlayback()
        {
            var buffer = GeminiLiveConversationService.CreatePlaybackBuffer();
            typeof(GeminiLiveConversationService).GetField("_playbackBuffer", PrivateInstance)!.SetValue(_service, buffer);
            return buffer;
        }
        public async ValueTask DisposeAsync()
        {
            Stop();
            ReleaseAction();
            await Drain();
            _dispatcher.Dispose();
            _service.Dispose();
            _session.Dispose();
        }
    }
}
