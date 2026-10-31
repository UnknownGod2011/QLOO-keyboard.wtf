namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorMailSenderRecoveryTests
{
    [Fact]
    public void Single_participant_recovery_returns_verified_incoming_candidates_with_partial_coverage()
    {
        var recovery = ConnectorMailSenderRecovery.Create("{from:\"Sorren\" to:\"Sorren\" cc:\"Sorren\"}");
        Assert.NotNull(recovery);
        var result = recovery.Filter(Page(("12345678", "aaaa1111", "Soren <soren@example.test>"),
            ("22345678", "aaaa2222", "Somebody <else@example.test>")));
        Assert.Single(result.GetProperty("result").GetProperty("data").GetProperty("messages").EnumerateArray());
        Assert.Contains("soren@example.test", result.GetRawText());
        Assert.DoesNotContain("else@example.test", result.GetRawText());
        Assert.True(result.GetProperty("result").GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("Sorren", "Soren <soren@example.test>")]
    [InlineData("Marcel", "Marccel Dupont <contact@example.test>")]
    [InlineData("Jsoef", "Josef <josef@example.test>")]
    [InlineData("Álvaro", "Alvaro Núñez <alvaro@example.test>")]
    [InlineData("Анна", "Анна <contact@example.test>")]
    [InlineData("Mira Raman", "Raman, Mira <mira@example.test>")]
    public void Candidate_recovery_uses_observed_sender_tokens_across_names(string name, string header)
    {
        var recovery = ConnectorMailSenderRecovery.Create("from:\"" + name + "\"");
        Assert.NotNull(recovery);
        Assert.True(recovery.MatchesHeader(header));
        Assert.InRange(recovery.Names.Length, 1, 24);
        Assert.InRange(recovery.Query.Length, 1, 2000);
    }

    [Theory]
    [InlineData("from:someone@example.test")]
    [InlineData("from:example.test")]
    [InlineData("-from:Mira")]
    [InlineData("from:Mira OR from:Soren")]
    [InlineData("{from:Mira from:Soren}")]
    [InlineData("{from:\"Mira\" to:\"Soren\" cc:\"Mira\"}")]
    [InlineData("subject:\"from:Mira\"")]
    [InlineData("\"from:Mira\"")]
    [InlineData("from:(Mira Raman)")]
    [InlineData("from:Mira OR subject:grant")]
    [InlineData("from:\"Mira")]
    public void Complex_expressions_addresses_and_quoted_content_keep_the_existing_reader(string query)
        => Assert.Null(ConnectorMailSenderRecovery.Create(query));

    [Fact]
    public void Alternative_preserves_every_other_query_constraint()
    {
        var recovery = ConnectorMailSenderRecovery.Create("(internship motion) from:\"Sorren\" after:1792434600 before:1792521000 -label:spam");
        Assert.StartsWith("(internship motion) {", recovery.Query);
        Assert.EndsWith(" after:1792434600 before:1792521000 -label:spam", recovery.Query);
        Assert.Contains("from:\"soren\"", recovery.Query);
    }

    [Theory]
    [InlineData("Someone Else <contact@sorren.example.test>")]
    [InlineData("Someone Else <contact@example.test>")]
    [InlineData("SorrenCompany <contact@example.test>")]
    public void Domain_or_substring_is_not_a_sender_identity(string header)
        => Assert.False(ConnectorMailSenderRecovery.Create("from:Sorren").MatchesHeader(header));

    [Fact]
    public void Body_mentions_do_not_become_retained_candidates_and_multiple_senders_remain_visible()
    {
        var result = ConnectorMailSenderRecovery.Create("from:Sorren").Filter(Page(
            ("12345678", "aaaa1111", "Soren <soren@example.test>"),
            ("22345678", "aaaa2222", "Sorren <sorren@example.test>"),
            ("32345678", "aaaa3333", "Somebody <other@example.test>")));
        var data = result.GetProperty("result").GetProperty("data");
        Assert.Equal(2, data.GetProperty("messages").GetArrayLength());
        Assert.Equal(2, data.GetProperty("pageItemCount").GetInt32());
        Assert.Equal(3, data.GetProperty("senderRecovery").GetProperty("sourcePageItemCount").GetInt32());
        Assert.DoesNotContain("aaaa3333", data.GetRawText());
        Assert.Contains("aaaa1111", data.GetRawText());
        Assert.Contains("aaaa2222", data.GetRawText());
        Assert.False(result.GetProperty("result").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Missing_sender_headers_cannot_prove_complete_candidate_coverage()
    {
        var result = ConnectorMailSenderRecovery.Create("from:Sorren").Filter(Page(
            ("12345678", "aaaa1111", "Soren <soren@example.test>"), ("22345678", "aaaa2222", "")));
        Assert.True(result.GetProperty("result").GetProperty("truncated").GetBoolean());
        Assert.False(result.GetProperty("result").GetProperty("data").GetProperty("metadataSample").GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void Provider_failure_and_continuation_are_not_reclassified_as_complete_success()
    {
        var recovery = ConnectorMailSenderRecovery.Create("from:Sorren");
        var failure = JsonSerializer.SerializeToElement(new { state = "failed", verified = false, errorCode = "rate_limited" });
        Assert.Equal(failure.GetRawText(), recovery.Filter(failure).GetRawText());
        var page = JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = true,
            data = new { messages = new[] { new { id = "12345678", threadId = "aaaa1111" } },
                sampledMessages = new[] { new { id = "12345678", threadId = "aaaa1111", from = "Soren <soren@example.test>" } },
                hasMore = true, nextPageToken = "opaque-native-cursor", pageItemCount = 1 } } });
        var result = recovery.Filter(page);
        Assert.True(result.GetProperty("result").GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("result").GetProperty("data").GetProperty("hasMore").GetBoolean());
        Assert.Contains("opaque-native-cursor", result.GetRawText());
    }

    private static JsonElement Page(params (string Id, string Thread, string From)[] messages)
        => JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
            data = new { queryUsed = "native query", messages = messages.Select(m => new { id = m.Id, threadId = m.Thread }),
                sampledMessages = messages.Select(m => new { id = m.Id, threadId = m.Thread, from = m.From, subject = "Sorren", snippet = "Sorren" }),
                pageItemCount = messages.Length, hasMore = false, metadataSample = new { requested = messages.Length, returned = messages.Length, complete = true } } } });
}
