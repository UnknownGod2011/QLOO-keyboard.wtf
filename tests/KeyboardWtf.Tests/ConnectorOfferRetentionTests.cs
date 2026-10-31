namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorOfferRetentionTests
{
    [Fact]
    public async Task Ambiguous_offer_acceptance_returns_clarification_instead_of_dead_task_or_new_read()
    {
        await using var f = new Fixture();
        f.User = "Read the identified email conversation."; f.Identity = "initial-read";
        await f.Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
        var origin = f.TaskId;
        await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
        { task_id = origin, capability_id = "mail_thread", arguments_json = "{\"id\":\"aaaabbbb\"}" }), false, default);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        f.Controller.ObserveCompletedReply("Which message or detail would you like to inspect?", f.Identity);
        var before = (f.Resolves, f.Executions);
        f.User = "Yes please."; f.Identity = "ambiguous-yes";
        var result = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
        { task_id = origin, capability_id = "mail_thread", arguments_json = "{\"id\":\"stale-reference\"}" }), false, default);
        Assert.Equal("clarification_required", result["state"]);
        Assert.Equal("ambiguous_followup", result["error_category"]);
        Assert.Equal(false, result["connector_disconnected"]);
        Assert.Equal(before, (f.Resolves, f.Executions));
        Assert.DoesNotContain("no longer active", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Details_offer_after_cached_question_belongs_to_that_answer_and_yes_recovers_a_fresh_read()
    {
        await using var f = new Fixture();
        f.User = "Read the identified email conversation."; f.Identity = "initial-read";
        await f.Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
        var origin = f.TaskId;
        var initial = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
        {
            task_id = origin, capability_id = "mail_thread", arguments_json = "{\"id\":\"aaaabbbb\"}"
        }), false, default);
        Assert.True(initial.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(initial));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        f.Controller.ObserveCompletedReply("I found and read the conversation.", f.Identity);

        // This real controller shortcut must answer without another provider
        // call, but its completed reply can still make a new read-only offer.
        f.User = "What did she ask me for?"; f.Identity = "cached-question";
        var beforeQuestion = (f.Resolves, f.Executions);
        var question = await f.Controller.ExecuteAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = origin, action = "result" }), false, default);
        Assert.True(question.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(question));
        Assert.Equal("retained_context", question["state"]);
        Assert.Contains(Fixture.Body, JsonSerializer.Serialize(question));
        Assert.Equal(beforeQuestion, (f.Resolves, f.Executions));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        const string offer = "She asked for the proposal. Would you like me to read the full messages?";
        Assert.True(ConnectorFollowupIntent.OffersMailDetails(offer));
        f.Controller.ObserveCompletedReply(offer, f.Identity);

        f.User = "Yes please."; f.Identity = "accepted-cached-question-offer";
        var details = await f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
        {
            task_id = origin, capability_id = "mail_thread", arguments_json = "{\"id\":\"stale-model-reference\"}"
        }), false, default);
        Assert.True(details.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(details));
        Assert.NotEqual(origin, f.TaskId);
        Assert.Equal(beforeQuestion.Resolves + 1, f.Resolves);
        Assert.Equal(beforeQuestion.Executions + 1, f.Executions);
        Assert.True(f.ResolvedSameAccount);
        Assert.Equal("aaaabbbb", f.LastReadId);
        Assert.Contains(Fixture.Body, JsonSerializer.Serialize(details));
        f.Controller.CompleteSettledUserTurn(f.Identity);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Body = "Alex asked for the research proposal. The recipient agreed to send it tomorrow.";
        private readonly CancellationTokenSource _session = new();
        private readonly string _account = Guid.NewGuid().ToString("N");
        internal string User = "", Identity = "", TaskId, LastReadId;
        internal int Resolves, Executions;
        internal bool ResolvedSameAccount;
        internal readonly JarvisConnectorController Controller;

        internal Fixture() => Controller = new(Request, () => "fixture-owner", () => _session.Token, () => User,
            (_, _) => Task.FromResult(false), userRequestIdentity: () => Identity,
            userRequestSnapshot: () => new(User, Identity), preferDirectReadReceipt: true);

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            var args = JsonSerializer.SerializeToElement(body);
            if (path == "v1/connectors/resolve")
            {
                Resolves++;
                TaskId = Guid.NewGuid().ToString("N");
                ResolvedSameAccount = args.TryGetProperty("accountId", out var account) && account.GetString() == _account;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    taskId = TaskId, state = "ready", connectorId = "gmail", accountId = _account,
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    choices = new[] { new { accountId = _account, label = "reader@example.test" } },
                    capabilities = new[] { new { id = "mail_thread", writes = false, activity = "Reading Gmail…",
                        arguments = new Dictionary<string, string> { ["id"] = "observed thread reference" } } }
                }));
            }
            if (path == "v1/connectors/execute")
            {
                Executions++;
                Assert.Equal("mail_thread", args.GetProperty("capabilityId").GetString());
                LastReadId = args.GetProperty("arguments").GetProperty("id").GetString();
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    state = "completed", verified = true,
                    result = new { truncated = false, data = new { id = "aaaabbbb", totalMessages = 1,
                        messages = new[] { new { id = "11112222", threadId = "aaaabbbb", subject = "Project review",
                            text = Body, contentTruncated = false } } } }
                }));
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed" }));
        }

        public async ValueTask DisposeAsync()
        {
            _session.Cancel();
            await Controller.ResetAsync();
            _session.Dispose();
        }
    }
}
