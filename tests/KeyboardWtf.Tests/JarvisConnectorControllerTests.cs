namespace KeyboardWtf.Tests;

using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class JarvisConnectorControllerTests : IAsyncLifetime
{
    [Theory]
    [InlineData(true, "gmail", "mail_search", true)]
    [InlineData(true, "googledocs", "docs_read", true)]
    [InlineData(true, "googledrive", "drive_read", true)]
    [InlineData(true, "slack", "slack_search", true)]
    [InlineData(false, "gmail", "mail_search", true)]
    [InlineData(true, "gmail", "mail_search", false)]
    [InlineData(true, "gmail", "mail_send", true)]
    public async Task Native_verified_reads_request_complete_spoken_delivery_without_extra_work(
        bool native, string provider, string capability, bool verified)
    {
        var write = capability == "mail_send";
        var f = new Fixture(preferDirectReadReceipt: native)
        {
            User = write ? "Send this email" : "Read connected service records", ConnectorId = provider,
            ReadCapability = write ? "mail_search" : capability
        };
        var resolved = await f.Resolve(new { connector_id = provider });
        Assert.False(resolved.ContainsKey("spoken_delivery"));
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new
        {
            state = verified ? "completed" : "failed", verified,
            result = new { data = new { body = "Observed first exchange, later reply and final outcome." }, truncated = false }
        }));
        var receipt = await f.Step(capability, "{}");
        Assert.Equal(native && verified && !write, receipt.ContainsKey("spoken_delivery"));
        if (native && verified && !write)
        {
            Assert.Contains("in full as plain prose", (string)receipt["spoken_delivery"]);
            Assert.Contains("final outcome", JsonSerializer.Serialize(receipt["connector"]));
        }
        Assert.Equal(1, f.Resolves);
        Assert.Equal(1, f.Executes);
        Assert.Equal(0, f.Interpretations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Today_search_keeps_request_day_across_resolve_midnight_and_account_handoff(bool useReader, bool changeAccount)
    {
        var zone = TimeZoneInfo.Local;
        var localMidnight = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMidnight, zone));
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMidnight.AddDays(1), zone));
        var requested = end.AddSeconds(-2);
        var expectedQuery = $"(invoice) after:{start.ToUnixTimeSeconds()} before:{end.ToUnixTimeSeconds()}";
        JsonElement prompt = default;
        var models = 0;
        var f = new Fixture(reasonRead: useReader ? (_, _, _, input, _, _, _) =>
        {
            ++models; prompt = JsonDocument.Parse(input).RootElement.Clone();
            return Task.FromResult("{\"action\":\"answer\",\"capability\":\"\",\"arguments_json\":\"{}\",\"answer\":\"The scoped headers list one email.\",\"references\":[\"r1\"],\"partial\":false}");
        } : null) { User = "Find today's emails", AllowReaderQuery = true, Now = requested };
        f.BeforeResolveReply = () => f.Now = end.AddSeconds(2);
        var resolved = await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal("2026-10-11", JsonSerializer.SerializeToElement(resolved["mail_date_scope"]).GetProperty("localDay").GetString());
        if (changeAccount)
        {
            f.User = "Actually, use my work Gmail account";
            f.Identity = "explicit-account-correction";
            var changed = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
                { task_id = f.Id, action = "change_account", account_id = Guid.NewGuid().ToString("N") }), true, default);
            Assert.True((bool)changed["ok"]);
            Assert.Equal("2026-10-11", JsonSerializer.SerializeToElement(changed["mail_date_scope"]).GetProperty("localDay").GetString());
        }
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = expectedQuery, messages = new[]
                { new { id = "aabbccdd11", threadId = "aabbccdd22", snippet = "Observed metadata" } } }, truncated = false } }));
        await f.Execute();
        Assert.Equal(1, f.Executes);
        Assert.Equal(expectedQuery, f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal("exact", f.ExecutedArguments.GetProperty("matchMode").GetString());
        Assert.Equal(useReader ? 1 : 0, models);
        if (useReader)
        {
            var scope = prompt.GetProperty("mailDateScope");
            Assert.Equal("2026-10-11", scope.GetProperty("localDay").GetString());
            Assert.Equal(requested, scope.GetProperty("requestUtc").GetDateTimeOffset());
            Assert.Equal(zone.Id, scope.GetProperty("timeZoneId").GetString());
        }
    }

    [Fact]
    public async Task Read_planning_never_publishes_search_before_dispatch_and_keeps_one_visual_owner_between_steps()
    {
        var firstModel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDecision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelCalls = 0;
        var f = new Fixture(reasonRead: async (_,_,_,_,_,_,token) =>
        {
            ++modelCalls; firstModel.SetResult(true); return await firstDecision.Task.WaitAsync(token);
        }) { User = "Find a relevant message", AllowReaderQuery = true, Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        var events = new List<(string Detail, string ConnectorId)>();
        void Changed() => events.Add(KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(1)));
        KeyboardWtfState.UiChanged += Changed;
        try
        {
            var execution = f.Execute(); await f.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEmpty(events);
            Assert.Equal(("Thinking…", "gmail"), events[0]);
            Assert.Equal(0, modelCalls); Assert.Equal(1, f.Executes);
            Assert.Equal("invoice", f.ExecutedArguments.GetProperty("query").GetString());
            Assert.Equal(("Searching…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(1)));
            f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data = new
                { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", snippet = "Observed project candidate" } } }, truncated = false } }));
            await firstModel.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(("Thinking…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(1)));
            Assert.All(events, state => Assert.False(string.IsNullOrEmpty(state.Detail)));
            Assert.False(f.Controller.TryCancelSingleTask("How long is this taking?", out _));
            Assert.True(f.Controller.TryCancelSingleTask("Stop the Gmail search", out _));
            firstDecision.SetResult("{\"action\":\"answer\",\"capability\":\"\",\"arguments_json\":\"{}\",\"answer\":\"late answer\",\"references\":[\"r1\"],\"partial\":false}");
            await execution;
            Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
            Assert.Equal(1, f.Executes);
            Assert.Equal(0, f.Notices);
        }
        finally { KeyboardWtfState.UiChanged -= Changed; }
    }

    [Theory]
    [InlineData("googledocs", "docs_read", "{\"documentId\":\"documentOne\",\"title\":\"Project notes\",\"body\":{\"text\":\"PRIVATE FULL BODY\"}}", "https://docs.google.com/document/d/documentOne/edit")]
    [InlineData("googledrive", "drive_read", "{\"id\":\"fileOne\",\"name\":\"Project notes\",\"webViewLink\":\"https://drive.google.com/file/d/fileOne/view\"}", "https://drive.google.com/file/d/fileOne/view")]
    [InlineData("github", "github_repos", "[{\"full_name\":\"owner/project\",\"name\":\"project\",\"html_url\":\"https://github.com/owner/project\"}]", "https://github.com/owner/project")]
    [InlineData("youtube", "youtube_channel", "{\"items\":[{\"id\":\"UCabcdefghijklmnopqrstuv\",\"snippet\":{\"title\":\"Project channel\"}}]}", "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv")]
    public async Task Provider_reference_survives_completed_task_recall_without_new_search_or_write_authority(string provider, string capability, string data, string expectedUrl)
    {
        var f = new Fixture { User = "Read connected service records", ReadCapability = capability };
        await f.Resolve(new { connector_id = provider });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = JsonDocument.Parse(data).RootElement.Clone(), truncated = false } }));
        await f.Step(capability, "{}"); await f.Control("complete");
        var recalls = f.Executes;
        var retained = await f.Control("recall");
        Assert.True((bool)retained["ok"]);
        Assert.False((bool)retained["action_authority"]);
        Assert.Contains("ObjectReferences", JsonSerializer.Serialize(retained));
        Assert.DoesNotContain("PRIVATE FULL BODY", JsonSerializer.Serialize(retained));
        Assert.True(f.Controller.TryResolveExactMailOpen("Open it in a new tab.", out var exactUrl, out var refused));
        Assert.Null(refused); Assert.Equal(expectedUrl, exactUrl);
        f.User = "Open that result";
        var opened = await f.Control("open");
        Assert.True((bool)opened["ok"]); Assert.Equal(expectedUrl, opened["url"]);
        Assert.False((bool)opened["navigation_verified"]);
        Assert.Equal(recalls, f.Executes);
        f.Owner = "different-account";
        Assert.False((bool)(await f.Control("open"))["ok"]);
        await f.Controller.ResetAsync();
        Assert.False((bool)(await f.Control("recall"))["ok"]);
    }

    [Fact]
    public async Task Owned_reader_survives_ordinary_interjection_but_explicit_cancel_discards_late_answer()
    {
        var modelStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateModel=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f=new Fixture(reasonRead: async (_,_,_,_,_,_,token) => {modelStarted.SetResult(true);return await lateModel.Task.WaitAsync(token);});
        await f.Resolve(new {connector_id="gmail"});
        var execution=f.Execute(); await modelStarted.Task;
        Assert.False(f.Controller.TryCancelSingleTask("Open Spotify",out _));
        Assert.False(f.Controller.TryCancelSingleTask("uh hmm",out _));
        Assert.Equal("Thinking…",KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
        Assert.True(f.Controller.TryCancelSingleTask("cancel that",out var cancellation));
        Assert.Equal("cancelled",cancellation["state"]);
        lateModel.SetResult("{\"action\":\"answer\",\"capability\":\"\",\"arguments_json\":\"{}\",\"answer\":\"late ungrounded answer\",\"references\":[],\"partial\":true}");
        await execution;
        Assert.Equal("",KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
        Assert.Equal(0,f.Executes); Assert.Equal(0,f.Notices);
        Assert.False((bool)(await f.Control("recall"))["ok"]);
    }
    [Fact]
    public async Task Reader_quota_failure_is_truthful_terminal_and_clears_activity_without_connector_retry()
    {
        var f = new Fixture(reasonRead: (_,_,_,_,_,_,_) => throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Quota));
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Execute();
        var result = (JsonElement)reply["connector"];
        Assert.Equal("failed",result.GetProperty("state").GetString());
        Assert.Equal("ai_rate_limit",result.GetProperty("errorCategory").GetString());
        Assert.Contains("not disconnected",result.GetProperty("message").GetString());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.Equal(0,f.Executes); Assert.Equal("",KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
        var repeat = await f.Execute();
        Assert.False((bool)repeat["this_proposal_dispatched"]); Assert.Equal(0,f.Executes);
    }
    [Fact]
    public async Task Github_insights_seed_the_requested_native_repository_before_reasoning()
    {
        Fixture f = null;
        f = new Fixture(reasonRead: (_, _, _, prompt, _, _, _) =>
        {
            Assert.Equal(1, f.Executes);
            Assert.Equal("github_repos", Assert.Single(f.ExecutedCapabilities));
            Assert.Contains("owner/project--desktop", prompt);
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Quota);
        }) { User = "Using GitHub give me insights about project--desktop and its latest release.", ReadCapability = "github_repos", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "github" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new[] { new { full_name = "owner/project--desktop" } }, truncated = false } }));
        var reply = await f.Step("github_repos", "{\"query\":\"project--desktop\"}");
        var result = (JsonElement)reply["connector"];
        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.Equal("ai_rate_limit", result.GetProperty("result").GetProperty("data").GetProperty("reasoningError").GetString());
        Assert.Equal(1, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        await f.Controller.ResetAsync();
    }
    [Fact]
    public async Task Reader_personal_503_after_verified_search_reports_ai_outage_and_stays_terminal()
    {
        var models = 0;
        Fixture f = null;
        f = new Fixture(reasonRead: (task, logical, _, prompt, _, _, _) =>
        {
            ++models;
            Assert.Equal(f.Id, task); Assert.Equal(f.LogicalLiveSessionId, logical);
            Assert.Equal(1, f.Executes);
            using var evidence = JsonDocument.Parse(prompt);
            Assert.True(evidence.RootElement.GetProperty("evidence")[0].GetProperty("verified").GetBoolean());
            Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider)
                { HttpStatusCode = 503 };
        }) { AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { messages = new[] { new { id = "observed-invoice" } } }, truncated = false } }));

        var reply = await f.Execute();
        var result = (JsonElement)reply["connector"];
        Assert.Equal("completed", result.GetProperty("state").GetString());
        Assert.True(result.GetProperty("verified").GetBoolean()); // Native evidence, explicitly not a completed semantic answer.
        Assert.True(result.GetProperty("result").GetProperty("truncated").GetBoolean());
        var data = result.GetProperty("result").GetProperty("data");
        Assert.Equal("ai_provider_unavailable", data.GetProperty("reasoningError").GetString());
        Assert.Equal("native_evidence_partial", data.GetProperty("answerKind").GetString());
        Assert.Contains("Message content has not been established", data.GetProperty("answer").GetString());
        Assert.Empty(data.GetProperty("nativeMessageContent").EnumerateArray());
        Assert.Equal("partial", data.GetProperty("goalStatus").GetString());
        Assert.Equal(2, models); Assert.Equal(1, f.Executes); Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Rebinds);
        Assert.Equal(new[] { f.LogicalLiveSessionId }, f.ExecutedLiveSessionIds);
        Assert.Equal("original-account", f.Owner);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));

        var repeat = await f.Execute();
        Assert.False((bool)repeat["this_proposal_dispatched"]);
        var write = await f.Step("mail_send", "{}");
        Assert.False((bool)write["this_proposal_dispatched"]);
        Assert.Equal(2, models); Assert.Equal(1, f.Executes);
    }
    [Theory]
    [InlineData("Provider", null, "unverified_result")]
    [InlineData("Provider", 429, "unverified_result")]
    [InlineData("Provider", 500, "unverified_result")]
    [InlineData("Provider", 502, "unverified_result")]
    [InlineData("Provider", 504, "unverified_result")]
    [InlineData("AmbiguousTransport", 503, "unverified_result")]
    [InlineData("PreDispatchConnection", 503, "unverified_result")]
    [InlineData("Capability", 503, "unverified_result")]
    [InlineData("Quota", 503, "ai_rate_limit")]
    public async Task Reader_other_personal_failures_do_not_claim_a_known_503_outage(string failure, int? status, string category)
    {
        var models = 0;
        var f = new Fixture(reasonRead: (_, _, _, _, _, _, _) =>
        {
            ++models;
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(
                Enum.Parse<KeyboardWtf.Services.Ai.Backends.PersonalAiFailure>(failure)) { HttpStatusCode = status };
        });
        await f.Resolve(new { connector_id = "gmail" });
        var result = (JsonElement)(await f.Execute())["connector"];
        Assert.Equal("failed", result.GetProperty("state").GetString());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.Equal(category, result.GetProperty("errorCategory").GetString());
        Assert.DoesNotContain("HTTP 503", result.GetProperty("message").GetString());
        Assert.Equal(1, models); Assert.Equal(0, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.False((bool)(await f.Execute())["this_proposal_dispatched"]);
        Assert.Equal(1, models);
    }
    [Fact]
    public async Task Reader_http_503_without_a_personal_provider_owner_remains_unverified()
    {
        var models = 0;
        var f = new Fixture(reasonRead: (_, _, _, _, _, _, _) =>
        {
            ++models;
            throw new HttpRequestException("PRIVATE transport payload", null, System.Net.HttpStatusCode.ServiceUnavailable);
        });
        await f.Resolve(new { connector_id = "gmail" });
        var result = (JsonElement)(await f.Execute())["connector"];
        Assert.Equal("unverified_result", result.GetProperty("errorCategory").GetString());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.DoesNotContain("HTTP 503", result.GetProperty("message").GetString());
        Assert.DoesNotContain("PRIVATE", result.GetRawText());
        Assert.Equal(1, models); Assert.Equal(0, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }
    [Theory]
    [InlineData("failed", 3, true)]
    [InlineData("truncated", 4, true)]
    [InlineData("missingText", 4, true)]
    [InlineData("complete", 4, false)]
    public async Task Prepared_day_required_body_coverage_survives_subset_synthesis_and_completed_recall(string middleBody, int expectedExecutions, bool expectedPartial)
    {
        var models = 0;
        Fixture f = null;
        f = new Fixture(reasonRead: (task, logical, _, _, _, _, _) =>
        {
            ++models; Assert.Equal(f.Id, task); Assert.Equal(f.LogicalLiveSessionId, logical);
            Assert.Equal(expectedExecutions, f.Executes);
            // The model intentionally hides the middle body's defect by citing
            // only the intact first body and declaring complete coverage.
            return Task.FromResult(JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
                answer = "The first observed body records a research response.", references = new[] { "r2" }, partial = false }));
        }) { User = "Check today's emails for a professor's research response", AllowReaderQuery = true, AllowPreparedDayBodies = true };
        var zone = TimeZoneInfo.Local;
        var midnight = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight.AddDays(1), zone));
        f.Now = start.AddHours(2);
        var query = $"after:{start.ToUnixTimeSeconds()} before:{end.ToUnixTimeSeconds()}";
        var resolved = await f.Resolve(new { connector_id = "gmail" });
        var selectedAccount = ((JsonElement)resolved["connector"]).GetProperty("accountId").GetString();
        Assert.Equal(query, JsonSerializer.SerializeToElement(resolved["mail_date_scope"]).GetProperty("query").GetString());
        var ids = new[] { "aabbccdd00", "aabbccdd11", "aabbccdd22" };
        var threads = new[] { "eeff001100", "eeff001111", "eeff001122" };
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
            data = new { queryUsed = query, messages = ids.Select((id, i) => new { id, threadId = threads[i] }).ToArray(),
                sampledMessages = ids.Select((id, i) => new { id, threadId = threads[i], from = "Research office <office@example.test>", subject = "Research correspondence", date = "12 October 2026" }).ToArray(),
                metadataSample = new { requested = 3, returned = 3, complete = true }, pageItemCount = 3, hasMore = false } } }));
        foreach (var index in Enumerable.Range(0, 3))
        {
            var data = new Dictionary<string, object> { ["id"] = ids[index], ["threadId"] = threads[index],
                ["text"] = "A research response was received.", ["contentTruncated"] = index == 1 && middleBody == "truncated",
                ["headers"] = new[] { new { name = "Subject", value = "Research correspondence" } } };
            if (index == 1 && middleBody == "missingText") data.Remove("text");
            var verified = index != 1 || middleBody != "failed";
            f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = verified ? "completed" : "failed", verified,
                result = new { data, truncated = index == 1 && middleBody == "truncated" } }));
        }

        var reply = await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        var result = (JsonElement)reply["connector"];
        Assert.True((bool)reply["ok"]); Assert.Equal("completed", result.GetProperty("state").GetString());
        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.Equal(expectedPartial, result.GetProperty("result").GetProperty("truncated").GetBoolean());
        if (expectedPartial)
        {
            Assert.True((bool)reply["partial"]); Assert.True((bool)reply["task_partial"]);
            Assert.Contains("Do not claim all reads complete", (string)reply["instruction"]);
        }
        else Assert.False(reply.ContainsKey("task_partial"));
        Assert.Equal(new[] { "r2" }, result.GetProperty("result").GetProperty("data").GetProperty("evidenceReferences").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(1, models); Assert.Equal(expectedExecutions, f.Executes); Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Rebinds);
        Assert.Equal(new[] { "mail_search" }.Concat(Enumerable.Repeat("mail_read", expectedExecutions - 1)), f.ExecutedCapabilities);
        Assert.All(f.ExecutedTaskIds, task => Assert.Equal(f.Id, task));
        Assert.All(f.ExecutedLiveSessionIds, logical => Assert.Equal(f.LogicalLiveSessionId, logical));

        var complete = await f.Control("complete");
        Assert.True((bool)complete["ok"]); Assert.Equal("completed", complete["state"]); Assert.Equal(f.Id, complete["task_id"]);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        var recall = await f.Control("recall");
        Assert.True((bool)recall["ok"]); Assert.False((bool)recall["action_authority"]);
        var context = Assert.IsType<ConnectorSessionResult>(recall["context"]);
        Assert.Equal(expectedPartial, context.Partial); Assert.Equal(selectedAccount, context.AccountId); Assert.Equal(f.Id, context.TaskId);
        Assert.False((bool)(await f.Step("mail_send", "{}"))["this_proposal_dispatched"]);
        Assert.Equal(1, models); Assert.Equal(expectedExecutions, f.Executes); Assert.Equal(1, f.Cleanups);
    }
    [Fact]
    public async Task Completed_read_retains_only_session_facts_and_one_exact_account_thread_without_action_authority()
    {
        var f = new Fixture { User = "Find a relevant email", ReadCapability = "mail_read", AccountLabel = "reader@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Private full body",
                headers = new[] { new { name = "subject", value = "Relevant message" } } } } }));
        await f.Step("mail_read", "{\"id\":\"aabbccdd11\"}");
        await f.Control("complete");
        var recall = await f.Control("recall");
        Assert.True((bool)recall["ok"]); Assert.False((bool)recall["action_authority"]);
        Assert.DoesNotContain("Private full body", JsonSerializer.Serialize(recall));
        Assert.Contains("Relevant message", JsonSerializer.Serialize(recall));
        Assert.True(f.Controller.TryResolveExactMailOpen("Open that email.", out var url, out var rejection));
        Assert.Null(rejection); Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aabbccdd22", url);
        foreach (var phrase in new[] { "Open that thread in a new tab.", "Open the email in a new tab.", "Open it.", "Pull it up.", "Show me that conversation." })
        {
            Assert.True(f.Controller.TryResolveExactMailOpen(phrase, out var exact, out var refused), phrase);
            Assert.Null(refused); Assert.Equal(url, exact);
        }
        Assert.False(f.Controller.TryResolveExactMailOpen("Open Gmail Inbox", out _, out _));
        Assert.False((bool)(await f.Step("mail_send", "{\"to\":\"other@example.test\"}"))["ok"]);
        f.Owner = "different-account";
        Assert.False((bool)(await f.Control("recall"))["ok"]);
        Assert.False(f.Controller.TryResolveExactMailOpen("Open that email", out _, out _));
        await f.Controller.ResetAsync();
    }
    private static readonly List<Fixture> Fixtures = new();
    [Fact]
    public async Task Admitted_connector_activity_is_visible_before_resolve_returns()
    {
        var f = new Fixture();
        var observed = false;
        f.BeforeResolveReply = () =>
        {
            observed = true;
            Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
            Assert.NotEmpty(KeyboardWtfState.LongTaskDisplayState().Detail);
        };
        try
        {
            Assert.True((bool)(await f.Resolve(new { connector_id = "gmail" }))["ok"]);
            Assert.True(observed);
        }
        finally { await f.Controller.ResetAsync(); }
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }
    [Fact]
    public async Task Yes_to_ambiguous_detail_offer_asks_which_thread_without_reusing_terminal_authority()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
            data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" }, new { id = "aabbccdd33", threadId = "aabbccdd44" } } } } }));
        await f.Execute(); await f.Control("complete");
        f.Controller.ObserveCompletedReply("Want me to pull the full details?", f.Identity);
        f.User = "Yes."; f.Identity = "new-yes";
        var result = await f.Execute();
        Assert.False((bool)result["ok"]); Assert.Contains("Which", (string)result["message"]);
        Assert.DoesNotContain("disconnected", JsonSerializer.Serialize(result));
        Assert.Equal(1, f.Executes); Assert.Equal(1, f.Resolves);
    }
    [Fact]
    public async Task Exhausted_synthesis_keeps_native_candidates_and_yes_clarifies_instead_of_reusing_dead_task()
    {
        var f = new Fixture(reasonRead: (_, _, _, _, _, _, _) => Task.FromResult(
            "{\"action\":\"answer\",\"capability\":\"\",\"arguments_json\":\"{}\",\"answer\":\"Unsupported complete summary\",\"references\":[\"r8\"],\"partial\":false}"))
            { User = "Find the relevant conversation", AllowReaderQuery = true, AccountLabel = "reader@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
            data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" }, new { id = "aabbccdd33", threadId = "aabbccdd44" } } } } }));
        await f.Execute(); await f.Control("complete");
        var recall = await f.Control("recall");
        Assert.Contains("aabbccdd22", JsonSerializer.Serialize(recall));
        Assert.Contains("aabbccdd44", JsonSerializer.Serialize(recall));
        Assert.DoesNotContain("Unsupported complete summary", JsonSerializer.Serialize(recall));
        Assert.True(f.Controller.TryResolveExactMailOpen("Open that email.", out var url, out var rejected));
        Assert.Null(url); Assert.NotNull(rejected);
        f.Controller.ObserveCompletedReply("Want me to pull the full details?", f.Identity);
        f.User = "Yes."; f.Identity = "new-yes";
        var answer = await f.Execute();
        Assert.False((bool)answer["ok"]); Assert.Contains("Which", (string)answer["message"]);
        Assert.DoesNotContain("task_lifecycle", JsonSerializer.Serialize(answer));
        Assert.Equal(1, f.Executes); Assert.Equal(1, f.Resolves);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_native_result_survives_503_until_collected_or_explicitly_cancelled(bool cancel)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new Fixture(reasonRead: async (_, _, _, _, _, _, token) =>
        {
            await release.Task.WaitAsync(token);
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider) { HttpStatusCode = 503 };
        }) { User = "Find the relevant conversation", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
            data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } } } } }));
        var started = await f.Execute(); Assert.True((bool)started["started"]);
        release.SetResult(true);
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.True(f.Controller.HoldsPendingReply(new(f.User, f.Identity)));
        if (cancel)
        {
            Assert.True(f.Controller.TryCancelSingleTask("Cancel the search", out _));
            Assert.False(f.Controller.HoldsPendingReply(new(f.User, f.Identity)));
        }
        else
        {
            var collected = await f.Control("result"); Assert.True((bool)collected["ok"]);
            Assert.False(f.Controller.HoldsPendingReply(new(f.User, f.Identity)));
            Assert.True((bool)(await f.Control("recall"))["ok"]);
        }
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, f.Executes);
    }

    [Theory]
    [InlineData("Would you like me to send a reply?")]
    [InlineData("I found the thread.")]
    public async Task Yes_without_a_specific_read_offer_does_not_create_read_or_write_authority(string answer)
    {
        var f = new Fixture { User = "Read this email", ReadCapability = "mail_read", AllowResourceId = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Fixture content" } } }));
        await f.Step("mail_read", "{\"id\":\"aabbccdd11\"}"); await f.Control("complete");
        f.Controller.ObserveCompletedReply(answer, f.Identity);
        f.Identity = "new-yes"; f.User = "Yes.";
        Assert.False((bool)(await f.Step("mail_read", "{\"id\":\"aabbccdd11\"}"))["ok"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
    }
    [Theory]
    [InlineData("Yes.", true, "status")]
    [InlineData("Yes.", true, "result")]
    [InlineData("Yes.", true, "guide")]
    [InlineData("Yes.", true, "desktop_context")]
    [InlineData("Pull up the full details.", false, "desktop_context")]
    [InlineData("Show me the full details of that thread.", false, "result")]
    [InlineData("Yes.", true)]
    [InlineData("Show me the full details of that thread.", false)]
    [InlineData("Could you please give the details about it?", false)]
    [InlineData("Read the full chat.", false)]
    [InlineData("Tell me more about it.", false)]
    [InlineData("What did she ask me for?", false)]
    [InlineData("Did I reply?", false)]
    [InlineData("Tell me more about it.", false, "result", true)]
    public async Task Retained_thread_followup_after_reasoning_503_reads_new_task_without_researching_or_replaying(string request, bool offered, string control = null, bool liveNative = false)
    {
        var failReasoning = true;
        var models = 0;
        var f = new Fixture(preferDirectReadReceipt: liveNative, reasonRead: (_, _, _, prompt, _, _, _) =>
        {
            ++models;
            if (failReasoning) throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider) { HttpStatusCode = 503 };
            Assert.Contains("Full conversation evidence", prompt);
            return Task.FromResult("{\"action\":\"answer\",\"capability\":\"\",\"arguments_json\":\"{}\",\"answer\":\"The sender requested the domain. The recipient replied. The latest message confirms completion.\",\"references\":[\"r1\"],\"partial\":false,\"goal_status\":\"matched\",\"target_reference\":\"r1\"}");
        }) { User = "Find the relevant conversation", AllowReaderQuery = true, AccountLabel = "reader@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        var oldId = f.Id;
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
            data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Project conversation" } } } } }));
        await f.Execute();
        Assert.Equal(liveNative ? 0 : 2, models);
        if (liveNative) await f.Control("complete");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        if (offered) f.Controller.ObserveCompletedReply("I found the thread. Want me to pull the full details?", f.Identity);
        f.User = request; f.Identity = "fresh-followup";
        f.ReadCapability = "mail_thread"; f.AllowReaderQuery = false; f.AllowResourceId = true;
        failReasoning = false;
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
            data = new { id = "aabbccdd22", messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Full conversation evidence. The domain request was completed." } } } } }));
        var result = control == "desktop_context"
            ? await f.Controller.TryReadRetainedContextAsync(default)
            : control is null
            ? await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
                { task_id = oldId, capability_id = "mail_search", arguments_json = "{\"query\":\"OLD QUERY DO NOT REPLAY\"}" }), false, default)
            : await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
                { task_id = oldId, action = control }), false, default);
        Assert.True((bool)result["ok"], JsonSerializer.Serialize(result));
        Assert.NotEqual(oldId, f.Id);
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes); Assert.Equal(liveNative ? 0 : 3, models);
        if (liveNative) Assert.Contains("Full conversation evidence", JsonSerializer.Serialize(result));
        Assert.Equal(new[] { "mail_search", "mail_thread" }, f.ExecutedCapabilities);
        Assert.Equal("aabbccdd22", f.ExecutedArguments.GetProperty("id").GetString());
        await f.Control("complete");
        var repeated = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = oldId, capability_id = "mail_search", arguments_json = "{\"query\":\"OLD QUERY DO NOT REPLAY\"}" }), false, default);
        Assert.True(repeated["ok"] is true);
        Assert.Equal(true, repeated["already_completed"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes); Assert.Equal(liveNative ? 0 : 3, models);
        Assert.True(f.Controller.TryResolveExactMailOpen("Open that thread in a new tab.", out var url, out var rejected));
        Assert.Null(rejected); Assert.EndsWith("#all/aabbccdd22", url);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }
    [Theory]
    [InlineData("Yes.")]
    [InlineData("Show me the full details of that thread.")]
    public async Task Retained_headers_only_result_does_not_grant_body_permission_from_a_details_offer(string followup)
    {
        var f = new Fixture { User = "Only headers; find the email", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Observed header" } } } } }));
        await f.Step("mail_search", "{\"query\":\"email\"}"); await f.Control("complete");
        f.Controller.ObserveCompletedReply("I found the thread. Want me to pull the full details?", f.Identity);
        f.Identity = "fresh-details"; f.User = followup;
        f.ReadCapability = "mail_thread"; f.AllowResourceId = true;
        var result = await f.Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.Contains("body", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("body_clipped", false)]
    [InlineData("receipt_partial", false)]
    [InlineData("missing_completeness", false)]
    [InlineData("older_scope", false)]
    [InlineData("different_message", false)]
    [InlineData("different_thread_reference", false)]
    [InlineData("account_changed", false)]
    [InlineData("cancelled", false)]
    public async Task Retained_complete_body_is_reused_only_after_fresh_same_thread_membership(string scenario, bool reuse)
    {
        const string message = "aabbccdd11", thread = "aabbccdd22", otherMessage = "aabbccdd33";
        var models = 0;
        var f = new Fixture(reasonRead: (_, _, _, _, _, _, _) =>
        {
            ++models;
            if (models == 1 || scenario == "different_thread_reference" && models == 4)
                return Task.FromResult("{\"action\":\"execute\",\"capability\":\"mail_read\",\"arguments_json\":\"{\\\"id\\\":\\\"aabbccdd11\\\"}\",\"answer\":\"\",\"references\":[],\"partial\":false}");
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider) { HttpStatusCode = 503 };
        }) { User = "Find the relevant email and give details", AllowReaderQuery = true, AllowPreparedDayBodies = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { messages = new[] { new { id = message, threadId = thread } } } } }));
        object contentTruncated = scenario == "missing_completeness" ? null : scenario == "body_clipped";
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = scenario == "receipt_partial", data = new { id = message, threadId = thread,
                text = "Retained complete body evidence", contentTruncated } } }));
        await f.Execute();
        Assert.Equal(2, f.Executes);
        Assert.Equal(3, models);
        if (scenario == "older_scope")
        {
            var tasks = (System.Collections.IDictionary)typeof(JarvisConnectorController)
                .GetField("_tasks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(f.Controller);
            var origin = tasks[f.Id];
            origin.GetType().GetField("ResearchEvidenceGoalRevisions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(origin, new[] { 1, 1 });
        }
        f.User = "Give me more details."; f.Identity = "fresh-details";
        f.ReadCapability = "mail_thread"; f.AllowReaderQuery = false; f.AllowResourceId = true;
        if (scenario is "account_changed" or "cancelled")
        {
            f.BeforeResolveReply = () => { if (scenario == "cancelled") f.Session.Cancel(); else f.Owner = "different-owner"; };
            var rejected = await f.Control("result");
            Assert.False(rejected.GetValueOrDefault("ok") is true);
            Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes); Assert.Equal(3, models);
            Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
            return;
        }
        var freshMessage = scenario == "different_message" ? otherMessage : message;
        var freshReferenceThread = scenario == "different_thread_reference" ? "aabbccdd44" : thread;
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = true, data = new { id = thread,
                messageReferences = new[] { new { id = freshMessage, threadId = freshReferenceThread } },
                messages = new[] { new { id = freshMessage, threadId = thread, text = "", contentTruncated = true } } } } }));
        if (!reuse) f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { id = freshMessage, threadId = thread,
                text = "Fresh complete body evidence", contentTruncated = false } } }));
        var result = await f.Control("result");
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal(2, f.Resolves);
        Assert.Equal(reuse ? 3 : 4, f.Executes);
        Assert.Equal(new[] { "mail_search", "mail_read", "mail_thread" }, f.ExecutedCapabilities.Take(3));
        Assert.Equal(reuse ? 1 : 2, f.ExecutedCapabilities.Count(cap => cap == "mail_read"));
        Assert.Contains(reuse ? "Retained complete body evidence" : "Fresh complete body evidence", JsonSerializer.Serialize(result));
        Assert.Equal(scenario == "different_thread_reference" ? 6 : 5, models);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Background_followup_result_collection_is_not_redirected_to_context_after_another_503()
    {
        var f = new Fixture(reasonRead: (_, _, _, _, _, _, _) => throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(
            KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider) { HttpStatusCode = 503 })
            { User = "Find the relevant conversation", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } } } } }));
        await f.Execute();
        f.User = "Give me more details."; f.Identity = "new-details";
        f.ReadCapability = "mail_thread"; f.AllowReaderQuery = false; f.AllowResourceId = true;
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = await f.Control("result");
        Assert.True(pending["started"] is true);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { id = "aabbccdd22", messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Actual message content" } } } } }));
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var collected = await f.Control("result");
        Assert.True(collected.ContainsKey("connector"), JsonSerializer.Serialize(collected));
        Assert.Contains("Actual message content", JsonSerializer.Serialize(collected));
        Assert.False(f.Controller.HoldsPendingReply(new(f.User, f.Identity)));
        Assert.Equal(2, f.Executes); Assert.Equal(2, f.Resolves);
    }

    [Theory]
    [InlineData("What did she ask me for?")]
    [InlineData("Did I reply?")]
    public async Task Natural_content_question_uses_already_read_messages_without_new_service_or_reasoning_work(string request)
    {
        var f = new Fixture { User = "Read this email", ReadCapability = "mail_read", AllowResourceId = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Please send the research proposal by Friday." } } }));
        await f.Step("mail_read", "{\"id\":\"aabbccdd11\"}"); await f.Control("complete");
        f.User = request; f.Identity = "new-content-question";
        var receipt = await f.Control("recall");
        Assert.True(receipt["ok"] is true);
        Assert.Contains("Please send the research proposal by Friday.", JsonSerializer.Serialize(receipt["nativeMessageContent"]));
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.DoesNotContain("Please send the research proposal by Friday.", JsonSerializer.Serialize(receipt["context"]));
    }

    [Fact]
    public async Task Details_for_multiple_already_read_candidates_returns_grouped_evidence_without_picking_or_researching()
    {
        var f = new Fixture { User = "Read these email conversations", ReadCapability = "mail_thread", AllowResourceId = true };
        await f.Resolve(new { connector_id = "gmail" });
        foreach (var id in new[] { "aabbccdd11", "aabbccdd22" })
        {
            f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = false, data = new { id, messages = new[] { new { id, threadId = id, text = "Observed discussion for " + id } } } } }));
            await f.Step("mail_thread", JsonSerializer.Serialize(new { id }));
        }
        await f.Control("complete");
        f.User = "Could you please give the details about it?"; f.Identity = "details-for-observed-results";
        var result = await f.Step("mail_thread", "{\"id\":\"stale-model-id\"}");
        Assert.True(result["ok"] is true);
        Assert.Equal(2, JsonSerializer.SerializeToElement(result["nativeMessageContent"]).GetArrayLength());
        Assert.Contains("Group multiple", (string)result["instruction"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(2, f.Executes);
    }

    [Theory]
    [InlineData("from:Sender date:2026/10/19")]
    [InlineData("{from:Sender DATE:2026-10-19}")]
    [InlineData("from:Sender -date:2026/10/19")]
    public async Task Unsupported_inferred_mail_date_is_correctable_before_dispatch(string query)
    {
        var f = new Fixture { User = "Find the email from Sender on October 19", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        Assert.False((bool)reply["ok"]);
        Assert.Equal("invalid_query", reply["state"]);
        Assert.False((bool)reply["this_proposal_dispatched"]);
        Assert.Equal(f.Id, reply["task_id"]);
        Assert.Contains("after:", (string)reply["validation"]);
        Assert.Equal(f.Now, (DateTimeOffset)reply["request_utc"]);
        Assert.Equal(TimeZoneInfo.Local.Id, reply["time_zone_id"]);
        Assert.Equal(0, f.Executes);
        var corrected = "from:\"Sender\" after:1792348200 before:1792434600";
        Assert.True((bool)(await f.Step("mail_search", JsonSerializer.Serialize(new { query = corrected })))["ok"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.Equal(corrected, f.ExecutedArguments.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("Search using: date:2026/10/19", "date:2026/10/19")]
    [InlineData("Find the email mentioning a date field", "subject:\"date:2026/10/19\"")]
    public async Task Mail_query_validation_preserves_literal_human_queries_and_quoted_content(string request, string query)
    {
        var f = new Fixture { User = request, AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)(await f.Step("mail_search", JsonSerializer.Serialize(new { query })))["ok"]);
        Assert.Equal(1, f.Executes);
        Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Simple_literal_search_uses_native_query_without_an_extra_reasoning_call()
    {
        var models = 0;
        var f = new Fixture(reasonRead: (_,_,_,_,_,_,_) =>
        { ++models; throw new InvalidOperationException("Simple literal lookup needs no reader model."); })
        { User = "Search using: subject:literal", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_search", "{\"query\":\"subject:literal\"}");
        Assert.True((bool)reply["ok"]);
        Assert.Equal(0, models); Assert.Equal(1, f.Executes);
        Assert.Equal("subject:literal", f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal("exact", f.ExecutedArguments.GetProperty("matchMode").GetString());
    }
    [Fact]
    public async Task Controller_rejects_changed_literal_before_provider_or_worker_dispatch()
    {
        var f = new Fixture { User = "Use the exact query: from:team@example.test subject:\"Project launch\"" };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_search", "{\"query\":\"from:team@example.test\"}");
        Assert.False((bool)reply["this_proposal_dispatched"]); Assert.Equal("literal_query_required", reply["state"]);
        Assert.Equal(0, f.Executes);
        await f.Step("mail_search", "{\"query\":\"from:team@example.test subject:\\\"Project launch\\\"\"}");
        Assert.Equal(1, f.Executes);
    }
    [Fact]
    public async Task Native_read_retains_bounded_prize_fact_and_date_without_whole_body_or_new_search()
    {
        var f = new Fixture { User = "Read the winning email", ReadCapability = "mail_read", AccountLabel = "reader@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { id = "aabbccdd11", threadId = "aabbccdd22",
                text = "PRIVATE UNRELATED PREAMBLE. Your project earned Top 10 Best Performers. PRIVATE UNRELATED SIGNATURE.",
                headers = new[] { new { name = "Date", value = "18 July 2026" }, new { name = "Subject", value = "Project winner" } } }, truncated = false } }));
        await f.Step("mail_read", "{\"id\":\"aabbccdd11\"}"); await f.Control("complete");
        var recall = await f.Control("recall"); var serialized = JsonSerializer.Serialize(recall);
        Assert.Contains("Top 10 Best Performers", serialized); Assert.Contains("18 July 2026", serialized);
        Assert.DoesNotContain("PRIVATE UNRELATED", serialized); Assert.False((bool)recall["action_authority"]);
        Assert.Equal(1, f.Executes);
        f.Now = f.Now.AddHours(2);
        var later = await f.Control("recall");
        Assert.True((bool)later["ok"]);
        Assert.Contains("Top 10 Best Performers", JsonSerializer.Serialize(later));
        Assert.False((bool)later["action_authority"]);
        Assert.Equal(1, f.Executes);
    }
    [Theory]
    [InlineData("googledocs", "docs_read", "https://docs.google.com/document/d/documentOne/edit", "documentOne")]
    [InlineData("googleforms", "forms_read", "https://docs.google.com/forms/d/formOne/edit", "formOne")]
    public async Task Literal_resource_read_reaches_provider_before_reasoning_quota_failure(string provider, string capability, string url, string id)
    {
        var models = 0;
        var f = new Fixture(reasonRead: (_,_,_,_,_,_,_) =>
        { ++models; throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Quota); })
            { User = "Summarize " + url, ReadCapability = capability, AllowResourceId = true };
        await f.Resolve(new { connector_id = provider });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { documentId = id, title = "Observed resource" }, truncated = false } }));
        var reply = await f.Step(capability, JsonSerializer.Serialize(new { id }));
        Assert.Equal(1, f.Executes);
        Assert.Equal(id, f.ExecutedArguments.GetProperty("id").GetString());
        Assert.Equal(1, models);
        var partial = ((JsonElement)reply["connector"]).GetProperty("result");
        Assert.True(partial.GetProperty("truncated").GetBoolean());
        Assert.Equal("ai_rate_limit", partial.GetProperty("data").GetProperty("reasoningError").GetString());
        Assert.Contains("retrieval succeeded", partial.GetProperty("data").GetProperty("answer").GetString());
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }
    [Fact]
    public async Task Read_only_recall_can_omit_an_id_only_for_one_retained_result_and_never_revives_actions()
    {
        var f=new Fixture { User="Find a relevant email", ReadCapability="mail_read", AccountLabel="reader@example.test" };
        async Task Retain()
        {
            await f.Resolve(new {connector_id="gmail"});
            f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new
                {data=new {id="aabbccdd11",threadId="aabbccdd22",text="Do not retain this body",headers=new[]{new {name="Subject",value="Verified topic"}}}}}));
            await f.Step("mail_read","{\"id\":\"aabbccdd11\"}"); await f.Control("complete");
        }
        await Retain(); var reads=f.Executes;
        var recall=await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {action="recall"}),false,default);
        Assert.True((bool)recall["ok"]); Assert.False((bool)recall["action_authority"]);
        Assert.DoesNotContain("Do not retain this body",JsonSerializer.Serialize(recall));
        f.User="Open that email";
        var open=await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {action="open"}),false,default);
        Assert.True((bool)open["ok"]); Assert.Contains("#all/aabbccdd22",(string)open["url"]);
        Assert.Equal(reads,f.Executes); Assert.False((bool)open["navigation_verified"]);
        var state=await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {action="cancel"}),false,default);
        Assert.False((bool)state["ok"]);
        foreach(var reference in new object[]{4,true,new {invented="id"}})
            Assert.False((bool)(await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {action="recall",task_id=reference}),false,default))["ok"]);
        f.User="Find another relevant email"; f.Identity="second-read"; await Retain();
        Assert.False((bool)(await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {action="recall"}),false,default))["ok"]);
        await f.Controller.ResetAsync();
        Assert.False((bool)(await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {action="recall"}),false,default))["ok"]);
    }
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var fixture in Fixtures.ToArray()) await fixture.Controller.ResetAsync();
        Fixtures.Clear();
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
    }
    [Theory]
    [InlineData("Read this Google document and tell me what it is about: https://docs.google.com/document/d/doc_A-1/edit?tab=t.0 . Do not change it.", true)]
    [InlineData("Could you please summarize https://docs.google.com/document/d/doc_A-1/edit", true)]
    [InlineData("Analyze this Google Doc: https://docs.google.com/document/d/doc_A-1/edit. Don't edit it.", true)]
    [InlineData("Open https://docs.google.com/document/d/doc_A-1/edit", false)]
    [InlineData("Read https://docs.google.com/document/d/doc_A-1/edit in my browser", false)]
    [InlineData("Analyze the visible layout of https://docs.google.com/document/d/doc_A-1/edit", false)]
    [InlineData("Read https://docs.google.com:8443/document/d/doc_A-1/edit", false)]
    [InlineData("Read https://docs.google.com/document/d/doc_A-1/edit and edit the heading", false)]
    [InlineData("Read https://docs.google.com/document/d/doc_A-1/edit. Do not change it, but delete a paragraph.", false)]
    [InlineData("Analyze the selected text from https://docs.google.com/document/d/doc_A-1/edit", false)]
    [InlineData("Translate the selection from https://docs.google.com/document/d/doc_A-1/edit", false)]
    [InlineData("Read https://docs.google.com.evil.test/document/d/doc_A-1/edit", false)]
    [InlineData("Read https://docs.google.com@evil.test/document/d/doc_A-1/edit", false)]
    [InlineData("Read https://docs.google.com/spreadsheets/d/doc_A-1/edit", false)]
    [InlineData("Read https://docs.google.com/document/d/doc_A-1/edit and https://example.com", false)]
    [InlineData("Explain the phrase 'read https://docs.google.com/document/d/doc_A-1/edit'", false)]
    [InlineData("What is two plus two?", false)]
    public void Only_a_clear_readonly_human_Doc_link_has_the_narrow_alternative(string request, bool expected)
    {
        Assert.Equal(expected, JarvisConnectorController.TryGetExplicitDocumentRead(request, out var id));
        Assert.Equal(expected ? "doc_A-1" : "", id);
    }

    [Fact]
    public async Task Wrong_browser_proposal_runs_the_same_owned_Docs_read_not_browser_or_model_arguments()
    {
        var f = new Fixture { User = "Read this Google Doc https://docs.google.com/document/d/exactHumanId/edit. Do not change it.", ReadCapability = "docs_read" };
        var reply = await f.Controller.TryReadExplicitDocumentAsync("browser_automation", default);
        Assert.True((bool)reply["handled_by_connector"]); Assert.False((bool)reply["browser_action_dispatched"]);
        Assert.Equal("googledocs", f.ResolvedPayload.GetProperty("connectorId").GetString());
        Assert.Equal("exactHumanId", f.ExecutedArguments.GetProperty("id").GetString());
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("open_app", "owner-a")]
    [InlineData("answer_question", "owner-a")]
    [InlineData("get_selected_text", "owner-a")]
    [InlineData("browser_automation", "")]
    public async Task Non_connector_tools_or_signed_out_user_do_no_lookup(string tool, string owner)
    {
        var f = new Fixture { User = "Read https://docs.google.com/document/d/exactHumanId/edit", Owner = owner };
        Assert.Null(await f.Controller.TryReadExplicitDocumentAsync(tool, default));
        Assert.Equal(0, f.Resolves); Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Missing_Docs_connection_preserves_browser_fallback_without_running_a_read()
    {
        var f = new Fixture { User = "Read https://docs.google.com/document/d/exactHumanId/edit", ResolveState = "account_required", EmptyChoices = true };
        Assert.Null(await f.Controller.TryReadExplicitDocumentAsync("browser_automation", default));
        Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Executes); Assert.Equal(1, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Ambiguous_account_uses_existing_choices_and_shutdown_runs_no_Doc_lookup()
    {
        var f = new Fixture { User = "Read https://docs.google.com/document/d/exactHumanId/edit", ResolveState = "account_required" };
        var result = await f.Controller.TryReadExplicitDocumentAsync("browser_automation", default);
        Assert.NotNull(result); Assert.Equal(0, f.Executes); Assert.Equal(1, f.Resolves);
        await f.Controller.ResetAsync(); f.Session.Cancel();
        Assert.Null(await f.Controller.TryReadExplicitDocumentAsync("browser_automation", default));
        Assert.Equal(1, f.Resolves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_bounded_evidence_is_explicit_in_the_connector_receipt_not_claimed_as_a_full_read(bool partial)
    {
        var f = new Fixture { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { source = "external_untrusted", truncated = partial, data = new { } } }));
        var reply = await f.Execute(); Assert.True((bool)reply["ok"]);
        Assert.Equal(partial, reply.TryGetValue("partial", out var flag) && flag is true);
        if (partial) Assert.Contains("partial", (string)reply["instruction"], StringComparison.OrdinalIgnoreCase);
        await f.Controller.ResetAsync();
    }
    [Fact]
    public async Task A_later_complete_step_cannot_erase_an_earlier_partial_read_in_the_same_task()
    {
        var f = new Fixture { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { source = "external_untrusted", truncated = true, data = new { } } }));
        await f.Execute();
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { source = "external_untrusted", truncated = false, data = new { } } }));
        var reply = await f.Step("mail_search", "{\"query\":\"another requested topic\"}");
        Assert.True((bool)reply["ok"]); Assert.True(reply.TryGetValue("task_partial", out var flag) && flag is true);
        Assert.False(reply.TryGetValue("partial", out var currentPartial) && currentPartial is true);
        Assert.Contains("earlier", (string)reply["instruction"], StringComparison.OrdinalIgnoreCase);
        await f.Controller.ResetAsync();
        f.Pending = null; await f.Resolve(new { connector_id = "gmail" });
        Assert.False((await f.Execute()).ContainsKey("partial"));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_page_with_more_matches_is_not_complete_count_evidence()
    {
        var f = new Fixture { User = "How many notification emails do I have?", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { source = "external_untrusted", truncated = false, data = new { pageItemCount = 50, hasMore = true } } }));
        var reply = await f.Execute();
        Assert.True(reply.TryGetValue("partial", out var flag) && flag is true);
        Assert.Contains("pageItemCount", (string)reply["instruction"]);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_search_page_is_never_a_verified_total_and_its_content_coverage_is_separate()
    {
        var f = new Fixture { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { queryUsed = "topic", pageItemCount = 5, hasMore = true,
                contentSample = new { complete = true, returned = 1, mode = "thread" } } } }));
        var result = await f.Execute();
        var coverage = JsonSerializer.SerializeToElement(result["step_coverage"]);
        Assert.Equal("mail_search_page", coverage.GetProperty("kind").GetString());
        Assert.Equal(5, coverage.GetProperty("matches_in_this_page").GetInt32());
        Assert.True(coverage.GetProperty("more_matches").GetBoolean());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("verified_query_total").ValueKind);
        Assert.Equal("mail_count", coverage.GetProperty("count_capability").GetString());
        Assert.True(coverage.GetProperty("sample_content_complete").GetBoolean());
        Assert.True((bool)result["partial"]);
        Assert.True(Array.IndexOf(result.Keys.ToArray(), "step_coverage") < Array.IndexOf(result.Keys.ToArray(), "connector"));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Complete_count_is_not_mislabeled_partial_because_an_earlier_thread_was_bounded()
    {
        var f = new Fixture { User = "Count the exact query from:team@example.test", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = true, data = new { queryUsed = "from:team@example.test", contentSample = new { complete = false } } } }));
        await f.Step("mail_search", "{\"query\":\"from:team@example.test\"}");
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { queryUsed = "from:team@example.test", complete = true, matchedMessageCount = 36 } } }));
        var result = await f.Step("mail_count", "{\"query\":\"from:team@example.test\"}");
        Assert.False(result.TryGetValue("partial", out var partial) && partial is true);
        Assert.True(result.TryGetValue("task_partial", out var taskPartial) && taskPartial is true);
        Assert.Contains("earlier", (string)result["instruction"], StringComparison.OrdinalIgnoreCase);
        var evidence = JsonSerializer.SerializeToElement(result["task_evidence"]).GetProperty("steps");
        Assert.Equal(2, evidence.GetArrayLength());
        var bounded = evidence.EnumerateArray().Single(step => step.GetProperty("capability_id").GetString() == "mail_search");
        Assert.True(bounded.GetProperty("partial").GetBoolean());
        Assert.Equal("from:team@example.test", bounded.GetProperty("query").GetString());
        var counted = evidence.EnumerateArray().Single(step => step.GetProperty("capability_id").GetString() == "mail_count");
        Assert.False(counted.GetProperty("partial").GetBoolean());
        Assert.True(counted.GetProperty("count_complete").GetBoolean());
        Assert.Equal(36, counted.GetProperty("matched_messages").GetInt32());
        Assert.Equal("from:team@example.test", counted.GetProperty("query").GetString());
        Assert.Equal(f.Id, result["task_id"]);
        Assert.True(Array.IndexOf(result.Keys.ToArray(), "task_evidence") < Array.IndexOf(result.Keys.ToArray(), "connector"));
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_scope_exposes_a_total_only_when_that_query_is_verified_complete(bool complete)
    {
        var f = new Fixture { User = "Count the exact query from:team@example.test", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = !complete, data = new { queryUsed = "from:team@example.test", complete, matchedMessageCount = 36 } } }));
        var result = await f.Step("mail_count", "{\"query\":\"from:team@example.test\"}");
        var coverage = JsonSerializer.SerializeToElement(result["step_coverage"]);
        Assert.Equal("mail_query_count", coverage.GetProperty("kind").GetString());
        Assert.Equal(complete, coverage.GetProperty("count_complete").GetBoolean());
        if (complete) Assert.Equal(36, coverage.GetProperty("verified_query_total").GetInt32());
        else
        {
            Assert.Equal(JsonValueKind.Null, coverage.GetProperty("verified_query_total").ValueKind);
            Assert.Equal(36, coverage.GetProperty("lower_bound").GetInt32());
        }
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task An_inferred_unsupported_sender_filter_is_rejected_before_it_can_report_false_absence()
    {
        var f = new Fixture { User = "Find my winning email notifications" }; await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_search", "{\"query\":\"sender:Example subject:winner\"}");
        Assert.Equal("invalid_query", reply["state"]); Assert.False((bool)reply["this_proposal_dispatched"]);
        Assert.Contains("from:", (string)reply["validation"]); Assert.Equal(0, f.Executes);
        Assert.True((bool)(await f.Step("mail_search", "{\"query\":\"from:Example subject:winner\"}"))["ok"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Use the exact query: sender:Example", "sender:Example")]
    [InlineData("Find relevant mail", "\"mentions sender:Example in text\"")]
    public async Task Explicit_queries_and_quoted_body_words_are_not_rewritten_as_sender_filters(string user, string query)
    {
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)(await f.Step("mail_search", JsonSerializer.Serialize(new { query })))["ok"]);
        Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString()); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Read my email and summarize the conversation with a colleague", "slack", 1)]
    [InlineData("Read my Gmail and summarize a conversation", "discord", 1)]
    [InlineData("Read my email and check Slack", "slack", 2)]
    [InlineData("Read my email and check my Google Calendar", "googlecalendar", 2)]
    [InlineData("Read my email and check Calendar", "googlecalendar", 2)]
    public async Task Same_mail_request_keeps_its_owned_scope_unless_another_service_is_actually_named(string user, string selected, int resolves)
    {
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = "gmail" });
        var first = f.Id; var reply = await f.Resolve(new { connector_id = selected });
        Assert.Equal(resolves, f.Resolves);
        if (resolves == 1) { Assert.Equal(first, f.Id); Assert.Contains("Gmail", (string)reply["instruction"]); }
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Earlier_mail_scope_does_not_redirect_a_fresh_request_or_named_account()
    {
        var f = new Fixture { User = "Read my email" }; await f.Resolve(new { connector_id = "gmail" });
        f.User = "Summarize a conversation"; f.Identity = "second-human-turn";
        await f.Resolve(new { connector_id = "slack" }); Assert.Equal(2, f.Resolves);
        f.User = "Read my email"; f.Identity = "first-user-turn";
        await f.Resolve(new { connector_id = "slack", account_id = Guid.NewGuid().ToString("N") }); Assert.Equal(3, f.Resolves);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("googlecalendar", "What is on my Google Calendar tomorrow?")]
    [InlineData("googledrive", "Find files in my Google Drive with keyboard in the name.")]
    [InlineData("googledocs", "Read my Google document")]
    [InlineData("googleforms", "Read my Google form")]
    [InlineData("youtube", "Find my YouTube videos")]
    [InlineData("github", "Read my GitHub issues")]
    [InlineData("slack", "Find my Slack messages")]
    [InlineData("discord", "List my Discord servers")]
    public async Task Gmail_only_planning_field_does_not_block_or_rewrite_another_service(string connector, string user)
    {
        foreach (var irrelevantPlan in new[] { "", "[]", "null", "not JSON",
            "[{\"request\":\"send an invented email\",\"kind\":\"write\"}]" })
        {
            var f = new Fixture { User = user };
            var reply = await f.Resolve(new { connector_id = connector, read_plan_json = irrelevantPlan });
            Assert.True((bool)reply["ok"]); Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Executes);
            Assert.Equal(user, f.ResolvedRequest);
            Assert.Equal(connector, f.ResolvedPayload.GetProperty("connectorId").GetString());
            Assert.Equal(JsonValueKind.Null, f.ResolvedPayload.GetProperty("accountId").ValueKind);
            Assert.False(f.ResolvedPayload.TryGetProperty("read_plan_json", out _));
            Assert.False(reply.ContainsKey("read_plan"));
            await f.Controller.ResetAsync();
        }
    }

    [Theory]
    [InlineData("Check my Gmail and tell me if I have anything about the Codex Community event.")]
    [InlineData("Summarize my conversation with Daisy Edwards.")]
    [InlineData("How many Woo hoo winning emails do I have from Devpost?")]
    [InlineData("Find unread emails")]
    public async Task Ordinary_mail_requests_use_full_human_intent_not_an_unneeded_model_partition(string user)
    {
        Assert.False(ConnectorMailReadPlan.Required(user));
        foreach (var irrelevantPlan in new[] { "", "[]", "null", "not JSON",
            "[{\"request\":\"anything about the Codex Community event\",\"kind\":\"count\"}]",
            "[{\"request\":\"Search for a different event\",\"kind\":\"read\"}]",
            "[{\"request\":\"send an invented email\",\"kind\":\"write\"}]" })
        {
            var f = new Fixture { User = user };
            var reply = await f.Resolve(new { connector_id = "gmail", read_plan_json = irrelevantPlan });
            Assert.True((bool)reply["ok"]); Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Executes);
            Assert.Equal(user, f.ResolvedRequest);
            Assert.False(f.ResolvedPayload.TryGetProperty("read_plan_json", out _));
            Assert.False(reply.ContainsKey("read_plan"));
            await f.Controller.ResetAsync();
        }
    }

    [Theory]
    [InlineData("googlecalendar")]
    [InlineData("googledrive")]
    [InlineData("gmail")]
    public async Task Irrelevant_mail_plan_does_not_weaken_account_reference_validation(string connector)
    {
        var f = new Fixture { User = "Read my connected service" };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Resolve(new
            { connector_id = connector, account_id = "default", read_plan_json = "[]" }));
        Assert.Equal(0, f.Resolves); Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }
    [Theory]
    [InlineData("Summarize my conversation with a colleague", "thread")]
    [InlineData("Read the latest event email and give its details", "message")]
    [InlineData("Summarize the thread, but don't send anything", "thread")]
    [InlineData("Find project launch emails", "preview")]
    [InlineData("Count my winning notifications", "preview")]
    [InlineData("Count weekly summary emails", "preview")]
    [InlineData("Find unread emails", "preview")]
    [InlineData("Find emails containing the word \"summarize\"", "preview")]
    [InlineData("Don't read message bodies, just show the subjects", "preview")]
    [InlineData("Read only the headers for this conversation", "preview")]
    public async Task Requested_mail_content_is_frozen_human_intent_not_a_model_or_later_turn(string user, string mode)
    {
        var f = new Fixture { User = user, DeclareReadMode = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.User = "Read someone else's conversation";
        await f.Step("mail_search", "{\"query\":\"topic\",\"readMode\":\"invented\"}");
        Assert.Equal(mode, f.ExecutedArguments.GetProperty("readMode").GetString());
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Clear_mixed_mail_parts_do_not_need_an_extra_model_plan_or_confirmation()
    {
        var f = new Fixture { User = PlannedRequest };
        var reply = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)reply["ok"]); Assert.Equal(3, Plan(reply).GetProperty("goals").GetArrayLength());
        Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Executes);
        await ResolvePlanned(f); Assert.Equal(1, f.Resolves);
        var replay = await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal(1, f.Resolves); Assert.Equal(f.Id, replay["task_id"]);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("googlecalendar", "calendar_list", "pageToken", "\"opaque\"", "{\"start\":\"2026-10-10T00:00:00+05:30\",\"end\":\"2026-10-11T00:00:00+05:30\"}")]
    [InlineData("googledrive", "drive_search", "pageToken", "\"opaque\"", "{\"query\":\"keyboard\"}")]
    [InlineData("youtube", "youtube_search", "pageToken", "\"opaque\"", "{\"query\":\"keyboard\"}")]
    [InlineData("github", "github_repos", "page", "2", "{}")]
    [InlineData("github", "github_issues", "page", "2", "{\"repository\":\"owner/repo\"}")]
    [InlineData("slack", "slack_search", "page", "2", "{\"query\":\"keyboard\"}")]
    [InlineData("slack", "slack_history", "cursor", "\"opaque\"", "{\"channel\":\"C123\"}")]
    [InlineData("discord", "discord_guilds", "after", "\"12345678901234567\"", "{}")]
    public async Task List_continuation_requires_verified_same_task_capability_and_unchanged_scope(string connector, string capability, string paging, string cursorJson, string scopeJson)
    {
        var f = new Fixture { User = "Read connected " + connector + " records", ReadCapability = capability };
        await f.Resolve(new { connector_id = connector });
        var scope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(scopeJson);
        var continued = new Dictionary<string, JsonElement>(scope) { [paging] = JsonSerializer.Deserialize<JsonElement>(cursorJson) };
        Assert.Equal("list_continuation_required", (await f.Step(capability, JsonSerializer.Serialize(continued)))["state"]);
        Assert.Equal(0, f.Executes);
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { source = "external_untrusted", truncated = true, coverage = new
                { next = new Dictionary<string, JsonElement> { [paging] = continued[paging] }, scopeArguments = scope }, data = Array.Empty<object>() } }));
        var first = await f.Step(capability, scopeJson);
        Assert.True((bool)first["partial"]); Assert.True(first.ContainsKey("continuation"));
        var wrong = new Dictionary<string, JsonElement>(continued);
        var key = scope.Keys.FirstOrDefault() ?? "query"; wrong[key] = JsonSerializer.SerializeToElement("changed");
        Assert.Equal("list_continuation_required", (await f.Step(capability, JsonSerializer.Serialize(wrong)))["state"]);
        Assert.Equal(1, f.Executes);
        Assert.True((bool)(await f.Step(capability, JsonSerializer.Serialize(continued)))["ok"]); Assert.Equal(2, f.Executes);
        var next = JsonSerializer.SerializeToElement(first["continuation"]);
        Assert.Equal(f.Id, next.GetProperty("task_id").GetString());
        Assert.Equal(capability, next.GetProperty("capability_id").GetString());
        await f.Controller.ResetAsync();
        await f.Resolve(new { connector_id = connector });
        Assert.Equal("list_continuation_required", (await f.Step(capability, JsonSerializer.Serialize(continued)))["state"]);
        Assert.Equal(2, f.Executes); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Failed_list_response_cannot_grant_continuation_or_publish_success()
    {
        var f = new Fixture { User = "Read GitHub repositories", ReadCapability = "github_repos" };
        await f.Resolve(new { connector_id = "github" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "failed", verified = false,
            result = new { coverage = new { next = new { page = 2 }, scopeArguments = new { } }, data = Array.Empty<object>() } }));
        var result = await f.Step("github_repos", "{}"); Assert.False((bool)result["ok"]); Assert.False(result.ContainsKey("continuation"));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task An_uncertain_partition_still_requires_a_valid_plan_before_any_http_request()
    {
        var f = new Fixture { User = "Tell me details of the event, a summary of a conversation, with the number of notification emails." };
        var reply = await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal("read_plan_required", reply["state"]); Assert.Equal(0, f.Resolves);
        Assert.False((bool)reply["this_proposal_dispatched"]); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Planned_parts_keep_frozen_request_modes_and_do_not_inherit_thread_reading_for_counts()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true };
        await ResolvePlanned(f); f.User = "Read a different person's private thread";
        PreparedRead(f, "message", "event");
        var first = await f.Step("mail_search", "{\"query\":\"event\",\"readMode\":\"thread\"}", "g1");
        Assert.Equal("message", f.ExecutedArguments.GetProperty("readMode").GetString());
        Assert.Equal("topic", f.ExecutedArguments.GetProperty("matchMode").GetString());
        Assert.Equal("g2", Plan(first).GetProperty("next_goal").GetString());
        PreparedRead(f, "thread", "colleague", partial: true);
        await f.Step("mail_search", "{\"query\":\"colleague\"}", "g2");
        Assert.Equal("thread", f.ExecutedArguments.GetProperty("readMode").GetString());
        PreparedRead(f, "preview", "notifications");
        var preview = await f.Step("mail_search", "{\"query\":\"notifications\",\"readMode\":\"thread\"}", "g3");
        Assert.Equal("preview", f.ExecutedArguments.GetProperty("readMode").GetString());
        Assert.Equal("g3", Plan(preview).GetProperty("next_goal").GetString());
        Assert.False(Plan(preview).GetProperty("all_parts_have_evidence").GetBoolean());
        Assert.Equal(3, f.Executes); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_preview_cannot_finish_the_planned_count_but_a_verified_count_can()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true };
        await ResolvePlanned(f); await PrepareFirstGoals(f);
        PreparedRead(f, "preview", "notifications");
        await f.Step("mail_search", "{\"query\":\"notifications\"}", "g3");
        var blocked = await f.Control("complete");
        Assert.Equal("read_plan_incomplete", blocked["state"]); Assert.Equal(0, f.Cleanups);
        Assert.Equal("g3", Plan(blocked).GetProperty("next_goal").GetString());
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { queryUsed = "from:updates@example.test subject:winner",
                complete = true, matchedMessageCount = 36 } } }));
        var counted = await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:winner\"}", "g3");
        var plan = Plan(counted);
        Assert.True(plan.GetProperty("all_parts_have_evidence").GetBoolean());
        Assert.True(plan.GetProperty("partial").GetBoolean()); // Earlier bounded thread, not the count.
        Assert.Equal(36, plan.GetProperty("goals")[2].GetProperty("verified_total").GetInt64());
        Assert.False(counted.TryGetValue("partial", out var partial) && partial is true);
        Assert.True((bool)(await f.Control("complete"))["ok"]); Assert.Equal(1, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("g2")]
    [InlineData("g3")]
    [InlineData("invented")]
    public async Task Missing_or_skipped_goal_cannot_dispatch_an_operation(string goal)
    {
        var f = new Fixture { User = PlannedRequest }; await ResolvePlanned(f);
        var result = await f.Step("mail_search", "{\"query\":\"event\"}", goal);
        Assert.Equal("read_plan_incomplete", result["state"]); Assert.Equal(0, f.Executes);
        Assert.Equal("g1", Plan(result).GetProperty("next_goal").GetString());
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Samples_from_a_different_plan_part_cannot_ground_the_count()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        await PrepareFirstGoals(f); // Their headers have updates@example.test + winner.
        var result = await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:winner\"}", "g3");
        Assert.Equal("evidence_required", result["state"]); Assert.Equal(2, f.Executes);
        Assert.Empty((Array)result["observedPatterns"]); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_partial_count_is_only_a_lower_bound_not_a_fabricated_verified_total()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        await PrepareFirstGoals(f); PreparedRead(f, "preview", "notifications");
        await f.Step("mail_search", "{\"query\":\"notifications\"}", "g3");
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = true, data = new { queryUsed = "from:updates@example.test subject:winner",
                complete = false, matchedMessageCount = 500, nextPageToken = "bounded-read-cursor" } } }));
        var result = await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:winner\"}", "g3");
        var plan = Plan(result); var count = plan.GetProperty("goals")[2];
        Assert.Equal("count_partial", count.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, count.GetProperty("verified_total").ValueKind);
        Assert.Equal(500, count.GetProperty("lower_bound").GetInt64());
        Assert.True(plan.GetProperty("partial").GetBoolean());
        Assert.True((bool)(await f.Control("complete"))["ok"]); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task An_empty_or_malformed_body_cannot_satisfy_a_requested_content_read()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { pageItemCount = 5, hasMore = false, contentSample = new { mode = "message", returned = 1 },
                sampledContent = new { result = new { data = new { text = "" } } } } } }));
        var result = await f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        Assert.Equal("g1", Plan(result).GetProperty("next_goal").GetString());
        Assert.False((bool)(await f.Control("complete"))["ok"]); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Quoted_body_words_are_not_misclassified_as_invented_sender_filters()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        PreparedRead(f, "message", "\"mentions from:invented@example.test here\"");
        Assert.True((bool)(await f.Step("mail_search", "{\"query\":\"\\\"mentions from:invented@example.test here\\\"\"}", "g1"))["ok"]);
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Inferred_notification_count_can_prepare_an_exact_observed_bare_subject_phrase_without_guessing()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        await PrepareFirstGoals(f); PreparedRead(f, "preview", "notifications");
        await f.Step("mail_search", "{\"query\":\"notifications\"}", "g3");
        await f.Step("mail_count", "{\"query\":\"from:updates@example.test \\\"Congrats, winner!\\\"\"}", "g3");
        Assert.Equal("from:updates@example.test subject:\"Congrats, winner!\"", f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal(4, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Count the exact query from:updates@example.test \"Congrats, winner!\"", "from:updates@example.test \"Congrats, winner!\"")]
    [InlineData("Count notifications whose body contains winner", "from:updates@example.test \"Congrats, winner!\"")]
    [InlineData("Count my notifications", "from:invented@example.test \"Congrats, winner!\"")]
    [InlineData("Count my notifications", "from:updates@example.test \"Unobserved phrase\"")]
    [InlineData("Count my notifications", "from:updates@example.test \"Congrats, winner!\" before:2026/01/01")]
    public async Task Subject_preparation_does_not_rewrite_human_literals_body_queries_or_unobserved_filters(string user, string query)
    {
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = "gmail" });
        var search = user.StartsWith("Count the exact", StringComparison.Ordinal) ? query : "notifications";
        PreparedRead(f, "preview", search); await f.Step("mail_search", JsonSerializer.Serialize(new { query = search }));
        var reply = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        if (user.StartsWith("Count the exact", StringComparison.Ordinal))
        { Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString()); Assert.Equal(2, f.Executes); }
        else { Assert.Equal("evidence_required", reply["state"]); Assert.Equal(1, f.Executes); }
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("from:Updates subject:winner", "from:updates@example.test subject:winner")]
    [InlineData("from:Updates \"Congrats, winner!\"", "from:updates@example.test subject:\"Congrats, winner!\"")]
    [InlineData("\"Congrats, winner!\" from:Updates", "from:updates@example.test subject:\"Congrats, winner!\"")]
    [InlineData("\"Congrats, winner!\" from:updates@example.test", "from:updates@example.test subject:\"Congrats, winner!\"")]
    public async Task Inferred_count_resolves_a_user_named_alias_only_from_its_own_observed_sender_and_subject(string query, string expected)
    {
        var f = new Fixture { User = PlannedRequest.Replace("winning notification", "Updates winning notification"), DeclareReadMode = true };
        await ResolvePlanned(f); await PrepareFirstGoals(f);
        PreparedRead(f, "preview", "notifications"); await f.Step("mail_search", "{\"query\":\"notifications\"}", "g3");
        await f.Step("mail_count", JsonSerializer.Serialize(new { query }), "g3");
        Assert.Equal(expected, f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal(4, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Count my Updates winning notifications", "from:Updates subject:winner", false, true)]
    [InlineData("Count my Updates winning notifications", "from:Updates subject:unobserved", false, false)]
    [InlineData("Count my winning notifications", "from:Updates subject:winner", false, false)]
    [InlineData("Count my Updates notifications whose body contains winner", "from:Updates subject:winner", false, false)]
    [InlineData("Count my Updates winning notifications", "from:Updates subject:winner before:2026/01/01", false, false)]
    [InlineData("Count the exact query from:Updates subject:winner", "from:Updates subject:winner", true, false)]
    [InlineData("Count my Updates winning notifications", "\"Congrats, winner!\" from:Updates", false, true)]
    [InlineData("Count my Updates winning notifications", "\"Unobserved phrase\" from:Updates", false, false)]
    [InlineData("Count my Updates winning notifications", "\"Congrats, winner!\" from:Updates OR from:other@example.test", false, false)]
    [InlineData("Count the exact query \"Congrats, winner!\" from:Updates", "\"Congrats, winner!\" from:Updates", true, false)]
    public async Task Sender_alias_does_not_guess_ambiguous_unobserved_or_unrequested_filters(string user, string query, bool literal, bool ambiguous)
    {
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "preview", literal ? query : "notifications", ambiguousSender: ambiguous);
        await f.Step("mail_search", JsonSerializer.Serialize(new { query = literal ? query : "notifications" }));
        var reply = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        if (literal) { Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString()); Assert.Equal(2, f.Executes); }
        else { Assert.Equal("evidence_required", reply["state"]); Assert.Equal(1, f.Executes); }
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_fully_settled_mixed_plan_still_exposes_partial_answer_scope_before_external_content()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        await PrepareFirstGoals(f); PreparedRead(f, "preview", "notifications");
        await f.Step("mail_search", "{\"query\":\"notifications\"}", "g3");
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { complete = true, matchedMessageCount = 36 } } }));
        var result = await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:winner\"}", "g3");
        var plan = Plan(result);
        Assert.True(plan.GetProperty("all_parts_have_evidence").GetBoolean());
        Assert.False(plan.GetProperty("all_reads_fully_complete").GetBoolean());
        Assert.Equal("partial", plan.GetProperty("result_scope").GetString());
        Assert.Contains("not fully read", plan.GetProperty("answer_notice").GetString());
        Assert.Contains("never say all requested reads", (string)result["instruction"]);
        Assert.Equal(36, plan.GetProperty("goals")[2].GetProperty("verified_total").GetInt32());
        Assert.False((bool)result["partial"]);
        Assert.True(Array.IndexOf(result.Keys.ToArray(), "read_plan") < Array.IndexOf(result.Keys.ToArray(), "connector"));
        var completed = await f.Control("complete");
        Assert.Equal("partial", Plan(completed).GetProperty("result_scope").GetString());
        Assert.False(Plan(completed).GetProperty("all_reads_fully_complete").GetBoolean());
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Search_continuation_requires_the_actual_query_that_created_its_cursor()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = "actual topic", nextPageToken = "owned-cursor", pageItemCount = 50 } } }));
        await f.Step("mail_search", "{\"query\":\"original inferred topic\"}");
        var rejected = await f.Step("mail_search", "{\"query\":\"original inferred topic\",\"pageToken\":\"owned-cursor\"}");
        Assert.Equal("search_continuation_required", rejected["state"]);
        Assert.Equal("actual topic", rejected["query_used"]); Assert.Equal(1, f.Executes);
        Assert.True((bool)(await f.Step("mail_search", "{\"query\":\"actual topic\",\"pageToken\":\"owned-cursor\"}"))["ok"]);
        Assert.Equal(2, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("unknown-cursor")]
    [InlineData("null")]
    public async Task A_search_cursor_cannot_be_invented_or_borrowed_from_another_goal(string cursor)
    {
        var f = new Fixture { User = PlannedRequest }; await ResolvePlanned(f);
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = "event", nextPageToken = "unknown-cursor", pageItemCount = 50,
                contentSample = new { mode = "message", returned = 1, complete = true },
                sampledContent = new { result = new { data = new { text = "Verified latest event details" } } } } } }));
        await f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        var result = await f.Step("mail_search", JsonSerializer.Serialize(new { query = "event", pageToken = cursor }), "g2");
        Assert.Equal("search_continuation_required", result["state"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Count_can_inspect_requested_headers_then_count_on_the_same_owner_without_another_model_turn()
    {
        var f = new Fixture { User = "How many Updates winner notification emails do I have?", DeclareReadMode = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", "from:Updates subject:winner"));
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        var result = await f.Step("mail_count", "{\"query\":\"from:Updates subject:winner\"}");
        Assert.True((bool)result["ok"]); Assert.Equal(2, f.Executes); Assert.Equal(1, f.Resolves);
        Assert.Equal("from:updates@example.test subject:winner", f.ExecutedArguments.GetProperty("query").GetString());
        Assert.False(f.WorkToken.IsCancellationRequested); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_probe_cannot_count_mismatched_or_ambiguous_headers_and_is_not_repeated(bool ambiguous)
    {
        var f = new Fixture { User = "Count my Updates winner notifications" }; await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", "from:Updates subject:winner", ambiguousSender: ambiguous,
            subject: ambiguous ? "Congrats, winner!" : "Unrelated account update"));
        for (var repeat = 0; repeat < 2; repeat++)
            Assert.Equal("evidence_required", (await f.Step("mail_count", "{\"query\":\"from:Updates subject:winner\"}"))["state"]);
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("from:invented@example.test subject:winner")]
    [InlineData("from:Updates subject:unrequested")]
    [InlineData("from:Updates subject:winner OR from:other@example.test")]
    [InlineData("from:Updates subject:winner before:2026/01/01")]
    public async Task Count_probe_never_discovers_guessed_addresses_extra_filters_or_unrequested_subjects(string query)
    {
        var f = new Fixture { User = "Count my Updates winner notifications" }; await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal("evidence_required", (await f.Step("mail_count", JsonSerializer.Serialize(new { query })))["state"]);
        Assert.Equal(0, f.Executes); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_model_count_proposal_cannot_automatically_search_when_the_human_only_requested_a_read()
    {
        var f = new Fixture { User = "Read my Updates winner emails" }; await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal("evidence_required", (await f.Step("mail_count", "{\"query\":\"from:Updates subject:winner\"}"))["state"]);
        Assert.Equal(0, f.Executes); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Cancel_during_a_count_evidence_probe_prevents_the_count_and_late_completion()
    {
        var f = new Fixture { User = "Count my Updates winner notifications", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        var pending = f.Step("mail_count", "{\"query\":\"from:Updates subject:winner\"}");
        f.User = "Cancel that";
        await f.Control("cancel"); var result = await pending;
        Assert.False((bool)result["ok"]); Assert.Equal(1, f.Executes); Assert.True(f.WorkToken.IsCancellationRequested);
        Assert.Equal(0, f.Notices); Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(2)));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task An_interrupted_proposal_does_not_cancel_the_owned_evidence_read_or_require_a_new_task()
    {
        var f = new Fixture { User = "Count my Updates winner notifications", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" }); using var speech = new CancellationTokenSource();
        var pending = f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, capability_id = "mail_count", arguments_json = "{\"query\":\"from:Updates subject:winner\"}" }), false, speech.Token);
        speech.Cancel(); Assert.True((bool)(await pending)["started"]); Assert.False(f.WorkToken.IsCancellationRequested);
        f.Pending.SetResult(PreparedReadReply("preview", "from:Updates subject:winner"));
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        Assert.True((bool)(await f.Step("mail_count", "{\"query\":\"from:Updates subject:winner\"}"))["ok"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(2, f.Executes); await f.Controller.ResetAsync();
    }

    private static JsonElement VerifiedCountReply(int count) => JsonSerializer.SerializeToElement(new
        { state = "completed", verified = true, result = new { truncated = false, data = new { complete = true, matchedMessageCount = count } } });

    [Fact]
    public async Task Sender_alias_cannot_borrow_headers_from_another_read_goal()
    {
        var f = new Fixture { User = PlannedRequest.Replace("winning notification", "Updates winning notification"), DeclareReadMode = true };
        await ResolvePlanned(f); await PrepareFirstGoals(f);
        var reply = await f.Step("mail_count", "{\"query\":\"from:Updates subject:winner\"}", "g3");
        Assert.Equal("evidence_required", reply["state"]); Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Invented_sender_address_is_rejected_but_provider_name_and_observed_address_remain_usable()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        var rejected = await f.Step("mail_search", "{\"query\":\"from:invented@example.test\"}", "g1");
        Assert.Equal("read_plan_incomplete", rejected["state"]); Assert.Equal(0, f.Executes);
        PreparedRead(f, "message", "from:Example");
        Assert.True((bool)(await f.Step("mail_search", "{\"query\":\"from:Example\"}", "g1"))["ok"]);
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Account_change_keeps_the_user_parts_but_discards_old_account_evidence()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        PreparedRead(f, "message", "event"); await f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        var oldId = f.Id; f.User = "Use my work account";
        var changed = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = oldId, action = "change_account", account_id = Guid.NewGuid().ToString("N") }), true, default);
        Assert.Equal("g1", Plan(changed).GetProperty("next_goal").GetString());
        Assert.Equal(3, Plan(changed).GetProperty("goals").GetArrayLength());
        Assert.NotEqual(oldId, f.Id); Assert.Equal(1, f.Rebinds); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Spoken_answer_cannot_leave_an_unfinished_plan_routable_by_later_speech()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        PreparedRead(f, "message", "event"); await f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(1, f.VisualNotices);
        Assert.Contains("incomplete", f.VisualText, StringComparison.OrdinalIgnoreCase);
        f.User = "Open Spotify"; f.Identity = "new-turn";
        Assert.False((bool)(await f.Step("mail_search", "{\"query\":\"event\"}", "g1"))["ok"]);
        Assert.Equal(1, f.Executes); Assert.False(f.Controller.TryCancelSingleTask("cancel", out _));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Explicit_cancel_clears_the_planned_owner_and_a_late_result_cannot_publish()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true,
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await ResolvePlanned(f);
        var pending = f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        f.User = "Cancel that";
        await f.Control("cancel"); var result = await pending;
        Assert.False((bool)result["ok"]); Assert.True(f.WorkToken.IsCancellationRequested);
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, f.Notices); await f.Controller.ResetAsync();
    }

    private const string PlannedRequest = "Read the launch event details. Summarize my conversation with a colleague. Finally count the winning notification emails.";
    private static Task<Dictionary<string, object>> ResolvePlanned(Fixture f) => f.Resolve(new { connector_id = "gmail",
        read_plan_json = JsonSerializer.Serialize(new[]
        {
            new { request = "Read the launch event details", kind = "read" },
            new { request = "Summarize my conversation with a colleague", kind = "thread" },
            new { request = "count the winning notification emails", kind = "count" }
        }) });
    private static JsonElement Plan(Dictionary<string, object> receipt) => JsonSerializer.SerializeToElement(receipt["read_plan"]);
    private static void PreparedRead(Fixture f, string mode, string query, bool partial = false, bool ambiguousSender = false)
    {
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pending.SetResult(PreparedReadReply(mode, query, partial, ambiguousSender));
    }
    private static JsonElement PreparedReadReply(string mode, string query, bool partial = false, bool ambiguousSender = false, string subject = "Congrats, winner!") =>
        JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = partial, data = new { queryUsed = query, pageItemCount = 3, hasMore = true,
                contentSample = new { mode, returned = mode == "preview" ? 0 : 1, complete = !partial },
                sampledContent = new { result = new { data = new { text = "Latest relevant event details",
                    messages = new[] { new { text = "Latest relevant conversation message" } } } } },
                sampledMessages = ambiguousSender
                    ? new[] { new { from = "Updates <updates@example.test>", subject }, new { from = "Updates <other@example.test>", subject } }
                    : new[] { new { from = "Updates <updates@example.test>", subject } } } } });
    private static async Task PrepareFirstGoals(Fixture f)
    {
        PreparedRead(f, "message", "event"); await f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        PreparedRead(f, "thread", "colleague", partial: true); await f.Step("mail_search", "{\"query\":\"colleague\"}", "g2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_partial_thread_has_one_visual_coverage_notice_when_authority_ends(bool explicitComplete)
    {
        var f = new Fixture { User = "Summarize my conversation with a colleague", DeclareReadMode = true };
        await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "thread", "colleague", partial: true);
        await f.Step("mail_search", "{\"query\":\"colleague\"}");
        if (explicitComplete) Assert.True((bool)(await f.Control("complete"))["ok"]);
        else f.Controller.CompleteSettledUserTurn(f.Identity);
        f.Controller.CompleteSettledUserTurn(f.Identity); await f.Control("complete");
        Assert.Equal(1, f.VisualNotices); Assert.Contains("partial", f.VisualText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("colleague", f.VisualText); Assert.Equal(1, f.Cleanups);
        Assert.False(f.Controller.TryCancelSingleTask("cancel", out _)); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("preview", true, true)]
    [InlineData("thread", false, true)]
    [InlineData("thread", true, false)]
    public async Task Partial_content_notice_does_not_come_from_preview_complete_or_unverified_content(string mode, bool partial, bool verified)
    {
        var f = new Fixture { User = "Find recent emails" }; await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified,
            result = new { truncated = partial, data = new { contentSample = new { mode, complete = !partial } } } }));
        var reply = await f.Execute(); Assert.False(reply.ContainsKey("answer_notice"));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(0, f.VisualNotices); Assert.Equal(1, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Cancelled_partial_thread_has_no_late_coverage_notice()
    {
        var f = new Fixture { User = "Summarize my conversation" }; await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "thread", "colleague", partial: true); await f.Execute();
        f.User = "Cancel that";
        Assert.False((await f.Control("cancel")).ContainsKey("answer_notice")); f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(0, f.VisualNotices); Assert.Equal(1, f.Cleanups); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task More_search_matches_do_not_make_fully_read_requested_content_incomplete(bool explicitComplete)
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        PreparedRead(f, "message", "event"); await f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        PreparedRead(f, "thread", "colleague"); await f.Step("mail_search", "{\"query\":\"colleague\"}", "g2");
        PreparedRead(f, "preview", "winning"); await f.Step("mail_search", "{\"query\":\"winning\"}", "g3");
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { matchedMessageCount = 3, complete = true } } }));
        Assert.True((bool)(await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:Congrats\"}", "g3"))["ok"]);
        if (explicitComplete) Assert.True((bool)(await f.Control("complete"))["ok"]);
        else f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(0, f.VisualNotices); Assert.Equal(1, f.Cleanups); Assert.Equal(4, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Older_backend_contract_is_not_sent_a_new_content_option()
    {
        var f = new Fixture { User = "Summarize my conversation" }; await f.Resolve(new { connector_id = "gmail" });
        await f.Step("mail_search", "{\"query\":\"topic\"}");
        Assert.False(f.ExecutedArguments.TryGetProperty("readMode", out _)); await f.Controller.ResetAsync();
    }
    [Fact]
    public async Task Descriptive_count_cannot_skip_observed_search_evidence()
    {
        var f = new Fixture { User = "How many project-winning notification emails do I have?" };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_count", "{\"query\":\"project winning\"}");
        Assert.False((bool)reply["ok"]); Assert.Equal("evidence_required", reply["state"]);
        Assert.False((bool)reply["this_proposal_dispatched"]); Assert.Equal(0, f.Executes);
        Assert.Contains("mail_search", (string)reply["instruction"]);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Example \"Woo hoo\" winner")]
    [InlineData("\"Woo hoo\" winner Example")]
    [InlineData("Example subject:\"Woo hoo\" subject:winner")]
    public async Task Unscoped_inferred_count_uses_only_one_observed_sender_and_keeps_all_subject_terms(string query)
    {
        var f = new Fixture { User = "Count my Woo hoo winner notification emails from Example" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", query, subject: "Woo hoo! You are a winner!"));
        await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        var counted = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        Assert.True((bool)counted["ok"]);
        Assert.Equal("from:updates@example.test subject:\"Woo hoo\" subject:winner", f.ExecutedArguments.GetProperty("query").GetString());
        Assert.True((bool)(await f.Step("mail_count", JsonSerializer.Serialize(new { query })))["ok"]);
        Assert.Equal(2, f.Executes); Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Notices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Example \"Woo hoo\" winning", false)] // Never stem/drop an absent subject term.
    [InlineData("Example \"Woo hoo\" winner", true)] // Two observed addresses are ambiguous.
    [InlineData("Unknown \"Woo hoo\" winner", false)]
    [InlineData("Example \"Woo hoo\" winner -subject:spam", false)]
    [InlineData("Example \"Woo hoo\" OR winner", false)]
    [InlineData("Example \"Woo hoo\" winner after:2026/01/01", false)]
    [InlineData("Example \"Woo hoo\" winner body:hello", false, true)]
    [InlineData("Example \"Woo hoo\" winner {extra}", false)]
    public async Task Unscoped_count_repair_refuses_ambiguous_or_non_header_queries(string query, bool ambiguous, bool invalidField = false)
    {
        var f = new Fixture { User = "Count my Woo hoo winner notification emails from Example" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", query, ambiguousSender: ambiguous, subject: "Woo hoo! You are a winner!"));
        var search = await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        if (invalidField)
        {
            Assert.Equal("invalid_query", search["state"]);
            Assert.False((bool)search["this_proposal_dispatched"]);
        }
        var counted = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        Assert.Equal("evidence_required", counted["state"]); Assert.False((bool)counted["this_proposal_dispatched"]);
        Assert.Equal(invalidField ? 0 : 1, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unscoped_count_cannot_replace_the_humans_sender_with_a_category_word(bool ambiguousSender)
    {
        const string query = "Example \"Woo hoo\" Winner";
        var f = new Fixture { User = "Count my Woo hoo Winner notification emails from Example" };
        await f.Resolve(new { connector_id = "gmail" });
        var headers = new List<object>
        {
            new { from = "Winner <team@winner.test>", subject = "Example Woo hoo" }
        };
        if (ambiguousSender)
        {
            headers.Add(new { from = "Example <updates@example.test>", subject = "Woo hoo Winner" });
            headers.Add(new { from = "Example <other@example.test>", subject = "Woo hoo Winner" });
        }
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = query, pageItemCount = headers.Count, hasMore = false,
                contentSample = new { mode = "preview", returned = 0, complete = true }, sampledMessages = headers } } }));
        await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        var counted = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        Assert.Equal("evidence_required", counted["state"]);
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Count my emails containing Example Woo hoo winner", "evidence_required")]
    [InlineData("Count emails matching the exact query Example \"Woo hoo\" winner", "completed")]
    public async Task Body_or_literal_user_scope_never_gets_header_rewritten(string user, string state)
    {
        const string query = "Example \"Woo hoo\" winner";
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", query, subject: "Woo hoo! You are a winner!"));
        await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        var reply = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        if (state != "completed") Assert.Equal(state, reply["state"]);
        else Assert.True((bool)reply["ok"]);
        if (state == "completed") Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString());
        else Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("except certain messages")]
    [InlineData("without attachments")]
    [InlineData("from last month")]
    [InlineData("over the past two years")]
    [InlineData("from 2025")]
    [InlineData("since yesterday")]
    [InlineData("only the latest three")]
    [InlineData("in my inbox")]
    [InlineData("that are unread")]
    [InlineData("with a starred label")]
    [InlineData("matching the exact terms")]
    [InlineData("from October")]
    [InlineData("but don't include archived ones")]
    [InlineData("or OtherCorp")]
    public async Task Inferred_unscoped_repair_never_discards_a_human_qualifier(string qualifier)
    {
        const string query = "Example \"Woo hoo\" winner";
        var f = new Fixture { User = "Count my Woo hoo winner notification emails from Example " + qualifier };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", query, subject: "Woo hoo! You are a winner!"));
        await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        var reply = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        Assert.Equal("evidence_required", reply["state"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Unscoped_count_cannot_borrow_another_goals_headers()
    {
        var f = new Fixture { User = "Read event details. Count Woo hoo winner notification emails from Example.", DeclareReadMode = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("message", "event", subject: "Woo hoo! You are a winner!"));
        await f.Step("mail_search", "{\"query\":\"event\"}", "g1");
        var count = await f.Step("mail_count", "{\"query\":\"Example \\\"Woo hoo\\\" winner\"}", "g2");
        Assert.Equal("evidence_required", count["state"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Unscoped_count_never_repairs_a_search_cursor_into_a_count_cursor()
    {
        const string query = "Example \"Woo hoo\" winner";
        var f = new Fixture { User = "Count Woo hoo winner notification emails from Example" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", query, subject: "Woo hoo! You are a winner!"));
        await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        var count = await f.Step("mail_count", JsonSerializer.Serialize(new { query, pageToken = "opaque-search-cursor" }));
        Assert.Equal("count_from_beginning", count["state"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Rejected_inferred_count_uses_one_owned_interpretation_then_the_existing_count_executor()
    {
        var f = new Fixture(enableInterpretation: true) { User = "How often has Example emailed me to say I won? Count the Woo hoo winning notifications, please." };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", "winning", subject: "Woo hoo! You are a winner!"));
        await f.Step("mail_search", "{\"query\":\"winning\"}");
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        var receipt = await f.Step("mail_count", "{\"query\":\"Example winning notifications\"}");
        Assert.True((bool)receipt["ok"]); Assert.Equal(1, f.Interpretations);
        Assert.Equal(f.User, f.InterpretedInput.Request); Assert.Equal(f.LogicalLiveSessionId, f.InterpretedInput.LogicalLiveSessionId);
        Assert.Single(f.InterpretedInput.Headers); Assert.Equal("Updates <updates@example.test>", f.InterpretedInput.Headers[0].From);
        Assert.Equal("from:updates@example.test subject:\"Woo hoo\" subject:\"winner\"", f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal(2, f.Executes); Assert.Equal(0, f.Notices);
        Assert.True((bool)(await f.Control("complete"))["ok"]); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Count emails containing the words Woo hoo winning", "Example winning")]
    [InlineData("Count my Woo hoo winning emails from Example since yesterday", "Example winning")]
    [InlineData("Count my Woo hoo winning emails from Example except archived ones", "Example winning")]
    [InlineData("Count emails matching the exact query Example winning", "Example winning")]
    public async Task Count_interpretation_never_rewrites_qualified_body_or_literal_human_scope(string user, string query)
    {
        var f = new Fixture(enableInterpretation: true) { User = user };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(PreparedReadReply("preview", query, subject: "Woo hoo! You are a winner!"));
        await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        Assert.Equal(0, f.Interpretations); await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_valid_observed_count_and_an_ordinary_search_have_no_interpretation_overhead()
    {
        var f = new Fixture(enableInterpretation: true) { User = "Count my winning notification emails from Example" };
        await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "preview", "winning"); await f.Step("mail_search", "{\"query\":\"winning\"}");
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:winner\"}");
        Assert.Equal(0, f.Interpretations); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_interpretation_cannot_execute_after_cancellation_or_account_switch(bool switchAccount)
    {
        var f = new Fixture(enableInterpretation: true) { User = "Count my winning notification emails from Example" };
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Interpretation = (_, _) => pending.Task;
        await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "preview", "winning"); await f.Step("mail_search", "{\"query\":\"winning\"}");
        var started = await f.Step("mail_count", "{\"query\":\"Example winning\"}");
        Assert.True((bool)started["started"]); Assert.Equal(1, f.Interpretations);
        if (switchAccount) f.Owner = "different-account";
        else { f.User = "Cancel that"; await f.Control("cancel"); }
        pending.SetResult("{\"headerIndex\":0,\"subjectTerms\":[\"winner\"]}");
        await f.Controller.ResetAsync();
        Assert.Equal(1, f.Executes); Assert.Equal(0, f.Notices);
        Assert.True(f.WorkToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Repeated_bad_proposals_share_the_same_inflight_interpretation_and_count_worker()
    {
        var f = new Fixture(enableInterpretation: true) { User = "Count my winning notification emails from Example" };
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Interpretation = (_, _) => pending.Task;
        await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "preview", "winning"); await f.Step("mail_search", "{\"query\":\"winning\"}");
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        var first = await f.Step("mail_count", "{\"query\":\"Example winning\"}");
        var second = await f.Step("mail_count", "{\"query\":\"Example notification\"}");
        Assert.Equal(first["operation_id"], second["operation_id"]); Assert.Equal(1, f.Interpretations);
        pending.SetResult("{\"headerIndex\":0,\"subjectTerms\":[\"winner\"]}");
        var result = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "result", operation_id = first["operation_id"] }), false, default);
        if (result.ContainsKey("started"))
        {
            await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(2));
            result = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
                { task_id = f.Id, action = "result", operation_id = first["operation_id"] }), false, default);
        }
        Assert.True((bool)result["ok"]); Assert.Equal(2, f.Executes); Assert.Equal(1, f.Interpretations);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Invalid_count_interpretation_has_no_count_dispatch_or_success_claim()
    {
        var f = new Fixture(enableInterpretation: true) { User = "Count my winning notification emails from Example",
            Interpretation = (_, _) => Task.FromResult("{\"headerIndex\":0,\"subjectTerms\":[\"unobserved\"]}") };
        await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "preview", "winning"); await f.Step("mail_search", "{\"query\":\"winning\"}");
        var failed = await f.Step("mail_count", "{\"query\":\"Example winning\"}");
        Assert.False((bool)failed["ok"]); Assert.Equal(1, f.Executes); Assert.Equal(1, f.Interpretations);
        Assert.Equal("evidence_required", ((JsonElement)failed["connector"]).GetProperty("state").GetString());
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        var repaired = await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:winner\"}");
        Assert.True((bool)repaired["ok"]); Assert.Equal(2, f.Executes); Assert.Equal(1, f.Interpretations);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Managed_optional_count_failure_keeps_same_read_owner_without_repeat_or_count_dispatch(int status)
    {
        var f = new Fixture(enableInterpretation: true) { User = "Count my winning notification emails from Example",
            Interpretation = (_, _) => Task.FromException<string>(new KeyboardWtf.Services.PaidStaging.PaidApiException(
                (System.Net.HttpStatusCode)status, "Unavailable optional interpretation")) };
        await f.Resolve(new { connector_id = "gmail" });
        var originalTask = f.Id;
        PreparedRead(f, "preview", "winning"); await f.Step("mail_search", "{\"query\":\"winning\"}");
        var failed = await f.Step("mail_count", "{\"query\":\"Example winning\"}");
        Assert.False((bool)failed["ok"]);
        Assert.Equal("evidence_required", ((JsonElement)failed["connector"]).GetProperty("state").GetString());
        var repeated = await f.Step("mail_count", "{\"query\":\"Example winning\"}");
        Assert.False((bool)repeated["ok"]); Assert.Equal(1, f.Executes); Assert.Equal(1, f.Interpretations);
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        var continued = await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:winner\"}");
        Assert.True((bool)continued["ok"]); Assert.Equal(originalTask, f.Id);
        Assert.Equal(2, f.Executes); Assert.Equal(1, f.Interpretations);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Content_sample_completeness_not_search_paging_or_truncated_flag_owns_disclosure()
    {
        foreach (var complete in new[] { true, false })
        {
            var f = new Fixture { User = "Summarize my conversation", DeclareReadMode = true };
            await f.Resolve(new { connector_id = "gmail" });
            f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = false, data = new { hasMore = complete,
                    contentSample = new { mode = "thread", complete } } } }));
            var read = await f.Execute(); Assert.Equal(!complete, read.ContainsKey("answer_notice"));
            var ended = await f.Control("complete"); Assert.Equal(!complete, ended.ContainsKey("answer_notice"));
            Assert.Equal(complete ? 0 : 1, f.VisualNotices); Assert.Equal(0, f.Notices);
            await f.Controller.ResetAsync();
        }
    }

    [Fact]
    public async Task Partial_content_disclosure_survives_simple_completion_and_replay_without_more_speech()
    {
        var f = new Fixture { User = "Summarize my conversation", DeclareReadMode = true };
        await f.Resolve(new { connector_id = "gmail" });
        PreparedRead(f, "thread", "colleague", partial: true);
        var read = await f.Step("mail_search", "{\"query\":\"colleague\"}");
        Assert.Contains("not fully read", (string)read["answer_notice"]);
        Assert.Equal(read["answer_notice"], read["message"]);
        var complete = await f.Control("complete");
        Assert.Equal("partial_content", complete["result_scope"]);
        Assert.StartsWith((string)complete["answer_notice"], (string)complete["message"]);
        var replay = await f.Control("complete");
        Assert.Equal(complete["answer_notice"], replay["answer_notice"]);
        Assert.Equal(1, f.VisualNotices); Assert.Equal(1, f.Cleanups); Assert.Equal(0, f.Notices);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Mixed_partial_content_disclosure_does_not_relabel_a_verified_total_as_partial()
    {
        var f = new Fixture { User = PlannedRequest, DeclareReadMode = true }; await ResolvePlanned(f);
        await PrepareFirstGoals(f);
        PreparedRead(f, "preview", "winning"); await f.Step("mail_search", "{\"query\":\"winning\"}", "g3");
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36));
        var counted = await f.Step("mail_count", "{\"query\":\"from:updates@example.test subject:Congrats\"}", "g3");
        Assert.False((bool)counted["partial"]); Assert.Contains("not fully read", (string)counted["answer_notice"]);
        var complete = await f.Control("complete");
        Assert.Contains("not fully read", (string)complete["answer_notice"]);
        Assert.Equal(36, Plan(complete).GetProperty("goals")[2].GetProperty("verified_total").GetInt32());
        Assert.Equal(1, f.VisualNotices); Assert.Equal(0, f.Notices);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Quoting_a_category_name_does_not_bypass_count_evidence()
    {
        var f = new Fixture { User = "How many \"launch announcement\" emails do I have?" };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_count", "{\"query\":\"launch announcement\"}");
        Assert.Equal("evidence_required", reply["state"]); Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("from:team@example.test subject:launch", true)]
    [InlineData("from:team@example.test subject:\"Project launch\"", true)]
    [InlineData("from:team@example.test subject:launch after:2026/01/01", true)]
    [InlineData("sender:Team subject:launch", false)]
    [InlineData("subject:launch", false)]
    [InlineData("from:elsewhere@example.test subject:launch", false)]
    [InlineData("from:team@example.test subject:winning", false)]
    [InlineData("from:team@example.test subject:launch OR from:elsewhere@example.test", false)]
    public async Task Inferred_count_requires_the_proposed_sender_and_subject_to_match_observed_headers(string query, bool permitted)
    {
        var f = new Fixture { User = "Count launch announcements", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { sampledMessages = new[] {
                new { from = "Team <team@example.test>", subject = "Project launch announcement", date = "2026-10-09" } } } } }));
        await f.Execute(); f.Pending = null;
        var reply = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        Assert.Equal(permitted, (bool)reply["ok"]); Assert.Equal(permitted ? 2 : 1, f.Executes);
        if (!permitted)
        {
            Assert.Equal("evidence_required", reply["state"]); Assert.False((bool)reply["this_proposal_dispatched"]);
            Assert.Contains("from:", JsonSerializer.Serialize(reply["observedPatterns"]));
        }
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("mail_search", true, "from:team@example.test subject:launch", false)]
    [InlineData("mail_count", true, "from:team@example.test subject:launch", true)]
    [InlineData("mail_count", false, "from:team@example.test subject:launch", false)]
    [InlineData("mail_count", true, "from:other@example.test subject:launch", false)]
    public async Task Count_continuation_requires_a_verified_count_cursor_for_the_identical_query(
        string earlierCapability, bool verified, string earlierQuery, bool permitted)
    {
        const string query = "from:team@example.test subject:launch";
        var f = new Fixture { User = "Count the exact query from:team@example.test subject:launch", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified,
            result = new { data = new { queryUsed = earlierQuery, nextPageToken = "opaque-cursor", complete = false } } }));
        await f.Step(earlierCapability, JsonSerializer.Serialize(new { query = earlierQuery })); f.Pending = null;
        var reply = await f.Step("mail_count", JsonSerializer.Serialize(new { query, pageToken = "opaque-cursor" }));
        Assert.Equal(permitted, (bool)reply["ok"]); Assert.Equal(permitted ? 2 : earlierQuery == query ? 1 : 0, f.Executes);
        if (!permitted) { Assert.Equal("count_from_beginning", reply["state"]); Assert.False((bool)reply["this_proposal_dispatched"]); }
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Count from:team@example.test subject:launch", "from:team@example.test subject:launch")]
    [InlineData("Count messages matching the exact query project launch", "project launch")]
    public async Task Explicit_literal_count_retains_the_direct_path(string user, string query)
    {
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)(await f.Step("mail_count", JsonSerializer.Serialize(new { query })))["ok"]);
        Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task Count_evidence_requires_a_verified_nonempty_preview_in_this_task(bool verified, int previews)
    {
        var f = new Fixture { User = "Count launch announcements", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified,
            result = new { data = new { sampledMessages = Enumerable.Range(0, previews)
                .Select(_ => new { from = "team@example.test", subject = "Project launch" }).ToArray() } } }));
        await f.Execute(); f.Pending = null;
        var reply = await f.Step("mail_count", "{\"query\":\"from:team@example.test subject:launch\"}");
        Assert.Equal(verified && previews > 0, (bool)reply["ok"]);
        Assert.Equal(verified && previews > 0 ? 2 : 1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Search_evidence_is_not_inherited_by_another_task_or_session()
    {
        var f = new Fixture { User = "Count launch announcements", Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { sampledMessages = new[] { new { from = "team@example.test", subject = "Project launch" } } } } }));
        await f.Execute(); f.Pending = null;
        f.Identity = "a-new-human-turn";
        await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal("evidence_required", (await f.Step("mail_count", "{\"query\":\"subject:launch\"}"))["state"]);
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
        await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal("evidence_required", (await f.Step("mail_count", "{\"query\":\"subject:launch\"}"))["state"]);
        Assert.Equal(1, f.Executes); await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("How many winning notification emails do I have?", "topic")]
    [InlineData("Find project launch emails", "topic")]
    [InlineData("Check my Gmail: count the launch announcements", "topic")]
    [InlineData("Search from:team@example.test subject:launch", "exact")]
    [InlineData("Search for the exact phrase launch announcement", "exact")]
    [InlineData("Find emails containing \"project launch\"", "exact")]
    public async Task Mail_search_mode_comes_from_the_frozen_human_turn_not_model_arguments(string user, string mode)
    {
        var f = new Fixture { User = user };
        await f.Resolve(new { connector_id = "gmail" });
        f.User = "Search from:other@example.test"; // Later speech cannot relabel this originating task.
        var query = ConnectorReadReasoner.TryLiteralMailQuery(user, out var literal, out _) ? literal : "inferred topic";
        await f.Step("mail_search", JsonSerializer.Serialize(new { query, matchMode = "invented" }));
        Assert.Equal(mode, f.ExecutedArguments.GetProperty("matchMode").GetString());
        Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal(1, f.Executes);
    }
    [Fact]
    public async Task Settled_answer_loses_task_authority_and_cleanup_is_idempotent()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" }); await f.Execute();
        f.Controller.CompleteSettledUserTurn(f.Identity);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.False(f.Controller.TryCancelSingleTask("cancel", out _));
        Assert.False((bool)(await f.Execute())["ok"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("How many winning notification emails do I have?")]
    [InlineData("How often has Example emailed me to say I won? Count the winning notifications, please.")]
    [InlineData("Count messages matching the exact query from:team@example.test")]
    public async Task A_single_mailbox_count_cannot_complete_from_searches_without_count_evidence(string user)
    {
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = "gmail" });
        if (ConnectorReadReasoner.TryLiteralMailQuery(user, out var literal, out _)) await f.Step("mail_search", JsonSerializer.Serialize(new { query = literal }));
        else await f.Execute();
        var premature = await f.Control("complete");
        Assert.False((bool)premature["ok"]); Assert.Equal("count_evidence_incomplete", premature["state"]);
        Assert.Equal(0, f.Cleanups); Assert.Equal(1, f.Executes);
        f.Controller.CompleteSettledUserTurn(f.Identity); f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(1, f.VisualNotices);
        Assert.Contains("incomplete", f.VisualText, StringComparison.OrdinalIgnoreCase);
        Assert.False(f.Controller.TryCancelSingleTask("cancel", out _));
        Assert.False((bool)(await f.Execute())["ok"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(36, true)]
    [InlineData(12, false)]
    public async Task Verified_zero_or_partial_mailbox_count_is_evidence_not_a_missing_result(int count, bool complete)
    {
        var f = new Fixture { User = "Count messages matching the exact query from:team@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { matchedMessageCount = count, complete } } }));
        var receipt = await f.Step("mail_count", "{\"query\":\"from:team@example.test\"}");
        Assert.True(JsonSerializer.SerializeToElement(receipt["requested_mailbox_count"]).GetProperty("evidence_available").GetBoolean());
        var coverage = JsonSerializer.SerializeToElement(receipt["step_coverage"]);
        Assert.Equal(complete, coverage.GetProperty("count_complete").GetBoolean());
        Assert.Equal(count, coverage.GetProperty(complete ? "verified_query_total" : "lower_bound").GetInt32());
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(0, f.VisualNotices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false, 36)]
    [InlineData(true, -1)]
    public async Task Unverified_or_invalid_count_cannot_satisfy_a_mailbox_total(bool verified, int count)
    {
        var f = new Fixture { User = "Count messages matching the exact query from:team@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified,
            result = new { data = new { matchedMessageCount = count, complete = true } } }));
        await f.Step("mail_count", "{\"query\":\"from:team@example.test\"}");
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(1, f.VisualNotices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Count the words in these emails.", "gmail")]
    [InlineData("How many action items are in my messages?", "gmail")]
    [InlineData("Find emails containing \"count the winning emails\".", "gmail")]
    [InlineData("Find emails containing 'count the winning emails'.", "gmail")]
    [InlineData("Find emails containing ‘count the winning emails’.", "gmail")]
    [InlineData("Read my recent emails.", "gmail")]
    [InlineData("How many messages are in this server?", "discord")]
    public async Task Non_mailbox_totals_do_not_gain_a_count_requirement(string user, string connector)
    {
        var f = new Fixture { User = user }; await f.Resolve(new { connector_id = connector });
        Assert.False((await f.Execute()).ContainsKey("requested_mailbox_count"));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(0, f.VisualNotices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("launch winner", "missing_sender_filter")]
    [InlineData("from:team@example.test launch winner", "missing_subject_filter")]
    [InlineData("from:team@example.test \"subject:launch\"", "missing_subject_filter")]
    [InlineData("from:other@example.test subject:launch", "observed_headers_do_not_match")]
    public async Task Inferred_count_rejection_identifies_the_missing_evidence_without_relaxing_the_query(string query, string reason)
    {
        var f = new Fixture { User = "Count launch announcement emails" }; await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { sampledMessages = new[] { new { from = "team@example.test", subject = "Project launch announcement" } } } } }));
        await f.Execute();
        var rejected = await f.Step("mail_count", JsonSerializer.Serialize(new { query }));
        Assert.Equal("evidence_required", rejected["state"]); Assert.Equal(reason, rejected["evidence_issue"]);
        Assert.False((bool)rejected["this_proposal_dispatched"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Exhausting_one_exact_query_preserves_the_existing_search_count_alternative(int firstPageItems)
    {
        const string query = "from:team@example.test";
        var f = new Fixture { User = "Count messages matching the exact query " + query };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = query, pageItemCount = firstPageItems, hasMore = firstPageItems > 0,
                nextPageToken = firstPageItems > 0 ? "next-count-page" : "",
                messages = Enumerable.Range(0, firstPageItems).Select(index => new { id = "message-" + index }).ToArray() } } }));
        var first = await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        Assert.Equal(firstPageItems == 0, JsonSerializer.SerializeToElement(first["requested_mailbox_count"]).GetProperty("evidence_available").GetBoolean());
        if (firstPageItems == 0) Assert.DoesNotContain("Refine", (string)first["instruction"]);
        else
        {
            Assert.True(JsonSerializer.SerializeToElement(first["step_coverage"]).GetProperty("more_matches").GetBoolean());
            Assert.Contains("same-task pages", (string)first["instruction"]);
        }
        if (firstPageItems > 0)
        {
            f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { data = new { queryUsed = query, pageItemCount = 1, hasMore = false,
                    messages = new[] { new { id = "message-1" } } } } })); // Duplicates can be deduplicated, not added blindly.
            await f.Step("mail_search", JsonSerializer.Serialize(new { query, pageToken = "next-count-page" }));
        }
        var complete = await f.Control("complete");
        Assert.True((bool)complete["ok"]); Assert.Equal(1, f.Cleanups); Assert.Equal(0, f.VisualNotices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Partial_or_undecodable_search_pages_do_not_prove_a_mailbox_total(bool more, bool missingId)
    {
        const string query = "from:team@example.test";
        var f = new Fixture { User = "Count messages matching the exact query " + query };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = query, pageItemCount = 1, hasMore = more,
                messages = new[] { new { id = missingId ? "" : "message-1" } } } } }));
        await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(1, f.VisualNotices);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task A_count_from_another_owned_task_cannot_satisfy_the_new_task()
    {
        var f = new Fixture { User = "Count messages matching the exact query from:team@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(VerifiedCountReply(36)); await f.Step("mail_count", "{\"query\":\"from:team@example.test\"}");
        f.Controller.CompleteSettledUserTurn(f.Identity);
        f.Identity = "new-human-turn";
        await f.Resolve(new { connector_id = "gmail" }); await f.Step("mail_search", "{\"query\":\"from:team@example.test\"}");
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(2, f.Cleanups); Assert.Equal(1, f.VisualNotices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("36")]
    [InlineData(null)]
    public async Task Malformed_verified_count_is_missing_evidence_not_an_answer_observer_exception(string value)
    {
        var f = new Fixture { User = "Count messages matching the exact query from:team@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { matchedMessageCount = value, complete = true } } }));
        await f.Step("mail_count", "{\"query\":\"from:team@example.test\"}");
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(1, f.VisualNotices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public async Task Search_result_keeps_a_single_requested_mailbox_total_explicit_until_evidence_exists(int items)
    {
        var f = new Fixture { User = "Count winning notification emails from Example" }; await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = "from:Example winning", pageItemCount = items, hasMore = items > 0,
                messages = Enumerable.Range(0, items).Select(index => new { id = "message-" + index }).ToArray() } } }));
        var receipt = await f.Step("mail_search", "{\"query\":\"from:Example winning\"}");
        Assert.False(JsonSerializer.SerializeToElement(receipt["requested_mailbox_count"]).GetProperty("evidence_available").GetBoolean());
        Assert.Contains(items == 0 ? "zero" : "mail_count", (string)receipt["instruction"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, f.Executes); Assert.Equal(1, f.Resolves); Assert.False(receipt.ContainsKey("read_plan"));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Single_count_scope_receipt_reports_verified_count_evidence_without_a_new_plan()
    {
        var f = new Fixture { User = "Count messages matching the exact query from:team@example.test" };
        await f.Resolve(new { connector_id = "gmail" }); f.ImmediateReplies.Enqueue(VerifiedCountReply(0));
        var receipt = await f.Step("mail_count", "{\"query\":\"from:team@example.test\"}");
        Assert.True(JsonSerializer.SerializeToElement(receipt["requested_mailbox_count"]).GetProperty("evidence_available").GetBoolean());
        Assert.False(receipt.ContainsKey("read_plan")); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Completed_task_controls_cannot_republish_cached_content_or_retarget_cancellation()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" }); await f.Execute();
        f.Controller.CompleteSettledUserTurn(f.Identity);
        f.Identity = "unrelated-human-turn";
        Assert.False((bool)(await f.Control("result"))["ok"]);
        foreach (var action in new[] { "status", "complete", "cancel" })
        {
            var receipt = await f.Control(action);
            Assert.True((bool)receipt["ok"]); Assert.Equal("completed", receipt["state"]);
            Assert.True((bool)receipt["already_completed"]); Assert.False(receipt.ContainsKey("connector"));
        }
        Assert.Equal(1, f.Cleanups); Assert.Equal(1, f.Executes); Assert.Equal(0, f.Notices);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_retrieval_rechecks_owner_after_cancellation_or_account_switch_during_dispatch(bool accountSwitch)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" }); await f.Execute();
        // GetTask already succeeded. Change authority at the next local
        // boundary, before the cached receipt can leave the controller.
        f.BeforeIdentityRead = () =>
        {
            f.BeforeIdentityRead = null;
            if (accountSwitch) f.Owner = "different-account";
            else Assert.True(f.Controller.TryCancelSingleTask("cancel", out _));
        };
        var result = await f.Control("result");
        Assert.False((bool)result["ok"]); Assert.False(result.ContainsKey("connector"));
        Assert.Equal(1, f.Executes); Assert.Equal(0, f.Notices);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Answer_from_another_turn_cannot_end_this_task()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" }); await f.Execute();
        f.Controller.CompleteSettledUserTurn("unrelated-user-turn");
        Assert.Equal(0, f.Cleanups); Assert.True((bool)(await f.Execute())["ok"]);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Pending_permission_preserves_the_existing_pinned_task()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" }); await f.Execute();
        f.Controller.CompleteSettledUserTurn(f.Identity, f.Id);
        Assert.Equal(0, f.Cleanups);
        Assert.True((bool)(await f.Step("mail_send", "{\"to\":\"self@example.test\"}"))["ok"]);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Result_on_a_confirming_turn_ends_authority_without_changing_original_write_fence()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" }); await f.Execute();
        var originatingIdentity = f.Identity;
        f.Controller.CompleteSettledUserTurn(f.Identity, f.Id);
        f.Identity = "confirmed-human-turn";
        Assert.True((bool)(await f.Step("mail_send", "{\"to\":\"self@example.test\"}"))["ok"]);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.False(f.Controller.TryCancelSingleTask("cancel", out _));
        f.Identity = originatingIdentity;
        var replay = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)replay["duplicate_suppressed"]); Assert.Equal(1, f.Resolves); Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Retrieved_background_result_can_settle_on_a_later_user_turn()
    {
        var f = new Fixture { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        using var proposal = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        Assert.True((bool)(await f.Execute(proposal.Token))["started"]);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Identity = "result-human-turn";
        f.Controller.CompleteSettledUserTurn(f.Identity); Assert.Equal(0, f.Cleanups);
        Assert.True((bool)(await f.Control("result"))["ok"]);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.False(f.Controller.TryCancelSingleTask("cancel", out _));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Late_synchronous_receipt_does_not_attribute_completion_to_an_unrelated_new_turn()
    {
        var f = new Fixture { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        var originatingIdentity = f.Identity;
        var work = f.Execute();
        f.Identity = "unrelated-human-turn";
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
        Assert.True((bool)(await work)["ok"]);
        f.Controller.CompleteSettledUserTurn(f.Identity); Assert.Equal(0, f.Cleanups);
        f.Controller.CompleteSettledUserTurn(originatingIdentity); Assert.Equal(1, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Background_work_and_unread_result_survive_answer_completion_until_result_is_delivered()
    {
        var f = new Fixture { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await f.Resolve(new { connector_id = "gmail" });
        using var proposal = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        Assert.True((bool)(await f.Execute(proposal.Token))["started"]);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.False(f.WorkToken.IsCancellationRequested); Assert.Equal(0, f.Cleanups);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(0, f.Cleanups); // A completion notice is not result retrieval.
        Assert.True((bool)(await f.Control("result"))["ok"]);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.Equal(1, f.Cleanups); Assert.False(f.Controller.TryCancelSingleTask("cancel", out _));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Account_choice_remains_pending_but_answer_ends_an_undispatched_ready_proposal()
    {
        var f = new Fixture { ResolveState = "account_required" };
        await f.Resolve(new { connector_id = "gmail" });
        f.Controller.CompleteSettledUserTurn(f.Identity); Assert.Equal(0, f.Cleanups);
        await f.Controller.ResetAsync();
        f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        await f.Step("mail_search", "bad JSON");
        f.Controller.CompleteSettledUserTurn(f.Identity); Assert.Equal(1, f.Cleanups);
        Assert.False((bool)(await f.Execute())["ok"]);
        Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Automatic_completion_preserves_same_turn_write_fence()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        await f.Step("mail_send", "{\"to\":\"self@example.test\"}");
        f.Controller.CompleteSettledUserTurn(f.Identity);
        var repeated = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)repeated["duplicate_suppressed"]); Assert.False((bool)repeated["new_task_created"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        f.Identity = "fresh-human-request";
        await f.Resolve(new { connector_id = "gmail" }); Assert.Equal(2, f.Resolves);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Settled_task_pruning_does_not_exhaust_the_session_task_limit()
    {
        var f = new Fixture();
        for (var index = 0; index < 12; index++)
        {
            f.Identity = "fresh-turn-" + index;
            Assert.True((bool)(await f.Resolve(new { connector_id = "gmail" }))["ok"]);
            await f.Execute(); f.Controller.CompleteSettledUserTurn(f.Identity);
        }
        Assert.Equal(12, f.Resolves); Assert.Equal(12, f.Executes); Assert.Equal(12, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public void Ordinary_answer_with_no_connector_task_has_no_http_or_notice_overhead()
    {
        var f = new Fixture();
        for (var index = 0; index < 10000; index++) f.Controller.CompleteSettledUserTurn("ordinary-turn");
        Assert.Equal(0, f.Resolves); Assert.Equal(0, f.Executes); Assert.Equal(0, f.Cleanups); Assert.Equal(0, f.Notices);
    }

    [Theory]
    [InlineData("{query: invoice}")]
    [InlineData("```json\n{\"query\":\"invoice\"}\n```")]
    [InlineData("{\"query\":\"first\",\"query\":\"second\"}")]
    [InlineData("[]")]
    [InlineData("{\"query\":{\"unreviewed\":\"value\"}}")]
    public async Task Malformed_read_has_precise_pre_dispatch_receipt_and_can_be_corrected_without_new_authority(string malformed)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var receipt = await f.Step("mail_search", malformed);
        Assert.False((bool)receipt["ok"]); Assert.Equal("invalid_arguments", receipt["state"]);
        Assert.False((bool)receipt["this_proposal_dispatched"]);
        Assert.Equal(f.Id, receipt["task_id"]); Assert.Equal("mail_search", receipt["capability_id"]);
        using var example = JsonDocument.Parse((string)receipt["format_example"]);
        Assert.Equal(JsonValueKind.Object, example.RootElement.ValueKind);
        Assert.Equal("subject:\"literal phrase\"", example.RootElement.GetProperty("query").GetString());
        Assert.Contains("same authorized read", (string)receipt["instruction"]);
        Assert.Equal(0, f.Executes); Assert.Equal(0, f.Notices);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal("Thinking…", KeyboardWtfState.LongTaskDisplayDetail());
        Assert.True((bool)(await f.Execute())["ok"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("shortened")]
    [InlineData("missing")]
    [InlineData("number")]
    [InlineData("uppercase")]
    public async Task Malformed_execution_reference_returns_correction_without_guessing_authority(string kind)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        object reference = kind switch
        {
            "shortened" => f.Id[1..],
            "number" => 42,
            "uppercase" => f.Id.ToUpperInvariant(),
            _ => null
        };
        var receipt = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = reference, capability_id = "mail_search", arguments_json = "{\"sender\":\"Mira\"}" }), false, default);
        Assert.False((bool)receipt["ok"]); Assert.Equal("invalid_reference", receipt["state"]);
        Assert.Equal("task_id", receipt["invalid_field"]);
        Assert.False((bool)receipt["this_proposal_dispatched"]);
        Assert.DoesNotContain(f.Id, JsonSerializer.Serialize(receipt));
        Assert.Contains("exact", (string)receipt["instruction"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Executes); Assert.Equal(0, f.Cleanups); Assert.Equal(0, f.Rebinds);
        Assert.True((bool)(await f.Execute())["ok"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("result")]
    [InlineData("status")]
    [InlineData("cancel")]
    [InlineData("complete")]
    [InlineData("change_account")]
    public async Task Malformed_control_task_reference_has_no_implicit_task_binding(string action)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var receipt = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = "current-task", action, account_id = Guid.NewGuid().ToString("N") }), true, default);
        AssertInvalidReference(receipt, "task_id");
        Assert.DoesNotContain(f.Id, JsonSerializer.Serialize(receipt));
        Assert.Equal(0, f.Executes); Assert.Equal(0, f.Cleanups); Assert.Equal(0, f.Rebinds);
        Assert.True((bool)(await f.Execute())["ok"]);
        Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"task_id\":null}")]
    [InlineData("{\"task_id\":42}")]
    [InlineData("{\"task_id\":\"ABCDEF0123456789ABCDEF0123456789\"}")]
    public async Task Missing_or_noncanonical_task_reference_is_not_coerced_or_dispatched(string argsJson)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var receipt = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.Deserialize<JsonElement>(argsJson), true, default);
        AssertInvalidReference(receipt, "task_id");
        Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Executes); Assert.Equal(0, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Malformed_result_operation_reference_can_recover_only_with_issued_reference()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var executed = await f.Execute(); var operation = (string)executed["operation_id"];
        var receipt = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "result", operation_id = "latest" }), true, default);
        AssertInvalidReference(receipt, "operation_id");
        Assert.DoesNotContain(operation, JsonSerializer.Serialize(receipt));
        Assert.Equal(1, f.Executes); Assert.Equal(0, f.Cleanups);
        var corrected = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "result", operation_id = operation }), true, default);
        Assert.True((bool)corrected["ok"]); Assert.Equal(operation, corrected["operation_id"]);
        Assert.Equal(1, f.Executes);
        var unknown = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "result", operation_id = Guid.NewGuid().ToString("N") }), true, default);
        Assert.False((bool)unknown["ok"]); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("{}")]
    public async Task Wrong_operation_reference_type_cannot_fall_back_to_the_only_operation(string operationJson)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        await f.Execute();
        var receipt = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "result", operation_id = JsonSerializer.Deserialize<JsonElement>(operationJson) }), true, default);
        AssertInvalidReference(receipt, "operation_id");
        Assert.Equal(1, f.Executes); Assert.Equal(0, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Malformed_account_correction_cannot_cancel_rebind_or_authorize_write()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var receipt = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "change_account", account_id = "work" }), true, default);
        AssertInvalidReference(receipt, "account_id");
        Assert.Equal(0, f.Rebinds); Assert.Equal(0, f.Cleanups); Assert.Equal(0, f.Executes);
        var denied = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, capability_id = "mail_send", arguments_json = "{}" }), false, default);
        Assert.False((bool)denied["ok"]); Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Foreign_task_reference_is_a_nonexecuting_lifecycle_receipt_and_known_cancellation_keeps_its_authority_failure()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var before = (f.Resolves, f.Executes, f.Cleanups, f.Rebinds);
        var unavailable = await f.Controller.ExecuteAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = Guid.NewGuid().ToString("N"), action = "result" }), true, default);
        Assert.False((bool)unavailable["ok"]);
        Assert.Equal("task_reference_unavailable", unavailable["state"]);
        Assert.Equal("task_lifecycle", unavailable["error_category"]);
        Assert.False((bool)unavailable["connector_disconnected"]);
        Assert.False((bool)unavailable["this_proposal_dispatched"]);
        Assert.False(unavailable.ContainsKey("task_id")); Assert.False(unavailable.ContainsKey("operation_id"));
        Assert.False(unavailable.ContainsKey("account_id"));
        Assert.Equal(before, (f.Resolves, f.Executes, f.Cleanups, f.Rebinds));
        f.User = "Cancel that"; await f.Control("cancel");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Control("result"));
        Assert.Equal(0, f.Executes); Assert.Equal(1, f.Cleanups);
        await f.Controller.ResetAsync();
    }

    private static void AssertInvalidReference(Dictionary<string, object> receipt, string field)
    {
        Assert.False((bool)receipt["ok"]);
        Assert.Equal("invalid_reference", receipt["state"]);
        Assert.Equal(field, receipt["invalid_field"]);
        Assert.False((bool)receipt["this_proposal_dispatched"]);
        Assert.False(receipt.ContainsKey("task_id")); Assert.False(receipt.ContainsKey("operation_id"));
        Assert.False(receipt.ContainsKey("account_id"));
        Assert.Contains("exact", (string)receipt["instruction"]);
        Assert.Equal("", KeyboardWtfState.LongTaskDetail);
    }

    [Fact]
    public async Task Wrong_argument_type_is_not_silently_treated_as_an_empty_object()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var receipt = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, capability_id = "mail_search", arguments_json = new { query = "invoice" } }), true, default);
        Assert.Equal("invalid_arguments", receipt["state"]); Assert.Equal(0, f.Executes);
        Assert.False((bool)receipt["this_proposal_dispatched"]);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Malformed_write_does_not_grant_permission_or_create_a_send_claim()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var receipt = await f.Step("mail_send", "not JSON");
        Assert.Equal("invalid_arguments", receipt["state"]);
        Assert.Contains("Do not replay any earlier write", (string)receipt["instruction"]);
        Assert.DoesNotContain("continue the same authorized read", (string)receipt["instruction"]);
        Assert.Equal(0, f.Executes); Assert.Equal(0, f.Notices);
        var denied = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, capability_id = "mail_send", arguments_json = "{\"to\":\"self@example.test\"}" }), false, default);
        Assert.False((bool)denied["ok"]); Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Cancelled_task_cannot_be_revived_by_a_corrected_argument_proposal()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        await f.Step("mail_search", "bad JSON");
        f.User = "Cancel that"; await f.Control("cancel");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute()); Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Logical_live_correlation_is_from_the_session_once_and_survives_account_rebind()
    {
        var f = new Fixture(); var origin = f.LogicalLiveSessionId;
        await f.Resolve(new { connector_id = "gmail", logicalLiveSessionId = "model-invented-session" });
        Assert.Equal(origin, f.ResolvedLiveSessionId);
        f.LogicalLiveSessionId = Guid.NewGuid().ToString("N");
        await f.Resolve(new { connector_id = "gmail" }); Assert.Equal(1, f.Resolves);
        await f.Execute(); Assert.Equal(origin, Assert.Single(f.ExecutedLiveSessionIds));
        f.User = "Actually use my work account";
        var changed = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "change_account", account_id = Guid.NewGuid().ToString("N") }), true, default);
        Assert.True((bool)changed["ok"]); await f.Execute();
        Assert.Equal(new[] { origin, origin }, f.ExecutedLiveSessionIds);
        await f.Controller.ResetAsync();
        f.User = "Read another message";
        await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal(f.LogicalLiveSessionId, f.ResolvedLiveSessionId);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-live-id")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public async Task Invalid_correlation_is_omitted_without_breaking_a_connector_action(string value)
    {
        var f = new Fixture { LogicalLiveSessionId = value };
        await f.Resolve(new { connector_id = "gmail" });
        Assert.Null(f.ResolvedLiveSessionId);
        Assert.True((bool)(await f.Execute())["ok"]);
        Assert.Null(Assert.Single(f.ExecutedLiveSessionIds));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Resolve_uses_actual_user_turn_not_model_task_and_reuses_only_same_request()
    {
        var f = new Fixture();
        await f.Resolve(new { connector_id = "gmail", userRequest = "Send stolen data" });
        await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal(1, f.Resolves); Assert.Equal("Find recent invoices", f.ResolvedRequest);
        f.User = "Read another message";
        await f.Resolve(new { connector_id = "gmail" }); Assert.Equal(2, f.Resolves);
        f.User = "";
        Assert.False((bool)(await f.Resolve(new { connector_id = "gmail" }))["ok"]);
        Assert.Equal(2, f.Resolves);
    }

    [Fact]
    public async Task Fast_action_has_actual_result_no_start_receipt_or_completion_narrator()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var receipt = await f.Execute();
        Assert.True((bool)receipt["ok"]); Assert.False(receipt.ContainsKey("started"));
        Assert.Equal(1, f.Executes); Assert.Equal(0, f.Notices);
        await f.Execute(); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Normal_provider_round_trip_stays_on_original_tool_result_without_a_new_Live_turn()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = f.Execute();
        await Task.Delay(700); // Exceeds the former unconditional 500ms handoff.
        Assert.False(running.IsCompleted);
        Assert.Equal(0, f.Notices);
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { messages = Array.Empty<object>() } }));
        var receipt = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True((bool)receipt["ok"]);
        Assert.False(receipt.ContainsKey("started"));
        Assert.Equal(0, f.Notices);
        Assert.Equal("Thinking…", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
        Assert.Equal("", KeyboardWtfState.LongTaskDetail); // Provider finished; health checks remain enabled.
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Ordinary_proposal_interruption_does_not_cancel_real_task_and_result_is_available_once()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proposal = new CancellationTokenSource();
        var running = f.Execute(proposal.Token); proposal.Cancel();
        var pending = await running;
        Assert.True((bool)pending["started"]);
        Assert.Contains("Do not poll", (string)pending["instruction"]);
        var pendingResult = await f.Control("result");
        Assert.Equal("executing", pendingResult["state"]);
        Assert.Contains("keep listening", (string)pendingResult["instruction"]);
        Assert.Equal(1, f.Executes);
        Assert.False(f.WorkToken.IsCancellationRequested);
        Assert.Equal(("Searching Gmail…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(1)));
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { text = "harmless invoice result" } }));
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Notices); Assert.Equal(1, f.Executes);
        Assert.Equal(("Thinking…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(1)));
        var result = await f.Control("result");
        Assert.Contains("harmless invoice result", ((JsonElement)result["connector"]).GetRawText());
        await f.Control("complete");
        Assert.False((bool)(await f.Execute())["ok"]);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Explicit_cancel_ends_authority_and_cannot_fake_completion()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = await f.Execute(); Assert.True((bool)running["started"]);
        Assert.False((bool)(await f.Control("complete"))["ok"]);
        f.User = "Cancel that";
        var cancelled = await f.Control("cancel"); Assert.True((bool)cancelled["ok"]);
        Assert.True(f.WorkToken.IsCancellationRequested);
        f.Pending.TrySetCanceled(f.WorkToken);
        await f.Controller.ResetAsync(); Assert.Equal(0, f.Notices);
    }

    [Fact]
    public async Task Account_switch_and_session_end_cannot_publish_or_keep_source_content()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Execute(); f.Owner = "different-account";
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = "private test text" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Control("result"));
        await f.Controller.ResetAsync(); Assert.Equal(0, f.Notices);
        Assert.Equal(0, f.Cleanups); // Never clean up using the new account token.
    }

    [Fact]
    public async Task Cached_capability_controls_existing_permission_owner_not_model_write_flag()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        Assert.False(f.Controller.RequiresConfirmation(JsonSerializer.SerializeToElement(new { task_id = f.Id, capability_id = "mail_search", writes = true })));
        Assert.True(f.Controller.RequiresConfirmation(JsonSerializer.SerializeToElement(new { task_id = f.Id, capability_id = "mail_send", writes = false })));
        var receipt = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, capability_id = "mail_send", arguments_json = "{}" }), false, default);
        Assert.False((bool)receipt["ok"]); Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData("Cancel that connector task.")]
    [InlineData("Cancel the search")]
    [InlineData("Please stop this search.")]
    public async Task Bare_cancel_owns_only_a_single_task_and_does_not_accept_ordinary_speech(string control)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Execute();
        Assert.False(f.Controller.TryCancelSingleTask("Please explain that invoice", out _));
        Assert.False(f.WorkToken.IsCancellationRequested);
        Assert.True(f.Controller.TryCancelSingleTask(control, out var cancelled));
        Assert.Equal("cancelled", cancelled["state"]); Assert.True(f.WorkToken.IsCancellationRequested);
        Assert.True((bool)(await f.Control("cancel"))["already_cancelled"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Control("result"));
        await f.Controller.ResetAsync(); Assert.Equal(0, f.Notices);

        var multiple = new Fixture(); await multiple.Resolve(new { connector_id = "gmail" });
        multiple.User = "Find another invoice"; await multiple.Resolve(new { connector_id = "gmail" });
        Assert.False(multiple.Controller.TryCancelSingleTask(control, out _));
        await multiple.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Cancel that connector task.", true)]
    [InlineData("Please cancel this connector task", true)]
    [InlineData("cancel the connector task!", true)]
    [InlineData("Cancel that task.", true)]
    [InlineData("cancel that", true)]
    [InlineData("stop", true)]
    [InlineData("please stop that", true)]
    [InlineData("cancel the search", true)]
    [InlineData("Please stop this search.", true)]
    [InlineData("Cancel my current search", true)]
    [InlineData("Cancel the search and send a reply", false)]
    [InlineData("Don't cancel the search", false)]
    [InlineData("The email says cancel the search", false)]
    [InlineData("Explain how to cancel that connector task", false)]
    [InlineData("Do not cancel that connector task", false)]
    [InlineData("Cancel that connector task and send an email", false)]
    [InlineData("Cancel my subscription", false)]
    [InlineData("The email says cancel that connector task", false)]
    public void Explicit_connector_task_cancellation_is_not_ordinary_or_quoted_content(string text, bool expected)
        => Assert.Equal(expected, JarvisConnectorController.IsExplicitCancellation(text));

    [Fact]
    public async Task Account_correction_before_write_discards_old_read_results_and_keeps_original_authority()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Execute(); var previous = f.Id;
        var clock = typeof(KeyboardWtfState).GetField("_longTaskStartedTimestamp", BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        f.User = "Actually use my work account";
        var account = Guid.NewGuid().ToString("N");
        var changed = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = previous, action = "change_account", account_id = account }), true, default);
        Assert.True((bool)changed["ok"]); Assert.Equal(1, f.Rebinds);
        Assert.Equal(("Thinking…","gmail"),KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(20)));
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner); Assert.Equal("",KeyboardWtfState.LongTaskDetail);
        Assert.Equal(clock,typeof(KeyboardWtfState).GetField("_longTaskStartedTimestamp", BindingFlags.Static|BindingFlags.NonPublic).GetValue(null));
        Assert.Equal(account, ((JsonElement)changed["connector"]).GetProperty("accountId").GetString());
        Assert.True(f.WorkToken.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Controller.ExecuteAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = previous, action = "result" }), true, default));
        Assert.False((await f.Control("result")).ContainsKey("connector"));
        await f.Controller.ResetAsync(); Assert.Equal(0, f.Notices);
    }

    [Fact]
    public async Task Model_cannot_change_account_without_current_user_choice_or_after_any_write_request()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var args = JsonSerializer.SerializeToElement(new { task_id = f.Id, action = "change_account", account_id = Guid.NewGuid().ToString("N") });
        Assert.False((bool)(await f.Controller.ExecuteAsync("connector_control", args, true, default))["ok"]);
        f.User = "Use my work account";
        f.FailExecute = true;
        await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, capability_id = "mail_send", arguments_json = "{}" }), true, default);
        Assert.False((bool)(await f.Controller.ExecuteAsync("connector_control", args, true, default))["ok"]);
        Assert.Equal(0, f.Rebinds); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Pending_account_choice_can_be_answered_without_new_broader_task_authority()
    {
        var f = new Fixture { ResolveState = "account_required" };
        await f.Resolve(new { connector_id = "gmail" }); f.User = "Work";
        var changed = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "change_account", account_id = Guid.NewGuid().ToString("N") }), true, default);
        Assert.True((bool)changed["ok"]); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Rebinds);
        Assert.Equal("Find recent invoices", f.ResolvedRequest);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Disconnect_of_target_account_fences_an_uncooperative_late_handoff()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var target = Guid.NewGuid().ToString("N");
        f.RebindPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.RebindIgnoresCancellation = true; f.User = "Use my work account";
        var rebind = f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "change_account", account_id = target }), true, default);
        Assert.Equal(1, f.Rebinds);
        f.Controller.InvalidateAccount(f.Owner, target);
        f.RebindPending.SetResult(true);
        Assert.False((bool)(await rebind)["ok"]);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(0, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Session_reset_clears_blocked_account_handoff_and_late_reply_cannot_restore_it()
    {
        var f=new Fixture(); await f.Resolve(new {connector_id="gmail"});
        f.RebindPending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.User="Use my work account";
        var rebind=f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new
            {task_id=f.Id,action="change_account",account_id=Guid.NewGuid().ToString("N")}),true,default);
        Assert.Equal("Checking account…",KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        await f.Controller.ResetAsync();
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        f.RebindPending.SetResult(true);
        var late=await rebind; Assert.False((bool)late["ok"]);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner); Assert.Equal(0,f.Executes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_cancel_fences_an_uncooperative_late_account_handoff(bool control)
    {
        var f=new Fixture(); await f.Resolve(new {connector_id="gmail"});
        var oldId=f.Id;
        f.RebindPending=new(TaskCreationOptions.RunContinuationsAsynchronously); f.RebindIgnoresCancellation=true;
        f.User="Use my work account";
        var rebind=f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new
            {task_id=oldId,action="change_account",account_id=Guid.NewGuid().ToString("N")}),true,default);
        f.User = "Cancel that";
        if(control) Assert.True((bool)(await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {task_id=oldId,action="cancel"}),true,default))["ok"]);
        else Assert.True(f.Controller.TryCancelSingleTask("cancel that",out _));
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        f.RebindPending.SetResult(true);
        Assert.False((bool)(await rebind)["ok"]); Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(0,f.Executes);
    }
    [Fact]
    public async Task Concurrent_control_cancel_and_account_handoff_cannot_recreate_task_authority()
    {
        for(var attempt=0;attempt<25;attempt++)
        {
            var f=new Fixture(); await f.Resolve(new {connector_id="gmail"}); var oldId=f.Id;
            f.User="Use my work account"; f.RebindPending=new(TaskCreationOptions.RunContinuationsAsynchronously); f.RebindIgnoresCancellation=true;
            using var start=new ManualResetEventSlim();
            var rebind=Task.Run(async()=>
            {
                start.Wait();
                try { return await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new
                    {task_id=oldId,action="change_account",account_id=Guid.NewGuid().ToString("N")}),true,default); }
                catch(InvalidOperationException) { return new Dictionary<string,object> { ["ok"]=false }; } // Cancellation won before reference lookup.
            });
            var cancel=Task.Run(async()=>
            {
                start.Wait();
                for (var spin = 0; f.Rebinds == 0 && spin < 300; ++spin) await Task.Delay(10);
                Assert.Equal(1, f.Rebinds); // The earlier human's account change reached its owned await.
                f.User = "Cancel that";
                return await f.Controller.ExecuteAsync("connector_control",JsonSerializer.SerializeToElement(new {task_id=oldId,action="cancel"}),true,default);
            });
            start.Set(); await cancel; f.RebindPending.TrySetResult(true);
            Assert.False((bool)(await rebind)["ok"]); Assert.Equal(0,f.Executes);
            Assert.False(KeyboardWtfState.HasLongTaskUiOwner); await f.Controller.ResetAsync();
        }
    }

    [Fact]
    public async Task Real_activity_status_clears_on_failure_and_does_not_add_delay_to_fast_work()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Execute();
        Assert.Equal("Searching Gmail…", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
        f.Pending.SetException(new HttpRequestException("Private provider payload must not be returned"));
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain("Private provider", JsonSerializer.Serialize(await f.Control("result")));
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData(false, "failed")]
    [InlineData(true, "outcome_unknown")]
    public async Task Transport_cancellation_without_owned_cancel_settles_a_real_terminal_result(bool write, string expected)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Step(write ? "mail_send" : "mail_search", "{}");
        Assert.False(f.WorkToken.IsCancellationRequested);
        f.Pending.TrySetCanceled();
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = (JsonElement)(await f.Control("result"))["connector"];
        Assert.Equal(expected, result.GetProperty("state").GetString());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Unknown_write_cannot_be_replayed_by_resolving_a_new_task_in_the_same_user_turn()
    {
        var f = new Fixture { FailExecute = true };
        await f.Resolve(new { connector_id = "gmail" });
        await f.Step("mail_send", "{}");
        var blocked = await f.Resolve(new { connector_id = "gmail" });
        Assert.False((bool)blocked["ok"]); Assert.Equal("outcome_unknown", blocked["state"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        f.Identity = "next-real-user-turn"; f.FailExecute = false;
        await f.Resolve(new { connector_id = "gmail" });
        Assert.Equal(2, f.Resolves);
        await f.Controller.ResetAsync();
        f.Identity = "first-user-turn";
        await f.Resolve(new { connector_id = "gmail" }); Assert.Equal(3, f.Resolves);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Bounded_unknown_write_fence_keeps_the_current_turn_after_many_prior_fresh_turns()
    {
        var f = new Fixture { FailExecute = true };
        for (var turn = 0; turn < 40; ++turn)
        {
            f.Identity = "fresh-user-turn-" + turn;
            await f.Resolve(new { connector_id = "gmail" });
            await f.Step("mail_send", "{}");
        }
        var blocked = await f.Resolve(new { connector_id = "gmail" });
        Assert.False((bool)blocked["ok"]); Assert.Equal("outcome_unknown", blocked["state"]);
        Assert.Equal(40, f.Resolves); Assert.Equal(40, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    public async Task Verified_write_fence_survives_task_end_and_pruning_but_allows_a_fresh_human_repeat(string ending)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var original = f.Id;
        var written = await f.Step("mail_send", "{}");
        Assert.True((bool)written["ok"]);
        if (ending == "cancel") f.User = "Cancel that";
        await f.Control(ending);
        f.User = "Find recent invoices";
        var settled = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)settled["verified"]); Assert.True((bool)settled["duplicate_suppressed"]);
        Assert.False((bool)settled["new_task_created"]); Assert.Equal(original, settled["task_id"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);

        f.Identity = "new-human-read"; f.User = "Find another message";
        await f.Resolve(new { connector_id = "gmail" }); // Prunes the terminal write task, not its fence.
        f.Identity = "first-user-turn"; f.User = "Find recent invoices";
        var afterPruning = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)afterPruning["duplicate_suppressed"]);
        Assert.Equal(original, afterPruning["task_id"]); Assert.Equal(2, f.Resolves);

        f.Identity = "fresh-human-repeat";
        await f.Resolve(new { connector_id = "gmail" });
        Assert.NotEqual(original, f.Id);
        Assert.True((bool)(await f.Step("mail_send", "{}"))["ok"]);
        Assert.Equal(3, f.Resolves); Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Dispatched_cancelled_write_keeps_same_turn_fence_before_and_after_worker_settlement()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = await f.Step("mail_send", "{}");
        Assert.True((bool)running["started"]);
        f.User = "Cancel that"; await f.Control("cancel");
        f.User = "Find recent invoices";
        var fenced = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)fenced["duplicate_suppressed"]); Assert.False((bool)fenced["new_task_created"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes); Assert.True(f.WorkToken.IsCancellationRequested);
        await f.WorkSettled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var attempt = 0; attempt < 100 && fenced["state"].ToString() != "outcome_unknown"; ++attempt)
        {
            await Task.Delay(10);
            fenced = await f.Resolve(new { connector_id = "gmail" });
        }
        Assert.Equal("outcome_unknown", fenced["state"]);
        f.Identity = "prune-terminal"; f.User = "Read another message"; f.Pending = null;
        await f.Resolve(new { connector_id = "gmail" });
        f.Identity = "first-user-turn"; f.User = "Find recent invoices";
        var afterSettlement = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)afterSettlement["duplicate_suppressed"]); Assert.False((bool)afterSettlement["verified"]);
        Assert.Equal("outcome_unknown", afterSettlement["state"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes); Assert.Equal(0, f.Notices);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Same_words_in_a_fresh_authoritative_turn_create_a_fresh_read_not_a_cached_operation()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var previous = f.Id; await f.Execute();
        await f.Resolve(new { connector_id = "gmail" }); Assert.Equal(1, f.Resolves);
        f.Identity = "new-human-turn-with-same-words";
        await f.Resolve(new { connector_id = "gmail" });
        Assert.NotEqual(previous, f.Id);
        await f.Execute(); Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Two_resolved_proposals_in_one_authoritative_turn_cannot_both_dispatch_a_write()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var first = f.Id;
        f.User = "Find recent invoices and send the requested message"; // Same human turn, later transcription fragment.
        await f.Resolve(new { connector_id = "gmail" });
        Assert.NotEqual(first, f.Id);
        var written = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = first, capability_id = "mail_send", arguments_json = "{}" }), true, default);
        Assert.True((bool)written["ok"]);
        Assert.False((bool)(await f.Step("mail_send", "{}"))["ok"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("Find the email about Winter internship.")]
    [InlineData("Can you check my emails for winter internship?")]
    [InlineData("Now find my emails with Dr Gao.")]
    [InlineData("I had got an email about an internship on humanoid motion generation, could you find it?")]
    [InlineData("I had got an email about an internship on humanoid motion generation could you find it")]
    [InlineData("I received an email from Mira. Could you please check it?")]
    [InlineData("I just got a message regarding a research position, please find that email.")]
    public async Task Fresh_read_with_completed_task_reference_resolves_current_request_without_replaying_old_arguments(string request)
    {
        var f = new Fixture { User = "Find my emails with Dr Gao." };
        var resolved = await f.Resolve(new { connector_id = "gmail" });
        var oldId = f.Id;
        var oldAccount = ((JsonElement)resolved["connector"]).GetProperty("accountId").GetString();
        await f.Execute(); await f.Control("complete");
        f.User = request; f.Identity = "new-human-read";
        var recovered = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = oldId, capability_id = "mail_search", arguments_json = "{\"query\":\"OLD QUERY MUST NOT REPLAY\"}" }), false, default);
        Assert.True((bool)recovered["ok"]);
        Assert.True((bool)recovered["recovered_from_completed_task"]);
        Assert.False((bool)recovered["this_proposal_dispatched"]);
        Assert.NotEqual(oldId, recovered["task_id"]);
        Assert.Equal(request, f.ResolvedRequest);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.Equal(oldAccount, f.ResolvedPayload.GetProperty("accountId").GetString());
        Assert.DoesNotContain("OLD QUERY", JsonSerializer.Serialize(recovered));
        await f.Step("mail_search", "{\"query\":\"winter internship\"}");
        Assert.Equal(2, f.Executes);
        Assert.Equal("winter internship", f.ExecutedArguments.GetProperty("query").GetString());
        await f.Control("complete"); Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Open Gmail.", "mail_search", false)]
    [InlineData("Find an Instagram message.", "mail_search", false)]
    [InlineData("Find the Gmail email and send it to someone.", "mail_search", false)]
    [InlineData("Find that email in my work Gmail account.", "mail_search", false)]
    [InlineData("I had got an email about a research role, could you find it and send a reply?", "mail_search", false)]
    [InlineData("I received an email in my work account, could you find it?", "mail_search", false)]
    [InlineData("If I said I got an email about research, could you find it?", "mail_search", false)]
    [InlineData("I got an email about a research role.", "mail_search", false)]
    [InlineData("Find the email about Winter internship.", "mail_send", false)]
    [InlineData("Find the email about Winter internship.", "mail_search", true)]
    public async Task Completed_task_recovery_does_not_create_unrelated_write_account_or_same_turn_authority(string request, string capability, bool sameTurn)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        await f.Execute(); await f.Control("complete");
        f.User = request; if (!sameTurn) f.Identity = "new-human-request";
        var result = await f.Step(capability, "{}");
        Assert.False((bool)result["ok"]); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("I had got an email about an internship on humanoid motion generation could you find it", "internship humanoid motion generation")]
    [InlineData("I received an email from Mira, could you find it?", "from:\"Mira\"")]
    public async Task Reported_fresh_read_recovery_retrieves_human_target_instead_of_returning_an_empty_task(string request, string query)
    {
        var f = new Fixture { User = "Find my emails with Dr Gao.", AllowReaderQuery = true, AllowPreparedDayBodies = true };
        var initial = await f.Resolve(new { connector_id = "gmail" });
        var account = ((JsonElement)initial["connector"]).GetProperty("accountId").GetString();
        var old = f.Id; await f.Execute(); await f.Control("complete");
        f.User = request; f.Identity = "reported-new-read";
        var recovered = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = old, capability_id = "mail_read", arguments_json = "{\"id\":\"invented-provider-id\"}" }), false, default);
        Assert.True((bool)recovered["ok"]);
        Assert.True((bool)recovered["recovered_from_completed_task"]);
        Assert.False((bool)recovered["this_proposal_dispatched"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
        Assert.Equal(query, f.ExecutedArguments.GetProperty("query").GetString());
        Assert.DoesNotContain("id", f.ExecutedArguments.EnumerateObject().Select(value => value.Name));
        Assert.Equal(account, f.ResolvedPayload.GetProperty("accountId").GetString());
        Assert.Equal(request, f.ResolvedRequest);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("I received an email about humanoid motion last month, could you find it?")]
    [InlineData("I received an email about humanoid motion from Mira, could you find it?")]
    [InlineData("I received an email about humanoid motion in spam, could you find it?")]
    [InlineData("I received an email about humanoid motion only, could you find it?")]
    [InlineData("I received an email about humanoid motion or robotics, could you find it?")]
    [InlineData("I received an email about \"humanoid motion\", could you find it?")]
    public async Task Reported_read_preparation_leaves_complex_scope_with_existing_planner(string request)
    {
        var f = new Fixture { User = "Find my emails with Dr Gao.", AllowReaderQuery = true, AllowPreparedDayBodies = true };
        await f.Resolve(new { connector_id = "gmail" }); await f.Execute(); await f.Control("complete");
        f.User = request; f.Identity = "reported-scoped-read";
        var recovered = await f.Step("mail_read", "{\"id\":\"invented-provider-id\"}");
        Assert.True((bool)recovered["ok"]); Assert.True((bool)recovered["recovered_from_completed_task"]);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.Equal(request, f.ResolvedRequest);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Fresh_read_recovery_rejects_a_changed_human_request_during_account_validation()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        await f.Execute(); await f.Control("complete");
        f.User = "Find my emails about Winter internship."; f.Identity = "new-read";
        f.BeforeResolveReply = () => { f.User = "Cancel that"; f.Identity = "later-human-input"; };
        var result = await f.Execute();
        Assert.False((bool)result["ok"]); Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("gmail", "mail_thread", "It was sent by Mira.")]
    [InlineData("googledocs", "docs_read", "The revised planning document, actually.")]
    public async Task New_human_clarification_can_resolve_a_registered_read_without_replaying_its_old_object(string connector, string capability, string request)
    {
        var f = new Fixture(preferDirectReadReceipt: true)
            { ConnectorId = connector, ReadCapability = capability, AllowResourceId = true, AccountLabel = "reader@example.test" };
        await f.Resolve(new { connector_id = connector });
        object data = connector == "gmail"
            ? new { id = "aaaaaaaa", messages = new[] { new { id = "11111111", threadId = "aaaaaaaa", subject = "Campus event", text = "Verified message" } } }
            : new { id = "old-resource", title = "Planning document", text = "Verified document" };
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data } }));
        await f.Step(capability, JsonSerializer.Serialize(new { id = connector == "gmail" ? "aaaaaaaa" : "old-resource" }));
        await f.Control("complete");
        var old = f.Id;
        f.User = request; f.Identity = "new-human-clarification";
        var prepared = await f.Step(capability, "{\"id\":\"DO-NOT-REPLAY\"}");
        Assert.True(prepared.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(prepared));
        Assert.True(prepared.GetValueOrDefault("recovered_from_completed_task") is true);
        Assert.NotEqual(old, f.Id);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.Equal(request, f.ResolvedRequest);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("expired")]
    [InlineData("write")]
    [InlineData("logical-session")]
    public async Task Fresh_read_recovery_does_not_reopen_other_terminal_or_write_authority(string scenario)
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        if (scenario == "write") await f.Step("mail_send", "{}");
        else { f.FailExecute = scenario == "failed"; await f.Execute(); }
        if (scenario == "cancelled") { f.User = "Cancel that"; await f.Control("cancel"); }
        else if (scenario != "failed") await f.Control("complete");
        if (scenario == "expired") f.Now = f.Now.AddMinutes(11);
        if (scenario == "logical-session") f.LogicalLiveSessionId = Guid.NewGuid().ToString("N");
        f.User = "Find my emails about Winter internship."; f.Identity = "fresh-human-read";
        if (scenario == "cancelled") await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute());
        else Assert.False((bool)(await f.Execute())["ok"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Expired_task_cannot_reuse_metadata_execute_cache_or_cached_result()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var previous = f.Id; await f.Execute();
        f.Now = f.Now.AddMinutes(11);
        Assert.False((bool)(await f.Execute())["ok"]);
        Assert.False((bool)(await f.Control("result"))["ok"]); Assert.Equal(1, f.Executes);
        await f.Resolve(new { connector_id = "gmail" });
        Assert.NotEqual(previous, f.Id); Assert.Equal(2, f.Resolves);
        await f.Execute(); Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Expiry_never_grants_another_write_in_the_same_authoritative_turn()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        await f.Step("mail_send", "{}"); f.Now = f.Now.AddMinutes(11);
        Assert.False((bool)(await f.Step("mail_send", "{}"))["ok"]);
        Assert.False((bool)(await f.Control("result"))["ok"]);
        var settled = await f.Resolve(new { connector_id = "gmail" });
        Assert.True((bool)settled["duplicate_suppressed"]); Assert.False((bool)settled["new_task_created"]);
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
        f.Identity = "new-human-request";
        await f.Resolve(new { connector_id = "gmail" }); await f.Step("mail_send", "{}");
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Pending_account_clarification_is_retained_across_another_task_in_the_same_session()
    {
        var f = new Fixture { ResolveState = "account_required" };
        var pending = await f.Resolve(new { connector_id = "gmail" });
        var original = ((JsonElement)pending["connector"]).GetProperty("taskId").GetString();
        await f.Resolve(new { connector_id = "gmail" }); Assert.Equal(1, f.Resolves);
        f.User = "Find another invoice"; f.ResolveState = "ready";
        await f.Resolve(new { connector_id = "gmail" }); Assert.Equal(2, f.Resolves);
        f.User = "Work";
        var chosen = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = original, action = "change_account", account_id = Guid.NewGuid().ToString("N") }), true, default);
        Assert.True((bool)chosen["ok"]); Assert.Equal(1, f.Rebinds);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Completion_retrieves_the_exact_operation_not_a_later_result()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await f.Step("mail_search", "{\"query\":\"first\"}");
        var firstOperation = first["operation_id"].ToString();
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = "first result" }));
        await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Pending = null;
        var second = await f.Step("mail_search", "{\"query\":\"second\"}");
        Assert.NotEqual(firstOperation, second["operation_id"]);
        var exact = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.Id, action = "result", operation_id = firstOperation }), true, default);
        Assert.Contains("first result", ((JsonElement)exact["connector"]).GetRawText());
        Assert.False((bool)(await f.Control("result"))["ok"]);
        Assert.Contains(firstOperation!, f.NoticeDetails.Single());
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Connector_status_never_displaces_an_existing_translation_owner()
    {
        const string accepted = "accepted-translation-owner";
        KeyboardWtfState.BeginLongTaskUi(accepted, "Translating…");
        try
        {
            var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
            f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await f.Execute(); Assert.Equal("Translating…", KeyboardWtfState.LongTaskDisplayDetail());
            f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
            await f.Notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Translating…", KeyboardWtfState.LongTaskDisplayDetail());
            await f.Controller.ResetAsync();
            Assert.Equal("Translating…", KeyboardWtfState.LongTaskDisplayDetail());
        }
        finally { KeyboardWtfState.ClearLongTaskUi(accepted); }
    }

    [Fact]
    public async Task Undelivered_completion_uses_one_privacy_safe_visual_fallback_not_speech_or_replay()
    {
        var f = new Fixture { DeliverNotice = false }; await f.Resolve(new { connector_id = "gmail" });
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Execute();
        f.Pending.SetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = "private email body" }));
        await f.Fallback.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Notices); Assert.Equal(1, f.VisualNotices); Assert.Equal(1, f.Executes);
        Assert.DoesNotContain("private email body", f.VisualText);
        Assert.True((bool)(await f.Control("result"))["ok"]);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Permission_identifies_the_pinned_account_and_destination_without_message_content()
    {
        var f = new Fixture(); await f.Resolve(new { connector_id = "gmail" });
        var args = JsonSerializer.SerializeToElement(new { task_id = f.Id, capability_id = "mail_send",
            arguments_json = "{\"to\":\"friend@example.com\",\"subject\":\"private subject\",\"text\":\"private message body\"}" });
        var description = f.Controller.DescribeConfirmation(args);
        Assert.Contains("Work", description); Assert.Contains("gmail", description); Assert.Contains("friend@example.com", description);
        Assert.DoesNotContain("private subject", description); Assert.DoesNotContain("private message body", description);
        Assert.True(description.Length <= 410);
        await f.Controller.ResetAsync();
    }

    [Theory]
    [InlineData("{\"query\":\"a\",\"query\":\"b\"}")]
    [InlineData("{\"query\":{\"secret\":\"x\"}}")]
    [InlineData("[]")]
    [InlineData("{\"query\":null}")]
    public void Argument_boundary_rejects_duplicate_or_structured_unreviewed_fields(string json) =>
        Assert.Throws<ArgumentException>(() => JarvisConnectorController.ParseArguments(json));

    [Theory]
    [InlineData("open_url")]
    [InlineData("get_selected_text")]
    [InlineData("inspect_screen")]
    [InlineData("run_workflow")]
    public void Ordinary_tools_do_not_enter_broker(string name) => Assert.False(JarvisConnectorController.IsTool(name));

    [Fact]
    public void Broker_is_three_small_declarations_not_full_vendor_catalog()
    {
        var method = typeof(GeminiLiveConversationService).GetMethod("BuildTools", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tools = JsonSerializer.SerializeToElement(method.Invoke(null, null));
        var declarations = tools[0].GetProperty("functionDeclarations").EnumerateArray()
            .Where(value => value.GetProperty("name").GetString()!.StartsWith("connector_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, declarations.Length);
        Assert.True(JsonSerializer.Serialize(declarations).Length < 2400);
        Assert.DoesNotContain("composio", JsonSerializer.Serialize(declarations), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/connectors/connectors-panel.js")]
    [InlineData("/connectors/connectors-panel.css")]
    [InlineData("/connectors/icons/gmail.svg")]
    public void Shared_settings_assets_are_real_embedded_resources(string path)
    {
        Assert.True(WebSettingsService.TryResolveConnectorResource(path, out var resource, out _));
        using var stream = typeof(WebSettingsService).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream); Assert.True(stream.Length > 0);
        Assert.True(WebSettingsService.RequiresFiniteSettingsUpdateWork("GET", "/api/connectors"));
    }


    [Theory]
    [InlineData("gmail", "mail_search", "Find the email about the research internship")]
    [InlineData("googledrive", "drive_search", "Find the document about deployment failures")]
    [InlineData("slack", "slack_search", "Find messages about the launch schedule")]
    public async Task Live_native_capability_returns_evidence_without_replanning_the_human_goal(string connector, string capability, string request)
    {
        var models = 0;
        var f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,_,_,_,_) =>
            { ++models; throw new InvalidOperationException("An already selected native capability must not start another planner."); })
            { User = request, ConnectorId = connector, ReadCapability = capability, AllowReaderQuery = true };
        await f.Resolve(new { connector_id = connector });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { matches = new[] { new { id = "observed-result", title = "Native provider evidence" } } } } }));
        var reply = await f.Step(capability, "{\"query\":\"research\"}");
        Assert.Equal(0, models);
        Assert.Equal(1, f.Executes);
        Assert.Contains("Native provider evidence", JsonSerializer.Serialize(reply));
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner); // Between steps until Live settles the user turn.
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Gmail_task_exposes_structured_constraints_and_frozen_clock_without_changing_authority()
    {
        var f = new Fixture { AllowReaderQuery = true, Now = new DateTimeOffset(2026, 10, 20, 20, 13, 0, TimeSpan.Zero) };
        var reply = await f.Resolve(new { connector_id = "gmail" });
        var metadata = JsonSerializer.SerializeToElement(reply["connector"]);
        var fields = metadata.GetProperty("capabilities")[0].GetProperty("arguments");
        Assert.True(fields.TryGetProperty("sender", out _));
        Assert.True(fields.TryGetProperty("receivedOn", out _));
        Assert.Equal(f.Id, metadata.GetProperty("taskId").GetString());
        Assert.False(metadata.GetProperty("capabilities")[0].GetProperty("writes").GetBoolean());
        var clock = JsonSerializer.SerializeToElement(reply["request_context"]);
        Assert.Equal(f.Now, clock.GetProperty("requestUtc").GetDateTimeOffset());
        Assert.Equal(TimeZoneInfo.Local.Id, clock.GetProperty("timeZoneId").GetString());
        var local = clock.GetProperty("localTime").GetDateTimeOffset();
        Assert.Equal(TimeZoneInfo.ConvertTime(f.Now, TimeZoneInfo.Local).Date, local.Date);
        Assert.Equal(TimeZoneInfo.ConvertTime(f.Now, TimeZoneInfo.Local).Offset, local.Offset);
        Assert.Equal(0, f.Executes);
    }

    [Theory]
    [InlineData("Could you find the email I received from Sorren?", "from:Sorren")]
    [InlineData("Could you find my emails from Sorren?", "Sorren")]
    [InlineData("Could you find an email from Sorren and open that thread in a new tab?", "Sorren")]
    [InlineData("Could you find my conversation with Sorren?", "Sorren")]
    [InlineData("Could you find my emails from Sorren?", "Soren")]
    [InlineData("Could you find the email by Sorren and open that email thread in a new tab?", "to:Sorren")]
    [InlineData("Could you find my conversation with Sorren and open it in a new tab?", "sent to:Sorren")]
    public async Task Empty_sender_search_recovers_native_candidates_without_an_available_reasoning_provider(string request, string proposedQuery)
    {
        var models = 0;
        var f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,_,_,_,_) =>
        {
            models++;
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider)
                { HttpStatusCode = 503 };
        }) { User = request, AllowReaderQuery = true, EchoNativeSearchQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(EmptySearchReceipt("from:Sorren"));
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new {
                messages = new[] { new { id = "123456789abcdef0", threadId = "abcdef0123456789" }, new { id = "987654321abcdef0", threadId = "abcdef0987654321" } },
                sampledMessages = new[] {
                    new { id = "123456789abcdef0", threadId = "abcdef0123456789", from = "Soren <soren@example.test>", subject = "Placement", snippet = "Application" },
                    new { id = "987654321abcdef0", threadId = "abcdef0987654321", from = "Someone else <else@example.test>", subject = "Sorren was mentioned", snippet = "Sorren" } },
                pageItemCount = 2, hasMore = false, metadataSample = new { requested = 2, returned = 2, complete = true }
            } } }));
        var reply = await f.Step("mail_search", JsonSerializer.Serialize(new { query = proposedQuery }));
        Assert.True((bool)reply["ok"]);
        Assert.Equal(0, models);
        Assert.Equal(2, f.Executes);
        Assert.Contains("soren@example.test", JsonSerializer.Serialize(reply));
        Assert.DoesNotContain("else@example.test", JsonSerializer.Serialize(reply));
        Assert.DoesNotContain("abcdef0987654321", JsonSerializer.Serialize(reply));
        Assert.All(f.ExecutedTaskIds, id => Assert.Equal(f.Id, id));
        Assert.All(f.ExecutedLiveSessionIds, id => Assert.Equal(f.LogicalLiveSessionId, id));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Human_person_preparation_is_first_lookup_only_and_never_overwrites_a_later_model_refinement()
    {
        var f = new Fixture(preferDirectReadReceipt: true) { User = "Find my emails from Sorren.", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        await f.Step("mail_search", "{\"query\":\"to:Sorren\"}");
        Assert.Equal("from:\"Sorren\"", f.ExecutedArguments.GetProperty("query").GetString());
        await f.Step("mail_search", "{\"query\":\"from:Soren subject:placement\"}");
        Assert.Equal("from:Soren subject:placement", f.ExecutedArguments.GetProperty("query").GetString());
        Assert.Equal(2, f.Executes);
        await f.Controller.ResetAsync();
    }

    [Fact]
    public async Task Human_clarification_after_empty_search_AI_outage_gets_fresh_search_authority()
    {
        var f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,_,_,_,_) =>
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider)
                { HttpStatusCode = 503 }) { User = "Find the email about Orbiton 2027.", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        var old = f.Id;
        f.ImmediateReplies.Enqueue(EmptySearchReceipt("Orbiton 2027"));
        var failed = await f.Step("mail_search", "{\"query\":\"Orbiton 2027\"}");
        Assert.False((bool)failed["ok"]);
        Assert.Equal("ai_provider_unavailable", failed["error_category"]);
        Assert.Contains("incomplete search", (string)failed["instruction"]);
        f.User = "It was sent by Morgan.";
        f.Identity = "human-clarification";
        var recovered = await f.Step("mail_search", "{\"query\":\"stale query\"}");
        Assert.True((bool)recovered["ok"], JsonSerializer.Serialize(recovered));
        Assert.True((bool)recovered["recovered_from_completed_task"]);
        Assert.NotEqual(old, recovered["task_id"]);
        Assert.Equal(1, f.Executes); // Resolving new authority never replays old arguments.
    }

    [Fact]
    public async Task Empty_native_search_recovers_in_existing_reader_without_repeating_the_first_query()
    {
        var models = 0;
        Fixture f = null;
        f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,prompt,_,_,_) =>
        {
            ++models;
            Assert.Equal(2, f.Executes); // Initial empty + one native recovery, never a repeated seed.
            Assert.Contains("from:Maren", prompt);
            Assert.Equal(1, models); // No second synthesis may fail after successful retrieval.
            return Task.FromResult("{\"action\":\"search_batch\",\"capability\":\"mail_search\",\"arguments_json\":\"[{\\\"query\\\":\\\"from:Marin\\\"},{\\\"query\\\":\\\"Maren\\\"}]\",\"answer\":\"\",\"references\":[],\"partial\":false}");
        }) { User = "I had received an email from Maren.", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(EmptySearchReceipt("from:Maren"));
        f.ImmediateReplies.Enqueue(EmptySearchReceipt("Maren"));
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { messages = new[] { new { id = "123456789abcdef0", threadId = "abcdef0123456789" } },
                sampledMessages = new[] { new { id = "123456789abcdef0", threadId = "abcdef0123456789", from = "Marin <marin@example.test>", subject = "Placement" } } }, truncated = false } }));
        var reply = await f.Step("mail_search", "{\"query\":\"from:Maren\"}");
        Assert.Equal(1, models);
        Assert.Equal(3, f.Executes);
        Assert.Equal(1, f.Resolves);
        Assert.Contains("marin@example.test", JsonSerializer.Serialize(reply));
        Assert.DoesNotContain("synthesized", JsonSerializer.Serialize(reply));
        Assert.All(f.ExecutedTaskIds, id => Assert.Equal(f.Id, id));
        Assert.All(f.ExecutedLiveSessionIds, id => Assert.Equal(f.LogicalLiveSessionId, id));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Empty_native_search_recovery_outage_is_not_a_verified_not_found_answer()
    {
        var models = 0;
        var f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,_,_,_,_) =>
        {
            ++models;
            throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Provider)
                { HttpStatusCode = 503 };
        }) { User = "I had received an email from Maren.", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(EmptySearchReceipt("from:Maren"));
        f.ImmediateReplies.Enqueue(EmptySearchReceipt("Maren"));
        var reply = await f.Step("mail_search", "{\"query\":\"from:Maren\"}");
        Assert.False((bool)reply["ok"]);
        var result = (JsonElement)reply["connector"];
        Assert.Equal("failed", result.GetProperty("state").GetString());
        Assert.Equal("ai_provider_unavailable", result.GetProperty("errorCode").GetString());
        Assert.Contains("does not establish", result.GetProperty("message").GetString());
        Assert.Equal(2, models); Assert.Equal(2, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    private static JsonElement EmptySearchReceipt(string query) => JsonSerializer.SerializeToElement(new
    {
        state = "completed", verified = true, result = new { truncated = false, data = new
        {
            queryUsed = query, messages = Array.Empty<object>(), sampledMessages = Array.Empty<object>(),
            pageItemCount = 0, hasMore = false, metadataSample = new { requested = 0, returned = 0, complete = true }
        } }
    });

    [Fact]
    public async Task Yesterday_rejects_a_wrong_raw_date_before_dispatch_and_accepts_the_same_task_correction()
    {
        var f = new Fixture { User = "I received an email from Maren yesterday.", AllowReaderQuery = true,
            Now = new DateTimeOffset(2026, 10, 20, 20, 13, 0, TimeSpan.Zero) };
        var resolved = await f.Resolve(new { connector_id = "gmail" });
        var day = JsonSerializer.SerializeToElement(resolved["mail_date_scope"]);
        var start = day.GetProperty("afterUnixSeconds").GetInt64();
        var end = day.GetProperty("beforeUnixSeconds").GetInt64();
        var wrong = await f.Step("mail_search", JsonSerializer.Serialize(new { query = $"from:Maren after:{start - 86400} before:{end - 86400}" }));
        Assert.Equal("date_scope_required", wrong["state"]);
        Assert.False((bool)wrong["this_proposal_dispatched"]);
        Assert.Equal(0, f.Executes);
        var corrected = await f.Step("mail_search", "{\"sender\":\"Maren\",\"receivedOn\":\"yesterday\"}");
        Assert.True((bool)corrected["ok"]);
        Assert.Equal(1, f.Executes);
        Assert.Contains($"after:{start} before:{end}", f.ExecutedArguments.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData(0, true, true, "gmail", "Find mail from Marin")]
    [InlineData(0, false, false, "gmail", "Find mail from Marin")]
    [InlineData(1, false, true, "gmail", "Find mail from Marin")]
    [InlineData(0, false, true, "googledrive", "Find the placement document")]
    [InlineData(0, false, true, "gmail", "Search using exact query: from:Marin")]
    public async Task Empty_search_refinement_does_not_rewrite_success_literals_failures_or_other_providers(
        int count, bool more, bool verified, string connector, string request)
    {
        var models = 0;
        var f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,_,_,_,_) =>
            { ++models; throw new InvalidOperationException("This search must stay direct."); })
            { User = request, ConnectorId = connector, AllowReaderQuery = true };
        await f.Resolve(new { connector_id = connector });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified,
            result = new { truncated = false, data = new { queryUsed = "from:Marin", pageItemCount = count, hasMore = more,
                messages = Array.Empty<object>(), metadataSample = new { requested = 0, returned = 0, complete = true } } } }));
        var reply = await f.Step("mail_search", "{\"query\":\"from:Marin\"}");
        Assert.Equal(0, models);
        Assert.Equal(1, f.Executes);
    }

    [Fact]
    public async Task Cancelled_empty_search_cannot_start_recovery_or_retain_new_authority()
    {
        var models = 0;
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,_,_,_,_) =>
            { ++models; throw new InvalidOperationException("Cancelled searches cannot reason."); })
            { User = "I had received an email from Maren.", AllowReaderQuery = true, Pending = pending };
        await f.Resolve(new { connector_id = "gmail" });
        var operation = f.Step("mail_search", "{\"query\":\"from:Maren\"}");
        await f.WorkStarted.Task;
        await f.Controller.ResetAsync();
        pending.TrySetResult(EmptySearchReceipt("from:Maren"));
        await operation;
        Assert.Equal(0, models);
        Assert.Equal(1, f.Executes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Mira Raman", "2026-09-07", "internship motion")]
    [InlineData("reader@example.test", "2025-01-18", "")]
    public async Task Structured_sender_and_calendar_day_become_native_filters_before_dispatch(string sender, string receivedOn, string query)
    {
        var f = new Fixture { User = "Find the requested email", AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_search", JsonSerializer.Serialize(new { query, sender, receivedOn }));
        Assert.True((bool)reply["ok"]);
        var day = DateTime.ParseExact(receivedOn, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day, TimeZoneInfo.Local)).ToUnixTimeSeconds();
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1), TimeZoneInfo.Local)).ToUnixTimeSeconds();
        var actual = f.ExecutedArguments.GetProperty("query").GetString();
        Assert.Contains("from:\"" + sender + "\"", actual);
        Assert.Contains($"after:{start} before:{end}", actual);
        Assert.Contains(query, actual);
        Assert.False(f.ExecutedArguments.TryGetProperty("sender", out _));
        Assert.False(f.ExecutedArguments.TryGetProperty("receivedOn", out _));
        Assert.Equal(1, f.Executes);
    }

    [Theory]
    [InlineData("from:Sender on:2026/10/19")]
    [InlineData("received:2026-02-03 proposal")]
    [InlineData("{person:someone subject:proposal}")]
    public async Task Unsupported_provider_operators_are_correctable_without_false_empty_results(string query)
    {
        var f = new Fixture { AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_search", JsonSerializer.Serialize(new { query }));
        Assert.False((bool)reply["ok"]);
        Assert.False((bool)reply["this_proposal_dispatched"]);
        Assert.Equal(f.Id, reply["task_id"]);
        Assert.Equal(0, f.Executes);
    }

    [Theory]
    [InlineData("not-a-day", "Sender")]
    [InlineData("2026-02-30", "Sender")]
    [InlineData("2026-10-19", "Sender\" OR in:anywhere")]
    public async Task Malformed_structured_constraints_never_dispatch(string receivedOn, string sender)
    {
        var f = new Fixture { AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" });
        var reply = await f.Step("mail_search", JsonSerializer.Serialize(new { query = "internship", sender, receivedOn }));
        Assert.False((bool)reply["ok"]);
        Assert.False((bool)reply["this_proposal_dispatched"]);
        Assert.Equal(0, f.Executes);
    }


    [Theory]
    [InlineData("gmail", "mail_search", "I received an email from Mira Raman on September 7, 2026. Can you find it?")]
    [InlineData("gmail", "mail_search", "There should be a research placement email from Mira; could you locate it?")]
    [InlineData("slack", "slack_search", "Was there anything about tomorrow's deployment in the launch channel?")]
    [InlineData("googledrive", "drive_search", "Which document mentioned the upload crash last month?")]
    public async Task Live_fresh_read_resolution_uses_tool_authority_not_sentence_grammar(string connector, string capability, string request)
    {
        var f = new Fixture(preferDirectReadReceipt: true)
            { ConnectorId = connector, ReadCapability = capability, AllowReaderQuery = true };
        await f.Resolve(new { connector_id = connector });
        await f.Step(capability, "{\"query\":\"old query\"}");
        await f.Control("complete");
        var old = f.Id;
        f.User = request; f.Identity = "fresh-native-human";
        var prepared = await f.Step(capability, "{\"query\":\"DO NOT REPLAY\"}");
        Assert.True((bool)prepared["ok"], JsonSerializer.Serialize(prepared));
        Assert.True((bool)prepared["recovered_from_completed_task"]);
        Assert.NotEqual(old, f.Id);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Executes);
        Assert.Equal(request, f.ResolvedRequest);
        Assert.Contains("CURRENT human", (string)prepared["instruction"]);
        await f.Step(capability, "{\"query\":\"new model query\"}");
        Assert.Equal(2, f.Executes);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("same-human")]
    [InlineData("expired")]
    [InlineData("signed-out")]
    [InlineData("write")]
    [InlineData("navigation")]
    [InlineData("account-change")]
    public async Task Live_fresh_read_does_not_restore_terminal_or_write_authority(string ending)
    {
        var f = new Fixture(preferDirectReadReceipt: true) { AllowReaderQuery = true };
        await f.Resolve(new { connector_id = "gmail" }); await f.Execute();
        if (ending == "cancelled") f.User = "Cancel that";
        await f.Control(ending == "cancelled" ? "cancel" : "complete");
        f.User = "Anything about the research placement in my email?";
        if (ending != "same-human") f.Identity = "new-human";
        if (ending == "expired") f.Now = f.Now.AddHours(1);
        if (ending == "signed-out") f.Owner = "another-owner";
        if (ending == "navigation") f.User = "Open Gmail";
        if (ending == "account-change") f.User = "Use my other account";
        try { await f.Step(ending == "write" ? "mail_send" : "mail_search", "{\"query\":\"old\"}"); }
        catch (InvalidOperationException) when (ending is "cancelled" or "signed-out") { }
        Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Executes);
    }

    [Theory]
    [InlineData("mail_read", "aabbccdd11", true)]
    [InlineData("mail_read", "deadbeef99", false)]
    public async Task Native_retained_thread_can_read_only_its_observed_messages(string capability, string id, bool allowed)
    {
        var f = new Fixture(preferDirectReadReceipt: true, reasonRead: (_,_,_,_,_,_,_) => throw new InvalidOperationException("No secondary planner"))
            { User = "Find the relevant correspondence", AllowReaderQuery = true, AccountLabel = "reader@example.test" };
        await f.Resolve(new { connector_id = "gmail" });
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Project" } } } } }));
        await f.Execute(); await f.Control("complete");
        f.Identity = "detail-turn"; f.User = "Show me the full details of that thread.";
        f.ReadCapability = "mail_thread"; f.AllowReaderQuery = false; f.AllowResourceId = true; f.AllowPreparedDayBodies = true;
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = true, data = new { id = "aabbccdd22", totalMessages = 1,
                messageReferences = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } },
                messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Clipped message", contentTruncated = true } } } } }));
        Assert.True((bool)(await f.Controller.TryReadRetainedContextAsync(default))["ok"]);
        f.ImmediateReplies.Enqueue(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated = false, data = new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Complete message body", contentTruncated = false } } }));
        var result = await f.Step(capability, JsonSerializer.Serialize(new { id }));
        Assert.Equal(allowed, result.GetValueOrDefault("ok") is true);
        Assert.Equal(allowed ? 3 : 2, f.Executes);
        if (allowed) Assert.Contains("Complete message body", JsonSerializer.Serialize(result));
        await f.Control("complete");
    }

    private sealed class Fixture
    {
        internal string Owner = "original-account", User = "Find recent invoices", ResolvedRequest = "", Id, Identity = "first-user-turn", VisualText;
        internal string LogicalLiveSessionId = Guid.NewGuid().ToString("N"), ResolvedLiveSessionId;
        internal JsonElement ExecutedArguments, ResolvedPayload;
        internal Action BeforeIdentityRead;
        internal Action BeforeResolveReply;
        internal readonly List<string> ExecutedLiveSessionIds = new();
        internal readonly List<string> ExecutedTaskIds = new(), ExecutedCapabilities = new();
        internal int Resolves, Executes, Notices, Cleanups, Rebinds;
        internal int Interpretations;
        internal ConnectorCountInterpretationInput InterpretedInput;
        internal Func<ConnectorCountInterpretationInput, CancellationToken, Task<string>> Interpretation = (_, _) =>
            Task.FromResult("{\"headerIndex\":0,\"subjectTerms\":[\"Woo hoo\",\"winner\"]}");
        internal string ConnectorId = "gmail";
        internal string ResolveState = "ready", ReadCapability = "mail_search", AccountLabel = "Work";
        internal bool FailExecute, DeclareReadMode, EmptyChoices, AllowReaderQuery, AllowResourceId, AllowPreparedDayBodies;
        internal bool DeliverNotice = true;
        internal bool EchoNativeSearchQuery;
        internal int VisualNotices;
        internal readonly List<string> NoticeDetails = new();
        internal CancellationToken WorkToken;
        internal TaskCompletionSource<JsonElement> Pending;
        internal TaskCompletionSource<bool> RebindPending;
        internal bool RebindIgnoresCancellation;
        internal readonly Queue<JsonElement> ImmediateReplies = new();
        internal readonly TaskCompletionSource<bool> Notified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Fallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource Session = new();
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal readonly TaskCompletionSource<bool> WorkSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> WorkStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly JarvisConnectorController Controller;
        internal Fixture(bool enableInterpretation = false,
            Func<string,string,string,string,object,int,CancellationToken,Task<string>> reasonRead = null, bool preferDirectReadReceipt = false)
        {
            Fixtures.Add(this);
            Controller = new(Request, () => Owner, () => Session.Token, () => User, (announcement, token) =>
            {
                if (announcement.CanPublish) { ++Notices; NoticeDetails.Add(announcement.Detail); Notified.TrySetResult(true); }
                return Task.FromResult(DeliverNotice);
            }, notify: (title, detail) => { ++VisualNotices; VisualText = title + " " + detail; Fallback.TrySetResult(true); },
                userRequestIdentity: () => { BeforeIdentityRead?.Invoke(); return Identity; },
                utcNow: () => Now, logicalLiveSessionId: () => LogicalLiveSessionId,
                interpretCount: enableInterpretation ? (input, token) =>
                    { ++Interpretations; InterpretedInput = input; return Interpretation(input, token); } : null,
                reasonRead: reasonRead, preferDirectReadReceipt: preferDirectReadReceipt);
        }
        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken ct)
        {
            if (path.EndsWith("/resolve", StringComparison.Ordinal))
            {
                ++Resolves; Id = Guid.NewGuid().ToString("N");
                ResolvedPayload = JsonSerializer.SerializeToElement(body).Clone();
                ResolvedRequest = JsonSerializer.SerializeToElement(body).GetProperty("userRequest").GetString();
                ResolvedLiveSessionId = JsonSerializer.SerializeToElement(body).GetProperty("logicalLiveSessionId").GetString();
                BeforeResolveReply?.Invoke();
                return Metadata(ResolvedPayload.TryGetProperty("accountId", out var selectedAccount) && selectedAccount.ValueKind == JsonValueKind.String
                    ? selectedAccount.GetString() : Guid.NewGuid().ToString("N"), ResolveState);
            }
            if (path.EndsWith("/account", StringComparison.Ordinal))
            {
                ++Rebinds; Id = Guid.NewGuid().ToString("N");
                if(RebindPending is not null)
                {
                    if(RebindIgnoresCancellation) await RebindPending.Task;
                    else await RebindPending.Task.WaitAsync(ct);
                }
                return Metadata(JsonSerializer.SerializeToElement(body).GetProperty("accountId").GetString(), "ready");
            }
            if (path.EndsWith("/execute", StringComparison.Ordinal))
            {
                ++Executes; WorkToken = ct;
                ExecutedArguments = JsonSerializer.SerializeToElement(body).GetProperty("arguments").Clone();
                WorkStarted.TrySetResult(true);
                ExecutedLiveSessionIds.Add(JsonSerializer.SerializeToElement(body).GetProperty("logicalLiveSessionId").GetString());
                ExecutedTaskIds.Add(JsonSerializer.SerializeToElement(body).GetProperty("taskId").GetString());
                ExecutedCapabilities.Add(JsonSerializer.SerializeToElement(body).GetProperty("capabilityId").GetString());
                if (FailExecute) throw new HttpRequestException("test-only lost response");
                try
                {
                    if (ImmediateReplies.Count > 0)
                    {
                        var reply = ImmediateReplies.Dequeue();
                        if (EchoNativeSearchQuery && reply.TryGetProperty("result", out var result)
                            && result.TryGetProperty("data", out var data) && data.TryGetProperty("queryUsed", out _))
                        {
                            var copy = System.Text.Json.Nodes.JsonNode.Parse(reply.GetRawText());
                            copy["result"]["data"]["queryUsed"] = ExecutedArguments.GetProperty("query").GetString();
                            return JsonSerializer.SerializeToElement(copy);
                        }
                        return reply;
                    }
                    return Pending is null ? JsonSerializer.SerializeToElement(new { state = "completed", verified = true })
                        : await Pending.Task.WaitAsync(ct);
                }
                finally { WorkSettled.TrySetResult(true); }
            }
            ++Cleanups; return JsonSerializer.SerializeToElement(new { state = "cancelled" });
        }
        private JsonElement Metadata(string account, string state) => JsonSerializer.SerializeToElement(new
            { taskId = Id, state, connectorId = ConnectorId,
                    accountId = account, choices = EmptyChoices ? Array.Empty<object>() : new object[] { new { accountId = account, label = AccountLabel } },
                    expiresAt = Now.AddMinutes(10),
                    capabilities = new[] { new { id = ReadCapability, writes = false, activity = "Searching Gmail…", arguments = AllowReaderQuery
                        ? new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" }
                        : AllowResourceId ? new Dictionary<string, string> { ["id"] = "literal resource id" }
                        : DeclareReadMode
                        ? new Dictionary<string, string> { ["matchMode"] = "exact or topic", ["readMode"] = "preview, message or thread" }
                        : new Dictionary<string, string> { ["matchMode"] = "exact or topic" } },
                        new { id = "mail_count", writes = false, activity = "Searching Gmail…", arguments = new Dictionary<string, string> { ["matchMode"] = "unused" } },
                        new { id = "mail_send", writes = true, activity = "Sending email…", arguments = new Dictionary<string, string> { ["matchMode"] = "unused" } },
                        new { id = "mail_read", writes = false, activity = "Reading Gmail…", arguments = new Dictionary<string, string> { ["id"] = "observed message ID" } } }.Take(AllowPreparedDayBodies ? 4 : 3) });
        internal Task<Dictionary<string, object>> Resolve(object args) => Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(args), true, default);
        internal Task<Dictionary<string, object>> Execute(CancellationToken ct = default) => Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = Id, capability_id = "mail_search", arguments_json = "{\"query\":\"invoice\"}" }), true, ct);
        internal Task<Dictionary<string, object>> Step(string capability, string arguments, string goal = null) => Controller.ExecuteAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = Id, capability_id = capability, arguments_json = arguments, goal_id = goal }), true, default);
        internal Task<Dictionary<string, object>> Control(string action) => Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new { task_id = Id, action }), true, default);
    }
}
