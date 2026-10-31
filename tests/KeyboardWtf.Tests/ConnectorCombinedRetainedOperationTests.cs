namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorCombinedRetainedOperationTests
{
    [Fact]
    public async Task Spoken_person_lookup_repairs_wrong_model_direction_then_opens_only_verified_recovered_thread()
    {
        await using var f = new Fixture(native: true) { RecoverPersonLookup = true };
        f.Commands.ObserveJarvisTranscript("Could you find my emails from Marccel?", default);
        var resolved = await f.Commands.ExecuteJarvisToolAsync("connector_resolve",
            JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), default);
        var task = (string)resolved["task_id"];
        // Actual failed model proposals used recipient direction. Do not feed
        // the controller an ideal corrected sender query in this regression.
        var found = await f.Commands.ExecuteJarvisToolAsync("connector_execute", JsonSerializer.SerializeToElement(new
        { task_id = task, capability_id = "mail_search", arguments_json = "{\"query\":\"to:Marccel\"}" }), default);
        Assert.True(found.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(found));
        Assert.Equal("from:\"Marccel\"", f.NativeQueries[0]);
        Assert.Equal(2, f.Searches);
        Assert.Empty(f.ReasoningRequests);
        Assert.Contains("Marcel", JsonSerializer.Serialize(found));
        f.Commands.CompleteJarvisUserTurn();

        foreach (var followup in new[] { "Can you open that in a new tab?", "Open that email thread again.", "Can you open it again?" })
        {
            f.Commands.ObserveJarvisTranscript(followup, default);
            var opened = await f.Propose("browser_automation", task);
            Assert.True(opened.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(opened));
            Assert.EndsWith("#all/aaaaaaaa", f.Urls[^1]);
            f.Commands.CompleteJarvisUserTurn();
        }
        Assert.Equal(3, f.Urls.Count);
        Assert.Equal(2, f.Searches);
        Assert.Empty(f.ReasoningRequests);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delayed_open_completion_cannot_replace_a_newer_turns_selected_conversation(bool earlierFinishesLast)
    {
        await using var f = new Fixture(native: true) { MultipleThreads = true };
        await f.Retain();
        var firstDispatch = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new List<string>();
        f.Input("Can you open that in a new tab?");
        var earlier = f.Controller.TryOpenRetainedResourcesAsync(f.Commands.ConnectorUserRequestSnapshot,
            (url, _) => { dispatched.Add(url); return firstDispatch.Task; }, default,
            proposedUrl: "https://mail.google.com/mail/u/0/#thread/aaaaaaaa");
        Assert.Single(dispatched);

        // A browser response may finish after another user turn has already
        // selected and opened a different observed result from the same search.
        f.Commands.CompleteJarvisUserTurn();
        f.Input("Can you open that in a new tab?");
        var secondDispatch = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = f.Controller.TryOpenRetainedResourcesAsync(f.Commands.ConnectorUserRequestSnapshot,
            (url, _) => { dispatched.Add(url); return secondDispatch.Task; }, default,
            proposedUrl: "https://mail.google.com/mail/u/0/#thread/bbbbbbbb");
        Assert.EndsWith("#all/bbbbbbbb", dispatched[^1]);
        if (earlierFinishesLast)
        {
            secondDispatch.SetResult(new() { ["ok"] = true });
            Assert.True((await newer).GetValueOrDefault("ok") is true);
        }
        firstDispatch.SetResult(new() { ["ok"] = true });
        Assert.True((await earlier).GetValueOrDefault("ok") is true);
        if (!earlierFinishesLast)
        {
            secondDispatch.SetResult(new() { ["ok"] = true });
            Assert.True((await newer).GetValueOrDefault("ok") is true);
        }

        f.Commands.CompleteJarvisUserTurn();
        f.Input("Open it again.");
        var repeated = await f.Propose("open_url", "unused-task");
        Assert.True(repeated.GetValueOrDefault("ok") is true);
        Assert.EndsWith("#all/bbbbbbbb", Assert.Single(f.Urls));
        Assert.Equal(1, f.Searches);
        Assert.Equal(0, f.Reads);
        Assert.Empty(f.ReasoningRequests);
    }

    [Theory]
    [InlineData("aaaaaaaa", true, "open_url")]
    [InlineData("bbbbbbbb", true, "open_url")]
    [InlineData("cccccccc", false, "open_url")]
    [InlineData("aaaaaaaa", true, "mail_thread")]
    [InlineData("bbbbbbbb", true, "mail_thread")]
    [InlineData("cccccccc", false, "mail_thread")]
    [InlineData("11111111", true, "mail_read")]
    [InlineData("cccccccc", false, "mail_read")]
    public async Task Explicit_open_can_select_one_observed_candidate_then_read_it_on_a_later_turn(string proposed, bool known, string proposal)
    {
        await using var f = new Fixture(native: true) { MultipleThreads = true };
        var old = await f.Retain();
        f.Input("Can you open that in a new tab?");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)?.Invoke();
        var tool = proposal == "open_url" ? proposal : "connector_execute";
        var args = proposal == "open_url"
            ? JsonSerializer.SerializeToElement(new { url = "https://mail.google.com/mail/u/0/#thread/" + proposed })
            : JsonSerializer.SerializeToElement(new { task_id = old, capability_id = proposal,
                arguments_json = JsonSerializer.Serialize(new { id = proposed }) });
        var result = await f.Commands.ExecuteJarvisToolAsync(tool, args, default);
        Assert.Equal(known, result.GetValueOrDefault("ok") is true);
        if (!known) { Assert.Empty(f.Urls); return; }
        var expectedThread = proposal == "mail_read" ? "aaaaaaaa" : proposed;
        Assert.EndsWith("#all/" + expectedThread, Assert.Single(f.Urls));
        Assert.Equal(0, f.Reads); Assert.Equal(1, f.Searches); Assert.Empty(f.ReasoningRequests);
        await f.Commands.ExecuteJarvisToolAsync(tool, args, default);
        Assert.Single(f.Urls);
        f.Commands.CompleteJarvisUserTurn();
        f.Input("Could give me more details on that conversation.");
        var read = f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default);
        Assert.NotNull(read); read();
        var details = await f.Propose("connector_execute", "old-model-task");
        Assert.True(details.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(details));
        Assert.Equal(expectedThread, f.LastReadThread); Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches);
    }

    [Theory]
    [InlineData("aaaaaaaa", true)]
    [InlineData("bbbbbbbb", true)]
    [InlineData("cccccccc", false)]
    public async Task Multiple_search_threads_allow_an_observed_native_read_without_reusing_execution(string proposed, bool known)
    {
        await using var f = new Fixture(native: true) { MultipleThreads = true };
        var old = await f.Retain();
        f.Input("Could give me more details on that conversation.");
        Assert.Null(f.Commands.ReserveFinalConnectorRead("Could give me more details on that conversation.", default));
        var receipt = await f.Commands.ExecuteJarvisToolAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = old, capability_id = "mail_thread", arguments_json = JsonSerializer.Serialize(new { id = proposed }) }), default);
        Assert.Equal(known, receipt.GetValueOrDefault("ok") is true);
        Assert.Equal(known ? 1 : 0, f.Reads);
        Assert.Equal(1, f.Searches);
        Assert.Empty(f.ReasoningRequests);
        if (known)
        {
            Assert.NotEqual(old, f.LastReadTask);
            Assert.Equal(proposed, f.LastReadThread);
            f.Commands.CompleteJarvisUserTurn();
            f.Input("Open it again.");
            Assert.True((await f.Propose("open_url", old)).GetValueOrDefault("ok") is true);
            Assert.EndsWith("#all/" + proposed, Assert.Single(f.Urls));
        }
    }

    [Theory]
    [InlineData("aaaaaaaa", true)]
    [InlineData("11111111", true)]
    [InlineData("bbbbbbbb", false)]
    public async Task Model_thread_link_outside_shortcut_grammar_uses_only_observed_identity(string proposedId, bool known)
    {
        await using var f = new Fixture(native: true);
        await f.Retain();
        f.Input("I'd like this conversation displayed in another Chrome tab.");
        var receipt = await f.Commands.ExecuteJarvisToolAsync("open_url", JsonSerializer.SerializeToElement(new
            { url = "https://mail.google.com/mail/u/0/#thread/" + proposedId }), default);
        Assert.Equal(known, receipt.GetValueOrDefault("ok") is true);
        if (known)
            Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", Assert.Single(f.Urls));
        else Assert.Empty(f.Urls);
        Assert.Equal(1, f.Searches);
        Assert.Empty(f.ReasoningRequests);
        if (known)
        {
            await f.Commands.ExecuteJarvisToolAsync("open_url", JsonSerializer.SerializeToElement(new
                { url = "https://mail.google.com/mail/u/0/#thread/" + proposedId }), default);
            Assert.Single(f.Urls);
        }
    }

    [Fact]
    public async Task Model_thread_link_cannot_switch_the_verified_mail_account()
    {
        await using var f = new Fixture(native: true);
        await f.Retain();
        f.Input("I'd like this conversation displayed in another Chrome tab.");
        var receipt = await f.Commands.ExecuteJarvisToolAsync("open_url", JsonSerializer.SerializeToElement(new
            { url = "https://mail.google.com/mail/?authuser=other%40example.test#thread/aaaaaaaa" }), default);
        Assert.False(receipt.GetValueOrDefault("ok") is true);
        Assert.Empty(f.Urls);
    }

    [Fact]
    public async Task Elliptical_followup_tool_can_recover_when_no_input_reservation_was_made()
    {
        await using var f = new Fixture(native: true);
        var old = await f.Retain();
        f.Input("Could give me more details on that conversation.");
        var receipt = await f.Propose("connector_execute", old);
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
    }

    [Theory]
    [InlineData("Could give me more details on that conversation.")]
    [InlineData("Can show me the full conversation?")]
    [InlineData("Would tell me more about that email?")]
    public async Task Elliptical_courtesy_keeps_completed_search_open_and_later_details_in_one_session(string request)
    {
        await using var f = new Fixture(native: true);
        var old = await f.Retain();
        f.Input("Open that conversation in a new tab.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.Propose("open_url", old);
        f.Commands.CompleteJarvisUserTurn();
        f.Input(request);
        var accepted = f.Commands.ReserveFinalConnectorRead(request, default);
        Assert.NotNull(accepted); accepted();
        var result = await f.Propose("connector_execute", old);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(result));
        f.Commands.CompleteJarvisUserTurn();
        f.Input("Open it again.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.Propose("open_url", old);
        Assert.Equal(2, f.Urls.Count);
        Assert.All(f.Urls, url => Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", url));
        Assert.Equal(1, f.Searches);
        Assert.Empty(f.ReasoningRequests);
    }

    [Fact]
    public async Task Combined_read_synthesis_receives_only_its_read_subgoal_while_controller_keeps_the_open_authority()
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        const string request = "Could you please open that thread in a new tab and give me more details about the entire conversation?";
        f.Input(request);
        f.Commands.ReserveFinalConnectorRead(request, default)();
        var receipt = await f.Propose("browser_automation", old);
        Assert.NotEmpty(f.ReasoningRequests);
        Assert.Contains("give me more details about the entire conversation", f.ReasoningRequests[^1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("open that thread", f.ReasoningRequests[^1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new tab", f.ReasoningRequests[^1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(request, f.Commands.ConnectorUserRequest);
        Assert.Single(f.Urls);
        Assert.Equal(1, receipt["opened_count"]);
    }

    [Theory]
    [InlineData("Give me more details on that and open the thread in a new tab.", "connector_execute", true)]
    [InlineData("Tell me about this and open it in a new tab.", "browser_automation", true)]
    [InlineData("Open the conversation in a new tab and explain that.", "open_url", true)]
    [InlineData("Open it on a new tab and give me more details.", "connector_execute", false)]
    [InlineData("Give me the details about that email and open it in a new tab.", "browser_action", false)]
    [InlineData("Open that thread in a new tab and tell me about it.", "open_url", false)]
    [InlineData("Could you please provide more details about the entire conversation and open the conversation thread in a new tab.", "connector_execute", false)]
    [InlineData("Could please provide more details about the entire conversation and open the conversation thread in a new tab.", "browser_automation", false)]
    [InlineData("Could you please provide more details about the entire conversation and open the conversation thread in a new tab.", "open_gmail_draft", false)]
    [InlineData("Open it on a new tab and give me more details.", "connector_execute", true)]
    [InlineData("Give me the details about that email and open it in a new tab.", "browser_action", true)]
    [InlineData("Open that thread in a new tab and tell me about it.", "open_url", true)]
    [InlineData("Could you please provide more details about the entire conversation and open the conversation thread in a new tab.", "connector_execute", true)]
    [InlineData("Could please provide more details about the entire conversation and open the conversation thread in a new tab.", "browser_automation", true)]
    [InlineData("Could you please provide more details about the entire conversation and open the conversation thread in a new tab.", "open_gmail_draft", true)]
    public async Task One_owned_followup_reads_and_opens_with_native_or_legacy_receipts(string request, string proposal, bool native)
    {
        await using var f = new Fixture(native);
        var old = await f.Retain();
        f.Input(request);
        var accepted = f.Commands.ReserveFinalConnectorRead(request, default);
        Assert.NotNull(accepted); accepted();
        var receipt = await f.Propose(proposal, old);
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        Assert.Equal("read_and_open_retained_conversation", receipt["actual_operation"]);
        Assert.True(receipt.GetValueOrDefault("browser_action_performed") is true);
        Assert.True(receipt.GetValueOrDefault("open_succeeded") is true);
        Assert.Contains("Browser dispatch succeeded", receipt["message"] as string);
        Assert.Contains("not verified", receipt["message"] as string);
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(receipt));
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", Assert.Single(f.Urls));
        Assert.Equal(1, f.Searches);
        Assert.NotEqual(old, f.LastReadTask);
        Assert.Equal("aaaaaaaa", f.LastReadThread);
        var before = f.Requests;
        var repeated = await f.Propose("browser_action", old);
        Assert.Equal("read_and_open_retained_conversation", repeated["actual_operation"]);
        Assert.Single(f.Urls); Assert.Equal(before, f.Requests);
        if (native) Assert.Empty(f.ReasoningRequests);
        f.Commands.CompleteJarvisUserTurn();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_success_and_browser_failure_are_separate_and_no_second_proposal_retries_the_open(bool native)
    {
        await using var f = new Fixture(native) { LaunchSucceeds = false };
        var old = await f.Retain();
        f.Input("Open it in a new tab and give me more details.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        var receipt = await f.Propose("browser_action", old);
        Assert.True(receipt.GetValueOrDefault("read_succeeded") is true);
        Assert.Equal(0, receipt["opened_count"]);
        Assert.False(receipt.GetValueOrDefault("browser_action_performed") is true);
        Assert.False(receipt.GetValueOrDefault("open_succeeded") is true);
        Assert.Contains("Browser dispatch was not confirmed", receipt["message"] as string);
        Assert.False(((Dictionary<string, object>)receipt["opening"]).GetValueOrDefault("ok") is true);
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(receipt));
        await f.Propose("open_url", old);
        Assert.Single(f.Urls);
    }

    [Fact]
    public async Task A_new_detail_offer_after_exact_open_binds_to_the_opened_retained_context()
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.Input("Open that email in a new tab.");
        var open = f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default);
        Assert.NotNull(open); open();
        await f.Propose("open_url", old);
        f.Controller.ObserveCompletedReply("I've opened it. Would you like me to read the full messages?", f.Commands.ConnectorUserRequestIdentity);
        f.Commands.CompleteJarvisUserTurn();
        f.Input("Yeah sure.");
        var details = f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default);
        Assert.NotNull(details); details();
        var receipt = await f.Propose("connector_execute", old);
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(receipt));
        Assert.Equal(1, f.Searches); Assert.Single(f.Urls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_between_read_and_open_cannot_dispatch_the_tab(bool native)
    {
        await using var f = new Fixture(native);
        var old = await f.Retain();
        f.BlockRead = true;
        f.Input("Open it on a new tab and give me more details.");
        var accepted = f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default);
        Assert.NotNull(accepted); accepted();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Input("Cancel the search.");
        var cancelled = await f.Commands.ExecuteJarvisToolAsync("connector_control",
            JsonSerializer.SerializeToElement(new { action = "cancel", task_id = old }), default);
        Assert.Equal("cancelled", cancelled["state"]);
        Assert.Empty(f.Urls);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("What's taking so long?")]
    [InlineData("How much longer?")]
    public async Task Pending_status_question_gives_both_model_proposals_one_progress_receipt_then_delivers_the_original_result(string status)
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.BlockRead = true;
        f.Input("Open it on a new tab and give me more details.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // A new spoken turn cancels the old provider proposals, not the owned
        // native read. Its status request cannot start an unrelated BA task.
        f.Input(status);
        var join = f.Commands.ReserveFinalConnectorRead(status, default);
        Assert.NotNull(join); join();
        Assert.True(f.Controller.HoldsPendingReply(f.Commands.ConnectorUserRequestSnapshot));
        var first = await f.Propose("connector_execute", old).WaitAsync(TimeSpan.FromSeconds(3));
        var second = await f.Propose("browser_automation", old).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(first, second);
        Assert.Equal("status_retained_conversation", first["actual_operation"]);
        Assert.Equal("executing", first["state"]);
        Assert.Equal("connector_status_receipt", first["reply_owner"]);
        Assert.False(f.Controller.HoldsPendingReply(f.Commands.ConnectorUserRequestSnapshot));
        Assert.Equal(1, f.Searches); Assert.Equal(1, f.Reads); Assert.Empty(f.Urls);

        // A truthful short progress answer ends its own routing turn. The
        // original accepted read/open must still finish under its own scope.
        f.Commands.CompleteJarvisUserTurn();
        f.ReleaseRead.TrySetResult(true);
        var receipt = await f.CollectOriginal();
        Assert.Equal("read_and_open_retained_conversation", receipt["actual_operation"]);
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(receipt));
        Assert.Single(f.Urls); Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Status_question_collects_the_already_settled_original_result_for_both_model_proposals()
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.BlockRead = true;
        f.Input("Open it on a new tab and give me more details.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        const string status = "What's taking so long?";
        f.Input(status);
        f.Commands.ReserveFinalConnectorRead(status, default)();
        f.ReleaseRead.TrySetResult(true);
        await f.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var first = await f.Propose("connector_execute", old);
        var second = await f.Propose("browser_automation", old);
        Assert.Same(first, second);
        Assert.Equal("read_and_open_retained_conversation", first["actual_operation"]);
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(first));
        Assert.Single(f.Urls); Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_tool_proposal_before_voice_end_joins_the_existing_operation_without_waiting_for_final_admission(bool lateVoiceEnd)
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.BlockRead = true;
        f.Input("Open it on a new tab and give me more details.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        const string status = "What's taking so long?";
        f.Controller.NotifyPendingReadInput(status);
        f.Input(status);
        // Real Live may send this tool batch before its ACTIVITY_END frame.
        // Deliberately omit ReserveFinalConnectorRead for the first proposal.
        var first = await f.Propose("connector_execute", old).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("status_retained_conversation", first["actual_operation"]);
        if (lateVoiceEnd)
        {
            var finalized = f.Commands.ReserveFinalConnectorRead(status, default);
            Assert.NotNull(finalized); finalized();
        }
        var second = await f.Propose("browser_automation", old).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(first, second);
        Assert.False(f.Controller.HoldsPendingReply(f.Commands.ConnectorUserRequestSnapshot));
        Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches); Assert.Empty(f.Urls);
        f.Commands.CompleteJarvisUserTurn();
        f.ReleaseRead.TrySetResult(true);
        var result = await f.CollectOriginal();
        Assert.Equal("read_and_open_retained_conversation", result["actual_operation"]);
        Assert.Single(f.Urls); Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches);
    }

    [Theory]
    [InlineData("Hello.")]
    [InlineData("Open Gmail.")]
    [InlineData("Send a reply.")]
    public async Task An_unrelated_finalized_turn_prevents_a_later_status_question_from_borrowing_old_action_authority(string intervening)
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.BlockRead = true;
        f.Input("Open it on a new tab and give me more details.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Input(intervening);
        Assert.Null(f.Commands.ReserveFinalConnectorRead(intervening, default));
        f.Input("What's taking so long?");
        Assert.Null(f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default));
        Assert.Equal(1, f.Reads); Assert.Empty(f.Urls);
        f.Input("Cancel the search.");
        await f.Commands.ExecuteJarvisToolAsync("connector_control", JsonSerializer.SerializeToElement(new { action = "cancel", task_id = old }), default);
    }

    [Fact]
    public async Task Cancelled_read_cannot_be_joined_by_a_status_question()
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.BlockRead = true;
        f.Input("Open it on a new tab and give me more details.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Input("Cancel the search.");
        await f.Commands.ExecuteJarvisToolAsync("connector_control", JsonSerializer.SerializeToElement(new { action = "cancel", task_id = old }), default);
        f.Input("What's taking so long?");
        Assert.Null(f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default));
        Assert.Empty(f.Urls); Assert.Equal(1, f.Reads);
    }

    [Fact]
    public async Task A_status_question_does_not_grant_body_access_to_a_headers_only_result()
    {
        await using var f = new Fixture();
        var old = await f.Retain(headersOnly: true);
        f.Input("Open it on a new tab and give me more details.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Input("What's taking so long?");
        var status = f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default);
        Assert.NotNull(status); status();
        var receipt = await f.Propose("browser_automation", old);
        Assert.False(receipt.GetValueOrDefault("ok") is true);
        Assert.Contains("headers only", JsonSerializer.Serialize(receipt), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, f.Reads); Assert.Empty(f.Urls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Cumulative_status_suffix_joins_the_exact_owned_request_without_changing_its_identity(
        bool finalizeBeforeProposal, bool lateVoiceEnd)
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.BlockRead = true;
        const string original = "Open it in a new tab and give me the full details about that email.";
        f.Input(original);
        f.Commands.ReserveFinalConnectorRead(original, default)();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var originalIdentity = f.Commands.ConnectorUserRequestIdentity;

        // Real Live appended the status audio to its earlier ASR buffer while
        // preserving the human-turn identity. Do not turn that frame shape
        // into a new action request or alter the original read/open selection.
        const string cumulative = original + " What's taking so long?";
        f.Controller.NotifyPendingReadInput(cumulative);
        if (finalizeBeforeProposal)
        {
            var reserved = f.Commands.ReserveFinalConnectorRead(cumulative, default);
            Assert.NotNull(reserved);
            f.Commands.ObserveJarvisTranscript(cumulative, default);
            Assert.True(f.Controller.HoldsPendingReply(f.Commands.ConnectorUserRequestSnapshot));
            reserved();
        }
        else f.Commands.ObserveJarvisTranscript(cumulative, default);
        Assert.Equal(originalIdentity, f.Commands.ConnectorUserRequestIdentity);

        var first = await f.Propose("connector_execute", old).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("status_retained_conversation", first["actual_operation"]);
        if (lateVoiceEnd)
        {
            var finalized = f.Commands.ReserveFinalConnectorRead(cumulative, default);
            Assert.NotNull(finalized); finalized();
        }
        var second = await f.Propose("browser_automation", old).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(first, second);
        Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches); Assert.Empty(f.Urls);
        Assert.False(f.Controller.HoldsPendingReply(f.Commands.ConnectorUserRequestSnapshot));

        f.Commands.CompleteJarvisUserTurn();
        f.ReleaseRead.TrySetResult(true);
        var result = await f.CollectOriginal();
        Assert.Equal("read_and_open_retained_conversation", result["actual_operation"]);
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(result));
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", Assert.Single(f.Urls));
        Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(" What's taking so long? Read another email.")]
    [InlineData(" What's taking so long and send a reply?")]
    [InlineData(" Headers only. What's taking so long?")]
    [InlineData(" What's taking so long? Do not read the bodies.")]
    [InlineData(" What's taking so long? Open Gmail.")]
    public async Task Cumulative_status_suffix_does_not_accept_new_targets_writes_or_permission_changes(string suffix)
    {
        await using var f = new Fixture();
        await f.Retain();
        f.BlockRead = true;
        const string original = "Open it in a new tab and give me the full details about that email.";
        f.Input(original);
        f.Commands.ReserveFinalConnectorRead(original, default)();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var changed = original + suffix;
        f.Controller.NotifyPendingReadInput(changed);
        f.Commands.ObserveJarvisTranscript(changed, default);
        Assert.Null(f.Commands.ReserveFinalConnectorRead(changed, default));
        var proposal = await f.Controller.TryCollectAdmittedReadProposalAsync("browser_automation",
            JsonSerializer.SerializeToElement(new { task = "Open a different email." }),
            f.Commands.ConnectorUserRequestSnapshot, default);
        Assert.Null(proposal);
        Assert.Equal(1, f.Reads); Assert.Equal(1, f.Searches); Assert.Empty(f.Urls);
    }

    [Theory]
    [InlineData("Open it again.")]
    [InlineData("Open it again in a new tab.")]
    [InlineData("Open that conversation in a new tab again.")]
    public async Task Current_session_reopen_after_completed_open_uses_the_same_verified_object(string request)
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.Input("Open that conversation in a new tab.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        await f.Propose("open_url", old);
        var expected = Assert.Single(f.Urls);
        f.Commands.CompleteJarvisUserTurn();
        var before = f.Requests;
        f.Input(request);
        var accepted = f.Commands.ReserveFinalConnectorRead(request, default);
        Assert.NotNull(accepted); accepted();
        var result = await f.Propose("browser_action", old);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal(new[] { expected, expected }, f.Urls);
        Assert.Equal(before, f.Requests);
        await f.Propose("open_url", old);
        Assert.Equal(2, f.Urls.Count);
    }

    [Theory]
    [InlineData("Could you open it in a new tab and tell me more about what we discussed?")]
    [InlineData("Could you please give me a recap of the conversation and open it in a new tab?")]
    public async Task Current_session_combined_contextual_question_survives_failed_synthesis(string request)
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.Input(request);
        var accepted = f.Commands.ReserveFinalConnectorRead(request, default);
        Assert.NotNull(accepted); accepted();
        var result = await f.Propose("connector_execute", old);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(result));
        Assert.Equal("read_and_open_retained_conversation", result["actual_operation"]);
        Assert.EndsWith("#all/aaaaaaaa", Assert.Single(f.Urls));
        Assert.Equal(1, f.Searches);
        f.Commands.CompleteJarvisUserTurn();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Later_recap_and_open_uses_retained_context_after_previous_read_completes()
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.Input("Give me the chronology of the full conversation and open it in a new tab.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        var earlier = await f.Propose("connector_execute", old);
        Assert.True(earlier.GetValueOrDefault("ok") is true);
        f.Commands.CompleteJarvisUserTurn();

        f.Input("Could you please recap all the details in that email conversation and open it again?");
        var accepted = f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default);
        Assert.NotNull(accepted);
        accepted();
        var result = await f.Propose("open_url", old);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal("read_and_open_retained_conversation", result["actual_operation"]);
        Assert.Contains("Please send the project outline", JsonSerializer.Serialize(result));
        await f.Propose("connector_execute", old);
        Assert.Equal(2, f.Urls.Count);
        Assert.Single(f.Urls.Distinct());
        Assert.Equal(1, f.Searches);
        f.Commands.CompleteJarvisUserTurn();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Give me the full details of that email.")]
    [InlineData("Could you please give me a recap of the conversation and open it in a new tab?")]
    public async Task Post_resolution_scope_failure_releases_only_its_new_task_ui_and_preserves_retained_context(string request)
    {
        await using var f = new Fixture();
        var old = await f.Retain();
        f.FailNextFreshScopeCheck = true;
        f.Input(request);
        f.Commands.ReserveFinalConnectorRead(request, default)();
        var failed = await f.Propose("connector_execute", old);
        Assert.False(failed.GetValueOrDefault("ok") is true);
        Assert.Equal("connector_followup_failed", failed["error_code"]);
        if (request.Contains("open it"))
        {
            Assert.Equal(0, failed["opened_count"]);
            Assert.False((bool)failed["open_succeeded"]);
            Assert.False((bool)failed["browser_action_performed"]);
        }
        Assert.Empty(f.Urls);
        Assert.Equal(0, f.Reads);
        Assert.False(f.Controller.HoldsPendingReply(f.Commands.ConnectorUserRequestSnapshot));
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        f.Commands.CompleteJarvisUserTurn();
        f.Input("Open that email in a new tab.");
        f.Commands.ReserveFinalConnectorRead(f.Commands.ConnectorUserRequest, default)();
        var opened = await f.Propose("open_url", old);
        Assert.True(opened.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(opened));
        Assert.EndsWith("#all/aaaaaaaa", Assert.Single(f.Urls));
        Assert.Equal(1, f.Searches);
    }


    [Theory]
    [InlineData("Tell me more about that email conversation.", "get_selected_text")]
    [InlineData("What else do we know about that conversation?", "get_selected_text")]
    [InlineData("Who sent that message, and when was it sent?", "get_selected_text")]
    [InlineData("Tell me more about that email conversation.", "get_desktop_context")]
    public async Task Context_tool_after_completed_open_uses_retained_information_without_recapturing_selection(string request, string tool)
    {
        await using var f = new Fixture(native: true);
        var old = await f.Retain();
        const string openRequest = "Open that conversation in a new tab.";
        f.Input(openRequest);
        f.Commands.ReserveFinalConnectorRead(openRequest, default)();
        Assert.True((await f.Propose("open_url", old)).GetValueOrDefault("ok") is true);
        f.Commands.CompleteJarvisUserTurn();
        var before = f.Requests;
        f.Input(request);
        var receipt = await f.Commands.ExecuteJarvisToolAsync(tool,
            JsonSerializer.SerializeToElement(new { use_recent = true }), default);
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        Assert.Equal("retained_context", receipt["state"]);
        Assert.Contains("Alice", JsonSerializer.Serialize(receipt));
        Assert.Equal(0, f.SelectionCaptures);
        Assert.Equal(before, f.Requests);
        Assert.Empty(f.ReasoningRequests);
        Assert.Single(f.Urls);
    }

    [Theory]
    [InlineData("Translate this selection to French.", true)]
    [InlineData("Tell me more about the selected text.", true)]
    [InlineData("Tell me more about that email conversation.", false)]
    public async Task Actual_selection_requests_and_absent_connector_context_keep_the_selection_path(string request, bool retain)
    {
        await using var f = new Fixture(native: true);
        if (retain) await f.Retain();
        f.Input(request);
        var receipt = await f.Commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), default);
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        Assert.Contains("Fixture selected passage", JsonSerializer.Serialize(receipt));
        Assert.Equal(1, f.SelectionCaptures);
        Assert.Empty(f.ReasoningRequests);
        Assert.Empty(f.Urls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _session = new();
        private readonly string _account = Guid.NewGuid().ToString("N");
        internal readonly List<string> Urls = [];
        internal readonly List<string> ReasoningRequests = [];
        internal readonly List<string> NativeQueries = [];
        internal readonly TaskCompletionSource<bool> ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReleaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<JarvisLifecycleAnnouncement> Notice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CommandRegistry Commands;
        internal readonly JarvisConnectorController Controller;
        internal int Requests, Searches, Reads, SelectionCaptures;
        internal string LastReadTask, LastReadThread;
        internal bool BlockRead, MultipleThreads;
        internal bool RecoverPersonLookup;
        internal bool FailNextFreshScopeCheck;
        private bool _failNextSnapshot;
        internal bool LaunchSucceeds = true;
        private readonly bool _native;

        internal Fixture(bool native = false)
        {
            _native = native;
            var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
            settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
            var automation = new JarvisAutomationService(null, settings, new(Path.Combine(_directory, "history.json")),
                new LearnedMappingService(Path.Combine(_directory, "mappings.json")), () => { }, null, null, null,
                simpleUrlLauncher: (url, _) => { Urls.Add(url); return (LaunchSucceeds, LaunchSucceeds ? "Opened." : "Launch unconfirmed.", "fixture"); });
            Commands = new(null, null, null, settings, new NotificationService(null), automation: automation,
                captureSelectedText: _ =>
                {
                    SelectionCaptures++;
                    return Task.FromResult(new Dictionary<string, object> { ["ok"] = true, ["text"] = "Fixture selected passage." });
                });
            Controller = new(Request, () => "fixture-owner", () => _session.Token, () => Commands.ConnectorUserRequest,
                (notice, _) => { Notice.TrySetResult(notice); return Task.FromResult(true); }, userRequestIdentity: () => Commands.ConnectorUserRequestIdentity,
                reasonRead: (_, _, _, prompt, _, _, _) =>
                {
                    using var parsed = JsonDocument.Parse(prompt);
                    ReasoningRequests.Add(parsed.RootElement.GetProperty("request").GetString());
                    throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 };
                },
                userRequestSnapshot: () =>
                {
                    if (_failNextSnapshot)
                    {
                        _failNextSnapshot = false;
                        throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
                    }
                    return Commands.ConnectorUserRequestSnapshot;
                }, preferDirectReadReceipt: native);
            automation.Connectors = Controller;
        }

        internal void Input(string text) => Commands.ObserveSubmittedJarvisText(text, _session.Token);
        internal Task<Dictionary<string, object>> Propose(string tool, string old) => Commands.ExecuteJarvisToolAsync(tool,
            tool == "connector_execute" ? JsonSerializer.SerializeToElement(new { task_id = old, capability_id = "mail_search", arguments_json = "{\"query\":\"stale unrelated search\"}" })
            : tool == "browser_action" ? JsonSerializer.SerializeToElement(new { action = "new tab" })
            : tool == "browser_automation" ? JsonSerializer.SerializeToElement(new { task = "Open the email in a new tab and read its details." })
            : JsonSerializer.SerializeToElement(new { url = "https://mail.google.com/mail/u/0/#thread/invented" }), default);

        internal async Task<Dictionary<string, object>> CollectOriginal()
        {
            var notice = await Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var match = System.Text.RegularExpressions.Regex.Match(notice.Detail, @"task_id=([a-f0-9]+); operation_id=([a-f0-9]+)");
            Assert.True(match.Success, notice.Detail);
            return await Commands.ExecuteJarvisToolAsync("connector_control", JsonSerializer.SerializeToElement(new
            { action = "result", task_id = match.Groups[1].Value, operation_id = match.Groups[2].Value }), default);
        }

        internal async Task<string> Retain(bool headersOnly = false)
        {
            Input(headersOnly ? "Find my emails with Alice Example, headers only." : "Find my emails with Alice Example and give me the conversation details.");
            var resolved = await Commands.ExecuteJarvisToolAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), default);
            var old = (string)resolved["task_id"];
            var found = await Commands.ExecuteJarvisToolAsync("connector_execute", JsonSerializer.SerializeToElement(new { task_id = old,
                capability_id = "mail_search", arguments_json = "{\"query\":\"Alice Example\"}" }), default);
            Assert.True(found.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(found));
            if (_native) Assert.Empty(ReasoningRequests);
            else if (!headersOnly) Assert.Contains("ai_provider_unavailable", JsonSerializer.Serialize(found));
            Commands.CompleteJarvisUserTurn();
            // Status-join assertions count only the new owned follow-up;
            // initial conversation retrieval can itself include a thread read.
            Reads = 0;
            return old;
        }

        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            Requests++;
            if (path.EndsWith("/resolve") && FailNextFreshScopeCheck)
            {
                FailNextFreshScopeCheck = false;
                _failNextSnapshot = true;
            }
            if (path.EndsWith("/resolve")) return JsonSerializer.SerializeToElement(new { taskId = Guid.NewGuid().ToString("N"),
                connectorId = "gmail", accountId = _account, state = "ready", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                choices = new[] { new { accountId = _account, label = "reader@example.test" } },
                capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching…", arguments = new Dictionary<string,string> { ["query"] = "query" } },
                    new { id = "mail_thread", writes = false, activity = "Reading…", arguments = new Dictionary<string,string> { ["id"] = "id" } } } });
            if (!path.EndsWith("/execute")) return JsonSerializer.SerializeToElement(new { state = "completed", verified = true });
            var args = JsonSerializer.SerializeToElement(body);
            if (args.GetProperty("capabilityId").GetString() == "mail_search")
            {
                Searches++;
                var query = args.GetProperty("arguments").GetProperty("query").GetString();
                NativeQueries.Add(query);
                if (RecoverPersonLookup && Searches == 1)
                    return JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
                        data = new { queryUsed = query, messages = Array.Empty<object>(), sampledMessages = Array.Empty<object>(),
                            hasMore = false, pageItemCount = 0, metadataSample = new { requested = 0, returned = 0, complete = true } } } });
                if (RecoverPersonLookup) Assert.Contains("from:\"marcel\"", query);
                if (_native) Assert.Equal("exact", args.GetProperty("arguments").GetProperty("matchMode").GetString());
                var messages = (MultipleThreads ? new[] { "aaaaaaaa", "bbbbbbbb" } : new[] { "aaaaaaaa" })
                    .Select((thread, index) => new { id = index == 0 ? "11111111" : "22222222", threadId = thread }).ToArray();
                return JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false, data = new {
                    messages,
                    sampledMessages = messages.Select(message => new { message.id, message.threadId,
                        from = RecoverPersonLookup ? "Marcel Example <marcel@example.test>" : "Alice Example <alice@example.test>", subject = "Project outline" }).ToArray(),
                    hasMore = false, pageItemCount = messages.Length } } });
            }
            LastReadTask = args.GetProperty("taskId").GetString(); LastReadThread = args.GetProperty("arguments").GetProperty("id").GetString();
            Reads++;
            if (BlockRead) { ReadStarted.TrySetResult(true); await ReleaseRead.Task.WaitAsync(token); }
            return JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false, data = new {
                id = LastReadThread, totalMessages = 1, messages = new[] { new { id = "11111111", threadId = LastReadThread,
                    from = "Alice Example <alice@example.test>", subject = "Project outline", text = "Please send the project outline tomorrow.", contentTruncated = false } } } } });
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.ResetAsync(); _session.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
