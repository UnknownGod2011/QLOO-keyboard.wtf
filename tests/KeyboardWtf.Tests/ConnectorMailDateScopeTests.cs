namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorMailDateScopeTests
{
    private static readonly DateTimeOffset RequestUtc = DateTimeOffset.Parse("2026-10-11T19:49:00Z");
    private const string Zone = "India Standard Time";
    private const string DayQuery = "after:1791743400 before:1791829800";
    private static readonly ConnectorReadCapability[] Mail = [
        new("mail_search", "Search candidates", new Dictionary<string, string> { ["query"] = "native query", ["pageToken"] = "observed cursor" }),
        new("mail_read", "Read content", new Dictionary<string, string> { ["id"] = "observed id" })];
    private static string Step(string action, string capability = "", string queryOrId = "", string answer = "", string[] references = null, bool partial = false)
        => JsonSerializer.Serialize(new { action, capability,
            arguments_json = JsonSerializer.Serialize(capability == "mail_search" ? (object)new { query = queryOrId }
                : capability == "mail_read" ? new { id = queryOrId } : new { }),
            answer, references = references ?? [], partial });
    private static ConnectorReadReasonerInput Input(string request, string seed = null) => new(request, Mail)
        { RequestUtc = RequestUtc, RequestTimeZoneId = Zone, InitialCapability = seed is null ? null : "mail_search",
            InitialArguments = seed is null ? null : JarvisConnectorController.ParseArguments(JsonSerializer.Serialize(new { query = seed })) };
    private static JsonElement Receipt(object data, bool truncated = false) => JsonSerializer.SerializeToElement(
        new { state = "completed", verified = true, result = new { data, truncated } });
    private static JsonElement Empty(string query, bool more = false, bool sampleComplete = true, bool truncated = false)
        => Receipt(new { queryUsed = query, messages = Array.Empty<object>(), pageItemCount = 0, hasMore = more,
            nextPageToken = more ? "observed-page" : "", metadataSample = new { requested = 0, returned = 0, complete = sampleComplete } }, truncated);

    [Fact]
    public void Today_uses_the_originating_local_calendar_day_and_fixed_unix_bounds()
    {
        var scope = ConnectorMailDateScope.Freeze("Check today's emails", RequestUtc, Zone);
        Assert.Equal("2026-10-12", scope.LocalDay); Assert.Equal(DayQuery, scope.Query);
        Assert.Equal(RequestUtc, scope.RequestUtc); Assert.Equal(Zone, scope.TimeZoneId);
        Assert.Equal("(research) " + DayQuery, scope.Apply("research", false));
    }
    [Fact]
    public void Yesterday_uses_the_previous_local_day_even_when_utc_is_already_yesterday()
    {
        var scope = ConnectorMailDateScope.Freeze("I received an email yesterday", RequestUtc, Zone);
        Assert.NotNull(scope);
        Assert.Equal("2026-10-11", scope.LocalDay);
        Assert.Equal("yesterday", scope.RelativeDay);
        Assert.Equal(1791657000, scope.AfterUnixSeconds);
        Assert.Equal(1791743400, scope.BeforeUnixSeconds);
        Assert.Throws<ArgumentException>(() => scope.Apply("from:Sender after:1791570600 before:1791657000", false));
    }
    [Theory]
    [InlineData("Find mail but not yesterday")]
    [InlineData("If I received the email yesterday, find it")]
    public void Yesterday_cannot_turn_a_negative_or_conditional_mention_into_positive_day_authority(string request)
        => Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.Freeze(request, RequestUtc, Zone));
    [Theory]
    [InlineData("research OR unrelated")]
    [InlineData("{research unrelated}")]
    [InlineData("(research OR {professor scientist})")]
    [InlineData("subject:\"after:old before:old\" research")]
    public void Every_refinement_and_boolean_group_is_inside_the_same_compulsory_day(string query)
    {
        var scope = ConnectorMailDateScope.Freeze("Find today's research responses", RequestUtc, Zone);
        var scoped = scope.Apply(query, false);
        Assert.Equal("(" + query + ") " + DayQuery, scoped);
        Assert.Equal(scoped, scope.Apply(scoped, false));
    }
    [Theory]
    [InlineData("research OR (after:1)")]
    [InlineData("research -after:1791743400")]
    [InlineData("research before:1791829799")]
    [InlineData("research older_than:1d")]
    [InlineData("research (unfinished")]
    [InlineData("research {mismatched)")]
    [InlineData("subject:\"unfinished")]
    [InlineData("research\nOR unrelated")]
    public void Conflicting_or_malformed_scope_cannot_dispatch(string query)
        => Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.Freeze("Find today's emails", RequestUtc, Zone).Apply(query, false));

    [Theory]
    [InlineData("Find the email with subject \"yesterday\"")]
    [InlineData("Find the email with subject \"today\"")]
    [InlineData("Search using: subject:\"today\"")]
    public void Other_phrases_and_literal_subject_terms_do_not_acquire_an_inferred_today_scope(string request)
        => Assert.Null(ConnectorMailDateScope.Freeze(request, default, null));
    [Fact]
    public void Today_without_a_frozen_clock_or_valid_zone_fails_closed()
    {
        Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.Freeze("Read today's email", default, Zone));
        Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.Freeze("Read today's email", RequestUtc, "unverified-zone"));
    }
    [Fact]
    public void Human_literal_filters_are_preserved_exactly_or_rejected_without_rewriting()
    {
        var scope = ConnectorMailDateScope.Freeze("Search today's emails using exact query: " + DayQuery, RequestUtc, Zone);
        var literal = "  subject:\"research OR news\" " + DayQuery + "  ";
        Assert.Equal(literal, scope.Apply(literal, true));
        Assert.Throws<ArgumentException>(() => scope.Apply("research OR " + DayQuery, true));
        Assert.Throws<ArgumentException>(() => scope.Apply("subject:research", true));
        Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.Freeze(
            "Search today's email using exact query: after:1 before:2", RequestUtc, Zone));
        Assert.False(scope.CanInspectWholeDay);
        Assert.False(ConnectorMailDateScope.Freeze("Find today's email from:professor@example.test", RequestUtc, Zone).CanInspectWholeDay);
    }
    [Fact]
    public async Task Native_seed_and_refined_query_retain_frozen_date_context_in_every_model_prompt()
    {
        var proposals = new Queue<string>([Step("execute", "mail_search", "research OR professor"),
            Step("execute", "mail_read", "observed"), Step("answer", answer: "The observed professor responded about research.", references: ["r3"])]);
        var queries = new List<string>();
        var result = await ConnectorReadReasoner.RunAsync(Input("Check today's emails for a research response", "research"),
            (_, prompt, _, _, _) =>
            {
                using var document = JsonDocument.Parse(prompt);
                var scope = document.RootElement.GetProperty("mailDateScope");
                Assert.Equal("2026-10-12", scope.GetProperty("localDay").GetString());
                Assert.Equal(1791743400, scope.GetProperty("afterUnixSeconds").GetInt64());
                Assert.Equal(1791829800, scope.GetProperty("beforeUnixSeconds").GetInt64());
                Assert.Equal(RequestUtc, scope.GetProperty("requestUtc").GetDateTimeOffset());
                return Task.FromResult(proposals.Dequeue());
            }, (capability, arguments, _) =>
            {
                if (capability == "mail_search") { queries.Add(arguments["query"].GetString()); return Task.FromResult(Receipt(new { messages = new[] { new { id = "observed" } } })); }
                return Task.FromResult(Receipt(new { id = "observed", text = "Thank you for your research proposal; I can discuss it next week." }));
            }, null, default);
        Assert.Equal(new[] { "(research) " + DayQuery, "(research OR professor) " + DayQuery }, queries);
        Assert.Equal(new[] { "r3" }, result.EvidenceReferences); Assert.Equal(3, result.Executions);
    }
    [Fact]
    public async Task Empty_verified_entire_day_finishes_without_a_structured_model_request()
    {
        var models = 0; var executions = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input("Check today's emails for a professor's research response", DayQuery),
            (_, _, _, _, _) => { ++models; throw new InvalidOperationException("A model is not needed for this deterministic empty day."); },
            (_, arguments, _) => { ++executions; return Task.FromResult(Empty(arguments["query"].GetString())); }, null, default);
        Assert.Equal(0, models); Assert.Equal(1, executions); Assert.Equal(0, result.ModelCalls);
        Assert.Equal("not_found", result.GoalOutcome); Assert.True(result.Partial); Assert.Empty(result.EvidenceReferences);
        Assert.Contains("exact Gmail day query", result.Answer); Assert.Contains("No professor-response content", result.Answer);
    }
    [Fact]
    public async Task Observed_cursor_uses_canonical_scope_before_comparison_but_cannot_change_topic()
    {
        string Page(string query) => JsonSerializer.Serialize(new { action = "execute", capability = "mail_search",
            arguments_json = JsonSerializer.Serialize(new { query, pageToken = "observed-page" }),
            answer = "", references = Array.Empty<string>(), partial = false });
        var proposals = new Queue<string>([Page("unrelated"), Page("research"),
            Step("execute", "mail_read", "observed"), Step("answer", answer: "The observed message responds about research.", references: ["r3"])]);
        var queries = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input("Check today's research responses", "research"),
            (_, _, _, _, _) => { ++models; return Task.FromResult(proposals.Dequeue()); },
            (capability, arguments, _) =>
            {
                if (capability == "mail_search")
                {
                    queries.Add(arguments["query"].GetString());
                    if (queries.Count == 1) Assert.False(arguments.ContainsKey("pageToken"));
                    else Assert.Equal("observed-page", arguments["pageToken"].GetString());
                    return Task.FromResult(Receipt(new { queryUsed = arguments["query"].GetString(),
                        messages = new[] { new { id = "observed" } }, nextPageToken = queries.Count == 1 ? "observed-page" : "" }));
                }
                return Task.FromResult(Receipt(new { id = "observed", text = "I received your research proposal." }));
            }, null, default);
        Assert.Equal(new[] { "(research) " + DayQuery, "(research) " + DayQuery }, queries);
        Assert.Equal(4, models); Assert.Equal(3, result.Executions); Assert.Equal(new[] { "r3" }, result.EvidenceReferences);
    }
    [Fact]
    public async Task Empty_inferred_topic_checks_the_bounded_day_before_spending_a_model_request()
    {
        var queries = new List<string>();
        var result = await ConnectorReadReasoner.RunAsync(Input("Check today's emails for a professor's research response", "research professor"),
            (_, _, _, _, _) => throw new InvalidOperationException("The actual empty day can answer without a model."),
            (_, arguments, _) => { var query = arguments["query"].GetString(); queries.Add(query); return Task.FromResult(Empty(query)); }, null, default);
        Assert.Equal(new[] { "(research professor) " + DayQuery, DayQuery }, queries);
        Assert.Equal(2, result.Executions); Assert.Equal(0, result.ModelCalls); Assert.Equal("not_found", result.GoalOutcome);
    }
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public async Task Cursor_sample_ambiguity_or_truncation_cannot_use_the_empty_day_shortcut(bool more, bool sampleComplete, bool truncated)
    {
        var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input("Check today's emails for a research response", DayQuery),
            (_, _, _, _, _) => { ++models; return Task.FromResult(Step("answer", answer: "Further scoped reading is needed.", partial: true)); },
            (_, arguments, _) => Task.FromResult(Empty(arguments["query"].GetString(), more, sampleComplete, truncated)), null, default);
        Assert.True(models > 0); Assert.True(result.Partial);
    }
    [Fact]
    public async Task Explicit_sender_filter_does_not_fall_back_to_the_whole_day()
    {
        var queries = new List<string>(); var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input("Find today's research response from:professor@example.test", "from:professor@example.test research"),
            (_, _, _, _, _) => { ++models; return Task.FromResult(Step("answer", answer: "No confident matching response was established.", partial: true)); },
            (_, arguments, _) => { queries.Add(arguments["query"].GetString()); return Task.FromResult(Empty(arguments["query"].GetString())); }, null, default);
        // Existing explicit-filter mode permits an honest partial answer after
        // one exact search; it does not require semantic query broadening.
        Assert.Single(queries); Assert.DoesNotContain(DayQuery, queries); Assert.Equal(1, models); Assert.True(result.Partial);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_present_or_absent_cannot_be_determined_from_a_preview(bool negative)
    {
        var proposals = new Queue<string>();
        if (negative) proposals.Enqueue(Step("execute", "mail_search", "professor response"));
        proposals.Enqueue(Step("answer", answer: negative ? "No professor replied." : "A professor replied.", references: negative ? [] : ["r1"], partial: negative));
        foreach (var proposal in new[] {
            Step("execute", "mail_read", "observed"),
            Step("answer", answer: "The observed body acknowledges the research proposal.", references: [negative ? "r3" : "r2"]) })
            proposals.Enqueue(proposal);
        var calls = new List<string>();
        var result = await ConnectorReadReasoner.RunAsync(Input("Check today's emails for a research response from a professor", "research"),
            (_, _, _, _, _) => Task.FromResult(proposals.Dequeue()), (capability, _, _) =>
            {
                calls.Add(capability);
                return Task.FromResult(Receipt(capability == "mail_search" ? (object)new { messages = new[] { new { id = "observed" } }, sampledMessages = new[] { new { snippet = "Research response" } } }
                    : new { id = "observed", text = "Thank you for the research proposal." }));
            }, null, default);
        Assert.Equal(negative ? new[] { "mail_search", "mail_search", "mail_read" } : ["mail_search", "mail_read"], calls);
        Assert.Equal(new[] { negative ? "r3" : "r2" }, result.EvidenceReferences);
        Assert.DoesNotContain("No professor", result.Answer); Assert.Equal(negative ? 4 : 3, result.ModelCalls);
    }
    [Theory]
    [InlineData("Find the professor's reply", true)]
    [InlineData("Read the professor's reply today", true)]
    [InlineData("Check if the professor has replied today", true)]
    [InlineData("Check today's emails for a research response", true)]
    [InlineData("Find the reply to my research email", true)]
    [InlineData("Reply to the professor", false)]
    [InlineData("Can you please reply to the professor?", false)]
    [InlineData("Find the professor's reply and reply to it", false)]
    [InlineData("Find a research response, then reply", false)]
    [InlineData("Summarize and send this reply", false)]
    [InlineData("Create a draft reply", false)]
    [InlineData("How many professor replies are there?", false)]
    public void Reply_nouns_select_a_read_without_granting_any_reply_write(string request, bool required)
        => Assert.Equal(required, ConnectorReadReasoner.Required(request));
}
