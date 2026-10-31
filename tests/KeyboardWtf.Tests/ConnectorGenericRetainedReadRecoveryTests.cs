namespace KeyboardWtf.Tests;

using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorGenericRetainedReadRecoveryTests
{
    [Theory]
    [InlineData("Provide more details about the entire conversation history.")]
    [InlineData("Get the full conversation history.")]
    [InlineData("Pull up the entire conversation history.")]
    public async Task A_third_details_turn_after_exact_open_uses_retained_content_without_terminal_authority(string question)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("Give me the full details of that email.");
        var details = await f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.True(details.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(details));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        var completed = f.LastExecute.GetProperty("taskId").GetString();
        f.Input("Open that email in a new tab.");
        var urls = new List<string>();
        var accepted = f.Controller.ReserveRetainedRead(f.User, default, (url, _) =>
        {
            urls.Add(url);
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = true });
        });
        Assert.NotNull(accepted);
        accepted();
        var opened = await f.Controller.TryCollectAdmittedReadProposalAsync("open_url", default,
            new(f.User, f.Identity), default);
        Assert.True(opened.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(opened));
        Assert.EndsWith("#all/aaaaaaaa", Assert.Single(urls));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        var requests = f.Requests;
        f.Input(question);
        var result = await f.Execute(completed, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("no longer active", JsonSerializer.Serialize(result));
        Assert.Equal(requests, f.Requests); // Already-read evidence needs no fresh Gmail read.
    }

    [Theory]
    [InlineData("gmail", "When did Daisy reply, and what is the latest status of that email?", "mail_thread", "{\"id\":\"aaaaaaaa\"}")]
    [InlineData("github", "How many stars does that repository have?", "github_repo", "{\"repository\":\"owner/project\"}")]
    [InlineData("googledocs", "What is the latest status of that document?", "docs_read", "{\"id\":\"observed-document\"}")]
    public async Task Repeating_the_same_contextual_question_on_a_new_human_turn_does_not_reuse_terminal_authority(
        string provider, string question, string capability, string arguments)
    {
        await using var f = new Fixture();
        var initial = await f.Retain(provider);
        f.Input(question);
        var first = await f.Execute(initial, capability, arguments);
        Assert.True(first.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(first));
        var completed = f.LastExecute.GetProperty("taskId").GetString();
        f.Controller.CompleteSettledUserTurn(f.Identity);
        var before = f.Executes;
        f.Input(question); // Same words, distinct real human turn.
        var repeated = await f.Execute(completed, capability, arguments);
        Assert.True(repeated.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(repeated));
        Assert.True(repeated.GetValueOrDefault("recovered_retained_result") is true);
        Assert.NotEqual(completed, f.LastExecute.GetProperty("taskId").GetString());
        Assert.Equal(before + 1, f.Executes);
        var settledRequests = f.Requests;
        Assert.Same(repeated, await f.Execute(completed, capability, arguments));
        Assert.Equal(settledRequests, f.Requests); // Same-turn proposals still deduplicate.
    }

    [Theory]
    [InlineData("When did Daisy reply, and what is the latest status of that email?")]
    [InlineData("Who sent that message, and when was it sent?")]
    [InlineData("Can you tell me the timeline of Daisy's conversation?")]
    public async Task Natural_contextual_questions_read_a_verified_object_under_fresh_authority(string question)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input(question);
        var result = await f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True(result.GetValueOrDefault("recovered_retained_result") is true);
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(result));
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
        Assert.NotEqual(old, f.LastExecute.GetProperty("taskId").GetString());
        Assert.Equal(f.Account, f.LastResolve.GetProperty("accountId").GetString());
        Assert.Equal(question, f.LastResolve.GetProperty("userRequest").GetString());
        Assert.Equal("aaaaaaaa", f.LastExecute.GetProperty("arguments").GetProperty("id").GetString());
        var before = f.Requests;
        var repeated = await f.Execute(old, "mail_read", "{\"id\":\"11111111\"}");
        Assert.Same(result, repeated); Assert.Equal(before, f.Requests);
    }

    [Fact]
    public async Task A_search_tool_guess_for_a_substantive_retained_question_reads_the_actual_conversation_instead_of_searching_again()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("When did Daisy reply, and what is the latest status of that email?");
        var result = await f.Execute(old, "mail_search", "{\"query\":\"from:daisy.edwards@identity.digital\"}");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(result));
        Assert.Equal("mail_thread", f.LastExecute.GetProperty("capabilityId").GetString());
        Assert.Equal("aaaaaaaa", f.LastExecute.GetProperty("arguments").GetProperty("id").GetString());
        Assert.DoesNotContain("query", f.LastExecute.GetProperty("arguments").GetRawText());
        Assert.NotEqual(old, f.LastExecute.GetProperty("taskId").GetString());
        Assert.Equal(f.Account, f.LastResolve.GetProperty("accountId").GetString());
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
        Assert.Same(result, await f.Execute(old, "mail_search", "{\"query\":\"OLD QUERY\"}"));
        Assert.Equal(2, f.Executes);
    }

    [Theory]
    [InlineData("What is the latest status of that email today?")]
    [InlineData("What is the latest status of that email? Search using: from:someone@example.test")]
    [InlineData("What is the latest status of that email from Dr Gao?")]
    [InlineData("When did Gao reply, and what is the latest status of that email?")]
    [InlineData("What other emails did Daisy send?")]
    public async Task A_new_literal_date_participant_or_search_scope_is_not_replaced_by_the_previous_thread(string question)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input(question);
        var result = await f.Execute(old, "mail_search", "{\"query\":\"OLD QUERY\"}");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.False((bool)result["this_proposal_dispatched"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.DoesNotContain("OLD QUERY", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Retained_reference_survives_pruned_execution_owner_without_reusing_its_authority()
    {
        await using var f = new Fixture();
        var mail = await f.Retain("gmail");
        await f.Retain("googledocs"); // Resolve removes the terminal mail owner.
        f.Input("When did Daisy reply to that email?");
        var result = await f.Execute(mail, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(result));
        Assert.Equal(3, f.Resolves); Assert.Equal(3, f.Executes);
        Assert.Equal("gmail", f.LastResolve.GetProperty("connectorId").GetString());
    }

    [Theory]
    [InlineData("googledocs", "What is the latest status of that document?", "docs_read", "{\"id\":\"observed-document\"}")]
    [InlineData("github", "How many stars does that repository have?", "github_repo", "{\"repository\":\"owner/project\"}")]
    [InlineData("github", "What is its latest release?", "github_releases", "{\"repository\":\"owner/project\",\"mode\":\"latest\"}")]
    public async Task The_same_recovery_boundary_supports_other_normalized_read_capabilities(string provider, string question,
        string capability, string arguments)
    {
        await using var f = new Fixture();
        var old = await f.Retain(provider);
        f.Input(question);
        var result = await f.Execute(old, capability, arguments);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True(result.GetValueOrDefault("recovered_retained_result") is true);
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
        Assert.Equal(capability, f.LastExecute.GetProperty("capabilityId").GetString());
        Assert.Equal(f.Account, f.LastResolve.GetProperty("accountId").GetString());
    }

    [Theory]
    [InlineData("result")]
    [InlineData("status")]
    public async Task A_terminal_control_read_uses_retained_data_without_claiming_fresh_content_or_execution(string action)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        await f.Retain("googledocs");
        f.Input("When was that email sent?");
        var before = f.Requests;
        var result = await f.Control(old, action);
        Assert.Equal("retained_context", result["state"]);
        Assert.False((bool)result["action_authority"]);
        Assert.True((bool)result["recovered_retained_result"]);
        Assert.False((bool)result["this_proposal_dispatched"]);
        Assert.Equal(before, f.Requests);
        Assert.DoesNotContain("The project outline is due Friday", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("What is 2 + 2?")]
    [InlineData("How do I fix my laptop?")]
    [InlineData("How do I fix this laptop?")]
    [InlineData("Open Gmail.")]
    [InlineData("Open Gmail, Instagram, Discord and Devpost.")]
    [InlineData("Please end the conversation.")]
    [InlineData("Can you stop talking now?")]
    [InlineData("Please reply to that email.")]
    [InlineData("Read that email and send it to someone.")]
    [InlineData("Read that email in my other account.")]
    public async Task Stale_tool_proposals_do_not_create_read_authority_for_unrelated_or_mutating_human_requests(string request)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input(request);
        var before = f.Requests;
        var result = await f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Equal(before, f.Requests);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
    }

    [Theory]
    [InlineData("What is 2 + 2?")]
    [InlineData("How do I fix this laptop?")]
    public async Task Generic_recovery_rejects_unrelated_stale_proposals_before_connector_account_lookup(string request)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input(request);
        var before = f.OwnerLookups;
        var recover = typeof(JarvisConnectorController).GetMethod("TryRecoverRetainedReadProposalAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(recover);
        var receipt = Assert.IsAssignableFrom<Task<Dictionary<string, object>>>(recover.Invoke(f.Controller,
            new object[] { "connector_execute", JsonSerializer.SerializeToElement(new
                { task_id = old, capability_id = "mail_thread", arguments_json = "{\"id\":\"aaaaaaaa\"}" }), default(CancellationToken) }));
        Assert.Null(await receipt);
        Assert.Null(await f.Controller.TryReadRetainedContextAsync(default));
        Assert.Equal(before, f.OwnerLookups);
    }

    [Fact]
    public async Task Empty_retained_context_short_circuits_both_new_recovery_hooks_before_reading_the_human_snapshot()
    {
        await using var f = new Fixture();
        f.Input("What is 2 + 2?");
        var generic = typeof(JarvisConnectorController).GetMethod("TryRecoverRetainedReadProposalAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var context = typeof(JarvisConnectorController).GetMethod("TryRecallContextualReadAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Null(await Assert.IsAssignableFrom<Task<Dictionary<string, object>>>(generic.Invoke(f.Controller,
            new object[] { "connector_execute", JsonSerializer.SerializeToElement(new { task_id = Guid.NewGuid().ToString("N"), capability_id = "mail_search", arguments_json = "{}" }), default(CancellationToken) })));
        Assert.Null(await Assert.IsAssignableFrom<Task<Dictionary<string, object>>>(context.Invoke(f.Controller, new object[] { default(CancellationToken) })));
        Assert.Equal(0, f.SnapshotReads); Assert.Equal(0, f.OwnerLookups); Assert.Equal(0, f.Requests);
    }

    [Fact]
    public async Task Active_task_tool_proposals_short_circuit_recovery_before_reading_the_human_snapshot()
    {
        await using var f = new Fixture();
        await f.Retain("gmail");
        f.Input("Find my emails with another person.");
        var active = await f.Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail", account_id = f.Account }), false, default);
        var before = f.SnapshotReads;
        var generic = typeof(JarvisConnectorController).GetMethod("TryRecoverRetainedReadProposalAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Null(await Assert.IsAssignableFrom<Task<Dictionary<string, object>>>(generic.Invoke(f.Controller,
            new object[] { "connector_execute", JsonSerializer.SerializeToElement(new { task_id = (string)active["task_id"], capability_id = "mail_search", arguments_json = "{}" }), default(CancellationToken) })));
        Assert.Equal(before, f.SnapshotReads);
    }

    [Theory]
    [InlineData("unobserved-id")]
    [InlineData("bbbbbbbb")]
    public async Task Model_arguments_cannot_select_an_unobserved_object(string id)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("When did Daisy reply to that email?");
        var before = f.Requests;
        var result = await f.Execute(old, "mail_thread", JsonSerializer.Serialize(new { id }));
        Assert.Equal("clarification_required", result["state"]);
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData("gmail", "mail_thread", "{\"id\":\"aaaaaaaa\"}")]
    [InlineData("github", "github_repo", "{\"repository\":\"owner/project\"}")]
    public async Task Verified_native_reference_recovers_even_when_the_model_lost_the_execution_task_id(string provider, string capability, string arguments)
    {
        await using var f = new Fixture();
        await f.Retain(provider);
        f.Input(provider == "gmail" ? "When did Daisy reply to that email?" : "How many stars does that repository have?");
        var result = await f.Execute(Guid.NewGuid().ToString("N"), capability, arguments);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True((bool)result["recovered_retained_result"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
    }

    [Fact]
    public async Task Ambiguous_native_reference_cannot_guess_between_retained_accounts()
    {
        await using var f = new Fixture();
        await f.Retain("gmail");
        f.Account = Guid.NewGuid().ToString("N");
        await f.Retain("gmail");
        f.Input("When did Daisy reply to that email?");
        var before = f.Requests;
        var result = await f.Execute(Guid.NewGuid().ToString("N"), "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.Equal("clarification_required", result["state"]);
        Assert.Equal(before, f.Requests);
    }

    [Fact]
    public async Task Ambiguous_reference_clarification_respects_a_new_headers_only_disclosure_restriction()
    {
        await using var f = new Fixture();
        async Task RetainBody()
        {
            var source = await f.Retain("gmail");
            f.Input("When did Daisy reply to that email?");
            var read = await f.Execute(source, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
            f.Controller.CompleteSettledUserTurn(f.Identity);
            var recalled = await f.Control((string)read["task_id"], "recall");
            Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(recalled));
        }
        await RetainBody();
        f.Account = Guid.NewGuid().ToString("N");
        await RetainBody();
        f.Input("When did Daisy reply to that email? Headers only, don't read the bodies.");
        var before = f.Requests;
        var result = await f.Execute(Guid.NewGuid().ToString("N"), "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.Equal("clarification_required", result["state"]);
        var context = Assert.IsType<ConnectorSessionResult>(result["context"]);
        Assert.Empty(context.Facts); Assert.Empty(context.ContentLoadedCapabilities); Assert.Equal("", context.Answer);
        Assert.DoesNotContain("The project outline is due Friday", JsonSerializer.Serialize(result));
        Assert.Equal(before, f.Requests);
    }

    [Fact]
    public async Task A_context_tool_proposal_returns_retained_facts_for_a_natural_question_without_desktop_capture()
    {
        await using var f = new Fixture();
        await f.Retain("github");
        f.Input("How many stars does that repository have?");
        var before = f.Requests;
        var result = await f.Controller.TryReadRetainedContextAsync(default);
        Assert.NotNull(result);
        Assert.Equal("retained_context", result["state"]);
        Assert.False((bool)result["action_authority"]);
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Data_only_recall_can_progress_to_one_pinned_native_read_in_the_same_human_recovery(bool contextToolFirst)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("When did Daisy reply, and what is the latest status of that email?");
        var facts = contextToolFirst ? await f.Controller.TryReadRetainedContextAsync(default) : await f.Control(old, "result");
        Assert.Equal("retained_context", facts["state"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        var read = await f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.True(read.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(read));
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(read));
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
        Assert.NotSame(facts, read);
        Assert.Same(read, await f.Control(old, "status"));
        Assert.Same(read, await f.Execute(old, "mail_read", "{\"id\":\"11111111\"}"));
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
    }

    [Theory]
    [InlineData("googlecalendar", "What is the latest status of that calendar event?", "calendar_list", "{\"timeMin\":\"2026-10-15T00:00:00Z\",\"timeMax\":\"2026-10-16T00:00:00Z\"}", false)]
    [InlineData("googledrive", "What is the latest status of that file?", "drive_read", "{\"id\":\"observed-file\"}", true)]
    [InlineData("googleforms", "What is the latest status of that form?", "forms_read", "{\"id\":\"observed-form\"}", true)]
    [InlineData("youtube", "What is the latest status of that YouTube channel?", "youtube_channel", "{}", false)]
    [InlineData("slack", "What is the latest status of that Slack channel?", "slack_history", "{\"channel\":\"C123456789\",\"cursor\":\"OLD CURSOR\"}", false)]
    [InlineData("discord", "What is the latest status of that Discord account?", "discord_self", "{}", false)]
    public async Task All_normalized_provider_reads_can_advance_after_facts_without_replaying_unpinned_arguments(string provider,
        string question, string capability, string arguments, bool pinned)
    {
        await using var f = new Fixture();
        var old = await f.Retain(provider);
        f.Input(question);
        var facts = await f.Control(old, "result");
        Assert.Equal("retained_context", facts["state"]);
        var result = await f.Execute(old, capability, arguments);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True((bool)result["recovered_retained_result"]);
        Assert.NotSame(facts, result); Assert.Equal(2, f.Resolves);
        Assert.Equal(pinned ? 2 : 1, f.Executes);
        if (!pinned)
        {
            Assert.False((bool)result["this_proposal_dispatched"]);
            Assert.Equal("ready", ((JsonElement)result["connector"]).GetProperty("state").GetString());
            Assert.DoesNotContain("OLD CURSOR", JsonSerializer.Serialize(result));
        }
        Assert.Equal(f.Account, f.LastResolve.GetProperty("accountId").GetString());
        Assert.Same(result, await f.Control(old, "status"));
    }

    [Theory]
    [InlineData("googlecalendar", "What is the latest status of that calendar event?", "calendar_list", "{\"timeMin\":\"2026-10-15T00:00:00Z\",\"timeMax\":\"2026-10-16T00:00:00Z\"}", false)]
    [InlineData("googledrive", "What is the latest status of that file?", "drive_read", "{\"id\":\"observed-file\"}", true)]
    [InlineData("googleforms", "What is the latest status of that form?", "forms_read", "{\"id\":\"observed-form\"}", true)]
    [InlineData("youtube", "What is the latest status of that YouTube channel?", "youtube_channel", "{}", false)]
    [InlineData("slack", "What is the latest status of that Slack channel?", "slack_history", "{\"channel\":\"C123456789\",\"cursor\":\"OLD CURSOR\"}", false)]
    [InlineData("discord", "What is the latest status of that Discord account?", "discord_self", "{}", false)]
    public async Task One_current_retained_account_can_resolve_a_normalized_read_when_execution_id_is_missing(string provider,
        string question, string capability, string arguments, bool pinned)
    {
        await using var f = new Fixture();
        await f.Retain(provider);
        f.Input(question);
        var result = await f.Execute(Guid.NewGuid().ToString("N"), capability, arguments);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True((bool)result["recovered_retained_result"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(pinned ? 2 : 1, f.Executes);
        Assert.Equal(f.Account, f.LastResolve.GetProperty("accountId").GetString());
        Assert.DoesNotContain("OLD CURSOR", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Current_body_restriction_discloses_headers_without_a_new_body_read()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("When did Daisy reply to that email? Headers only, don't read the bodies.");
        var before = f.Requests;
        var result = await f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        Assert.Equal("retained_context", result["state"]);
        Assert.DoesNotContain("The project outline is due Friday", JsonSerializer.Serialize(result));
        Assert.Equal(before, f.Requests);
    }

    [Fact]
    public async Task Explicit_body_regrant_collects_existing_verified_content_without_repeating_native_work()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("When did Daisy reply to that email?");
        var read = await f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        var bodyTask = (string)read["task_id"];
        f.Controller.CompleteSettledUserTurn(f.Identity);
        var before = f.Requests;
        f.Input("Read that email, but headers only; do not read the bodies.");
        var headers = await f.Control(bodyTask, "result");
        Assert.DoesNotContain("The project outline is due Friday", JsonSerializer.Serialize(headers));
        Assert.DoesNotContain("nativeMessageContent", JsonSerializer.Serialize(headers));
        f.Input("Actually read the message bodies.");
        var regrant = await f.Control(bodyTask, "result");
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(regrant));
        Assert.True(regrant.ContainsKey("nativeMessageContent"));
        Assert.False((bool)regrant["action_authority"]);
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("session")]
    [InlineData("reset")]
    public async Task Retained_read_cannot_cross_account_session_or_reset(string scenario)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        if (scenario == "owner") f.Owner = "other-owner";
        if (scenario == "session") f.LogicalSession = Guid.NewGuid().ToString("N");
        if (scenario == "reset") await f.Controller.ResetAsync();
        f.Input("When did Daisy reply to that email?");
        var before = f.Requests;
        try { Assert.False((await f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}")).GetValueOrDefault("ok") is true); }
        catch (InvalidOperationException) { /* Existing account/session fence. */ }
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fresh_admitted_read_can_finish_after_its_originating_context_cache_expires(bool readAndOpen)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        var retainedAt = f.Now;
        f.Now = retainedAt.AddMinutes(19).AddSeconds(59);
        f.Input(readAndOpen
            ? "Give me more details about that thread and open it in a new tab."
            : "Give me the full details of that email.");
        f.BlockRead = true;
        var urls = new List<string>();
        var accepted = f.Controller.ReserveRetainedRead(f.User, default, (url, _) =>
        {
            urls.Add(url);
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = true });
        });
        Assert.NotNull(accepted);
        accepted();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var fresh = f.LastExecute.GetProperty("taskId").GetString();
        Assert.NotEqual(old, fresh);
        var proposal = JsonSerializer.SerializeToElement(new
            { task_id = old, capability_id = "mail_thread", arguments_json = "{\"id\":\"aaaaaaaa\"}" });
        // Join while the original reference is current. The accepted fresh
        // task has its own ten-minute lease; no later input grants authority.
        var collecting = f.Controller.TryCollectAdmittedReadProposalAsync("connector_execute", proposal,
            new(f.User, f.Identity), default);
        Assert.False(collecting.IsCompleted);
        f.Now = retainedAt.AddMinutes(20).AddSeconds(1);
        f.ReleaseRead.TrySetResult(true);
        var result = await collecting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(result));
        if (readAndOpen)
        {
            Assert.True(result.GetValueOrDefault("opened_count") is int count && count == 1, JsonSerializer.Serialize(result));
            Assert.EndsWith("#all/aaaaaaaa", Assert.Single(urls));
        }
        else Assert.Empty(urls);
        var requests = f.Requests;
        var again = await f.Controller.TryCollectAdmittedReadProposalAsync("connector_execute", proposal,
            new(f.User, f.Identity), default);
        Assert.Same(result, again);
        Assert.Equal(requests, f.Requests);
        Assert.Equal(2, f.Resolves);
        Assert.Equal(2, f.Executes);
        Assert.Equal(readAndOpen ? 1 : 0, urls.Count);
        f.Controller.CompleteSettledUserTurn(f.Identity);
    }

    [Fact]
    public async Task Session_reference_can_admit_a_fresh_read_after_the_old_execution_lease()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Now = f.Now.AddHours(2);
        f.Input("Give me the full details of that email.");
        var accepted = f.Controller.ReserveRetainedRead(f.User, default);
        Assert.NotNull(accepted); accepted();
        var result = await f.Controller.TryCollectAdmittedReadProposalAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = old, capability_id = "mail_thread", arguments_json = "{\"id\":\"aaaaaaaa\"}" }),
            new(f.User, f.Identity), default);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal(2, f.Resolves);
        Assert.Equal(2, f.Executes);
        Assert.NotEqual(old, result["task_id"]);
        Assert.Equal(f.Account, f.LastResolve.GetProperty("accountId").GetString());
        Assert.Equal("aaaaaaaa", f.LastExecute.GetProperty("arguments").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("fresh_task_expired")]
    [InlineData("cancelled")]
    [InlineData("owner_changed")]
    [InlineData("session_changed")]
    public async Task A_bound_fresh_read_still_suppresses_content_after_its_own_authority_ends(string ending)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Now = f.Now.AddMinutes(19).AddSeconds(59);
        f.Input("Give me the full details of that email.");
        f.BlockRead = true;
        var accepted = f.Controller.ReserveRetainedRead(f.User, default);
        Assert.NotNull(accepted);
        accepted();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var fresh = f.LastExecute.GetProperty("taskId").GetString();
        var collecting = f.Controller.TryCollectAdmittedReadProposalAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = old, capability_id = "mail_thread", arguments_json = "{\"id\":\"aaaaaaaa\"}" }),
            new(f.User, f.Identity), default);
        Assert.False(collecting.IsCompleted);
        if (ending == "fresh_task_expired") f.Now = f.Now.AddMinutes(10).AddSeconds(1);
        if (ending == "owner_changed") f.Owner = "other-owner";
        if (ending == "session_changed") f.LogicalSession = Guid.NewGuid().ToString("N");
        if (ending == "cancelled")
        {
            f.Input("Cancel the search.");
            Assert.Equal("cancelled", (await f.Control(fresh, "cancel"))["state"]);
        }
        f.ReleaseRead.TrySetResult(true);
        try
        {
            var result = await collecting.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
            Assert.DoesNotContain("The project outline is due Friday", JsonSerializer.Serialize(result));
        }
        catch (OperationCanceledException) { /* Revoked operation cannot disclose content. */ }
        Assert.Equal(2, f.Resolves);
        Assert.Equal(2, f.Executes);
    }

    [Fact]
    public async Task An_already_dispatched_exact_open_reports_its_receipt_after_the_originating_cache_expires()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        var retainedAt = f.Now;
        f.Now = retainedAt.AddMinutes(19).AddSeconds(59);
        f.Input("Open that email in a new tab.");
        var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var urls = new List<string>();
        try
        {
            var accepted = f.Controller.ReserveRetainedRead(f.User, default, async (url, token) =>
            {
                urls.Add(url);
                dispatched.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return new Dictionary<string, object> { ["ok"] = true };
            });
            Assert.NotNull(accepted);
            accepted();
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var human = new ConnectorHumanRequestSnapshot(f.User, f.Identity);
            var collecting = f.Controller.TryCollectAdmittedReadProposalAsync("open_url", default, human, default);
            Assert.False(collecting.IsCompleted);
            f.Now = retainedAt.AddMinutes(20).AddSeconds(1);
            release.TrySetResult(true);
            var result = await collecting.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
            Assert.Equal(1, result["opened_count"]);
            Assert.EndsWith("#all/aaaaaaaa", Assert.Single(urls));
            await f.Controller.TryCollectAdmittedReadProposalAsync("open_url", default, human, default);
            Assert.Single(urls);
            Assert.Equal(1, f.Resolves);
            Assert.Equal(1, f.Executes);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task Query_proposal_resolves_current_human_scope_without_replaying_old_query_or_cursor()
    {
        await using var f = new Fixture();
        var old = await f.Retain("github");
        f.Input("Can you show the recent issues in that repository?");
        var result = await f.Execute(old, "github_repos", "{\"query\":\"OLD QUERY\",\"page\":42}");
        Assert.True(result.GetValueOrDefault("ok") is true);
        Assert.False((bool)result["this_proposal_dispatched"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.DoesNotContain("OLD QUERY", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Pending_read_can_be_collected_through_old_result_and_status_proposals_without_another_worker()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("When did Daisy reply to that email?");
        f.BlockRead = true;
        var initial = f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Controller.NotifyPendingReadInput("hello"); // Releases receipt wait, not the native read.
        var pending = await initial.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("executing", pending["state"]);
        var status = await f.Control(old, "status");
        Assert.Equal("executing", status["state"]);
        f.ReleaseRead.TrySetResult(true);
        await f.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var collected = await f.Control(old, "result");
        Assert.True(collected.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(collected));
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(collected));
        var again = await f.Execute(old, "mail_read", "{\"id\":\"11111111\"}");
        Assert.Same(collected, again);
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
    }

    [Fact]
    public async Task Older_pending_recovery_receipt_collects_verified_content_after_its_worker_answer_ended_task_authority()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.Input("When did Daisy reply to that email?");
        f.BlockRead = true;
        var initial = f.Execute(old, "mail_thread", "{\"id\":\"aaaaaaaa\"}");
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Controller.NotifyPendingReadInput("hello");
        var pending = await initial.WaitAsync(TimeSpan.FromSeconds(3));
        f.ReleaseRead.TrySetResult(true);
        await f.Notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var delivered = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { action = "result", task_id = (string)pending["task_id"], operation_id = (string)pending["operation_id"] }), false, default);
        Assert.True(delivered.GetValueOrDefault("ok") is true);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        var before = f.Requests;
        var result = await f.Control(old, "result");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Contains("The project outline is due Friday", JsonSerializer.Serialize(result));
        Assert.False((bool)result["action_authority"]);
        Assert.Equal(before, f.Requests);
        Assert.Same(result, await f.Control(old, "status"));
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
    }

    [Theory]
    [InlineData("gmail", "What do the retained headers say about that email?")]
    [InlineData("googledocs", "What do you know about that document?")]
    [InlineData("github", "What do you know about that repository?")]
    public async Task Current_session_retained_context_outlives_the_backend_task_lease(string provider, string question)
    {
        await using var f = new Fixture();
        var old = await f.Retain(provider);
        f.Now = f.Now.AddHours(2);
        f.Input(question);
        var before = f.Requests;
        var result = await f.Control(old, "recall");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal("retained_context", result["state"]);
        Assert.False((bool)result["action_authority"]);
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_reference_cannot_cross_logical_sessions_without_reset(bool opening)
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.LogicalSession = Guid.NewGuid().ToString("N");
        f.Input(opening ? "Open that conversation in a new tab." : "What did that conversation say?");
        var before = f.Requests;
        if (opening)
        {
            var launched = 0;
            var result = await f.Controller.TryOpenRetainedResourcesAsync(new(f.User, f.Identity),
                (_, _) => { launched++; return Task.FromResult(new Dictionary<string, object> { ["ok"] = true }); }, default);
            Assert.False(result?.GetValueOrDefault("ok") is true);
            Assert.Equal(0, launched);
        }
        else Assert.False((await f.Control(old, "recall")).GetValueOrDefault("ok") is true);
        Assert.Equal(before, f.Requests);
    }

    [Fact]
    public async Task Earlier_selected_reference_survives_more_than_three_completed_results()
    {
        await using var f = new Fixture();
        var email = await f.Retain("gmail");
        await f.Retain("googledocs");
        await f.Retain("github");
        await f.Retain("googlecalendar");
        f.Input("Open that email in a new tab.");
        var before = f.Requests;
        var result = await f.Control(email, "recall");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True(f.Controller.TryResolveExactMailOpen(f.User, out var url, out var rejection));
        Assert.Null(rejection);
        Assert.EndsWith("#all/aaaaaaaa", url);
        Assert.Equal(before, f.Requests);
    }

    [Fact]
    public async Task Observed_disconnect_invalidates_only_its_account_without_retargeting_context()
    {
        await using var f = new Fixture();
        var firstAccount = f.Account;
        var first = await f.Retain("gmail");
        f.Account = Guid.NewGuid().ToString("N");
        var other = await f.Retain("googledocs");
        f.Controller.InvalidateAccount(f.Owner, firstAccount);
        f.Input("What was in that email?");
        Assert.False((await f.Control(first, "recall")).GetValueOrDefault("ok") is true);
        f.Input("What was in that document?");
        Assert.True((await f.Control(other, "recall")).GetValueOrDefault("ok") is true);
        Assert.False(f.Controller.TryResolveExactMailOpen("Open that email in a new tab.", out _, out _));
    }

    [Fact]
    public async Task Old_owner_disconnect_cannot_clear_current_account_context()
    {
        await using var f = new Fixture();
        var retained = await f.Retain("gmail");
        f.Controller.InvalidateAccount("obsolete-owner", f.Account);
        f.Input("What did the email say?");
        Assert.True((await f.Control(retained, "recall")).GetValueOrDefault("ok") is true);
    }

    [Fact]
    public async Task Disconnect_during_read_cancels_delivery_and_cannot_restore_retained_context()
    {
        await using var f = new Fixture();
        var old = await f.Retain("gmail");
        f.BlockRead = true;
        f.Input("Give me the full details of that email.");
        var accepted = f.Controller.ReserveRetainedRead(f.User, default);
        Assert.NotNull(accepted); accepted();
        await f.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Controller.InvalidateAccount(f.Owner, f.Account);
        f.ReleaseRead.TrySetResult(true);
        Assert.Null(f.Controller.ReserveRetainedRead(f.User, default));
        Assert.False((await f.Control(old, "recall")).GetValueOrDefault("ok") is true);
        Assert.False(KeyboardWtf.Models.KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Disconnect_fences_a_late_resolve_even_when_transport_ignores_cancellation()
    {
        await using var f = new Fixture();
        f.BlockResolve = true;
        f.Input("Find my emails with Daisy Edwards.");
        var pending = f.Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail", account_id = f.Account }), false, default);
        await f.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Controller.InvalidateAccount(f.Owner, f.Account);
        f.ReleaseResolve.TrySetResult(true);
        var result = await pending;
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Equal(0, f.Executes);
        Assert.False(KeyboardWtf.Models.KeyboardWtfState.HasLongTaskUiOwner);
        f.BlockResolve = false;
        f.Input("Find my emails with Daisy Edwards again.");
        var fresh = await f.Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail", account_id = f.Account }), false, default);
        Assert.True(fresh.GetValueOrDefault("ok") is true);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _session = new();
        internal string User = "", Identity = "", Owner = "owner", LogicalSession = Guid.NewGuid().ToString("N");
        internal string Account = Guid.NewGuid().ToString("N");
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal int Requests, Resolves, Executes, OwnerLookups, SnapshotReads;
        internal JsonElement LastResolve, LastExecute;
        internal bool BlockRead, BlockResolve;
        internal readonly TaskCompletionSource<bool> ResolveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReleaseResolve = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReleaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Notice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly JarvisConnectorController Controller;
        internal Fixture() => Controller = new(Request, () => { OwnerLookups++; return Owner; }, () => _session.Token, () => User,
            (_, _) => { Notice.TrySetResult(true); return Task.FromResult(false); }, userRequestIdentity: () => Identity, utcNow: () => Now,
            logicalLiveSessionId: () => LogicalSession, userRequestSnapshot: () => { SnapshotReads++; return new(User, Identity); }, preferDirectReadReceipt: true);
        internal void Input(string text) { User = text; Identity = Guid.NewGuid().ToString("N"); }

        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            Requests++;
            var args = JsonSerializer.SerializeToElement(body);
            if (path.EndsWith("/resolve"))
            {
                Resolves++; LastResolve = args;
                if (BlockResolve) { ResolveStarted.TrySetResult(true); await ReleaseResolve.Task; }
                var provider = args.GetProperty("connectorId").GetString();
                var capabilities = provider switch
                {
                    "gmail" => new[] { "mail_search", "mail_thread", "mail_read" },
                    "github" => new[] { "github_repos", "github_repo", "github_releases" },
                    "googlecalendar" => new[] { "calendar_list" }, "googledrive" => new[] { "drive_search", "drive_read" },
                    "googleforms" => new[] { "forms_read" }, "youtube" => new[] { "youtube_channel", "youtube_search" },
                    "slack" => new[] { "slack_search", "slack_history" }, "discord" => new[] { "discord_self", "discord_guilds" },
                    _ => new[] { "docs_read" }
                };
                return JsonSerializer.SerializeToElement(new
                {
                    taskId = Guid.NewGuid().ToString("N"), state = "ready", connectorId = provider, accountId = Account,
                    expiresAt = Now.AddMinutes(10), choices = new[] { new { accountId = Account, label = "reader@example.test" } },
                    capabilities = capabilities.Select(id => new { id, writes = false, activity = "Reading…",
                        arguments = new Dictionary<string, string> { [id.StartsWith("github_") ? "repository" : "id"] = "verified reference" } }).ToArray()
                });
            }
            if (path.EndsWith("/execute"))
            {
                Executes++; LastExecute = args;
                if (BlockRead)
                { ReadStarted.TrySetResult(true); await ReleaseRead.Task.WaitAsync(token); }
                var capability = args.GetProperty("capabilityId").GetString();
                object data = capability switch
                {
                    "mail_search" => new { messages = new[] { new { id = "11111111", threadId = "aaaaaaaa", subject = "Daisy project update",
                        from = "Daisy Edwards <daisy@example.test>", date = "2026-10-15", internalDate = "1792000000000" } }, hasMore = false, pageItemCount = 1 },
                    "mail_thread" or "mail_read" => new { id = "aaaaaaaa", totalMessages = 1, messages = new[] { new { id = "11111111", threadId = "aaaaaaaa",
                        subject = "Daisy project update", from = "Daisy Edwards <daisy@example.test>", text = "The project outline is due Friday.", contentTruncated = false } } },
                    "github_repos" => new[] { new { full_name = "owner/project", name = "project", html_url = "https://github.com/owner/project" } },
                    "github_repo" => new { full_name = "owner/project", name = "project", html_url = "https://github.com/owner/project", stargazers_count = 17 },
                    "github_releases" => new { id = 123, tag_name = "v1.2", html_url = "https://github.com/owner/project/releases/tag/v1.2", totalAssetDownloads = 91 },
                    "calendar_list" => new { items = new[] { new { id = "observed-event", summary = "Project meeting", htmlLink = "https://calendar.google.com/calendar/event?eid=observedEvent", start = new { date = "2026-10-15" } } } },
                    "drive_search" => new { files = new[] { new { id = "observed-file", name = "Project notes", webViewLink = "https://drive.google.com/file/d/observed-file/view" } } },
                    "drive_read" => new { id = "observed-file", name = "Project notes", webViewLink = "https://drive.google.com/file/d/observed-file/view" },
                    "forms_read" => new { formId = "observed-form", info = new { title = "Project form" } },
                    "youtube_channel" => new { items = new[] { new { id = "UCabcdefghijklmnopqrstuv", snippet = new { title = "Project channel" } } } },
                    "slack_search" => new { messages = new { matches = new[] { new { channel = new { id = "C123456789" }, ts = "1234567890.123456", permalink = "https://project.slack.com/archives/C123456789/p1234567890123456", text = "Project status" } } } },
                    "discord_self" => new { id = "123456789012345678", username = "project-reader" },
                    _ => new { documentId = "observed-document", title = "Project notes", body = new { content = new[] { new { paragraph = new { elements = new[] { new { textRun = new { content = "The project is completed." } } } } } } } }
                };
                var result = JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false, data } });
                if (BlockRead) ReadCompleted.TrySetResult(true);
                return result;
            }
            return JsonSerializer.SerializeToElement(new { state = "completed" });
        }
        internal async Task<string> Retain(string provider)
        {
            Input(provider == "gmail" ? "Find my emails with Daisy Edwards." : provider == "github" ? "Find my project repository using GitHub." : "Read my Google document.");
            var resolved = await Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = provider, account_id = Account }), false, default);
            var task = (string)resolved["task_id"];
            var capability = provider switch { "gmail" => "mail_search", "github" => "github_repos", "googlecalendar" => "calendar_list",
                "googledrive" => "drive_search", "googleforms" => "forms_read", "youtube" => "youtube_channel", "slack" => "slack_search", "discord" => "discord_self", _ => "docs_read" };
            var arguments = provider switch { "gmail" => "{\"query\":\"Daisy\"}", "googledocs" => "{\"id\":\"observed-document\"}",
                "googleforms" => "{\"id\":\"observed-form\"}", _ => "{}" };
            Assert.True((await Execute(task, capability, arguments)).GetValueOrDefault("ok") is true);
            Controller.CompleteSettledUserTurn(Identity);
            return task;
        }
        internal Task<Dictionary<string, object>> Execute(string task, string capability, string arguments) => Controller.ExecuteAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = task, capability_id = capability, arguments_json = arguments }), false, default);
        internal Task<Dictionary<string, object>> Control(string task, string action) => Controller.ExecuteAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = task, action }), false, default);
        public async ValueTask DisposeAsync() { _session.Cancel(); ReleaseResolve.TrySetResult(true); ReleaseRead.TrySetResult(true); await Controller.ResetAsync(); _session.Dispose(); }
    }
}
