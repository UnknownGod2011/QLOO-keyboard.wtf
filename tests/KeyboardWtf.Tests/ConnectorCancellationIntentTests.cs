using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Connectors;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class ConnectorCancellationIntentTests
{
    [Theory]
    [InlineData("stop the Gmail search", "gmail")]
    [InlineData("Please cancel my Gmail task!", "gmail")]
    [InlineData("cancel that Gmail connector task.", "gmail")]
    [InlineData("stop the Google Calendar task", "googlecalendar")]
    [InlineData("cancel this Calendar search", "googlecalendar")]
    [InlineData("stop my Google Drive search", "googledrive")]
    [InlineData("cancel the Drive task", "googledrive")]
    [InlineData("stop the Google Docs task", "googledocs")]
    [InlineData("cancel the Docs task", "googledocs")]
    [InlineData("stop the Forms task", "googleforms")]
    [InlineData("cancel the Google Forms task", "googleforms")]
    [InlineData("stop the Meet task", "googlemeet")]
    [InlineData("cancel the Google Meet task", "googlemeet")]
    [InlineData("stop the GitHub search", "github")]
    [InlineData("cancel the Git Hub task", "github")]
    [InlineData("stop the YouTube task", "youtube")]
    [InlineData("cancel the You Tube search", "youtube")]
    [InlineData("stop the Slack search", "slack")]
    [InlineData("cancel the Discord task", "discord")]
    [InlineData("stop the Reddit task", "reddit")]
    [InlineData("cancel the LinkedIn search", "linkedin")]
    [InlineData("cancel", "")]
    [InlineData("cancel that", "")]
    [InlineData("please stop", "")]
    [InlineData("forget it", "")]
    [InlineData("Never mind.", "")]
    public void Exact_provider_task_controls_are_parsed_without_fuzzy_matching(string request, string provider)
    {
        Assert.True(JarvisConnectorController.IsExplicitCancellation(request));
        Assert.True(JarvisConnectorController.TryParseExplicitCancellation(request, out var parsed));
        Assert.Equal(provider, parsed);
    }

    [Theory]
    [InlineData("stop Chrome")]
    [InlineData("stop Spotify")]
    [InlineData("stop Gmail")]
    [InlineData("cancel my subscription")]
    [InlineData("stop the browser task")]
    [InlineData("stop the selected text translation")]
    [InlineData("stop the Gmal search")]
    [InlineData("stop the Google search")]
    [InlineData("don't stop the Gmail search")]
    [InlineData("do not cancel the Gmail task")]
    [InlineData("Can you stop the Gmail search?")]
    [InlineData("Should I stop the Gmail search?")]
    [InlineData("Explain how to cancel the Gmail task")]
    [InlineData("The message says stop the Gmail search")]
    [InlineData("\"stop the Gmail search\"")]
    [InlineData("‘cancel the Gmail task’")]
    [InlineData("stop the Gmail search and send an email")]
    [InlineData("uh stop hmm Gmail")]
    [InlineData("What is taking so long?")]
    public void Questions_noise_quoted_text_and_unrelated_controls_are_not_cancellation(string request)
    {
        Assert.False(JarvisConnectorController.IsExplicitCancellation(request));
        Assert.False(JarvisConnectorController.TryParseExplicitCancellation(request, out _));
    }

    [Fact]
    public void Missing_and_unbounded_input_do_not_enter_control_parsing()
    {
        Assert.False(JarvisConnectorController.IsExplicitCancellation(null));
        Assert.False(JarvisConnectorController.IsExplicitCancellation(""));
        Assert.False(JarvisConnectorController.IsExplicitCancellation(new string(' ', 3001) + "cancel"));
    }

    [Fact]
    public async Task Named_control_cancels_only_the_correct_current_provider_owner_among_other_tasks()
    {
        await using var f = new Fixture();
        var gmail = await f.Resolve("gmail");
        var github = await f.Resolve("github");
        Assert.False(f.Controller.TryCancelSingleTask("cancel", out _));
        Assert.True(f.Controller.TryCancelSingleTask("stop the Gmail search", out var result));
        Assert.Equal(gmail, result["task_id"]); Assert.Equal("cancelled", result["state"]);
        Assert.True((bool)(await f.Control(gmail, "cancel"))["already_cancelled"]);
        Assert.Equal("ready", (await f.Control(github, "result"))["state"]);
        Assert.Single(f.Stopped); Assert.Equal(gmail, f.Stopped[0]);
    }

    [Fact]
    public async Task Named_control_does_not_guess_between_two_tasks_for_the_same_provider()
    {
        await using var f = new Fixture();
        var first = await f.Resolve("gmail");
        var second = await f.Resolve("gmail");
        Assert.False(f.Controller.TryCancelSingleTask("stop the Gmail search", out _));
        Assert.Equal("ready", (await f.Control(first, "result"))["state"]);
        Assert.Equal("ready", (await f.Control(second, "result"))["state"]);
        Assert.Empty(f.Stopped);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("account_switched")]
    [InlineData("session_ended")]
    [InlineData("completed")]
    public async Task Named_control_never_selects_stale_or_ended_task_authority(string boundary)
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail");
        switch (boundary)
        {
            case "expired": f.Now = f.Now.AddMinutes(11); break;
            case "account_switched": f.Owner = "different-firebase-owner"; break;
            case "session_ended": f.Session.Cancel(); break;
            case "completed": await f.Control(id, "complete"); break;
        }
        var stopsBefore = f.Stopped.Count;
        Assert.False(f.Controller.TryCancelSingleTask("stop the Gmail search", out _));
        Assert.Equal(stopsBefore, f.Stopped.Count);
    }

    [Fact]
    public async Task Wrong_named_provider_cannot_cancel_the_single_other_provider_task()
    {
        await using var f = new Fixture();
        var id = await f.Resolve("github");
        Assert.False(f.Controller.TryCancelSingleTask("stop the Gmail search", out _));
        Assert.Equal("ready", (await f.Control(id, "result"))["state"]); Assert.Empty(f.Stopped);
        Assert.True(f.Controller.TryCancelSingleTask("cancel", out var result)); Assert.Equal(id, result["task_id"]);
    }

    [Fact]
    public async Task Ordinary_input_does_not_interrupt_actual_work_and_named_cancel_fences_late_result()
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail");
        f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
        {
            task_id = id, capability_id = "mail_search", arguments_json = "{\"query\":\"topic\"}"
        }), false, default);
        await f.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(f.Controller.TryCancelSingleTask("How long is it taking?", out _));
        Assert.False(f.Controller.TryCancelSingleTask("Open Spotify", out _));
        Assert.False(f.WorkToken.IsCancellationRequested); Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.True(f.Controller.TryCancelSingleTask("stop the Gmail search", out var cancelled));
        Assert.Equal(id, cancelled["task_id"]); Assert.True(f.WorkToken.IsCancellationRequested);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        f.Pending.TrySetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
        await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, f.Notices); Assert.Single(f.Stopped); Assert.Equal(1, f.Executions);
        Assert.False(f.Controller.TryCancelSingleTask("stop the Gmail search", out _));
    }

    [Theory]
    [InlineData("How long is it taking?")]
    [InlineData("Open Spotify")]
    [InlineData("don't cancel the Gmail task")]
    [InlineData("\"stop the Gmail search\"")]
    [InlineData("stop the GitHub search")]
    public async Task Model_cancel_cannot_replace_clear_current_human_authority(string currentRequest)
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail"); f.User = currentRequest;
        var reply = await f.Control(id, "cancel");
        Assert.False((bool)reply["ok"]); Assert.Equal("cancellation_not_authorized", reply["state"]);
        Assert.False((bool)reply["this_proposal_dispatched"]);
        Assert.Equal("ready", (await f.Control(id, "result"))["state"]); Assert.Empty(f.Stopped);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("cancel", "github")]
    [InlineData("stop the Gmail search", "gmail")]
    public async Task Model_task_id_cannot_disambiguate_multiple_human_cancellation_targets(string currentRequest, string secondProvider)
    {
        await using var f = new Fixture();
        var first = await f.Resolve("gmail"); var second = await f.Resolve(secondProvider); f.User = currentRequest;
        foreach (var id in new[] { first, second })
        {
            var reply = await f.Control(id, "cancel");
            Assert.False((bool)reply["ok"]); Assert.Equal("cancellation_not_authorized", reply["state"]);
            Assert.Equal("ready", (await f.Control(id, "result"))["state"]);
        }
        Assert.Empty(f.Stopped);
    }

    [Fact]
    public async Task Model_cancel_requires_the_uniquely_named_provider_owner_not_another_valid_task_id()
    {
        await using var f = new Fixture();
        var gmail = await f.Resolve("gmail"); var github = await f.Resolve("github"); f.User = "stop the Gmail search";
        Assert.False((bool)(await f.Control(github, "cancel"))["ok"]); Assert.Empty(f.Stopped);
        var cancelled = await f.Control(gmail, "cancel");
        Assert.True((bool)cancelled["ok"]); Assert.Equal("cancelled", cancelled["state"]);
        Assert.Equal(gmail, Assert.Single(f.Stopped));
        Assert.Equal("ready", (await f.Control(github, "result"))["state"]);
    }

    [Theory]
    [InlineData("cancel that")]
    [InlineData("stop the Gmail search")]
    public async Task Model_cancel_uses_the_supported_human_control_and_keeps_terminal_replay_idempotent(string currentRequest)
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail"); f.User = currentRequest;
        Assert.True((bool)(await f.Control(id, "cancel"))["ok"]); Assert.Equal(id, Assert.Single(f.Stopped));
        f.User = "Open Spotify";
        var repeat = await f.Control(id, "cancel");
        Assert.True((bool)repeat["already_cancelled"]); Assert.Single(f.Stopped);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Model_cancel_of_completed_task_does_not_change_the_verified_terminal_outcome()
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail"); await f.Control(id, "complete"); f.User = "Open Spotify";
        var repeat = await f.Control(id, "cancel");
        Assert.True((bool)repeat["already_completed"]); Assert.Equal("completed", repeat["state"]); Assert.Empty(f.Stopped);
    }

    [Fact]
    public async Task Model_cancel_cannot_select_an_expired_owner_even_with_a_clear_named_request()
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail"); f.Now = f.Now.AddMinutes(11); f.User = "stop the Gmail search";
        Assert.False((bool)(await f.Control(id, "cancel"))["ok"]); Assert.Empty(f.Stopped);
    }

    [Fact]
    public async Task Model_cancel_needs_human_authority_and_fences_uncooperative_late_work_after_that_authority()
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail"); f.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = f.Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            { task_id = id, capability_id = "mail_search", arguments_json = "{\"query\":\"topic\"}" }), false, default);
        await f.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.User = "How long is it taking?";
        Assert.False((bool)(await f.Control(id, "cancel"))["ok"]);
        Assert.False(f.WorkToken.IsCancellationRequested); Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        f.User = "stop the Gmail search";
        Assert.True((bool)(await f.Control(id, "cancel"))["ok"]);
        Assert.True(f.WorkToken.IsCancellationRequested); Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        f.Pending.TrySetResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
        await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, f.Notices); Assert.Equal(1, f.Executions); Assert.Equal(id, Assert.Single(f.Stopped));
        Assert.False(f.Controller.TryCancelSingleTask("stop the Gmail search", out _));
    }

    [Fact]
    public async Task Missing_document_account_uses_private_fallback_cleanup_without_human_cancel_authority()
    {
        await using var f = new Fixture { ResolveState = "account_required", User = "Read https://docs.google.com/document/d/harmlessDoc/edit" };
        Assert.Null(await f.Controller.TryReadExplicitDocumentAsync("browser_automation", default));
        Assert.Equal(0, f.Executions); Assert.Single(f.Stopped); Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.False(JarvisConnectorController.IsExplicitCancellation(f.User));
    }

    [Fact]
    public async Task Model_cannot_use_missing_document_account_as_a_trusted_cleanup_bypass()
    {
        await using var f = new Fixture { ResolveState = "account_required" };
        var id = await f.Resolve("googledocs");
        f.User = "Read https://docs.google.com/document/d/harmlessDoc/edit";
        Assert.False((bool)(await f.Control(id, "cancel"))["ok"]); Assert.Empty(f.Stopped);
    }

    [Fact]
    public async Task Cancelled_old_read_is_not_terminal_while_account_handoff_still_owns_real_work()
    {
        await using var f = new Fixture();
        var id = await f.Resolve("gmail"); f.RebindPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.User = "Use my work account";
        var handoff = f.Controller.ExecuteAsync("connector_control", JsonSerializer.SerializeToElement(new
            { task_id = id, action = "change_account", account_id = Guid.NewGuid().ToString("N") }), false, default);
        await f.RebindStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Checking account…", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
        foreach (var ordinary in new[] { "How long is it taking?", "Open Spotify", "don't cancel the Gmail task", "stop the GitHub search" })
        {
            f.User = ordinary;
            var rejected = await f.Control(id, "cancel");
            Assert.False((bool)rejected["ok"]); Assert.Equal("cancellation_not_authorized", rejected["state"]);
            Assert.False(f.RebindToken.IsCancellationRequested); Assert.Empty(f.Stopped);
            Assert.Equal("Checking account…", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(20)));
        }
        f.User = "stop the Gmail search";
        Assert.True((bool)(await f.Control(id, "cancel"))["ok"]);
        Assert.True(f.RebindToken.IsCancellationRequested); Assert.Equal(id, Assert.Single(f.Stopped));
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        f.RebindPending.SetResult(true); // The provider deliberately ignores cancellation and returns late.
        Assert.False((bool)(await handoff.WaitAsync(TimeSpan.FromSeconds(3)))["ok"]);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner); Assert.Equal(0, f.Executions);
        Assert.False(f.Controller.TryCancelSingleTask("stop the Gmail search", out _));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Owner = "original-firebase-owner", User = "", Identity = "", ResolveState = "ready";
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal readonly CancellationTokenSource Session = new();
        internal readonly List<string> Stopped = new();
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> RebindStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly JarvisConnectorController Controller;
        internal TaskCompletionSource<JsonElement> Pending;
        internal TaskCompletionSource<bool> RebindPending;
        internal CancellationToken WorkToken, RebindToken;
        internal int Executions, Notices;

        internal Fixture()
        {
            Controller = new(Request, () => Owner, () => Session.Token, () => User,
                (announcement, token) => { if (announcement.CanPublish) Notices++; return Task.FromResult(true); },
                userRequestIdentity: () => Identity, utcNow: () => Now);
        }
        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            if (path == "v1/connectors/resolve")
            {
                var request = JsonSerializer.SerializeToElement(body);
                return Metadata(request.GetProperty("connectorId").GetString(), Guid.NewGuid().ToString("N"), ResolveState);
            }
            if (path.EndsWith("/account", StringComparison.Ordinal))
            {
                RebindToken = token; RebindStarted.TrySetResult(true);
                if (RebindPending is not null) await RebindPending.Task; // Intentionally uncooperative late handoff.
                return Metadata("gmail", JsonSerializer.SerializeToElement(body).GetProperty("accountId").GetString(), "ready");
            }
            if (path == "v1/connectors/execute")
            {
                Executions++; WorkToken = token; Started.TrySetResult(true);
                if (Pending is not null) return await Pending.Task; // Intentionally uncooperative late response.
                return JsonSerializer.SerializeToElement(new { state = "completed", verified = true });
            }
            if (path.EndsWith("/cancel", StringComparison.Ordinal)) Stopped.Add(path.Split('/')[3]);
            return JsonSerializer.SerializeToElement(new { state = "cancelled" });
        }
        private JsonElement Metadata(string connector, string account, string state) => JsonSerializer.SerializeToElement(new
        {
            taskId = Guid.NewGuid().ToString("N"), state, connectorId = connector, accountId = account,
            choices = Array.Empty<object>(), expiresAt = Now.AddMinutes(10),
            capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching Gmail…", arguments = new Dictionary<string, string> { ["query"] = "Search terms" } } }
        });
        internal async Task<string> Resolve(string provider)
        {
            Identity = Guid.NewGuid().ToString("N"); User = "Find a recent " + provider + " item";
            var reply = await Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = provider }), false, default);
            return ((JsonElement)reply["connector"]).GetProperty("taskId").GetString();
        }
        internal Task<Dictionary<string, object>> Control(string id, string action) => Controller.ExecuteAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = id, action }), false, default);
        public async ValueTask DisposeAsync()
        {
            Pending?.TrySetCanceled(); RebindPending?.TrySetCanceled(); await Controller.ResetAsync(); Session.Dispose();
        }
    }
}
