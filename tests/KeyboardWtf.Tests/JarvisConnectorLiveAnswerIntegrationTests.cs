using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

namespace KeyboardWtf.Tests;

// Replay the real receive/dispatcher/command/controller path. Only the remote
// backend, reasoning response and socket publication are replaced; no Live
// session, microphone, credentials or provider requests are started.
[Collection("Live event integration")]
public sealed class JarvisConnectorLiveAnswerIntegrationTests
{
    [Theory]
    [InlineData("Can you open that in a new tab?")]
    [InlineData("Can you open that email thread in a new tab?")]
    public async Task Spoken_open_of_one_paged_native_result_runs_when_the_model_proposes_no_tool(string request)
    {
        await using var replay = new Replay(true, retainedMail: true, preferDirectReadReceipt: true, finalAdmission: true)
            { SearchHasMore = true };
        await replay.Resolve("Find the most recent email from the sender.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found this conversation.");
        var before = (replay.BackendExecutions, replay.Resolves, replay.ReasoningCalls);
        // Separate transcript and END frames match the actual microphone trace.
        replay.Activity("ACTIVITY_START");
        await replay.Input(request);
        replay.Activity("ACTIVITY_END");
        await replay.Drain();
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aabbccdd22", Assert.Single(replay.OpenedUrls));
        var receipt = await replay.CollectNotice();
        Assert.True(receipt.GetProperty("retained_object_open").GetBoolean());
        Assert.Equal(before, (replay.BackendExecutions, replay.Resolves, replay.ReasoningCalls));
        await replay.Answer("The conversation was opened.");
        Assert.False(replay.PendingReply);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("give me more details on that and open the thread in a new", " tab.")]
    [InlineData("Could you open that thread in a new tab and give me more", " details about that entire conversation?")]
    public async Task Spoken_compound_followup_spanning_two_activities_keeps_the_complete_request(string first, string last)
    {
        await using var replay = new Replay(true, retainedMail: true, preferDirectReadReceipt: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        var originalTask = replay.TaskId;
        await replay.Answer("I found the conversation.");
        // A short spoken pause can end one activity before Live proposes a tool.
        // Both fragments still belong to its one unanswered human request.
        await replay.FinalInput(first);
        await replay.FinalInput(last);
        Assert.Equal(first + last, replay.Commands.ConnectorUserRequestSnapshot.Text);
        var result = await replay.Tool("followup", "connector_execute", new
        { task_id = originalTask, capability_id = "mail_thread", arguments_json = "{\"id\":\"stale-model-reference\"}" });
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Contains(Replay.ThreadBody, result.ToString());
        Assert.Single(replay.OpenedUrls);
        Assert.DoesNotContain("no longer active", result.ToString(), StringComparison.OrdinalIgnoreCase);
        await replay.Answer("Here is the full recap, and the verified thread was opened.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("connector_execute")]
    [InlineData("connector_resolve")]
    public async Task Admitted_thread_read_receipt_does_not_deny_its_correct_read_proposal(string tool)
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation and give me the details.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation.");
        await replay.FinalInput("Give me the full details of that conversation.");
        var result = await replay.Tool("thread-read", tool, new
        { task_id = replay.TaskId, capability_id = "mail_thread", arguments_json = "{\"id\":\"stale-model-reference\"}" });
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Contains(Replay.ThreadBody, result.ToString());
        Assert.False(result.TryGetProperty("proposed_action_dispatched", out _));
        Assert.DoesNotContain("not execution of the proposed connector_execute", result.GetProperty("instruction").GetString());
        await replay.Answer("Here are the retrieved conversation details.");
    }

    [Theory]
    [InlineData("get_selected_text")]
    [InlineData("open_url")]
    [InlineData("connector_execute")]
    public async Task Admitted_details_owner_collects_result_before_an_unrelated_model_proposal(string tool)
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation and give me the details.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation.");
        await replay.FinalInput("Please summarize the entire conversation in detail.");
        var args = tool switch
        {
            "open_url" => (object)new { url = "https://unrelated.example.test" },
            "connector_execute" => new { task_id = replay.TaskId, capability_id = "mail_send",
                arguments_json = "{\"to\":\"unrequested@example.test\",\"body\":\"Not authorized\"}" },
            _ => new { }
        };
        var result = await replay.Tool("wrong-proposal", tool, args);
        Assert.Contains(Replay.ThreadBody, result.ToString());
        Assert.Equal("read_retained_conversation", result.GetProperty("actual_operation").GetString());
        Assert.False(result.GetProperty("proposed_action_dispatched").GetBoolean());
        Assert.False(result.GetProperty("browser_action_performed").GetBoolean());
        Assert.Contains("did not open", result.GetProperty("instruction").GetString());
        Assert.Equal("detailed_conversation", result.GetProperty("answer_scope").GetProperty("responseStyle").GetString());
        Assert.Contains("Chronology", result.GetProperty("answer_scope").GetProperty("sections").ToString());
        Assert.Equal(0, replay.SelectionCaptures);
        Assert.Empty(replay.OpenedUrls);
        Assert.False(replay.PendingReply);
        // Another proposal in the same human turn still cannot capture the
        // foreground selection or launch an unrelated destination.
        var again = await replay.Tool("same-turn-proposal", tool, args);
        Assert.Contains(Replay.ThreadBody, again.ToString());
        Assert.DoesNotContain("mail_send", replay.ExecutedCapabilities);
        Assert.Equal(0, replay.SelectionCaptures);
        Assert.Empty(replay.OpenedUrls);
        await replay.Answer("The conversation details are available.");
        var reads = replay.BackendExecutions;
        await replay.Input("Read my selected text.");
        var selection = await replay.Tool("normal-selection", "get_selected_text", new { });
        Assert.Contains("ordinary selected text", selection.ToString());
        Assert.Equal(1, replay.SelectionCaptures);
        Assert.Equal(reads, replay.BackendExecutions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_offer_after_long_connector_answer_is_not_lost_to_rollover_history(bool personal)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: true);
        await replay.Resolve("Find the relevant email conversation and give me the details.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        var origin = replay.TaskId;
        var introduction = string.Concat(Enumerable.Repeat("These are verified details from the retrieved conversation. ", 14));
        replay.Output(introduction);
        await replay.Answer("Would you like more information about that thread?");
        Assert.Contains("Would you like more information", replay.CompletedAnswers.Last());
        Assert.True(replay.CompletedAnswers.Last().Length > 600);
        Assert.All(replay.HistoryText, text => Assert.True(text.Length <= 600));
        await replay.Input("Yes please.");
        var result = await replay.Tool("long-answer-yes", "connector_execute", new {
            task_id = origin, capability_id = "mail_read", arguments_json = "{\"id\":\"stale-model-message\"}"
        });
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.NotEqual(origin, replay.TaskId);
        Assert.Contains(Replay.ThreadBody, result.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ordinary_answer_keeps_existing_history_limit_after_a_connector_turn(bool personal)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: true);
        await replay.Resolve("Find the relevant email conversation and give me the details.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation.");
        await replay.Input("Tell me a story.");
        var story = string.Concat(Enumerable.Repeat("This is an ordinary story without connector work. ", 24));
        await replay.Answer(story);
        Assert.InRange(replay.CompletedAnswers.Last().Length, 590, 600);
        Assert.All(replay.HistoryText, text => Assert.True(text.Length <= 600));
        Assert.DoesNotContain("I found the conversation", replay.CompletedAnswers.Last());
    }

    [Fact]
    public async Task Oversized_connector_answer_cannot_drop_a_mixed_offer_to_authorize_yes()
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true);
        await replay.Resolve("Find the relevant email conversation and give me the details.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        var origin = replay.TaskId;
        replay.Output("Would you like me to send a reply? " + new string('x', 3010));
        await replay.Answer("Would you like more information about that thread?");
        Assert.Equal("", replay.CompletedAnswers.Last());
        var before = (replay.BackendExecutions, replay.Resolves);
        await replay.Input("Yes please.");
        var result = await replay.Tool("oversized-answer-yes", "connector_execute", new {
            task_id = origin, capability_id = "mail_read", arguments_json = "{\"id\":\"stale-model-message\"}"
        });
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(before, (replay.BackendExecutions, replay.Resolves));
    }

    [Fact]
    public async Task Retained_question_notice_delivers_the_original_question_with_its_evidence()
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation and give me the details.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found and read the conversation.");
        var before = (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves);
        const string question = "What did she ask me for?";
        await replay.FinalInput(question);
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        replay.TurnComplete();
        await replay.Drain();
        var receipt = await replay.CollectNotice();
        Assert.Equal(question, receipt.GetProperty("answer_scope").GetProperty("request").GetString());
        Assert.Contains(Replay.ThreadBody, receipt.ToString());
        Assert.Equal(before, (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves));
        Assert.False(replay.PendingReply);
    }

