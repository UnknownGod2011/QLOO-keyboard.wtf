namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorMailContentPermissionTests
{
    [Theory]
    [InlineData("Only headers; do not read message bodies.", true)]
    [InlineData("Show only metadata of these messages.", true)]
    [InlineData("Use headers only for now.", true)]
    [InlineData("Only read the message headers.", true)]
    [InlineData("Don't fetch any more email bodies.", true)]
    [InlineData("Show emails using exact query: subject:\"only headers\"", false)]
    [InlineData("Read my emails using exact query: subject:(only headers)", false)]
    [InlineData("The email said \"never read message bodies\".", false)]
    public void Human_content_scope_distinguishes_real_constraints_from_quoted_search_or_provider_words(string text, bool restricted)
        => Assert.Equal(restricted, ConnectorMailContentPermission.MetadataOnly(text, []));

    [Fact]
    public void Repeated_contextual_followups_keep_permission_semantics_after_unrelated_regex_cache_churn()
    {
        const string original = "Could you open it in a new tab and tell me more about what we discussed?";
        const string recap = "Could you please give me a recap of the conversation and open it in a new tab?";
        for (var round = 0; round < 4; round++)
        {
            for (var pattern = 0; pattern < 20; pattern++)
                System.Text.RegularExpressions.Regex.IsMatch("unrelated", "unrelated-route-" + round + "-" + pattern);
            Assert.False(ConnectorMailContentPermission.MetadataOnly(original, [recap, recap]));
            Assert.True(ConnectorMailContentPermission.MetadataOnly(original, ["Headers only.", recap]));
            Assert.True(ConnectorMailContentPermission.MetadataOnly(original,
                ["Headers only.", "Read the message bodies only if I approve later."]));
            Assert.False(ConnectorMailContentPermission.MetadataOnly(original,
                ["Headers only.", "Actually, read the message bodies."]));
        }
    }

    [Fact]
    public void Latest_explicit_human_body_request_can_restore_read_permission_without_erasing_previous_history()
    {
        var history = new[] { "Only headers; do not read message bodies.", "Actually, read the message bodies." };
        Assert.False(ConnectorMailContentPermission.MetadataOnly("Find a professor's response.", history));
        Assert.True(ConnectorMailContentPermission.MetadataOnly("Find a professor's response.", history.Take(1)));
        Assert.Equal(2, history.Length);
    }

    [Fact]
    public void Unmet_future_approval_does_not_restore_body_permission()
        => Assert.True(ConnectorMailContentPermission.MetadataOnly("Find a professor's response.",
            ["Only headers; do not read message bodies.", "Read message bodies only if I approve later."]));

    [Fact]
    public async Task Metadata_projection_cannot_refund_the_original_aggregate_evidence_budget()
    {
        var models = 0; var reads = 0; var maximumEvidenceCharacters = 0;
        var arguments = new Dictionary<string, string> { ["query"] = "bounded query" };
        var result = await ConnectorReadReasoner.RunAsync(new("Summarize only headers of these messages.",
            [new("mail_search", "Search messages", arguments)])
        {
            Provider = "gmail", InitialCapability = "mail_search",
            InitialArguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("q1") }
        }, (_, prompt, _, _, _) =>
        {
            models++;
            using var parsed = JsonDocument.Parse(prompt);
            var total = parsed.RootElement.GetProperty("evidence").EnumerateArray()
                .Where(item => item.TryGetProperty("reference", out _))
                .Sum(item => item.GetProperty("data").GetString().Length);
            maximumEvidenceCharacters = Math.Max(maximumEvidenceCharacters, total);
            Assert.True(total <= 80000, "A projected metadata view must retain the original per-receipt aggregate allocation.");
            return Task.FromResult(models < 4
                ? JsonSerializer.Serialize(new { action = "execute", capability = "mail_search",
                    arguments_json = JsonSerializer.Serialize(new { query = "q" + (models + 1) }),
                    answer = "", references = Array.Empty<string>(), partial = true })
                : JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "",
                    answer = "The supplied headers were inspected within the bounded view.", references = new[] { "r1", "r2", "r3", "r4" },
                    partial = true, goal_status = "partial", target_reference = "" }));
        }, (_, values, _) =>
        {
            reads++;
            var header = new { id = "11111111", threadId = "11111111aaaa",
                from = new string('f', 7900), subject = new string('s', 7900), date = new string('d', 7900) };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = false, data = new { queryUsed = values["query"].GetString(), pageItemCount = 1,
                    hasMore = false, messages = new[] { header }, sampledMessages = new[] { header } } } }));
        }, null, default);
        Assert.Equal(4, reads); Assert.Equal(4, models);
        Assert.True(maximumEvidenceCharacters > 70000); Assert.True(result.Partial);
    }

    [Fact]
    public void Metadata_projection_keeps_observed_headers_and_received_time_but_drops_all_body_sources_and_nested_variants()
    {
        var receipt = JsonSerializer.SerializeToElement(new
        {
            state = "completed", verified = true,
            result = new
            {
                source = "external_untrusted", truncated = false,
                data = new
                {
                    id = "11111111aaaa", queryUsed = "professor", pageItemCount = 1, hasMore = false,
                    contentSample = new { text = "DISALLOWED_BODY" }, html = "DISALLOWED_HTML",
                    messages = new[] { new { id = "11111111", threadId = "11111111aaaa", snippet = "DISALLOWED_SNIPPET",
                        internalDate = "1791763200000", text = "DISALLOWED_TEXT", preview = "DISALLOWED_PREVIEW",
                        headers = new[] { new { name = "Subject", value = "Research correspondence" },
                            new { name = "X-Body", value = "DISALLOWED_HEADER_BODY" } },
                        messages = new[] { new { id = "22222222", text = "DISALLOWED_NESTED" } } } },
                    sampledMessages = new[] { new { id = "11111111", threadId = "11111111aaaa", from = "Professor <prof@example.test>",
                        subject = "Research correspondence", snippet = "DISALLOWED_SAMPLE" } },
                    metadataSample = new { requested = 1, returned = 1, complete = true, unknown = "DISALLOWED_METADATA_BODY" }
                }
            }
        });
        var projected = ConnectorMailContentPermission.Project(receipt);
        Assert.DoesNotContain("DISALLOWED_", projected.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("DISALLOWED_TEXT", receipt.GetRawText(), StringComparison.Ordinal); // Original bounded private receipt stays unchanged.
        var data = projected.GetProperty("result").GetProperty("data");
        Assert.Equal("professor", data.GetProperty("queryUsed").GetString());
        Assert.Equal("1791763200000", data.GetProperty("messages")[0].GetProperty("internalDate").GetString());
        Assert.Equal("Research correspondence", data.GetProperty("messages")[0].GetProperty("headers")[0].GetProperty("value").GetString());
        Assert.True(data.GetProperty("metadataSample").GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void Duplicate_metadata_property_is_not_chosen_from_an_ambiguous_provider_shape()
    {
        using var parsed = JsonDocument.Parse("""{"state":"completed","verified":true,"result":{"truncated":false,"data":{"id":"11111111","id":"22222222","text":"DISALLOWED_BODY"}}}""");
        var data = ConnectorMailContentPermission.Project(parsed.RootElement).GetProperty("result").GetProperty("data");
        Assert.False(data.TryGetProperty("id", out _));
        Assert.False(data.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Retained_body_only_reader_projects_earlier_content_after_headers_only_update()
    {
        var guidance = new ConnectorReadGuidance(0, ""); var reads = 0;
        var scope = ConnectorReadScope.Freeze("Read this full thread aabbccdd22", DateTimeOffset.UtcNow, "UTC");
        const string restriction = "Only headers; do not read message bodies.";
        var result = await ConnectorReadReasoner.RunAsync(new("Read this full thread aabbccdd22",
            [new("mail_thread", "Read thread", new Dictionary<string,string> { ["id"] = "Thread ID" }),
             new("mail_read", "Read message", new Dictionary<string,string> { ["id"] = "Message ID" })])
        {
            Provider = "gmail", InitialCapability = "mail_thread", RetainedThreadId = "aabbccdd22",
            InitialArguments = new Dictionary<string,JsonElement> { ["id"] = JsonSerializer.SerializeToElement("aabbccdd22") },
            Guidance = () => guidance
        }, (_, prompt, _, _, _) =>
        {
            Assert.DoesNotContain("PRIVATE_EARLIER_BODY", prompt);
            return Task.FromResult(JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
                answer = "The observed subject is Project.", references = new[] { "r1" }, partial = true, goal_status = "partial", target_reference = "" }));
        }, (_, _, _) =>
        {
            ++reads;
            guidance = new(1, restriction) { Instructions = [restriction], CurrentScope = scope.RestrictToMetadata() };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data = new
            { id = "aabbccdd22", messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Project", text = "PRIVATE_EARLIER_BODY" } } } } }));
        }, null, default);
        Assert.Equal(1, reads); Assert.DoesNotContain("PRIVATE_EARLIER_BODY", JsonSerializer.Serialize(result.EvidenceReceipts));
    }
}

[Collection("Live event integration")]
public sealed class ConnectorMailMetadataDirectPathTests
{
    [Theory]
    [InlineData("mail_read")]
    [InlineData("mail_thread")]
    public async Task Simple_headers_request_refuses_body_capability_before_provider_dispatch(string capability)
    {
        await using var f = new Fixture();
        await f.Resolve();
        var result = await f.Execute(capability, new { id = "11111111" });
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Equal("metadata_only", result["state"]);
        Assert.Equal(0, f.Executions);
    }

    [Fact]
    public async Task Simple_headers_search_stays_native_preview_and_excludes_implicit_provider_snippets()
    {
        await using var f = new Fixture();
        await f.Resolve();
        var result = await f.Execute("mail_search", new { query = "from:imran", readMode = "thread" });
        Assert.True(result.GetValueOrDefault("ok") is true);
        Assert.Equal(1, f.Executions);
        Assert.Equal("preview", f.ActualReadMode);
        Assert.DoesNotContain("DISALLOWED_", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Contains("Observed subject", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string TaskId = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _session = new();
        private readonly JarvisConnectorController _controller;
        internal int Executions;
        internal string ActualReadMode;
        internal Fixture() => _controller = new(Request, () => "fixture-owner", () => _session.Token,
            () => "Show only headers of emails from Imran.", (_, _) => Task.FromResult(true));
        internal Task<Dictionary<string, object>> Resolve() => _controller.ExecuteAsync("connector_resolve",
            JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), true, default);
        internal Task<Dictionary<string, object>> Execute(string capability, object arguments) => _controller.ExecuteAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = TaskId, capability_id = capability, arguments_json = JsonSerializer.Serialize(arguments) }), true, default);
        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, method);
            token.ThrowIfCancellationRequested();
            if (path == "v1/connectors/resolve")
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    taskId = TaskId, state = "ready", connectorId = "gmail", accountId = "fixture-account",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    capabilities = new[]
                    {
                        new { id = "mail_search", writes = false, activity = "Searching Gmail…",
                            arguments = new Dictionary<string, string> { ["query"] = "query", ["readMode"] = "mode", ["matchMode"] = "mode" } },
                        new { id = "mail_read", writes = false, activity = "Reading Gmail…", arguments = new Dictionary<string, string> { ["id"] = "id" } },
                        new { id = "mail_thread", writes = false, activity = "Reading Gmail…", arguments = new Dictionary<string, string> { ["id"] = "id" } }
                    }
                }));
            if (path == "v1/connectors/execute")
            {
                Executions++;
                var payload = JsonSerializer.SerializeToElement(body);
                Assert.Equal("mail_search", payload.GetProperty("capabilityId").GetString());
                ActualReadMode = payload.GetProperty("arguments").GetProperty("readMode").GetString();
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    state = "completed", verified = true,
                    result = new { truncated = false, data = new
                    {
                        pageItemCount = 1, hasMore = false,
                        messages = new[] { new { id = "11111111", threadId = "11111111aaaa",
                            subject = "Observed subject", snippet = "DISALLOWED_PROVIDER_SNIPPET" } },
                        contentSample = new { text = "DISALLOWED_CONTENT_SAMPLE" }
                    } }
                }));
            }
            Assert.Equal("v1/connectors/tasks/" + TaskId + "/cancel", path);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "cancelled", verified = true }));
        }
        public async ValueTask DisposeAsync() { await _controller.ResetAsync(); _session.Cancel(); _session.Dispose(); }
    }
}
