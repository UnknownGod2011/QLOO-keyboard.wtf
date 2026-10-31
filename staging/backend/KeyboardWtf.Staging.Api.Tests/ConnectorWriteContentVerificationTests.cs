using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorWriteContentVerificationTests
{
    [Theory]
    [InlineData("2026-11-06", "2026-11-07")]
    [InlineData("2028-02-29", "2028-03-01")]
    [InlineData("2026-12-31", "2027-01-02")]
    public void Calendar_all_day_plan_preserves_dates_and_exclusive_end_without_inventing_a_time(string start, string end)
    {
        var operation = ConnectorIds.New();
        var plan = ConnectorOperations.Plan("calendar_create", Args(new { title = "Review the plan", start, end }), operation);
        var body = JsonSerializer.SerializeToElement(plan.Body);
        Assert.Equal("https://www.googleapis.com/calendar/v3/calendars/primary/events", plan.Endpoint);
        Assert.Equal("POST", plan.Method);
        Assert.Equal("k" + operation, body.GetProperty("id").GetString());
        Assert.Equal(start, body.GetProperty("start").GetProperty("date").GetString());
        Assert.Equal(end, body.GetProperty("end").GetProperty("date").GetString());
        Assert.False(body.GetProperty("start").TryGetProperty("dateTime", out _));
        Assert.False(body.GetProperty("end").TryGetProperty("dateTime", out _));
        Assert.False(body.TryGetProperty("attendees", out _));
        Assert.Equal(plan.Endpoint + "/k" + operation, plan.ReconcileEndpoint);
    }

    [Theory]
    [InlineData("2026-11-06", "2026-11-06")]
    [InlineData("2026-11-07", "2026-11-06")]
    [InlineData("2026-02-29", "2026-03-01")]
    [InlineData("2026-11-6", "2026-11-07")]
    [InlineData("2026-11-06", "2026-11-7")]
    [InlineData("2026-11-06", "2026-11-07T10:00:00+05:30")]
    [InlineData("2026-11-06T10:00:00+05:30", "2026-11-07")]
    [InlineData("2026-11-06", "2027-11-08")]
    [InlineData("2026-11-06T10:00:00", "2026-11-06T11:00:00")]
    public void Calendar_creation_rejects_invalid_mixed_or_unbounded_date_intervals(string start, string end)
    {
        Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("calendar_create",
            Args(new { title = "Review the plan", start, end }), ConnectorIds.New()));
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("wrong_start", false)]
    [InlineData("wrong_end", false)]
    [InlineData("missing_date", false)]
    [InlineData("time_instead", false)]
    [InlineData("mixed_start", false)]
    [InlineData("mixed_end", false)]
    [InlineData("wrong_title", false)]
    [InlineData("wrong_description", false)]
    [InlineData("wrong_id", false)]
    [InlineData("cancelled", false)]
    [InlineData("attendees", false)]
    public async Task Calendar_all_day_completion_requires_the_exact_dates_and_independent_readback(string scenario, bool verified)
    {
        var operation = ConnectorIds.New();
        var plan = ConnectorOperations.Plan("calendar_create", Args(new
        {
            title = "Review the plan", start = "2026-11-06", end = "2026-11-07", description = "Private notes"
        }), operation);
        var resource = new Dictionary<string, object>
        {
            ["id"] = scenario == "wrong_id" ? "unrelated" : "k" + operation,
            ["status"] = scenario == "cancelled" ? "cancelled" : "confirmed",
            ["summary"] = scenario == "wrong_title" ? "Other event" : "Review the plan",
            ["description"] = scenario == "wrong_description" ? "Other notes" : "Private notes",
            ["start"] = scenario == "missing_date" ? new { } : scenario == "time_instead"
                ? (object)new { dateTime = "2026-11-06T00:00:00+05:30" } : scenario == "mixed_start"
                ? new { date = "2026-11-06", dateTime = "2026-11-06T00:00:00+05:30" }
                : new { date = scenario == "wrong_start" ? "2026-11-05" : "2026-11-06" },
            ["end"] = scenario == "mixed_end"
                ? (object)new { date = "2026-11-07", dateTime = "2026-11-07T00:00:00+05:30" }
                : new { date = scenario == "wrong_end" ? "2026-11-08" : "2026-11-07" }
        };
        if (scenario == "attendees") resource["attendees"] = new[] { new { email = "other@example.test" } };
        var provider = new ReadbackProvider(JsonSerializer.SerializeToElement(resource));
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "calendar_create", operation, plan,
            JsonSerializer.SerializeToElement(new { id = "k" + operation }), default);
        Assert.Equal(verified, result.HasValue);
        Assert.Equal(plan.ReconcileEndpoint, Assert.Single(provider.Endpoints));
        Assert.Equal(0, provider.Writes);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("equivalent_offset", true)]
    [InlineData("wrong_title", false)]
    [InlineData("wrong_start", false)]
    [InlineData("wrong_end", false)]
    [InlineData("wrong_description", false)]
    [InlineData("wrong_id", false)]
    [InlineData("cancelled", false)]
    [InlineData("attendees", false)]
    [InlineData("missing_time", false)]
    [InlineData("malformed_time", false)]
    [InlineData("mixed_start", false)]
    [InlineData("mixed_end", false)]
    public async Task Calendar_completion_requires_the_requested_event_fields_not_only_its_identity(string scenario, bool verified)
    {
        var operation = ConnectorIds.New();
        var plan = ConnectorOperations.Plan("calendar_create", Args(new
        {
            title = "Private planning", start = "2026-10-12T10:00:00+05:30", end = "2026-10-12T10:30:00+05:30", description = "Review the plan"
        }), operation);
        var resource = new Dictionary<string, object>
        {
            ["id"] = scenario == "wrong_id" ? "unrelated" : "k" + operation,
            ["status"] = scenario == "cancelled" ? "cancelled" : "confirmed",
            ["summary"] = scenario == "wrong_title" ? "Unrelated" : "Private planning",
            ["description"] = scenario == "wrong_description" ? "Other details" : "Review the plan",
            ["start"] = scenario == "missing_time" ? new { date = "2026-10-12" } : (object)new
            {
                dateTime = scenario == "wrong_start" ? "2026-10-12T10:01:00+05:30" : scenario == "malformed_time" ? "not a time"
                    : scenario == "equivalent_offset" ? "2026-10-12T04:30:00Z" : "2026-10-12T10:00:00+05:30"
            },
            ["end"] = new { dateTime = scenario == "wrong_end" ? "2026-10-12T10:31:00+05:30" : "2026-10-12T10:30:00+05:30" }
        };
        if (scenario == "mixed_start") resource["start"] = new { date = "2026-10-12", dateTime = "2026-10-12T10:00:00+05:30" };
        if (scenario == "mixed_end") resource["end"] = new { date = "2026-10-12", dateTime = "2026-10-12T10:30:00+05:30" };
        if (scenario == "attendees") resource["attendees"] = new[] { new { email = "other@example.test" } };
        var provider = new ReadbackProvider(JsonSerializer.SerializeToElement(resource));
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "calendar_create", operation, plan,
            JsonSerializer.SerializeToElement(new { id = "k" + operation }), default);
        Assert.Equal(verified, result.HasValue);
        Assert.Equal(plan.ReconcileEndpoint, Assert.Single(provider.Endpoints));
        Assert.Equal(0, provider.Writes);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("wrong_title", false)]
    [InlineData("wrong_id", false)]
    [InlineData("nonblank", false)]
    [InlineData("table", false)]
    [InlineData("second_tab_content", false)]
    [InlineData("missing_tabs", false)]
    [InlineData("malformed_tabs", false)]
    [InlineData("malformed_text", false)]
    [InlineData("header_content", false)]
    [InlineData("blank_child", true)]
    [InlineData("nonblank_child", false)]
    [InlineData("malformed_child", false)]
    [InlineData("tab_limit", false)]
    [InlineData("depth_limit", false)]
    [InlineData("inline_object", false)]
    [InlineData("positioned_object", false)]
    [InlineData("inline_object_map", false)]
    public async Task Blank_document_completion_checks_its_title_and_all_tab_content(string scenario, bool verified)
    {
        var operation = ConnectorIds.New();
        var plan = ConnectorOperations.Plan("docs_create", Args(new { title = "Private notes" }), operation);
        object Tab(string text) => new { documentTab = new { body = new { content = new object[]
        {
            new { sectionBreak = new { } }, new { paragraph = new { elements = new[] { new { textRun = new { content = text } } } } }
        } } } };
        var resource = new Dictionary<string, object>
        {
            ["documentId"] = scenario == "wrong_id" ? "unrelated" : "exactDocument",
            ["title"] = scenario == "wrong_title" ? "Unrelated" : "Private notes",
            ["tabs"] = scenario == "second_tab_content" ? new object[] { Tab("\n"), Tab("Unexpected content") }
                : new[] { Tab(scenario == "nonblank" ? "Unexpected content" : "\n") }
        };
        if (scenario == "missing_tabs") resource.Remove("tabs");
        if (scenario == "malformed_tabs") resource["tabs"] = new { };
        if (scenario == "table") resource["tabs"] = new[] { new { documentTab = new { body = new { content = new[] { new { table = new { } } } } } } };
        if (scenario == "malformed_text") resource["tabs"] = new[] { new { documentTab = new { body = new { content = new[]
            { new { paragraph = new { elements = new[] { new { textRun = new { content = 7 } } } } } } } } } };
        if (scenario == "header_content") resource["tabs"] = new[] { new { documentTab = new { body = new { content = Array.Empty<object>() },
            headers = new { existing = new { content = "Unexpected header" } } } } };
        if (scenario is "blank_child" or "nonblank_child" or "malformed_child") resource["tabs"] = new[] { new
        {
            documentTab = JsonSerializer.SerializeToElement(Tab("\n")).GetProperty("documentTab"),
            childTabs = scenario == "malformed_child" ? (object)new { } : new[] { Tab(scenario == "nonblank_child" ? "Unexpected child content" : "\n") }
        } };
        if (scenario == "tab_limit") resource["tabs"] = Enumerable.Repeat(Tab("\n"), 257).ToArray();
        if (scenario == "depth_limit")
        {
            object nested = Tab("\n");
            for (var i = 0; i < 14; i++) nested = new { documentTab = JsonSerializer.SerializeToElement(Tab("\n")).GetProperty("documentTab"), childTabs = new[] { nested } };
            resource["tabs"] = new[] { nested };
        }
        if (scenario == "inline_object") resource["tabs"] = new[] { new { documentTab = new { body = new { content = new[]
            { new { paragraph = new { elements = new[] { new { inlineObjectElement = new { inlineObjectId = "image" } } } } } } } } } };
        if (scenario is "positioned_object" or "inline_object_map")
        {
            var document = JsonSerializer.SerializeToElement(Tab("\n")).GetProperty("documentTab").EnumerateObject()
                .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
            document[scenario == "positioned_object" ? "positionedObjects" : "inlineObjects"] = new { image = new { objectId = "image" } };
            resource["tabs"] = new[] { new { documentTab = document } };
        }
        var provider = new ReadbackProvider(JsonSerializer.SerializeToElement(resource));
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "docs_create", operation, plan,
            JsonSerializer.SerializeToElement(new { documentId = "exactDocument" }), default);
        Assert.Equal(verified, result.HasValue);
        Assert.Single(provider.Endpoints);
        Assert.Equal(0, provider.Writes);
        if (verified) Assert.EndsWith("?includeTabsContent=true", provider.Endpoints[0]);
    }

    [Fact]
    public async Task Lost_document_create_ack_never_guesses_an_identity_or_dispatches_again()
    {
        var operation = ConnectorIds.New();
        var provider = new ReadbackProvider(JsonSerializer.SerializeToElement(new { }));
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "docs_create", operation,
            ConnectorOperations.Plan("docs_create", Args(new { title = "Private notes" }), operation), null, default);
        Assert.Null(result); Assert.Empty(provider.Endpoints); Assert.Equal(0, provider.Writes);
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
    private sealed class ReadbackProvider(JsonElement result) : IConnectorProvider
    {
        internal readonly List<string> Endpoints = [];
        internal int Writes;
        public Task<JsonElement> RequestAsync(string account, string endpoint, string method, object? body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Endpoints.Add(endpoint);
            if (method != "GET") Writes++;
            Assert.Equal("ca_test123", account); Assert.Equal("GET", method); Assert.Null(body);
            return Task.FromResult(result);
        }
        public Task<ProviderConnectReply> ConnectAsync(string user, string config, string callback, string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderConnection?> GetAsync(string account, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync(string account, CancellationToken ct) => throw new NotSupportedException();
    }
}

public sealed partial class ConnectorPlatformTests
{
    [Fact]
    public async Task Mismatched_calendar_readback_settles_unknown_and_never_resends_the_claimed_write()
    {
        var f = Fixture(); await Connect(f); f.Provider.CalendarContentMismatch = true;
        var task = await f.Tasks.ResolveAsync(f.Caller, new("googlecalendar", "Create a private event"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "calendar_create", Args(new
            { title = "Private planning", start = "2026-10-12T10:00:00+05:30", end = "2026-10-12T10:30:00+05:30" }), true);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.Equal("outcome_unknown", reply.State); Assert.False(reply.Verified); Assert.Null(reply.Result);
        var status = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
        Assert.Equal("failed", status.State); Assert.Equal("outcome_unknown", Assert.Single(status.Operations).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller, request, default));
        Assert.Equal(1, f.Provider.Writes);
    }
}
