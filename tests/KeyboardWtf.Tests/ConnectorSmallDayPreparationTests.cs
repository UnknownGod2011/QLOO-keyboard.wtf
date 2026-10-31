namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorSmallDayPreparationTests
{
    private const string Day = "after:1791743400 before:1791829800";
    private const string Request = "Check today's emails for a professor's research response";
    private static readonly ConnectorReadCapability[] Tools = [
        new("mail_search", "Search candidates", new Dictionary<string, string> { ["query"] = "query", ["pageToken"] = "cursor" }),
        new("mail_read", "Read actual body", new Dictionary<string, string> { ["id"] = "observed ID" })];
    private static ConnectorReadReasonerInput Input(string request = Request, string seed = Day) => new(request, Tools)
    {
        InitialCapability = "mail_search", InitialArguments = JarvisConnectorController.ParseArguments(JsonSerializer.Serialize(new { query = seed })),
        RequestUtc = DateTimeOffset.Parse("2026-10-11T19:49:00Z"), RequestTimeZoneId = "India Standard Time"
    };
    private static string Answer(string[] references, bool partial = false) => JsonSerializer.Serialize(new
    { action = "answer", capability = "", arguments_json = "{}", answer = "The returned bodies describe research correspondence; professor identity remains uncertain.", references, partial });
    private static JsonElement Receipt(object data, bool truncated = false, bool verified = true) => JsonSerializer.SerializeToElement(new
    { state = verified ? "completed" : "failed", verified, result = new { data, truncated } });
    private static JsonElement Body(string id, bool truncated = false) => Receipt(new
    { id, threadId = "thread-" + id, text = "Actual research correspondence for " + id, contentTruncated = truncated }, truncated);
    private static JsonElement Page(string query = Day, int count = 3, string defect = "")
    {
        var messages = Enumerable.Range(0, count).Select(i => new Dictionary<string, object>
            { ["id"] = "message-" + i, ["threadId"] = "thread-message-" + i }).ToArray();
        var headers = messages.Select(message => new Dictionary<string, object>(message)
            { ["from"] = "Research office <office@example.test>", ["subject"] = "A routine notice", ["date"] = "Mon, 12 Oct 2026 02:00:00 +0530" }).ToArray();
        var metadata = new Dictionary<string, object> { ["requested"] = count, ["returned"] = count, ["complete"] = true };
        var data = new Dictionary<string, object> { ["queryUsed"] = query, ["messages"] = messages,
            ["sampledMessages"] = headers, ["metadataSample"] = metadata, ["pageItemCount"] = count, ["hasMore"] = false };
        switch (defect)
        {
            case "cursor": data["nextPageToken"] = "next-page"; break;
            case "wrongCursorType": data["nextPageToken"] = 42; break;
            case "emptyCursor": data["nextPageToken"] = ""; break;
            case "hasMore": data["hasMore"] = true; break;
            case "missingHasMore": data.Remove("hasMore"); break;
            case "query": data["queryUsed"] = "different"; break;
            case "pageCount": data["pageItemCount"] = count + 1; break;
            case "requested": metadata["requested"] = count - 1; break;
            case "returned": metadata["returned"] = count - 1; break;
            case "incomplete": metadata["complete"] = false; break;
            case "sampleID": headers[0]["id"] = "unobserved"; break;
            case "sampleThread": headers[0]["threadId"] = "different"; break;
            case "duplicateID": messages[1]["id"] = messages[0]["id"]; break;
            case "invalidID": messages[0]["id"] = "bad/reference"; break;
            case "missingHeader": headers[0].Remove("from"); break;
            case "wrongHeaderType": headers[0]["date"] = 42; break;
            case "missingSample": data.Remove("sampledMessages"); break;
        }
        return Receipt(data, defect == "truncated", defect != "unverified");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Complete_small_day_reads_each_observed_body_before_one_semantic_call(int count)
    {
        var executed = new List<string>(); var modelCalls = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, prompt, _, _, _) =>
        {
            modelCalls++; Assert.Equal(count + 1, executed.Count);
            for (var i = 0; i < count; i++) Assert.Contains("Actual research correspondence for message-" + i, prompt);
            return Task.FromResult(Answer(Enumerable.Range(2, count).Select(i => "r" + i).ToArray()));
        }, (capability, arguments, _) =>
        {
            executed.Add(capability == "mail_search" ? capability : arguments["id"].GetString());
            return Task.FromResult(capability == "mail_search" ? Page(count: count) : Body(arguments["id"].GetString()));
        }, null, default);
        Assert.Equal(new[] { "mail_search" }.Concat(Enumerable.Range(0, count).Select(i => "message-" + i)), executed);
        Assert.Equal(1, modelCalls); Assert.Equal(1, result.ModelCalls); Assert.Equal(count + 1, result.Executions);
        Assert.False(result.Partial); Assert.Contains("uncertain", result.Answer);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("wrongCursorType")]
    [InlineData("hasMore")]
    [InlineData("missingHasMore")]
    [InlineData("query")]
    [InlineData("pageCount")]
    [InlineData("requested")]
    [InlineData("returned")]
    [InlineData("incomplete")]
    [InlineData("sampleID")]
    [InlineData("sampleThread")]
    [InlineData("duplicateID")]
    [InlineData("invalidID")]
    [InlineData("missingHeader")]
    [InlineData("wrongHeaderType")]
    [InlineData("missingSample")]
    [InlineData("truncated")]
    [InlineData("unverified")]
    public async Task Ambiguous_page_reaches_normal_planning_before_any_native_body_read(string defect)
        => await AssertNormalPlanning(Input(), Page(defect: defect));

    [Theory]
    [InlineData("Check today's research responses; don't read bodies")]
    [InlineData("Check today's research responses; only headers")]
    [InlineData("Check today's research responses; headers only")]
    [InlineData("Check today's research responses; metadata only")]
    [InlineData("Check today's research responses without bodies")]
    [InlineData("Check today's research responses; do not retrieve bodies")]
    [InlineData("Check today's research responses; just headers")]
    [InlineData("Check today's research responses without reading bodies")]
    [InlineData("Check today's research responses size:1000")]
    [InlineData("Check today's research responses larger:1M")]
    [InlineData("Check today's research responses smaller:1M")]
    [InlineData("Check today's research responses from:professor@example.test")]
    [InlineData("Check today's research responses using exact query: after:1791743400 before:1791829800")]
    [InlineData("Summarize today's emails")]
    public async Task Human_filters_or_content_restrictions_keep_the_normal_planning_path(string request)
        => await AssertNormalPlanning(Input(request), Page());

    [Fact]
    public async Task Larger_day_or_topic_only_page_is_not_prepared()
    {
        await AssertNormalPlanning(Input(), Page(count: 4));
        await AssertNormalPlanning(Input(seed: "research"), Page("(research) " + Day));
        await AssertNormalPlanning(Input() with { Capabilities = Tools.Take(1).ToArray() }, Page());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normalized_absent_cursor_and_explicit_empty_cursor_both_prepare(bool explicitEmpty)
    {
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, _, _, _, _) => Task.FromResult(Answer(["r2"])),
            (capability, arguments, _) => Task.FromResult(capability == "mail_search" ? Page(count: 1, defect: explicitEmpty ? "emptyCursor" : "")
                : Body(arguments["id"].GetString())), null, default);
        Assert.Equal(1, result.ModelCalls); Assert.Equal(2, result.Executions); Assert.False(result.Partial);
    }

    private static async Task AssertNormalPlanning(ConnectorReadReasonerInput input, JsonElement page)
    {
        var models = 0; var executions = new List<string>();
        await Assert.ThrowsAsync<PlanningReached>(() => ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => { ++models; throw new PlanningReached(); },
            (capability, _, _) => { executions.Add(capability); return Task.FromResult(page); }, null, default));
        Assert.Equal(1, models); Assert.Equal(new[] { "mail_search" }, executions);
    }
    private sealed class PlanningReached : Exception { }

    [Fact]
    public async Task Terminal_body_failure_ends_native_queue_without_another_read()
    {
        var executions = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, _, _, _, _) =>
        { models++; return Task.FromResult(Answer([], true)); }, (capability, _, _) =>
        {
            executions.Add(capability);
            return Task.FromResult(capability == "mail_search" ? Page() : Receipt(new { id = "message-0" }, verified: false));
        }, null, default);
        Assert.Equal(new[] { "mail_search", "mail_read" }, executions); Assert.Equal(1, models);
        Assert.True(result.Partial); Assert.Empty(result.EvidenceReferences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_search_or_first_body_prevents_remaining_reads_and_synthesis(bool afterBody)
    {
        using var cancellation = new CancellationTokenSource(); var executions = 0; var models = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectorReadReasoner.RunAsync(Input(),
            (_, _, _, _, _) => { models++; throw new InvalidOperationException("Cancelled owner cannot synthesize."); },
            (capability, _, _) =>
            {
                executions++; if (afterBody ? capability == "mail_read" : capability == "mail_search") cancellation.Cancel();
                return Task.FromResult(capability == "mail_search" ? Page() : Body("message-0"));
            }, null, cancellation.Token));
        Assert.Equal(afterBody ? 2 : 1, executions); Assert.Equal(0, models);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truncated_or_clipped_actual_body_remains_partial(bool evidenceClipped)
    {
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, _, _, _, _) => Task.FromResult(Answer(["r2", "r3", "r4"])),
            (capability, arguments, _) => Task.FromResult(capability == "mail_search" ? Page()
                : evidenceClipped ? Receipt(new { id = arguments["id"].GetString(), text = new string('x', 25000) })
                : Body(arguments["id"].GetString(), true)), null, default);
        Assert.True(result.Partial); Assert.Equal(1, result.ModelCalls); Assert.Equal(4, result.Executions);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Prepared_day_later_body_failure_cannot_be_hidden_by_an_intact_primary_citation(int failingBodyIndex)
    {
        var executed = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, _, _, _, _) =>
        {
            ++models;
            Assert.Equal(failingBodyIndex + 2, executed.Count);
            return Task.FromResult(Answer(["r2"], partial: false));
        }, (capability, arguments, _) =>
        {
            if (capability == "mail_search")
            {
                executed.Add(capability);
                return Task.FromResult(Page());
            }
            var id = arguments["id"].GetString(); executed.Add(id);
            return Task.FromResult(id == "message-" + failingBodyIndex
                ? Receipt(new { id }, verified: false) : Body(id));
        }, null, default);

        Assert.Equal(new[] { "mail_search" }.Concat(Enumerable.Range(0, failingBodyIndex + 1).Select(i => "message-" + i)), executed);
        Assert.Equal(1, models); Assert.Equal(1, result.ModelCalls);
        Assert.Equal(failingBodyIndex + 2, result.Executions);
        Assert.Equal(new[] { "r2" }, result.EvidenceReferences);
        Assert.True(result.Partial);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("clipped")]
    [InlineData("missingText")]
    [InlineData("missingID")]
    [InlineData("differentID")]
    [InlineData("contentFlagOnly")]
    public async Task Prepared_day_uncited_middle_body_loss_stays_partial(string loss)
    {
        var executed = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, _, _, _, _) =>
        {
            ++models; Assert.Equal(4, executed.Count);
            return Task.FromResult(Answer(["r2"], partial: false));
        }, (capability, arguments, _) =>
        {
            if (capability == "mail_search")
            {
                executed.Add(capability);
                return Task.FromResult(Page());
            }
            var id = arguments["id"].GetString(); executed.Add(id);
            return Task.FromResult(id != "message-1" ? Body(id) : loss switch
            {
                "truncated" => Body(id, truncated: true),
                "clipped" => Receipt(new { id, threadId = "thread-" + id, text = new string('x', 25000), contentTruncated = false }),
                "missingID" => Receipt(new { threadId = "thread-" + id, text = "A full body without its normalized message identity.", contentTruncated = false }),
                "differentID" => Body("different-message"),
                "contentFlagOnly" => Receipt(new { id, threadId = "thread-" + id, text = "An explicitly clipped body with inconsistent outer coverage.", contentTruncated = true }),
                _ => Receipt(new { id, threadId = "thread-" + id, contentTruncated = false })
            });
        }, null, default);

        Assert.Equal(new[] { "mail_search", "message-0", "message-1", "message-2" }, executed);
        Assert.Equal(1, models); Assert.Equal(1, result.ModelCalls); Assert.Equal(4, result.Executions);
        Assert.Equal(new[] { "r2" }, result.EvidenceReferences);
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task Fully_seen_prepared_day_can_cite_only_its_primary_body_without_becoming_partial()
    {
        var executed = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, prompt, _, _, _) =>
        {
            ++models; Assert.Equal(4, executed.Count);
            for (var i = 0; i < 3; i++) Assert.Contains("Actual research correspondence for message-" + i, prompt);
            return Task.FromResult(Answer(["r2"], partial: false));
        }, (capability, arguments, _) =>
        {
            executed.Add(capability == "mail_search" ? capability : arguments["id"].GetString());
            return Task.FromResult(capability == "mail_search" ? Page() : Body(arguments["id"].GetString()));
        }, null, default);

        Assert.Equal(new[] { "mail_search", "message-0", "message-1", "message-2" }, executed);
        Assert.Equal(1, models); Assert.Equal(1, result.ModelCalls); Assert.Equal(4, result.Executions);
        Assert.Equal(new[] { "r2" }, result.EvidenceReferences);
        Assert.False(result.Partial);
    }

    [Fact]
    public async Task Complete_blank_prepared_body_still_fulfills_day_coverage()
    {
        var executed = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, _, _, _, _) =>
        {
            ++models; Assert.Equal(4, executed.Count);
            return Task.FromResult(Answer(["r2"], partial: false));
        }, (capability, arguments, _) =>
        {
            if (capability == "mail_search")
            {
                executed.Add(capability);
                return Task.FromResult(Page());
            }
            var id = arguments["id"].GetString(); executed.Add(id);
            return Task.FromResult(id != "message-1" ? Body(id) : Receipt(new
            {
                id, threadId = "thread-" + id, text = "", contentTruncated = false,
                headers = new[] { new { name = "Date", value = "Mon, 12 Oct 2026 02:00:00 +0530" } }
            }));
        }, null, default);

        Assert.Equal(new[] { "mail_search", "message-0", "message-1", "message-2" }, executed);
        Assert.Equal(1, models); Assert.Equal(1, result.ModelCalls); Assert.Equal(4, result.Executions);
        Assert.Equal(new[] { "r2" }, result.EvidenceReferences);
        Assert.False(result.Partial);
    }

    [Fact]
    public async Task Nonprepared_identified_body_stays_complete_despite_partial_candidate_search()
    {
        var executed = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(
            Input("Read details of the identified research message-0", "research"), (_, _, _, _, _) =>
            {
                ++models;
                return Task.FromResult(models == 1 ? JsonSerializer.Serialize(new
                {
                    action = "execute", capability = "mail_read", arguments_json = "{\"id\":\"message-0\"}",
                    answer = "", references = Array.Empty<string>(), partial = false
                }) : Answer(["r2"], partial: false));
            }, (capability, arguments, _) =>
            {
                executed.Add(capability == "mail_search" ? capability : arguments["id"].GetString());
                return Task.FromResult(capability == "mail_search" ? Page("research", defect: "truncated")
                    : Body(arguments["id"].GetString()));
            }, null, default);

        Assert.Equal(new[] { "mail_search", "message-0" }, executed);
        Assert.Equal(2, models); Assert.Equal(2, result.ModelCalls); Assert.Equal(2, result.Executions);
        Assert.Equal(new[] { "r2" }, result.EvidenceReferences);
        Assert.False(result.Partial);
    }

    [Fact]
    public async Task Invalid_semantic_proposals_still_have_nine_model_calls_and_no_repeated_body_dispatch()
    {
        var models = 0; var executions = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(), (_, _, _, _, _) =>
        { models++; return Task.FromResult("{}"); }, (capability, arguments, _) =>
        { executions++; return Task.FromResult(capability == "mail_search" ? Page() : Body(arguments["id"].GetString())); }, null, default);
        Assert.Equal(9, models); Assert.Equal(9, result.ModelCalls); Assert.Equal(4, executions); Assert.True(result.Partial);
    }

    [Fact]
    public async Task Empty_inferred_topic_can_prepare_only_the_complete_following_whole_day()
    {
        var queries = new List<string>(); var bodies = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(seed: "research professor"), (_, prompt, _, _, _) =>
        { Assert.Equal(3, bodies); return Task.FromResult(Answer(["r3", "r4", "r5"])); }, (capability, arguments, _) =>
        {
            if (capability == "mail_read") { bodies++; return Task.FromResult(Body(arguments["id"].GetString())); }
            var query = arguments["query"].GetString(); queries.Add(query); return Task.FromResult(Page(query, queries.Count == 1 ? 0 : 3));
        }, null, default);
        Assert.Equal(new[] { "(research professor) " + Day, Day }, queries);
        Assert.Equal(1, result.ModelCalls); Assert.Equal(5, result.Executions);
    }
}
