namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

// A fresh full-details request must advance beyond the same cached first page.
// Provider/model delegates are inert; task ownership and read budgets are real.
[Collection("Live event integration")]
public sealed class ConnectorRetainedThreadCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_details_reads_missing_messages_without_spending_the_read_budget_on_cached_bodies(bool wireClippedChildren)
    {
        await using var f = new Fixture { WireClippedChildren = wireClippedChildren };
        var initial = await f.Initial();
        Assert.Equal(6, Content(initial).GetArrayLength());
        Assert.Equal(8, f.Reads.Count);

        var details = await f.Details();
        Assert.Equal(10, Content(details).GetArrayLength());
        Assert.Equal(new[] { "mail_thread", "mail_read:7", "mail_read:8", "mail_read:9", "mail_read:10" }, f.Reads.Skip(8));
        Assert.Equal(13, f.Reads.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(Id).OrderBy(id => id),
            Content(details).EnumerateArray().Select(message => message.GetProperty("id").GetString()).OrderBy(id => id));
        Assert.True(((JsonElement)details["connector"]).GetProperty("result").GetProperty("truncated").GetBoolean());

        // Repeating details can refresh mutable thread membership, but must
        // neither reread immutable bodies nor lose the earlier cached ones.
        var repeated = await f.Details();
        Assert.Equal(10, Content(repeated).GetArrayLength());
        Assert.Equal(14, f.Reads.Count);
        Assert.Equal("mail_thread", f.Reads.Last());
    }

    [Fact]
    public async Task Fresh_thread_membership_excludes_cached_messages_that_moved_to_another_thread()
    {
        await using var f = new Fixture { DifferentFirstReference = true };
        await f.Initial();
        var details = await f.Details();
        Assert.Equal(9, Content(details).GetArrayLength());
        Assert.DoesNotContain(Content(details).EnumerateArray(), message => message.GetProperty("id").GetString() == Id(1));
        Assert.Equal(13, f.Reads.Count);
    }

    [Fact]
    public async Task Cached_body_replaces_only_content_and_keeps_fresh_mutable_metadata()
    {
        await using var f = new Fixture { FreshMessageMetadata = true };
        await f.Initial();
        await f.Details();
        using var receipt = JsonDocument.Parse(f.LastModelEvidence[0].GetProperty("data").GetString());
        var messages = receipt.RootElement.GetProperty("result").GetProperty("data").GetProperty("messages");
        var first = messages.EnumerateArray().Single(message => message.GetProperty("id").GetString() == Id(1));
        Assert.Equal("fresh-label", first.GetProperty("labelIds")[0].GetString());
        Assert.Equal("fresh header", first.GetProperty("headers")[0].GetProperty("value").GetString());
        Assert.Contains("Verified content", first.GetProperty("text").GetString());
        Assert.False(first.GetProperty("contentTruncated").GetBoolean());
        Assert.Equal("retained_immutable_message_revalidated", first.GetProperty("bodySource").GetString());
        Assert.DoesNotContain("old-label", messages.GetRawText());
    }

    [Fact]
    public async Task Oversized_cached_bodies_stay_bounded_without_clipping_them_into_a_reread_loop()
    {
        await using var f = new Fixture { BodyLength = 15000 };
        await f.Initial();
        var before = f.Reads.Count;
        var details = await f.Details();
        Assert.True(details.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(details));
        Assert.InRange(f.Reads.Count - before, 1, 8);
        Assert.True(((JsonElement)details["connector"]).GetProperty("result").GetProperty("truncated").GetBoolean());
        Assert.True(Content(details).EnumerateArray().Sum(message => message.GetProperty("text").GetString().Length) <= 18000);
        Assert.All(Content(details).EnumerateArray(), message => Assert.True(message.GetProperty("contentClipped").GetBoolean()));
    }

    [Theory]
    [InlineData("result")]
    [InlineData("recall")]
    public async Task Headers_only_correction_cannot_reuse_body_cache_or_fetch_a_thread(string action)
    {
        await using var f = new Fixture();
        var initial = await f.Initial();
        Assert.Contains("Verified content", JsonSerializer.Serialize(initial));
        var details = await f.Details("Give me more details, but only headers; do not read message bodies.", action);
        Assert.True(details.GetValueOrDefault("ok") is true);
        Assert.Equal(8, f.Reads.Count);
        Assert.DoesNotContain("Verified content", JsonSerializer.Serialize(details));
        Assert.DoesNotContain("nativeMessageContent", JsonSerializer.Serialize(details));
        Assert.Contains("Project review", JsonSerializer.Serialize(details));

        // A metadata-only view does not destroy native evidence. A later
        // explicit body regrant can collect it without repeating provider work.
        var regrant = await f.Details("Actually read the message bodies.");
        Assert.Contains("Verified content", JsonSerializer.Serialize(regrant));
        Assert.Equal(8, f.Reads.Count);
    }

    private static JsonElement Content(Dictionary<string, object> receipt) =>
        ((JsonElement)receipt["connector"]).GetProperty("result").GetProperty("data").GetProperty("nativeMessageContent");
    private static string Id(int index) => "eeeeffff" + index.ToString("x2");

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Thread = "aaaabbbb22";
        private readonly string _account = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _session = new();
        private readonly JarvisConnectorController _controller;
        private string _user = "Find my emails with Alex Example and give me the details.", _identity = "initial-request", _task;
        private int _followups;
        internal readonly List<string> Reads = new();
        internal bool DifferentFirstReference, FreshMessageMetadata, WireClippedChildren;
        internal int BodyLength;
        internal JsonElement LastModelEvidence;

        internal Fixture()
        {
            _controller = new(Request, () => "fixture-owner", () => _session.Token, () => _user,
                (_, _) => Task.FromResult(true), userRequestIdentity: () => _identity,
                reasonRead: (_, _, _, prompt, _, _, _) =>
                {
                    using var json = JsonDocument.Parse(prompt);
                    LastModelEvidence = json.RootElement.GetProperty("evidence").Clone();
                    throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 };
                },
                userRequestSnapshot: () => new(_user, _identity), preferDirectReadReceipt: false); // Legacy automatic body-reader coverage.
        }

        internal async Task<Dictionary<string, object>> Initial()
        {
            await _controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
            return await _controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = _task, capability_id = "mail_search", arguments_json = "{\"query\":\"Alex Example\"}" }), false, default);
        }

        internal Task<Dictionary<string, object>> Details(string request = "Please summarize the entire conversation in detail.", string action = "result")
        {
            _controller.CompleteSettledUserTurn(_identity);
            _user = request; _identity = "details-" + ++_followups;
            return _controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new { task_id = _task, action }), false, default);
        }

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            if (path == "v1/connectors/resolve")
            {
                _task = Guid.NewGuid().ToString("N");
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    taskId = _task, state = "ready", connectorId = "gmail", accountId = _account,
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), choices = new[] { new { accountId = _account, label = "reader@example.test" } },
                    capabilities = new[] {
                        new { id = "mail_search", writes = false, description = "Search", activity = "Searching Gmail…", arguments = new Dictionary<string,string> { ["query"] = "query" } },
                        new { id = "mail_thread", writes = false, description = "Thread", activity = "Reading Gmail…", arguments = new Dictionary<string,string> { ["id"] = "id" } },
                        new { id = "mail_read", writes = false, description = "Message", activity = "Reading Gmail…", arguments = new Dictionary<string,string> { ["id"] = "id" } } }
                }));
            }
            if (path != "v1/connectors/execute") return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed" }));
            var payload = JsonSerializer.SerializeToElement(body);
            var capability = payload.GetProperty("capabilityId").GetString();
            var args = payload.GetProperty("arguments");
            Reads.Add(capability == "mail_read" ? capability + ":" + Convert.ToInt32(args.GetProperty("id").GetString()[8..], 16) : capability);
            object data = capability switch
            {
                "mail_search" => new { messages = new[] { new { id = Id(1), threadId = Thread } },
                    sampledMessages = new[] { new { id = Id(1), threadId = Thread, from = "Alex Example", subject = "Project review" } } },
                "mail_thread" => new { id = Thread,
                    messages = _followups > 0 && FreshMessageMetadata ? new object[] { new { id = Id(1), threadId = Thread,
                        text = "clipped", contentTruncated = true, labelIds = new[] { "fresh-label" },
                        headers = new[] { new { name = "Subject", value = "fresh header" } } } }
                    : WireClippedChildren ? Enumerable.Range(1, 10).Select(i => new {
                        id = i <= 4 ? Id(i) : "", threadId = i <= 4 ? Thread : "", internalDate = (string)null,
                        contentTruncated = true, attachmentMetadataTruncated = true, attachments = Array.Empty<object>(),
                        headers = new[] { new { name = "", value = "" } }, snippet = "", text = i <= 4 ? "clipped" : "" }).ToArray()
                    : Array.Empty<object>(),
                    messageReferences = Enumerable.Range(1, 10).Select(i => new { id = Id(i),
                        threadId = _followups > 0 && DifferentFirstReference && i == 1 ? "aaaabbbb44" : Thread }).ToArray() },
                _ => new { id = args.GetProperty("id").GetString(), threadId = Thread,
                    text = BodyLength > 0 ? new string('A', BodyLength) : "Verified content for message " + args.GetProperty("id").GetString(),
                    contentTruncated = false, labelIds = new[] { "old-label" },
                    headers = new[] { new { name = "Subject", value = "immutable header" } } }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { data, truncated = capability == "mail_thread" } }));
        }

        public async ValueTask DisposeAsync()
        {
            await _controller.ResetAsync();
            _session.Cancel(); _session.Dispose();
        }
    }
}
