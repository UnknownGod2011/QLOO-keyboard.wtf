namespace KeyboardWtf.Tests;

using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorReadAndOpenIntentTests
{
    [Theory]
    [InlineData("Open it on a new tab and give me more details.", "give me more details.", "Open it on a new tab")]
    [InlineData("Open that email in a new tab and tell me the details.", "tell me the details.", "Open that email in a new tab")]
    [InlineData("Give me more details about that thread and open it in a new tab.", "Give me more details about that thread", "open it in a new tab.")]
    [InlineData("Could you open it in a new tab and give me all the details about that email?", "give me all the details about that email?", "Could you open it in a new tab")]
    [InlineData("Please read that thread, then open that email in a new tab.", "Please read that thread", "open that email in a new tab.")]
    [InlineData("Open it and then give me the detail.", "give me the detail.", "Open it")]
    [InlineData("Tell me about it then open it.", "Tell me about it", "open it.")]
    [InlineData("Open that thread and give me details on the email you found.", "give me details on the email you found.", "Open that thread")]
    [InlineData("Could you please provide more details about the entire conversation and open the conversation thread in a new tab.", "Could you please provide more details about the entire conversation", "open the conversation thread in a new tab.")]
    [InlineData("Could please provide more details about the entire conversation and open the conversation thread in a new tab.", "Could please provide more details about the entire conversation", "open the conversation thread in a new tab.")]
    [InlineData("Open that email thread in a new tab and give me more details.", "give me more details.", "Open that email thread in a new tab")]
    public void Two_complete_mail_clauses_have_one_read_and_one_single_object_open(string request, string read, string open)
    {
        Assert.True(ConnectorFollowupIntent.TryReadAndOpen(request, out var actualRead, out var actualOpen));
        Assert.Equal(read, actualRead);
        Assert.Equal(open, actualOpen);
        Assert.True(ConnectorFollowupIntent.MailDetails(request));
        // Generic pre-routing must not execute just the open half and discard
        // the requested read. Only the explicit combined owner may compose it.
        Assert.False(ConnectorFollowupIntent.IsExactOpen(request));
        Assert.True(ConnectorFollowupIntent.MailDetails(actualRead));
        Assert.True(ConnectorFollowupIntent.TryParseExactOpen(actualOpen, out var selection));
        Assert.Equal(ConnectorOpenSelection.Single, selection);
    }

    [Theory]
    [InlineData("Give me details about the email which I had sent and open it in a new tab.", ConnectorMailSelection.Sent)]
    [InlineData("Open that email and read the one I received.", ConnectorMailSelection.Received)]
    [InlineData("Open it in a new tab and give me more details.", ConnectorMailSelection.Unspecified)]
    public void Mail_selection_comes_from_only_the_verified_read_clause(string request, object expected)
    {
        Assert.True(ConnectorFollowupIntent.TryReadAndOpen(request, out _, out _));
        Assert.Equal((ConnectorMailSelection)expected, ConnectorFollowupIntent.MailSelection(request));
    }

    [Theory]
    [InlineData("Read that thread then open Gmail.")]
    [InlineData("Open Gmail and give me more details.")]
    [InlineData("Open that document and give me more details.")]
    [InlineData("Give me details about that email and open that repository.")]
    [InlineData("Read that thread and open that issue.")]
    [InlineData("Open both in separate tabs and give me more details.")]
    [InlineData("Open all those emails and give me more details.")]
    [InlineData("Read that email and send a reply.")]
    [InlineData("Open it and delete it.")]
    [InlineData("Find the Daisy email and open it.")]
    [InlineData("Give me details about another email and open it.")]
    [InlineData("Open it and read that email and delete it.")]
    [InlineData("Open it or give me more details.")]
    [InlineData("Open it and yes please.")]
    [InlineData("Open it and give me more details about the weather.")]
    [InlineData("Open that email and tell me about that document.")]
    [InlineData("Read that conversation and open the conversation thread in a different account.")]
    [InlineData("Read that conversation and open the conversation thread and delete it.")]
    [InlineData("Give me more details and open it and send it.")]
    [InlineData("Give me more details.")]
    [InlineData("Open it on a new tab.")]
    public void Another_goal_write_provider_navigation_plural_or_ambiguous_clause_is_not_a_combined_read(string request)
    {
        Assert.False(ConnectorFollowupIntent.TryReadAndOpen(request, out var read, out var open));
        Assert.Equal("", read);
        Assert.Equal("", open);
    }

    [Fact]
    public void Overlong_input_cannot_compose_new_operations()
    {
        Assert.False(ConnectorFollowupIntent.TryReadAndOpen(new string(' ', 3000) + "Open it and read it.", out _, out _));
    }
}
