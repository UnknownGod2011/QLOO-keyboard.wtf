using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorReplyAndMetadataEdgeTests
{
    [Theory]
    [InlineData("duplicate_parent")]
    [InlineData("duplicate_references")]
    [InlineData("duplicate_to")]
    [InlineData("duplicate_subject")]
    [InlineData("duplicate_message_id")]
    [InlineData("headers_object")]
    [InlineData("header_entry_number")]
    [InlineData("header_value_number")]
    [InlineData("message_id_malformed")]
    [InlineData("message_id_large_malformed")]
    [InlineData("actual_thread_number")]
    [InlineData("invalid_reference_token")]
    [InlineData("reference_substring_only")]
    [InlineData("expected_thread_number")]
    [InlineData("expected_raw_invalid")]
    [InlineData("expected_parent_invalid")]
    [InlineData("expected_parent_duplicate")]
    public async Task Ambiguous_or_malformed_required_reply_evidence_returns_null_without_throwing(string scenario)
    {
        var operation = ConnectorIds.New();
        var plan = ConnectorOperations.Plan("mail_send", Args(new
        {
            to = "reader@example.test", subject = "Reply verification fixture", text = "Fixture reply.",
            threadId = "expected_thread", inReplyTo = "<parent@example.test>"
        }), operation);
        if (scenario.StartsWith("expected_", StringComparison.Ordinal))
        {
            var body = JsonSerializer.SerializeToElement(plan.Body).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
            if (scenario == "expected_thread_number") body["threadId"] = 7;
            else if (scenario == "expected_raw_invalid") body["raw"] = "not-base64!!!";
            else
            {
                var encoded = JsonSerializer.SerializeToElement(body).GetProperty("raw").GetString()!;
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/')
                    .PadRight((encoded.Length + 3) / 4 * 4, '=')));
                raw = raw.Replace("In-Reply-To: <parent@example.test>\r\n", scenario == "expected_parent_invalid"
                    ? "In-Reply-To: not-an-identity\r\n"
                    : "In-Reply-To: <parent@example.test>\r\nIn-Reply-To: <parent@example.test>\r\n", StringComparison.Ordinal);
                body["raw"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            plan = plan with { Body = body };
        }
        var headers = new List<object>
        {
            Header("Message-ID", "<kwt." + operation + "@keyboard.wtf>"),
            Header("To", "reader@example.test"), Header("Subject", "Reply verification fixture"),
            Header("In-Reply-To", "<parent@example.test>"),
            Header("References", scenario == "invalid_reference_token" ? "not-an-identity <parent@example.test>"
                : scenario == "reference_substring_only" ? "<prefix-parent@example.test>" : "<ancestor@example.test> <parent@example.test>")
        };
        if (scenario == "duplicate_parent") headers.Add(Header("in-reply-to", "<parent@example.test>"));
        if (scenario == "duplicate_references") headers.Add(Header("references", "<parent@example.test>"));
        if (scenario == "duplicate_to") headers.Add(Header("to", "reader@example.test"));
        if (scenario == "duplicate_subject") headers.Add(Header("subject", "Reply verification fixture"));
        if (scenario == "duplicate_message_id") headers.Add(Header("message-id", "<kwt." + operation + "@keyboard.wtf>"));
        if (scenario == "header_entry_number") headers.Add(7);
        if (scenario == "header_value_number") headers[3] = new { name = "In-Reply-To", value = 7 };
        if (scenario == "message_id_malformed") headers[0] = Header("Message-ID", "not-an-identity");
        if (scenario == "message_id_large_malformed") headers[0] = Header("Message-ID", "<" + new string('@', 50000));
        var message = JsonSerializer.SerializeToElement(new
        {
            id = "sent_message", threadId = scenario == "actual_thread_number" ? (object)7 : "expected_thread",
            labelIds = new[] { "SENT" }, payload = new
            {
                mimeType = "text/plain", headers = scenario == "headers_object" ? (object)new { } : headers,
                body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("Fixture reply.")) }
            }
        });
        var provider = new ReadbackProvider(message);
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "mail_send", operation, plan,
            JsonSerializer.SerializeToElement(new { id = "sent_message" }), default);
        Assert.Null(result); // An exception is a failure: required metadata should return null.
        Assert.Equal(scenario.StartsWith("expected_", StringComparison.Ordinal) ? 0 : 1, provider.Reads);
        Assert.Equal(0, provider.Writes);
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public async Task Acknowledged_rewritten_message_identity_is_bounded_without_changing_its_policy(int length, bool verified)
    {
        var operation = ConnectorIds.New();
        var plan = ConnectorOperations.Plan("mail_send", Args(new { to = "reader@example.test", subject = "Reply verification fixture", text = "Fixture reply." }), operation);
        var messageId = "<" + new string('a', length - 4) + "@b>";
        Assert.Equal(length, messageId.Length);
        var message = JsonSerializer.SerializeToElement(new
        {
            id = "sent_message", threadId = "expected_thread", labelIds = new[] { "SENT" }, payload = new
            {
                mimeType = "text/plain", headers = new[]
                {
                    new { name = "Message-ID", value = messageId }, new { name = "To", value = "reader@example.test" },
                    new { name = "Subject", value = "Reply verification fixture" }
                }, body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("Fixture reply.")) }
            }
        });
        var provider = new ReadbackProvider(message);
        var result = await ConnectorOperations.VerifyWriteAsync(provider, "ca_test123", "mail_send", operation, plan,
            JsonSerializer.SerializeToElement(new { id = "sent_message" }), default);
        Assert.Equal(verified, result.HasValue); Assert.Equal(1, provider.Reads); Assert.Equal(0, provider.Writes);
    }

    [Theory]
    [InlineData("+0000", "0")]
    [InlineData("-0001", "-1")]
    [InlineData("253402300799999", "253402300799999")]
    [InlineData("-62135596800000", "-62135596800000")]
    public void Valid_internal_date_is_canonical_and_keeps_the_representable_boundaries(string input, string canonical)
    {
        var data = ConnectorOperations.SafeResult(Mail(new { mimeType = "text/plain" }, input), "mail_read").GetProperty("data");
        Assert.Equal(canonical, data.GetProperty("internalDate").GetString());
    }

    [Theory]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("１２３")]
    [InlineData("1e3")]
    [InlineData("-62135596800001")]
    public void Internal_date_rejects_non_decimal_and_out_of_range_strings(string input)
    {
        var data = ConnectorOperations.SafeResult(Mail(new { mimeType = "text/plain" }, input), "mail_read").GetProperty("data");
        Assert.True(data.GetProperty("internalDate").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void Deep_attachment_tree_is_partial_before_unobserved_leaf_metadata()
    {
        object part = Attachment("unvisited.pdf", "application/pdf", "unvisited_id", 10);
        for (var depth = 0; depth < 14; depth++) part = new { mimeType = "multipart/mixed", parts = new[] { part } };
        var safe = ConnectorOperations.SafeResult(Mail(part), "mail_read");
        Assert.Empty(safe.GetProperty("data").GetProperty("attachments").EnumerateArray());
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("unvisited_id", safe.GetRawText());
    }

    [Fact]
    public void Global_visited_part_bound_stops_attachment_only_tree_after_128_observed_parts()
    {
        object empty = new { mimeType = "application/octet-stream", body = new { size = 0 } };
        var children = Enumerable.Range(0, 6).Select(_ => (object)new { mimeType = "multipart/mixed", parts = Enumerable.Repeat(empty, 20).ToArray() }).ToList();
        // Root1 + six branches of21 =127. This seventh container is node128;
        // its first attachment child must not be inspected as node129.
        children.Add(new { mimeType = "multipart/mixed", parts = new[] { Attachment("unvisited.pdf", "application/pdf", "unvisited_id", 10) } });
        var safe = ConnectorOperations.SafeResult(Mail(new { mimeType = "multipart/mixed", parts = children }), "mail_read");
        Assert.Empty(safe.GetProperty("data").GetProperty("attachments").EnumerateArray());
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("unvisited_id", safe.GetRawText());
    }

    [Fact]
    public void Global_attachment_bound_is_20_across_valid_nested_parents()
    {
        object Branch(int start) => new { mimeType = "multipart/mixed", parts = Enumerable.Range(start, 11)
            .Select(index => Attachment("fixture.pdf", "application/pdf", "attachment_" + index, 10)).ToArray() };
        var safe = ConnectorOperations.SafeResult(Mail(new { mimeType = "multipart/mixed", parts = new[] { Branch(0), Branch(11) } }), "mail_read");
        Assert.Equal(20, safe.GetProperty("data").GetProperty("attachments").GetArrayLength());
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Field_bounds_clip_descriptions_with_disclosure_and_drop_an_oversized_reference_whole()
    {
        var safe = ConnectorOperations.SafeResult(Mail(Attachment(new string('f', 301), new string('m', 161), new string('i', 1025), 10)), "mail_read");
        var attachment = Assert.Single(safe.GetProperty("data").GetProperty("attachments").EnumerateArray());
        Assert.Equal(300, attachment.GetProperty("filename").GetString()!.Length);
        Assert.Equal(160, attachment.GetProperty("mimeType").GetString()!.Length);
        Assert.Equal(JsonValueKind.Null, attachment.GetProperty("attachmentId").ValueKind);
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain(new string('i', 100), safe.GetRawText());
    }

    [Fact]
    public void Valid_attachment_reference_at_1024_characters_is_preserved_exactly()
    {
        var id = new string('i', 1024);
        var safe = ConnectorOperations.SafeResult(Mail(Attachment("fixture.pdf", "application/pdf", id, 10)), "mail_read");
        var attachment = Assert.Single(safe.GetProperty("data").GetProperty("attachments").EnumerateArray());
        Assert.Equal(id, attachment.GetProperty("attachmentId").GetString());
        Assert.False(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
    }

    [Theory]
    [InlineData("filename_number")]
    [InlineData("mime_number")]
    [InlineData("attachment_id_number")]
    [InlineData("attachment_id_control")]
    [InlineData("size_overflow")]
    [InlineData("body_array")]
    public void Malformed_attachment_metadata_is_partial_without_an_invented_reference(string scenario)
    {
        var body = new Dictionary<string, object> { ["attachmentId"] = "observed_id", ["size"] = 10 };
        if (scenario == "attachment_id_number") body["attachmentId"] = 7;
        if (scenario == "attachment_id_control") body["attachmentId"] = "observed\n_id";
        if (scenario == "size_overflow") body["size"] = JsonSerializer.Deserialize<JsonElement>("9223372036854775808");
        var part = new
        {
            filename = scenario == "filename_number" ? (object)7 : "fixture.pdf",
            mimeType = scenario == "mime_number" ? (object)7 : "application/pdf",
            body = scenario == "body_array" ? (object)Array.Empty<object>() : body
        };
        var safe = ConnectorOperations.SafeResult(Mail(part), "mail_read");
        var attachment = Assert.Single(safe.GetProperty("data").GetProperty("attachments").EnumerateArray());
        if (scenario.StartsWith("attachment_id_", StringComparison.Ordinal) || scenario == "body_array")
            Assert.Equal(JsonValueKind.Null, attachment.GetProperty("attachmentId").ValueKind);
        if (scenario is "size_overflow" or "body_array") Assert.Equal(JsonValueKind.Null, attachment.GetProperty("size").ValueKind);
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Aggregate_budget_does_not_emit_attachment_reference_prefixes()
    {
        var parts = Enumerable.Range(0, 20).Select(index => Attachment(new string('f', 300), new string('m', 160), Id(index), 10)).ToArray();
        var safe = ConnectorOperations.SafeResult(Mail(new { mimeType = "multipart/mixed", parts }), "mail_read");
        var attachments = safe.GetProperty("data").GetProperty("attachments").EnumerateArray().ToArray();
        Assert.Contains(attachments, attachment => attachment.GetProperty("attachmentId").ValueKind == JsonValueKind.Null);
        for (var index = 0; index < attachments.Length; index++)
            if (attachments[index].GetProperty("attachmentId").ValueKind == JsonValueKind.String)
                Assert.Equal(Id(index), attachments[index].GetProperty("attachmentId").GetString());
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Search_sample_serialization_keeps_attachment_ids_whole_or_unknown_under_its_shared_budget()
    {
        var parts = Enumerable.Range(0, 20).Select(index => Attachment(new string('f', 300), new string('m', 160), Id(index), 10)).ToArray();
        var body = ConnectorOperations.SafeResult(Mail(new { mimeType = "multipart/mixed", parts }), "mail_read");
        var raw = JsonSerializer.SerializeToElement(new
        {
            messages = new[] { new { id = "m1", threadId = "thread1" } },
            sampledMessages = Enumerable.Range(0, 8).Select(index => new { id = "preview_" + index, subject = new string('s', 300), snippet = new string('p', 500) }),
            sampledContent = new { capability = "mail_read", messageId = "m1", threadId = "thread1", result = body },
            contentSample = new { mode = "message", requested = 1, returned = 1, complete = false }
        });
        var safe = ConnectorOperations.SafeResult(raw, "mail_search");
        var retained = safe.GetProperty("data").GetProperty("sampledContent").GetProperty("result");
        var attachments = retained.GetProperty("data").GetProperty("attachments").EnumerateArray().ToArray();
        for (var index = 0; index < attachments.Length; index++)
            if (attachments[index].GetProperty("attachmentId").ValueKind == JsonValueKind.String)
                Assert.Equal(Id(index), attachments[index].GetProperty("attachmentId").GetString());
        Assert.True(retained.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.False(safe.GetProperty("data").GetProperty("contentSample").GetProperty("complete").GetBoolean());
    }

    private static string Id(int index) => new string('i', 1000) + index.ToString("D24");
    private static object Attachment(string filename, string mimeType, string id, long size) => new { filename, mimeType, body = new { attachmentId = id, size } };
    private static JsonElement Mail(object payload, string internalDate = "1791743400000") => JsonSerializer.SerializeToElement(new { id = "m1", threadId = "thread1", internalDate, payload });
    private static object Header(string name, string value) => new { name, value };
    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
    private sealed class ReadbackProvider(JsonElement message) : IConnectorProvider
    {
        internal int Reads, Writes;
        public Task<JsonElement> RequestAsync(string account, string endpoint, string method, object? body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); if (method == "GET") Reads++; else Writes++;
            Assert.Equal("ca_test123", account); Assert.Equal("GET", method); Assert.Null(body);
            return Task.FromResult(message);
        }
        public Task<ProviderConnectReply> ConnectAsync(string user, string config, string callback, string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderConnection?> GetAsync(string account, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync(string account, CancellationToken ct) => throw new NotSupportedException();
    }
}
