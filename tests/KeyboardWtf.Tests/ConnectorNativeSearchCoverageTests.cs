namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorNativeSearchCoverageTests
{
    [Theory]
    [InlineData(10, false)]
    [InlineData(12, true)]
    public async Task Reasoning_outage_preserves_page_and_metadata_scope_instead_of_counting_four_displayed_headers(int items, bool more)
    {
        await using var f = new Fixture(items, more);
        var reply = await f.Read();
        Assert.True(reply.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(reply));
        var data = ((JsonElement)reply["connector"]).GetProperty("result").GetProperty("data");
        var page = Assert.Single(data.GetProperty("nativeSearchPages").EnumerateArray());
        Assert.Equal(items, page.GetProperty("pageItemCount").GetInt32());
        Assert.Equal(more, page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(8, page.GetProperty("metadataSample").GetProperty("returned").GetInt32());
        Assert.Empty(data.GetProperty("nativeMessageContent").EnumerateArray());
        Assert.Contains("bounded display sample of at most 4", data.GetProperty("answer").GetString());
        var answer = data.GetProperty("answer").GetString();
        Assert.StartsWith("Native Gmail search coverage:", answer);
        Assert.Contains(items + " message references in this returned page", answer);
        Assert.Contains("Metadata was returned for 8 messages", answer);
        Assert.True(answer.IndexOf("Native Gmail search coverage:", StringComparison.Ordinal)
            < answer.IndexOf("Retrieved Gmail headers", StringComparison.Ordinal));
        if (more) Assert.Contains("More matching pages remain; this page count is NOT the total", answer);
        else Assert.Contains("provider reported no further page", answer);

        var coverage = JsonSerializer.SerializeToElement(reply["step_coverage"]);
        Assert.Equal("in:spam", coverage.GetProperty("query").GetString());
        Assert.Equal(items, coverage.GetProperty("matches_in_this_page").GetInt32());
        Assert.Equal(more, coverage.GetProperty("more_matches").GetBoolean());
        Assert.Equal(8, coverage.GetProperty("metadata_sample").GetProperty("returned").GetInt32());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("verified_query_total").ValueKind);
        var answerScope = JsonSerializer.SerializeToElement(reply["answer_scope"]);
        Assert.Equal(4, answerScope.GetProperty("displayedHeaderLimit").GetInt32());
        Assert.Equal("partial", answerScope.GetProperty("coverage").GetString());
        Assert.Contains("never the count", (string)reply["instruction"]);
        Assert.True((bool)reply["partial"]);
        Assert.Equal(1, f.NativeReads);
    }

    [Fact]
    public async Task Missing_native_paging_information_remains_unknown_instead_of_false()
    {
        await using var f = new Fixture(10, null);
        var reply = await f.Read();
        var coverage = JsonSerializer.SerializeToElement(reply["step_coverage"]);
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("more_matches").ValueKind);
        var page = Assert.Single(coverage.GetProperty("native_search_pages").EnumerateArray());
        Assert.False(page.TryGetProperty("hasMore", out _));
        var answer = ((JsonElement)reply["connector"]).GetProperty("result").GetProperty("data").GetProperty("answer").GetString();
        Assert.Contains("Whether more matching pages exist is unknown; no total is established", answer);
    }

    [Fact]
    public void Factual_prefix_keeps_separate_page_counts_without_summing_or_claiming_body_review()
    {
        var pages = new[]
        {
            JsonSerializer.SerializeToElement(new { pageItemCount = 12, hasMore = true, metadataSample = new { returned = 8 } }),
            JsonSerializer.SerializeToElement(new { pageItemCount = 10, hasMore = false, metadataSample = new { returned = 6 } })
        };
        var answer = ConnectorMailSearchCoverage.Format(pages);
        Assert.Contains("Page 1: 12 message references in this returned page", answer);
        Assert.Contains("Page 2: 10 message references in this returned page", answer);
        Assert.Contains("not been added into a total", answer);
        Assert.Contains("not a full body review", answer);
        Assert.DoesNotContain("22", answer);
    }

    [Fact]
    public void Coverage_uses_verified_search_metadata_only_and_does_not_copy_body_cursor_or_total_claims()
    {
        var native = JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { queryUsed = "in:spam", pageItemCount = 12, hasMore = true,
                nextPageToken = "not-action-authority", text = "PRIVATE BODY", verified_query_total = 999,
                metadataSample = new { requested = 8, returned = 8, complete = true, text = "PRIVATE PREVIEW" } } } });
        var unverified = JsonSerializer.SerializeToElement(new { state = "completed", verified = false,
            result = new { data = new { queryUsed = "unverified", pageItemCount = 99, hasMore = false } } });
        var pages = ConnectorMailSearchCoverage.Capture(new[] { ("mail_search", native), ("mail_read", native), ("mail_search", unverified) });
        var page = Assert.Single(pages);
        Assert.Equal(12, page.GetProperty("pageItemCount").GetInt32());
        Assert.DoesNotContain("PRIVATE", page.GetRawText());
        Assert.False(page.TryGetProperty("nextPageToken", out _));
        Assert.False(page.TryGetProperty("verified_query_total", out _));
    }

    private sealed class Fixture(int items, bool? more) : IAsyncDisposable
    {
        private readonly string _task = Guid.NewGuid().ToString("N"), _account = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _session = new();
        private JarvisConnectorController _controller;
        internal int NativeReads;

        internal async Task<Dictionary<string, object>> Read()
        {
            _controller = new(Request, () => "coverage-owner", () => _session.Token,
                () => "Check my spam folder for potentially important emails.", (_, _) => Task.FromResult(true),
                userRequestIdentity: () => "coverage-request", reasonRead: (_, _, _, _, _, _, _) =>
                    throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 },
                preferDirectReadReceipt: false); // Exercise the retained secondary-reader failure path.
            await _controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
            return await _controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = _task, capability_id = "mail_search", arguments_json = "{\"query\":\"in:spam\"}" }), false, default);
        }

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            if (path == "v1/connectors/resolve") return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                taskId = _task, state = "ready", connectorId = "gmail", accountId = _account,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), choices = new[] { new { accountId = _account, label = "reader@example.test" } },
                capabilities = new[] { new { id = "mail_search", writes = false, description = "Search", activity = "Searching Gmail…",
                    arguments = new Dictionary<string, string> { ["query"] = "Gmail query" } } }
            }));
            if (path != "v1/connectors/execute") return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed" }));
            NativeReads++;
            var data = new Dictionary<string, object>
            {
                ["queryUsed"] = "in:spam", ["pageItemCount"] = items,
                ["messages"] = Enumerable.Range(1, items).Select(index => new { id = index.ToString("x8"), threadId = index.ToString("x8") }).ToArray(),
                ["sampledMessages"] = Enumerable.Range(1, 8).Select(index => new { id = index.ToString("x8"), threadId = index.ToString("x8"),
                    subject = "Observed subject " + index, from = "sender@example.test", date = "16 Oct 2026" }).ToArray(),
                ["metadataSample"] = new { requested = 8, returned = 8, complete = true }
            };
            if (more.HasValue) data["hasMore"] = more.Value;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data, truncated = false } }));
        }

        public async ValueTask DisposeAsync()
        {
            if (_controller is not null) await _controller.ResetAsync();
            _session.Cancel(); _session.Dispose();
        }
    }
}
