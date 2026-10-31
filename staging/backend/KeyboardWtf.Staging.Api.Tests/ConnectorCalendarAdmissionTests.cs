using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Reuse the existing in-memory connector/provider fixture. These checks resolve
// capabilities and unconfirmed proposals only; no provider write is dispatched.
public sealed partial class ConnectorPlatformTests
{
    [Fact]
    public async Task CalendarAdmission_newly_admitted_reminder_requires_confirmation_before_dispatch()
    {
        var fixture = Fixture();
        await Connect(fixture);
        var task = await fixture.Tasks.ResolveAsync(fixture.Caller,
            new("googlecalendar", "Please set a calendar reminder to review the plan next week."), default);
        Assert.Contains(task.Capabilities, capability => capability.Id == "calendar_create" && capability.Writes);

        var start = DateTimeOffset.UtcNow.AddDays(7);
        var proposal = new ConnectorExecuteRequest(task.TaskId, "calendar_create", Args(new
        {
            title = "Review the plan",
            start = start.ToString("O"),
            end = start.AddMinutes(15).ToString("O")
        }));
        var reply = await fixture.Tasks.ExecuteAsync(fixture.Caller, proposal, default);

        Assert.Equal("confirmation_required", reply.State);
        Assert.False(reply.Verified);
        Assert.Null(reply.Result);
        Assert.Empty(await fixture.Store.OperationsAsync(fixture.Caller.UserId, task.TaskId, default));
        Assert.Equal(0, fixture.Provider.Writes);
    }

    [Theory]
    [InlineData("Set a reminder for next Friday to review my plan.")]
    [InlineData("Could you set a reminder in Google Calendar for next week?")]
    [InlineData("Please remind me next month to review the release.")]
    [InlineData("Schedule a private event next Friday.")]
    [InlineData("Add an event to my calendar next week.")]
    [InlineData("SET A REMINDER FOR TOMORROW TO REVIEW THE PLAN.")]
    [InlineData("Please set my calendar reminder for next week.")]
    [InlineData("Could you set the Google Calendar reminder for next month?")]
    [InlineData("Set reminders for next week.")]
    [InlineData("Jarvis, please remind me to review my notes tomorrow.")]
    [InlineData("Could you remind me on Friday to review my notes?")]
    [InlineData("Please remind me again tomorrow to prepare the outline.")]
    public async Task CalendarAdmission_explicit_reminder_or_event_request_returns_creation_for_confirmation(string request)
    {
        var fixture = Fixture();
        await Connect(fixture);

        var reply = await fixture.Tasks.ResolveAsync(fixture.Caller, new("googlecalendar", request), default);

        Assert.Equal("ready", reply.State);
        Assert.Contains(reply.Capabilities, capability => capability.Id == "calendar_list" && !capability.Writes);
        Assert.Contains(reply.Capabilities, capability => capability.Id == "calendar_create" && capability.Writes);
        Assert.True((await fixture.Store.GetTaskAsync(fixture.Caller.UserId, reply.TaskId, default)).AllowsWrites);
        Assert.Empty(await fixture.Store.OperationsAsync(fixture.Caller.UserId, reply.TaskId, default));
        Assert.Equal(0, fixture.Provider.Writes);
    }

    [Theory]
    [InlineData("Don't set a reminder next Friday.")]
    [InlineData("Do not schedule an event.")]
    [InlineData("What reminders do I have next week?")]
    [InlineData("What does 'set a reminder' mean?")]
    [InlineData("Read the note \"Set a reminder next Friday\".")]
    [InlineData("Read a message that says schedule an event.")]
    [InlineData("Can you tell me how to set a reminder?")]
    [InlineData("Show my calendar events.")]
    [InlineData("Remind me what we discussed.")]
    [InlineData("Remind me when the conference starts.")]
    [InlineData("Remind me whether I need to attend.")]
    [InlineData("Remind me again what time to arrive.")]
    [InlineData("Remind me how to set a reminder.")]
    [InlineData("Remind me about our plan to review the release.")]
    [InlineData("Tell me how to set a reminder in Calendar.")]
    [InlineData("Could you not set a reminder for tomorrow?")]
    [InlineData("Read this: 'remind me to review the plan'.")]
    [InlineData("Don't remind me to review the plan.")]
    [InlineData("Don't remind me next month to review the plan.")]
    [InlineData("Don't set a calendar reminder.")]
    [InlineData("Can you remind me where to go?")]
    [InlineData("Can you remind me to which room I should go?")]
    public async Task CalendarAdmission_non_authorizing_requests_remain_read_only(string request)
    {
        var fixture = Fixture();
        await Connect(fixture);

        var reply = await fixture.Tasks.ResolveAsync(fixture.Caller, new("googlecalendar", request), default);

        Assert.Equal("ready", reply.State);
        Assert.Contains(reply.Capabilities, capability => capability.Id == "calendar_list" && !capability.Writes);
        Assert.DoesNotContain(reply.Capabilities, capability => capability.Id == "calendar_create");
        Assert.False((await fixture.Store.GetTaskAsync(fixture.Caller.UserId, reply.TaskId, default)).AllowsWrites);
        Assert.Empty(await fixture.Store.OperationsAsync(fixture.Caller.UserId, reply.TaskId, default));
        Assert.Equal(0, fixture.Provider.Writes);
    }
}
