using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed partial class ConnectorPlatformTests
{
    [Theory]
    [InlineData("same_end")]
    [InlineData("missing_end")]
    [InlineData("mixed_dates")]
    [InlineData("unsupported_field")]
    public async Task Calendar_predispatch_validation_preserves_the_task_for_one_corrected_create(string scenario)
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller,
            new("googlecalendar", "Set a reminder on November 21 to review the plan."), default);
        var invalid = Args(new { title = "Review the plan", start = "2026-11-21", end = "2026-11-21" });
        if (scenario == "missing_end") invalid.Remove("end");
        if (scenario == "mixed_dates") invalid["end"] = JsonSerializer.SerializeToElement("2026-11-22T00:00:00+05:30");
        if (scenario == "unsupported_field") invalid["attendees"] = JsonSerializer.SerializeToElement(new[] { "other@example.test" });

        var rejected = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "calendar_create", invalid, true), default);
        AssertPreDispatchValidation(rejected, task.TaskId, "calendar_create", "end");
        Assert.Equal(0, f.Provider.Writes);
        var before = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
        Assert.Equal("ready", before.State); Assert.Empty(before.Operations);

        var request = new ConnectorExecuteRequest(task.TaskId, "calendar_create", Args(new
            { title = "Review the plan", start = "2026-11-21", end = "2026-11-22" }), true);
        var corrected = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.True(corrected.Verified); Assert.Equal("completed", corrected.State);
        Assert.Equal(1, f.Provider.Writes);
        Assert.Single((await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).Operations);

        var duplicate = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.True(duplicate.Verified); Assert.Equal("completed", duplicate.State);
        Assert.Equal(1, f.Provider.Writes);
    }

    [Theory]
    [InlineData("docs_create", "Create a document", "googledocs", "{}", "title")]
    [InlineData("mail_send", "Send an email", "gmail", "{\"to\":\"not an address\",\"subject\":\"Notes\",\"text\":\"Hello\"}", "to")]
    [InlineData("drive_read", "Read my file", "googledrive", "{\"id\":\"../outside\"}", "id")]
    public async Task Shared_predispatch_validation_returns_the_registered_schema_without_a_claim(
        string capability, string request, string connector, string encodedArguments, string expectedField)
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new(connector, request), default);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(encodedArguments)!;
        var rejected = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, capability, arguments, true), default);
        AssertPreDispatchValidation(rejected, task.TaskId, capability, expectedField);
        Assert.Equal(0, f.Provider.Writes);
        var status = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
        Assert.Equal("ready", status.State); Assert.Empty(status.Operations);
        Assert.DoesNotContain("not an address", JsonSerializer.Serialize(rejected));
        Assert.DoesNotContain("../outside", JsonSerializer.Serialize(rejected));
    }

    [Fact]
    public async Task Predispatch_validation_does_not_bypass_write_authority_confirmation_or_task_cancellation()
    {
        var f = Fixture(); await Connect(f);
        var readOnly = await f.Tasks.ResolveAsync(f.Caller, new("googlecalendar", "Show my calendar"), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Tasks.ExecuteAsync(f.Caller,
            new(readOnly.TaskId, "calendar_create", Args(new { }), true), default));
        Assert.Empty((await f.Tasks.StatusAsync(f.Caller, readOnly.TaskId, default)).Operations);

        var task = await f.Tasks.ResolveAsync(f.Caller, new("googlecalendar", "Create an event"), default);
        var valid = Args(new { title = "Review the plan", start = "2026-11-21", end = "2026-11-22" });
        var confirmation = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "calendar_create", valid), default);
        Assert.Equal("confirmation_required", confirmation.State); Assert.False(confirmation.Verified);
        Assert.Empty((await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).Operations);
        await f.Tasks.CancelAsync(f.Caller, task.TaskId, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "calendar_create", valid, true), default));
        Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Calendar_dispatched_unknown_write_does_not_receive_predispatch_retry_permission()
    {
        var f = Fixture(); await Connect(f); f.Provider.CalendarContentMismatch = true;
        var task = await f.Tasks.ResolveAsync(f.Caller, new("googlecalendar", "Create an event"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "calendar_create", Args(new
            { title = "Review the plan", start = "2026-11-21", end = "2026-11-22" }), true);
        var result = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.Equal("outcome_unknown", result.State); Assert.False(result.Verified); Assert.Null(result.Result);
        Assert.Equal(1, f.Provider.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller, request, default));
        Assert.Equal(1, f.Provider.Writes);
    }

    [Fact]
    public async Task Later_invalid_proposal_does_not_erase_or_reissue_a_previously_verified_write()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("googlecalendar", "Create an event"), default);
        var original = new ConnectorExecuteRequest(task.TaskId, "calendar_create", Args(new
            { title = "Review the plan", start = "2026-11-21", end = "2026-11-22" }), true);
        var completed = await f.Tasks.ExecuteAsync(f.Caller, original, default);
        Assert.True(completed.Verified); Assert.Equal(1, f.Provider.Writes);

        var invalid = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "calendar_create",
            Args(new { title = "Another proposal", start = "2026-11-21", end = "2026-11-21" }), true), default);
        AssertPreDispatchValidation(invalid, task.TaskId, "calendar_create", "end");
        var operation = Assert.Single((await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).Operations);
        Assert.Equal(completed.OperationId, operation.OperationId); Assert.True(operation.Verified);
        Assert.Equal("completed", operation.State); Assert.Equal(1, f.Provider.Writes);

        var repeated = await f.Tasks.ExecuteAsync(f.Caller, original, default);
        Assert.True(repeated.Verified); Assert.Equal(completed.OperationId, repeated.OperationId);
        Assert.Equal(1, f.Provider.Writes);
    }

    private static void AssertPreDispatchValidation(ConnectorExecutionReply result, string task,
        string capability, string expectedField)
    {
        Assert.Equal(task, result.TaskId); Assert.Equal("", result.OperationId);
        Assert.Equal("validation_required", result.State); Assert.False(result.Verified);
        var details = Assert.IsType<JsonElement>(result.Result);
        Assert.Equal(capability, details.GetProperty("capabilityId").GetString());
        Assert.False(details.GetProperty("providerDispatched").GetBoolean());
        Assert.False(details.GetProperty("operationClaimed").GetBoolean());
        Assert.True(details.GetProperty("retryableWithCorrectedArguments").GetBoolean());
        Assert.True(details.GetProperty("expectedArguments").TryGetProperty(expectedField, out _));
        Assert.False(string.IsNullOrWhiteSpace(details.GetProperty("validationMessage").GetString()));
    }
}
