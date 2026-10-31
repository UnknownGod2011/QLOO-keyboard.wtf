using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorSpamSearchTests
{
    [Theory]
    [InlineData("in:spam", true)]
    [InlineData("in:spam research", true)]
    [InlineData("from:professor@example.test in:trash", true)]
    [InlineData("IN:ANYWHERE subject:research", true)]
    [InlineData("{in:spam in:inbox} subject:research", true)]
    [InlineData("(in:spam) is:unread", true)]
    [InlineData("in:inbox", false)]
    [InlineData("research", false)]
    [InlineData("-in:spam", false)]
    [InlineData("-(in:spam)", false)]
    [InlineData("-{in:spam in:inbox}", false)]
    [InlineData("subject:in:spam", false)]
    [InlineData("\"in:spam\"", false)]
    [InlineData("subject:\"in:spam\"", false)]
    [InlineData("in:spammy", false)]
    public void Explicit_native_location_controls_spam_inclusion_for_search_and_count(string query, bool include)
    {
        foreach (var capability in new[] { "mail_search", "mail_count" })
        foreach (var continuation in new[] { "", "opaque-next-page" })
        {
            var args = new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement(query),
                ["pageToken"] = JsonSerializer.SerializeToElement(continuation)
            };
            var plan = ConnectorOperations.Plan(capability, args, ConnectorIds.New());
            Assert.Equal("GET", plan.Method);
            Assert.Equal(include, plan.Endpoint.Contains("&includeSpamTrash=true", StringComparison.Ordinal));
            Assert.Contains("q=" + Uri.EscapeDataString(query), plan.Endpoint);
            if (continuation.Length > 0) Assert.Contains("&pageToken=opaque-next-page", plan.Endpoint);
        }
    }
}
