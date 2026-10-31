namespace KeyboardWtf.Tests;

using System.Globalization;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

// IGNORED PROPOSAL ONLY: UNBUILT/UNRUN. The real Controller owns admission,
// Reader execution, revisions, commit and retention. All callbacks are synthetic.
// No scope/private-field injection, simulator, profile, gateway, HTTP client,
// application, HWND, microphone or real model is initialized here.
// RetrievalReplacement cases FIRST require actual GuideRead acceptance. Current
// J1 can fail at that phase gate; that is NOT a downstream ledger RED.
[Collection("Live event integration")]
public sealed class ConnectorReadScopeEvidenceProposalTests
{
    private const string Original = "Summarize my email using exact query: subject:\"BioCyclic\"";
    private const string Changed = "Actually, use exact query: subject:\"Daisy\"";
    private const string A = "subject:\"BioCyclic\"", B = "subject:\"Daisy\"";
    private const string OldAnswer = "OLD_SCOPE_ANSWER_SENTINEL";
    private const string CurrentAnswer = "The currently scoped correspondence has verified content.";

    [Fact]
    public async Task Natural_professor_correction_requires_current_search_and_keeps_one_owner()
    {
        await using var f = new Fixture(original: "Find and read the BioCyclic research emails"); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "22222222"));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Answer("UNREOBSERVED_PROFESSOR", ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search("BioCyclic professor")); f.Models.Enqueue(_ => Read("22222222"));
        f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r4"], "matched", "r4"));
        f.HoldModel(2); await f.Resolve(); var work = f.Step(); await f.WaitModel(2);
        await f.Accept("Only emails from the professor."); f.ReleaseModel(2);
        var result = await f.Result(work);
        Assert.DoesNotContain("UNREOBSERVED_PROFESSOR", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(CurrentAnswer, Data(result).GetProperty("answer").GetString());
        Assert.Equal(new[] { A, "BioCyclic professor" }, f.Queries); Assert.Contains("Only emails from the professor.", f.Prompts.Last());
        Assert.Equal(1, f.Resolves); Assert.False(f.ReadToken.IsCancellationRequested); f.AssertOwner();
    }

    [Fact]
    public async Task Unsupported_day_with_clear_body_restriction_keeps_day_and_blocks_bodies()
    {
        await using var f = new Fixture(day: true); f.Searches.Enqueue(q => Page(q, "11111111", f.Received(0)));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Read("11111111"));
        f.Models.Enqueue(_ => Answer("Only the permitted headers are available.", ["r1"], "partial", partial: true));
        f.HoldModel(1); await f.Resolve(); var work = f.Step(); await f.WaitModel(1);
        await f.Accept("Only last week's emails; do not read message bodies."); f.ReleaseModel(1);
        var result = await f.Result(work); Assert.Equal(0, f.BodyCalls); Assert.Equal(1, f.NativeCalls);
        Assert.True(Partial(result)); Assert.DoesNotContain("last week's", f.Prompts.Last());
        Assert.Contains("Headers and metadata only", f.Prompts.Last()); f.AssertOwner();
    }

    [Fact]
    public void Check_today_instead_restores_frozen_day_and_original_broad_dates_keep_planning()
    {
        var clock = DateTimeOffset.Parse("2026-10-11T19:49:00Z", CultureInfo.InvariantCulture);
        var original = ConnectorReadScope.Freeze("Check today's emails", clock, "Asia/Kolkata");
        var yesterday = original.Refine("Actually only yesterday.", []).Scope;
        var restored = yesterday.Refine("Check today instead.", ["Actually only yesterday."]);
        Assert.True(restored.Accepted); Assert.Equal(original.DateScope.Query, restored.Scope.DateScope.Query);
        Assert.Equal(original.RequestUtc, restored.Scope.RequestUtc);
        Assert.Null(ConnectorReadScope.Freeze("Find research replies last year", clock, "Asia/Kolkata").DateScope);
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Old_empty_query_cannot_prove_changed_scope_absence_before_a_current_native_search()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(Empty); f.Searches.Enqueue(Empty); f.HoldSearch = 1;
        f.Models.Enqueue(_ => Answer("UNPROVEN_CURRENT_ABSENCE", [], "not_found"));
        f.Models.Enqueue(_ => Search(B));
        f.Models.Enqueue(_ => Answer("No messages matched the accepted exact Daisy query.", [], "not_found"));
        await f.Resolve(); var work = f.Step(); await f.SearchHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Accept(Changed); f.SearchRelease.TrySetResult(true);
        var result = await f.Result(work); var data = Data(result);
        Assert.Equal("not_found", data.GetProperty("goalStatus").GetString());
        Assert.DoesNotContain("UNPROVEN_CURRENT_ABSENCE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(new[] { A, B }, f.Queries); Assert.Equal(2, f.NativeCalls); Assert.Equal(0, f.BodyCalls);
        Assert.Equal(3, f.ModelCalls); Assert.Contains("validation_error", f.Prompts[1], StringComparison.Ordinal);
        await f.CompleteAndRecall(1); f.AssertOwner();
    }

    [Fact, Trait("Phase", "CurrentScopeControl")]
    public async Task Complete_empty_native_search_can_prove_absence_for_its_unchanged_exact_scope()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(Empty);
        f.Models.Enqueue(_ => Answer("No messages matched this exact query.", [], "not_found"));
        await f.Resolve(); var result = await f.Result(f.Step());
        Assert.Equal("not_found", Data(result).GetProperty("goalStatus").GetString());
        Assert.Single(f.Queries); Assert.Equal(A, f.Queries[0]); Assert.Equal(1, f.ModelCalls);
        Assert.Equal(1, f.NativeCalls); Assert.Equal(0, f.BodyCalls); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Old_candidate_is_not_a_positive_changed_query_target_until_native_reobservation()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "22222222"));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Answer("UNREOBSERVED_TARGET_SENTINEL", ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search(B)); f.Models.Enqueue(_ => Read("22222222"));
        f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r4"], "matched", "r4")); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept(Changed); f.ReleaseModel(2);
        var result = await f.Result(work); var data = Data(result);
        Assert.Equal(CurrentAnswer, data.GetProperty("answer").GetString()); Assert.Equal("r4", data.GetProperty("targetReference").GetString());
        Assert.DoesNotContain("UNREOBSERVED_TARGET_SENTINEL", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(OldAnswer, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(new[] { A, B }, f.Queries); Assert.Equal(new[] { "11111111", "22222222" }, f.BodyIds);
        Assert.Equal(4, f.NativeCalls); Assert.Equal(6, f.ModelCalls); Assert.Contains("validation_error", f.Prompts[3], StringComparison.Ordinal);
        var context = await f.CompleteAndRecall(1); Assert.Equal("r4", context.TargetReference); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Old_query_cursor_cannot_cross_the_accepted_canonical_query()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111", cursor: "old-cursor"));
        f.Searches.Enqueue(q => Page(q, "22222222")); f.HoldSearch = 1;
        f.Models.Enqueue(_ => Search(B, "old-cursor")); f.Models.Enqueue(_ => Search(B));
        f.Models.Enqueue(_ => Read("22222222")); f.Models.Enqueue(prompt => CurrentBodyAnswer(prompt, "22222222"));
        await f.Resolve(); var work = f.Step(); await f.SearchHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Accept(Changed); f.SearchRelease.TrySetResult(true); var result = await f.Result(work);
        Assert.Equal(CurrentAnswer, Data(result).GetProperty("answer").GetString());
        Assert.Equal(new[] { A, B }, f.Queries); Assert.All(f.SearchArguments, args => Assert.False(args.TryGetProperty("pageToken", out _)));
        Assert.Equal(3, f.NativeCalls); Assert.Equal(4, f.ModelCalls); Assert.Contains("validation_error", f.Prompts[1], StringComparison.Ordinal); f.AssertOwner();
    }

    [Fact, Trait("Phase", "CurrentScopeControl")]
    public async Task Observed_cursor_can_continue_its_identical_unchanged_canonical_query()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111", cursor: "old-cursor"));
        f.Searches.Enqueue(q => Page(q, "22222222")); f.Models.Enqueue(_ => Search(A, "old-cursor"));
        f.Models.Enqueue(_ => Read("22222222")); f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r3"], "matched", "r3"));
        await f.Resolve(); var result = await f.Result(f.Step());
        Assert.True(result.GetValueOrDefault("ok") is true); Assert.Equal(new[] { A, A }, f.Queries);
        Assert.Equal("old-cursor", f.SearchArguments[1].GetProperty("pageToken").GetString());
        Assert.Equal(3, f.NativeCalls); Assert.Equal(3, f.ModelCalls); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Reobserved_immutable_message_reuses_actual_prior_body_with_partial_and_no_duplicate_read()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "11111111"));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search(B)); f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r2", "r3"], "matched", "r2")); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept(Changed); f.ReleaseModel(2);
        var result = await f.Result(work); Assert.True(result.GetValueOrDefault("ok") is true);
        Assert.True(Partial(result)); Assert.Equal("r2", Data(result).GetProperty("targetReference").GetString());
        Assert.Equal(new[] { A, B }, f.Queries); Assert.Equal("11111111", Assert.Single(f.BodyIds));
        Assert.Equal(3, f.NativeCalls); Assert.Equal(4, f.ModelCalls);
        var context = await f.CompleteAndRecall(1); Assert.True(context.Partial); Assert.Equal("r2", context.TargetReference); f.AssertOwner();
    }

    [Fact]
    public async Task Retained_content_question_does_not_republish_earlier_scope_supporting_bodies_as_current()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "22222222"));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search(B)); f.Models.Enqueue(_ => Read("22222222"));
        f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r4", "r2"], "matched", "r4", partial: true)); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept(Changed); f.ReleaseModel(2);
        await f.Result(work); await f.CompleteAndRecall(1);
        f.Observe("What did she ask me for?");
        var followup = await f.Control("recall");
        var bodies = JsonSerializer.SerializeToElement(followup["nativeMessageContent"]);
        Assert.Equal("22222222", Assert.Single(bodies.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(4, f.NativeCalls); Assert.Equal(1, f.Resolves);
    }

    [Theory, Trait("Phase", "RetrievalReplacement")]
    [InlineData("wrong-day")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task Date_header_cannot_substitute_for_current_canonical_received_time(string receivedShape)
    {
        await using var f = new Fixture(day: true);
        f.Searches.Enqueue(q => Page(q, "11111111", f.Received(0), f.DateHeader(-1)));
        var bad = receivedShape == "missing" ? null : receivedShape == "malformed" ? "17917-not-an-epoch" : f.Received(0);
        f.Searches.Enqueue(q => Page(q, "11111111", bad, f.DateHeader(-1)));
        f.Searches.Enqueue(q => Page(q, "22222222", f.Received(-1), f.DateHeader(0)));
        f.BodyReceived["11111111"] = f.Received(0); f.BodyReceived["22222222"] = f.Received(-1);
        f.BodyDateHeaders["11111111"] = f.DateHeader(-1); f.BodyDateHeaders["22222222"] = f.DateHeader(0);
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search("professor")); f.Models.Enqueue(_ => Answer("HEADER_DATE_TARGET_SENTINEL", ["r2", "r3"], "matched", "r2"));
        f.Models.Enqueue(_ => Search("research")); f.Models.Enqueue(_ => Read("22222222"));
        f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r4", "r5"], "matched", "r5")); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept("Only yesterday's professor responses"); f.ReleaseModel(2);
        var result = await f.Result(work); Assert.Equal(CurrentAnswer, Data(result).GetProperty("answer").GetString());
        Assert.Equal("r5", Data(result).GetProperty("targetReference").GetString());
        Assert.DoesNotContain("HEADER_DATE_TARGET_SENTINEL", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(new[] { f.InitialQuery, f.DayQuery("professor", -1), f.DayQuery("research", -1) }, f.Queries);
        Assert.Equal(5, f.NativeCalls); Assert.Equal(7, f.ModelCalls); Assert.Contains("validation_error", f.Prompts[4], StringComparison.Ordinal); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Canonical_received_time_can_qualify_current_day_despite_a_different_sender_date_header()
    {
        await using var f = new Fixture(day: true); f.Searches.Enqueue(q => Page(q, "11111111", f.Received(0), f.DateHeader(-1)));
        f.Searches.Enqueue(q => Page(q, "22222222", f.Received(-1), f.DateHeader(0)));
        f.BodyReceived["11111111"] = f.Received(0); f.BodyReceived["22222222"] = f.Received(-1);
        f.BodyDateHeaders["11111111"] = f.DateHeader(-1); f.BodyDateHeaders["22222222"] = f.DateHeader(0);
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search("professor")); f.Models.Enqueue(_ => Read("22222222"));
        f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r4"], "matched", "r4")); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept("Only yesterday's professor responses"); f.ReleaseModel(2);
        var result = await f.Result(work); Assert.True(result.GetValueOrDefault("ok") is true);
        Assert.Equal("r4", Data(result).GetProperty("targetReference").GetString());
        Assert.Equal(new[] { f.InitialQuery, f.DayQuery("professor", -1) }, f.Queries); Assert.Equal(4, f.NativeCalls); Assert.Equal(5, f.ModelCalls); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Old_mutable_thread_is_partial_support_without_a_guessed_current_primary_target()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "11111111"));
        f.Models.Enqueue(_ => Read("11111111aaaa", thread: true)); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search(B)); f.Models.Enqueue(_ => Answer("OLD_THREAD_CURRENT_TARGET_SENTINEL", ["r2", "r3"], "matched", "r2"));
        f.Models.Enqueue(_ => Answer("The earlier thread is useful support, but current thread content was not refreshed.", ["r2", "r3"], "partial", partial: true)); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept(Changed); f.ReleaseModel(2);
        var result = await f.Result(work); Assert.True(result.GetValueOrDefault("ok") is true); Assert.True(Partial(result));
        Assert.Equal("", Data(result).GetProperty("targetReference").GetString()); Assert.Equal(1, f.BodyCalls);
        Assert.Equal("mail_thread", Assert.Single(f.BodyCapabilities)); Assert.Equal(3, f.NativeCalls); Assert.Equal(5, f.ModelCalls);
        Assert.DoesNotContain("OLD_THREAD_CURRENT_TARGET_SENTINEL", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var context = await f.CompleteAndRecall(1); Assert.True(context.Partial); Assert.Null(context.ExactResourceUrl()); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Last_spent_old_inference_cannot_reset_budget_or_publish_changed_scope_success()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Models.Enqueue(_ => Read("11111111"));
        for (var index = 2; index <= 8; index++) f.Models.Enqueue(_ => "{\"invalid_shape\":true}");
        f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2")); f.HoldModel(9);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(9); await f.Accept(Changed); f.ReleaseModel(9);
        var result = await f.Result(work); Assert.True(result.GetValueOrDefault("ok") is true); Assert.True(Partial(result));
        Assert.Equal(1, Data(result).GetProperty("goalRevision").GetInt32()); Assert.Equal("", Data(result).GetProperty("targetReference").GetString());
        Assert.DoesNotContain(OldAnswer, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(9, f.ModelCalls); Assert.Equal(2, f.NativeCalls); Assert.Single(f.Queries);
        var context = await f.CompleteAndRecall(1); Assert.True(context.Partial); Assert.Null(context.ExactResourceUrl()); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task A_B_A_discards_both_old_answers_without_resetting_budgets_or_guessing_a_target()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "22222222"));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search(B)); f.Models.Enqueue(_ => Answer("STALE_B_ANSWER_SENTINEL", ["r2"], "partial", partial: true));
        f.Models.Enqueue(_ => Answer("Current A scope has earlier supporting content; complete current selection was not established.", ["r2"], "partial", partial: true));
        f.HoldModel(2); f.HoldModel(4);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept(Changed); f.ReleaseModel(2);
        await f.WaitModel(4); await f.Accept("Actually, use exact query: " + A, expectedRevision: 1); f.ReleaseModel(4);
        var result = await f.Result(work); Assert.True(Partial(result)); Assert.Equal(2, Data(result).GetProperty("goalRevision").GetInt32());
        Assert.DoesNotContain(OldAnswer, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("STALE_B_ANSWER_SENTINEL", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(new[] { A, B }, f.Queries); Assert.Equal(5, f.ModelCalls); Assert.Equal(3, f.NativeCalls); Assert.Equal(1, f.BodyCalls);
        var context = await f.CompleteAndRecall(2); Assert.Equal(2, context.AcceptedReadGuidance.Length); Assert.True(context.Partial);
        Assert.Null(context.ExactResourceUrl()); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Old_native_search_proposal_is_discarded_before_dispatch_under_the_new_scope()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "22222222"));
        f.Models.Enqueue(_ => Search(A)); f.Models.Enqueue(_ => Search(B)); f.Models.Enqueue(_ => Read("22222222"));
        f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r3"], "matched", "r3")); f.HoldModel(1);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(1); await f.Accept(Changed); f.ReleaseModel(1);
        var result = await f.Result(work); Assert.Equal(CurrentAnswer, Data(result).GetProperty("answer").GetString());
        Assert.Equal(new[] { A, B }, f.Queries); Assert.Equal(3, f.NativeCalls); Assert.Equal(4, f.ModelCalls); f.AssertOwner();
    }

    [Fact, Trait("Phase", "RetrievalReplacement")]
    public async Task Stale_scope_guide_cannot_replace_the_latest_accepted_query_or_end_its_worker()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111")); f.Searches.Enqueue(q => Page(q, "22222222"));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2"));
        f.Models.Enqueue(_ => Search(B)); f.Models.Enqueue(_ => Read("22222222"));
        f.Models.Enqueue(_ => Answer(CurrentAnswer, ["r4"], "matched", "r4")); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept(Changed);
        f.Observe("Actually, use exact query: subject:\"Unaccepted C\"");
        var stale = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.TaskId, action = "guide", instruction = f.User, expected_goal_revision = 0 }), true, default);
        Assert.False(stale.GetValueOrDefault("ok") is true); Assert.Equal("guidance_stale", stale["state"]);
        Assert.Equal(1, stale["goal_revision"]); Assert.False(f.ReadToken.IsCancellationRequested); Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        f.ReleaseModel(2); var result = await f.Result(work);
        Assert.Equal(CurrentAnswer, Data(result).GetProperty("answer").GetString()); Assert.Equal(1, Data(result).GetProperty("goalRevision").GetInt32());
        Assert.Equal(new[] { A, B }, f.Queries); Assert.Equal(4, f.NativeCalls); Assert.Equal(5, f.ModelCalls);
        var context = await f.CompleteAndRecall(1); Assert.Equal(new[] { Changed }, context.AcceptedReadGuidance); f.AssertOwner();
    }

    [Theory, Trait("Phase", "RetrievalReplacement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_cancel_or_session_reset_after_replacement_fences_every_late_answer(bool reset)
    {
        await using var f = new Fixture(); f.Searches.Enqueue(q => Page(q, "11111111"));
        f.Models.Enqueue(_ => Read("11111111")); f.Models.Enqueue(_ => Answer(OldAnswer, ["r2"], "matched", "r2")); f.HoldModel(2);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(2); await f.Accept(Changed);
        Task cleanup = Task.CompletedTask;
        if (reset) cleanup = f.Controller.ResetAsync();
        else { f.Observe("Cancel that Gmail task"); Assert.True((await f.Control("cancel")).GetValueOrDefault("ok") is true); }
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner); Assert.True(f.ReadToken.IsCancellationRequested);
        f.ReleaseModel(2); var result = await work.WaitAsync(TimeSpan.FromSeconds(10)); await cleanup;
        Assert.False(result.GetValueOrDefault("ok") is true); Assert.DoesNotContain(OldAnswer, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(2, f.NativeCalls); Assert.Equal(2, f.ModelCalls); Assert.Single(f.Queries); Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact, Trait("Phase", "CurrentScopeControl")]
    public async Task Metadata_view_change_does_not_force_research_of_the_identical_empty_retrieval_query()
    {
        await using var f = new Fixture(); f.Searches.Enqueue(Empty);
        f.Models.Enqueue(_ => Answer("OLD_VIEW_ANSWER", [], "not_found"));
        f.Models.Enqueue(_ => Answer("No message matched the permitted header query.", [], "not_found")); f.HoldModel(1);
        await f.Resolve(); var work = f.Step(); await f.WaitModel(1); await f.Accept("Only headers; do not read message bodies."); f.ReleaseModel(1);
        var result = await f.Result(work); Assert.True(result.GetValueOrDefault("ok") is true);
        Assert.Equal("not_found", Data(result).GetProperty("goalStatus").GetString());
        Assert.DoesNotContain("OLD_VIEW_ANSWER", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(1, f.NativeCalls); Assert.Equal(0, f.BodyCalls); Assert.Equal(2, f.ModelCalls); Assert.Equal(A, Assert.Single(f.Queries)); f.AssertOwner();
    }

    private static JsonElement Data(Dictionary<string, object> result) => ((JsonElement)result["connector"]).GetProperty("result").GetProperty("data");
    private static bool Partial(Dictionary<string, object> result) => ((JsonElement)result["connector"]).GetProperty("result").GetProperty("truncated").GetBoolean();
    private static string Search(string query, string cursor = null) => Execute("mail_search", cursor is null ? new { query } : (object)new { query, pageToken = cursor });
    private static string Read(string id, bool thread = false) => Execute(thread ? "mail_thread" : "mail_read", new { id });
    private static string Execute(string capability, object arguments) => JsonSerializer.Serialize(new { action = "execute", capability,
        arguments_json = JsonSerializer.Serialize(arguments), answer = "", references = Array.Empty<string>(), partial = false });
    private static string Answer(string answer, string[] references, string status, string target = "", bool partial = false)
        => JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "", answer, references, partial,
            goal_status = status, target_reference = target });
    private static string CurrentBodyAnswer(string prompt, string id)
    {
        using var parsed = JsonDocument.Parse(prompt);
        var row = parsed.RootElement.GetProperty("evidence").EnumerateArray().Last(entry =>
            entry.TryGetProperty("capability", out var cap) && cap.GetString() == "mail_read"
            && entry.TryGetProperty("arguments", out var args) && args.TryGetProperty("id", out var value) && value.GetString() == id);
        var reference = row.GetProperty("reference").GetString();
        return Answer(CurrentAnswer, [reference], "matched", reference);
    }
    private static string SafeUnexpectedModelAnswer(string prompt)
    {
        using var parsed = JsonDocument.Parse(prompt);
        var row = parsed.RootElement.GetProperty("evidence").EnumerateArray().LastOrDefault(entry =>
            entry.TryGetProperty("reference", out _) && entry.TryGetProperty("evidenceKind", out var kind) && kind.GetString() == "content");
        var refs = row.ValueKind == JsonValueKind.Object ? new[] { row.GetProperty("reference").GetString() } : Array.Empty<string>();
        return Answer("Only partial earlier supporting evidence is available; no complete current selection was established.", refs, "partial", partial: true);
    }
    private static JsonElement Empty(string query) => Receipt(new { queryUsed = query, pageItemCount = 0, hasMore = false,
        messages = Array.Empty<object>(), sampledMessages = Array.Empty<object>(), metadataSample = new { requested = 0, returned = 0, complete = true } });
    private static JsonElement Page(string query, string id, string received = "1791763200000", string date = "Sender-controlled date", string cursor = null)
        => Receipt(new { queryUsed = query, pageItemCount = 1, hasMore = cursor is not null, nextPageToken = cursor,
            messages = new[] { new { id, threadId = id + "aaaa", internalDate = received, subject = "BioCyclic and Daisy research correspondence", date } },
            sampledMessages = new[] { new { id, threadId = id + "aaaa", internalDate = received,
                from = "Professor <reader@example.test>", subject = "BioCyclic and Daisy research correspondence", date } },
            metadataSample = new { requested = 1, returned = 1, complete = true } });
    private static JsonElement Receipt(object data) => JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
        result = new { source = "external_untrusted", truncated = false, data } });

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string TaskId = Guid.NewGuid().ToString("N"), AccountId = Guid.NewGuid().ToString("N"), Logical = Guid.NewGuid().ToString("N");
        internal readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-10-11T19:49:00Z", CultureInfo.InvariantCulture);
        internal readonly TimeZoneInfo Zone = TimeZoneInfo.Local;
        internal readonly string OriginalRequest, InitialQuery;
        internal string User, Identity = "origin";
        internal readonly CancellationTokenSource Session = new();
        internal readonly Queue<Func<string, JsonElement>> Searches = new();
        internal readonly Queue<Func<string, string>> Models = new();
        internal readonly Dictionary<string, string> BodyReceived = new();
        internal readonly Dictionary<string, string> BodyDateHeaders = new();
        internal readonly List<string> Queries = new(), BodyIds = new(), BodyCapabilities = new(), Prompts = new();
        internal readonly List<JsonElement> SearchArguments = new();
        internal readonly List<string> NativeTaskIds = new(), NativeLogicalIds = new(), ModelTaskIds = new(), ModelLogicalIds = new();
        internal readonly TaskCompletionSource<bool> SearchHeld = new(TaskCreationOptions.RunContinuationsAsynchronously), SearchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, TaskCompletionSource<bool>> _modelHeld = new(), _modelRelease = new();
        internal readonly JarvisConnectorController Controller;
        internal CancellationToken ReadToken;
        internal int NativeCalls, BodyCalls, ModelCalls, Resolves, Completes, HoldSearch;
        private int _observation;
        internal Fixture(bool day = false, string original = null)
        {
            OriginalRequest = User = original ?? (day ? "Check today's emails for a professor response." : Original);
            InitialQuery = day ? DayQuery("professor", 0) : A;
            Controller = new(Request, () => "fixture-owner", () => Session.Token, () => User,
                (notice, _) => { if (notice.CanPublish) Settled.TrySetResult(true); return Task.FromResult(true); },
                userRequestIdentity: () => Identity, utcNow: () => Clock, logicalLiveSessionId: () => Logical,
                reasonRead: Reason, userRequestSnapshot: () => new(User, Identity));
        }
        internal string DayQuery(string topic, int offset)
        {
            var day = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(Clock, Zone).Date.AddDays(offset), DateTimeKind.Unspecified);
            var after = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day, Zone)).ToUnixTimeSeconds();
            var before = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1), Zone)).ToUnixTimeSeconds();
            return "(" + topic + ") after:" + after + " before:" + before;
        }
        internal string Received(int offset)
        {
            var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(Clock, Zone).Date.AddDays(offset).AddHours(12), DateTimeKind.Unspecified);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }
        internal string DateHeader(int offset) => DateTimeOffset.FromUnixTimeMilliseconds(
            long.Parse(Received(offset), NumberStyles.None, CultureInfo.InvariantCulture)).ToString("r", CultureInfo.InvariantCulture);
        internal void HoldModel(int ordinal)
        { _modelHeld.Add(ordinal, new(TaskCreationOptions.RunContinuationsAsynchronously)); _modelRelease.Add(ordinal, new(TaskCreationOptions.RunContinuationsAsynchronously)); }
        internal Task WaitModel(int ordinal) => _modelHeld[ordinal].Task.WaitAsync(TimeSpan.FromSeconds(5));
        internal void ReleaseModel(int ordinal) => _modelRelease[ordinal].TrySetResult(true);
        internal void Observe(string text) { User = text; Identity = "observed-" + ++_observation; }
        internal async Task Accept(string instruction, int expectedRevision = 0)
        {
            Observe(instruction);
            var guide = await Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
                { task_id = TaskId, action = "guide", instruction, expected_goal_revision = expectedRevision }), true, default);
            // PHASE GATE: current J1 rejects literal/day replacement here. A
            // failure on this assertion is admission-only, not ledger coverage.
            Assert.True(guide.GetValueOrDefault("ok") is true);
            Assert.Equal(expectedRevision + 1, guide["goal_revision"]); Assert.Equal(1, Resolves);
            Assert.False(ReadToken.IsCancellationRequested); Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        }
        internal Task<Dictionary<string, object>> Resolve() => Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), true, default);
        internal Task<Dictionary<string, object>> Step() => Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = TaskId, capability_id = "mail_search", arguments_json = JsonSerializer.Serialize(new { query = InitialQuery }) }), true, default);
        internal Task<Dictionary<string, object>> Control(string action) => Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new { task_id = TaskId, action }), true, default);
        internal async Task<Dictionary<string, object>> Result(Task<Dictionary<string, object>> work)
        {
            var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
            if (result.GetValueOrDefault("started") is true)
            {
                await Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                result = await Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
                    { task_id = TaskId, action = "result", operation_id = result["operation_id"] }), true, default);
            }
            Assert.True(result.GetValueOrDefault("ok") is true); return result;
        }
        internal async Task<ConnectorSessionResult> CompleteAndRecall(int revision)
        {
            Assert.True((await Control("complete")).GetValueOrDefault("ok") is true);
            Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
            var recall = await Control("recall"); Assert.False((bool)recall["action_authority"]);
            var context = Assert.IsType<ConnectorSessionResult>(recall["context"]);
            Assert.Equal(revision, context.GoalRevision); Assert.Equal(TaskId, context.TaskId); Assert.Equal(AccountId, context.AccountId);
            Assert.Equal(OriginalRequest, context.Request); Assert.Equal(1, Completes); return context;
        }
        private async Task<string> Reason(string task, string logical, string instruction, string prompt, object schema, int limit, CancellationToken token)
        {
            Assert.Equal(TaskId, task); Assert.Equal(Logical, logical); Assert.Equal(2400, limit); Assert.True(ModelCalls < 24); // Fixture-only safety ceiling, not the product budget.
            ModelCalls++; ModelTaskIds.Add(task); ModelLogicalIds.Add(logical); Prompts.Add(prompt);
            using var parsed = JsonDocument.Parse(prompt); Assert.Equal(OriginalRequest, parsed.RootElement.GetProperty("request").GetString());
            // An extra source turn returns a truthful Partial from actually
            // supplied references, so product counter assertions expose a budget
            // regression rather than an unrelated synthetic queue exception.
            var response = Models.Count > 0 ? Models.Dequeue()(prompt) : SafeUnexpectedModelAnswer(prompt);
            if (_modelHeld.TryGetValue(ModelCalls, out var held)) { held.TrySetResult(true); await _modelRelease[ModelCalls].Task.WaitAsync(token); }
            return response;
        }
        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, method); token.ThrowIfCancellationRequested();
            if (path == "v1/connectors/resolve")
            {
                Resolves++; var payload = JsonSerializer.SerializeToElement(body); Assert.Equal(OriginalRequest, payload.GetProperty("userRequest").GetString());
                return JsonSerializer.SerializeToElement(new { taskId = TaskId, state = "ready", connectorId = "gmail", accountId = AccountId,
                    expiresAt = Clock.AddMinutes(10), choices = new[] { new { accountId = AccountId, label = "reader@example.test" } },
                    capabilities = new[] { Cap("mail_search", new[] { "query", "pageToken", "readMode", "matchMode", "limit", "metadataLimit" }),
                        Cap("mail_read", ["id"]), Cap("mail_thread", ["id"]) } });
            }
            if (path == "v1/connectors/execute")
            {
                Assert.True(NativeCalls < 8); NativeCalls++; ReadToken = token; var payload = JsonSerializer.SerializeToElement(body);
                Assert.Equal(TaskId, payload.GetProperty("taskId").GetString()); Assert.Equal(Logical, payload.GetProperty("logicalLiveSessionId").GetString());
                Assert.False(payload.GetProperty("confirmed").GetBoolean()); NativeTaskIds.Add(TaskId); NativeLogicalIds.Add(Logical);
                var cap = payload.GetProperty("capabilityId").GetString(); var args = payload.GetProperty("arguments");
                if (cap == "mail_search")
                {
                    Assert.True(Queries.Count < 4); var query = args.GetProperty("query").GetString(); Queries.Add(query); SearchArguments.Add(args.Clone());
                    // Unexpected admission still gets an inert normalized page;
                    // assert the actual wire trace outside this callback instead
                    // of hiding a cursor failure behind synthetic queue exhaustion.
                    var receipt = Searches.Count > 0 ? Searches.Dequeue()(query) : Page(query, "22222222");
                    if (HoldSearch == Queries.Count) { SearchHeld.TrySetResult(true); await SearchRelease.Task.WaitAsync(token); }
                    return receipt;
                }
                Assert.True(cap is "mail_read" or "mail_thread"); BodyCalls++; BodyCapabilities.Add(cap); var id = args.GetProperty("id").GetString(); BodyIds.Add(id);
                var messageId = cap == "mail_thread" ? id[..^4] : id;
                var message = new { id = messageId, threadId = messageId + "aaaa", internalDate = BodyReceived.GetValueOrDefault(messageId, "1791763200000"),
                    contentTruncated = false, headers = new[] { new { name = "Subject", value = "BioCyclic and Daisy research correspondence" },
                        new { name = "Date", value = BodyDateHeaders.GetValueOrDefault(messageId, "Mon, 12 Oct 2026 12:00:00 +0530") } }, text = "Prize Alpha was awarded for the observed research correspondence." };
                return cap == "mail_read" ? Receipt(message) : Receipt(new { id, messages = new[] { message }, contentTruncated = false });
            }
            if (path == "v1/connectors/tasks/" + TaskId + "/complete") { Completes++; return JsonSerializer.SerializeToElement(new { state = "completed", verified = true }); }
            Assert.Equal("v1/connectors/tasks/" + TaskId + "/cancel", path); return JsonSerializer.SerializeToElement(new { state = "cancelled", verified = true });
        }
        private static object Cap(string id, string[] fields) => new { id, writes = false, description = "Synthetic owned read", activity = "Reading Gmail…",
            arguments = fields.ToDictionary(field => field, _ => "bounded synthetic argument", StringComparer.Ordinal) };
        internal void AssertOwner()
        {
            Assert.Equal(1, Resolves); Assert.All(NativeTaskIds, value => Assert.Equal(TaskId, value)); Assert.All(ModelTaskIds, value => Assert.Equal(TaskId, value));
            Assert.All(NativeLogicalIds, value => Assert.Equal(Logical, value)); Assert.All(ModelLogicalIds, value => Assert.Equal(Logical, value));
            Assert.InRange(ModelCalls, 0, 9); Assert.InRange(NativeCalls, 0, 8); Assert.InRange(Queries.Count, 0, 4);
        }
        public async ValueTask DisposeAsync()
        { SearchRelease.TrySetResult(true); foreach (var release in _modelRelease.Values) release.TrySetResult(true); await Controller.ResetAsync(); Session.Cancel(); Session.Dispose(); }
    }
}
