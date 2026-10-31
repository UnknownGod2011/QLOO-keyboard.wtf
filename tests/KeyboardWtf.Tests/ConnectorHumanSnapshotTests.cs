namespace KeyboardWtf.Tests;

using System.Reflection;
using KeyboardWtf.Services;
using Xunit;

public sealed class ConnectorHumanSnapshotTests
{
    [Theory]
    [InlineData("Yes, please give me more details.")]
    [InlineData("What did she ask me for?")]
    [InlineData("Open that thread in a new tab.")]
    [InlineData("Open both separately in new tabs.")]
    public void Connector_human_snapshot_survives_tool_only_boundaries_without_preserving_ordinary_routing(string request)
    {
        var commands = new CommandRegistry(null, null, null, null, null);
        commands.ObserveJarvisTranscript(request, default);
        var human = commands.ConnectorUserRequestSnapshot;

        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);

        Assert.Equal("", OrdinaryRequest(commands));
        Assert.Equal(human, commands.ConnectorUserRequestSnapshot);
        Assert.Equal(request, commands.ConnectorUserRequest);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        Assert.Equal(human, commands.ConnectorUserRequestSnapshot);

        commands.CompleteJarvisUserTurn(assistantAnswerComplete: true);
        Assert.Equal("", commands.ConnectorUserRequestSnapshot.Text);
        Assert.Equal("", commands.ConnectorUserRequest);
        commands.ObserveJarvisTranscript("What is 2 plus 2?", default);
        Assert.NotEqual(human.Identity, commands.ConnectorUserRequestSnapshot.Identity);
        Assert.Equal("What is 2 plus 2?", commands.ConnectorUserRequestSnapshot.Text);
    }

    [Fact]
    public void New_input_replaces_the_preserved_connector_request_without_borrowing_old_text()
    {
        var commands = new CommandRegistry(null, null, null, null, null);
        commands.ObserveJarvisTranscript("Read that thread in detail.", default);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        commands.ObserveJarvisTranscript("Open Gmail.", default);
        Assert.Equal("Open Gmail.", commands.ConnectorUserRequestSnapshot.Text);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        Assert.Equal("Open Gmail.", commands.ConnectorUserRequestSnapshot.Text);
        Assert.Equal("", OrdinaryRequest(commands));
    }

    [Fact]
    public void Tool_only_boundaries_do_not_extend_the_connector_input_expiry()
    {
        var commands = new CommandRegistry(null, null, null, null, null);
        commands.ObserveJarvisTranscript("Open that thread in a new tab.", default);
        typeof(CommandRegistry).GetField("_latestJarvisUserTurnUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(commands, DateTime.UtcNow.AddMinutes(-2));
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        Assert.Equal("", commands.ConnectorUserRequestSnapshot.Text);
        Assert.Equal("", OrdinaryRequest(commands));
    }

    [Fact]
    public async Task Session_end_clears_the_preserved_connector_request()
    {
        var commands = new CommandRegistry(null, null, null, null, null);
        commands.ObserveJarvisTranscript("Give me more details.", default);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        Assert.Equal("Give me more details.", commands.ConnectorUserRequestSnapshot.Text);
        await commands.HandleJarvisSessionEndingAsync();
        Assert.Equal("", commands.ConnectorUserRequestSnapshot.Text);
        Assert.Equal("", OrdinaryRequest(commands));
    }

    private static string OrdinaryRequest(CommandRegistry commands) => (string)typeof(CommandRegistry)
        .GetMethod("RecentJarvisUserTurn", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(commands, null);
}
