namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorNativeReadBudgetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Queued_person_threads_cannot_skip_synthesis_after_eight_native_reads(int citationCount)
    {
        var models = 0; var reads = new List<string>();
        var tools = new[] {
            new ConnectorReadCapability("mail_search", "Search", new Dictionary<string,string> { ["query"] = "query" }),
            new ConnectorReadCapability("mail_thread", "Read thread", new Dictionary<string,string> { ["id"] = "id" }),
            new ConnectorReadCapability("mail_read", "Read message", new Dictionary<string,string> { ["id"] = "id" }) };
        var result = await ConnectorReadReasoner.RunAsync(new("Find my emails with Alex Example and give me the details.", tools)
        {
            Provider = "gmail", InitialCapability = "mail_search",
            InitialArguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("Alex Example") }
        }, (_, prompt, _, _, _) =>
        {
            models++;
            Assert.Equal(0, JsonDocument.Parse(prompt).RootElement.GetProperty("remainingExecutions").GetInt32());
            return Task.FromResult(JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
                answer = "The retrieved messages describe project review. Other matching threads remain unread.",
                references = citationCount == 1 ? new[] { "r3" } : Enumerable.Range(1, 8).Select(i => "r" + i).ToArray(),
                partial = true, goal_status = "partial", target_reference = "" }));
        }, (capability, args, _) =>
        {
            reads.Add(capability);
            object data = capability switch
            {
                "mail_search" => new { messages = new[] { new { id = "aaaabbbb11", threadId = "aaaabbbb22" }, new { id = "ccccdddd11", threadId = "ccccdddd22" } },
                    sampledMessages = new[] { new { id = "aaaabbbb11", threadId = "aaaabbbb22", from = "Alex Example", subject = "Project review" }, new { id = "ccccdddd11", threadId = "ccccdddd22", from = "Alex Example", subject = "Another review" } } },
                "mail_thread" => new { id = args["id"].GetString(), messages = Array.Empty<object>(),
                    messageReferences = Enumerable.Range(1, 7).Select(i => new { id = "eeeeffff0" + i, threadId = args["id"].GetString() }).ToArray() },
                _ => new { id = args["id"].GetString(), threadId = "aaaabbbb22", text = "Please review the project.", contentTruncated = false }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data, truncated = capability == "mail_thread" } }));
        }, null, default);
        Assert.Equal(8, reads.Count);
        Assert.Equal(1, models);
        Assert.Equal(1, result.ModelCalls);
        Assert.Contains("project review", result.Answer);
        Assert.True(result.Partial);
        Assert.Contains("r3", result.EvidenceReferences);
        Assert.Equal(citationCount, result.EvidenceReferences.Length);
    }
}
