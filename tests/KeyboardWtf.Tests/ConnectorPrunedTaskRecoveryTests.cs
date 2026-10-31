namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorPrunedTaskRecoveryTests
{
    [Theory]
    [InlineData("execute")]
    [InlineData("result")]
    [InlineData("status")]
    public async Task Pruned_earlier_task_does_not_block_a_new_explicit_mail_search(string proposal)
    {
        await using var f = new Fixture();
        var first = await f.CompletedRead("Find my emails with Daisy Edwards.");
        await f.CompletedRead("Find the Winter internship email."); // Resolve prunes the first task.
        f.User = "Find my emails with Dr Gao."; f.Identity = "current-gao-read";
        var result = proposal == "execute"
            ? await f.Execute(first, "mail_search", "{\"query\":\"OLD QUERY MUST NOT REPLAY\"}")
            : await f.Control(first, proposal);
        Assert.True(result.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(result));
        Assert.True(result.GetValueOrDefault("recovered_from_unavailable_task") is true);
        Assert.False((bool)result["this_proposal_dispatched"]);
        Assert.NotEqual(first, result["task_id"]);
        Assert.Equal(3, f.Resolves); Assert.Equal(2, f.Executes);
        Assert.Equal(f.User, f.LastResolve.GetProperty("userRequest").GetString());
        Assert.Equal(f.Account, f.LastResolve.GetProperty("accountId").GetString());
        Assert.DoesNotContain("OLD QUERY", JsonSerializer.Serialize(result));
        await f.Execute((string)result["task_id"], "mail_search", "{\"query\":\"Gao\"}");
        Assert.Equal(3, f.Executes);
        Assert.Equal("Gao", f.LastExecute.GetProperty("arguments").GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("mail_send", "Find my emails with Dr Gao.")]
    [InlineData("mail_thread", "Find my emails with Dr Gao.")]
    [InlineData("mail_search", "Open Gmail.")]
    [InlineData("mail_search", "Find a message in my other Gmail account.")]
    [InlineData("mail_search", "Send an email to Dr Gao.")]
    [InlineData("mail_search", "Find the email and send a reply.")]
    public async Task Missing_reference_does_not_supply_write_object_navigation_or_account_authority(string capability, string user)
    {
        await using var f = new Fixture();
        var first = await f.CompletedRead("Find my emails with Daisy Edwards.");
        await f.CompletedRead("Find the Winter internship email.");
        f.User = user; f.Identity = "new-human-request";
        var before = f.Requests;
        var result = await f.Execute(first, capability, "{\"query\":\"OLD QUERY\",\"id\":\"untrusted-object\"}");
        AssertUnavailable(result);
        Assert.Equal(before, f.Requests);
        Assert.Equal(2, f.Resolves); Assert.Equal(2, f.Executes);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("complete")]
    [InlineData("change_account")]
    [InlineData("guide")]
    public async Task Missing_control_reference_never_controls_or_recreates_another_task(string action)
    {
        await using var f = new Fixture();
        var first = await f.CompletedRead("Find my emails with Daisy Edwards.");
        await f.CompletedRead("Find the Winter internship email.");
        f.User = "Find my emails with Dr Gao."; f.Identity = "new-human-request";
        var before = f.Requests;
        AssertUnavailable(await f.Control(first, action));
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData("no-authority")]
    [InlineData("same-turn")]
    [InlineData("changed-owner")]
    [InlineData("changed-session")]
    [InlineData("expired")]
    public async Task Unknown_id_needs_current_account_session_and_a_new_human_read(string scenario)
    {
        await using var f = new Fixture();
        if (scenario != "no-authority") await f.CompletedRead("Find the Winter internship email.");
        f.User = "Find my emails with Dr Gao.";
        if (scenario != "same-turn") f.Identity = "new-human-request";
        if (scenario == "changed-owner") f.Owner = "other-owner";
        if (scenario == "changed-session") f.LogicalSession = Guid.NewGuid().ToString("N");
        if (scenario == "expired") f.Now = f.Now.AddMinutes(11);
        var before = f.Requests;
        AssertUnavailable(await f.Execute(Guid.NewGuid().ToString("N"), "mail_search", "{\"query\":\"OLD QUERY\"}"));
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("task")]
    [InlineData("another-task-id")]
    public async Task Invalid_reference_does_not_gain_the_current_accounts_read_authority(string reference)
    {
        await using var f = new Fixture();
        await f.CompletedRead("Find the Winter internship email.");
        f.User = "Find my emails with Dr Gao."; f.Identity = "new-human-request";
        var before = f.Requests;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Execute(reference, "mail_search", "{}"));
        Assert.Equal(before, f.Requests);
        var invalidType = await f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = 42, action = "result" }), false, default);
        Assert.Equal("invalid_reference", invalidType["state"]);
        Assert.Equal(before, f.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_current_mail_accounts_do_not_supply_one_recovery_account(bool native)
    {
        await using var f = new Fixture(native);
        await f.ResolveRead("Find my emails with Daisy Edwards."); // Remains active on the first account.
        f.Account = Guid.NewGuid().ToString("N");
        await f.CompletedRead("Find the Winter internship email.");
        f.User = "Find my emails with Dr Gao."; f.Identity = "new-human-request";
        var before = f.Requests;
        AssertUnavailable(await f.Execute(Guid.NewGuid().ToString("N"), "mail_search", "{\"query\":\"OLD QUERY\"}"));
        Assert.Equal(before, f.Requests);
    }

    [Fact]
    public async Task Missing_cancel_id_does_not_cancel_an_active_read_and_known_cancellation_stays_terminal()
    {
        await using var f = new Fixture();
        var task = await f.ResolveRead("Find my emails with Daisy Edwards.");
        f.User = "Cancel the search."; f.Identity = "cancel-human-request";
        var before = f.Requests;
        AssertUnavailable(await f.Control(Guid.NewGuid().ToString("N"), "cancel"));
        Assert.Equal(before, f.Requests);
        var cancelled = await f.Control(task, "cancel");
        Assert.Equal("cancelled", cancelled["state"]);
        f.User = "Find my emails with Dr Gao."; f.Identity = "new-human-request";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(task, "mail_search", "{}"));
        Assert.Equal(1, f.Resolves); Assert.Equal(0, f.Executes);
    }

    private static void AssertUnavailable(Dictionary<string, object> result)
    {
        Assert.False((bool)result["ok"]);
        Assert.Equal("task_reference_unavailable", result["state"]);
        Assert.Equal("task_lifecycle", result["error_category"]);
        Assert.False((bool)result["connector_disconnected"]);
        Assert.False((bool)result["this_proposal_dispatched"]);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _session = new();
        internal string Account = Guid.NewGuid().ToString("N");
        internal string User = "", Identity = "", Owner = "owner", LogicalSession = Guid.NewGuid().ToString("N");
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal int Requests, Resolves, Executes;
        internal JsonElement LastResolve, LastExecute;
        internal readonly JarvisConnectorController Controller;

        internal Fixture(bool native = true) => Controller = new(Request, () => Owner, () => _session.Token, () => User,
            (_, _) => Task.FromResult(false), userRequestIdentity: () => Identity,
            userRequestSnapshot: () => new(User, Identity), utcNow: () => Now,
            logicalLiveSessionId: () => LogicalSession, preferDirectReadReceipt: native);

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            Requests++;
            var args = JsonSerializer.SerializeToElement(body);
            if (path.EndsWith("/resolve"))
            {
                Resolves++; LastResolve = args;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    taskId = Guid.NewGuid().ToString("N"), state = "ready", connectorId = "gmail", accountId = Account,
                    expiresAt = Now.AddMinutes(10), choices = new[] { new { accountId = Account, label = "reader@example.test" } },
                    capabilities = new[]
                    {
                        new { id = "mail_search", writes = false, activity = "Searching Gmail…", arguments = new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" } },
                        new { id = "mail_send", writes = true, activity = "Sending email…", arguments = new Dictionary<string, string> { ["text"] = "requested text" } }
                    }
                }));
            }
            if (path.EndsWith("/execute"))
            {
                Executes++; LastExecute = args;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { messages = new[] { new { id = "aaaabbbb", threadId = "ccccdddd" } } } } }));
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed" }));
        }

        internal async Task<string> CompletedRead(string user)
        {
            var task = await ResolveRead(user);
            Assert.True((bool)(await Execute(task, "mail_search", "{\"query\":\"topic\"}"))["ok"]);
            Assert.True((bool)(await Control(task, "complete"))["ok"]);
            return task;
        }

        internal async Task<string> ResolveRead(string user)
        {
            User = user; Identity = Guid.NewGuid().ToString("N");
            var resolved = await Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new
                { connector_id = "gmail", account_id = Account }), false, default);
            return (string)resolved["task_id"];
        }

        internal Task<Dictionary<string, object>> Execute(string task, string capability, string arguments) =>
            Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new { task_id = task, capability_id = capability, arguments_json = arguments }), false, default);
        internal Task<Dictionary<string, object>> Control(string task, string action) =>
            Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new { task_id = task, action }), false, default);
        public async ValueTask DisposeAsync() { _session.Cancel(); await Controller.ResetAsync(); _session.Dispose(); }
    }
}
