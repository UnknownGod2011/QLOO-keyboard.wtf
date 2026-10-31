namespace KeyboardWtf.Tests;
using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorMailSearchArgumentsTests
{
    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value));

    [Theory]
    [InlineData("Could you find my emails from Sorren?", "Sorren", "from:\"Sorren\"")]
    [InlineData("Could you find an email from Sorren and open that thread in a new tab?", "Sorren", "from:\"Sorren\"")]
    [InlineData("Find my conversation with Mira and open it in a new tab.", "Mira", "{from:\"Mira\" to:\"Mira\" cc:\"Mira\"}")]
    [InlineData("Find the email by María López.", "maría lópez", "from:\"María López\"")]
    [InlineData("Could you find my emails from Sorren?", "Soren", "from:\"Sorren\"")]
    [InlineData("Could you find my emails from Sorren?", "to:Sorren", "from:\"Sorren\"")]
    [InlineData("Could you find my conversation with Sorren and open it in a new tab?", "sent to:Sorren", "{from:\"Sorren\" to:\"Sorren\" cc:\"Sorren\"}")]
    public void Direct_native_lookup_preserves_the_requested_person_relation(string request, string query, string expected)
    {
        var args = Args(new { query });
        ConnectorMailSearchArguments.Normalize(args, false, "UTC", request: request);
        Assert.Equal(expected, args["query"].GetString());
    }

    [Theory]
    [InlineData("Find emails about Sorren.", "Sorren", false)]
    [InlineData("Find emails from Sorren about an internship.", "Sorren", false)]
    [InlineData("Find emails from Sorren yesterday.", "Sorren", false)]
    [InlineData("Find emails from Sorren in my work account.", "Sorren", false)]
    [InlineData("Find emails from Sorren and send a reply.", "Sorren", false)]
    [InlineData("Find emails from Sorren.", "Sorren", true)]
    public void Preparation_never_rewrites_topics_scope_literals_or_model_refinement(string request, string query, bool literal)
    {
        var args = Args(new { query });
        ConnectorMailSearchArguments.Normalize(args, literal, "UTC", request: request);
        Assert.Equal(query, args["query"].GetString());
    }

    [Theory]
    [InlineData("from:Soren")]
    [InlineData("Soren")]
    [InlineData("subject:research from:Soren")]
    public void Later_model_refinement_is_preserved_without_repreparing_the_first_request(string query)
    {
        var args = Args(new { query });
        ConnectorMailSearchArguments.Normalize(args, false, "UTC");
        Assert.Equal(query, args["query"].GetString());
    }

    [Theory]
    [InlineData("today", "India Standard Time", "2026-10-20T20:13:00Z", "2026-10-21", 24)]
    [InlineData("yesterday", "India Standard Time", "2026-10-20T20:13:00Z", "2026-10-20", 24)]
    [InlineData("yesterday", "Eastern Standard Time", "2026-03-09T05:00:00Z", "2026-03-08", 23)]
    [InlineData("yesterday", "UTC", "2027-01-01T01:00:00Z", "2026-12-31", 24)]
    public void Relative_native_day_uses_frozen_local_calendar_not_UTC(string value, string zone, string now, string expectedDay, int hours)
    {
        var args = Args(new { sender = "Mira", receivedOn = value });
        ConnectorMailSearchArguments.Normalize(args, false, zone, DateTimeOffset.Parse(now));
        var bounds = args["query"].GetString().Split(' ').TakeLast(2).Select(part => long.Parse(part.Split(':')[1])).ToArray();
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(bounds[0]), TimeZoneInfo.FindSystemTimeZoneById(zone));
        Assert.Equal(expectedDay, local.ToString("yyyy-MM-dd"));
        Assert.Equal(hours * 3600, bounds[1] - bounds[0]);
        Assert.Single(args);
    }

    [Fact]
    public void Relative_day_without_frozen_clock_dispatches_nothing_and_preserves_arguments()
    {
        var args = Args(new { sender = "Mira", receivedOn = "yesterday" });
        var before = JsonSerializer.Serialize(args);
        Assert.Throws<ArgumentException>(() => ConnectorMailSearchArguments.Normalize(args, false, "UTC"));
        Assert.Equal(before, JsonSerializer.Serialize(args));
    }

    [Fact]
    public void Typed_scope_keeps_OR_terms_inside_required_sender_and_day()
    {
        var args = Args(new { query = "internship OR placement", sender = "Mira Raman", receivedOn = "2026-03-08" });
        ConnectorMailSearchArguments.Normalize(args, false, "Eastern Standard Time");
        var query = args["query"].GetString();
        Assert.StartsWith("(internship OR placement) from:\"Mira Raman\" after:", query);
        var bounds = query.Split(' ').TakeLast(2).Select(part => long.Parse(part.Split(':')[1])).ToArray();
        Assert.Equal(23 * 3600, bounds[1] - bounds[0]);
        Assert.Single(args);
    }

    [Fact]
    public void Failed_conversion_is_atomic_and_never_changes_literal_queries()
    {
        var args = Args(new { query = "subject:\"literal\"", sender = "Mira" });
        var original = JsonSerializer.Serialize(args);
        Assert.Throws<ArgumentException>(() => ConnectorMailSearchArguments.Normalize(args, true, "UTC"));
        Assert.Equal(original, JsonSerializer.Serialize(args));
    }

    [Theory]
    [InlineData("\"date:unknown\"")]
    [InlineData("subject:(review OR planning)")]
    [InlineData("header:X-Google-Calendar-Notification:rsvpWithNote")]
    [InlineData("https://example.test/path?q=a:b")]
    [InlineData("from:\"Mira Raman\" newer_than:7d has:attachment")]
    public void Provider_grammar_preserves_valid_operators_groups_and_literals(string query)
        => ConnectorMailDateScope.ValidateInferredQuery(query);

    [Theory]
    [InlineData("received:2026-10-19")]
    [InlineData("on:2026/10/19")]
    [InlineData("-sender:Mira")]
    [InlineData("{date:2026/10/19 from:Mira}")]
    public void Unsupported_unquoted_fields_are_errors_instead_of_empty_searches(string query)
        => Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.ValidateInferredQuery(query));
}
