namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorNaturalDetailsTests
{
    internal const string NaturalRequest = "I had got an email from sophia gao /dr gao could you pls find that email and give the details about it.";

    [Theory]
    [InlineData(1, false)]
    [InlineData(5, true)]
    public async Task Natural_find_and_details_reads_observed_thread_and_missing_body_before_reasoning_outage(int candidates, bool hasMore)
    {
        var calls = new List<string>();
        var input = new ConnectorReadReasonerInput(NaturalRequest, [
            new("mail_search", "Search", new Dictionary<string,string> { ["query"] = "query" }),
            new("mail_thread", "Thread", new Dictionary<string,string> { ["id"] = "id" }),
            new("mail_read", "Message", new Dictionary<string,string> { ["id"] = "id" })])
        { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string,JsonElement> { ["query"] = JsonSerializer.SerializeToElement("from:gao") } };
        var result = await ConnectorReadReasoner.RunAsync(input, (_, prompt, _, _, _) =>
        {
            Assert.Contains("Observed full body requesting a research proposal", prompt);
            throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 };
        }, (cap, args, _) =>
        {
            calls.Add(cap);
            object data = cap switch
            {
                "mail_search" => new { messages = Enumerable.Range(0, candidates).Select(_ => new { id = "aabbccdd11", threadId = "aabbccdd22" }).ToArray(), pageItemCount = candidates, hasMore },
                "mail_thread" => new { id = "aabbccdd22", messageReferences = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } }, messages = Array.Empty<object>() },
                _ => new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Observed full body requesting a research proposal", headers = new[] { new { name = "Subject", value = "Research proposal" } } }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data, truncated = false } }));
        }, _ => {}, default);
        Assert.Equal(new[] { "mail_search", "mail_thread", "mail_read" }, calls);
        Assert.Equal("ai_provider_unavailable", result.ReasoningError);
        Assert.Equal(2, result.ModelCalls);
        Assert.Contains("Research proposal", result.Answer);
    }

    [Theory]
    [InlineData(NaturalRequest)]
    [InlineData("Find the email with Jane and give details about it.")]
    [InlineData("Send a reply to that email.")]
    [InlineData("Delete that thread.")]
    [InlineData("What is 2 + 2?")]
    [InlineData("Open Gmail.")]
    public void New_named_request_or_another_action_is_not_an_anaphoric_details_followup(string text)
        => Assert.False(ConnectorFollowupIntent.MailDetails(text));

    [Fact]
    public void Outage_content_uses_verified_body_reads_only_with_explicit_coverage_and_bounds()
    {
        JsonElement Receipt(bool verified, string content) => JsonSerializer.SerializeToElement(new { state = "completed", verified,
            result = new { data = new { id = "aabbccdd11", threadId = "aabbccdd22", text = content, contentTruncated = false } } });
        var accepted = Receipt(true, new string('A', 7000));
        var output = JsonSerializer.SerializeToElement(ConnectorMailFacts.Content([
            ("mail_search", Receipt(true, "SEARCH PREVIEW")), ("mail_read", Receipt(false, "UNVERIFIED")), ("mail_read", accepted)]));
        var item = Assert.Single(output.EnumerateArray());
        Assert.Equal(6000, item.GetProperty("text").GetString().Length);
        Assert.True(item.GetProperty("contentClipped").GetBoolean());
        Assert.Equal("external_untrusted", item.GetProperty("source").GetString());
        Assert.Empty(ConnectorMailFacts.Content([("mail_read", ConnectorMailContentPermission.Project(accepted))]));
    }
}
