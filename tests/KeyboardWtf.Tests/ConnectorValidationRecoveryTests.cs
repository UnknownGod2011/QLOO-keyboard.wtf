namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorValidationRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsent_calendar_validation_can_be_corrected_in_same_task_without_an_extra_confirmation(bool laterHumanRetry)
    {
        await using var f = new Fixture();
        await f.Resolve();
        var rejected = await f.Execute(corrected: false);
        Assert.False(rejected.GetValueOrDefault("ok") is true);
        Assert.Equal(false, rejected.GetValueOrDefault("this_proposal_dispatched"));
        Assert.Contains("continue this same task now", (string)rejected["instruction"]);
        Assert.Equal(1, f.ExecuteRequests);
        Assert.Equal(0, f.NativeWrites);
        Assert.Null(f.Automation.PendingSnapshot());
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));

        if (laterHumanRetry)
        {
            f.Commands.CompleteJarvisUserTurn();
            f.Commands.ObserveJarvisTranscript("Try again.", f.Session.Token);
        }
        var corrected = await f.Execute(corrected: true);
        Assert.True(corrected.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(corrected));
        Assert.Equal(1, f.Resolves);
        Assert.Equal(2, f.ExecuteRequests);
        Assert.Equal(1, f.NativeWrites);
        Assert.Null(f.Automation.PendingSnapshot());

        // A repeated model proposal reuses the successful operation instead of
        // turning argument recovery into a duplicate external Calendar write.
        var duplicate = await f.Execute(corrected: true);
        Assert.True(duplicate.GetValueOrDefault("ok") is true);
        Assert.Equal(1, f.NativeWrites);
        Assert.Equal(2, f.ExecuteRequests);
        f.Commands.CompleteJarvisUserTurn();
        Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("missing_stage")]
    [InlineData("dispatched")]
    [InlineData("claimed")]
    [InlineData("wrong_capability")]
    [InlineData("wrong_task")]
    [InlineData("outcome_unknown")]
    public async Task A_validation_label_without_exact_no_dispatch_proof_does_not_preserve_write_authority(string marker)
    {
        await using var f = new Fixture { Marker = marker };
        await f.Resolve();
        Assert.False((await f.Execute(corrected: false)).GetValueOrDefault("ok") is true);
        f.Commands.CompleteJarvisUserTurn();
        f.Commands.ObserveJarvisTranscript("Try again.", f.Session.Token);
        var retry = await f.Execute(corrected: true);
        Assert.False(retry.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(retry));
        Assert.Equal(1, f.ExecuteRequests);
        Assert.Equal(0, f.NativeWrites);
    }

    [Fact]
    public async Task Rejecting_a_later_proposal_preserves_the_prior_verified_write_fence()
    {
        await using var f = new Fixture { RejectedRequest = 2 };
        await f.Resolve();
        Assert.True((await f.Execute(corrected: true)).GetValueOrDefault("ok") is true);
        Assert.False((await f.Execute(corrected: false)).GetValueOrDefault("ok") is true);
        var resolvedAgain = await f.Commands.ExecuteJarvisToolAsync("connector_resolve",
            JsonSerializer.SerializeToElement(new { connector_id = "googlecalendar" }), default);
        Assert.Equal(f.TaskId, resolvedAgain.GetValueOrDefault("task_id"));
        Assert.Equal(1, f.Resolves);

        // While correction is pending, same-task resolve is deliberately still
        // usable. End that authority explicitly before observing the fence that
        // must survive the rejected later proposal and completed task.
        await f.Complete();
        var fenced = await f.Commands.ExecuteJarvisToolAsync("connector_resolve",
            JsonSerializer.SerializeToElement(new { connector_id = "googlecalendar" }), default);
        Assert.True(fenced.GetValueOrDefault("duplicate_suppressed") is true, JsonSerializer.Serialize(fenced));
        Assert.True(fenced.GetValueOrDefault("verified") is true, JsonSerializer.Serialize(fenced));
        Assert.Equal(1, f.Resolves);
        Assert.Equal(2, f.ExecuteRequests);
        Assert.Equal(1, f.NativeWrites);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("account")]
    [InlineData("session")]
    public async Task Correctable_arguments_do_not_survive_expiry_account_change_or_session_end(string change)
    {
        await using var f = new Fixture();
        await f.Resolve();
        await f.Execute(corrected: false);
        f.Commands.CompleteJarvisUserTurn();
        if (change == "expiry") f.Now = f.Now.AddMinutes(11);
        if (change == "account") f.Owner = "another-account-owner";
        if (change == "session") f.Session.Cancel();
        try
        {
            var retry = await f.Execute(corrected: true);
            Assert.False(retry.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(retry));
        }
        catch (OperationCanceledException) when (change == "session") { }
        catch (InvalidOperationException error) when (change is "account" or "session")
        {
            Assert.Equal("This connector task belongs to an ended session or changed account.", error.Message);
        }
        Assert.Equal(1, f.ExecuteRequests);
        Assert.Equal(0, f.NativeWrites);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        private readonly string _taskId = Guid.NewGuid().ToString("N");
        private readonly string _accountId = Guid.NewGuid().ToString("N");
        internal readonly CancellationTokenSource Session = new();
        internal readonly JarvisAutomationService Automation;
        internal readonly CommandRegistry Commands;
        private readonly JarvisConnectorController _controller;
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal string Owner = "fixture-account-owner";
        internal string Marker = "valid";
        internal int RejectedRequest = 1;
        internal int Resolves, ExecuteRequests, NativeWrites;
        internal string TaskId => _taskId;

        internal Fixture()
        {
            var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
            settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
            Automation = new JarvisAutomationService(null, settings, new(Path.Combine(_directory, "history.json")),
                new(Path.Combine(_directory, "mappings.json")), () => { });
            Commands = new CommandRegistry(null, null, null, settings, new NotificationService(null), automation: Automation);
            _controller = new JarvisConnectorController((_, path, payload, _) =>
            {
                var body = JsonSerializer.SerializeToElement(payload);
                if (path.EndsWith("/resolve"))
                {
                    ++Resolves;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        taskId = _taskId, connectorId = "googlecalendar", accountId = _accountId, state = "ready",
                        expiresAt = Now.AddMinutes(10),
                        capabilities = new[] { new { id = "calendar_create", writes = true, activity = "Creating event…",
                            arguments = new { title = "event title", start = "date or timestamp", end = "exclusive later date or timestamp" } } }
                    }));
                }
                if (path.EndsWith("/execute"))
                {
                    ++ExecuteRequests;
                    Assert.Equal(_taskId, body.GetProperty("taskId").GetString());
                    Assert.Equal("calendar_create", body.GetProperty("capabilityId").GetString());
                    Assert.True(body.GetProperty("confirmed").GetBoolean());
                    if (ExecuteRequests == RejectedRequest)
                    {
                        var detail = new Dictionary<string, object>
                        {
                            ["retryableWithCorrectedArguments"] = true,
                            ["capabilityId"] = Marker == "wrong_capability" ? "mail_send" : "calendar_create",
                            ["expectedArguments"] = new { title = "event title", start = "YYYY-MM-DD", end = "exclusive later YYYY-MM-DD" },
                            ["validationMessage"] = "The end date must be after the start date.",
                            ["instruction"] = "No provider action was sent. Correct arguments and continue this authorized task."
                        };
                        if (Marker != "missing_stage")
                        {
                            detail["providerDispatched"] = Marker == "dispatched";
                            detail["operationClaimed"] = Marker == "claimed";
                        }
                        return Task.FromResult(JsonSerializer.SerializeToElement(new
                        {
                            taskId = Marker == "wrong_task" ? Guid.NewGuid().ToString("N") : _taskId,
                            operationId = "", state = Marker == "outcome_unknown" ? "outcome_unknown" : "validation_required",
                            verified = false, result = detail
                        }));
                    }
                    Assert.Equal("2026-11-22", body.GetProperty("arguments").GetProperty("end").GetString());
                    ++NativeWrites;
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { taskId = _taskId,
                        operationId = "verified-event-operation", state = "completed", verified = true, result = new { id = "verified-event" } }));
                }
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "cancelled" }));
            }, () => Owner, () => Session.Token, () => Commands.ConnectorUserRequest,
                (_, _) => Task.FromResult(true), userRequestIdentity: () => Commands.ConnectorUserRequestIdentity,
                utcNow: () => Now, userRequestSnapshot: () => Commands.ConnectorUserRequestSnapshot);
            Automation.Connectors = _controller;
        }

        internal async Task Resolve()
        {
            Commands.ObserveJarvisTranscript("Set a reminder on November 21 to lock in.", Session.Token);
            var receipt = await Commands.ExecuteJarvisToolAsync("connector_resolve",
                JsonSerializer.SerializeToElement(new { connector_id = "googlecalendar" }), default);
            Assert.True(receipt.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(receipt));
        }

        internal Task<Dictionary<string, object>> Execute(bool corrected) => Commands.ExecuteJarvisToolAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = _taskId, capability_id = "calendar_create",
                arguments_json = JsonSerializer.Serialize(new { title = "lock in", start = "2026-11-21",
                    end = corrected ? "2026-11-22" : "2026-11-21" }) }), default);

        internal Task<Dictionary<string, object>> Complete() => Commands.ExecuteJarvisToolAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = _taskId, action = "complete" }), default);

        public async ValueTask DisposeAsync()
        {
            await _controller.ResetAsync();
            Automation.Dispose();
            Session.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
