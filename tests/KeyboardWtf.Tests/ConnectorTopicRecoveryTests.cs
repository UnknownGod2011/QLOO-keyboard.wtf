namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorTopicRecoveryTests
{
    [Fact]
    public async Task Native_topic_alternative_keeps_numeric_qualifier_outside_the_OR_group()
    {
        var queries = new List<string>();
        await ConnectorReadReasoner.RunAsync(new("Find the email about orbital navigation 2027.",
            [new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" })])
            { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string, JsonElement>
                { ["query"] = JsonSerializer.SerializeToElement("orbital navigation 2027") } },
            (_, _, _, _, _) => throw new PersonalAiException(PersonalAiFailure.Quota),
            (_, args, _) => { var query = args["query"].GetString(); queries.Add(query); return Task.FromResult(Empty(query)); }, null, default);
        Assert.Equal(new[] { "orbital navigation 2027", "2027 {orbital navigation}" }, queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_inferred_topic_gets_one_native_alternative_before_optional_synthesis(bool details)
    {
        var searches = new List<string>(); var reads = 0; var models = 0;
        var input = new ConnectorReadReasonerInput("Find my email about humanoid motion generation internship" + (details ? " and give me the details." : "."),
            [new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" }),
                new("mail_thread", "Read thread", new Dictionary<string, string> { ["id"] = "thread" })])
        { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string, JsonElement>
            { ["query"] = JsonSerializer.SerializeToElement("humanoid motion generation internship") } };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => { models++; throw new PersonalAiException(PersonalAiFailure.Quota); },
            (capability, args, _) =>
            {
                if (capability == "mail_thread")
                {
                    reads++; Assert.Equal("ccccdddd", args["id"].GetString());
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                        result = new { truncated = false, data = new { id = "ccccdddd", messages = new[] {
                            new { id = "aaaabbbb", threadId = "ccccdddd", text = "The internship application outcome is available.", contentTruncated = false } } } } }));
                }
                var query = args["query"].GetString(); searches.Add(query);
                return Task.FromResult(searches.Count == 1 ? Empty(query) : JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { queryUsed = query, messages = new[] { new { id = "aaaabbbb", threadId = "ccccdddd" } },
                        sampledMessages = new[] { new { id = "aaaabbbb", threadId = "ccccdddd", from = "Research team", subject = "Internship — Humanoid motion generation" } },
                        pageItemCount = 1, hasMore = false } } }));
            }, null, default);
        Assert.Equal(new[] { "humanoid motion generation internship", "{humanoid motion generation internship}" }, searches);
        Assert.Equal(details ? 1 : 0, reads); Assert.Equal(1, models);
        Assert.Equal("ai_rate_limit", result.ReasoningError); Assert.Equal("partial", result.GoalStatus);
        Assert.Equal(2 + reads, result.Executions); Assert.Empty(result.TargetReference);
    }

    [Theory]
    [InlineData("Find my email about humanoid motion.", "invented subject", false)]
    [InlineData("Find my email about Orbiton 2027.", "Orbiton 2027", false)]
    [InlineData("Find my email about humanoid motion.", "subject:humanoid motion", false)]
    [InlineData("Find my email about humanoid motion.", "humanoid motion", true)]
    [InlineData("Find exactly the email about humanoid motion.", "humanoid motion", false)]
    [InlineData("Search using: humanoid motion", "humanoid motion", false)]
    [InlineData("Find my email from Mira about humanoid motion.", "Mira humanoid motion", false)]
    [InlineData("Find my email about humanoid motion from last month.", "humanoid motion", false)]
    [InlineData("Find my email with the subject humanoid motion.", "humanoid motion", false)]
    public async Task Alternative_never_invents_terms_changes_explicit_queries_or_repeats_empty_searches(string request, string initial, bool alternate)
    {
        var queries = new List<string>();
        var result = await ConnectorReadReasoner.RunAsync(new(request,
            [new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" })])
            { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string, JsonElement>
                { ["query"] = JsonSerializer.SerializeToElement(initial) } },
            (_, _, _, _, _) => throw new PersonalAiException(PersonalAiFailure.Quota),
            (_, args, _) => { var query = args["query"].GetString(); queries.Add(query); return Task.FromResult(Empty(query)); }, null, default);
        Assert.Equal(alternate ? 2 : 1, queries.Count); Assert.Equal(queries.Count, queries.Distinct().Count());
        Assert.NotEqual("matched", result.GoalStatus);
    }

    private static JsonElement Empty(string query) => JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
        result = new { truncated = false, data = new { queryUsed = query, messages = Array.Empty<object>(), pageItemCount = 0,
            hasMore = false, metadataSample = new { requested = 0, returned = 0, complete = true } } } });
}
