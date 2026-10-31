namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

// Real controller/reader boundaries with inert provider/model delegates only.
[Collection("Live event integration")]
public sealed class ConnectorBoundedPartialReceiptTests
{
    [Fact]
    public async Task Retained_question_keeps_all_native_content_from_the_bounded_read()
    {
        await using var f = new Fixture("verified");
        var first = await f.Read();
        var firstContent = ((JsonElement)first["connector"]).GetProperty("result").GetProperty("data").GetProperty("nativeMessageContent");
        var followup = await f.Question();
        var retained = JsonSerializer.SerializeToElement(followup["nativeMessageContent"]);
        Assert.Equal(6, firstContent.GetArrayLength());
        Assert.Equal(firstContent.EnumerateArray().Select(message => message.GetProperty("id").GetString()),
            retained.EnumerateArray().Select(message => message.GetProperty("id").GetString()));
        Assert.Equal(8, f.Reads);
    }

    [Fact]
    public void Native_receipt_budget_preserves_an_excerpt_from_every_retrieved_message()
    {
        var evidence = Enumerable.Range(1, 8).Select(index => ("mail_read", JsonSerializer.SerializeToElement(new
        {
            state = "completed", verified = true, result = new { data = new {
                id = "aaaabbbb0" + index, threadId = "ccccdddd11", text = new string((char)('A' + index), 9000), contentTruncated = false
            } }
        })));
        var messages = JsonSerializer.SerializeToElement(ConnectorMailFacts.Content(evidence)).EnumerateArray().ToArray();
        Assert.Equal(8, messages.Length);
        Assert.Equal(18000, messages.Sum(message => message.GetProperty("text").GetString().Length));
        Assert.All(messages, message => {
            Assert.True(message.GetProperty("text").GetString().Length >= 2000);
            Assert.True(message.GetProperty("contentClipped").GetBoolean());
        });
    }

