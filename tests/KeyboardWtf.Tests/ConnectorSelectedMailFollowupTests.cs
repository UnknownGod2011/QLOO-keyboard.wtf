namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorSelectedMailFollowupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_answer_citation_does_not_discard_another_verified_sent_candidate(bool searchHasMore)
    {
        await using var f = new Fixture(scriptedSynthesis: true, searchHasMore: searchHasMore);
        var old = await f.Retain();
        Assert.Equal(new[] { "11111111" }, f.ReadMessages);
        Assert.Empty(f.ReadThreads);
        var recall = await f.Tool("connector_control", new { task_id = old, action = "recall" });
        var context = Assert.IsType<ConnectorSessionResult>(recall["context"]);
        Assert.Equal("r2", context.TargetReference);
        Assert.Equal("aaaaaaaa", context.PrimaryThread);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa", context.ExactMailUrl());
        Assert.Equal(new[] { "aaaaaaaa", "bbbbbbbb" }, context.Resources.Select(item => item.GetProperty("threadId").GetString()).Distinct().OrderBy(value => value));
        Assert.Contains("reader@example.test", JsonSerializer.Serialize(context.Resources));
        Assert.DoesNotContain("Verified incoming content", JsonSerializer.Serialize(context.Resources));
        Assert.DoesNotContain("Verified sent-email content", JsonSerializer.Serialize(context.Resources));
        var both = context.TryResolveExactOpen(ConnectorOpenSelection.Both, out var objects, out _);
        Assert.Equal(!searchHasMore, both);
        if (both) Assert.Equal(new[] { "aaaaaaaa", "bbbbbbbb" }, objects.Select(value => value.Id).OrderBy(value => value));

        f.User = "Give me more details about the email which I had sent."; f.Identity = "sent-after-ranked-answer";
        var result = await f.Tool("connector_execute", new { task_id = old, capability_id = "mail_search",
            arguments_json = "{\"query\":\"stale unrelated search\"}" });
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal("bbbbbbbb", Assert.Single(f.ReadThreads));
        Assert.NotEqual(old, f.ReadTask);
        Assert.Equal(f.Account, f.FreshAccount);
        Assert.Equal(2, f.Resolves); Assert.Equal(1, f.Searches);
        Assert.Contains("Verified sent-email content", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("Verified incoming content", JsonSerializer.Serialize(result));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData(false, "Yeah sure give me more details about the email which I had sent.")]
    [InlineData(true, "Yeah sure give me more details about the email which I had sent.")]
    [InlineData(true, "Give me more details about the one email which I had sent.")]
    [InlineData(false, "Read the one I sent.")]
    public async Task Explicit_sent_email_after_two_results_reads_the_observed_selected_thread_under_fresh_authority(bool admission, string request)
    {
        await using var f = new Fixture();
        var oldTask = await f.Retain();
        f.User = request;
        f.Identity = "selected-details";
        if (admission) { var start = f.Controller.ReserveRetainedRead(f.User, default); Assert.NotNull(start); start(); }
        var result = await f.Tool("connector_execute", new { task_id = oldTask, capability_id = "mail_search",
            arguments_json = "{\"query\":\"stale unrelated search\"}" });
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal(2, f.Resolves);
        Assert.Equal(1, f.Searches);
        Assert.Equal("bbbbbbbb", Assert.Single(f.ReadThreads));
        Assert.NotEqual(oldTask, f.ReadTask);
        Assert.Equal(f.Account, f.FreshAccount);
        Assert.Contains("Verified sent-email content", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("Verified incoming content", JsonSerializer.Serialize(result));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Repeated_affirmative_words_recover_unique_Daisy_details_without_using_dead_task_arguments()
    {
        await using var f = new Fixture(single: true);
        var old = await f.Retain();
        f.User = "Yeah sure give me more details about that email."; f.Identity = "new-details";
        var result = await f.Tool("connector_execute", new { task_id = old, capability_id = "mail_thread",
            arguments_json = "{\"id\":\"cccccccc\"}" });
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.Equal("aaaaaaaa", Assert.Single(f.ReadThreads));
        Assert.Equal(1, f.Searches);
    }

    [Theory]
    [InlineData("recipient-only")]
    [InlineData("two-sent")]
    [InlineData("unknown-from")]
    public async Task Unproved_or_ambiguous_sender_asks_selection_instead_of_replaying_or_reporting_inactive(string shape)
    {
        await using var f = new Fixture(shape: shape);
        var old = await f.Retain();
        f.User = "Give me details about the email I sent."; f.Identity = "selected-details";
        var result = await f.Tool("connector_execute", new { task_id = old, capability_id = "mail_read",
            arguments_json = "{\"id\":\"cccccccc\"}" });
        Assert.Equal("clarification_required", result.GetValueOrDefault("state"));
        Assert.Equal("ambiguous_followup", result.GetValueOrDefault("error_category"));
        Assert.False(result.GetValueOrDefault("connector_disconnected") is true);
        Assert.Empty(f.ReadThreads);
        Assert.Equal(1, f.Searches); Assert.Equal(1, f.Resolves);
        Assert.DoesNotContain("no longer active", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task A_selected_detail_read_does_not_widen_a_headers_only_scope()
    {
        await using var f = new Fixture();
        var old = await f.Retain(headersOnly: true);
        f.User = "Give me details about the email I sent."; f.Identity = "selected-details";
        var result = await f.Tool("connector_execute", new { task_id = old, capability_id = "mail_thread",
            arguments_json = "{\"id\":\"bbbbbbbb\"}" });
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Empty(f.ReadThreads); Assert.Equal(1, f.Searches);
        Assert.Contains("headers only", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string User = "", Identity = "", ReadTask, FreshAccount;
        internal readonly string Account = Guid.NewGuid().ToString("N");
        internal int Resolves, Searches;
        internal readonly List<string> ReadThreads = [];
        internal readonly List<string> ReadMessages = [];
        private readonly CancellationTokenSource _session = new();
        private readonly bool _single;
        private readonly string _shape;
        private readonly bool _scriptedSynthesis, _searchHasMore;
        private int _models;
        internal readonly JarvisConnectorController Controller;

        internal Fixture(bool single = false, string shape = "normal", bool scriptedSynthesis = false, bool searchHasMore = false)
        {
            _single = single; _shape = shape; _scriptedSynthesis = scriptedSynthesis; _searchHasMore = searchHasMore;
            Controller = new(Request, () => "owner", () => _session.Token,
                () => User, (_, _) => Task.FromResult(false), userRequestIdentity: () => Identity,
                userRequestSnapshot: () => new(User, Identity), preferDirectReadReceipt: !scriptedSynthesis,
                reasonRead: scriptedSynthesis ? (_, _, _, prompt, _, _, _) => Model(prompt) : null);
        }

        private Task<string> Model(string prompt)
        {
            _models++;
            if (_models == 1) return Task.FromResult(JsonSerializer.Serialize(new
            {
                action = "execute", capability = "mail_read", arguments_json = "{\"id\":\"11111111\"}",
                answer = "", references = Array.Empty<string>(), partial = false
            }));
            var initial = Identity == "search-turn";
            Assert.Contains(initial ? "Verified incoming content" : "Verified sent-email content", prompt);
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                action = "answer", capability = "", arguments_json = "{}",
                answer = initial ? "Verified incoming content describes the internship opportunity." : "Verified sent-email content gives the research proposal details.",
                references = new[] { initial ? "r2" : "r1" }, partial = false,
                goal_status = "matched", target_reference = initial ? "r2" : "r1"
            }));
        }

        internal async Task<string> Retain(bool headersOnly = false)
        {
            User = headersOnly ? "Find the internship emails, headers only." : "Find the internship emails."; Identity = "search-turn";
            var resolved = await Tool("connector_resolve", new { connector_id = "gmail" });
            var task = (string)resolved["task_id"];
            var found = await Tool("connector_execute", new { task_id = task, capability_id = "mail_search",
                arguments_json = "{\"query\":\"internship\",\"matchMode\":\"exact\"}" });
            Assert.True(found.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(found));
            await Tool("connector_control", new { task_id = task, action = "complete" });
            return task;
        }

        internal Task<Dictionary<string, object>> Tool(string name, object args) =>
            Controller.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), false, default);

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            var args = JsonSerializer.SerializeToElement(body);
            if (path.EndsWith("/resolve"))
            {
                Resolves++; FreshAccount = args.TryGetProperty("accountId", out var account) ? account.GetString() : null;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { taskId = Guid.NewGuid().ToString("N"),
                    state = "ready", connectorId = "gmail", accountId = Account, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    choices = new[] { new { accountId = Account, label = "reader@example.test" } },
                    capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching…",
                        arguments = new Dictionary<string, string> { ["query"] = "query", ["matchMode"] = "mode" } },
                        new { id = "mail_thread", writes = false, activity = "Reading…", arguments = new Dictionary<string, string> { ["id"] = "thread" } },
                        new { id = "mail_read", writes = false, activity = "Reading…", arguments = new Dictionary<string, string> { ["id"] = "message" } } } }));
            }
            if (!path.EndsWith("/execute")) return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
            if (args.GetProperty("capabilityId").GetString() == "mail_search")
            {
                Searches++;
                object Message(string id, string thread, string from) => new { id, threadId = thread, from,
                    headers = new[] { new { name = "From", value = from }, new { name = "To", value = "reader@example.test" } } };
                var messages = new List<object> { Message("11111111", "aaaaaaaa", _shape == "two-sent" ? "reader@example.test" : "Anika <anika@example.test>") };
                if (!_single) messages.Add(Message("22222222", "bbbbbbbb", _shape is "recipient-only" ? "other@example.test" : _shape is "unknown-from" ? "" : "Reader <reader@example.test>"));
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { messages, hasMore = _searchHasMore, pageItemCount = messages.Count } } }));
            }
            var input = args.GetProperty("arguments");
            if (_scriptedSynthesis && args.GetProperty("capabilityId").GetString() == "mail_read")
            {
                var message = input.GetProperty("id").GetString(); ReadMessages.Add(message);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { id = message, threadId = "aaaaaaaa", from = "Anika <anika@example.test>",
                        text = "Verified incoming content.", contentTruncated = false } } }));
            }
            var threadId = input.GetProperty("id").GetString();
            ReadThreads.Add(threadId); ReadTask = args.GetProperty("taskId").GetString();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = false, data = new { id = threadId, totalMessages = 1, messages = new[] {
                    new { id = threadId == "bbbbbbbb" ? "22222222" : "11111111", threadId,
                        text = threadId == "bbbbbbbb" ? "Verified sent-email content." : "Verified incoming content.", contentTruncated = false } } } } }));
        }

        public async ValueTask DisposeAsync() { await Controller.ResetAsync(); _session.Dispose(); }
    }
}
