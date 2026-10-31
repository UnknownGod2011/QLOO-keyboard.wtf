namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorRetainedOpenSelectionTests
{
    private static JsonElement Receipt(object data, bool complete = true, string state = "completed", bool verified = true)
        => JsonSerializer.SerializeToElement(new { state, verified, result = new { data, truncated = !complete } });
    private static object Message(string id, string thread, string subject = "Observed conversation") => new
    {
        id, threadId = thread, headers = new[] { new { name = "Subject", value = subject },
            new { name = "From", value = "person@example.test" }, new { name = "To", value = "reader@example.test" },
            new { name = "Date", value = "Tue, 13 Oct 2026 09:00:00 +0530" } }
    };
    private static ConnectorSessionResult Search(object[] messages, bool allowExact = true, bool complete = true,
        bool hasMore = false, string label = "reader@example.test", string state = "completed", bool verified = true)
        => ConnectorSessionResult.Create("observed-task", "gmail", "observed-account", label, "Find relevant conversations",
            Receipt(new { answer = "Observed candidates; no chosen target." }),
            [Receipt(new { messages, pageItemCount = messages.Length, hasMore }, complete, state, verified)],
            rankedPrimaryEvidence: false, evidenceCapabilities: ["mail_search"], allowExactResourceSelection: allowExact);
    private static ConnectorSessionResult Pair(bool allowExact = true) => Search([
        Message("11111111", "aaaaaaaa", "Conversation one"), Message("22222222", "bbbbbbbb", "Conversation two")], allowExact);

    [Fact]
    public void Explicit_single_selection_of_a_returned_page_is_not_an_automatic_or_exhaustive_selection()
    {
        var context = Search([Message("11111111", "aaaaaaaa")], hasMore: true);
        Assert.Null(context.ExactResourceUrl());
        Assert.True(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out var targets, out _));
        Assert.Equal("aaaaaaaa", Assert.Single(targets).Id);
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.All, out _, out _));
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.Both, out _, out _));
    }

    [Theory]
    [InlineData("{\"hasMore\":true,\"pageItemCount\":2}")]
    [InlineData("{\"hasMore\":true}")]
    [InlineData("{\"hasMore\":\"unknown\",\"pageItemCount\":1}")]
    [InlineData("{\"hasMore\":true,\"pageItemCount\":1,\"nextPageToken\":{}}")]
    public void Missing_inconsistent_or_malformed_paging_cannot_authorize_the_single_page_exception(string paging)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(paging);
        data["messages"] = new[] { Message("11111111", "aaaaaaaa") };
        var context = ConnectorSessionResult.Create("task", "gmail", "account", "reader@example.test", "Find email",
            Receipt(new { answer = "Candidate" }), [Receipt(data)], rankedPrimaryEvidence: false, evidenceCapabilities: ["mail_search"]);
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out _, out _));
    }

    [Theory]
    [InlineData(false, true, "reader@example.test", false)]
    [InlineData(true, false, "reader@example.test", false)]
    [InlineData(true, true, "Unidentified account", false)]
    [InlineData(true, true, "reader@example.test", true)]
    public void Explicit_single_paged_open_preserves_evidence_account_and_goal_guards(
        bool complete, bool verified, string label, bool staleGoal)
    {
        var context = Search([Message("11111111", "aaaaaaaa")], complete: complete,
            hasMore: true, label: label, verified: verified);
        if (staleGoal) context = context with { GoalRevision = 1, EvidenceGoalRevisions = [0] };

        Assert.Null(context.ExactResourceUrl());
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out var targets, out var rejection));
        Assert.Empty(targets);
        Assert.NotNull(rejection);
    }

    [Fact]
    public void Explicit_single_open_can_inspect_one_complete_candidate_after_reasoning_failure()
    {
        var context = Search([Message("11111111", "aaaaaaaa")], allowExact: false) with { GoalStatus = "partial" };
        Assert.Null(context.ExactResourceUrl()); // Failure did not choose a semantic match.
        Assert.True(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out var targets, out _));
        Assert.Equal("aaaaaaaa", Assert.Single(targets).Id);
        Assert.False((context with { GoalStatus = "not_found" }).TryResolveExactOpen(ConnectorOpenSelection.Single, out _, out _));
        Assert.False((context with { GoalRevision = 1, EvidenceGoalRevisions = [0] }).TryResolveExactOpen(ConnectorOpenSelection.Single, out _, out _));
        Assert.False((Search([Message("11111111", "aaaaaaaa")], allowExact: false, complete: false) with { GoalStatus = "partial" })
            .TryResolveExactOpen(ConnectorOpenSelection.Single, out _, out _));
    }

    private static ConnectorSessionResult BoundedPartialConversation(int differentThreadReceipt = 0, bool additionalReceipt = false)
    {
        // The worker accepted eight native receipts: one candidate search,
        // one thread, and six message reads. Body coverage is partial, while
        // the observed candidate identities all remain available for selection.
        var initial = Enumerable.Range(1, 3).Select(index => Message(index.ToString("x8"), "aaaaaaaa")).ToArray();
        var evidence = new List<JsonElement>
        {
            Receipt(new { messages = initial, pageItemCount = initial.Length, hasMore = false }),
            Receipt(new { id = "aaaaaaaa", messages = initial }, complete: false)
        };
        for (var receipt = 3; receipt <= 8; receipt++)
            evidence.Add(Receipt(Message((receipt + 1).ToString("x8"), receipt == differentThreadReceipt ? "bbbbbbbb" : "aaaaaaaa")));
        if (additionalReceipt) evidence.Add(Receipt(Message("0000000a", "aaaaaaaa")));
        return ConnectorSessionResult.Create("task", "gmail", "account", "reader@example.test", "Find the conversation",
            Receipt(new { answer = "Native results retained after reasoning failed." }, complete: false), evidence,
            rankedPrimaryEvidence: false, evidenceCapabilities: ["mail_search", "mail_thread", "mail_read", "mail_read", "mail_read", "mail_read", "mail_read", "mail_read", "mail_read"],
            allowExactResourceSelection: false, goalStatus: "partial");
    }

    [Fact]
    public void Eight_accepted_receipts_preserve_one_complete_candidate_for_explicit_partial_open()
    {
        var context = BoundedPartialConversation();
        Assert.True(context.Partial);
        Assert.False(context.ExactResourceSelectionAllowed);
        Assert.Null(context.ExactResourceUrl()); // No semantic match was asserted by failed synthesis.
        Assert.Equal(10, context.Resources.Length);
        Assert.True(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out var targets, out var rejection));
        Assert.Null(rejection);
        Assert.Equal("aaaaaaaa", Assert.Single(targets).Id);
        Assert.EndsWith("#all/aaaaaaaa", targets[0].Url);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void A_different_thread_in_either_last_accepted_receipt_keeps_single_open_ambiguous(int receipt)
    {
        var context = BoundedPartialConversation(differentThreadReceipt: receipt);
        Assert.Equal(new[] { "aaaaaaaa", "bbbbbbbb" }, context.MailThreadReferences().Select(thread => thread.ThreadId));
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out var targets, out _));
        Assert.Empty(targets);
    }

    [Fact]
    public void More_than_eight_receipts_remain_omitted_and_cannot_authorize_complete_candidate_opening()
    {
        var context = BoundedPartialConversation(additionalReceipt: true);
        Assert.Equal(10, context.Resources.Length);
        Assert.DoesNotContain(context.Resources, item => item.GetProperty("id").GetString() == "0000000a");
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out _, out _));
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.All, out _, out _));
    }

    [Fact]
    public void Later_identity_only_receipt_preserves_previously_verified_headers_for_the_same_message()
    {
        var context = ConnectorSessionResult.Create("task", "gmail", "account", "reader@example.test", "Find email",
            Receipt(new { answer = "Partial" }), [
                Receipt(Message("aaaaaaaa", "aaaaaaaa", "Observed subject")),
                Receipt(new { id = "aaaaaaaa", messages = Array.Empty<object>() })],
            evidenceCapabilities: ["mail_read", "mail_thread"]);
        var thread = Assert.Single(context.MailThreadReferences());
        Assert.Equal("Observed subject", thread.Subject);
        Assert.Contains("person@example.test", thread.Participants);
    }

    [Theory]
    [InlineData("Open that thread in a new tab.", ConnectorOpenSelection.Single)]
    [InlineData("Open the email in a new tab.", ConnectorOpenSelection.Single)]
    [InlineData("can you open that in a new tab ?", ConnectorOpenSelection.Single)]
    [InlineData("Can you open that in a new tab?", ConnectorOpenSelection.Single)]
    [InlineData("Please open this in a new tab.", ConnectorOpenSelection.Single)]
    [InlineData("Open it on the new thread.", ConnectorOpenSelection.Single)]
    [InlineData("Open that thread on a new tab.", ConnectorOpenSelection.Single)]
    [InlineData("Could you please open that thread in a new tab?", ConnectorOpenSelection.Single)]
    [InlineData("Open thread in a new tab.", ConnectorOpenSelection.Single)]
    [InlineData("Pull it up.", ConnectorOpenSelection.Single)]
    [InlineData("Show me that conversation.", ConnectorOpenSelection.Single)]
    [InlineData("Open both separately in new tabs.", ConnectorOpenSelection.Both)]
    [InlineData("Please open both in separate tabs.", ConnectorOpenSelection.Both)]
    [InlineData("Open both conversations in separate new tabs.", ConnectorOpenSelection.Both)]
    [InlineData("Pull both up.", ConnectorOpenSelection.Both)]
    [InlineData("Could you open both of them?", ConnectorOpenSelection.Both)]
    [InlineData("Open those conversations in new tabs.", ConnectorOpenSelection.All)]
    [InlineData("Open all the threads you found.", ConnectorOpenSelection.All)]
    [InlineData("Open them separately.", ConnectorOpenSelection.All)]
    [InlineData("Pull all of them up in new windows.", ConnectorOpenSelection.All)]
    public void Presentation_modifiers_preserve_the_retained_object_selection(string request, object expected)
    {
        Assert.True(ConnectorFollowupIntent.TryParseExactOpen(request, out var actual));
        Assert.Equal((ConnectorOpenSelection)expected, actual);
        Assert.True(ConnectorFollowupIntent.IsExactOpen(request));
    }

    [Theory]
    [InlineData("Open Gmail.")]
    [InlineData("Open Gmail and Instagram.")]
    [InlineData("Open Gmail, Instagram, Discord and Devpost.")]
    [InlineData("Open Discord.")]
    [InlineData("Open a new tab.")]
    [InlineData("Open a new thread.")]
    [InlineData("Create a new email thread.")]
    [InlineData("Open both Gmail and Instagram.")]
    [InlineData("Find all emails.")]
    [InlineData("Open that thread and send a reply.")]
    [InlineData("Open that and send a reply.")]
    [InlineData("On this webpage, open that in a new tab.")]
    [InlineData("The email said open both separately in new tabs.")]
    public void Ordinary_navigation_or_a_different_action_does_not_claim_retained_opening(string request)
        => Assert.False(ConnectorFollowupIntent.TryParseExactOpen(request, out _));

    [Theory]
    [InlineData("Open those threads.", "gmail", true)]
    [InlineData("Open those threads.", "github", false)]
    [InlineData("Open both repositories.", "github", true)]
    [InlineData("Open both repositories.", "gmail", false)]
    [InlineData("Open those documents.", "googledocs", true)]
    [InlineData("Open those events.", "gmail", false)]
    public void Plural_object_words_preserve_provider_authority(string request, string provider, bool expected)
        => Assert.Equal(expected, ConnectorFollowupIntent.ObjectMatchesProvider(request, provider));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_opens_each_observed_thread_once_without_guessing_a_primary_even_after_reasoning_failure(bool allowExact)
    {
        var context = Pair(allowExact);
        Assert.Null(context.ExactMailUrl());
        Assert.True(context.TryResolveExactOpen(ConnectorOpenSelection.Both, out var targets, out var rejection));
        Assert.Null(rejection);
        Assert.Equal(new[] { "aaaaaaaa", "bbbbbbbb" }, targets.Select(value => value.Id));
        Assert.Equal(new[] { "Conversation one", "Conversation two" }, targets.Select(value => value.Title));
        Assert.All(targets, value => Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/" + value.Id, value.Url));
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out _, out _));
    }

    [Fact]
    public void Several_messages_in_two_threads_are_two_open_targets()
    {
        var context = Search([Message("11111111", "aaaaaaaa"), Message("22222222", "bbbbbbbb"), Message("33333333", "aaaaaaaa")]);
        Assert.True(context.TryResolveExactOpen(ConnectorOpenSelection.Both, out var targets, out _));
        Assert.Equal(2, targets.Length);
        var first = context.MailThreadReferences()[0];
        Assert.Equal(new[] { "11111111", "33333333" }, first.MessageIds);
        Assert.Equal("observed-account", first.AccountId);
        Assert.Equal("reader@example.test", first.AccountLabel);
        Assert.Contains("person@example.test", first.Participants);
        Assert.False(first.BodyContentObserved);
    }

    [Fact]
    public void Both_requires_two_distinct_threads_and_all_uses_the_complete_retained_set()
    {
        var one = Search([Message("11111111", "aaaaaaaa")]);
        var three = Search([Message("11111111", "aaaaaaaa"), Message("22222222", "bbbbbbbb"), Message("33333333", "cccccccc")]);
        Assert.False(one.TryResolveExactOpen(ConnectorOpenSelection.Both, out _, out _));
        Assert.False(three.TryResolveExactOpen(ConnectorOpenSelection.Both, out _, out _));
        Assert.True(three.TryResolveExactOpen(ConnectorOpenSelection.All, out var targets, out _));
        Assert.Equal(3, targets.Length);
    }

    [Theory]
    [InlineData("CtWLlTTscGXjJqpTGnBlql")]
    [InlineData("FMfcgzQhWTkHtjtXDRpRQCqPFNQNvWxH")]
    [InlineData("<message@example.test>")]
    [InlineData("../aaaaaaaa")]
    public void Opaque_ui_provider_or_RFC_ids_never_become_native_thread_routes(string invalid)
    {
        var context = Search([Message("11111111", "aaaaaaaa"), Message("22222222", invalid)]);
        Assert.Single(context.Resources);
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.All, out var targets, out _));
        Assert.Empty(targets);
        Assert.DoesNotContain(invalid, JsonSerializer.Serialize(context.MailThreadReferences()));
    }

    [Theory]
    [InlineData(false, false, "reader@example.test", "completed", true)]
    [InlineData(true, true, "reader@example.test", "completed", true)]
    [InlineData(true, false, "Unidentified account", "completed", true)]
    [InlineData(true, false, "reader@example.test", "failed", true)]
    [InlineData(true, false, "reader@example.test", "completed", false)]
    public void Incomplete_or_unverified_candidates_and_missing_account_cannot_open_as_a_complete_set(
        bool complete, bool hasMore, string label, string state, bool verified)
    {
        var context = Search([Message("11111111", "aaaaaaaa"), Message("22222222", "bbbbbbbb")],
            complete: complete, hasMore: hasMore, label: label, state: state, verified: verified);
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.Both, out var targets, out var rejection));
        Assert.Empty(targets);
        Assert.NotNull(rejection);
    }

    [Fact]
    public void Omitted_candidates_do_not_silently_become_all()
    {
        var messages = Enumerable.Range(1, 13).Select(index => Message(index.ToString("x8"), (index + 100).ToString("x8"))).ToArray();
        var context = Search(messages);
        Assert.Equal(12, context.MailThreadReferences().Length);
        Assert.False(context.TryResolveExactOpen(ConnectorOpenSelection.All, out _, out _));
    }

    [Fact]
    public void Thread_metadata_and_body_read_state_survive_without_retaining_body_text()
    {
        var context = ConnectorSessionResult.Create("task", "gmail", "account", "reader@example.test", "Read this thread",
            Receipt(new { answer = "Read result" }), [Receipt(new { id = "aaaaaaaa", messages = new[] {
                new { id = "11111111", threadId = "aaaaaaaa", text = "PRIVATE BODY not retained", headers = new[] {
                    new { name = "Subject", value = "Actual thread subject" } } } } })], evidenceCapabilities: ["mail_thread"]);
        var thread = Assert.Single(context.MailThreadReferences());
        Assert.Equal(new[] { "11111111" }, thread.MessageIds);
        Assert.True(thread.BodyContentObserved);
        Assert.Equal("Actual thread subject", thread.Subject);
        Assert.DoesNotContain("PRIVATE BODY", JsonSerializer.Serialize(context));
        Assert.True(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out var targets, out _));
        Assert.EndsWith("#all/aaaaaaaa", Assert.Single(targets).Url);
    }

    [Fact]
    public void Single_other_provider_reference_keeps_the_existing_exact_link()
    {
        var context = ConnectorSessionResult.Create("task", "github", "account", "Account", "Find repo",
            Receipt(new { answer = "Found" }), [Receipt(new { full_name = "owner/repo", name = "repo", html_url = "https://github.com/owner/repo" })],
            evidenceCapabilities: ["github_repo"]);
        Assert.True(context.TryResolveExactOpen(ConnectorOpenSelection.Single, out var targets, out _));
        Assert.Equal("https://github.com/owner/repo", Assert.Single(targets).Url);
    }
}