    [Theory]
    [InlineData("verified", 6)]
    [InlineData("unverified", 0)]
    [InlineData("headers_only", 0)]
    [InlineData("changed_day", 0)]
    public async Task Bounded_partial_receipt_preserves_only_verified_permitted_current_scope_message_content(string scenario, int expectedMessages)
    {
        await using var f = new Fixture(scenario);
        var receipt = await f.Read();
        Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        var result = ((JsonElement)receipt["connector"]).GetProperty("result");
        var data = result.GetProperty("data");
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal("native_evidence_partial", data.GetProperty("answerKind").GetString());
        Assert.DoesNotContain("AI", data.GetProperty("answer").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("reasoningError").ValueKind);
        Assert.False(data.GetProperty("supported").GetBoolean());
        Assert.Empty(data.GetProperty("evidenceReferences").EnumerateArray());
        Assert.Equal("", data.GetProperty("targetReference").GetString());
        Assert.Equal(expectedMessages, data.GetProperty("nativeMessageContent").GetArrayLength());
        if (expectedMessages > 0)
        {
            Assert.Equal(8, f.Reads);
            Assert.Equal(1, f.Models);
            Assert.All(data.GetProperty("nativeMessageContent").EnumerateArray(), message =>
            {
                Assert.Equal("Actually retrieved project detail.", message.GetProperty("text").GetString());
                Assert.Equal("external_untrusted", message.GetProperty("source").GetString());
                Assert.Contains("not proof of complete thread", message.GetProperty("scope").GetString());
            });
        }
        else Assert.DoesNotContain("Actually retrieved project detail.", data.GetRawText());
        Assert.Equal(scenario is "headers_only" or "changed_day" ? 1 : 0, data.GetProperty("goalRevision").GetInt32());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _scenario, _task = Guid.NewGuid().ToString("N"), _account = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _session = new();
        private readonly JarvisConnectorController _controller;
        private string _user = "Find my emails with Alex Example and give me the details.", _identity = "initial-request";
        internal int Reads, Models;

        internal Fixture(string scenario)
        {
            _scenario = scenario;
            _controller = new(Request, () => "fixture-owner", () => _session.Token, () => _user,
                (_, _) => Task.FromResult(true), userRequestIdentity: () => _identity,
                // This suite exercises the retained non-Live bounded reader.
                reasonRead: Reason, userRequestSnapshot: () => new(_user, _identity), preferDirectReadReceipt: false);
        }

        internal async Task<Dictionary<string, object>> Read()
        {
            await _controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
            return await _controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            {
                task_id = _task, capability_id = "mail_search", arguments_json = "{\"query\":\"Alex Example\"}"
            }), false, default);
        }

        internal Task<Dictionary<string, object>> Question()
        {
            _controller.CompleteSettledUserTurn(_identity);
            _user = "What was the latest message?"; _identity = "next-human-question";
            return _controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new { task_id = _task, action = "result" }), false, default);
        }

        private async Task<string> Reason(string task, string logical, string instruction, string prompt, object schema, int limit, CancellationToken token)
        {
            Models++;
            if (Models == 1 && _scenario is "headers_only" or "changed_day")
            {
                _user = _scenario == "headers_only" ? "Only headers; do not read message bodies." : "Actually only yesterday.";
                _identity = "explicit-read-correction";
                var guided = await _controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
                { task_id = _task, action = "guide", instruction = _user, expected_goal_revision = 0 }), false, token);
                Assert.True(guided.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(guided));
            }
            // A further read after the bounded budget ends retrieval without
            // a grounded answer. This is not a reasoning-provider exception.
            return JsonSerializer.Serialize(new { action = "execute", capability = "mail_read",
                arguments_json = "{\"id\":\"eeeeffff07\"}", answer = "", references = Array.Empty<string>(), partial = true });
        }

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            if (path == "v1/connectors/resolve") return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                taskId = _task, state = "ready", connectorId = "gmail", accountId = _account,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), choices = new[] { new { accountId = _account, label = "reader@example.test" } },
                capabilities = new[] {
                    new { id = "mail_search", writes = false, description = "Search", activity = "Searching Gmail…", arguments = new Dictionary<string,string> { ["query"] = "query" } },
                    new { id = "mail_thread", writes = false, description = "Thread", activity = "Reading Gmail…", arguments = new Dictionary<string,string> { ["id"] = "id" } },
                    new { id = "mail_read", writes = false, description = "Message", activity = "Reading Gmail…", arguments = new Dictionary<string,string> { ["id"] = "id" } } }
            }));
            if (path != "v1/connectors/execute") return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed" }));
            Reads++;
            var payload = JsonSerializer.SerializeToElement(body);
            var capability = payload.GetProperty("capabilityId").GetString();
            var args = payload.GetProperty("arguments");
            object data = capability switch
            {
                "mail_search" => new { messages = new[] { new { id = "aaaabbbb11", threadId = "aaaabbbb22" } },
                    sampledMessages = new[] { new { id = "aaaabbbb11", threadId = "aaaabbbb22", from = "Alex Example", subject = "Project review" } } },
                "mail_thread" => new { id = args.GetProperty("id").GetString(), messages = Array.Empty<object>(),
                    messageReferences = Enumerable.Range(1, 7).Select(i => new { id = "eeeeffff0" + i, threadId = "aaaabbbb22" }).ToArray() },
                _ => new { id = args.GetProperty("id").GetString(), threadId = "aaaabbbb22", text = "Actually retrieved project detail.", contentTruncated = false }
            };
            var verified = _scenario != "unverified" || capability != "mail_read";
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = verified ? "completed" : "failed", verified,
                result = new { data, truncated = capability == "mail_thread" } }));
        }

        public async ValueTask DisposeAsync()
        {
            await _controller.ResetAsync();
            _session.Cancel(); _session.Dispose();
        }
    }
}
