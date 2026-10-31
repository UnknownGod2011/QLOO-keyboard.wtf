namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

// Exercise the first search/read receipt, before any retained-thread follow-up.
[Collection("Live event integration")]
public sealed class ConnectorInitialNativeContentScopeTests
{
    [Fact]
    public async Task Initial_details_outage_scopes_the_answer_to_retrieved_bodies_and_preserves_search_coverage()
    {
        await using var f = new Fixture("Find my emails with Alex Example and give me the details.");
        var receipt = await f.Read();
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        var data = ((JsonElement)receipt["connector"]).GetProperty("result").GetProperty("data");
        Assert.Equal("native_evidence_partial", data.GetProperty("answerKind").GetString());
        Assert.Equal(2, data.GetProperty("nativeMessageContent").GetArrayLength());
        Assert.Equal(new[] { "mail_search", "mail_thread" }, f.Capabilities);

        var scope = JsonSerializer.SerializeToElement(receipt["answer_scope"]);
        Assert.Equal("connector.result.data.nativeMessageContent", scope.GetProperty("contentSource").GetString());
        Assert.Equal(2, scope.GetProperty("retrievedMessageExcerpts").GetInt32());
        Assert.Equal(4, scope.GetProperty("displayedHeaderLimit").GetInt32());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("verifiedQueryTotal").ValueKind);
        var page = Assert.Single(scope.GetProperty("nativeSearchPages").EnumerateArray());
        Assert.Equal(1, page.GetProperty("pageItemCount").GetInt32());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, page.GetProperty("metadataSample").GetProperty("returned").GetInt32());
        var instruction = Assert.IsType<string>(receipt["instruction"]);
        Assert.Contains("nativeMessageContent", instruction);
        Assert.Contains("never the count", instruction);
        Assert.DoesNotContain("within answer_scope.nativeSearchPages", instruction);
    }

    [Theory]
    [InlineData("Find my emails with Alex Example, but only headers; do not read message bodies.")]
    [InlineData("Find my emails with Alex Example.")]
    public async Task Metadata_or_search_only_outage_does_not_advertise_body_evidence(string request)
    {
        await using var f = new Fixture(request);
        var receipt = await f.Read();
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        var data = ((JsonElement)receipt["connector"]).GetProperty("result").GetProperty("data");
        Assert.Empty(data.GetProperty("nativeMessageContent").EnumerateArray());
        Assert.Equal(new[] { "mail_search" }, f.Capabilities);
        var serialized = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("Verified first message body", serialized);
        Assert.DoesNotContain("Verified second message body", serialized);
        if (receipt.TryGetValue("answer_scope", out var value))
        {
            var scope = JsonSerializer.SerializeToElement(value);
            Assert.False(scope.TryGetProperty("contentSource", out _));
            Assert.False(scope.TryGetProperty("retrievedMessageExcerpts", out _));
            Assert.Equal(4, scope.GetProperty("displayedHeaderLimit").GetInt32());
            Assert.Equal(JsonValueKind.Null, scope.GetProperty("verifiedQueryTotal").ValueKind);
        }
        var instruction = Assert.IsType<string>(receipt["instruction"]);
        if (request.Contains("only headers", StringComparison.Ordinal))
            Assert.Contains("excludes body content", instruction);
        else
            Assert.DoesNotContain("nativeMessageContent", instruction);
    }

    private sealed class Fixture(string request) : IAsyncDisposable
    {
        private const string Thread = "aaaabbbb22";
        private readonly string _task = Guid.NewGuid().ToString("N"), _account = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _session = new();
        private JarvisConnectorController _controller;
        private int _modelCalls;
        internal readonly List<string> Capabilities = new();

        internal async Task<Dictionary<string, object>> Read()
        {
            _controller = new(Request, () => "initial-content-owner", () => _session.Token, () => request,
                (_, _) => Task.FromResult(true), userRequestIdentity: () => "initial-content-request",
                reasonRead: (_, _, _, _, _, _, _) =>
                {
                    var first = ++_modelCalls == 1;
                    throw new PersonalAiException(first ? PersonalAiFailure.Provider : PersonalAiFailure.Quota)
                        { HttpStatusCode = first ? 503 : 429 };
                }, preferDirectReadReceipt: false); // Legacy reader outage coverage; Live returns native evidence directly.
            await _controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
            return await _controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = _task, capability_id = "mail_search", arguments_json = "{\"query\":\"Alex Example\"}" }), false, default);
        }

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            if (path == "v1/connectors/resolve") return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                taskId = _task, state = "ready", connectorId = "gmail", accountId = _account,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), choices = new[] { new { accountId = _account, label = "reader@example.test" } },
                capabilities = new[]
                {
                    new { id = "mail_search", writes = false, description = "Search", activity = "Searching Gmail…", arguments = new Dictionary<string, string> { ["query"] = "Gmail query" } },
                    new { id = "mail_thread", writes = false, description = "Thread", activity = "Reading Gmail…", arguments = new Dictionary<string, string> { ["id"] = "thread reference" } },
                    new { id = "mail_read", writes = false, description = "Message", activity = "Reading Gmail…", arguments = new Dictionary<string, string> { ["id"] = "message reference" } }
                }
            }));
            if (path != "v1/connectors/execute") return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed" }));
            var payload = JsonSerializer.SerializeToElement(body);
            var capability = payload.GetProperty("capabilityId").GetString();
            Capabilities.Add(capability);
            object data = capability == "mail_search"
                ? new
                {
                    queryUsed = "Alex Example", pageItemCount = 1, hasMore = false,
                    messages = new[] { new { id = "aaaabbbb11", threadId = Thread } },
                    sampledMessages = new[] { new { id = "aaaabbbb11", threadId = Thread, subject = "Project review", from = "Alex Example <alex@example.test>", date = "16 Oct 2026" } },
                    metadataSample = new { requested = 1, returned = 1, complete = true }
                }
                : new
                {
                    id = Thread, messages = new[]
                    {
                        new { id = "aaaabbbb11", threadId = Thread, text = "Verified first message body.", contentTruncated = false },
                        new { id = "aaaabbbb12", threadId = Thread, text = "Verified second message body.", contentTruncated = false }
                    }
                };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data, truncated = false } }));
        }

        public async ValueTask DisposeAsync()
        {
            if (_controller is not null) await _controller.ResetAsync();
            _session.Cancel(); _session.Dispose();
        }
    }
}
