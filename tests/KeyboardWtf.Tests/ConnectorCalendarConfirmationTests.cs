namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorCalendarConfirmationTests
{
    [Fact]
    public async Task AutoExecute_calendar_write_uses_current_native_authority_without_a_confirmation_prompt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        using var session = new CancellationTokenSource();
        var taskId = Guid.NewGuid().ToString("N");
        var accountId = Guid.NewGuid().ToString("N");
        var backendWrites = 0;
        var originalRequest = "Please schedule a calendar event next week to review the project.";
        var settings = new SettingsService(Path.Combine(directory, "settings.json"));
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        using var automation = new JarvisAutomationService(null, settings, new(Path.Combine(directory, "history.json")),
            new(Path.Combine(directory, "mappings.json")), () => { });
        var commands = new CommandRegistry(null, null, null, settings, new NotificationService(null), automation: automation);
        var controller = new JarvisConnectorController((_, path, payload, _) =>
        {
            var body = JsonSerializer.SerializeToElement(payload);
            if (path.EndsWith("/resolve"))
            {
                Assert.Equal(originalRequest, body.GetProperty("userRequest").GetString());
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    taskId, connectorId = "googlecalendar", accountId, state = "ready",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    capabilities = new[] { new { id = "calendar_create", writes = true, activity = "Creating event…",
                        arguments = new { title = "event title", start = "ISO timestamp", end = "ISO timestamp" } } }
                }));
            }
            if (path.EndsWith("/execute"))
            {
                ++backendWrites;
                Assert.Equal(taskId, body.GetProperty("taskId").GetString());
                Assert.Equal("calendar_create", body.GetProperty("capabilityId").GetString());
                Assert.True(body.GetProperty("confirmed").GetBoolean());
                Assert.Equal("Project review", body.GetProperty("arguments").GetProperty("title").GetString());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { id = "observed-event" } } }));
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "cancelled" }));
        }, () => "fixture-owner", () => session.Token, () => commands.ConnectorUserRequest,
            (_, _) => Task.FromResult(true), userRequestIdentity: () => commands.ConnectorUserRequestIdentity,
            userRequestSnapshot: () => commands.ConnectorUserRequestSnapshot);
        automation.Connectors = controller;
        try
        {
            commands.ObserveJarvisTranscript(originalRequest, session.Token);
            var resolved = await commands.ExecuteJarvisToolAsync("connector_resolve",
                JsonSerializer.SerializeToElement(new { connector_id = "googlecalendar" }), default);
            Assert.True(resolved.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(resolved));
            var proposal = JsonSerializer.SerializeToElement(new
            {
                task_id = taskId, capability_id = "calendar_create",
                arguments_json = JsonSerializer.Serialize(new { title = "Project review",
                    start = "2027-03-12T09:00:00+05:30", end = "2027-03-12T09:15:00+05:30" })
            });
            var created = await commands.ExecuteJarvisToolAsync("connector_execute", proposal, default);
            Assert.True(created.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(created));
            Assert.False(created.GetValueOrDefault("confirmation_required") is true);
            Assert.Null(automation.PendingSnapshot());
            Assert.Equal(1, backendWrites);

            // A repeated model proposal must reuse the existing owned operation,
            // rather than submit another event because confirmation is absent.
            var repeated = await commands.ExecuteJarvisToolAsync("connector_execute", proposal, default);
            Assert.True(repeated.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(repeated));
            Assert.Equal(created["operation_id"], repeated["operation_id"]);
            Assert.Equal(1, backendWrites);
            Assert.Null(automation.PendingSnapshot());
            Assert.Equal(JarvisPermissionMode.AutoExecute, settings.Current.JarvisPermissionMode);
        }
        finally
        {
            await controller.ResetAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("confirmation_tool", true)]
    [InlineData("repeated_create", true)]
    [InlineData("reordered_create", true)]
    [InlineData("changed_title", false)]
    [InlineData("changed_time", false)]
    [InlineData("different_task", false)]
    [InlineData("unconfirmed", false)]
    [InlineData("cancelled", false)]
    [InlineData("expired", false)]
    public async Task AlwaysAsk_calendar_write_uses_only_fresh_matching_confirmation(string proposal, bool shouldWrite)
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        using var session = new CancellationTokenSource();
        var taskId = Guid.NewGuid().ToString("N");
        var accountId = Guid.NewGuid().ToString("N");
        var backendReads = 0;
        var backendWrites = 0;
        var originalRequest = "Please set a calendar reminder for next week to review the plan.";
        string resolvedHuman = null;
        var settings = new SettingsService(Path.Combine(directory, "settings.json"));
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        using var automation = new JarvisAutomationService(null, settings, new(Path.Combine(directory, "history.json")),
            new(Path.Combine(directory, "mappings.json")), () => { });
        var commands = new CommandRegistry(null, null, null, settings, new NotificationService(null), automation: automation);
        var controller = new JarvisConnectorController((_, path, payload, _) =>
        {
            var body = JsonSerializer.SerializeToElement(payload);
            if (path.EndsWith("/resolve"))
            {
                resolvedHuman = body.GetProperty("userRequest").GetString();
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    taskId, connectorId = "googlecalendar", accountId, state = "ready",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    choices = new[] { new { accountId, label = "Personal calendar" } },
                    capabilities = new[]
                    {
                        new { id = "calendar_list", writes = false, activity = "Checking Calendar…",
                            arguments = new Dictionary<string, string> { ["start"] = "ISO timestamp", ["end"] = "ISO timestamp" } },
                        new { id = "calendar_create", writes = true, activity = "Creating event…",
                            arguments = new Dictionary<string, string> { ["title"] = "event title", ["start"] = "ISO timestamp", ["end"] = "ISO timestamp" } }
                    }
                }));
            }
            if (path.EndsWith("/execute"))
            {
                if (body.GetProperty("capabilityId").GetString() == "calendar_create")
                {
                    ++backendWrites;
                    Assert.True(body.GetProperty("confirmed").GetBoolean());
                }
                else ++backendReads;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { id = "observed-event", items = Array.Empty<object>() } } }));
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "cancelled" }));
        }, () => "fixture-owner", () => session.Token, () => commands.ConnectorUserRequest,
            (_, _) => Task.FromResult(true), userRequestIdentity: () => commands.ConnectorUserRequestIdentity,
            userRequestSnapshot: () => commands.ConnectorUserRequestSnapshot);
        automation.Connectors = controller;
        try
        {
            commands.ObserveJarvisTranscript(originalRequest, session.Token);
            var resolved = await commands.ExecuteJarvisToolAsync("connector_resolve",
                JsonSerializer.SerializeToElement(new { connector_id = "googlecalendar" }), default);
            Assert.True(resolved.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(resolved));
            Assert.Equal(originalRequest, resolvedHuman);
            Assert.Contains("calendar_create", JsonSerializer.Serialize(resolved));

            var start = "2027-03-12T09:00:00+05:30";
            var end = "2027-03-12T09:15:00+05:30";
            var read = await automation.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
                { task_id = taskId, capability_id = "calendar_list", arguments_json = JsonSerializer.Serialize(new { start, end }) }), default);
            Assert.True(read.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(read));
            Assert.False(read.GetValueOrDefault("confirmation_required") is true);
            Assert.Equal(1, backendReads);

            var pending = await automation.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            {
                task_id = taskId, capability_id = "calendar_create",
                arguments_json = JsonSerializer.Serialize(new { title = "Project review", start, end })
            }), default);
            Assert.Equal(0, backendWrites);
            Assert.True(pending.GetValueOrDefault("confirmation_required") is true, JsonSerializer.Serialize(pending));
            Assert.False(pending.GetValueOrDefault("always_requires_confirmation") is true, JsonSerializer.Serialize(pending));
            Assert.Equal("AlwaysAsk", pending.GetValueOrDefault("permission_mode"));
            Assert.Equal(JarvisPermissionMode.AlwaysAsk, settings.Current.JarvisPermissionMode);
            Assert.Contains("Project review", pending["description"].ToString());
            Assert.Contains(start, pending["description"].ToString());
            Assert.Contains(end, pending["description"].ToString());
            Assert.NotNull(automation.PendingSnapshot());

            // The desktop receives a new submitted/spoken user turn. The model
            // may repeat its original create proposal rather than choose the
            // ideal confirmation tool; that was the actual installed failure.
            if (proposal != "unconfirmed")
                commands.ObserveSubmittedJarvisText(proposal == "cancelled" ? "Cancel" : "Confirm", session.Token);
            if (proposal == "expired")
            {
                var action = typeof(JarvisAutomationService).GetField("_pending",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(automation);
                action.GetType().GetProperty("ConfirmedAt").SetValue(action, DateTimeOffset.UtcNow.AddSeconds(-31));
            }
            var repeatedArgs = JsonSerializer.SerializeToElement(new
            {
                task_id = proposal == "different_task" ? Guid.NewGuid().ToString("N") : taskId,
                capability_id = "calendar_create",
                arguments_json = proposal == "reordered_create"
                    ? JsonSerializer.Serialize(new { end, title = "Project review", start })
                    : JsonSerializer.Serialize(new { title = proposal == "changed_title" ? "Different event" : "Project review",
                        start = proposal == "changed_time" ? "2027-03-12T10:00:00+05:30" : start, end })
            });
            var confirmed = await automation.ExecuteAsync(proposal == "confirmation_tool"
                ? "confirm_sensitive_action" : "connector_execute", proposal == "confirmation_tool"
                ? JsonSerializer.SerializeToElement(new { }) : repeatedArgs, default);
            Assert.Equal(shouldWrite ? 1 : 0, backendWrites);
            Assert.Equal(JarvisPermissionMode.AlwaysAsk, settings.Current.JarvisPermissionMode);
            if (shouldWrite)
            {
                Assert.True(confirmed.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(confirmed));
                Assert.Null(automation.PendingSnapshot());
                var repeated = await automation.ExecuteAsync("confirm_sensitive_action", JsonSerializer.SerializeToElement(new { }), default);
                Assert.False(repeated.GetValueOrDefault("ok") is true);
                Assert.Equal(1, backendWrites);
            }
        }
        finally
        {
            await controller.ResetAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Calendar_confirmation_identifies_the_event_and_times_without_executing_it(bool longTitle)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var account = Guid.NewGuid().ToString("N");
        var executes = 0;
        using var session = new CancellationTokenSource();
        var controller = new JarvisConnectorController((_, path, _, _) =>
        {
            if (path.EndsWith("/execute")) ++executes;
            return Task.FromResult(JsonSerializer.SerializeToElement(path.EndsWith("/resolve") ? (object)new
            {
                taskId, connectorId = "googlecalendar", accountId = account, state = "ready",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                choices = new[] { new { accountId = account, label = "Personal calendar" } },
                capabilities = new[] { new { id = "calendar_create", writes = true, activity = "Creating event…",
                    arguments = new { title = "event title", start = "ISO timestamp", end = "ISO timestamp", description = "optional" } } }
            } : new { state = "cancelled" }));
        }, () => "owner", () => session.Token, () => "Schedule a private planning event.",
            (_, _) => Task.FromResult(true));
        try
        {
            await controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "googlecalendar" }), false, default);
            var title = longTitle ? new string('x', 300) : "Project review";
            var args = JsonSerializer.SerializeToElement(new { task_id = taskId, capability_id = "calendar_create",
                arguments_json = JsonSerializer.Serialize(new { title, start = "2027-03-12T09:00:00+05:30",
                    end = "2027-03-12T09:15:00+05:30", description = "PRIVATE unrelated notes" }) });
            Assert.True(controller.RequiresConfirmation(args));
            var description = controller.DescribeConfirmation(args);
            Assert.Contains("Personal calendar", description);
            Assert.Contains(title, description);
            Assert.Contains("2027-03-12T09:00:00+05:30", description);
            Assert.Contains("2027-03-12T09:15:00+05:30", description);
            Assert.DoesNotContain("PRIVATE", description);
            Assert.Equal(0, executes);
        }
        finally { await controller.ResetAsync(); }
    }
}
