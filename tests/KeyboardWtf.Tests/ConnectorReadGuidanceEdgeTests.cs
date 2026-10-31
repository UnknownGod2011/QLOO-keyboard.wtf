namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorReadGuidanceEdgeTests
{
    private const string Original = "Find and read the BioCyclic winner email and explain the prize";
    private const string Guidance = "Only the 2025 competition email";
    private const string OldAnswer = "The 2026 competition awarded Top 10 Best Performers.";

    [Fact]
    public async Task Relevant_human_guidance_during_native_search_reaches_same_reader_without_replacing_task()
    {
        await using var f = new Fixture(holdSearch: true, holdModel: 1);
        await f.Resolve();
        var execution = f.ExecuteRead();
        await f.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var activity = KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(20));
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var guide = await f.Guide(Guidance);
        Assert.Equal(1, f.Resolves);
        Assert.Equal(1, f.Searches);
        Assert.False(f.ReadToken.IsCancellationRequested);
        Assert.Equal(activity, KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(20)));
        f.ReleaseSearch();
        var prompt = await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Resolves);
        Assert.All(f.ProviderTasks, id => Assert.Equal(f.TaskId, id));
        Assert.All(f.ProviderAccounts, id => Assert.Equal(f.AccountId, id));
        // Intended causal RED on G: this unique exact human phrase does not
        // occur in the harmless backend receipts or frozen original request.
        Assert.Contains(Guidance, prompt, StringComparison.Ordinal);
        Assert.Contains("BioCyclic", prompt, StringComparison.Ordinal);
        Assert.True(guide.GetValueOrDefault("ok") is true);
        f.ReleaseModel();
        await execution.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Old_valid_answer_cannot_publish_over_new_relevant_human_guidance()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve();
        var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Searches);
        Assert.Equal(1, f.Bodies);
        Assert.Equal(2, f.ModelCalls);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var guide = await f.Guide(Guidance);
        Assert.Equal(1, f.Resolves);
        Assert.False(f.ReadToken.IsCancellationRequested);
        f.ReleaseModel(); // Valid supported r2 answer; not malformed JSON.
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        var connector = Assert.IsType<JsonElement>(result["connector"]);
        var answer = connector.GetProperty("result").GetProperty("data").GetProperty("answer").GetString();
        // Intended causal RED on G: the original r2 answer publishes unchanged.
        Assert.NotEqual(OldAnswer, answer);
        Assert.True(guide.GetValueOrDefault("ok") is true);
        Assert.Contains(Guidance, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(3, f.ModelCalls); // Discarded inference still consumes budget.
        Assert.Equal(1, f.Resolves);
        Assert.Equal(1, f.Searches);
        Assert.Equal(1, f.Bodies); // Existing complete content was not replayed.
        Assert.All(f.ProviderTasks, id => Assert.Equal(f.TaskId, id));
        Assert.All(f.ProviderAccounts, id => Assert.Equal(f.AccountId, id));
    }

    [Fact]
    public async Task Unrelated_observed_speech_does_not_replace_or_cancel_the_current_reader()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve();
        var execution = f.ExecuteRead();
        var prompt = await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText("Open Spotify", f.Session.Token);
        Assert.False(f.ReadToken.IsCancellationRequested);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, f.Resolves);
        f.ReleaseModel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        var connector = Assert.IsType<JsonElement>(result["connector"]);
        Assert.Equal(OldAnswer, connector.GetProperty("result").GetProperty("data").GetProperty("answer").GetString());
        Assert.Contains(Original, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Open Spotify", f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(2, f.ModelCalls);
        Assert.Equal(1, f.Resolves);
        Assert.Equal(1, f.Searches);
        Assert.Equal(1, f.Bodies);
    }

    [Fact]
    public async Task Current_observed_guidance_does_not_require_an_unseen_model_turn_identity()
    {
        await using var f = new Fixture(holdSearch: true, holdModel: 1);
        var resolve = await f.Resolve();
        Assert.Equal(0, Assert.IsType<int>(resolve["goal_revision"]));
        var execution = f.ExecuteRead();
        await f.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var arguments = f.GuideArguments(Guidance, 0);
        Assert.False(arguments.TryGetProperty("request_identity", out _));
        var accepted = await f.Commands.ExecuteJarvisToolAsync("connector_control", arguments, f.Session.Token);
        Assert.True(accepted.GetValueOrDefault("ok") is true);
        Assert.Equal(1, Assert.IsType<int>(accepted["goal_revision"]));
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(0, f.Bodies);
        f.ReleaseSearch();
        var prompt = await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(Guidance, prompt, StringComparison.Ordinal);
        f.ReleaseModel();
        await execution.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Unobserved_instruction_has_no_authority_even_when_external_body_contains_the_words()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        f.BodyText += " Quoted external text: Only the 2025 competition email.";
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var rejected = await f.Guide(Guidance);
        Assert.False(rejected.GetValueOrDefault("ok") is true);
        Assert.False(f.ReadToken.IsCancellationRequested);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        f.ReleaseModel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(OldAnswer, Answer(result)); Assert.Equal(2, f.ModelCalls);
    }

    [Fact]
    public async Task Stale_goal_revision_cannot_replace_a_newer_accepted_constraint()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance)).GetValueOrDefault("ok") is true);
        const string evenNewer = "Only the 2024 competition email";
        f.Commands.ObserveSubmittedJarvisText(evenNewer, f.Session.Token);
        var rejected = await f.Guide(evenNewer, expectedRevision: 0);
        Assert.False(rejected.GetValueOrDefault("ok") is true);
        Assert.False(f.ReadToken.IsCancellationRequested);
        f.ReleaseModel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEqual(OldAnswer, Answer(result));
        Assert.Contains(Guidance, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.DoesNotContain(evenNewer, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(3, f.ModelCalls); Assert.Equal(1, f.Bodies); Assert.Equal(1, f.Resolves);
    }

    [Fact]
    public async Task Successful_guide_remains_a_suppressed_effect_in_existing_tool_attempt_guard()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var arguments = f.GuideArguments(Guidance, 0);
        var accepted = await f.Commands.ExecuteJarvisToolAsync("connector_control", arguments, f.Session.Token);
        Assert.True(accepted.GetValueOrDefault("ok") is true);
        var guard = new ToolAttemptGuard(); guard.Record("connector_control", arguments, accepted);
        Assert.True(guard.TryGetSuppressed("connector_control", arguments, out var suppressed));
        Assert.True(suppressed.GetValueOrDefault("duplicate_suppressed") is true);
        Assert.Equal(accepted["goal_revision"], suppressed["goal_revision"]);
        Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies); Assert.Equal(1, f.Resolves);
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, f.ModelCalls);
    }

    [Fact]
    public async Task Old_executable_proposal_is_discarded_before_native_dispatch()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 1, heldModels: new[] { 1, 2 });
        f.Response = (call, _) => call <= 2 ? ReadProposal() : GuidedAnswer();
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.WaitModel(1);
        Assert.Equal(1, f.Searches); Assert.Equal(0, f.Bodies);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance)).GetValueOrDefault("ok") is true);
        f.ReleaseModel(1);
        var latestPrompt = await f.WaitModel(2);
        Assert.Equal(0, f.Bodies); // Old model's valid mail_read was not dispatched.
        Assert.Contains(Guidance, latestPrompt, StringComparison.Ordinal);
        Assert.Equal(1, f.Resolves); Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        f.ReleaseModel(2);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEqual(OldAnswer, Answer(result));
        Assert.Equal(3, f.ModelCalls); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
    }

    [Fact]
    public async Task Repeated_accepted_guidance_consumes_one_continuous_model_budget()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 0);
        f.ResponseAsync = async (call, _) =>
        {
            if (call == 1) return ReadProposal();
            var observed = "Only the " + (2000 + call) + " competition email";
            f.Commands.ObserveSubmittedJarvisText(observed, f.Session.Token);
            Assert.True((await f.Guide(observed, expectedRevision: call - 2)).GetValueOrDefault("ok") is true);
            return OriginalAnswer(); // Each valid answer belongs to the revision captured before its await.
        };
        await f.Resolve(); var result = await f.ExecuteRead().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEqual(OldAnswer, Answer(result));
        Assert.Equal(9, f.ModelCalls); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        Assert.True(Assert.IsType<JsonElement>(result["connector"]).GetProperty("result").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Explicit_human_cancel_after_guidance_discards_late_answer_and_ownership()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance)).GetValueOrDefault("ok") is true);
        f.Commands.ObserveSubmittedJarvisText("Cancel that Gmail task", f.Session.Token);
        Assert.True(f.ReadToken.IsCancellationRequested);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        f.ReleaseModel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Equal(2, f.ModelCalls); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("logical_session")]
    public async Task Changed_current_owner_cannot_guide_the_originating_reader(string change)
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (change == "account") f.CurrentOwner = "different-fixture-owner";
        else f.CurrentLogicalLiveSessionId = Guid.NewGuid().ToString("N");
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        if (change == "account")
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Guide(Guidance));
        else Assert.False((await f.Guide(Guidance)).GetValueOrDefault("ok") is true);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        f.ReleaseModel(); var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.DoesNotContain(OldAnswer, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        Assert.Equal(2, f.ModelCalls);
    }

    [Fact]
    public async Task Guidance_after_committed_result_truthfully_reports_settled_without_restarting()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 0);
        await f.Resolve(); var result = await f.ExecuteRead().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(OldAnswer, Answer(result));
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var settled = await f.Guide(Guidance);
        Assert.True(settled.GetValueOrDefault("ok") is true);
        Assert.True(settled.GetValueOrDefault("already_settled") is true);
        Assert.Equal(2, f.ModelCalls); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
    }

    [Fact]
    public async Task Only_current_guided_answer_delivery_ends_the_existing_owned_task_ui()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var originalIdentity = f.Commands.ConnectorUserRequestIdentity;
        var execution = f.ExecuteRead(); await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance)).GetValueOrDefault("ok") is true);
        Assert.NotEqual(originalIdentity, f.Commands.ConnectorUserRequestIdentity);
        f.ReleaseModel(); var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEqual(OldAnswer, Answer(result)); Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        f.Controller.CompleteSettledUserTurn(originalIdentity);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner); Assert.Equal(0, f.Completes);
        f.Commands.CompleteJarvisUserTurn(assistantAnswerComplete: true);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, f.Completes); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
    }

    [Fact]
    public async Task Dispatched_unknown_write_cannot_be_guided_or_replayed_on_a_new_human_turn()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 0, holdWrite: true,
            original: "Send an email to fixture@example.invalid with subject Fixture original and body Fixture original body");
        await f.Resolve(); var execution = f.ExecuteWrite();
        await f.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, f.Writes);
        f.Commands.ObserveSubmittedJarvisText("Change the recipient to other@example.invalid", f.Session.Token);
        Assert.False((await f.Guide("Change the recipient to other@example.invalid")).GetValueOrDefault("ok") is true);
        Assert.Equal(1, f.Writes); Assert.Equal("fixture@example.invalid", f.DispatchedWrite.GetProperty("to").GetString());
        Assert.Equal("Fixture original body", f.DispatchedWrite.GetProperty("body").GetString());
        f.ReleaseWrite(); var unknown = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("outcome_unknown", Assert.IsType<JsonElement>(unknown["connector"]).GetProperty("state").GetString());
        Assert.False((await f.ExecuteWrite()).GetValueOrDefault("ok") is true);
        Assert.Equal(1, f.Writes); Assert.Equal(1, f.Resolves); Assert.Equal(0, f.ModelCalls);
    }

    [Theory]
    [InlineData(Original, "Use Managed for this task now")]
    [InlineData(Original, "Use my work Gmail account")]
    [InlineData(Original, "Forward that email to other@example.invalid")]
    public async Task Guidance_cannot_silently_replace_frozen_scope_owner_or_read_authority(string original, string changed)
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2, original: original);
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText(changed, f.Session.Token);
        var refused = await f.Guide(changed);
        Assert.False(refused.GetValueOrDefault("ok") is true);
        Assert.False(f.ReadToken.IsCancellationRequested);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies); Assert.Equal(0, f.Writes);
        Assert.All(f.ProviderAccounts, account => Assert.Equal(f.AccountId, account));
        Assert.All(f.ProviderTasks, task => Assert.Equal(f.TaskId, task));
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, f.ModelCalls);
    }

    [Fact]
    public async Task Same_observed_human_input_repeated_with_fresh_revision_is_idempotent()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var first = await f.Guide(Guidance, 0); Assert.True(first.GetValueOrDefault("ok") is true);
        var identity = f.Commands.ConnectorUserRequestIdentity;
        var repeated = await f.Guide(Guidance, expectedRevision: 1);
        Assert.True(repeated.GetValueOrDefault("ok") is true); Assert.True(repeated.GetValueOrDefault("already_applied") is true);
        Assert.Equal(1, Assert.IsType<int>(repeated["goal_revision"]));
        Assert.Equal(identity, f.Commands.ConnectorUserRequestIdentity);
        Assert.Equal(2, f.ModelCalls); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, f.ModelCalls); Assert.Equal(1, f.Resolves);
    }

    [Fact]
    public async Task Tool_only_turn_boundary_preserves_latest_accepted_replay_but_cannot_authorize_a_new_constraint()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveJarvisTranscript(Guidance, f.Session.Token);
        var first = await f.Guide(Guidance, 0); Assert.True(first.GetValueOrDefault("ok") is true);
        var identity = f.Commands.ConnectorUserRequestIdentity;
        f.Commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        Assert.Equal(Guidance, f.Commands.ConnectorUserRequestSnapshot.Text);
        Assert.Equal(identity, f.Commands.ConnectorUserRequestSnapshot.Identity);
        var repeated = await f.Guide(Guidance, 1);
        Assert.True(repeated.GetValueOrDefault("ok") is true);
        Assert.True(repeated.GetValueOrDefault("already_applied") is true);
        Assert.Equal(1, Assert.IsType<int>(repeated["goal_revision"]));
        var unobserved = await f.Guide("Also only senders from Devpost", 1);
        Assert.False(unobserved.GetValueOrDefault("ok") is true);
        Assert.Equal("guidance_not_authorized", unobserved["state"]);
        Assert.Equal(1, Assert.IsType<int>(unobserved["goal_revision"]));
        f.Commands.ObserveJarvisTranscript("Also only senders from Devpost", f.Session.Token);
        Assert.Equal(identity, f.Commands.ConnectorUserRequestSnapshot.Identity);
        var changedObservation = await f.Guide(Guidance, 1);
        Assert.False(changedObservation.GetValueOrDefault("ok") is true);
        Assert.Equal("guidance_not_authorized", changedObservation["state"]);
        Assert.Equal(1, Assert.IsType<int>(changedObservation["goal_revision"]));
        Assert.Equal(2, f.ModelCalls); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, f.ModelCalls); Assert.Equal(1, f.Resolves);
    }

    [Fact]
    public async Task Additional_human_constraint_keeps_earlier_nonconflicting_guidance_in_the_same_reader()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2, heldModels: new[] { 2, 3 });
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance, 0)).GetValueOrDefault("ok") is true);
        f.ReleaseModel(2); await f.WaitModel(3);
        const string additional = "Also only senders from Devpost";
        var priorIdentity = f.Commands.ConnectorUserRequestIdentity;
        f.Commands.ObserveSubmittedJarvisText(additional, f.Session.Token);
        Assert.NotEqual(priorIdentity, f.Commands.ConnectorUserRequestIdentity);
        var accepted = await f.Guide(additional, 1); Assert.True(accepted.GetValueOrDefault("ok") is true);
        Assert.Equal(2, Assert.IsType<int>(accepted["goal_revision"]));
        f.ReleaseModel(3); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        using var prompt = JsonDocument.Parse(f.Prompts.Last());
        var instructions = prompt.RootElement.GetProperty("readGuidance").GetProperty("instructions")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[] { Guidance, additional }, instructions);
        Assert.Equal(4, f.ModelCalls); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        Assert.All(f.ProviderTasks, task => Assert.Equal(f.TaskId, task)); Assert.All(f.ProviderAccounts, account => Assert.Equal(f.AccountId, account));
    }

    [Fact]
    public async Task Explicit_correction_of_prior_guidance_is_ordered_after_it_without_dropping_other_constraints()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2, heldModels: new[] { 2, 3 });
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance)).GetValueOrDefault("ok") is true);
        f.ReleaseModel(2); await f.WaitModel(3);
        const string corrected = "Actually the 2024 competition instead, still from Devpost";
        f.Commands.ObserveSubmittedJarvisText(corrected, f.Session.Token);
        Assert.True((await f.Guide(corrected, 1)).GetValueOrDefault("ok") is true);
        f.ReleaseModel(3); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        using var prompt = JsonDocument.Parse(f.Prompts.Last()); var guidance = prompt.RootElement.GetProperty("readGuidance");
        Assert.Equal(new[] { Guidance, corrected }, guidance.GetProperty("instructions").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Contains("later explicit correction replaces", guidance.GetProperty("scope").GetString(), StringComparison.Ordinal);
        Assert.Equal(4, f.ModelCalls); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
        // Prompt/ordering proof only. A real model must still demonstrate that
        // its interpreted result applies the correction correctly.
    }

    [Theory]
    [InlineData("Only emails that asked me to send a CV")]
    [InlineData("Only emails that asked me to update my CV")]
    [InlineData("Only emails discussing my reply to the professor")]
    [InlineData("Update the search criteria to only the 2025 competition")]
    public async Task Content_description_words_do_not_misclassify_read_guidance_as_a_write(string observed)
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveSubmittedJarvisText(observed, f.Session.Token);
        var accepted = await f.Guide(observed); Assert.True(accepted.GetValueOrDefault("ok") is true);
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(observed, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(3, f.ModelCalls); Assert.Equal(0, f.Writes); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Bodies);
    }

    [Fact]
    public async Task Logical_session_change_after_guidance_fences_snapshot_dispatch_and_final_publication()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance)).GetValueOrDefault("ok") is true);
        f.CurrentLogicalLiveSessionId = Guid.NewGuid().ToString("N");
        f.ReleaseModel(); var failed = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(failed.GetValueOrDefault("ok") is true);
        if (failed.TryGetValue("connector", out var raw) && raw is JsonElement receipt)
            Assert.DoesNotContain(OldAnswer, receipt.GetRawText(), StringComparison.Ordinal);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(2, f.ModelCalls); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies);
    }

    [Fact]
    public async Task Aggregate_guidance_bound_does_not_erase_prior_accepted_human_constraints()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        var first = "Only the 2025 competition email with this topic anchor " + new string('a', 2840);
        f.Commands.ObserveSubmittedJarvisText(first, f.Session.Token);
        Assert.True((await f.Guide(first)).GetValueOrDefault("ok") is true);
        var tooMuch = "Also this independent topic anchor " + new string('b', 240);
        f.Commands.ObserveSubmittedJarvisText(tooMuch, f.Session.Token);
        var rejected = await f.Guide(tooMuch, 1); Assert.False(rejected.GetValueOrDefault("ok") is true);
        Assert.Equal(1, Assert.IsType<int>(rejected["goal_revision"]));
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(first, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.DoesNotContain(tooMuch, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(3, f.ModelCalls); Assert.Equal(1, f.Bodies); Assert.Equal(1, f.Resolves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_commit_race_rejects_old_candidate_without_restarting_reader_budgets(bool exhaustBeforeCommit)
    {
        var revision = 0; var commits = 0; var models = 0; var executes = 0;
        var capabilities = new[] {
            new ConnectorReadCapability("mail_search", "Native topic search", new Dictionary<string,string> { ["query"] = "Search terms" }),
            new ConnectorReadCapability("mail_read", "Observed content", new Dictionary<string,string> { ["id"] = "Observed ID" }) };
        var input = new ConnectorReadReasonerInput(Original, capabilities)
        {
            InitialCapability = "mail_search", InitialArguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("biocyclic winner") },
            Guidance = () => revision == 0 ? new(0, "") : new(1, Guidance) { Instructions = new[] { Guidance } },
            TryCommit = (candidate, expected) =>
            {
                ++commits;
                Assert.Equal(revision, expected);
                if (commits == 1) { revision = 1; return false; } // New guide wins inside the real owner's commit lock.
                Assert.Equal(1, candidate.GoalRevision); return true;
            }
        };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, prompt, _, limit, _) =>
            {
                Assert.Equal(2400, limit); ++models;
                if (models == 1) return Task.FromResult(ReadProposal());
                if (exhaustBeforeCommit) return Task.FromResult(ReadProposal()); // Duplicate proposal repairs consume the same bounded model budget.
                if (models == 2) return Task.FromResult(OriginalAnswer());
                Assert.Contains(Guidance, prompt, StringComparison.Ordinal); return Task.FromResult(GuidedAnswer());
            },
            (capability, arguments, _) =>
            {
                ++executes;
                return Task.FromResult(capability == "mail_search"
                    ? JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
                        data = new { queryUsed = arguments["query"].GetString(), pageItemCount = 1, hasMore = false,
                            messages = new[] { new { id = "message-1", threadId = "thread-1" } } } } })
                    : JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
                        data = new { id = "message-1", threadId = "thread-1", contentTruncated = false, text = "The 2026 BioCyclic prize is Top 10 Best Performers." } } }));
            }, null, CancellationToken.None);
        Assert.Equal(2, commits); Assert.Equal(2, executes); Assert.Equal(2, result.Executions);
        Assert.Equal(exhaustBeforeCommit ? 9 : 3, models); Assert.Equal(models, result.ModelCalls);
        Assert.Equal(1, result.GoalRevision); Assert.True(result.Partial);
        Assert.Equal(new[] { 0, 0 }, result.EvidenceGoalRevisions);
        Assert.NotEqual(OldAnswer, result.Answer);
        // This verifies the real reader's atomic-acceptance callback contract;
        // controller _sync serialization is additionally reviewed in source.
    }

    [Fact]
    public async Task Earlier_constraint_reobserved_after_a_correction_under_same_spoken_identity_is_new_guidance()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve(); var origin = f.Commands.ConnectorUserRequestIdentity;
        var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveJarvisTranscript(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance, 0)).GetValueOrDefault("ok") is true);
        const string correction = "Actually the 2024 competition instead";
        f.Commands.ObserveJarvisTranscript(correction, f.Session.Token);
        Assert.True((await f.Guide(correction, 1)).GetValueOrDefault("ok") is true);
        f.Commands.ObserveJarvisTranscript(Guidance, f.Session.Token);
        Assert.Equal(origin, f.Commands.ConnectorUserRequestIdentity);
        var restored = await f.Guide(Guidance, 2);
        Assert.True(restored.GetValueOrDefault("ok") is true);
        Assert.Equal(3, Assert.IsType<int>(restored["goal_revision"]));
        Assert.False(restored.GetValueOrDefault("already_applied") is true);
        var exactReplay = await f.Guide(Guidance, 3);
        Assert.True(exactReplay.GetValueOrDefault("already_applied") is true);
        Assert.Equal(3, Assert.IsType<int>(exactReplay["goal_revision"]));
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        using var prompt = JsonDocument.Parse(f.Prompts.Last());
        Assert.Equal(new[] { Guidance, correction, Guidance }, prompt.RootElement.GetProperty("readGuidance")
            .GetProperty("instructions").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal(3, f.ModelCalls); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies); Assert.Equal(1, f.Resolves);
    }

    [Fact]
    public async Task Completed_guided_context_retains_cumulative_human_scope_and_cited_evidence_origin_without_new_authority()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        f.MessageId = "a111111111111111"; f.ThreadId = "b111111111111111";
        await f.Resolve(); var execution = f.ExecuteRead(); await f.WaitModel(2);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        Assert.True((await f.Guide(Guidance, 0)).GetValueOrDefault("ok") is true);
        const string additional = "Also only senders from Devpost";
        f.Commands.ObserveSubmittedJarvisText(additional, f.Session.Token);
        Assert.True((await f.Guide(additional, 1)).GetValueOrDefault("ok") is true);
        f.ReleaseModel(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.CompleteJarvisUserTurn(assistantAnswerComplete: true);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner); Assert.Equal(1, f.Completes);
        var recall = await f.Commands.ExecuteJarvisToolAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = f.TaskId, action = "recall" }), f.Session.Token);
        Assert.True(recall.GetValueOrDefault("ok") is true); Assert.False(Assert.IsType<bool>(recall["action_authority"]));
        var context = Assert.IsType<ConnectorSessionResult>(recall["context"]);
        Assert.Equal(Original, context.Request); Assert.Equal(2, context.GoalRevision);
        Assert.Equal(new[] { Guidance, additional }, context.AcceptedReadGuidance);
        Assert.Equal(new[] { 0 }, context.EvidenceGoalRevisions); Assert.True(context.Partial);
        Assert.Equal(f.TaskId, context.TaskId); Assert.Equal(f.AccountId, context.AccountId);
        Assert.Equal(3, f.ModelCalls); Assert.Equal(1, f.Searches); Assert.Equal(1, f.Bodies); Assert.Equal(1, f.Resolves);
        Assert.Equal(0, f.Writes);
    }

    private static string Answer(Dictionary<string, object> result) => Assert.IsType<JsonElement>(result["connector"])
        .GetProperty("result").GetProperty("data").GetProperty("answer").GetString();
    private static string ReadProposal() => JsonSerializer.Serialize(new { action = "execute", capability = "mail_read",
        arguments_json = "{\"id\":\"message-1\"}", answer = "", references = Array.Empty<string>(), partial = false });
    private static string OriginalAnswer() => JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
        answer = OldAnswer, references = new[] { "r2" }, partial = false });
    private static string GuidedAnswer() => JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
        answer = "The observed email concerns 2026; no 2025 competition winner was verified.", references = new[] { "r2" }, partial = true });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        private readonly bool _holdSearch;
        private readonly int _holdModel;
        private readonly string _initialQuery;
        private readonly TaskCompletionSource<bool> _searchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<int> _heldModels;
        private readonly Dictionary<int, TaskCompletionSource<bool>> _modelReleases = Enumerable.Range(1, 9)
            .ToDictionary(n => n, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        private readonly Dictionary<int, TaskCompletionSource<string>> _modelStarts = Enumerable.Range(1, 9)
            .ToDictionary(n => n, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        private readonly bool _holdWrite;
        private readonly TaskCompletionSource<bool> _writeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly JarvisAutomationService _automation;
        private readonly JarvisConnectorController _controller;
        private readonly Dictionary<string, string> _serverTaskAccounts = new(StringComparer.Ordinal);
        internal CancellationTokenSource Session { get; } = new();
        internal CommandRegistry Commands { get; }
        internal string TaskId { get; } = Guid.NewGuid().ToString("N");
        internal string AccountId { get; } = Guid.NewGuid().ToString("N");
        internal string LogicalLiveSessionId { get; } = Guid.NewGuid().ToString("N");
        internal string CurrentLogicalLiveSessionId;
        internal string CurrentOwner = "fixture-owner";
        internal string BodyText = "The 2026 BioCyclic competition prize is Top 10 Best Performers.";
        internal string MessageId = "message-1", ThreadId = "thread-1";
        internal Func<int, string, string> Response;
        internal Func<int, string, Task<string>> ResponseAsync;
        internal JarvisConnectorController Controller => _controller;
        internal JsonElement DispatchedWrite;
        internal TaskCompletionSource<bool> WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> HeldPrompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> Prompts { get; } = [];
        internal List<string> ProviderTasks { get; } = [];
        internal List<string> ProviderAccounts { get; } = [];
        internal CancellationToken ReadToken;
        internal int Resolves, Searches, Bodies, ModelCalls, Writes, Completes;
        internal Fixture(bool holdSearch, int holdModel, int[] heldModels = null, bool holdWrite = false, string original = Original)
        {
            _holdSearch = holdSearch; _holdModel = holdModel;
            _initialQuery = ConnectorReadReasoner.TryLiteralMailQuery(original, out var literal, out _) ? literal : "biocyclic winner";
            _heldModels = new(heldModels ?? (holdModel > 0 ? new[] { holdModel } : Array.Empty<int>()));
            _holdWrite = holdWrite; CurrentLogicalLiveSessionId = LogicalLiveSessionId;
            var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
            settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
            var notifications = new NotificationService(null);
            _automation = new(notifications, settings, new(Path.Combine(_directory, "history.json")),
                new(Path.Combine(_directory, "mappings.json")), () => throw new InvalidOperationException("Fixture refuses Settings opening"));
            Commands = new(null, null, null, settings, notifications, automation: _automation);
            _controller = new(Request, () => CurrentOwner, () => Session.Token, () => Commands.ConnectorUserRequest,
                (_, _) => Task.FromResult(true), userRequestIdentity: () => Commands.ConnectorUserRequestIdentity,
                logicalLiveSessionId: () => CurrentLogicalLiveSessionId, reasonRead: Model);
            _automation.Connectors = _controller;
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Fixture listening");
            Commands.ObserveSubmittedJarvisText(original, Session.Token);
        }
        internal Task<Dictionary<string, object>> Resolve() => Commands.ExecuteJarvisToolAsync("connector_resolve",
            JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), Session.Token);
        internal Task<Dictionary<string, object>> ExecuteRead() => Commands.ExecuteJarvisToolAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = TaskId, capability_id = "mail_search",
                arguments_json = JsonSerializer.Serialize(new { query = _initialQuery }) }), Session.Token);
        internal JsonElement GuideArguments(string text, int expectedRevision) => JsonSerializer.SerializeToElement(new
            { task_id = TaskId, action = "guide", instruction = text, expected_goal_revision = expectedRevision });
        internal Task<Dictionary<string, object>> Guide(string text, int expectedRevision = 0) =>
            Commands.ExecuteJarvisToolAsync("connector_control", GuideArguments(text, expectedRevision), Session.Token);
        internal Task<Dictionary<string, object>> ExecuteWrite() => Commands.ExecuteJarvisToolAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = TaskId, capability_id = "mail_send",
                arguments_json = "{\"to\":\"fixture@example.invalid\",\"subject\":\"Fixture original\",\"body\":\"Fixture original body\"}" }), Session.Token);
        internal void ReleaseSearch() => _searchRelease.TrySetResult(true);
        internal void ReleaseModel() => ReleaseModel(_holdModel);
        internal void ReleaseModel(int call) => _modelReleases[call].TrySetResult(true);
        internal Task<string> WaitModel(int call) => _modelStarts[call].Task.WaitAsync(TimeSpan.FromSeconds(3));
        internal void ReleaseWrite() => _writeRelease.TrySetResult(true);
        private async Task<string> Model(string task, string logical, string instruction, string prompt, object schema, int limit, CancellationToken token)
        {
            Assert.Equal(TaskId, task);
            Assert.Equal(LogicalLiveSessionId, logical);
            Assert.Equal(2400, limit);
            Prompts.Add(prompt);
            var call = ++ModelCalls;
            _modelStarts[call].TrySetResult(prompt);
            if (_heldModels.Contains(call)) { HeldPrompt.TrySetResult(prompt); await _modelReleases[call].Task.WaitAsync(token); }
            if (ResponseAsync is not null) return await ResponseAsync(call, prompt);
            if (Response is not null) return Response(call, prompt);
            return call == 1
                ? JsonSerializer.Serialize(new { action = "execute", capability = "mail_read", arguments_json = JsonSerializer.Serialize(new { id = MessageId }), answer = "", references = Array.Empty<string>(), partial = false })
                : JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
                    answer = call == 2 ? OldAnswer : "The observed email concerns 2026; no 2025 competition winner was verified.",
                    references = new[] { "r2" }, partial = call > 2 });
        }
        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (path == "v1/connectors/resolve")
            {
                ++Resolves;
                _serverTaskAccounts.Add(TaskId, AccountId);
                return JsonSerializer.SerializeToElement(new { taskId = TaskId, connectorId = "gmail", accountId = AccountId,
                    state = "ready", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), choices = new[] { new { accountId = AccountId, label = "Fixture" } },
                    capabilities = new[] {
                        new { id = "mail_search", writes = false, activity = "Searching Gmail…", description = "Native search",
                            arguments = new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" } },
                        new { id = "mail_read", writes = false, activity = "Reading Gmail…", description = "Observed message content",
                            arguments = new Dictionary<string, string> { ["id"] = "observed message ID" } },
                        // Real Gmail metadata exposes write capabilities too;
                        // their existence alone must not reject a read-only
                        // guidance operation or grant a write to this request.
                        new { id = "mail_send", writes = true, activity = "Sending email…", description = "Existing permission-owned write",
                            arguments = new Dictionary<string, string> { ["to"] = "recipient", ["subject"] = "subject", ["body"] = "body" } } } });
            }
            if (path == "v1/connectors/execute")
            {
                ReadToken = token;
                var payload = JsonSerializer.SerializeToElement(body);
                var executedTask = payload.GetProperty("taskId").GetString()!;
                Assert.True(_serverTaskAccounts.TryGetValue(executedTask, out var selectedAccount));
                Assert.Equal(LogicalLiveSessionId, payload.GetProperty("logicalLiveSessionId").GetString());
                ProviderTasks.Add(executedTask);
                ProviderAccounts.Add(selectedAccount!); // Fake server's resolved binding; execute DTO has no account field.
                var capability = payload.GetProperty("capabilityId").GetString();
                if (capability == "mail_send")
                {
                    ++Writes; DispatchedWrite = payload.GetProperty("arguments").Clone(); WriteStarted.TrySetResult(true);
                    if (_holdWrite) await _writeRelease.Task.WaitAsync(token);
                    return JsonSerializer.SerializeToElement(new { taskId = TaskId, operationId = Guid.NewGuid().ToString("N"),
                        state = "outcome_unknown", verified = false, message = "Synthetic unknown write outcome; never retry." });
                }
                if (capability == "mail_search")
                {
                    ++Searches; SearchStarted.TrySetResult(true);
                    if (_holdSearch) await _searchRelease.Task.WaitAsync(token);
                    return JsonSerializer.SerializeToElement(new { taskId = TaskId, operationId = Guid.NewGuid().ToString("N"), state = "completed", verified = true,
                        result = new { truncated = false, data = new { queryUsed = payload.GetProperty("arguments").GetProperty("query").GetString(), pageItemCount = 1, hasMore = false,
                            messages = new[] { new { id = MessageId, threadId = ThreadId } } } } });
                }
                Assert.Equal("mail_read", capability); ++Bodies;
                Assert.Equal(MessageId, payload.GetProperty("arguments").GetProperty("id").GetString());
                return JsonSerializer.SerializeToElement(new { taskId = TaskId, operationId = Guid.NewGuid().ToString("N"), state = "completed", verified = true,
                    result = new { truncated = false, data = new { id = MessageId, threadId = ThreadId, contentTruncated = false,
                        text = BodyText,
                        headers = new[] { new { name = "Subject", value = "BioCyclic 2026 winner" } } } } });
            }
            Assert.True(path.EndsWith("/cancel", StringComparison.Ordinal) || path.EndsWith("/complete", StringComparison.Ordinal));
            if (path.EndsWith("/complete", StringComparison.Ordinal)) ++Completes;
            return JsonSerializer.SerializeToElement(new { state = path.EndsWith("/complete", StringComparison.Ordinal) ? "completed" : "cancelled" });
        }
        public async ValueTask DisposeAsync()
        {
            Session.Cancel();
            await _controller.ResetAsync();
            _automation.Dispose(); Session.Dispose();
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup containment failed");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
