using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorReplyThreadVerificationTests
{
    [Theory]
    [InlineData("matching", true)]
    [InlineData("references_ancestry", true)]
    [InlineData("rewritten_rfc_identity", true)]
    [InlineData("wrong_thread", false)]
    [InlineData("missing_thread", false)]
    [InlineData("missing_in_reply_to", false)]
    [InlineData("wrong_in_reply_to", false)]
    [InlineData("missing_references", false)]
    [InlineData("wrong_references", false)]
    [InlineData("lost_ack_wrong_thread", false)]
    [InlineData("thread_only_matching", true)]
    [InlineData("thread_only_wrong", false)]
    [InlineData("rfc_only_matching", true)]
    [InlineData("rfc_only_wrong", false)]
    [InlineData("ordinary_new_message", true)]
    [InlineData("ordinary_rewritten_rfc", true)]
    public async Task Sent_content_cannot_verify_the_wrong_reply_thread_or_parent(string scenario, bool expectedVerified)
    {
        var operation = ConnectorIds.New();
        var ordinary = scenario.StartsWith("ordinary_", StringComparison.Ordinal);
        var args = Args(new { to = "reader@example.test", subject = "Reply verification fixture", text = "Fixture reply." });
        if (!ordinary && !scenario.StartsWith("rfc_only_", StringComparison.Ordinal))
            args["threadId"] = JsonSerializer.SerializeToElement("expected_thread");
        if (!ordinary && !scenario.StartsWith("thread_only_", StringComparison.Ordinal))
            args["inReplyTo"] = JsonSerializer.SerializeToElement("<parent@example.test>");
        var plan = ConnectorOperations.Plan("mail_send", args, operation);
        var headers = new List<Dictionary<string, string>>
        {
            Header("Message-ID", scenario is "rewritten_rfc_identity" or "ordinary_rewritten_rfc"
                ? "<rewritten@example.test>" : "<kwt." + operation + "@keyboard.wtf>"),
            Header("To", "reader@example.test"),
            Header("Subject", "Reply verification fixture")
        };
        if (args.ContainsKey("inReplyTo"))
        {
            if (scenario != "missing_in_reply_to")
                headers.Add(Header("In-Reply-To", scenario is "wrong_in_reply_to" or "rfc_only_wrong"
                    ? "<other@example.test>" : "<parent@example.test>"));
            if (scenario != "missing_references")
                headers.Add(Header("References", scenario == "wrong_references" ? "<other@example.test>"
                    : scenario == "references_ancestry" ? "<ancestor@example.test> <parent@example.test>"
                    : "<parent@example.test>"));
        }
        var message = new Dictionary<string, object>
        {
            ["id"] = "sent_message", ["labelIds"] = new[] { "SENT" },
            ["payload"] = new
            {
                mimeType = "text/plain", headers,
                body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("Fixture reply.")) }
            }
        };
        if (scenario != "missing_thread")
            message["threadId"] = scenario is "wrong_thread" or "lost_ack_wrong_thread" or "thread_only_wrong"
                ? "other_thread" : "expected_thread";
        var provider = new ReadbackProvider(JsonSerializer.SerializeToElement(message));
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "mail_send", operation, plan,
            scenario == "lost_ack_wrong_thread" ? null : JsonSerializer.SerializeToElement(new { id = "sent_message" }), default);
        Assert.Equal(expectedVerified, result.HasValue);
        Assert.Equal(scenario == "lost_ack_wrong_thread" ? 2 : 1, provider.Endpoints.Count);
        Assert.EndsWith("/messages/sent_message?format=full", provider.Endpoints[^1]);
        Assert.Equal(0, provider.Writes);
        if (expectedVerified && args.ContainsKey("threadId"))
            Assert.Equal("expected_thread", result!.Value.GetProperty("threadId").GetString());
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
    private static Dictionary<string, string> Header(string name, string value) => new() { ["name"] = name, ["value"] = value };
    private sealed class ReadbackProvider(JsonElement message) : IConnectorProvider
    {
        internal readonly List<string> Endpoints = [];
        internal int Writes;
        public Task<JsonElement> RequestAsync(string account, string endpoint, string method, object? body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Endpoints.Add(endpoint);
            if (method != "GET") Writes++;
            Assert.Equal("ca_test123", account); Assert.Equal("GET", method); Assert.Null(body);
            return Task.FromResult(endpoint.Contains("/messages?q=", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(new { messages = new[] { new { id = "sent_message", threadId = "expected_thread" } } })
                : message);
        }
        public Task<ProviderConnectReply> ConnectAsync(string user, string config, string callback, string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderConnection?> GetAsync(string account, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync(string account, CancellationToken ct) => throw new NotSupportedException();
    }
}