namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorNamedMailSelectionTests
{
    [Theory]
    [InlineData("Open the email from Mira.", false)]
    [InlineData("Open the email with Mira.", true)]
    [InlineData("Pull Mira's email up.", true)]
    [InlineData("Open Mira's email separately.", true)]
    [InlineData("Open the email from Mira in the browser.", false)]
    public void Sender_and_participant_selectors_do_not_confuse_a_recipient_with_a_sender(string request, bool allowed)
    {
        var observed = JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false, data = new
        {
            messages = new[] { new { id = "aaaabbbb", threadId = "ccccdddd", from = "Reader <reader@example.test>", subject = "Research discussion",
                headers = new[] { new { name = "To", value = "Mira <mira@example.test>" } } } }, hasMore = false, pageItemCount = 1
        } } });
        var context = ConnectorSessionResult.Create("task", "gmail", "account", "reader@example.test", "Find email.", observed, [observed], evidenceCapabilities: ["mail_search"]);
        Assert.True(ConnectorFollowupIntent.TryParseExactOpen(request, out var selection));
        Assert.True(ConnectorFollowupIntent.NamedMailOpen(request, out _, out _));
        Assert.Equal(allowed, context.TryResolveExactOpen(selection, out var targets, out _, request));
        if (allowed) Assert.EndsWith("#all/ccccdddd", Assert.Single(targets).Url); else Assert.Empty(targets);
    }

    [Theory]
    [InlineData("Open Gmail.")]
    [InlineData("Open https://mail.google.com/mail/u/0/#inbox.")]
    [InlineData("Open the email from Mira and reply to it.")]
    [InlineData("Find the email from Mira in Gmail.")]
    [InlineData("Open the email from Mira in my work account.")]
    public void Ordinary_literal_search_and_compound_actions_never_borrow_a_named_open(string request)
        => Assert.False(ConnectorFollowupIntent.TryParseExactOpen(request, out _));
}
