using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class UpdateLaunchAcknowledgementTests
{
    [Fact]
    public async Task Successful_launch_requests_exit_exactly_once_independently_of_acknowledgement()
    {
        var events = new List<string>();
        await CompleteAsync(() => { events.Add("launch"); return true; },
            () => { events.Add("acknowledge"); return Task.CompletedTask; }, () => events.Add("exit"));
        Assert.Equal(new[] { "launch", "exit", "acknowledge" }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_launcher_never_acknowledges_or_requests_exit(bool throws)
    {
        var acknowledgements = 0;
        var exits = 0;
        var error = new InvalidOperationException("Synthetic failed launcher");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(
            () => throws ? throw error : false,
            () => { acknowledgements++; return Task.CompletedTask; }, () => exits++));
        if (throws) Assert.Same(error, observed);
        Assert.Equal(0, acknowledgements);
        Assert.Equal(0, exits);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("synchronous")]
    public async Task Lost_http_acknowledgement_after_launch_still_requests_exit_once(string mode)
    {
        var events = new List<string>();
        Exception error = mode == "cancel" ? new OperationCanceledException("Synthetic client cancellation")
            : new IOException("Synthetic client disconnect");
        var observed = await Record.ExceptionAsync(() => CompleteAsync(
            () => { events.Add("launch"); return true; },
            () =>
            {
                events.Add("acknowledge");
                if (mode == "synchronous") throw error;
                return Task.FromException(error);
            }, () => events.Add("exit")));
        Assert.Same(error, observed);
        Assert.Equal(new[] { "launch", "exit", "acknowledge" }, events);
    }

    [Fact]
    public async Task Stalled_acknowledgement_does_not_prevent_exit_scheduling()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exits = 0;
        var task = CompleteAsync(() => true, () => completed.Task, () => exits++);
        Assert.False(task.IsCompleted);
        Assert.Equal(1, exits);
        completed.SetResult(true);
        await task;
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task Exit_callback_failure_is_preserved_and_never_replayed()
    {
        var launches = 0;
        var acknowledgements = 0;
        var exits = 0;
        var error = new InvalidOperationException("Synthetic exit scheduling failure");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(
            () => { launches++; return true; },
            () => { acknowledgements++; return Task.CompletedTask; },
            () => { exits++; throw error; }));
        Assert.Same(error, observed);
        Assert.Equal(1, launches);
        Assert.Equal(0, acknowledgements);
        Assert.Equal(1, exits);
    }

    private static Task CompleteAsync(Func<bool> launch, Func<Task> acknowledge, Action exit)
    {
        var method = typeof(WebSettingsService).GetMethod("CompleteUpdateLaunchAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(method is not null, "The update launch/acknowledgement boundary is missing.");
        try { return (Task)method.Invoke(null, new object[] { launch, acknowledge, exit }); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
