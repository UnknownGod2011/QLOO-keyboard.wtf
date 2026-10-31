using System.Collections.Concurrent;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorMailSearchTests
{
    [Theory]
    [InlineData("\"project OR event\"")]
    [InlineData("\"project -event\"")]
    [InlineData("\"project\nevent\"")]
    [InlineData("\"project event\"", "next")]
    public void Topic_query_does_not_relax_operators_controls_or_existing_continuations(string query, string pageToken = "")
        => Assert.Equal(query, ConnectorMailQueries.SearchQuery(Args(new { query, matchMode = "topic", pageToken })));

    [Theory]
    [InlineData("topic", "\"project launch event\"", "project%20launch%20event")]
    [InlineData("exact", "\"project launch event\"", "%22project%20launch%20event%22")]
    [InlineData("topic", "from:team@example.test \"project launch\"", "from%3Ateam%40example.test%20%22project%20launch%22")]
    public async Task Inferred_whole_topic_phrase_is_not_an_accidental_exact_literal_search(string mode, string query, string encoded)
    {
        var args = Args(new { query, matchMode = mode, metadataLimit = 0 });
        var plan = ConnectorOperations.Plan("mail_search", args, ConnectorIds.New());
        Assert.Contains("q=" + encoded + "&", plan.Endpoint);
        var provider = new Provider((endpoint, ct) => Task.FromResult(Header(endpoint)));
        var raw = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(1), args, _ => Task.CompletedTask, default);
        var expected = mode == "topic" && query[0] == '"' ? query.Trim('"') : query;
        Assert.Equal(expected, raw.GetProperty("queryUsed").GetString());
        Assert.Empty(provider.Calls); // Normalize before dispatch, not another search/planner.
    }

    [Theory]
    [InlineData("message")]
    [InlineData("thread")]
    public async Task Requested_content_replaces_one_header_read_and_uses_existing_bounded_normalizer(string mode)
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(endpoint.Contains("format=full")
            ? mode == "thread" ? Thread() : Message("m0", "t0", "Full event details beyond a preview.")
            : Header(endpoint)));
        var raw = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(3),
            Args(new { query = "event", readMode = mode }), _ => Task.CompletedTask, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search"); var data = safe.GetProperty("data");
        Assert.Equal(3, provider.Calls.Count); // Replace, not add another model/provider round trip.
        Assert.Single(provider.Calls, value => value.Contains("format=full"));
        Assert.Equal(3, data.GetProperty("sampledMessages").GetArrayLength());
        Assert.Equal("Subject m0", data.GetProperty("sampledMessages")[0].GetProperty("subject").GetString());
        var content = data.GetProperty("sampledContent");
        Assert.Equal(mode, data.GetProperty("contentSample").GetProperty("mode").GetString());
        Assert.True(data.GetProperty("contentSample").GetProperty("complete").GetBoolean());
        Assert.Equal(mode == "thread" ? "mail_thread" : "mail_read", content.GetProperty("capability").GetString());
        Assert.Contains("Full event details beyond a preview.", content.GetRawText());
        Assert.DoesNotContain("access_token", safe.GetRawText()); Assert.DoesNotContain("PRIVATE_TOKEN", safe.GetRawText());
        Assert.False(safe.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData(2)]
    [InlineData(false)]
    public void Invalid_content_mode_fails_before_provider_dispatch(object mode) => Assert.Throws<ArgumentException>(() =>
        ConnectorOperations.Plan("mail_search", Args(new { query = "topic", readMode = mode }), ConnectorIds.New()));

    [Theory]
    [InlineData("message")]
    [InlineData("thread")]
    public async Task Content_opt_out_and_default_continuation_do_not_fetch_bodies(string mode)
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(Header(endpoint)));
        foreach (var args in new[] { Args(new { query = "topic", readMode = mode, metadataLimit = 0 }),
            Args(new { query = "topic", readMode = mode, pageToken = "next" }) })
        {
            var raw = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(3), args, _ => Task.CompletedTask, default);
            var data = ConnectorOperations.SafeResult(raw, "mail_search").GetProperty("data");
            Assert.False(data.TryGetProperty("sampledContent", out _));
        }
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("rate_limited")]
    public async Task Optional_content_failure_keeps_page_facts_without_false_full_read_evidence(string code)
    {
        var provider = new Provider((endpoint, ct) => endpoint.Contains("format=full")
            ? Task.FromException<JsonElement>(new ConnectorProviderException(code)) : Task.FromResult(Header(endpoint)));
        var raw = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(3),
            Args(new { query = "event", readMode = "message" }), _ => Task.CompletedTask, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search"); var data = safe.GetProperty("data");
        Assert.Equal(3, data.GetProperty("pageItemCount").GetInt32());
        Assert.False(data.TryGetProperty("sampledContent", out _));
        Assert.False(data.GetProperty("contentSample").GetProperty("complete").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean()); Assert.Equal(3, provider.Calls.Count);
    }

    [Theory]
    [InlineData("reconnect_required")]
    [InlineData("scope_required")]
    public async Task Content_authorization_error_cannot_be_hidden_as_preview_success(string code)
    {
        var provider = new Provider((endpoint, ct) => Task.FromException<JsonElement>(new ConnectorProviderException(code)));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => ConnectorMailQueries.SearchAsync(provider,
            "owned-account", Page(1), Args(new { query = "event", readMode = "message" }), _ => Task.CompletedTask, default));
        Assert.Equal(code, error.Code); Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData("wrong_parent")]
    [InlineData("wrong_message")]
    [InlineData("wrong_child_thread")]
    public async Task Thread_content_must_match_the_selected_message_and_its_thread(string mismatch)
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(Thread(mismatch)));
        var raw = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(1),
            Args(new { query = "event", readMode = "thread" }), _ => Task.CompletedTask, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        Assert.False(safe.GetProperty("data").TryGetProperty("sampledContent", out _));
        Assert.True(safe.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Oversized_thread_content_is_bounded_and_truthfully_partial_with_references_preserved()
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            id = "t0", messages = Enumerable.Range(0, 12).Select(index => Message("m" + index, "t0", new string('x', 20000)))
        })));
        var raw = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(1),
            Args(new { query = "event", readMode = "thread" }), _ => Task.CompletedTask, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.False(safe.GetProperty("data").GetProperty("contentSample").GetProperty("complete").GetBoolean());
        Assert.InRange(safe.GetRawText().Length, 1, 49000);
        Assert.Equal(10, safe.GetProperty("data").GetProperty("sampledContent").GetProperty("result")
            .GetProperty("data").GetProperty("messageReferences").GetArrayLength());
    }

    [Fact]
    public void Final_search_budget_loss_updates_previously_complete_thread_coverage()
    {
        var inner = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { id = "t0",
            messages = Enumerable.Range(0, 8).Select(i => Message(i == 0 ? "m0" : "body" + i, "t0", new string('x', 2800))) }), "mail_thread");
        Assert.False(inner.GetProperty("truncated").GetBoolean());
        var raw = JsonSerializer.SerializeToElement(new
        {
            messages = Page(8).GetProperty("messages"),
            sampledMessages = Enumerable.Range(0, 8).Select(i => new { id = "m" + i, threadId = "t" + i,
                from = "sender@example.test", subject = i == 0 ? "Subject m0" : new string('h', 500),
                snippet = i == 0 ? "Only a short preview." : new string('s', 500) }),
            metadataSample = new { requested = 8, returned = 8, complete = true },
            contentSample = new { mode = "thread", requested = 1, returned = 1, complete = true },
            sampledContent = new { capability = "mail_thread", messageId = "m0", threadId = "t0", result = inner }
        });
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.False(safe.GetProperty("data").GetProperty("contentSample").GetProperty("complete").GetBoolean());
        Assert.True(safe.GetProperty("data").GetProperty("sampledContent").GetProperty("result").GetProperty("truncated").GetBoolean());
        Assert.InRange(safe.GetRawText().Length, 1, 49000);
    }

    [Fact]
    public void Metadata_only_budget_loss_does_not_mark_unchanged_message_content_partial()
    {
        var inner = ConnectorOperations.SafeResult(Message("m0", "t0", "Complete requested message."), "mail_read");
        var safe = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
        {
            messages = Page(1).GetProperty("messages"),
            sampledMessages = new[] { new { id = "m0", snippet = new string('x', 20000) } },
            contentSample = new { mode = "message", requested = 1, returned = 1, complete = true },
            sampledContent = new { capability = "mail_read", messageId = "m0", threadId = "t0", result = inner }
        }), "mail_search");
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.True(safe.GetProperty("data").GetProperty("contentSample").GetProperty("complete").GetBoolean());
        Assert.False(safe.GetProperty("data").GetProperty("sampledContent").GetProperty("result").GetProperty("truncated").GetBoolean());
        Assert.Equal(inner.GetProperty("data").GetRawText(), safe.GetProperty("data").GetProperty("sampledContent").GetProperty("result").GetProperty("data").GetRawText());
    }

    [Fact]
    public void Content_coverage_survives_preview_budget_exhaustion_and_never_claims_missing_content_complete()
    {
        var inner = ConnectorOperations.SafeResult(Message("m0", "t0", "Requested message."), "mail_read");
        var safe = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
        {
            messages = Page(1).GetProperty("messages"),
            sampledMessages = Enumerable.Range(0, 100).Select(i => new { id = "m" + i, snippet = new string('x', 1000) }),
            contentSample = new { mode = "message", requested = 1, returned = 1, complete = true },
            sampledContent = new { capability = "mail_read", messageId = "m0", threadId = "t0", result = inner }
        }), "mail_search");
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.False(safe.GetProperty("data").GetProperty("contentSample").GetProperty("complete").GetBoolean());
        Assert.InRange(safe.GetRawText().Length, 1, 49000);
    }

    [Fact]
    public void Content_mode_survives_large_valid_identity_arrays_before_missing_body_is_marked_partial()
    {
        var inner = ConnectorOperations.SafeResult(Message("m0", "t0", "Requested message."), "mail_read");
        var safe = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new
        {
            messages = Enumerable.Range(0, 100).Select(i => new { id = new string('a', 253) + i.ToString("D3"), threadId = new string('b', 256) }),
            contentSample = new { mode = "message", requested = 1, returned = 1, complete = true },
            sampledContent = new { capability = "mail_read", messageId = "m0", threadId = "t0", result = inner }
        }), "mail_search");
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.Equal("message", safe.GetProperty("data").GetProperty("contentSample").GetProperty("mode").GetString());
        Assert.False(safe.GetProperty("data").GetProperty("contentSample").GetProperty("complete").GetBoolean());
        Assert.InRange(safe.GetRawText().Length, 1, 49000);
    }

    [Fact]
    public async Task Cancellation_discards_late_full_content_from_the_existing_owned_read()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var provider = new Provider(async (endpoint, ct) => { entered.SetResult(); return await late.Task; });
        var pending = ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(1),
            Args(new { query = "event", readMode = "message" }), _ => Task.CompletedTask, cancel.Token);
        await entered.Task; cancel.Cancel(); late.SetResult(Message("m0", "t0", "Late body"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData("from:team@example.test \"project launch\"", "from:team@example.test {project launch}")]
    [InlineData("after:2026/01/01 -subject:cancelled project launch", "after:2026/01/01 -subject:cancelled {project launch}")]
    [InlineData("from:\"Team Updates\" project launch", "from:\"Team Updates\" {project launch}")]
    [InlineData("subject:\"project launch\"", "{subject:project subject:launch}")]
    [InlineData("from:team@example.test after:2026/01/01 -subject:cancelled subject:\"project launch announcement\"", "from:team@example.test after:2026/01/01 -subject:cancelled {subject:project subject:launch subject:announcement}")]
    [InlineData("subject:\"项目 发布\"", "{subject:项目 subject:发布}")]
    [InlineData("subject:\"project PROJECT launch\"", "{subject:project subject:launch}")]
    [InlineData("subject:\"project launch\" release note", "{subject:project subject:launch} {release note}")]
    [InlineData("subject:\"project launch\" release", "{subject:project subject:launch} release")]
    [InlineData("subject:\"project launch\" subject:\"release note\"", "{subject:project subject:launch} {subject:release subject:note}")]
    [InlineData("-subject:\"project launch\"", null)]
    [InlineData("subject:\"project\"", null)]
    [InlineData("subject:\"project PROJECT\"", null)]
    [InlineData("subject:\"one two three four five six seven eight nine ten eleven twelve thirteen\"", null)]
    [InlineData("subject:\"project OR launch\"", null)]
    [InlineData("subject:\"project -launch\"", null)]
    [InlineData("subject:\"project from:other\"", null)]
    [InlineData("subject:\"project +launch\"", null)]
    [InlineData("subject:\"project\"launch", null)]
    [InlineData("(project OR launch)", null)]
    [InlineData("project OR launch", null)]
    [InlineData("{project launch}", null)]
    [InlineData("project", null)]
    public void Topic_refinement_is_generic_and_preserves_filters_and_explicit_boolean_semantics(string query, string? expected)
        => Assert.Equal(expected, ConnectorMailQueries.BroaderTopicQuery(query));

    [Theory]
    [InlineData(1, "", "topic")]
    [InlineData(0, "next", "topic")]
    [InlineData(0, "", "default")]
    public async Task Subject_refinement_never_replaces_existing_results_a_paging_window_or_default_exact_mode(int messages, string next, string mode)
    {
        var provider = new Provider((endpoint, ct) => throw new InvalidOperationException("No fallback read is allowed."));
        var options = mode == "default" ? Args(new { query = "subject:\"project launch\"", metadataLimit = 0 })
            : Args(new { query = "subject:\"project launch\"", matchMode = mode, metadataLimit = 0 });
        var result = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(messages, next),
            options, _ => Task.CompletedTask, default);
        Assert.Empty(provider.Calls);
        Assert.False(result.GetProperty("refinedEmptySearch").GetBoolean());
        Assert.Equal("subject:\"project launch\"", result.GetProperty("queryUsed").GetString());
    }

    [Fact]
    public async Task Still_empty_subject_refinement_is_one_read_not_a_loop_or_a_verified_category()
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(Page(0)));
        var result = await ConnectorMailQueries.SearchAsync(provider, "owned-account", Page(0),
            Args(new { query = "subject:\"project launch\"", matchMode = "topic", metadataLimit = 0 }),
            _ => Task.CompletedTask, default);
        Assert.Single(provider.Calls);
        Assert.True(result.GetProperty("refinedEmptySearch").GetBoolean());
        Assert.False(result.GetProperty("topicMatchVerified").GetBoolean());
        Assert.Empty(result.GetProperty("messages").EnumerateArray());
        Assert.False(result.TryGetProperty("matchedMessageCount", out _));
    }

    [Fact]
    public void Search_and_count_options_reject_invalid_values_before_dispatch()
    {
        Assert.Equal("exact", ConnectorMailQueries.MatchMode(Args(new { query = "topic" })));
        foreach (var value in new object[] { 0, 11, "3", 1.5 })
            Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("mail_count", Args(new { query = "topic", maxPages = value }), ConnectorIds.New()));
        foreach (var value in new object[] { "unknown", 1, false })
            Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("mail_search", Args(new { query = "topic", matchMode = value }), ConnectorIds.New()));
        Assert.Equal("GET", ConnectorOperations.Plan("mail_count", Args(new { query = "topic" }), ConnectorIds.New()).Method);
    }

    [Fact]
    public void Metadata_is_bounded_opt_out_and_off_on_continuation_pages()
    {
        Assert.Equal(6, ConnectorOperations.MailMetadataLimit(Args(new { query = "topic" })));
        Assert.Equal(0, ConnectorOperations.MailMetadataLimit(Args(new { query = "topic", pageToken = "next" })));
        Assert.Equal(8, ConnectorOperations.MailMetadataLimit(Args(new { query = "topic", metadataLimit = 8 })));
        foreach (var args in new[] { Args(new { query = "topic", metadataLimit = -1 }),
            Args(new { query = "topic", metadataLimit = 9 }), Args(new { query = "topic", metadataLimit = "6" }),
            Args(new { query = "topic", metadataLimit = 2.5 }) })
            Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("mail_search", args, ConnectorIds.New()));
    }

    [Fact]
    public async Task Header_sample_is_distributed_read_only_body_free_and_at_most_three_parallel()
    {
        var active = 0; var maximum = 0;
        var provider = new Provider(async (endpoint, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = maximum; if (observed >= current) break; }
            while (Interlocked.CompareExchange(ref maximum, current, observed) != observed);
            try { await Task.Delay(20, ct); return Header(endpoint); }
            finally { Interlocked.Decrement(ref active); }
        });
        var raw = await ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(12, "next"), 6, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        var data = safe.GetProperty("data");
        Assert.Equal(12, data.GetProperty("pageItemCount").GetInt32());
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.Equal("next", data.GetProperty("nextPageToken").GetString());
        Assert.Equal(new[] { "m0", "m2", "m4", "m6", "m8", "m11" }, data.GetProperty("sampledMessages")
            .EnumerateArray().Select(value => value.GetProperty("id").GetString()));
        Assert.Equal(6, provider.Calls.Count); Assert.Equal(3, maximum);
        Assert.All(provider.Calls, value =>
        {
            Assert.Contains("format=metadata", value);
            Assert.Contains("metadataHeaders=Subject", value);
            Assert.Contains("fields=id,threadId,internalDate,snippet,payload(headers)", value);
        });
        Assert.DoesNotContain("PRIVATE_BODY", safe.GetRawText());
        Assert.DoesNotContain("access_token", safe.GetRawText());
        Assert.DoesNotContain("resultSizeEstimate", safe.GetRawText());
        Assert.False(safe.GetProperty("truncated").GetBoolean());
        Assert.Equal("external_untrusted", safe.GetProperty("source").GetString());
        Assert.Equal("Subject m0", data.GetProperty("sampledMessages")[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Short_preview_reuses_the_header_request_and_is_bounded_not_a_full_body()
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            id = "m0", threadId = "t0", snippet = new string('x', 900),
            payload = new { body = new { data = "PRIVATE_FULL_BODY" }, headers = new[] { new { name = "Subject", value = "Launch" } } }
        })));
        var raw = await ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(1), 6, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        Assert.Single(provider.Calls);
        Assert.Equal(500, safe.GetProperty("data").GetProperty("sampledMessages")[0].GetProperty("snippet").GetString()!.Length);
        Assert.DoesNotContain("PRIVATE_FULL_BODY", safe.GetRawText());
        Assert.Contains("Read the relevant message/thread", safe.GetProperty("data").GetProperty("metadataSample").GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(8, 0)]
    public async Task Count_only_or_empty_search_does_not_fetch_metadata(int limit, int messages)
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(Header(endpoint)));
        var raw = await ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(messages), limit, default);
        var data = ConnectorOperations.SafeResult(raw, "mail_search").GetProperty("data");
        Assert.Empty(provider.Calls);
        Assert.Equal(messages, data.GetProperty("pageItemCount").GetInt32());
        Assert.Empty(data.GetProperty("sampledMessages").EnumerateArray());
        Assert.True(data.GetProperty("metadataSample").GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Partial_optional_metadata_preserves_real_page_facts_but_marks_partial_evidence()
    {
        var provider = new Provider((endpoint, ct) => endpoint.Contains("/m0?") ? Task.FromResult(Header(endpoint))
            : Task.FromException<JsonElement>(new ConnectorProviderException(endpoint.Contains("/m1?") ? "rate_limited" : "unavailable")));
        var raw = await ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(3), 6, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search"); var data = safe.GetProperty("data");
        Assert.Equal(3, data.GetProperty("pageItemCount").GetInt32());
        Assert.Single(data.GetProperty("sampledMessages").EnumerateArray());
        Assert.False(data.GetProperty("metadataSample").GetProperty("complete").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.Contains("not all matches", data.GetProperty("metadataSample").GetProperty("scope").GetString());
        Assert.Equal(3, provider.Calls.Count); // No automatic retries.
    }

    [Theory]
    [InlineData("reconnect_required")]
    [InlineData("scope_required")]
    public async Task Authorization_failure_is_not_hidden_as_successful_metadata(string code)
    {
        var provider = new Provider((endpoint, ct) => Task.FromException<JsonElement>(new ConnectorProviderException(code)));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() =>
            ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(1), 6, default));
        Assert.Equal(code, error.Code); Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Metadata_must_match_both_requested_message_and_thread(bool wrongMessage)
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new
            { id = wrongMessage ? "other" : "m0", threadId = wrongMessage ? "t0" : "other" })));
        var raw = await ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(1), 6, default);
        Assert.Empty(raw.GetProperty("sampledMessages").EnumerateArray());
        Assert.False(raw.GetProperty("metadataSample").GetProperty("complete").GetBoolean());
    }

    [Theory]
    [InlineData("../other", "t0")]
    [InlineData("https://other.test", "t0")]
    [InlineData("m0", "../other")]
    public async Task Invalid_resource_references_fail_before_metadata_dispatch(string id, string thread)
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(Header(endpoint)));
        var page = JsonSerializer.SerializeToElement(new { messages = new[] { new { id, threadId = thread } } });
        await Assert.ThrowsAsync<ConnectorProviderException>(() =>
            ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", page, 6, default));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Cancellation_stops_waiting_metadata_and_never_returns_late_sample()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var provider = new Provider(async (endpoint, ct) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Header(endpoint);
        });
        var pending = ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(8), 8, cancellation.Token);
        await entered.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.InRange(provider.Calls.Count, 1, 3);
    }

    [Fact]
    public async Task Oversized_provider_page_is_not_silently_counted_or_dispatched()
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(Header(endpoint)));
        await Assert.ThrowsAsync<ConnectorProviderException>(() =>
            ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(101), 6, default));
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [InlineData("{\"messages\":\"invalid\"}")]
    [InlineData("[]")]
    public async Task Malformed_provider_page_is_not_reported_as_zero_matches(string raw)
    {
        var provider = new Provider((endpoint, ct) => Task.FromResult(Header(endpoint)));
        await Assert.ThrowsAsync<ConnectorProviderException>(() => ConnectorOperations.MailSearchMetadataAsync(provider,
            "owned-account", JsonSerializer.Deserialize<JsonElement>(raw), 6, default));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Optional_metadata_deadline_cancels_slow_reads_but_preserves_the_search_page()
    {
        var cancelled = 0;
        var provider = new Provider(async (endpoint, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return Header(endpoint);
        });
        var raw = await ConnectorOperations.MailSearchMetadataAsync(provider, "owned-account", Page(6), 6, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        Assert.Equal(6, safe.GetProperty("data").GetProperty("pageItemCount").GetInt32());
        Assert.Empty(safe.GetProperty("data").GetProperty("sampledMessages").EnumerateArray());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.Equal(provider.Calls.Count, cancelled); Assert.InRange(cancelled, 1, 3);
    }

    private static Dictionary<string, JsonElement> Args(object value) => ConnectorPlatformTests.Args(value);
    private static JsonElement Message(string id, string thread, string text) => JsonSerializer.SerializeToElement(new
    {
        id, threadId = thread, access_token = "PRIVATE_TOKEN", snippet = "Only a short preview.",
        payload = new { mimeType = "text/plain", headers = new[] { new { name = "Subject", value = "Subject " + id },
            new { name = "From", value = "sender@example.test" } }, body = new { data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)) } }
    });
    private static JsonElement Thread(string mismatch = "") => JsonSerializer.SerializeToElement(new
    {
        id = mismatch == "wrong_parent" ? "other" : "t0", messages = new[]
        { Message(mismatch == "wrong_message" ? "other" : "m0", mismatch == "wrong_child_thread" ? "other" : "t0", "Full event details beyond a preview.") }
    });
    private static JsonElement Page(int count, string next = "") => JsonSerializer.SerializeToElement(new
    {
        messages = Enumerable.Range(0, count).Select(index => new { id = "m" + index, threadId = "t" + index }),
        nextPageToken = next, resultSizeEstimate = 9999,
        sampledMessages = new[] { new { id = "spoofed", subject = "not independently fetched" } }
    });
    private static JsonElement Header(string endpoint)
    {
        var id = new Uri(endpoint).AbsolutePath.Split('/').Last();
        return JsonSerializer.SerializeToElement(new { id, threadId = "t" + id[1..], access_token = "SECRET",
            payload = new { headers = new[] { new { name = "From", value = "sender@example.test" },
                new { name = "Subject", value = "Subject " + id }, new { name = "Date", value = "today" } },
                body = new { data = "PRIVATE_BODY" } } });
    }
    private sealed class Provider(Func<string, CancellationToken, Task<JsonElement>> request) : IConnectorProvider
    {
        internal ConcurrentBag<string> Calls { get; } = [];
        public Task<JsonElement> RequestAsync(string account, string endpoint, string method, object? body, CancellationToken ct)
        {
            Assert.Equal("owned-account", account); Assert.Equal("GET", method); Assert.Null(body);
            Calls.Add(endpoint); return request(endpoint, ct);
        }
        public Task<ProviderConnectReply> ConnectAsync(string user, string config, string callback, string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderConnection?> GetAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
