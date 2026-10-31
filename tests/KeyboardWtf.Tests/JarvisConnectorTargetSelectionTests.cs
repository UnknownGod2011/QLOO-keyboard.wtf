// Real Controller -> Reader -> complete -> recall with synthetic backend and
// reasoning callbacks; no profile, provider, real model, GUI/browser, microphone
// or external write. Open controls return a URL, not navigation/rendered proof.
namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class JarvisConnectorTargetSelectionTests
{
    private const string Human = "Find the BioCyclic winning email and tell me the prize.";
    private const string Supporting = "aaaaaaaa11111111", Winner = "cccccccc33333333";
    private const string Prize = "BioCyclic won Top 10 Best Performers.";
    private static JsonElement Read(object data) => JsonSerializer.SerializeToElement(new
        { state = "completed", verified = true, result = new { source = "external_untrusted", truncated = false, data } });
    private static string Answer(string status, string target, string[] refs, string answer = Prize, bool legacy = false) =>
        JsonSerializer.Serialize(legacy ? (object)new { action = "answer", capability = "", arguments_json = "", answer, references = refs, partial = false }
            : new { action = "answer", capability = "", arguments_json = "", answer, references = refs, partial = status != "matched", goal_status = status, target_reference = target });
    private static string Execute(string cap, string id) => JsonSerializer.Serialize(new
        { action = "execute", capability = cap, arguments_json = JsonSerializer.Serialize(new { id }), answer = "", references = Array.Empty<string>(), partial = false });

    [Fact]
    public async Task Explicit_target_survives_actual_completion_recall_and_owned_url_resolution_without_research_replay()
    {
        await using var f = new Fixture();
        f.Responses.Enqueue(Execute("mail_read", "11111111")); f.Responses.Enqueue(Execute("mail_read", "33333333"));
        f.Responses.Enqueue(Answer("matched", "r3", ["r1", "r2", "r3"]));
        await f.Resolve(); var result = await f.Step("mail_search", "{\"query\":\"BioCyclic\"}");
        Assert.True((bool)result["ok"]);
        var data = ((JsonElement)result["connector"]).GetProperty("result").GetProperty("data");
        Assert.Equal(new[] { "r1", "r2", "r3" }, data.GetProperty("evidenceReferences").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal("r3", data.GetProperty("targetReference").GetString());
        Assert.Equal("evidence_available", data.GetProperty("goalOutcome").GetString());
        Assert.Equal("matched", data.GetProperty("goalStatus").GetString());
        Assert.True((bool)(await f.Control("complete"))["ok"]); Assert.Equal(1, f.Completes);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        var recall = await f.Control("recall"); Assert.True((bool)recall["ok"]); Assert.False((bool)recall["action_authority"]);
        var context = Assert.IsType<ConnectorSessionResult>(recall["context"]);
        Assert.Equal(f.TaskId, context.TaskId); Assert.Equal("gmail", context.Provider); Assert.Equal(f.AccountId, context.AccountId);
        Assert.Equal("reader@example.test", context.AccountLabel); Assert.Equal(Winner, context.PrimaryThread);
        Assert.Equal("r3", context.TargetReference); Assert.Equal(Prize, context.Answer);
        Assert.Equal(2, context.Resources.Length); Assert.Equal(3, f.Models); Assert.Equal(3, f.Executes);
        f.User = "Open that email";
        var opened = await f.Control("open"); Assert.True((bool)opened["ok"]); Assert.False((bool)opened["navigation_verified"]);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/" + Winner, opened["url"]);
        Assert.True(f.Controller.TryResolveExactMailOpen(f.User, out var same, out var rejection)); Assert.Null(rejection); Assert.Equal(opened["url"], same);
        Assert.Equal(3, f.Models); Assert.Equal(3, f.Executes); Assert.Equal(1, f.Resolves); Assert.Equal(1, f.Completes);
        Assert.False((bool)(await f.Step("mail_send", "{}"))["ok"]); Assert.Equal(3, f.Executes);
        f.Owner = "other-owner";
        Assert.False((bool)(await f.Control("recall"))["ok"]); Assert.False(f.Controller.TryResolveExactMailOpen(f.User, out _, out _));
        Assert.Equal(3, f.Models); Assert.Equal(3, f.Executes);
    }

    [Theory] [InlineData("not_found")] [InlineData("ambiguous")]
    public async Task Negative_or_ambiguous_single_support_citation_retains_answer_but_cannot_open_after_completion(string status)
    {
        await using var f = new Fixture();
        f.Responses.Enqueue(Execute("mail_read", "11111111")); f.Responses.Enqueue(Execute("mail_read", "33333333"));
        const string answer = "Observed correspondence did not establish a matching winner.";
        f.Responses.Enqueue(Answer(status, "", ["r2"], answer));
        await f.Resolve(); Assert.True((bool)(await f.Step("mail_search", "{\"query\":\"BioCyclic\"}"))["ok"]);
        Assert.True((bool)(await f.Control("complete"))["ok"]);
        var recall = await f.Control("recall"); var context = Assert.IsType<ConnectorSessionResult>(recall["context"]);
        Assert.Equal(answer, context.Answer); Assert.Equal(status, context.GoalStatus);
        // Verified candidates remain available for a later explicit selection;
        // retaining their headers must not manufacture a matched target.
        Assert.Equal(2, context.Resources.Length);
        Assert.Single(context.Resources.Where(resource => resource.GetProperty("bodyContentObserved").GetBoolean()));
        Assert.Equal(f.AccountId, context.AccountId); Assert.False(context.ExactResourceSelectionAllowed);
        Assert.Null(context.PrimaryThread); Assert.Null(context.ExactMailUrl()); Assert.Null(context.ExactResourceUrl());
        f.User = "Open that email";
        Assert.False((bool)(await f.Control("open"))["ok"]);
        Assert.True(f.Controller.TryResolveExactMailOpen(f.User, out var url, out var rejection)); Assert.Null(url); Assert.NotNull(rejection);
        Assert.Equal(1, f.Completes); Assert.Equal(3, f.Models); Assert.Equal(3, f.Executes); Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Guidance_during_answer_keeps_target_and_answer_on_same_current_revision_with_old_bodies_partial()
    {
        await using var f = new Fixture { HoldModel = 3 };
        f.Responses.Enqueue(Execute("mail_read", "11111111")); f.Responses.Enqueue(Execute("mail_read", "33333333"));
        f.Responses.Enqueue(Answer("matched", "r2", ["r2"], "Stale supporting target."));
        f.Responses.Enqueue(Answer("matched", "r3", ["r2", "r3"]));
        await f.Resolve(); var running = f.Step("mail_search", "{\"query\":\"BioCyclic\"}");
        await f.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.User = "Focus on the winning message."; f.Identity = "observed-guidance";
        var guide = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = f.TaskId, action = "guide", instruction = f.User, expected_goal_revision = 0 }), true, default);
        Assert.True((bool)guide["ok"]); Assert.Equal(1, guide["goal_revision"]); f.Release.TrySetResult(true);
        var result = await running;
        if (result.TryGetValue("started", out var started) && started is true)
        {
            await f.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            result = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
                { task_id = f.TaskId, action = "result", operation_id = result["operation_id"] }), true, default);
        }
        Assert.True((bool)result["ok"]);
        var receipt = ((JsonElement)result["connector"]).GetProperty("result"); Assert.True(receipt.GetProperty("truncated").GetBoolean());
        Assert.Equal("r3", receipt.GetProperty("data").GetProperty("targetReference").GetString());
        Assert.DoesNotContain("Stale supporting target.", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.True((bool)(await f.Control("complete"))["ok"]);
        var context = Assert.IsType<ConnectorSessionResult>((await f.Control("recall"))["context"]);
        Assert.Equal(1, context.GoalRevision); Assert.Equal(new[] { f.User }, context.AcceptedReadGuidance);
        Assert.All(context.EvidenceGoalRevisions, revision => Assert.Equal(0, revision)); Assert.True(context.Partial);
        Assert.Equal("r3", context.TargetReference); Assert.Equal(Prize, context.Answer); Assert.Equal(Winner, context.PrimaryThread);
        Assert.Contains(f.User, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(4, f.Models); Assert.Equal(3, f.Executes); Assert.Equal(1, f.Completes);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Actual_controller_legacy_two_literal_scope_never_opens_one_cited_document(bool readSecond)
    {
        await using var f = new Fixture("googledocs", "Summarize documents literalOne and literalTwo");
        if (readSecond) f.Responses.Enqueue(Execute("docs_read", "literalTwo"));
        f.Responses.Enqueue(Answer("matched", "", ["r1"], "One observed document was summarized.", legacy: true));
        await f.Resolve(); Assert.True((bool)(await f.Step("docs_read", "{\"id\":\"literalOne\"}"))["ok"]);
        Assert.True((bool)(await f.Control("complete"))["ok"]);
        var context = Assert.IsType<ConnectorSessionResult>((await f.Control("recall"))["context"]);
        Assert.Equal("googledocs", context.Provider); Assert.Equal(f.AccountId, context.AccountId); Assert.Single(context.ObjectReferences);
        Assert.Equal("", context.TargetReference); Assert.False(context.ExactResourceSelectionAllowed); Assert.Null(context.ExactResourceUrl());
        f.User = "Open that result"; Assert.False((bool)(await f.Control("open"))["ok"]);
        Assert.Equal(readSecond ? 2 : 1, f.Executes); Assert.Equal(readSecond ? 2 : 1, f.Models); Assert.Equal(1, f.Completes);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string TaskId = Guid.NewGuid().ToString("N"), AccountId = Guid.NewGuid().ToString("N"), Logical = Guid.NewGuid().ToString("N");
        internal string Owner = "fixture-owner", Identity = "originating-turn", User;
        internal readonly string Provider, Original;
        internal readonly CancellationTokenSource Session = new();
        internal readonly Queue<string> Responses = new();
        internal readonly List<string> Prompts = new();
        internal readonly JarvisConnectorController Controller;
        internal int Resolves, Executes, Models, Completes, HoldModel;
        internal readonly TaskCompletionSource<bool> Held = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-10-12T00:00:00Z");
        internal Fixture(string provider = "gmail", string human = Human)
        {
            Provider = provider; User = Original = human;
            Controller = new(Request, () => Owner, () => Session.Token, () => User,
                (notice, _) => { if (notice.CanPublish) Settled.TrySetResult(true); return Task.FromResult(true); }, userRequestIdentity: () => Identity, utcNow: () => _now,
                logicalLiveSessionId: () => Logical, reasonRead: Reason);
        }
        private async Task<string> Reason(string task, string logical, string instruction, string prompt, object schema, int limit, CancellationToken token)
        {
            Assert.Equal(TaskId, task); Assert.Equal(Logical, logical); Assert.Equal("fixture-owner", Owner); Assert.Equal(2400, limit);
            Assert.Equal(Original, JsonDocument.Parse(prompt).RootElement.GetProperty("request").GetString());
            Models++; Prompts.Add(prompt); var response = Responses.Dequeue();
            if (Models == HoldModel) { Held.TrySetResult(true); await Release.Task.WaitAsync(token); }
            return response;
        }
        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal("fixture-owner", Owner);
            if (path == "v1/connectors/resolve")
            {
                Assert.Equal(HttpMethod.Post, method); Resolves++;
                var payload = JsonSerializer.SerializeToElement(body); Assert.Equal(Original, payload.GetProperty("userRequest").GetString()); Assert.Equal(Logical, payload.GetProperty("logicalLiveSessionId").GetString());
                var caps = Provider == "gmail" ? new object[]
                { new { id = "mail_search", writes = false, description = "Search", activity = "Searching Gmail…", arguments = new Dictionary<string,string> { ["query"] = "search terms", ["matchMode"] = "exact or topic", ["readMode"] = "preview", ["limit"] = "limit", ["metadataLimit"] = "limit" } },
                  new { id = "mail_read", writes = false, description = "Read", activity = "Reading Gmail…", arguments = new Dictionary<string,string> { ["id"] = "observed ID" } } }
                : new object[] { new { id = "docs_read", writes = false, description = "Read", activity = "Reading document…", arguments = new Dictionary<string,string> { ["id"] = "literal document ID" } } };
                return Task.FromResult(JsonSerializer.SerializeToElement(new { taskId = TaskId, state = "ready", connectorId = Provider, accountId = AccountId,
                    choices = new[] { new { accountId = AccountId, label = "reader@example.test" } }, expiresAt = _now.AddMinutes(10), capabilities = caps }));
            }
            if (path == "v1/connectors/execute")
            {
                Assert.Equal(HttpMethod.Post, method); Executes++;
                var payload = JsonSerializer.SerializeToElement(body); Assert.Equal(TaskId, payload.GetProperty("taskId").GetString()); Assert.Equal(Logical, payload.GetProperty("logicalLiveSessionId").GetString());
                Assert.False(payload.GetProperty("confirmed").GetBoolean()); var cap = payload.GetProperty("capabilityId").GetString(); var args = payload.GetProperty("arguments");
                if (cap == "docs_read") return Task.FromResult(Read(new { documentId = args.GetProperty("id").GetString(), body = new { content = "Actual document content." } }));
                if (cap == "mail_search")
                {
                    Assert.Equal("BioCyclic", args.GetProperty("query").GetString()); Assert.Equal("exact", args.GetProperty("matchMode").GetString()); Assert.Equal("preview", args.GetProperty("readMode").GetString());
                    return Task.FromResult(Read(new { queryUsed = "BioCyclic", messages = new[] { new { id = "11111111", threadId = Supporting }, new { id = "33333333", threadId = Winner } }, hasMore = false, pageItemCount = 2 }));
                }
                Assert.Equal("mail_read", cap); var id = args.GetProperty("id").GetString(); Assert.True(id is "11111111" or "33333333");
                return Task.FromResult(Read(new { id, threadId = id == "11111111" ? Supporting : Winner, contentTruncated = false,
                    headers = new[] { new { name = "From", value = "Devpost <fixture@example.test>" }, new { name = "Subject", value = "BioCyclic" } }, text = id == "11111111" ? "Supporting BioCyclic correspondence." : Prize }));
            }
            Assert.Equal(HttpMethod.Post, method);
            if (path == "v1/connectors/tasks/" + TaskId + "/complete") { Completes++; return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true })); }
            Assert.Equal("v1/connectors/tasks/" + TaskId + "/cancel", path); return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "cancelled", verified = true }));
        }
        internal Task<Dictionary<string, object>> Resolve() => Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = Provider }), true, default);
        internal Task<Dictionary<string, object>> Step(string cap, string arguments) => Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new { task_id = TaskId, capability_id = cap, arguments_json = arguments }), true, default);
        internal Task<Dictionary<string, object>> Control(string action) => Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new { task_id = TaskId, action }), true, default);
        public async ValueTask DisposeAsync() { Release.TrySetResult(true); await Controller.ResetAsync(); Session.Cancel(); Session.Dispose(); Assert.False(KeyboardWtfState.HasLongTaskUiOwner); }
    }
}
