using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorReadCoverageTests
{
    [Theory]
    [InlineData("calendar_list", "items")]
    [InlineData("drive_search", "files")]
    [InlineData("youtube_search", "items")]
    [InlineData("youtube_channel", "items")]
    public void Google_page_completion_uses_the_documented_end_marker_not_http_success(string capability, string field)
    {
        foreach (var next in new[] { "", "another-page" })
        {
            var data = new Dictionary<string, object> { [field] = new[] { new { id = "one" } } };
            if (next.Length > 0) data["nextPageToken"] = next;
            var result = Safe(data, capability);
            var coverage = result.GetProperty("coverage");
            Assert.Equal(next.Length == 0, coverage.GetProperty("complete").GetBoolean());
            Assert.Equal(next.Length > 0, result.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, coverage.GetProperty("pageItemCount").GetInt32());
            Assert.Equal(next.Length > 0, coverage.GetProperty("hasMore").GetBoolean());
            Assert.True(result.GetRawText().IndexOf("coverage", StringComparison.Ordinal) < result.GetRawText().IndexOf("data", StringComparison.Ordinal));
        }
        AssertPartial(Safe(new Dictionary<string, object> { [field] = Array.Empty<object>(), ["nextPageToken"] = 12 }, capability));
        AssertPartial(Safe(new { unfamiliar_result = true }, capability));
        AssertPartial(Safe(new Dictionary<string, object> { [field] = Array.Empty<object>(), ["next_cursor"] = "unrecognized" }, capability));
    }

    [Fact]
    public void Drive_incomplete_search_is_requested_and_never_called_complete()
    {
        var plan = ConnectorOperations.Plan("drive_search", Args(new { query = "name' fragment" }), ConnectorIds.New());
        Assert.Contains("incompleteSearch", plan.Endpoint); Assert.Equal("GET", plan.Method); Assert.Null(plan.Body);
        var partial = Safe(new { files = Array.Empty<object>(), incompleteSearch = true }, "drive_search");
        AssertPartial(partial); Assert.Equal("search_incomplete", partial.GetProperty("coverage").GetProperty("status").GetString());
        AssertPartial(Safe(new { files = Array.Empty<object>(), incompleteSearch = "unknown" }, "drive_search"));
        Assert.False(Safe(new { files = Array.Empty<object>(), incompleteSearch = false }, "drive_search").GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("github_repos", 15)]
    [InlineData("github_issues", 15)]
    [InlineData("discord_guilds", 50)]
    public void Array_list_without_response_headers_cannot_claim_a_full_page_is_exhaustive(string capability, int limit)
    {
        var small = Safe(Enumerable.Range(0, limit - 1).Select(i => new { id = i.ToString() }), capability);
        Assert.True(small.GetProperty("coverage").GetProperty("complete").GetBoolean());
        var full = Safe(Enumerable.Range(0, limit).Select(i => new { id = i.ToString() }), capability);
        AssertPartial(full); Assert.Equal(JsonValueKind.Null, full.GetProperty("coverage").GetProperty("hasMore").ValueKind);
        AssertPartial(Safe(new { results = Array.Empty<object>() }, capability));
        AssertPartial(Safe(new[] { "invalid-shape" }, capability));
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(1, 15, false)]
    [InlineData(2, 2, false)]
    public void GitHub_issue_scope_discloses_the_fixed_open_filter_without_changing_continuation(int page, int count, bool complete)
    {
        var args = Args(new { repository = "owner/repo", page });
        var plan = ConnectorOperations.Plan("github_issues", args, ConnectorIds.New());
        Assert.Equal("https://api.github.com/repos/owner/repo/issues?per_page=15&state=open&page=" + page, plan.Endpoint);
        Assert.Equal("GET", plan.Method); Assert.Null(plan.Body); Assert.Null(plan.ReconcileEndpoint);
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(
            Enumerable.Range(0, count).Select(i => new { id = i, number = i + 1, title = "Issue " + i, state = "open" })),
            "github_issues", args);
        var coverage = result.GetProperty("coverage");
        Assert.Equal("open", coverage.GetProperty("fixedFilters").GetProperty("state").GetString());
        Assert.Contains("issues", coverage.GetProperty("scope").GetString());
        Assert.Contains("pull requests", coverage.GetProperty("scope").GetString());
        Assert.Contains("state filter", coverage.GetProperty("scope").GetString());
        Assert.Equal(complete, coverage.GetProperty("complete").GetBoolean());
        Assert.Equal(count, coverage.GetProperty("pageItemCount").GetInt32());
        Assert.Equal("owner/repo", coverage.GetProperty("scopeArguments").GetProperty("repository").GetString());
        Assert.False(coverage.GetProperty("scopeArguments").TryGetProperty("state", out _));
        Assert.False(coverage.GetProperty("scopeArguments").TryGetProperty("page", out _));
        if (count == 15) Assert.Equal(page + 1, coverage.GetProperty("next").GetProperty("page").GetInt32());
        else Assert.Equal(JsonValueKind.Null, coverage.GetProperty("next").ValueKind);
    }

    [Fact]
    public void GitHub_issue_scope_is_explicit_and_supports_closed_state_without_more_permanent_tools()
    {
        var github = ConnectorRegistry.Definitions.Single(definition => definition.Id == "github");
        var issues = github.Capabilities.Single(capability => capability.Id == "github_issues");
        Assert.Contains("issues", issues.Description);
        Assert.Contains("closed", issues.Arguments["state"]);
        Assert.False(issues.Writes);
        Assert.Equal(new[] { "page", "repository", "state" }, issues.Arguments.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(8, github.Capabilities.Length);
    }

    [Fact]
    public void GitHub_issue_scope_filter_is_not_added_to_other_connector_reads()
    {
        Assert.False(Safe(new[] { new { id = 1 } }, "github_repos").GetProperty("coverage").TryGetProperty("fixedFilters", out _));
        Assert.False(Safe(new { files = Array.Empty<object>() }, "drive_search").GetProperty("coverage").TryGetProperty("fixedFilters", out _));
        Assert.False(Safe(new { items = Array.Empty<object>() }, "calendar_list").GetProperty("coverage").TryGetProperty("fixedFilters", out _));
    }

    [Fact]
    public void Slack_history_requires_a_valid_flag_or_cursor_and_checks_both()
    {
        var none = Array.Empty<object>();
        Assert.False(Safe(new { messages = none, has_more = false }, "slack_history").GetProperty("truncated").GetBoolean());
        AssertPartial(Safe(new { messages = none, has_more = true }, "slack_history"));
        AssertPartial(Safe(new { messages = none, has_more = false, response_metadata = new { next_cursor = "more" } }, "slack_history"));
        Assert.False(Safe(new { messages = none, response_metadata = new { next_cursor = "" } }, "slack_history").GetProperty("truncated").GetBoolean());
        AssertPartial(Safe(new { messages = none }, "slack_history"));
        AssertPartial(Safe(new { messages = none, has_more = "false" }, "slack_history"));
        AssertPartial(Safe(new { messages = none, has_more = false, response_metadata = new { next_cursor = 4 } }, "slack_history"));
    }

    [Fact]
    public void Slack_search_uses_nested_pagination_and_rejects_conflicting_or_unknown_shapes()
    {
        var none = Array.Empty<object>();
        Assert.False(Safe(new { messages = new { matches = none, paging = new { page = 1, pages = 1 } } }, "slack_search").GetProperty("truncated").GetBoolean());
        AssertPartial(Safe(new { messages = new { matches = none, paging = new { page = 1, pages = 2 } } }, "slack_search"));
        AssertPartial(Safe(new { messages = new { matches = none, paging = new { page = 2, pages = 2 } } }, "slack_search"));
        Assert.False(Safe(new { messages = new { matches = none, pagination = new { page = 1, page_count = 1 } } }, "slack_search").GetProperty("truncated").GetBoolean());
        AssertPartial(Safe(new { messages = new { matches = none, paging = new { page = 1, pages = 1 }, pagination = new { page = 1, page_count = 2 } } }, "slack_search"));
        AssertPartial(Safe(new { messages = new { matches = none } }, "slack_search"));
    }

    [Fact]
    public void Docs_requests_all_tabs_once_and_discloses_legacy_or_bounded_content()
    {
        var plan = ConnectorOperations.Plan("docs_read", Args(new { id = "document1" }), ConnectorIds.New());
        Assert.Equal("https://docs.googleapis.com/v1/documents/document1?includeTabsContent=true", plan.Endpoint);
        Assert.Equal("GET", plan.Method); Assert.Null(plan.Body); Assert.Null(plan.ReconcileEndpoint);
        object Tab(string text) => new { documentTab = new { body = new { content = new[] { new { paragraph = new { elements = new[] { new { textRun = new { content = text } } } } } } } } };
        var full = Safe(new { documentId = "one", tabs = new[] { Tab("First tab"), Tab("Second tab") } }, "docs_read");
        Assert.False(full.GetProperty("truncated").GetBoolean()); Assert.True(full.GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.Contains("Second tab", full.GetRawText());
        AssertPartial(Safe(new { documentId = "legacy", body = new { content = Array.Empty<object>() } }, "docs_read"));
        var large = Safe(new { tabs = new[] { Tab(new string('界', 60000)) }, access_token = "NEVER_EXPOSE" }, "docs_read");
        AssertPartial(large); Assert.Equal("content_bounded", large.GetProperty("coverage").GetProperty("status").GetString());
        Assert.InRange(large.GetRawText().Length, 1, 64000); Assert.DoesNotContain("NEVER_EXPOSE", large.GetRawText());
    }

    [Fact]
    public void Coverage_never_changes_the_existing_mail_envelope_or_write_plan()
    {
        Assert.False(Safe(new { messages = Array.Empty<object>() }, "mail_search").TryGetProperty("coverage", out _));
        Assert.False(Safe(new { id = "file1" }, "drive_read").TryGetProperty("coverage", out _));
        var plan = ConnectorOperations.Plan("docs_create", Args(new { title = "requested" }), ConnectorIds.New());
        Assert.Equal("https://docs.googleapis.com/v1/documents", plan.Endpoint); Assert.Equal("POST", plan.Method);
    }

    [Fact]
    public void Repository_overviews_keep_useful_facts_without_repeating_vendor_link_metadata()
    {
        var raw = Enumerable.Range(0, 15).Select(i => new { id = i, full_name = "user/project" + i,
            description = "Relevant repository " + i, html_url = "https://github.com/user/project" + i,
            language = "C#", updated_at = "2026-10-09T00:00:00Z", @private = false,
            commits_url = new string('x', 10000), owner = new { avatar_url = new string('y', 10000), access_token = "NEVER_EXPOSE" } }).ToArray();
        var result = Safe(raw, "github_repos");
        Assert.Equal(15, result.GetProperty("data").GetArrayLength());
        Assert.Contains("user/project14", result.GetRawText()); Assert.Contains("Relevant repository 14", result.GetRawText());
        Assert.DoesNotContain("commits_url", result.GetRawText()); Assert.DoesNotContain("NEVER_EXPOSE", result.GetRawText());
        Assert.InRange(result.GetRawText().Length, 1, 7000);
        AssertPartial(result); // smaller output is not proof there are no more pages
        Assert.Equal("page_boundary_unknown", result.GetProperty("coverage").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData('x')]
    [InlineData('界')]
    public void Issue_overviews_preserve_every_reference_before_bounded_body_excerpts(char filler)
    {
        var raw = Enumerable.Range(1, 15).Select(i => new { id = i, number = i,
            title = "Issue " + i, state = "open", created_at = "2026-10-09T00:00:00Z",
            updated_at = "2026-10-09T01:00:00Z", html_url = "https://github.com/test/repo/issues/" + i,
            body = "BODY_" + i + ": " + new string(filler, 12000),
            user = new { login = "author" + i, avatar_url = new string('y', 10000), access_token = "NEVER_EXPOSE" },
            events_url = new string('z', 10000) }).ToArray();
        var result = Safe(raw, "github_issues");
        var items = result.GetProperty("data");
        Assert.Equal(15, items.GetArrayLength());
        for (var index = 0; index < 15; index++)
        {
            var issue = items[index]; var number = index + 1;
            Assert.Equal(number, issue.GetProperty("id").GetInt32());
            Assert.Equal(number, issue.GetProperty("number").GetInt32());
            Assert.Equal("Issue " + number, issue.GetProperty("title").GetString());
            Assert.Equal("open", issue.GetProperty("state").GetString());
            Assert.Equal("author" + number, issue.GetProperty("user").GetProperty("login").GetString());
            Assert.Equal("2026-10-09T00:00:00Z", issue.GetProperty("created_at").GetString());
            Assert.Equal("2026-10-09T01:00:00Z", issue.GetProperty("updated_at").GetString());
            Assert.Equal("https://github.com/test/repo/issues/" + number, issue.GetProperty("html_url").GetString());
        }
        for (var index = 0; index < 15; index++)
        {
            Assert.StartsWith("BODY_" + (index + 1) + ": ", items[index].GetProperty("body").GetString());
            Assert.True(items[index].GetProperty("body_truncated").GetBoolean());
        }
        Assert.DoesNotContain("events_url", result.GetRawText());
        Assert.DoesNotContain("avatar_url", result.GetRawText());
        Assert.DoesNotContain("NEVER_EXPOSE", result.GetRawText());
        Assert.InRange(result.GetRawText().Length, 1, 64000);
        AssertPartial(result);
        Assert.Equal(2, result.GetProperty("coverage").GetProperty("next").GetProperty("page").GetInt32());
        Assert.Equal(15, result.GetProperty("coverage").GetProperty("pageItemCount").GetInt32());
    }

    [Fact]
    public void Short_issue_reads_keep_complete_content_labels_assignees_and_pull_request_marker()
    {
        var result = Safe(new[] { new { id = 1, number = 7, title = "Small issue", state = "closed",
            state_reason = "completed", html_url = "https://github.com/test/repo/issues/7",
            body = "The complete requested detail.", user = new { login = "author", access_token = "NEVER_EXPOSE" },
            labels = new[] { new { name = "bug", description = new string('x', 20000) } },
            assignees = new[] { new { login = "owner", avatar_url = new string('x', 20000) } },
            pull_request = new { html_url = "https://github.com/test/repo/pull/7", patch_url = new string('x', 20000) } } }, "github_issues");
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("coverage").GetProperty("complete").GetBoolean());
        var issue = result.GetProperty("data")[0];
        Assert.Equal("The complete requested detail.", issue.GetProperty("body").GetString());
        Assert.False(issue.GetProperty("body_truncated").GetBoolean());
        Assert.Equal("completed", issue.GetProperty("state_reason").GetString());
        Assert.Equal("bug", issue.GetProperty("labels")[0].GetProperty("name").GetString());
        Assert.Equal("owner", issue.GetProperty("assignees")[0].GetProperty("login").GetString());
        Assert.Equal("https://github.com/test/repo/pull/7", issue.GetProperty("pull_request").GetProperty("html_url").GetString());
        Assert.DoesNotContain("avatar_url", result.GetRawText()); Assert.DoesNotContain("patch_url", result.GetRawText());
        Assert.DoesNotContain("NEVER_EXPOSE", result.GetRawText());
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("\"\"", false)]
    [InlineData("{}", true)]
    [InlineData("4", true)]
    public void Issue_body_shape_and_later_page_scope_remain_truthful(string bodyJson, bool partial)
    {
        var raw = JsonSerializer.SerializeToElement(new[] { new { id = 1, number = 7, title = "Small issue",
            body = JsonSerializer.Deserialize<JsonElement>(bodyJson) } });
        var result = ConnectorOperations.SafeResult(raw, "github_issues", Args(new { repository = "owner/repo", page = 2 }));
        var issue = result.GetProperty("data")[0];
        Assert.Equal(partial, issue.GetProperty("body_truncated").GetBoolean());
        if (partial) Assert.False(issue.TryGetProperty("body", out _));
        var coverage = result.GetProperty("coverage");
        Assert.True(result.GetProperty("truncated").GetBoolean()); // later page alone is not all pages
        Assert.Equal("page_series_terminal", coverage.GetProperty("status").GetString());
        Assert.False(coverage.GetProperty("complete").GetBoolean());
        Assert.False(coverage.GetProperty("seriesStart").GetBoolean());
        Assert.True(coverage.GetProperty("seriesEnd").GetBoolean());
        Assert.Equal("owner/repo", coverage.GetProperty("scopeArguments").GetProperty("repository").GetString());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("next").ValueKind);
    }

    [Theory]
    [InlineData("calendar_list", "{\"start\":\"2026-10-10T00:00:00+05:30\",\"end\":\"2026-10-11T00:00:00+05:30\",\"pageToken\":\"opaque+/=\"}", "pageToken=opaque%2B%2F%3D")]
    [InlineData("drive_search", "{\"query\":\"keyboard\",\"pageToken\":\"opaque+/=\"}", "pageToken=opaque%2B%2F%3D")]
    [InlineData("youtube_search", "{\"query\":\"keyboard\",\"pageToken\":\"opaque+/=\"}", "pageToken=opaque%2B%2F%3D")]
    [InlineData("github_repos", "{\"page\":2}", "page=2")]
    [InlineData("github_issues", "{\"repository\":\"owner/repo\",\"page\":2}", "page=2")]
    [InlineData("slack_search", "{\"query\":\"keyboard\",\"page\":2}", "page=2")]
    [InlineData("slack_history", "{\"channel\":\"C123\",\"cursor\":\"opaque+/=\"}", "cursor=opaque%2B%2F%3D")]
    [InlineData("discord_guilds", "{\"after\":\"12345678901234567\"}", "after=12345678901234567")]
    public void Every_supported_list_continues_through_the_same_fixed_get_endpoint(string capability, string json, string expected)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var plan = ConnectorOperations.Plan(capability, args, ConnectorIds.New());
        Assert.Contains(expected, plan.Endpoint); Assert.Equal("GET", plan.Method); Assert.Null(plan.Body); Assert.Null(plan.ReconcileEndpoint);
        Assert.Single(ConnectorRegistry.Definitions.SelectMany(d => d.Capabilities), c => c.Id == capability);
        var paging = args.Keys.Single(k => k is "pageToken" or "page" or "cursor" or "after");
        Assert.Contains(paging, ConnectorRegistry.Definitions.SelectMany(d => d.Capabilities).Single(c => c.Id == capability).Arguments.Keys);
    }

    [Fact]
    public void List_continuation_is_bounded_and_scope_is_preserved_separately_from_cursor()
    {
        foreach (var invalid in new[] { "{\"page\":0}", "{\"page\":1001}", "{\"page\":\"2\"}" })
            Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("github_repos", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(invalid)!, ConnectorIds.New()));
        Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("drive_search", Args(new { query = "x", pageToken = "\r\n" }), ConnectorIds.New()));
        Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("discord_guilds", Args(new { after = "untrusted-url" }), ConnectorIds.New()));
        var args = Args(new { query = "keyboard", pageToken = "current-page" });
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { files = Array.Empty<object>(), nextPageToken = "next-page" }), "drive_search", args);
        var coverage = result.GetProperty("coverage");
        Assert.Equal("next-page", coverage.GetProperty("next").GetProperty("pageToken").GetString());
        Assert.Equal("keyboard", coverage.GetProperty("scopeArguments").GetProperty("query").GetString());
        Assert.False(coverage.GetProperty("scopeArguments").TryGetProperty("pageToken", out _));
        var terminal = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { files = Array.Empty<object>() }), "drive_search", args);
        AssertPartial(terminal); Assert.True(terminal.GetProperty("coverage").GetProperty("seriesEnd").GetBoolean());
        Assert.False(terminal.GetProperty("coverage").GetProperty("seriesStart").GetBoolean());
        Assert.Equal(JsonValueKind.Null, terminal.GetProperty("coverage").GetProperty("next").ValueKind);
    }

    [Theory]
    [InlineData("paging", "pages")]
    [InlineData("pagination", "page_count")]
    public void Slack_wrong_page_is_unknown_not_an_end_of_series(string pagerName, string pagesName)
    {
        var response = JsonSerializer.SerializeToElement(new { messages = new Dictionary<string, object>
        {
            ["matches"] = Array.Empty<object>(),
            [pagerName] = new Dictionary<string, object> { ["page"] = 1, [pagesName] = 1 }
        } });
        var result = ConnectorOperations.SafeResult(response, "slack_search", Args(new { query = "same query", page = 2 }));
        AssertPartial(result);
        var coverage = result.GetProperty("coverage");
        Assert.Equal("coverage_unknown", coverage.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("hasMore").ValueKind);
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("next").ValueKind);
        Assert.False(coverage.GetProperty("seriesEnd").GetBoolean());
    }

    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    private static JsonElement Safe(object value, string capability) => ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(value), capability);
    private static void AssertPartial(JsonElement result)
    {
        Assert.True(result.GetProperty("truncated").GetBoolean()); Assert.False(result.GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.Contains("not an exhaustive", result.GetProperty("instruction").GetString());
    }
}
