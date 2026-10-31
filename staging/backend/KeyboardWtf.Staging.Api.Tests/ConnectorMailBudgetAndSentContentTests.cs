using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorMailBudgetAndSentContentTests
{
    [Fact]
    public void Later_thread_internal_date_is_unknown_when_only_a_decimal_prefix_would_fit()
    {
        var source = Thread(newTextLength: 15900, middleTextLength: 7988);
        var safe = ConnectorOperations.SafeResult(source, "mail_thread");
        var messages = safe.GetProperty("data").GetProperty("messages");
        Assert.Equal("1791743400000", messages[0].GetProperty("internalDate").GetString());
        Assert.Equal("1791743400000", messages[1].GetProperty("internalDate").GetString());
        Assert.Equal("old", messages[2].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("internalDate").ValueKind);
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, safe.GetProperty("data").GetProperty("messageReferences").GetArrayLength());
    }

    [Fact]
    public void Nested_search_budget_does_not_convert_a_previously_valid_internal_date_to_a_prefix()
    {
        var inner = ConnectorOperations.SafeResult(Thread(newTextLength: 12000, middleTextLength: 5019), "mail_thread");
        Assert.False(inner.GetProperty("truncated").GetBoolean());
        Assert.All(inner.GetProperty("data").GetProperty("messages").EnumerateArray(), message =>
            Assert.Equal("1791743400000", message.GetProperty("internalDate").GetString()));
        var ids = new[] { "new", "id1", "id2", "id3", "id4", "id5", "id6", "id7" };
        var raw = JsonSerializer.SerializeToElement(new
        {
            messages = ids.Select(id => new { id, threadId = "thread1" }),
            sampledMessages = ids.Select(id => new
            {
                id, threadId = "thread1", internalDate = "1791743400000", from = "fixture@example.test",
                subject = new string('s', 300), snippet = new string('p', 500)
            }),
            sampledContent = new { capability = "mail_thread", messageId = "new", threadId = "thread1", result = inner },
            contentSample = new { mode = "thread", requested = 1, returned = 1, complete = true }
        });
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        var retained = safe.GetProperty("data").GetProperty("sampledContent").GetProperty("result");
        var messages = retained.GetProperty("data").GetProperty("messages");
        Assert.Equal("old", messages[2].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("internalDate").ValueKind);
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.True(retained.GetProperty("truncated").GetBoolean());
        Assert.False(safe.GetProperty("data").GetProperty("contentSample").GetProperty("complete").GetBoolean());
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("alternative_html", true)]
    [InlineData("late_bad_base64", false)]
    [InlineData("late_external_text", false)]
    [InlineData("late_unvisited_text", false)]
    public async Task Matching_first_body_part_is_not_complete_sent_content_when_later_text_is_unread(string scenario, bool verified)
    {
        var operation = ConnectorIds.New();
        var plan = ConnectorOperations.Plan("mail_send", Args(new
        {
            to = "reader@example.test", subject = "Reply verification fixture", text = "Fixture reply.",
            threadId = "expected_thread", inReplyTo = "<parent@example.test>"
        }), operation);
        var message = SentMessage(scenario, "<kwt." + operation + "@keyboard.wtf>");
        var provider = new ReadbackProvider(message);
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "mail_send", operation, plan,
            JsonSerializer.SerializeToElement(new { id = "sent_message" }), default);
        Assert.Equal(verified, result.HasValue); Assert.Equal(1, provider.Reads); Assert.Equal(0, provider.Writes);
    }

    internal static JsonElement SentMessage(string scenario, string messageId)
    {
        var parts = new List<object> { Text("Fixture reply.") };
        if (scenario == "alternative_html") parts.Add(new { mimeType = "text/html", body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>Fixture reply.</p>")) } });
        if (scenario == "late_bad_base64") parts.Add(new { mimeType = "text/plain", body = new { data = "%%%invalid-base64%%%" } });
        if (scenario == "late_external_text") parts.Add(new { mimeType = "text/plain", body = new { attachmentId = "unread_text_attachment" } });
        if (scenario == "late_unvisited_text")
        {
            object part = Text("Unvisited additional text.");
            for (var depth = 0; depth < 14; depth++) part = new { mimeType = "multipart/mixed", parts = new[] { part } };
            parts.Add(part);
        }
        return JsonSerializer.SerializeToElement(new
        {
            id = "sent_message", threadId = "expected_thread", labelIds = new[] { "SENT" }, payload = new
            {
                mimeType = "multipart/mixed", parts, headers = new[]
                {
                    new { name = "Message-ID", value = messageId }, new { name = "To", value = "reader@example.test" },
                    new { name = "Subject", value = "Reply verification fixture" },
                    new { name = "In-Reply-To", value = "<parent@example.test>" },
                    new { name = "References", value = "<ancestor@example.test> <parent@example.test>" }
                }
            }
        });
    }
    private static JsonElement Thread(int newTextLength, int middleTextLength) => JsonSerializer.SerializeToElement(new
    {
        id = "thread1", messages = new[] { Message("old", "Older body."), Message("mid", new string('y', middleTextLength)), Message("new", new string('x', newTextLength)) }
    });
    private static JsonElement Message(string id, string text) => JsonSerializer.SerializeToElement(new { id, threadId = "thread1", internalDate = "1791743400000", payload = Text(text) });
    private static object Text(string text) => new { mimeType = "text/plain", body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) } };
    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
    private sealed class ReadbackProvider(JsonElement message) : IConnectorProvider
    {
        internal int Reads, Writes;
        public Task<JsonElement> RequestAsync(string account, string endpoint, string method, object? body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); if (method == "GET") Reads++; else Writes++;
            Assert.Equal("ca_test123", account); Assert.Equal("GET", method); Assert.Null(body); return Task.FromResult(message);
        }
        public Task<ProviderConnectReply> ConnectAsync(string user, string config, string callback, string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderConnection?> GetAsync(string account, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync(string account, CancellationToken ct) => throw new NotSupportedException();
    }
}

public sealed partial class ConnectorPlatformTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unread_late_sent_text_settles_unknown_without_repeating_the_claimed_write(bool malformedLatePart)
    {
        var f = Fixture(); await Connect(f);
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (endpoint.EndsWith("/messages/send", StringComparison.Ordinal))
            {
                f.Provider.Writes++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { id = "sent_message" }));
            }
            Assert.EndsWith("/messages/sent_message?format=full", endpoint);
            return Task.FromResult(ConnectorMailBudgetAndSentContentTests.SentMessage(
                malformedLatePart ? "late_bad_base64" : "matching", "<rewritten@example.test>"));
        };
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Send an email reply to the selected original message"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "mail_send", Args(new
        {
            to = "reader@example.test", subject = "Reply verification fixture", text = "Fixture reply.",
            threadId = "expected_thread", inReplyTo = "<parent@example.test>"
        }), true);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.Equal(malformedLatePart ? "outcome_unknown" : "completed", reply.State);
        Assert.Equal(!malformedLatePart, reply.Verified);
        if (malformedLatePart)
        {
            Assert.Null(reply.Result);
            var status = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
            Assert.Equal("failed", status.State); Assert.Equal("outcome_unknown", Assert.Single(status.Operations).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller, request, default));
        }
        else Assert.True((await f.Tasks.ExecuteAsync(f.Caller, request, default)).Verified);
        Assert.Equal(1, f.Provider.Writes);
    }
}
