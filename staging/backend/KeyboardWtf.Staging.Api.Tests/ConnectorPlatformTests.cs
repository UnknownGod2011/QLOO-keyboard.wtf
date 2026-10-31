using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Connectors;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed partial class ConnectorPlatformTests
{
    [Fact]
    public async Task Requested_content_is_one_owned_verified_read_and_never_stored_in_operation_records()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read event email details"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++;
            return Task.FromResult(endpoint.Contains("format=full") ? JsonSerializer.SerializeToElement(new
            {
                id = "message1", threadId = "thread1", payload = new { mimeType = "text/plain",
                    body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("PRIVATE_FULL_CONTENT")) } }
            }) : JsonSerializer.SerializeToElement(new { messages = new[] { new { id = "message1", threadId = "thread1" } } }));
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search",
            Args(new { query = "event", readMode = "message" })), default);
        Assert.True(reply.Verified); Assert.Equal("completed", reply.State);
        Assert.Contains("PRIVATE_FULL_CONTENT", reply.Result!.Value.GetRawText());
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(2, calls); Assert.Equal(2, operation.ProviderRequestCount);
        Assert.DoesNotContain("PRIVATE_FULL_CONTENT", JsonSerializer.Serialize(operation));
        Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Remote_cancellation_before_content_enrichment_prevents_any_full_body_dispatch()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read email details"), default);
        var calls = 0;
        f.Provider.RequestOverride = async (endpoint, ct) =>
        {
            calls++; await f.Tasks.CancelAsync(f.Caller, task.TaskId, ct);
            return JsonSerializer.SerializeToElement(new { messages = new[] { new { id = "message1", threadId = "thread1" } } });
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search",
            Args(new { query = "event", readMode = "message" })), default);
        Assert.False(reply.Verified); Assert.Equal("cancelled", reply.State); Assert.Null(reply.Result);
        Assert.Equal(1, calls); Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Topic_search_refines_one_empty_inferred_phrase_inside_the_existing_read()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find project launch announcement emails"), default);
        var calls = new List<string>();
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls.Add(endpoint);
            return Task.FromResult(JsonSerializer.SerializeToElement(calls.Count == 1
                ? new { messages = Array.Empty<object>() }
                : new { messages = new object[] { new { id = "message1", threadId = "thread1" } } }));
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search",
            Args(new { query = "from:team@example.test \"project launch announcement\"", matchMode = "topic", metadataLimit = 0 })), default);
        Assert.True(reply.Verified); Assert.Equal(2, calls.Count);
        Assert.Contains("from%3Ateam%40example.test", calls[1]);
        var data = reply.Result!.Value.GetProperty("data");
        Assert.True(data.GetProperty("refinedEmptySearch").GetBoolean());
        Assert.Equal(1, data.GetProperty("pageItemCount").GetInt32());
        Assert.Equal("from:team@example.test {project launch announcement}", data.GetProperty("queryUsed").GetString());
        Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(2, (await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default))[0].ProviderRequestCount);
        Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Empty_inferred_subject_search_refines_once_keeps_filters_and_is_not_a_verified_topic_count()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find project launch notification emails"), default);
        var calls = new List<string>();
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls.Add(endpoint);
            return Task.FromResult(JsonSerializer.SerializeToElement(calls.Count == 1
                ? new { messages = Array.Empty<object>() }
                : new { messages = new object[] { new { id = "message1", threadId = "thread1" } } }));
        };
        const string query = "from:team@example.test after:2026/01/01 -subject:cancelled subject:\"project launch notification\"";
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search",
            Args(new { query, matchMode = "topic", metadataLimit = 0 })), default);
        Assert.True(reply.Verified); Assert.Equal(2, calls.Count);
        Assert.Contains("from%3Ateam%40example.test", calls[1]);
        Assert.Contains("after%3A2026%2F01%2F01", calls[1]);
        Assert.Contains("-subject%3Acancelled", calls[1]);
        var data = reply.Result!.Value.GetProperty("data");
        Assert.True(data.GetProperty("refinedEmptySearch").GetBoolean());
        Assert.False(data.GetProperty("topicMatchVerified").GetBoolean());
        Assert.False(data.TryGetProperty("matchedMessageCount", out _));
        Assert.Equal("from:team@example.test after:2026/01/01 -subject:cancelled {subject:project subject:launch subject:notification}", data.GetProperty("queryUsed").GetString());
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(2, operation.ProviderRequestCount); Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Cancellation_after_first_empty_subject_page_prevents_the_refinement_read()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find project launch notifications"), default);
        var calls = 0;
        f.Provider.RequestOverride = async (endpoint, ct) =>
        {
            calls++; await f.Tasks.CancelAsync(f.Caller, task.TaskId, ct);
            return JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() });
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search",
            Args(new { query = "subject:\"project launch\"", matchMode = "topic", metadataLimit = 0 })), default);
        Assert.False(reply.Verified); Assert.Equal("cancelled", reply.State); Assert.Null(reply.Result);
        Assert.Equal(1, calls); Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Large_count_pages_are_deduplicated_compact_and_not_a_provider_estimate()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count all matching notification emails"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            var index = calls++;
            Assert.Contains("maxResults=500", endpoint);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                messages = Enumerable.Range(index * 499, index == 3 ? 153 : 500)
                    .Select(i => new { id = "message" + i, threadId = "thread" + i }),
                nextPageToken = index == 3 ? "" : "page" + (index + 1), resultSizeEstimate = 999999
            }));
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_count", Args(new { query = "from:team@example.test" })), default);
        var safe = reply.Result!.Value; var data = safe.GetProperty("data");
        Assert.True(reply.Verified); Assert.True(data.GetProperty("complete").GetBoolean());
        Assert.Equal(1650, data.GetProperty("matchedMessageCount").GetInt32());
        Assert.Equal(4, data.GetProperty("pagesRead").GetInt32());
        Assert.False(safe.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("resultSizeEstimate", safe.GetRawText());
        Assert.True(safe.GetRawText().Length < 3000);
        Assert.Equal(4, calls); Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public void Oversized_email_body_is_never_reported_as_complete_empty_text()
    {
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
        {
            id = "message1", threadId = "thread1", payload = new
            {
                mimeType = "text/plain", body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 180000))) }
            }
        }), "mail_read");
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.NotEmpty(result.GetProperty("data").GetProperty("text").GetString()!);
    }

    [Theory]
    [InlineData("exact", "", "from:team@example.test \"project launch\"")]
    [InlineData("topic", "next", "from:team@example.test \"project launch\"")]
    [InlineData("exact", "", "from:team@example.test subject:\"project launch\"")]
    [InlineData("topic", "next", "from:team@example.test subject:\"project launch\"")]
    public async Task Exact_or_continued_empty_search_never_changes_its_query(string mode, string pageToken, string query)
    {
        var f = Fixture(); await Connect(f); var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) => { calls++; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); };
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Search for an exact phrase"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search",
            Args(new { query, matchMode = mode, pageToken })), default);
        Assert.True(reply.Verified); Assert.Equal(1, calls);
        var data = reply.Result!.Value.GetProperty("data");
        Assert.False(data.GetProperty("refinedEmptySearch").GetBoolean());
        Assert.Equal(query, data.GetProperty("queryUsed").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task Count_window_bound_is_truthful_and_has_a_continuation_not_a_false_total(int pages)
    {
        var f = Fixture(); await Connect(f); var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            messages = Enumerable.Range(calls++ * 500, 500).Select(i => new { id = "m" + i, threadId = "t" + i }),
            nextPageToken = "page" + calls
        }));
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching mail"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_count", Args(new { query = "topic", maxPages = pages })), default);
        var safe = reply.Result!.Value; var data = safe.GetProperty("data");
        Assert.Equal(pages, calls); Assert.Equal(pages * 500, data.GetProperty("matchedMessageCount").GetInt32());
        Assert.False(data.GetProperty("complete").GetBoolean()); Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.Equal("page_window_limit", data.GetProperty("stoppedBecause").GetString());
        Assert.NotEmpty(data.GetProperty("nextPageToken").GetString()!);
        Assert.Equal("ready", (await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).State);
    }

    [Theory]
    [InlineData("rate_limited")]
    [InlineData("unavailable")]
    public async Task Count_partial_provider_failure_never_retries_or_claims_a_complete_total(string code)
    {
        var f = Fixture(); await Connect(f); var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) => ++calls == 1
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { messages = new[] { new { id = "m1", threadId = "t1" } }, nextPageToken = "next" }))
            : Task.FromException<JsonElement>(new ConnectorProviderException(code));
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching mail"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        var safe = reply.Result!.Value; var data = safe.GetProperty("data");
        Assert.True(safe.GetProperty("truncated").GetBoolean()); Assert.False(data.GetProperty("complete").GetBoolean());
        Assert.Equal(1, data.GetProperty("matchedMessageCount").GetInt32());
        Assert.Equal(code, data.GetProperty("stoppedBecause").GetString());
        Assert.Equal(2, calls); Assert.Equal(0, f.Provider.Writes);
    }

    [Theory]
    [InlineData("reconnect_required")]
    [InlineData("scope_required")]
    public async Task Count_authorization_failure_is_not_hidden_as_a_partial_success(string code)
    {
        var f = Fixture(); await Connect(f); var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) => ++calls == 1
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { nextPageToken = "next" }))
            : Task.FromException<JsonElement>(new ConnectorProviderException(code));
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching mail"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        Assert.False(reply.Verified); Assert.Equal("failed", reply.State);
        Assert.Null(reply.Result); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("mail_count")]
    [InlineData("mail_search")]
    public async Task Remote_cancel_between_pages_or_refinement_stops_additional_provider_work(string capability)
    {
        var f = Fixture(); await Connect(f); var calls = 0;
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find topic mail"), default);
        f.Provider.RequestOverride = async (endpoint, ct) =>
        {
            calls++; await f.Tasks.CancelAsync(f.Caller, task.TaskId, ct);
            return JsonSerializer.SerializeToElement(new { nextPageToken = capability == "mail_count" ? "next" : "" });
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, capability,
            capability == "mail_count" ? Args(new { query = "topic" }) : Args(new { query = "inferred topic", matchMode = "topic" })), default);
        Assert.Equal("cancelled", reply.State); Assert.False(reply.Verified); Assert.Null(reply.Result);
        Assert.Equal(1, calls); Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Count_pagination_cycle_and_provider_extra_content_are_not_complete_evidence()
    {
        var f = Fixture(); await Connect(f); var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                messages = new[] { new { id = "m1", threadId = "t1", text = "PRIVATE_BODY", access_token = "SECRET" } }, nextPageToken = "same"
            }));
        };
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count topic mail"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        var safe = reply.Result!.Value; var data = safe.GetProperty("data");
        Assert.Equal(2, calls); Assert.Equal(1, data.GetProperty("matchedMessageCount").GetInt32());
        Assert.False(data.GetProperty("complete").GetBoolean()); Assert.Equal("pagination_cycle", data.GetProperty("stoppedBecause").GetString());
        Assert.DoesNotContain("PRIVATE_BODY", safe.GetRawText()); Assert.DoesNotContain("SECRET", safe.GetRawText());
    }

    [Fact]
    public async Task A_count_starting_from_a_cursor_does_not_claim_to_be_the_whole_mailbox_query_total()
    {
        var f = Fixture(); await Connect(f);
        f.Provider.RequestOverride = (endpoint, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new
            { messages = new[] { new { id = "m1", threadId = "t1" } } }));
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching mail"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_count", Args(new { query = "topic", pageToken = "next" })), default);
        var data = reply.Result!.Value.GetProperty("data");
        Assert.False(data.GetProperty("complete").GetBoolean()); Assert.True(data.GetProperty("windowComplete").GetBoolean());
        Assert.False(data.GetProperty("startedFromBeginning").GetBoolean());
        Assert.True(reply.Result.Value.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("{\"messages\":[{\"id\":\"../other\"}]}")]
    [InlineData("{\"messages\":\"not-a-page\"}")]
    [InlineData("{\"nextPageToken\":12}")]
    public async Task Malformed_count_pages_never_become_verified_empty_counts(string raw)
    {
        var f = Fixture(); await Connect(f);
        f.Provider.RequestOverride = (endpoint, ct) => Task.FromResult(JsonSerializer.Deserialize<JsonElement>(raw));
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching mail"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        Assert.False(reply.Verified); Assert.Equal("failed", reply.State); Assert.Null(reply.Result);
    }

    [Fact]
    public async Task Remote_cancellation_after_search_prevents_metadata_dispatch_and_result_publication()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find mail about an event"), default);
        var calls = 0;
        f.Provider.RequestOverride = async (endpoint, ct) =>
        {
            calls++; await f.Tasks.CancelAsync(f.Caller, task.TaskId, ct);
            return JsonSerializer.SerializeToElement(new { messages = new[] { new { id = "message1", threadId = "thread1" } } });
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search", Args(new { query = "event" })), default);
        Assert.Equal("cancelled", reply.State); Assert.Null(reply.Result); Assert.False(reply.Verified);
        Assert.Equal(1, calls); Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Search_metadata_uses_the_same_owned_operation_and_counts_each_read_without_storing_content()
    {
        var f = Fixture(); await Connect(f);
        f.Provider.RequestOverride = (endpoint, ct) => Task.FromResult(JsonSerializer.SerializeToElement(endpoint.Contains("?format=metadata")
            ? new { id = "message1", threadId = "thread1", payload = new { headers = new[]
                { new { name = "Subject", value = "PRIVATE_SUBJECT" } } } } as object
            : new { messages = new[] { new { id = "message1", threadId = "thread1" } } }));
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find an email about my event"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search", Args(new { query = "event" })), default);
        Assert.Equal("completed", reply.State); Assert.True(reply.Verified);
        Assert.Equal("PRIVATE_SUBJECT", reply.Result!.Value.GetProperty("data").GetProperty("sampledMessages")[0].GetProperty("subject").GetString());
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(2, operation.ProviderRequestCount); Assert.Null(operation.ResourceId);
        Assert.DoesNotContain("PRIVATE_SUBJECT", JsonSerializer.Serialize(operation));
        Assert.Equal(0, f.Provider.Writes);
        Assert.Equal(4, task.Capabilities.Count);
    }

    [Fact]
    public async Task Empty_search_guidance_is_query_scoped_and_does_not_retry_or_change_evidence()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find mail about an event"), default);
        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_search", Args(new { query = "an inferred exact phrase" })), default);
        Assert.Equal("completed", reply.State); Assert.True(reply.Verified);
        Assert.Equal(0, reply.Result!.Value.GetProperty("data").GetProperty("pageItemCount").GetInt32());
        Assert.Contains("broaden", reply.Message); Assert.Contains("exact query the user explicitly supplied", reply.Message);
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(1, operation.ProviderRequestCount);
        Assert.False(reply.Result.Value.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Gmail_search_exposes_page_facts_not_a_provider_estimate_as_total_evidence()
    {
        var plan = ConnectorOperations.Plan("mail_search", new Dictionary<string, JsonElement>
            { ["query"] = JsonSerializer.SerializeToElement("from:example.test") }, ConnectorIds.New());
        Assert.Contains("maxResults=50", plan.Endpoint);
        var data = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
        {
            messages = new[] { new { id = "first" }, new { id = "second" } },
            nextPageToken = "next-page", resultSizeEstimate = 201
        }), "mail_search").GetProperty("data");
        Assert.Equal(2, data.GetProperty("pageItemCount").GetInt32());
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.Equal("next-page", data.GetProperty("nextPageToken").GetString());
        Assert.False(data.TryGetProperty("resultSizeEstimate", out _));
        var empty = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { resultSizeEstimate = 0 }), "mail_search").GetProperty("data");
        Assert.Equal(0, empty.GetProperty("pageItemCount").GetInt32());
        Assert.False(empty.GetProperty("hasMore").GetBoolean());
        Assert.False(empty.TryGetProperty("nextPageToken", out _));
    }

    [Fact]
    public async Task Correlation_is_diagnostic_only_and_telemetry_has_hashes_not_request_content()
    {
        var log = new MetadataLogger(); var f = Fixture(log); var account = await Connect(f);
        var caller = f.Caller with { SessionId = "test-auth-session-private" };
        var live = ConnectorIds.New();
        var task = await f.Tasks.ResolveAsync(caller, new("gmail", "Read private invoices", account, live), default);
        var stored = await f.Store.GetTaskAsync(caller.UserId, task.TaskId, default);
        Assert.Equal(live, stored.LogicalLiveSessionId);
        Assert.Equal(ConnectorTelemetry.AuthHash(caller.SessionId), stored.AuthSessionHash);
        var result = await f.Tasks.ExecuteAsync(caller,
            new(task.TaskId, "mail_search", Args(new { query = "private-query" }), false, ConnectorIds.New()), default);
        Assert.Equal("completed", result.State);
        var operation = Assert.Single(await f.Store.OperationsAsync(caller.UserId, task.TaskId, default));
        Assert.Equal(live, operation.LogicalLiveSessionId); Assert.Equal(stored.AuthSessionHash, operation.AuthSessionHash);
        Assert.Equal(1, operation.ProviderRequestCount); Assert.True(operation.ElapsedMilliseconds >= 0);
        var settled = Assert.Single(log.Entries, entry => Equals(entry["Phase"], "settled"));
        Assert.Equal(live, settled["LogicalLiveClientCorrelation"]);
        Assert.Equal(ConnectorTelemetry.UserHash(caller.UserId), settled["UidHash"]);
        Assert.Equal("googlesuper", settled["Toolkit"]); Assert.Equal(true, settled["CorrelationMismatch"]);
        Assert.Equal(1, settled["ProviderActionRequestAttempts"]);
        var serialized = JsonSerializer.Serialize(log.Entries);
        foreach (var privateValue in new[] { caller.UserId, caller.Email, caller.SessionId!, "private invoices", "private-query", "test@example.com" })
            Assert.DoesNotContain(privateValue, serialized);
        Assert.DoesNotContain("usageRequestId", serialized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("model-invented")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public async Task Missing_or_malformed_live_metadata_does_not_fabricate_a_session_or_block_work(string? live)
    {
        var f = Fixture(); var account = await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read recent mail", account, live), default);
        var stored = await f.Store.GetTaskAsync(f.Caller.UserId, task.TaskId, default);
        Assert.Null(stored.LogicalLiveSessionId); Assert.Null(stored.AuthSessionHash);
        Assert.Equal("completed", (await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_search", Args(new { query = "inbox" })), default)).State);
    }

    [Fact]
    public async Task Rebind_retains_origin_correlation_and_each_real_verification_call_is_counted_once()
    {
        var f = Fixture(); var account = await Connect(f);
        var caller = f.Caller with { SessionId = "test-auth-origin" }; var live = ConnectorIds.New();
        var task = await f.Tasks.ResolveAsync(caller,
            new("googlecalendar", "Create a calendar event", account, live), default);
        var second = new StoredConnectorAccount(ConnectorIds.New(), "google", "ca_other", "Second", "connected", false, DateTimeOffset.UtcNow);
        f.Store.SeedAccount(caller.UserId, second);
        var changed = await f.Tasks.ChangeAccountAsync(caller with { SessionId = "test-new-auth-session" }, task.TaskId, second.Id, default);
        var rebound = await f.Store.GetTaskAsync(caller.UserId, changed.TaskId, default);
        Assert.Equal(live, rebound.LogicalLiveSessionId); Assert.Equal(ConnectorTelemetry.AuthHash(caller.SessionId), rebound.AuthSessionHash);
        var args = Args(new { title = "Test", start = "2026-10-10T09:00:00Z", end = "2026-10-10T10:00:00Z" });
        var result = await f.Tasks.ExecuteAsync(caller, new(changed.TaskId, "calendar_create", args, true, live), default);
        Assert.Equal("completed", result.State); Assert.Equal(1, f.Provider.Writes);
        var operation = Assert.Single(await f.Store.OperationsAsync(caller.UserId, changed.TaskId, default));
        Assert.Equal(2, operation.ProviderRequestCount); Assert.Null(operation.ErrorCode);
        Assert.Equal(live, operation.LogicalLiveSessionId); Assert.Equal(rebound.AuthSessionHash, operation.AuthSessionHash);
        await f.Tasks.ExecuteAsync(caller, new(changed.TaskId, "calendar_create", args, true, live), default);
        Assert.Equal(1, f.Provider.Writes);
        Assert.Equal(2, Assert.Single(await f.Store.OperationsAsync(caller.UserId, changed.TaskId, default)).ProviderRequestCount);
    }

    [Fact]
    public async Task Local_failure_is_normalized_and_does_not_log_raw_error_payload_or_fake_extra_calls()
    {
        var log = new MetadataLogger(); var f = Fixture(log); var account = await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read mail", account), default);
        f.Provider.RequestException = new InvalidOperationException("DO_NOT_LEAK_PROVIDER_BODY_OR_SECRET");
        var reply = await f.Tasks.ExecuteAsync(f.Caller, new(task.TaskId, "mail_search", Args(new { query = "inbox" })), default);
        Assert.Equal("failed", reply.State);
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal("local_failure", operation.ErrorCode); Assert.Equal(1, operation.ProviderRequestCount);
        Assert.DoesNotContain("DO_NOT_LEAK", JsonSerializer.Serialize(log.Entries));
        Assert.DoesNotContain("DO_NOT_LEAK", JsonSerializer.Serialize(operation));
    }

    [Fact]
    public void Disabled_configuration_requires_no_vendor_secret_and_production_rejects_staging()
    {
        Assert.True(new ConnectorOptions().Validate(new PaidSystemOptions()));
        var value = Settings();
        Assert.Throws<InvalidOperationException>(() => value.Validate(new PaidSystemOptions
            { DeploymentEnvironment = PaidDeploymentEnvironment.Production }));
        Assert.True(value.Validate(new PaidSystemOptions { DeploymentEnvironment = PaidDeploymentEnvironment.Staging }));
    }

    [Fact]
    public void Registry_has_unique_small_capabilities_and_one_google_authorization_family()
    {
        var registry = new ConnectorRegistry(Options.Create(Settings()));
        Assert.Equal(12, registry.Catalog().Count);
        Assert.Equal("google", registry.Get("gmail").Family);
        Assert.Equal("google", registry.Get("googledocs").Family);
        Assert.Equal("google", registry.Get("googledrive").Family);
        Assert.Equal("google", registry.Get("googlecalendar").Family);
        Assert.All(ConnectorRegistry.Definitions, value => Assert.InRange(value.Capabilities.Length, 0, 8));
        Assert.All(ConnectorRegistry.Definitions, value => Assert.InRange(value.Capabilities.Count(cap => !cap.Writes), 0, 8));
        Assert.All(registry.Catalog().Where(value => value.Id is "reddit" or "linkedin" or "googlemeet"),
            value => Assert.Equal("coming_soon", value.Availability));
        Assert.DoesNotContain(registry.Get("discord").Capabilities, value => value.Writes);
    }

    [Theory]
    [InlineData("../../other-user")]
    [InlineData("ac_secret-provider-id")]
    [InlineData("user@example.com")]
    [InlineData("")]
    public void Opaque_references_reject_paths_provider_ids_and_email(string id) =>
        Assert.Throws<ArgumentException>(() => ConnectorIds.RequireId(id));

    [Fact]
    public void Customer_capability_help_is_projected_from_the_executable_registry_without_internal_schemas()
    {
        var registry = new ConnectorRegistry(Options.Create(Settings()));
        foreach (var item in registry.Catalog())
        {
            var definition = registry.Get(item.Id);
            Assert.Equal(definition.Capabilities.Select(cap => cap.Help), item.Capabilities);
            Assert.All(item.Capabilities, help => Assert.False(string.IsNullOrWhiteSpace(help)));
            var json = JsonSerializer.Serialize(item);
            Assert.DoesNotContain("composio", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arguments", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mail_send", json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(registry.Catalog().Single(item => item.Id == "gmail").Capabilities,
            help => help.Contains("attachment", StringComparison.OrdinalIgnoreCase) || help.Contains("draft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Executable_registry_projects_the_shared_read_evidence_contract()
    {
        foreach (var definition in ConnectorRegistry.Definitions)
            foreach (var capability in definition.Capabilities)
            {
                Assert.Equal(KeyboardWtf.Connectors.ConnectorEvidenceTraits.For(capability.Id), capability.EvidenceKind);
                if (capability.Writes || capability.Id == "mail_count") Assert.Equal("unknown", capability.EvidenceKind);
                else Assert.Contains(capability.EvidenceKind, new[] { "search", "content", "metadata" });
            }
        Assert.Equal("metadata", ConnectorRegistry.Definitions.Single(value => value.Id == "googledrive").Capabilities.Single(value => value.Id == "drive_read").EvidenceKind);
    }

    [Fact]
    public void Provider_user_is_stable_environment_bound_and_not_an_email()
    {
        var staging = ConnectorIds.ProviderUser("staging", "firebase-uid");
        Assert.Equal(staging, ConnectorIds.ProviderUser("staging", "firebase-uid"));
        Assert.NotEqual(staging, ConnectorIds.ProviderUser("production", "firebase-uid"));
        Assert.DoesNotContain("firebase-uid", staging);
    }

    [Fact]
    public async Task Double_connect_claims_one_provider_create_and_does_not_claim_connected_from_redirect()
    {
        var f = Fixture();
        var request = new ConnectRequest("gmail", ConnectorIds.New());
        var replies = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Connections.ConnectAsync(f.Caller, request, default)));
        Assert.Equal(1, f.Provider.Creates);
        Assert.Single(replies.Select(value => value.AttemptId).Distinct());
        Assert.DoesNotContain(replies, value => value.State == "connected");
        Assert.Empty(await f.Store.AccountsAsync(f.Caller.UserId, default));
        Assert.DoesNotContain("firebase-uid", f.Provider.LastCallback);
        Assert.Contains("/connectors?attempt=", f.Provider.LastCallback);
    }

    [Fact]
    public async Task OAuth_return_is_UID_bound_state_verified_and_replay_harmless()
    {
        var f = Fixture();
        var reply = await f.Connections.ConnectAsync(f.Caller, new("gmail", ConnectorIds.New()), default);
        f.Provider.Status = "ACTIVE";
        var state = Uri.UnescapeDataString(f.Provider.LastCallback.Split("&state=")[1]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Connections.AttemptAsync(f.Caller, reply.AttemptId, "bad-state", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Connections.AttemptAsync(f.Caller with { UserId = "other-user" }, reply.AttemptId, state, default));
        var completed = await f.Connections.AttemptAsync(f.Caller, reply.AttemptId, state, default);
        Assert.Equal("connected", completed.State);
        Assert.Equal(completed, await f.Connections.AttemptAsync(f.Caller, reply.AttemptId, state, default));
        Assert.Single(await f.Store.AccountsAsync(f.Caller.UserId, default));
        Assert.Equal(1, f.Provider.Creates);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("config")]
    [InlineData("toolkit")]
    public async Task Forged_or_cross_environment_provider_binding_cannot_be_connected(string mismatch)
    {
        var f = Fixture();
        var reply = await f.Connections.ConnectAsync(f.Caller, new("gmail", ConnectorIds.New()), default);
        f.Provider.Status = "ACTIVE";
        f.Provider.Mismatch = mismatch;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Connections.AttemptAsync(f.Caller, reply.AttemptId, null, default));
        Assert.Empty(await f.Store.AccountsAsync(f.Caller.UserId, default));
    }

    [Fact]
    public async Task Lost_connect_ack_is_reconciled_by_alias_without_repeating_POST()
    {
        var f = Fixture();
        f.Provider.LoseCreateAck = true;
        var reply = await f.Connections.ConnectAsync(f.Caller, new("gmail", ConnectorIds.New()), default);
        Assert.Equal("outcome_unknown", reply.State);
        f.Provider.Status = "ACTIVE";
        var reconciled = await f.Connections.AttemptAsync(f.Caller, reply.AttemptId, null, default);
        Assert.Equal("connected", reconciled.State);
        Assert.Equal(1, f.Provider.Creates);
    }

    [Theory]
    [InlineData("EXPIRED", "expired")]
    [InlineData("REVOKED", "revoked")]
    [InlineData("FAILED", "reconnect_required")]
    public async Task Management_status_comes_from_provider_not_cached_connected(string status, string expected)
    {
        var f = Fixture(); await Connect(f);
        f.Provider.Status = status;
        var snapshot = await f.Connections.SnapshotAsync(f.Caller, default);
        Assert.Equal(expected, Assert.Single(snapshot.Accounts).State);
    }

    [Fact]
    public async Task Provider_outage_is_unavailable_not_stale_green_check()
    {
        var f = Fixture(); await Connect(f); f.Provider.Outage = true;
        var result = await f.Connections.SnapshotAsync(f.Caller, default);
        Assert.Equal("unavailable", Assert.Single(result.Accounts).State);
        Assert.Null(result.Accounts[0].VerifiedAt);
    }

    [Fact]
    public async Task Late_snapshot_does_not_recreate_a_cleaned_account()
    {
        var f = Fixture(); var id = await Connect(f);
        f.Provider.BeforeGet = () => { f.Store.ForgetAccount(f.Caller.UserId, id); return Task.CompletedTask; };
        var snapshot = await f.Connections.SnapshotAsync(f.Caller, default);
        Assert.Empty(snapshot.Accounts); Assert.Empty(await f.Store.AccountsAsync(f.Caller.UserId, default));
    }

    [Fact]
    public async Task Snapshot_returns_current_default_after_an_overlapping_default_change()
    {
        var f = Fixture(); var a = await Connect(f); var b = ConnectorIds.New();
        f.Store.SeedAccount(f.Caller.UserId, new(b, "google", "ca_other", "Second account", "connected", false, DateTimeOffset.UtcNow));
        await f.Store.SetDefaultAsync(f.Caller.UserId, a, default);
        f.Provider.BeforeGet = () => f.Store.SetDefaultAsync(f.Caller.UserId, b, default);
        var snapshot = await f.Connections.SnapshotAsync(f.Caller, default);
        Assert.Equal(b, Assert.Single(snapshot.Accounts, value => value.IsDefault).AccountId);
    }

    [Fact]
    public async Task Disconnect_removes_provider_account_and_blocks_future_execution()
    {
        var f = Fixture(); var id = await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Find recent mail", id), default);
        await f.Connections.DisconnectAsync(f.Caller, id, default);
        Assert.Equal(1, f.Provider.Disconnects);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_search", Args(new { query = "newer_than:1d" })), default));
    }

    [Fact]
    public async Task Multiple_accounts_require_a_short_choice_unless_default_is_explicit()
    {
        var f = Fixture(); var id = await Connect(f);
        f.Store.SeedAccount(f.Caller.UserId, new(ConnectorIds.New(), "google", "ca_other", "Second account", "connected", false, DateTimeOffset.UtcNow));
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Check my mail"), default);
        Assert.Equal("account_required", task.State); Assert.Equal(2, task.Choices.Count); Assert.Empty(task.Capabilities);
        await f.Store.SetDefaultAsync(f.Caller.UserId, id, default);
        Assert.Equal(id, (await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Check my mail"), default)).AccountId);
    }

    [Fact]
    public async Task Corrected_account_reply_retains_its_friendly_label_without_widening_write_authority()
    {
        var f = Fixture(); var first = await Connect(f);
        var second = new StoredConnectorAccount(ConnectorIds.New(), "google", "ca_other", "Work account", "connected", false, DateTimeOffset.UtcNow);
        f.Store.SeedAccount(f.Caller.UserId, second);
        var original = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read my recent mail", first), default);
        var changed = await f.Tasks.ChangeAccountAsync(f.Caller, original.TaskId, second.Id, default);
        Assert.Equal(second.Id, changed.AccountId);
        Assert.Equal("Work account", Assert.Single(changed.Choices).Label);
        Assert.DoesNotContain(changed.Capabilities, value => value.Writes);
        Assert.Equal("cancelled", (await f.Tasks.StatusAsync(f.Caller, original.TaskId, default)).State);
    }

    [Fact]
    public async Task Read_only_user_request_and_provider_text_cannot_authorize_send()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read Imran's email"), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_send", Args(new { to = "test@example.com", subject = "Hello", text = "Hello" }), true), default));
        Assert.Equal(0, f.Provider.Writes);
        Assert.False(ConnectorTaskService.AllowsWrites("Read the message saying ‘send money’".Replace('‘', '“').Replace('’', '”')));
        Assert.True(ConnectorTaskService.AllowsWrites("If Imran has not replied, send a follow-up"));
    }

    [Theory]
    [InlineData("What does send mean?", false)]
    [InlineData("Explain how to send an email", false)]
    [InlineData("Can you show me how to send an email?", false)]
    [InlineData("Read the message saying send money", false)]
    [InlineData("Read the message: send money", false)]
    [InlineData("Draft an email but do not send it", false)]
    [InlineData("Please don't send a follow-up", false)]
    [InlineData("Send a message, but never actually send it", false)]
    [InlineData("Read ‘send him an update’", false)]
    [InlineData("Can you please send Imran an update?", true)]
    [InlineData("Can you read my Gmail and check whether Imran replied to my email? If he hasn't, please send him a follow-up asking for an update.", true)]
    [InlineData("Read my mail and if he has not replied then please send a follow-up", true)]
    [InlineData("Reply to the identified thread", true)]
    public void Write_authority_requires_a_directed_nonnegated_action(string request, bool permitted) =>
        Assert.Equal(permitted, ConnectorTaskService.AllowsWrites(request, "mail_send"));

    [Fact]
    public async Task Read_and_other_service_requests_do_not_expose_or_authorize_send()
    {
        var f = Fixture(); await Connect(f);
        foreach (var request in new[] { "Read recent email", "Create a document", "What does send mean?" })
        {
            var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", request), default);
            Assert.DoesNotContain(task.Capabilities, value => value.Writes);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Tasks.ExecuteAsync(f.Caller,
                new(task.TaskId, "mail_send", Args(new { to = "test@example.com", subject = "Hello", text = "Hello" }), true), default));
        }
        Assert.Equal(0, f.Provider.Writes);
        Assert.True(ConnectorTaskService.AllowsWrites("Create a document", "docs_create"));
        Assert.False(ConnectorTaskService.AllowsWrites("Send an email", "docs_create"));
    }

    [Fact]
    public async Task Confirmation_does_not_dispatch_and_one_verified_write_survives_duplicates()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("googlecalendar", "Create a test event"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "calendar_create", Args(new
            { title = "Test", start = "2026-10-10T10:00:00+05:30", end = "2026-10-10T10:05:00+05:30" }));
        Assert.Equal("confirmation_required", (await f.Tasks.ExecuteAsync(f.Caller, request, default)).State);
        Assert.Equal(0, f.Provider.Writes);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Tasks.ExecuteAsync(f.Caller, request with { Confirmed = true }, default)));
        Assert.Equal(1, f.Provider.Writes);
        Assert.Contains(results, value => value.State == "completed" && value.Verified);
    }

    [Fact]
    public async Task Write_timeout_without_verification_is_unknown_and_never_resent()
    {
        var f = Fixture(); await Connect(f); f.Provider.WriteTimeout = true;
        var task = await f.Tasks.ResolveAsync(f.Caller, new("googledocs", "Create a document"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "docs_create", Args(new { title = "Test" }), true);
        Assert.Equal("outcome_unknown", (await f.Tasks.ExecuteAsync(f.Caller, request, default)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller, request, default));
        var status = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
        Assert.Equal("failed", status.State);
        Assert.Equal("outcome_unknown", Assert.Single(status.Operations).State);
        Assert.Equal(1, f.Provider.Writes);
    }

    [Theory]
    [InlineData("{\"messages\":{}}")]
    [InlineData("[]")]
    [InlineData("{\"messages\":[null]}")]
    public async Task Malformed_send_reconciliation_settles_unknown_without_replay(string response)
    {
        var f = Fixture(); await Connect(f); f.Provider.WriteTimeout = true;
        f.Provider.ReconcileResponse = JsonSerializer.Deserialize<JsonElement>(response);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Send a test message"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "mail_send",
            Args(new { to = "test@example.com", subject = "Test", text = "Test" }), true);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.Equal("outcome_unknown", reply.State); Assert.False(reply.Verified);
        var status = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
        Assert.Equal("outcome_unknown", Assert.Single(status.Operations).State);
        Assert.Equal("failed", status.State); Assert.Equal(1, f.Provider.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller, request, default));
        Assert.Equal(1, f.Provider.Writes);
    }

    [Fact]
    public async Task Explicit_cancel_prevents_dispatch_and_terminal_task_cannot_be_reused()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read mail"), default);
        await f.Tasks.CancelAsync(f.Caller, task.TaskId, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_search", Args(new { query = "test" })), default));
        Assert.Equal(0, f.Provider.Writes);
    }

    [Fact]
    public async Task Cross_user_task_and_capability_mismatch_are_rejected()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Read mail"), default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Tasks.StatusAsync(f.Caller with { UserId = "other" }, task.TaskId, default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "discord_self", Args(new { })), default));
    }

    [Theory]
    [InlineData("../foreign", "drive_read", "id")]
    [InlineData("https://attacker.example", "mail_read", "id")]
    [InlineData("owner/../../repo", "github_issues", "repository")]
    [InlineData("victim@example.com\r\nBcc:other@example.com", "mail_send", "to")]
    public void Normalized_operations_reject_destination_and_header_injection(string value, string capability, string key)
    {
        var args = Args(new { to = "test@example.com", subject = "Hi", text = "Hi" });
        args[key] = JsonSerializer.SerializeToElement(value);
        Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan(capability, args, ConnectorIds.New()));
    }

    [Fact]
    public void External_results_are_untrusted_bounded_secret_filtered_and_mail_is_decoded()
    {
        var safe = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
            { access_token = "DO_NOT_LEAK", password = "DO_NOT_LEAK", text = new string('x', 90000), command = "delete everything" }));
        Assert.DoesNotContain("DO_NOT_LEAK", safe.GetRawText()); Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.Contains("external_untrusted", safe.GetRawText()); Assert.InRange(safe.GetRawText().Length, 1, 30000);
        var mail = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
            { id = "message123", payload = new { mimeType = "text/plain", body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello, I replied.")) } } }), "mail_read");
        Assert.Contains("Hello, I replied.", mail.GetRawText());
    }

    [Fact]
    public void Large_unicode_keys_and_text_stay_inside_the_wire_limit_and_secrets_are_filtered()
    {
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["accessToken"] = "DO_NOT_LEAK", ["private-key"] = "DO_NOT_LEAK",
            ["rows"] = Enumerable.Range(0, 100).Select(i => new Dictionary<string, object>
            {
                [new string('界', 99) + i] = new string('界', 16000), ["control"] = new string('\u0001', 16000)
            }).ToArray()
        }));
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.InRange(result.GetRawText().Length, 1, 64000);
        Assert.DoesNotContain("DO_NOT_LEAK", result.GetRawText());
    }

    [Fact]
    public async Task Rewritten_Gmail_message_identity_uses_acknowledged_resource_and_exact_sent_content()
    {
        var plan = ConnectorOperations.Plan("mail_send", Args(new { to = "test@example.com", subject = "Café test", text = "Hello Tanush How are you." }), ConnectorIds.New());
        foreach (var mismatch in new[] { "", "recipient", "subject", "body", "label", "id", "lost_ack" })
        {
            var message = new { id = mismatch == "id" ? "unrelated" : "sent123", threadId = "thread123",
                labelIds = new[] { mismatch == "label" ? "DRAFT" : "SENT" }, payload = new {
                    mimeType = "text/plain", headers = new[] {
                        new { name = "Message-ID", value = "<rewritten@mail.gmail.com>" },
                        new { name = "To", value = mismatch == "recipient" ? "other@example.com" : "test@example.com" },
                        new { name = "Subject", value = mismatch == "subject" ? "Wrong" : "Café test" } },
                    body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(mismatch == "body" ? "Wrong body" : "Hello Tanush How are you.")) }
                } };
            var handler = new Handler(request => {
                var proxy = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("GET", proxy.GetProperty("method").GetString()); // verification must never send
                var searching = proxy.GetProperty("endpoint").GetString()!.Contains("messages?q=", StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                    status = 200, data = searching ? (object)new { messages = new[] { new { id = "sent123" } } } : message }) };
            });
            var adapter = new ComposioConnectorProvider(new HttpClient(handler), Options.Create(Settings()));
            var result = await ConnectorOperations.VerifyWriteAsync(adapter, "ca_test123", "mail_send", ConnectorIds.New(), plan,
                mismatch == "lost_ack" ? null : JsonSerializer.SerializeToElement(new { id = "sent123" }), default);
            Assert.Equal(mismatch == "", result is not null);
            Assert.Equal(mismatch == "lost_ack" ? 2 : 1, handler.Calls);
        }
    }

    [Fact]
    public void Mail_search_paginates_without_changing_query_or_treating_estimate_as_total()
    {
        var plan = ConnectorOperations.Plan("mail_search", Args(new { query = "from:devpost.com subject:woo", limit = 100, pageToken = "opaque+/=123" }), ConnectorIds.New());
        Assert.Contains("maxResults=100", plan.Endpoint);
        Assert.Contains("pageToken=opaque%2B%2F%3D123", plan.Endpoint);
        Assert.Contains("q=from%3Adevpost.com%20subject%3Awoo", plan.Endpoint);
        Assert.Equal("GET", plan.Method);
        foreach (var args in new[] { Args(new { query = "x", limit = 101 }), Args(new { query = "x", limit = "10" }), Args(new { query = "x", pageToken = "bad\r\n" }) })
            Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("mail_search", args, ConnectorIds.New()));
        var search = new ConnectorRegistry(Options.Create(Settings())).Get("gmail").Capabilities.Single(c => c.Id == "mail_search");
        Assert.Contains("pageToken", search.Arguments.Keys);
        Assert.Contains("not an exact count", search.Description);
        Assert.Contains("Broaden an empty descriptive search", search.Description);
        Assert.Contains("Inspect sample subjects/bodies", search.Description);
    }

    [Fact]
    public void Mime_alternatives_do_not_duplicate_text_or_starve_latest_thread_reply()
    {
        object Part(string mime, string text) => new { mimeType = mime, body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) } };
        var mail = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { id = "one", payload = new {
            mimeType = "multipart/alternative", parts = new[] { Part("text/plain", "Real message"), Part("text/html", new string('x', 60000)) } } }), "mail_read");
        Assert.False(mail.GetProperty("truncated").GetBoolean());
        Assert.Equal("Real message", mail.GetProperty("data").GetProperty("text").GetString());
        var thread = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { id = "thread", messages = new[] {
            new { id = "old", threadId = "thread", payload = Part("text/plain", new string('o', 50000)) },
            new { id = "new", threadId = "thread", payload = Part("text/plain", "Latest answer: transfer complete") }
        } }), "mail_thread");
        Assert.Equal("newest_first", thread.GetProperty("data").GetProperty("order").GetString());
        Assert.Equal("new", thread.GetProperty("data").GetProperty("messages")[0].GetProperty("id").GetString());
        Assert.Contains("Latest answer: transfer complete", thread.GetRawText());
        Assert.Equal(2, thread.GetProperty("data").GetProperty("messageReferences").GetArrayLength());
        Assert.True(thread.GetProperty("truncated").GetBoolean());
        var htmlOnly = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { payload = Part("text/html", "<p>Only body</p>") }), "mail_read");
        Assert.Contains("Only body", htmlOnly.GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public void Thread_read_uses_existing_account_and_retains_recent_message_identity_with_truthful_bounds()
    {
        var plan = ConnectorOperations.Plan("mail_thread", Args(new { id = "thread123" }), ConnectorIds.New());
        Assert.Equal("GET", plan.Method); Assert.Contains("/threads/thread123?format=full", plan.Endpoint);
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
        {
            id = "thread123", messages = Enumerable.Range(0, 15).Select(i => new
            {
                id = "message" + i, threadId = "thread123", payload = new { mimeType = "text/plain",
                    body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("Reply number " + i)) } }
            }).ToArray()
        }), "mail_thread");
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(10, result.GetProperty("data").GetProperty("messages").GetArrayLength());
        Assert.Contains("Reply number 14", result.GetRawText()); Assert.DoesNotContain("Reply number 0", result.GetRawText());
    }

    [Fact]
    public async Task Expired_OAuth_reference_survives_until_provider_cleanup_without_creating_an_account()
    {
        var f = Fixture();
        var reply = await f.Connections.ConnectAsync(f.Caller, new("gmail", ConnectorIds.New()), default);
        var attempt = await f.Store.GetAttemptAsync(f.Caller.UserId, reply.AttemptId, default);
        await f.Store.SaveAttemptAsync(f.Caller.UserId, attempt with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, default);
        f.Provider.Status = "ACTIVE";
        await f.Connections.CleanupExpiredAttemptAsync(f.Caller.UserId, reply.AttemptId, default);
        var cleaned = await f.Store.GetAttemptAsync(f.Caller.UserId, reply.AttemptId, default);
        Assert.Equal("expired", cleaned.State); Assert.False(cleaned.CleanupRequired);
        Assert.Equal(1, f.Provider.Disconnects); Assert.Empty(await f.Store.AccountsAsync(f.Caller.UserId, default));
        await f.Connections.CleanupExpiredAttemptAsync(f.Caller.UserId, reply.AttemptId, default);
        Assert.Equal(1, f.Provider.Disconnects);
    }

    [Fact]
    public async Task Cancelled_OAuth_is_removed_and_a_late_callback_cannot_adopt_it()
    {
        var f = Fixture(); var reply = await f.Connections.ConnectAsync(f.Caller, new("gmail", ConnectorIds.New()), default);
        await f.Connections.CancelAttemptAsync(f.Caller, reply.AttemptId, default);
        Assert.Equal(1, f.Provider.Disconnects);
        var attempt = await f.Connections.AttemptAsync(f.Caller, reply.AttemptId, null, default);
        Assert.Equal("cancelled", attempt.State); Assert.Empty(await f.Store.AccountsAsync(f.Caller.UserId, default));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task Unsupported_upstream_revocation_still_verifies_credential_removal(int revokeStatus)
    {
        var handler = new Handler(request => new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("/revoke")
            ? (HttpStatusCode)revokeStatus : request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.NoContent));
        var adapter = new ComposioConnectorProvider(new HttpClient(handler), Options.Create(Settings()));
        await adapter.DisconnectAsync("ca_test123", default);
        Assert.Equal(3, handler.Calls); // revoke, delete, independently verify absent
    }

    [Fact]
    public async Task Missing_revoke_route_cannot_claim_disconnect_while_the_credential_still_exists()
    {
        var handler = new Handler(request => new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("/revoke")
            ? HttpStatusCode.NotFound : request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK)
        { Content = new StringContent("{\"id\":\"ca_test123\",\"status\":\"ACTIVE\"}") });
        var adapter = new ComposioConnectorProvider(new HttpClient(handler), Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.DisconnectAsync("ca_test123", default));
        Assert.True(error.OutcomeUnknown); Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("GET", "unavailable", false)]
    [InlineData("POST", "outcome_unknown", true)]
    public async Task Adapter_deadline_covers_a_stalled_body_after_fast_headers(string method, string code, bool unknown)
    {
        using var body = new StalledBody();
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var adapter = new ComposioConnectorProvider(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) }, Options.Create(Settings()));
        var request = adapter.RequestAsync("ca_test123", "https://api.github.com/user", method, null, default);
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(code, error.Code); Assert.Equal(unknown, error.OutcomeUnknown);
        Assert.True(body.ReadCancelled); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(429, "rate_limited")]
    [InlineData(500, "outcome_unknown")]
    [InlineData(403, "unavailable")]
    public async Task Adapter_normalizes_errors_without_payload_keys_or_automatic_write_retry(int status, string code)
    {
        var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("raw-secret-provider-payload") });
        var adapter = new ComposioConnectorProvider(new HttpClient(handler), Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/repos/test/repo/issues", "POST", new { title = "test" }, default));
        Assert.Equal(code, error.Code); Assert.DoesNotContain("raw-secret", error.ToString()); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Adapter_explicitly_pins_account_and_uses_current_version_without_credentials_in_body()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("https://backend.composio.dev/api/v3.1/tools/execute/proxy", request.RequestUri!.AbsoluteUri);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("ca_test123", body); Assert.DoesNotContain("unit-test-only-key", body);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":200,\"data\":{\"login\":\"test\"},\"headers\":{\"authorization\":\"DO_NOT_LEAK\"}}") };
        });
        var adapter = new ComposioConnectorProvider(new HttpClient(handler), Options.Create(Settings()));
        Assert.DoesNotContain("DO_NOT_LEAK", (await adapter.RequestAsync("ca_test123", "https://api.github.com/user", "GET", null, default)).GetRawText());
    }

    [Fact]
    public async Task Adapter_rejects_a_different_connection_identity_even_with_a_valid_response_shape()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"id\":\"ca_other123\",\"status\":\"ACTIVE\"}") });
        var adapter = new ComposioConnectorProvider(new HttpClient(handler), Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.GetAsync("ca_test123", default));
        Assert.Equal("unavailable", error.Code); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Account_deletion_removes_provider_connections_and_does_not_call_AI_or_payments()
    {
        var f = Fixture(); await Connect(f); f.Store.Active = false;
        await f.Connections.CleanupDeletedUserAsync(f.Caller.UserId, default);
        Assert.Equal(1, f.Provider.Disconnects);
        Assert.Empty(await f.Store.AccountsAsync(f.Caller.UserId, default));
    }

    private static ConnectorOptions Settings() => new() { Enabled = true, Environment = "staging", ApiKey = "unit-test-only-key",
        WebBaseUrl = "https://keyboard-wtf-staging.example", AuthConfigIds = new() { ["google"] = "ac_testgoogle", ["github"] = "ac_testgithub", ["discord"] = "ac_testdiscord" } };
    internal static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
    private static TestFixture Fixture(ILogger<ConnectorTaskService>? logger = null)
    {
        var options = Options.Create(Settings()); var store = new MemoryStore(); var provider = new FakeProvider(); var registry = new ConnectorRegistry(options);
        var connections = new ConnectorConnectionService(store, provider, registry, new SystemClock(), options);
        var tasks = new ConnectorTaskService(store, provider, connections, registry, new SystemClock(), logger ?? NullLogger<ConnectorTaskService>.Instance);
        return new(store, provider, connections, tasks, new("firebase-uid", "test@example.com", "Test", "firebase"));
    }
    private static async Task<string> Connect(TestFixture f)
    {
        var reply = await f.Connections.ConnectAsync(f.Caller, new("gmail", ConnectorIds.New()), default);
        f.Provider.Status = "ACTIVE";
        return (await f.Connections.AttemptAsync(f.Caller, reply.AttemptId, null, default)).AccountId!;
    }
    private sealed record TestFixture(MemoryStore Store, FakeProvider Provider, ConnectorConnectionService Connections,
        ConnectorTaskService Tasks, CallerIdentity Caller);
    private sealed class MetadataLogger : ILogger<ConnectorTaskService>
    {
        internal readonly List<Dictionary<string, object?>> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> fields) Entries.Add(fields.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> run) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(run(request)); }
    }
    private sealed class StalledBody : Stream
    {
        internal bool ReadCancelled;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { ReadCancelled = true; throw; }
            return 0;
        }
    }
    private sealed class FakeProvider : IConnectorProvider
    {
        internal int Creates, Writes, Disconnects;
        internal string Status = "INITIALIZING", Mismatch = "", LastCallback = "";
        internal bool LoseCreateAck, WriteTimeout, Outage;
        internal Func<Task>? BeforeGet;
        internal JsonElement? ReconcileResponse;
        internal Exception? RequestException;
        internal Func<string, CancellationToken, Task<JsonElement>>? RequestOverride;
        private string _user = "", _config = "", _alias = "";
        private JsonElement _event;
        internal bool CalendarContentMismatch;
        private bool _deleted;
        public Task<ProviderConnectReply> ConnectAsync(string user, string config, string callback, string alias, CancellationToken ct)
        {
            Creates++; _user = user; _config = config; _alias = alias; LastCallback = callback;
            return LoseCreateAck ? Task.FromException<ProviderConnectReply>(new ConnectorProviderException("outcome_unknown", true))
                : Task.FromResult(new ProviderConnectReply("ca_test123", "https://accounts.google.com/oauth?state=test"));
        }
        private ProviderConnection Connection() => new("ca_test123", Mismatch == "owner" ? "other-user" : _user,
            Mismatch == "config" ? "ac_otherproject" : _config, Mismatch == "toolkit" ? "github" : "googlesuper", Status, false, _alias);
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(string user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderConnection>>(_deleted || _user.Length == 0 ? [] : [Connection()]);
        public async Task<ProviderConnection?> GetAsync(string id, CancellationToken ct)
        {
            if (BeforeGet is { } hook) { BeforeGet = null; await hook(); }
            if (Outage) throw new ConnectorProviderException("unavailable");
            return _deleted ? null : Connection() with { Id = id };
        }
        public Task DisconnectAsync(string id, CancellationToken ct) { Disconnects++; _deleted = true; return Task.CompletedTask; }
        public Task<JsonElement> RequestAsync(string id, string endpoint, string method, object? body, CancellationToken ct)
        {
            if (RequestException is not null) throw RequestException;
            if (RequestOverride is not null) return RequestOverride(endpoint, ct);
            if (method == "POST")
            {
                Writes++;
                if (WriteTimeout) throw new ConnectorProviderException("outcome_unknown", true);
                if (endpoint.Contains("calendar", StringComparison.Ordinal)) _event = JsonSerializer.SerializeToElement(body);
            }
            if (ReconcileResponse is { } reconciliation && endpoint.Contains("rfc822msgid", StringComparison.Ordinal))
                return Task.FromResult(reconciliation);
            if (endpoint.Contains("calendar", StringComparison.Ordinal))
            {
                var result = _event.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone());
                result["status"] = "confirmed";
                if (CalendarContentMismatch) result["summary"] = "Different event content";
                return Task.FromResult(JsonSerializer.SerializeToElement(result));
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { email = "test@example.com" }));
        }
    }

    internal sealed class MemoryStore : IConnectorStore
    {
        private readonly object _sync = new();
        internal bool Active = true;
        private readonly Dictionary<string, ConnectorAttempt> _attempts = new();
        private readonly Dictionary<string, StoredConnectorAccount> _accounts = new();
        private readonly Dictionary<string, StoredConnectorTask> _tasks = new();
        private readonly Dictionary<string, StoredConnectorOperation> _operations = new();
        private static string Key(string uid, string id) => uid + ":" + id;
        internal void SeedAccount(string uid, StoredConnectorAccount item) => _accounts[Key(uid, item.Id)] = item;
        internal void ForgetAccount(string uid, string id) => _accounts.Remove(Key(uid, id));
        public Task EnsureActiveAsync(string uid, CancellationToken ct) => Active ? Task.CompletedTask : throw new InvalidOperationException("Account inactive.");
        public Task<ConnectorClaim<ConnectorAttempt>> ClaimAttemptAsync(string uid, ConnectorAttempt item, DateTimeOffset now, CancellationToken ct)
        {
            lock (_sync)
            {
                var old = _attempts.Where(pair => pair.Key.StartsWith(uid + ":", StringComparison.Ordinal)).Select(pair => pair.Value)
                    .FirstOrDefault(value => value.Family == item.Family && (value.RequestId == item.RequestId
                        || value.ExpiresAt > now && value.State is "creating" or "connecting" or "outcome_unknown"));
                if (old is not null) return Task.FromResult(new ConnectorClaim<ConnectorAttempt>(old, false));
                _attempts[Key(uid, item.Id)] = item; return Task.FromResult(new ConnectorClaim<ConnectorAttempt>(item, true));
            }
        }
        public Task<ConnectorAttempt> GetAttemptAsync(string uid, string id, CancellationToken ct) =>
            Task.FromResult(_attempts.TryGetValue(Key(uid, id), out var value) ? value : throw new KeyNotFoundException());
        public Task SaveAttemptAsync(string uid, ConnectorAttempt item, CancellationToken ct) { _attempts[Key(uid, item.Id)] = item; return Task.CompletedTask; }
        public Task<bool> CompleteAttemptAsync(string uid, ConnectorAttempt attempt, StoredConnectorAccount account, DateTimeOffset now, CancellationToken ct)
        {
            lock (_sync)
            {
                var current = _attempts[Key(uid, attempt.Id)];
                if (!Active || current.ExpiresAt <= now || current.State is "expired" or "cancelled" or "failed") return Task.FromResult(false);
                _accounts[Key(uid, account.Id)] = account;
                _attempts[Key(uid, attempt.Id)] = attempt with { State = "connected", AccountId = account.Id, RedirectUrl = null, CleanupRequired = false };
                return Task.FromResult(true);
            }
        }
        public Task<IReadOnlyList<ConnectorAttempt>> AttemptsAsync(string uid, CancellationToken ct) => Task.FromResult<IReadOnlyList<ConnectorAttempt>>(
            _attempts.Where(pair => pair.Key.StartsWith(uid + ":", StringComparison.Ordinal)).Select(pair => pair.Value).ToArray());
        public Task<IReadOnlyList<StoredConnectorAccount>> AccountsAsync(string uid, CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredConnectorAccount>>(
            _accounts.Where(pair => pair.Key.StartsWith(uid + ":", StringComparison.Ordinal) && pair.Value.State != "disconnected").Select(pair => pair.Value).ToArray());
        public Task SaveAccountAsync(string uid, StoredConnectorAccount item, CancellationToken ct)
        {
            if (!_accounts.TryGetValue(Key(uid, item.Id), out var previous)) throw new KeyNotFoundException();
            if (previous.State is "disconnecting" or "disconnected") return Task.CompletedTask;
            _accounts[Key(uid, item.Id)] = item with { IsDefault = item.State == "disconnecting" ? item.IsDefault : previous.IsDefault };
            return Task.CompletedTask;
        }
        public Task RemoveAccountAsync(string uid, string id, CancellationToken ct) { _accounts[Key(uid, id)] = _accounts[Key(uid, id)] with { State = "disconnected", IsDefault = false }; return Task.CompletedTask; }
        public Task SetDefaultAsync(string uid, string id, CancellationToken ct)
        {
            foreach (var pair in _accounts.Where(pair => pair.Key.StartsWith(uid + ":", StringComparison.Ordinal)).ToArray())
                _accounts[pair.Key] = pair.Value with { IsDefault = pair.Value.Id == id };
            return Task.CompletedTask;
        }
        public Task CreateTaskAsync(string uid, StoredConnectorTask item, CancellationToken ct) { _tasks[Key(uid, item.Id)] = item; return Task.CompletedTask; }
        public Task<bool> RebindTaskAsync(string uid, string previousId, StoredConnectorTask replacement, IReadOnlyList<string> writeCapabilities, DateTimeOffset now, CancellationToken ct)
        {
            lock (_sync)
            {
                var old = _tasks[Key(uid, previousId)];
                if (old.State is not ("ready" or "account_required") || old.ExpiresAt <= now
                    || _operations.Values.Any(value => value.TaskId == previousId && writeCapabilities.Contains(value.CapabilityId))) return Task.FromResult(false);
                _tasks[Key(uid, previousId)] = old with { State = "cancelled" }; _tasks[Key(uid, replacement.Id)] = replacement;
                return Task.FromResult(true);
            }
        }
        public Task<StoredConnectorTask> GetTaskAsync(string uid, string id, CancellationToken ct) =>
            Task.FromResult(_tasks.TryGetValue(Key(uid, id), out var value) ? value : throw new KeyNotFoundException());
        public Task CancelTaskAsync(string uid, string id, CancellationToken ct) { _tasks[Key(uid, id)] = _tasks[Key(uid, id)] with { State = "cancelled" }; return Task.CompletedTask; }
        public Task FinishTaskAsync(string uid, string id, CancellationToken ct) { _tasks[Key(uid, id)] = _tasks[Key(uid, id)] with { State = "completed" }; return Task.CompletedTask; }
        public Task EndTaskAsync(string uid, string id, string state, CancellationToken ct) { if (_tasks[Key(uid, id)].State is "ready" or "account_required") _tasks[Key(uid, id)] = _tasks[Key(uid, id)] with { State = state }; return Task.CompletedTask; }
        public Task<IReadOnlyList<StoredConnectorOperation>> OperationsAsync(string uid, string taskId, CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredConnectorOperation>>(
            _operations.Where(pair => pair.Key.StartsWith(uid + ":", StringComparison.Ordinal) && pair.Value.TaskId == taskId).Select(pair => pair.Value).ToArray());
        public Task<ConnectorClaim<StoredConnectorOperation>> ClaimOperationAsync(string uid, StoredConnectorOperation item, DateTimeOffset now, CancellationToken ct)
        {
            lock (_sync)
            {
                if (_operations.TryGetValue(Key(uid, item.Id), out var value)) return Task.FromResult(new ConnectorClaim<StoredConnectorOperation>(value, false));
                _operations[Key(uid, item.Id)] = item; return Task.FromResult(new ConnectorClaim<StoredConnectorOperation>(item, true));
            }
        }
        public Task SaveOperationAsync(string uid, StoredConnectorOperation item, CancellationToken ct) { _operations[Key(uid, item.Id)] = item; return Task.CompletedTask; }
        public Task PurgeAsync(string uid, CancellationToken ct)
        {
            foreach (var key in _accounts.Keys.Where(key => key.StartsWith(uid + ":", StringComparison.Ordinal)).ToArray()) _accounts.Remove(key);
            return Task.CompletedTask;
        }
    }
}
