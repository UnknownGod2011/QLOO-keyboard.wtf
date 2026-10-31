using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

namespace KeyboardWtf.Tests;

// The physical 15 October trace proposed browser_action + connector_execute
// for the follow-up. Exercise those choices, not a prescribed open_url call.
[Collection("Live event integration")]
public sealed class ConnectorRetainedProposalChoiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly string _task = Guid.NewGuid().ToString("N"), _account = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _session = new();
    private readonly CommandRegistry _commands;
    private readonly JarvisConnectorController _controller;
    private readonly List<string> _urls = [], _requests = [];
    private readonly TaskCompletionSource<string> _firstLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _owners;
    private bool _sameSenderPair;
    private bool _moreSearchResults;

    public ConnectorRetainedProposalChoiceTests()
    {
        var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var automation = new JarvisAutomationService(null, settings, new(Path.Combine(_directory, "history.json")),
            new LearnedMappingService(Path.Combine(_directory, "mappings.json")), () => { }, null, null, null,
            simpleUrlLauncher: (url, _) => { _urls.Add(url); _firstLaunch.TrySetResult(url); return (true, "Opened.", "fixture"); });
        _commands = new(null, null, null, settings, new NotificationService(null), automation: automation);
        _controller = new(Request, () => { _owners++; return "fixture-owner"; }, () => _session.Token,
            () => _commands.ConnectorUserRequest, (_, _) => Task.FromResult(true),
            userRequestIdentity: () => _commands.ConnectorUserRequestIdentity,
            userRequestSnapshot: () => _commands.ConnectorUserRequestSnapshot);
        automation.Connectors = _controller;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await _controller.ResetAsync(); _session.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
    {
        _requests.Add(path);
        if (path.EndsWith("/resolve")) return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            taskId = _task, state = "ready", connectorId = "gmail", accountId = _account,
            choices = new[] { new { accountId = _account, label = "reader@example.test" } }, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching Gmail…",
                arguments = new Dictionary<string, string> { ["query"] = "Search terms", ["matchMode"] = "exact or topic" } },
                new { id = "mail_thread", writes = false, activity = "Reading thread…", arguments = new Dictionary<string,string> { ["id"] = "thread id" } } }
        }));
        if (path.EndsWith("/execute") && JsonSerializer.SerializeToElement(body).GetProperty("capabilityId").GetString() == "mail_thread")
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = false, data = new { id = "aaaaaaaa", messages = new[] {
                    new { id = "11111111", threadId = "aaaaaaaa", text = "Please send the project summary.", contentTruncated = false } } } } }));
        if (path.EndsWith("/execute")) return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            state = "completed", verified = true, result = new { truncated = false, data = new
            {
                messages = _sameSenderPair
                    ? new[] { new { id = "11111111", threadId = "aaaaaaaa", subject = "Fixture conversation", from = "Nanda <nanda@example.test>" },
                        new { id = "22222222", threadId = "bbbbbbbb", subject = "Another discussion", from = "Nanda <nanda@example.test>" } }
                    : new[] { new { id = "11111111", threadId = "aaaaaaaa", subject = "Fixture conversation", from = "Nanda <nanda@example.test>" } },
                hasMore = _moreSearchResults, pageItemCount = _sameSenderPair ? 2 : 1
            } }
        }));
        return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
    }

    private Task<Dictionary<string, object>> Tool(string name, object arguments) =>
        _commands.ExecuteJarvisToolAsync(name, JsonSerializer.SerializeToElement(arguments), default);

    private async Task RetainConversation(bool readBody = false)
    {
        _commands.ObserveJarvisTranscript("Find the requested email.", default);
        Assert.Equal(true, (await Tool("connector_resolve", new { connector_id = "gmail" }))["ok"]);
        Assert.Equal(true, (await Tool("connector_execute", new { task_id = _task, capability_id = "mail_search",
            arguments_json = "{\"query\":\"fixture\",\"matchMode\":\"exact\"}" }))["ok"]);
        if (readBody) Assert.Equal(true, (await Tool("connector_execute", new { task_id = _task, capability_id = "mail_thread",
            arguments_json = "{\"id\":\"aaaaaaaa\"}" }))["ok"]);
        _commands.CompleteJarvisUserTurn();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Open that thread in a new tab.")]
    [InlineData("Open it on the new thread.")]
    [InlineData("can you open that in a new tab ?")]
    [InlineData("Please open this in a new tab.")]
    public async Task Finalized_exact_open_runs_without_waiting_for_a_model_proposal_and_later_proposals_share_its_receipt(string request)
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript(request, default);
        var before = _requests.Count;
        var accepted = _commands.ReserveFinalConnectorRead(request, default);
        Assert.NotNull(accepted); accepted();
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa",
            await _firstLaunch.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(_controller.HoldsPendingReply(_commands.ConnectorUserRequestSnapshot));
        var result = await Tool("connector_execute", new { task_id = _task, capability_id = "mail_search",
            arguments_json = "{\"query\":\"model did not propose opening\"}" });
        Assert.Equal("open_retained_object", result["actual_operation"]);
        Assert.True(result.GetValueOrDefault("retained_object_open") is true);
        Assert.Equal(1, result["opened_count"]);
        await Tool("open_url", new { url = "https://mail.google.com/mail/u/0/#thread/%23thread-f%3A1876108190529080085" });
        Assert.Single(_urls); Assert.Equal(before, _requests.Count);
        Assert.False(_controller.HoldsPendingReply(_commands.ConnectorUserRequestSnapshot));
    }

    [Theory]
    [InlineData("Can you open that in a new tab?")]
    [InlineData("Could you open that email thread in a new tab?")]
    public async Task Explicit_open_of_the_one_returned_conversation_does_not_require_an_exhaustive_mailbox_search(string request)
    {
        // Real native searches may request one result and return hasMore=true.
        // Opening this observed conversation is not opening every search match.
        _moreSearchResults = true;
        await RetainConversation();
        _commands.ObserveJarvisTranscript(request, default);
        var before = _requests.Count;
        var accepted = _commands.ReserveFinalConnectorRead(request, default);
        Assert.NotNull(accepted);
        accepted();
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa",
            await _firstLaunch.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var receipt = await Tool("connector_control", new { action = "result", task_id = _task });
        Assert.True(receipt.GetValueOrDefault("retained_object_open") is true);
        Assert.Single(_urls);
        Assert.Equal(before, _requests.Count);
        Assert.False(_controller.HoldsPendingReply(_commands.ConnectorUserRequestSnapshot));
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Can you open that in a new tab?", true)]
    [InlineData("Open all the threads you found.", false)]
    public async Task Paged_search_does_not_choose_between_two_returned_threads_or_open_an_unseen_complete_set(string request, bool pair)
    {
        _moreSearchResults = true;
        _sameSenderPair = pair;
        await RetainConversation();
        _commands.ObserveJarvisTranscript(request, default);
        Assert.Null(_commands.ReserveFinalConnectorRead(request, default));
        Assert.Empty(_urls);
    }

    [Theory]
    [InlineData("Open Gmail.")]
    [InlineData("Open Gmail, Instagram, Discord and Devpost.")]
    [InlineData("Open that email and send a reply.")]
    public async Task Finalized_ordinary_or_compound_request_never_reserves_a_retained_open(string request)
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript(request, default);
        var before = (_requests.Count, _owners);
        Assert.Null(_commands.ReserveFinalConnectorRead(request, default));
        Assert.Equal(before, (_requests.Count, _owners)); Assert.Empty(_urls);
    }

    [Theory]
    [InlineData("Open it on the new thread.")]
    [InlineData("Open thread in a new tab.")]
    [InlineData("Could you please open that thread in a new tab?")]
    [InlineData("Open that thread in a new tab please.")]
    [InlineData("Open it on a new tab for me please.")]
    [InlineData("Open the exact email thread in a new tab.")]
    [InlineData("Please open the actual conversation.")]
    [InlineData("Open that matching thread in a new tab.")]
    [InlineData("Open Nanda's email in a new tab.")]
    [InlineData("Open the email from Nanda in a new tab.")]
    [InlineData("Open the email about Fixture conversation in a new tab.")]
    public async Task Reported_browser_thread_token_cannot_replace_retained_native_object(string human)
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript(human, default);
        var before = _requests.Count;
        var result = await Tool("open_url", new { url = "https://mail.google.com/mail/u/0/#thread/%23thread-f%3A1876108190529080085" });
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True(result.GetValueOrDefault("retained_object_open") is true);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", Assert.Single(_urls));
        Assert.DoesNotContain("#thread/", JsonSerializer.Serialize(result));
        Assert.Equal(before, _requests.Count);
    }

    [Theory]
    [InlineData("Open the email from Lea in a new tab.", false)]
    [InlineData("Pull Lea's email up.", false)]
    [InlineData("Open Lea's email separately.", false)]
    [InlineData("Open the email from Lea in the browser.", false)]
    [InlineData("Open Nanda's thread in a new tab.", true)]
    public async Task Named_mail_mismatch_or_two_matching_threads_never_dispatches_a_model_destination(string human, bool ambiguous)
    {
        _sameSenderPair = ambiguous;
        await RetainConversation();
        _commands.ObserveJarvisTranscript(human, default);
        var before = _requests.Count;
        var result = await Tool("open_url", new { url = "https://mail.google.com/mail/u/0/#thread/1a0d14c9b7886d69" });
        Assert.True(result.GetValueOrDefault("ok") is false, JsonSerializer.Serialize(result));
        Assert.Empty(_urls);
        Assert.Equal(before, _requests.Count);
    }

    [Fact]
    public async Task Retained_open_accepts_browser_new_tab_then_stale_connector_proposal_without_two_actions()
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript("Open that thread in a new tab.", default);
        var before = _requests.Count;
        var first = await Tool("browser_action", new { action = "new tab" });
        var later = await Tool("connector_execute", new { task_id = _task, capability_id = "mail_search",
            arguments_json = "{\"query\":\"stale-model-query\"}" });
        Assert.Equal(true, first["ok"]);
        Assert.Equal(true, later["ok"]);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", Assert.Single(_urls));
        Assert.Equal(before, _requests.Count);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Open that thread in a new tab.")]
    [InlineData("can you open that in a new tab ?")]
    [InlineData("Can you open that in a new tab?")]
    [InlineData("Please open this in a new tab.")]
    public async Task Retained_open_owns_a_browser_automation_proposal_without_starting_browser_work(string request)
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript(request, default);
        var before = _requests.Count;
        // No Browser Automation coordinator is configured. The verified
        // retained URL is sufficient, regardless of this model's proposal.
        var first = await Tool("browser_automation", new
        {
            task = "Open https://unrelated.example.test in a new tab."
        });
        var repeated = await Tool("open_url", new { url = "https://mail.google.com/" });
        var repeatedBrowser = await Tool("browser_automation", new { task = "Open the email." });

        Assert.Equal(true, first["ok"]);
        Assert.Equal(true, first["retained_object_open"]);
        Assert.Equal(1, first["opened_count"]);
        Assert.False(first.ContainsKey("started"));
        Assert.Equal(true, repeated["ok"]);
        Assert.Equal(true, repeatedBrowser["ok"]);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", Assert.Single(_urls));
        Assert.Equal(before, _requests.Count);
        Assert.DoesNotContain("unrelated.example.test", JsonSerializer.Serialize(first));
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Bare_reference_without_retained_context_cannot_invent_an_opening()
    {
        const string request = "Can you open that in a new tab?";
        _commands.ObserveJarvisTranscript(request, default);
        Assert.Null(_commands.ReserveFinalConnectorRead(request, default));
        var result = await Tool("browser_automation", new { task = request });
        Assert.False(result.ContainsKey("retained_object_open"));
        Assert.Empty(_urls);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("Open Gmail.")]
    [InlineData("Open a new tab.")]
    [InlineData("On this webpage, click the Sign in button.")]
    public async Task Ordinary_browser_automation_proposal_keeps_its_existing_owner_without_connector_work(string request)
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript(request, default);
        var before = (_requests.Count, _owners);
        var result = await Tool("browser_automation", new { task = request });

        // The existing BA owner remains unavailable in this fixture; retained
        // connector objects must neither replace it nor start connector work.
        Assert.Equal(false, result["ok"]);
        Assert.Equal("Browser Automation is unavailable.", result["message"]);
        Assert.False(result.ContainsKey("retained_object_open"));
        Assert.Equal(before, (_requests.Count, _owners));
        Assert.Empty(_urls);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Retained_open_accepts_stale_connector_read_as_first_model_proposal()
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript("Open that email in a new tab.", default);
        var before = _requests.Count;
        var result = await Tool("connector_execute", new { task_id = _task, capability_id = "mail_search",
            arguments_json = "{\"query\":\"stale-model-query\"}" });
        Assert.Equal(true, result["ok"]);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", Assert.Single(_urls));
        Assert.Equal(before, _requests.Count);
    }

    [Fact]
    public async Task Retained_question_uses_connector_evidence_before_model_proposed_desktop_context()
    {
        await RetainConversation(readBody: true);
        _commands.ObserveJarvisTranscript("What did she ask me for?", default);
        var before = _requests.Count;
        var result = await Tool("get_desktop_context", new { });
        Assert.Equal(true, result["ok"]);
        Assert.Equal("retained_context", result["state"]);
        Assert.Contains("Please send the project summary.", JsonSerializer.Serialize(result["nativeMessageContent"]));
        Assert.DoesNotContain("clipboard_text", result.Keys);
        Assert.Equal(before, _requests.Count);
        Assert.Empty(_urls);
    }

    [Theory]
    [InlineData("What is on my screen?")]
    [InlineData("Give me details about the selected text.")]
    [InlineData("Open Gmail.")]
    [InlineData("Send a reply to that email.")]
    public async Task Non_connector_context_request_never_inspects_retained_accounts(string request)
    {
        await RetainConversation(readBody: true);
        _commands.ObserveJarvisTranscript(request, default);
        var before = (_requests.Count, _owners);
        Assert.Null(await _controller.TryReadRetainedContextAsync(default));
        Assert.Equal(before, (_requests.Count, _owners));
    }

    [Fact]
    public async Task Conflicting_model_url_receipt_identifies_the_actual_retained_conversation()
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript("Open that thread in a new tab.", default);
        var before = _requests.Count;
        var result = await Tool("open_url", new { url = "https://unrelated.example.test" });
        Assert.Equal(true, result["ok"]);
        var receipt = JsonSerializer.SerializeToElement(result);
        Assert.Equal("gmail", receipt.GetProperty("provider").GetString());
        var resolved = Assert.Single(receipt.GetProperty("resolved_objects").EnumerateArray());
        Assert.Equal("Fixture conversation", resolved.GetProperty("title").GetString());
        Assert.Equal(Assert.Single(_urls), resolved.GetProperty("url").GetString());
        Assert.True(resolved.GetProperty("opened").GetBoolean());
        Assert.False(receipt.GetProperty("navigation_verified").GetBoolean());
        Assert.Equal(before, _requests.Count);
        Assert.DoesNotContain("unrelated.example.test", receipt.GetRawText());
    }

    [Fact]
    public async Task Ordinary_navigation_does_not_inspect_retained_account_or_request_connector_data()
    {
        await RetainConversation();
        _commands.ObserveJarvisTranscript("Open Gmail.", default);
        var before = (_requests.Count, _owners);
        Assert.Equal(true, (await Tool("open_url", new { url = "https://mail.google.com/" }))["ok"]);
        Assert.Equal(before, (_requests.Count, _owners));
        Assert.Equal("https://mail.google.com/", Assert.Single(_urls));
    }
}