    [Theory]
    [InlineData("Is there anything specific you'd like to know about these emails?")]
    [InlineData("Is there a specific detail you were looking for?")]
    [InlineData("Is there any specific detail you'd like to know more about?")]
    [InlineData("Would you like me to read any specific message or give more detail?")]
    [InlineData("Is there anything else you'd like to know about them?")]
    [InlineData("I can read the full messages if you like.")]
    [InlineData("Do you want me to attempt to retrieve more details?")]
    [InlineData("Would you like me to read the most recent one?")]
    public async Task Spoken_yes_after_specific_information_offer_recovers_a_fresh_read_from_stale_model_task(string offer)
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true);
        await replay.Resolve("Find the relevant email conversation and give me the details.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        var origin = replay.TaskId;
        // The initial compound request already reads the thread for details.
        Assert.Equal(new[] { "mail_search", "mail_thread" }, replay.ExecutedCapabilities);
        await replay.Answer("I found the conversation. " + offer);
        // Real v23 had inputTranscription but no finished marker. The model
        // supplied mail_read against the old terminal task, exactly as here.
        await replay.Input("Yes please.");
        var result = await replay.Tool("spoken-yes", "connector_execute", new {
            task_id = origin, capability_id = "mail_read", arguments_json = "{\"id\":\"stale-model-message\"}"
        });
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.NotEqual(origin, replay.TaskId);
        Assert.Contains(Replay.ThreadBody, result.ToString());
        Assert.Equal(new[] { "mail_search", "mail_thread", "mail_thread" }, replay.ExecutedCapabilities);
        Assert.Equal("aabbccdd22", replay.LastExecutedArguments.GetProperty("id").GetString());
        await replay.Answer("The sender asked for a proposal and the recipient replied.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admitted_read_unavailable_notice_releases_reply_without_discarding_result(bool throws)
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation. Would you like more information about it?");
        replay.NoticeFailure = throws ? "throw" : "false";
        await replay.FinalInput("Give me the full details of that email.");
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await replay.WaitAdmissionSettled();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.False(replay.PendingReply);
        var receipt = await replay.CollectNotice();
        Assert.Contains(Replay.ThreadBody, receipt.ToString());
        Assert.Equal(new[] { "mail_search", "mail_thread" }, replay.ExecutedCapabilities);
    }

    [Theory]
    [InlineData(false, "Give me the full details of that email.")]
    [InlineData(true, "Give me the full details of that email.")]
    [InlineData(true, "Yes, please.")]
    public async Task Finalized_retained_read_runs_without_model_tool_and_owns_same_frame_answer(bool personal, string input)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        var origin = replay.TaskId;
        await replay.Answer("I found the conversation. Would you like more information about it?");
        replay.ReasoningOutage = true;
        await replay.FinalInput(input, "I cannot access your email details.");
        Assert.DoesNotContain("cannot access", replay.AssistantTranscript);
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEqual(origin, replay.TaskId);
        Assert.Equal(new[] { "mail_search", "mail_thread" }, replay.ExecutedCapabilities);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        replay.TurnComplete();
        await replay.Drain();
        var receipt = await replay.CollectNotice();
        Assert.Contains(Replay.ThreadBody, receipt.ToString());
        Assert.Equal("mail_thread", receipt.GetProperty("answer_scope").GetProperty("objectKind").GetString());
        Assert.Equal("aabbccdd22", receipt.GetProperty("answer_scope").GetProperty("threadId").GetString());
        Assert.Contains("whole retained conversation", receipt.GetProperty("instruction").GetString());
        var reads = replay.BackendExecutions;
        var repeated = await replay.Tool("stale-model", "connector_execute", new { task_id = origin,
            capability_id = "mail_thread", arguments_json = "{\"id\":\"stale-model-reference\"}" });
        Assert.Contains(Replay.ThreadBody, repeated.ToString());
        Assert.Equal(reads, replay.BackendExecutions);
        await replay.Answer("The sender requested a proposal and the recipient replied with it.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retained_read_prefix_is_not_admitted_before_content_restriction_finishes(bool personal)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation. Would you like more information about it?");
        var before = (replay.BackendExecutions, replay.Resolves);
        await replay.Input("Give me the full details");
        Assert.Equal(before, (replay.BackendExecutions, replay.Resolves));
        await replay.FinalInput(personal ? " but headers only, do not read bodies." : "Give me the full details but headers only, do not read bodies.");
        Assert.Equal(before, (replay.BackendExecutions, replay.Resolves));
        Assert.False(replay.Notice.Task.IsCompleted);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Voice_end_without_input_in_its_activity_cannot_read_stale_transcription(bool start)
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation. Would you like more information about it?");
        await replay.Input("Give me the full details of that email.");
        var before = (replay.Resolves, replay.BackendExecutions);
        if (start) replay.Activity("ACTIVITY_START");
        replay.Activity("ACTIVITY_END");
        await replay.Drain();
        Assert.Equal(before, (replay.Resolves, replay.BackendExecutions));
        Assert.False(replay.PendingReply);
        Assert.False(replay.Notice.Task.IsCompleted);
    }

    [Theory]
    [InlineData("after_completion")]
    [InlineData("before_completion")]
    [InlineData("split_across_completion")]
    public async Task Prior_reply_completion_cannot_erase_the_new_spoken_retained_read_activity(string inputOrdering)
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        var origin = replay.TaskId;
        var originHuman = replay.Commands.ConnectorUserRequestIdentity;
        await replay.Answer("I found the conversation. Would you like more information about it?");

        // Physical Live frames put the new speech START before interruption
        // and completion of the older reply. Check input arriving on either
        // side: that output boundary cannot revoke the new input activity.
        replay.Output("The earlier response is still finishing.");
        replay.Activity("ACTIVITY_START");
        replay.Interrupt();
        const string request = "Give me the full details of that email.";
        if (inputOrdering == "before_completion")
            await replay.Input(request);
        else if (inputOrdering == "split_across_completion")
            await replay.Input("Give me the full");
        replay.TurnComplete();
        if (inputOrdering == "after_completion")
            await replay.Input(request);
        else if (inputOrdering == "split_across_completion")
            await replay.Input(" details of that email.");
        replay.Activity("ACTIVITY_END");
        await replay.Drain();
        Assert.Equal(request, replay.Commands.ConnectorUserRequestSnapshot.Text);
        var followupHuman = replay.Commands.ConnectorUserRequestIdentity;
        Assert.NotEqual(originHuman, followupHuman);

        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var receipt = await replay.CollectNotice();
        Assert.NotEqual(origin, replay.TaskId);
        Assert.Equal(new[] { "mail_search", "mail_thread" }, replay.ExecutedCapabilities);
        Assert.Contains(Replay.ThreadBody, receipt.ToString());
        Assert.DoesNotContain("no longer active", receipt.ToString(), StringComparison.OrdinalIgnoreCase);
        var once = (replay.Resolves, replay.BackendExecutions, replay.TaskId);
        replay.Activity("ACTIVITY_END");
        await replay.Drain();
        Assert.Equal(once, (replay.Resolves, replay.BackendExecutions, replay.TaskId));
        Assert.Equal(followupHuman, replay.Commands.ConnectorUserRequestIdentity);
        await replay.Answer("The sender requested a proposal and the recipient replied with it.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Ordinary_voice_end_remains_dormant_with_no_connector_work()
    {
        await using var replay = new Replay(true, retainedMail: true, finalAdmission: true);
        foreach (var input in new[] { "Open Gmail.", "Open Instagram.", "Open Discord.",
            "Open Gmail, Instagram, Discord and Devpost.", "What is 2 + 2?" })
        {
            await replay.FinalInput(input);
            await replay.Answer("Ordinary answer.");
        }
        Assert.Equal(0, replay.Resolves);
        Assert.Equal(0, replay.BackendExecutions);
        Assert.Equal(0, replay.ReasoningCalls);
        Assert.False(replay.PendingReply);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Admitted_read_cancel_during_resolve_rejects_late_task_and_clears_owner()
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation. Would you like more information about it?");
        replay.HoldNextResolve = true;
        await replay.FinalInput("Give me the full details of that email.");
        await replay.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        replay.InterruptTurn();
        await replay.Input("Cancel the search.");
        replay.ReleaseResolve.TrySetResult(true);
        await replay.ResolveFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Drain the continuation of the deliberately cancellation-ignoring
        // fixture response, just as a late remote response can arrive.
        await replay.WaitAdmissionSettled();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.False(replay.PendingReply);
        Assert.Equal(new[] { "mail_search" }, replay.ExecutedCapabilities);
        Assert.False(replay.Notice.Task.IsCompleted);
    }

    [Theory]
    [InlineData("backend", "connector_backend_unavailable")]
    [InlineData("json", "invalid_connector_response")]
    [InlineData("timeout", "connector_timeout")]
    public async Task Admitted_read_resolve_failure_is_collectable_and_does_not_strand_reply(string failure, string code)
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation. Would you like more information about it?");
        replay.NextResolveFailure = failure;
        await replay.FinalInput("Give me the full details of that email.");
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var receipt = await replay.CollectNotice();
        Assert.False(receipt.GetProperty("ok").GetBoolean());
        Assert.Equal(code, receipt.GetProperty("error_code").GetString());
        Assert.False(replay.PendingReply);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Admitted_read_early_model_join_releases_for_unrelated_human_input()
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        var origin = replay.TaskId;
        await replay.Answer("I found the conversation. Would you like more information about it?");
        replay.HoldNextResolve = true;
        await replay.FinalInput("Give me the full details of that email.");
        await replay.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var join = replay.Tool("early-join", "connector_control", new { task_id = origin, action = "status" });
        replay.InterruptTurn();
        await replay.Input("Hello.").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True((await join).GetProperty("started").GetBoolean());
        Assert.False(replay.PendingReply);
        replay.ReleaseResolve.TrySetResult(true);
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Hello does not cancel the earlier, explicitly accepted body read.
        // The read keeps the original account/thread and human authority.
        Assert.Equal(new[] { "mail_search", "mail_thread" }, replay.ExecutedCapabilities);
        var result = await replay.CollectNotice();
        Assert.Contains(Replay.ThreadBody, result.ToString());
        await replay.Answer("The requested conversation details are ready.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Finalized_read_queued_behind_observer_survives_later_hello_before_start()
    {
        await using var replay = new Replay(true, retainedMail: true, reasoningOutage: true, finalAdmission: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("initial", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation. Would you like more information about it?");
        await replay.BlockObserver();
        var accepted = replay.FinalInput("Give me the full details of that email.");
        var hello = replay.Input("Hello.");
        replay.ReleaseObserver.TrySetResult(true);
        await Task.WhenAll(accepted, hello).WaitAsync(TimeSpan.FromSeconds(3));
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "mail_search", "mail_thread" }, replay.ExecutedCapabilities);
        Assert.Contains(Replay.ThreadBody, (await replay.CollectNotice()).ToString());
        await replay.Answer("Here are the conversation details.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_read_returns_actual_result_on_original_tool_without_timer_generated_Live_turn(bool personal)
    {
        await using var replay = new Replay(personal, holdBackend: true, preferDirectReadReceipt: true);
        await replay.Resolve();
        var execution = replay.Tool("direct-read", "connector_execute", replay.ExecuteArguments);
        await replay.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // This crosses the former five-second synthetic-turn handoff boundary.
        await Task.Delay(TimeSpan.FromMilliseconds(5200));
        Assert.False(execution.IsCompleted);
        Assert.False(replay.Notice.Task.IsCompleted);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        replay.ReleaseBackend();
        var completed = await execution;
        Assert.False(completed.TryGetProperty("started", out _));
        Assert.Equal("completed", completed.GetProperty("connector").GetProperty("state").GetString());
        Assert.False(replay.Notice.Task.IsCompleted);
        await replay.Answer("I found the requested invoice.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false, "Hello.")]
    [InlineData(true, "Hello.")]
    [InlineData(false, "Tell me a short unrelated fact.")]
    [InlineData(true, "Tell me a short unrelated fact.")]
    public async Task Actual_new_human_input_hands_off_direct_read_without_cancelling_owned_work(bool personal, string input)
    {
        await using var replay = new Replay(personal, holdBackend: true, preferDirectReadReceipt: true);
        await replay.Resolve();
        var execution = replay.Tool("direct-with-later-input", "connector_execute", replay.ExecuteArguments);
        await replay.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        replay.InterruptTurn();
        await replay.Input(input).WaitAsync(TimeSpan.FromSeconds(3));
        var started = await execution;
        Assert.True(started.GetProperty("started").GetBoolean());
        Assert.False(replay.WorkToken.IsCancellationRequested);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        await replay.Answer("I am here.");
        Assert.False(replay.WorkToken.IsCancellationRequested);
        replay.ReleaseBackend();
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await replay.Collect(started.GetProperty("operation_id").GetString());
        await replay.Answer("I found the requested invoice.");
        Assert.Equal(1, replay.BackendExecutions);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Queued_new_text_cannot_release_the_previous_provider_reply_before_its_turn_ends()
    {
        await using var replay = new Replay(true, holdBackend: true, preferDirectReadReceipt: true);
        await replay.Resolve();
        var execution = replay.Tool("read-before-hello", "connector_execute", replay.ExecuteArguments);
        await replay.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Actual v37: local text observation releases the tool wait before
        // SendTextTurnAsync can send Hello after the previous provider turn.
        // The globally current human input is already Hello at that point.
        replay.SubmitText("Hello.");
        Assert.True((await execution).GetProperty("started").GetBoolean());
        replay.Output("An unverified response for the old connector turn.");
        Assert.DoesNotContain("unverified response", replay.AssistantTranscript);
        Assert.False(replay.WorkToken.IsCancellationRequested);
        replay.InterruptTurn();
        await replay.Input("Hello.");
        await replay.Answer("Hello! I am here.");
        Assert.Contains(replay.CompletedAnswers, text => text.Contains("I am here"));
        replay.ReleaseBackend();
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await replay.Collect((await execution).GetProperty("operation_id").GetString());
        await replay.Answer("The requested email result is ready.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_cancel_remains_immediate_while_original_read_tool_is_waiting(bool personal)
    {
        await using var replay = new Replay(personal, holdBackend: true, preferDirectReadReceipt: true);
        await replay.Resolve();
        var execution = replay.Tool("cancel-direct-read", "connector_execute", replay.ExecuteArguments);
        await replay.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        replay.InterruptTurn();
        await replay.Input("Cancel the search.").WaitAsync(TimeSpan.FromSeconds(3));
        await execution;
        Assert.True(replay.WorkToken.IsCancellationRequested);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        replay.ReleaseBackend();
        await replay.Drain();
        Assert.False(replay.Notice.Task.IsCompleted);
        Assert.Equal(1, replay.BackendExecutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_day_input_is_prompt_but_cannot_rewrite_an_already_dispatched_native_query(bool personal)
    {
        await using var replay = new Replay(personal, retainedMail: true, holdBackend: true, preferDirectReadReceipt: true);
        await replay.Resolve("Find today's emails.");
        var execution = replay.Tool("direct-day-read", "connector_execute", replay.ExecuteArguments);
        await replay.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dispatchedArguments = replay.LastExecutedArguments.GetRawText();
        replay.InterruptTurn();
        await replay.Input("Actually only yesterday.").WaitAsync(TimeSpan.FromSeconds(3));
        var started = await execution;
        Assert.True(started.GetProperty("started").GetBoolean());
        var guided = await replay.Tool("same-task-day-update", "connector_control", new
        {
            task_id = replay.TaskId, action = "guide", instruction = "Actually only yesterday.", expected_goal_revision = 0
        });
        Assert.False(guided.GetProperty("ok").GetBoolean(), guided.ToString());
        Assert.Equal("guidance_read_only", guided.GetProperty("state").GetString());
        Assert.Equal(0, guided.GetProperty("goal_revision").GetInt32());
        Assert.False(guided.GetProperty("this_proposal_dispatched").GetBoolean());
        Assert.Equal(dispatchedArguments, replay.LastExecutedArguments.GetRawText());
        Assert.Equal(1, replay.BackendExecutions);
        Assert.Equal(0, replay.ReasoningCalls);
        Assert.False(replay.WorkToken.IsCancellationRequested);
        Assert.Equal(1, replay.Resolves);
        replay.InterruptTurn();
        await replay.Input("Cancel the search.");
        replay.ReleaseBackend();
        await replay.Drain();
        Assert.True(replay.WorkToken.IsCancellationRequested);
        Assert.False(replay.Notice.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_read_returns_native_evidence_without_calling_an_unavailable_secondary_reasoner(bool personal)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: true,
            holdReasoning: true, preferDirectReadReceipt: true);
        await replay.Resolve("Find the relevant email conversation.");
        var execution = replay.Tool("direct-read-with-outage", "connector_execute", replay.ExecuteArguments);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("completed", result.GetProperty("connector").GetProperty("state").GetString());
        Assert.Contains("Research proposal", result.ToString());
        Assert.DoesNotContain("ai_provider_unavailable", result.ToString());
        Assert.False(result.TryGetProperty("started", out _));
        Assert.False(replay.Notice.Task.IsCompleted);
        Assert.False(replay.ReasoningStarted.Task.IsCompleted);
        Assert.Equal(0, replay.ReasoningCalls);
        Assert.Equal(1, replay.BackendExecutions);
        await replay.Answer("I found the research proposal conversation.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_started_receipt_does_not_release_the_connector_reply_to_generic_Live_speech(bool personal)
    {
        await using var replay = new Replay(personal, holdBackend: true);
        await replay.Resolve();
        var started = await replay.Tool("execute", "connector_execute", replay.ExecuteArguments);
        Assert.True(started.GetProperty("started").GetBoolean());
        replay.Output("I am just an LLM and cannot help with that.");
        Assert.Empty(replay.AssistantTranscript);
        replay.TurnComplete();
        await replay.Drain();
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        replay.ReleaseBackend();
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        replay.Output("Another answer before collecting the result.");
        Assert.Empty(replay.AssistantTranscript);
        await replay.Collect(started.GetProperty("operation_id").GetString());
        replay.Output("I found the thread.");
        Assert.Equal("I found the thread.", replay.AssistantTranscript);
        replay.TurnComplete();
        await replay.Drain();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spoken_cancel_the_search_stops_work_before_a_model_supplied_task_reference(bool personal)
    {
        await using var replay = new Replay(personal, holdBackend: true);
        await replay.Resolve();
        var started = await replay.Tool("execute", "connector_execute", replay.ExecuteArguments);
        Assert.True(started.GetProperty("started").GetBoolean());
        await replay.Answer("The search is running.");
        await replay.Input("Cancel the search.");
        Assert.True(replay.WorkToken.IsCancellationRequested);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        var receipt = await replay.Tool("cancel", "connector_control", new { action = "cancel", task_id = "gmail" });
        Assert.True(receipt.GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", receipt.GetProperty("state").GetString());
        replay.ReleaseBackend();
        await replay.Drain();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.False(replay.Notice.Task.IsCompleted);
        Assert.Equal(1, replay.BackendExecutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_only_answer_releases_activity_through_the_real_answer_observer(bool personal)
    {
        await using var replay = new Replay(personal);
        await replay.Resolve();
        replay.TurnComplete();
        await replay.Drain();
        AssertThinking();
        await replay.Answer("No connected-service operation was performed.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        AssertListening();
        Assert.Equal(replay.TaskId, Assert.Single(replay.Completions));
        Assert.Equal(0, replay.BackendExecutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_task_survives_tool_boundary_and_unrelated_answer_until_collected_answer_completes(bool personal)
    {
        await using var replay = new Replay(personal, holdBackend: true);
        await replay.Resolve();
        var execution = replay.Tool("execute", "connector_execute", replay.ExecuteArguments);
        await replay.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        replay.TurnComplete();
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.False(replay.WorkToken.IsCancellationRequested);
        var started = await execution;
        Assert.True(started.GetProperty("started").GetBoolean());
        var operationId = started.GetProperty("operation_id").GetString();

        await replay.Input("Tell me a short unrelated fact.");
        await replay.Answer("A triangle has three sides.");
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.False(replay.WorkToken.IsCancellationRequested);
        Assert.Empty(replay.Completions);

        replay.ReleaseBackend();
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AssertThinking();
        var collected = await replay.Collect(operationId);
        Assert.Equal("completed", collected.GetProperty("connector").GetProperty("state").GetString());
        replay.TurnComplete();
        await replay.Drain();
        AssertThinking();
        Assert.Empty(replay.Completions);

        replay.Output("I found the requested invoice.");
        Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);
        AssertThinking(); // Output alone is not the completed-answer boundary.
        replay.TurnComplete();
        await replay.Drain();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        AssertListening();
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
        Assert.Equal(replay.TaskId, Assert.Single(replay.Completions));
        Assert.True(replay.WorkToken.IsCancellationRequested);
        Assert.Equal(1, replay.BackendExecutions);
    }

    [Fact]
    public async Task Unread_background_result_and_other_current_request_identity_cannot_end_the_task()
    {
        await using var replay = new Replay(personal: true, holdBackend: true);
        await replay.Resolve();
        var started = await replay.Tool("execute", "connector_execute", replay.ExecuteArguments);
        Assert.True(started.GetProperty("started").GetBoolean());
        var operationId = started.GetProperty("operation_id").GetString();
        replay.ReleaseBackend();
        await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await replay.Answer("Here is an unrelated answer.");
        AssertListening();
        AssertThinking();
        Assert.Empty(replay.Completions); // Settled does not mean collected.

        await replay.Collect(operationId);
        var deliveryIdentity = replay.Commands.ConnectorUserRequestIdentity;
        // This is the real local text input entry point, which establishes a
        // new identity before its provider transcription/answer arrives.
        replay.Commands.ObserveSubmittedJarvisText("Tell me another short fact.", replay.SessionToken);
        await replay.Input("Tell me another short fact.");
        Assert.NotEqual(deliveryIdentity, replay.Commands.ConnectorUserRequestIdentity);
        await replay.Answer("A square has four sides.");
        AssertListening();
        AssertThinking();
        Assert.Empty(replay.Completions);
        Assert.False(replay.WorkToken.IsCancellationRequested);

        await replay.Collect(operationId); // Re-delivery belongs to this turn.
        await replay.Answer("The requested invoice was found.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        AssertListening();
        Assert.Equal(replay.TaskId, Assert.Single(replay.Completions));
        Assert.Equal(1, replay.BackendExecutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_frame_output_is_suppressed_and_only_post_receipt_answer_ends_owned_Thinking(bool personal)
    {
        await using var replay = new Replay(personal, holdBackend: true);
        await replay.Resolve();
        var execution = replay.Tool("execute", "connector_execute", replay.ExecuteArguments,
            prematureAnswer: "An unverified early answer.");
        await replay.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("", replay.AssistantTranscript);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        replay.ReleaseBackend();
        var result = await execution;
        Assert.Equal("completed", result.GetProperty("connector").GetProperty("state").GetString());
        AssertThinking();
        Assert.Empty(replay.Completions);
        replay.TurnComplete();
        await replay.Drain();
        AssertThinking();

        await replay.Answer("The requested invoice was found.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        AssertListening();
        Assert.Equal(replay.TaskId, Assert.Single(replay.Completions));
        Assert.Equal(1, replay.BackendExecutions);
    }

    [Fact]
    public async Task Personal_quota_is_terminal_through_real_tool_dispatch_and_never_repeats_reasoning_or_backend_work()
    {
        await using var replay = new Replay(personal: true, quota: true);
        await replay.Resolve();
        var arguments = new { task_id = replay.TaskId, capability_id = "mail_search", arguments_json = "{}" };
        var failed = await replay.Tool("quota", "connector_execute", arguments);
        var connector = failed.GetProperty("connector");
        Assert.Equal("failed", connector.GetProperty("state").GetString());
        Assert.Equal("ai_rate_limit", connector.GetProperty("errorCategory").GetString());
        Assert.False(connector.GetProperty("verified").GetBoolean());
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, replay.ReasoningCalls);
        Assert.Equal(0, replay.BackendExecutions);

        replay.TurnComplete();
        await replay.Drain();
        var repeat = await replay.Tool("quota-repeat", "connector_execute", arguments);
        Assert.False(repeat.GetProperty("this_proposal_dispatched").GetBoolean());
        await replay.Answer("The Personal AI provider rate limit stopped this request.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        AssertListening();
        Assert.Equal(1, replay.ReasoningCalls);
        Assert.Equal(0, replay.BackendExecutions);
        Assert.Empty(replay.Completions);
    }

    [Theory]
    [InlineData(true, true, "status")]
    [InlineData(true, true, "result")]
    [InlineData(true, true, "guide")]
    [InlineData(false, true, "status")]
    [InlineData(false, true, "result")]
    [InlineData(false, true, "guide")]
    [InlineData(true, false, "result")]
    [InlineData(false, false, "result")]
    public async Task Spoken_yes_recovers_a_terminal_search_through_control_and_later_stale_execute(
        bool personal, bool reasoningOutage, string control)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: reasoningOutage);
        await replay.Resolve("Find the relevant email conversation.");
        var origin = replay.TaskId;
        var searched = await replay.Tool("search", "connector_execute", replay.ExecuteArguments);
        Assert.Equal("completed", searched.GetProperty("connector").GetProperty("state").GetString());
        Assert.Equal(2, replay.ReasoningCalls);
        await replay.Answer("I found the conversation. Would you like more information about it?");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);

        replay.ReasoningOutage = false;
        await replay.Input("Yes, please.");
        var identity = replay.Commands.ConnectorUserRequestIdentity;
        var details = await replay.Tool("details-control", "connector_control", new { task_id = origin, action = control });
        Assert.True(details.GetProperty("ok").GetBoolean(), details.ToString());
        Assert.Contains(Replay.ThreadBody, details.ToString());
        Assert.NotEqual(origin, replay.TaskId);
        Assert.Equal(reasoningOutage ? new[] { "mail_search", "mail_thread" }
            : new[] { "mail_search", "mail_thread", "mail_thread" }, replay.ExecutedCapabilities);

        // Gemini may emit a tool-only boundary before selecting another tool.
        // This is still the same spoken human request, not an empty new turn.
        replay.TurnComplete();
        await replay.Drain();
        Assert.Equal(identity, replay.Commands.ConnectorUserRequestIdentity);
        var beforeRepeat = (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves);
        var repeated = await replay.Tool("details-stale-execute", "connector_execute", new
        {
            task_id = origin, capability_id = "mail_thread", arguments_json = "{\"id\":\"stale-model-reference\"}"
        });
        Assert.True(repeated.GetProperty("ok").GetBoolean(), repeated.ToString());
        Assert.Contains(Replay.ThreadBody, repeated.ToString());
        Assert.Equal(beforeRepeat, (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves));
        Assert.Equal("aabbccdd22", replay.LastExecutedArguments.GetProperty("id").GetString());
        await replay.Answer("The sender requested a proposal and the recipient replied with it.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(true, "What did she ask me for?", "result")]
    [InlineData(true, "Did I reply?", "status")]
    [InlineData(true, "What was the latest message?", "guide")]
    [InlineData(false, "What did she ask me for?", "result")]
    [InlineData(false, "Did I reply?", "status")]
    [InlineData(false, "What was the latest message?", "guide")]
    public async Task Spoken_question_uses_body_evidence_after_reasoning_503_across_a_tool_only_boundary(
        bool personal, string question, string control)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: true);
        await replay.Resolve("Find the email conversation and give the details.");
        var origin = replay.TaskId;
        var searched = await replay.Tool("search-details", "connector_execute", replay.ExecuteArguments);
        Assert.Equal("completed", searched.GetProperty("connector").GetProperty("state").GetString());
        Assert.Equal(2, replay.ReasoningCalls);
        Assert.Equal(new[] { "mail_search", "mail_thread" }, replay.ExecutedCapabilities);
        await replay.Answer("I found and retrieved the conversation, but the reasoning service had a temporary problem.");

        await replay.Input(question);
        var beforeFollowup = (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves);
        var details = await replay.Tool("question-control", "connector_control", new { task_id = origin, action = control });
        Assert.True(details.GetProperty("ok").GetBoolean(), details.ToString());
        Assert.Contains(Replay.ThreadBody, details.ToString());
        Assert.Equal(question, details.GetProperty("answer_scope").GetProperty("request").GetString());
        replay.TurnComplete();
        await replay.Drain();
        var repeated = await replay.Tool("question-stale-execute", "connector_execute", new
        {
            task_id = origin, capability_id = "mail_thread", arguments_json = "{\"id\":\"stale-model-reference\"}"
        });
        Assert.True(repeated.GetProperty("ok").GetBoolean(), repeated.ToString());
        Assert.Contains(Replay.ThreadBody, repeated.ToString());
        Assert.Equal(beforeFollowup, (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves));
        await replay.Answer("The sender asked for a proposal, and the recipient replied with it.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spoken_exact_open_keeps_its_retained_destination_after_control_and_tool_only_boundary(bool personal)
    {
        await using var replay = new Replay(personal, retainedMail: true, reasoningOutage: true);
        await replay.Resolve("Find the relevant email conversation.");
        await replay.Tool("search-for-opening", "connector_execute", replay.ExecuteArguments);
        await replay.Answer("I found the conversation. I can open it.");
        await replay.Input("Open that thread in a new tab.");
        var beforeOpen = (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves);
        var open = await replay.Tool("retained-open", "connector_control", new { action = "open", task_id = replay.TaskId });
        Assert.True(open.GetProperty("ok").GetBoolean(), open.ToString());
        replay.TurnComplete();
        await replay.Drain();
        var launched = await replay.Tool("model-inbox-open", "open_url", new { url = "https://mail.google.com/mail/u/0/#inbox" });
        Assert.True(launched.GetProperty("ok").GetBoolean(), launched.ToString());
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aabbccdd22", Assert.Single(replay.OpenedUrls));
        Assert.Equal(beforeOpen, (replay.BackendExecutions, replay.ReasoningCalls, replay.Resolves));
        await replay.Answer("Opened the exact conversation.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    private static void AssertThinking()
    {
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(("Thinking…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(20)));
    }

    private static void AssertListening()
    {
        Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);
        Assert.Equal("Listening for your next request...", KeyboardWtfState.UiDetail);
    }

    private sealed class Replay : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        private readonly GeminiLiveConversationService _live;
        private readonly JarvisAutomationService _automation;
        private readonly JarvisConnectorController _controller;
        private readonly CancellationTokenSource _session = new();
        private readonly object _diagnostics;
        private readonly LiveToolCallDispatcher _dispatcher;
        private readonly bool _personal;
        private readonly bool _holdBackend;
        private readonly bool _retainedMail;
        private readonly bool _holdReasoning;
        private readonly TaskCompletionSource<bool> _reasoningRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<JsonElement> _backend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, JsonElement> _receipts = new();
        private readonly MethodInfo _handle = typeof(GeminiLiveConversationService).GetMethod("HandleServerMessage", PrivateInstance)!;
        private readonly MethodInfo _execute = typeof(GeminiLiveConversationService).GetMethod("ExecuteToolAsync", PrivateInstance)!;
        internal const string ThreadBody = "The sender requested a research proposal. The recipient replied with the proposal. The latest message confirms receipt.";
        internal string TaskId { get; private set; } = Guid.NewGuid().ToString("N");
        internal CommandRegistry Commands { get; }
        internal CancellationToken SessionToken => _session.Token;
        internal CancellationToken WorkToken { get; private set; }
        internal TaskCompletionSource<bool> WorkStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReasoningStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Notice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Completions { get; } = new();
        internal ConcurrentQueue<string> CompletedAnswers { get; } = new();
        internal IEnumerable<string> HistoryText => ((System.Collections.IEnumerable)typeof(GeminiLiveConversationService)
            .GetField("_conversationContext", PrivateInstance)!.GetValue(_live)!).Cast<object>()
            .Select(entry => (string)entry.GetType().GetProperty("Text")!.GetValue(entry)!);
        internal JarvisLifecycleAnnouncement LastNotice;
        internal string NoticeFailure;
        internal bool HoldNextResolve;
        internal string NextResolveFailure;
        internal TaskCompletionSource<bool> ResolveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseResolve { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ResolveFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool PendingReply => _controller.HoldsPendingReply(Commands.ConnectorUserRequestSnapshot);
        internal TaskCompletionSource<bool> ReleaseObserver { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task BlockObserver()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.Observe((_, _) => { started.TrySetResult(true); ReleaseObserver.Task.GetAwaiter().GetResult(); }, "");
            return started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        internal async Task WaitAdmissionSettled()
        {
            for (var i = 0; i < 100 && KeyboardWtfState.HasLongTaskUiOwner; i++) await Task.Delay(10);
        }
        internal int BackendExecutions;
        internal int ReasoningCalls;
        internal int Resolves;
        internal int SelectionCaptures;
        internal bool ReasoningOutage;
        internal bool SearchHasMore;
        internal ConcurrentQueue<string> ExecutedCapabilities { get; } = new();
        internal ConcurrentQueue<string> OpenedUrls { get; } = new();
        internal JsonElement LastExecutedArguments;
        internal object ExecuteArguments => new { task_id = TaskId, capability_id = "mail_search", arguments_json = "{\"query\":\"invoice\"}" };
        internal string AssistantTranscript => ((StringBuilder)typeof(GeminiLiveConversationService)
            .GetField("_currentAssistantTranscript", PrivateInstance)!.GetValue(_live)!).ToString();

        internal Replay(bool personal, bool holdBackend = false, bool quota = false, bool retainedMail = false,
            bool reasoningOutage = false, bool holdReasoning = false, bool preferDirectReadReceipt = false, bool finalAdmission = false)
        {
            _personal = personal;
            _holdBackend = holdBackend;
            _retainedMail = retainedMail;
            _holdReasoning = holdReasoning;
            ReasoningOutage = reasoningOutage;
            var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
            settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
            var notifications = new NotificationService(null);
            _live = new(notifications, settings, null);
            _automation = new(notifications, settings, new(Path.Combine(_directory, "history.json")),
                new(Path.Combine(_directory, "mappings.json")), () => { }, null, null, null, simpleUrlLauncher: (url, _) =>
                {
                    OpenedUrls.Enqueue(url);
                    return (true, "Opened.", "fixture");
                });
            Commands = new(null, null, null, settings, notifications, _live, _automation,
                captureSelectedText: _ => { SelectionCaptures++; return Task.FromResult(new Dictionary<string, object>
                    { ["ok"] = true, ["text"] = "ordinary selected text", ["source"] = "current_selection" }); });
            _controller = new(Request, () => "fixture-account-owner", () => _session.Token,
                () => Commands.ConnectorUserRequest, (announcement, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (announcement.CanPublish) { LastNotice = announcement; Notice.TrySetResult(true); }
                    if (NoticeFailure == "throw") throw new InvalidOperationException("Fixture notice unavailable");
                    if (NoticeFailure == "false") return Task.FromResult(false);
                    return Task.FromResult(true);
                }, userRequestIdentity: () => Commands.ConnectorUserRequestIdentity,
                userRequestSnapshot: () => Commands.ConnectorUserRequestSnapshot,
                reasonRead: quota ? (_, _, _, _, _, _, _) =>
                {
                    Interlocked.Increment(ref ReasoningCalls);
                    throw new PersonalAiException(PersonalAiFailure.Quota);
                } : retainedMail ? async (_, _, _, prompt, _, _, token) =>
                {
                    Interlocked.Increment(ref ReasoningCalls);
                    ReasoningStarted.TrySetResult(true);
                    if (_holdReasoning) await _reasoningRelease.Task.WaitAsync(token);
                    if (ReasoningOutage) throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 };
                    var hasBody = prompt.Contains(ThreadBody, StringComparison.Ordinal);
                    if (!hasBody) return JsonSerializer.Serialize(new
                    {
                        action = "execute", capability = "mail_thread", arguments_json = "{\"id\":\"aabbccdd22\"}",
                        answer = "", references = Array.Empty<string>(), partial = false
                    });
                    return JsonSerializer.Serialize(new
                    {
                        action = "answer", capability = "", arguments_json = "{}",
                        answer = hasBody ? ThreadBody : "I found the requested conversation.",
                        references = new[] { prompt.Contains("\"reference\":\"r2\"", StringComparison.Ordinal) ? "r2" : "r1" }, partial = true
                    });
                } : null, preferDirectReadReceipt: preferDirectReadReceipt);
            _automation.Connectors = _controller;
            _live.SetToolExecutor(Commands.ExecuteJarvisToolAsync);
            _live.SetUserTranscriptObserver(Commands.ObserveJarvisTranscript,
                Commands.ObserveImmediateJarvisControl, Commands.CompleteJarvisUserTurn);
            _live.SetConnectorReplyOwnership(() => _controller.HoldsPendingReply(Commands.ConnectorUserRequestSnapshot),
                answer => { CompletedAnswers.Enqueue(answer); _controller.ObserveCompletedReply(answer, Commands.ConnectorUserRequestIdentity); });
            if (finalAdmission) _live.SetConnectorFinalInputObserver(Commands.ReserveFinalConnectorRead);
            typeof(GeminiLiveConversationService).GetField("_sessionCts", PrivateInstance)!.SetValue(_live, _session);
            var diagnosticsType = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
            _diagnostics = Activator.CreateInstance(diagnosticsType, "connector-answer-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
            diagnosticsType.GetProperty("UsesPersonalToolProtocol")!.SetValue(_diagnostics, personal);
            _dispatcher = new(_session.Token, (name, args, token) =>
                (Task<Dictionary<string, object>>)_execute.Invoke(_live, [name, args, token])!,
                (build, onSent, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var response = build();
                    if (response is not null)
                        foreach (var receipt in JsonSerializer.SerializeToElement(response).GetProperty("toolResponse").GetProperty("functionResponses").EnumerateArray())
                            _receipts[receipt.GetProperty("id").GetString()!] = receipt.GetProperty("response").Clone();
                    onSent();
                    return Task.CompletedTask;
                });
            diagnosticsType.GetProperty("ToolCalls")!.SetValue(_diagnostics, _dispatcher);
        }

        internal async Task Resolve(string request = "Find recent invoices in Gmail.")
        {
            await Input(request);
            var receipt = await Tool("resolve", "connector_resolve", new { connector_id = "gmail" });
            Assert.Equal(TaskId, receipt.GetProperty("task_id").GetString());
        }

        internal async Task Input(string text)
        {
            Push(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text } } }));
            await Drain();
        }

        internal async Task FinalInput(string text, string premature = null)
        {
            // Actual Personal Live v34 emits ACTIVITY_START, transcription,
            // ACTIVITY_END. The public protocol has no transcription.finished.
            Push(JsonSerializer.Serialize(new { voiceActivity = new { type = "ACTIVITY_START" } }));
            Push(JsonSerializer.Serialize(new { voiceActivity = new { type = "ACTIVITY_END" },
                serverContent = new { inputTranscription = new { text },
                outputTranscription = new { text = premature ?? "" } } }));
            await Drain();
        }

        internal void Activity(string type) => Push(JsonSerializer.Serialize(new { voiceActivity = new { type } }));
        internal void SubmitText(string text) => Commands.ObserveSubmittedJarvisText(text, _session.Token);

        internal Task<JsonElement> CollectNotice()
        {
            var ids = System.Text.RegularExpressions.Regex.Match(LastNotice.Detail,
                @"task_id=([a-f0-9]+); operation_id=([a-f0-9]+)");
            Assert.True(ids.Success, LastNotice.Detail);
            return Tool(Guid.NewGuid().ToString("N"), "connector_control", new { task_id = ids.Groups[1].Value,
                action = "result", operation_id = ids.Groups[2].Value });
        }

        internal async Task<JsonElement> Tool(string id, string name, object args, string prematureAnswer = null)
        {
            var frame = new Dictionary<string, object>
            {
                ["toolCall"] = new { functionCalls = new[] { new { id, name, args } } }
            };
            if (!_personal || prematureAnswer is not null)
            {
                var content = new Dictionary<string, object> { ["turnComplete"] = true };
                if (prematureAnswer is not null) content["outputTranscription"] = new { text = prematureAnswer };
                frame["serverContent"] = content;
            }
            Push(JsonSerializer.Serialize(frame));
            await Drain();
            Assert.True(_receipts.TryGetValue(id, out var receipt), "The real dispatcher did not publish this tool's receipt.");
            return receipt;
        }

        internal Task<JsonElement> Collect(string operationId) => Tool(Guid.NewGuid().ToString("N"), "connector_control",
            new { task_id = TaskId, action = "result", operation_id = operationId });
        internal void Output(string text) => Push(JsonSerializer.Serialize(new { serverContent = new { outputTranscription = new { text } } }));
        internal void TurnComplete() => Push("""{"serverContent":{"turnComplete":true}}""");
        internal void Interrupt() => Push("""{"serverContent":{"interrupted":true}}""");
        // Live's documented interruption sequence and the physical session
        // both end the interrupted provider turn before the new transcript.
        internal void InterruptTurn() => Push("""{"serverContent":{"interrupted":true,"turnComplete":true}}""");
        internal async Task Answer(string text)
        {
            Output(text);
            TurnComplete();
            await Drain();
        }
        internal Task Drain() => _dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        internal void ReleaseBackend() => _backend.TrySetResult(CompletedResult());
        internal void ReleaseReasoning() => _reasoningRelease.TrySetResult(true);
        private void Push(string frame) => _handle.Invoke(_live, [frame, new TaskCompletionSource<bool>(), _diagnostics, _session.Token]);

        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (path == "v1/connectors/resolve")
            {
                if (NextResolveFailure is { } failure)
                {
                    NextResolveFailure = null;
                    if (failure == "json") throw new JsonException("Fixture invalid schema");
                    if (failure == "timeout") throw new TaskCanceledException("Fixture transport timeout");
                    throw new KeyboardWtf.Services.PaidStaging.PaidApiException(System.Net.HttpStatusCode.ServiceUnavailable, "Fixture backend unavailable");
                }
                if (HoldNextResolve)
                {
                    HoldNextResolve = false; ResolveStarted.TrySetResult(true);
                    await ReleaseResolve.Task; ResolveFinished.TrySetResult(true);
                }
                if (Interlocked.Increment(ref Resolves) > 1) TaskId = Guid.NewGuid().ToString("N");
                return JsonSerializer.SerializeToElement(new
                {
                    taskId = TaskId, state = "ready", connectorId = "gmail", accountId = "aabbccddaabbccddaabbccddaabbccdd",
                    choices = new[] { new { accountId = "aabbccddaabbccddaabbccddaabbccdd", label = "reader@example.test" } },
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    capabilities = _retainedMail
                        ? new[] { "mail_search", "mail_thread", "mail_read" }.Select(capability => new
                        {
                            id = capability, writes = false, activity = "Searching Gmail…",
                            arguments = capability == "mail_search"
                                ? new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" }
                                : new Dictionary<string, string> { ["id"] = "observed resource identifier" }
                        }).ToArray()
                        : new[] { new { id = "mail_search", writes = false, activity = "Searching Gmail…",
                            arguments = new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" } } }
                });
            }
            if (path == "v1/connectors/execute")
            {
                Interlocked.Increment(ref BackendExecutions);
                WorkToken = token;
                WorkStarted.TrySetResult(true);
                if (_retainedMail)
                {
                    var request = JsonSerializer.SerializeToElement(body);
                    var capability = request.GetProperty("capabilityId").GetString();
                    ExecutedCapabilities.Enqueue(capability);
                    LastExecutedArguments = request.GetProperty("arguments").Clone();
                    if (_holdBackend) await _backend.Task.WaitAsync(token);
                    object data = capability == "mail_search"
                        ? new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Research proposal" } }, hasMore = SearchHasMore, pageItemCount = 1 }
                        : new { id = "aabbccdd22", messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", text = ThreadBody, subject = "Research proposal" } } };
                    return JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data, truncated = false } });
                }
                return _holdBackend ? await _backend.Task.WaitAsync(token) : CompletedResult();
            }
            if (path.StartsWith("v1/connectors/tasks/", StringComparison.Ordinal) && path.EndsWith("/complete", StringComparison.Ordinal))
            {
                Completions.Enqueue(path.Split('/')[3]);
                return JsonSerializer.SerializeToElement(new { state = "completed" });
            }
            if (path.StartsWith("v1/connectors/tasks/", StringComparison.Ordinal) && path.EndsWith("/cancel", StringComparison.Ordinal))
                return JsonSerializer.SerializeToElement(new { state = "cancelled" });
            throw new InvalidOperationException("Unexpected fixture backend route: " + path);
        }

        private static JsonElement CompletedResult() => JsonSerializer.SerializeToElement(new
        {
            state = "completed", verified = true,
            result = new { data = new { id = "fixture-invoice", text = "An isolated test invoice." } }
        });

        public async ValueTask DisposeAsync()
        {
            ReleaseResolve.TrySetResult(true);
            ReleaseObserver.TrySetResult(true);
            _session.Cancel();
            _dispatcher.Stop();
            ReleaseBackend();
            ReleaseReasoning();
            await Drain();
            await _controller.ResetAsync();
            _dispatcher.Dispose();
            _automation.Dispose();
            _live.Dispose();
            _session.Dispose();
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
