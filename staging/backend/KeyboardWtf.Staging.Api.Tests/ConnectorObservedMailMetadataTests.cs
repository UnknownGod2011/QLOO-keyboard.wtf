using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorObservedMailMetadataTests
{
    [Theory]
    [InlineData("1791743400000")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Full_message_retains_provider_internal_date_separately_from_header_date(string milliseconds)
    {
        var safe = ConnectorOperations.SafeResult(Message("m1", JsonSerializer.SerializeToElement(milliseconds)), "mail_read");
        var data = safe.GetProperty("data");
        Assert.Equal(milliseconds, data.GetProperty("internalDate").GetString());
        Assert.Contains(data.GetProperty("headers").EnumerateArray(), header =>
            header.GetProperty("name").GetString() == "Date" && header.GetProperty("value").GetString() == "Tue, 01 Jan 2030 00:00:00 +0000");
        Assert.Equal("Fixture body.", data.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("1791743400000")]
    [InlineData("\"not-a-timestamp\"")]
    [InlineData("\"1791743400000.5\"")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("\"253402300800000\"")]
    public void Malformed_or_out_of_range_internal_date_is_unknown_without_header_or_clock_fallback(string raw)
    {
        var safe = ConnectorOperations.SafeResult(Message("m1", JsonSerializer.Deserialize<JsonElement>(raw)), "mail_read");
        Assert.True(Unknown(safe.GetProperty("data"), "internalDate"));
        Assert.Equal("Fixture body.", safe.GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public void Missing_provider_internal_date_stays_unknown_even_with_a_valid_date_header()
    {
        var source = Message("m1", null);
        Assert.False(source.TryGetProperty("internalDate", out _));
        Assert.True(Unknown(ConnectorOperations.SafeResult(source, "mail_read").GetProperty("data"), "internalDate"));
    }

    [Fact]
    public void Thread_messages_retain_their_independent_internal_dates_and_existing_recent_order()
    {
        var source = JsonSerializer.SerializeToElement(new { id = "thread1", messages = new[]
        {
            Message("older", JsonSerializer.SerializeToElement("1791743400000")),
            Message("newer", JsonSerializer.SerializeToElement("1791743401000"))
        } });
        var safe = ConnectorOperations.SafeResult(source, "mail_thread").GetProperty("data");
        Assert.Equal("newest_first", safe.GetProperty("order").GetString());
        Assert.Equal("newer", safe.GetProperty("messages")[0].GetProperty("id").GetString());
        Assert.Equal("1791743401000", safe.GetProperty("messages")[0].GetProperty("internalDate").GetString());
        Assert.Equal("1791743400000", safe.GetProperty("messages")[1].GetProperty("internalDate").GetString());
    }

    [Fact]
    public async Task Existing_metadata_GET_requests_and_retains_internal_date_without_extra_body_calls()
    {
        var provider = new MetadataProvider(Message("m1", JsonSerializer.SerializeToElement("1791743400000")));
        var search = JsonSerializer.SerializeToElement(new { messages = new[] { new { id = "m1", threadId = "thread1" } } });
        var raw = await ConnectorOperations.MailSearchMetadataAsync(provider, "ca_test123", search, 6, default);
        var safe = ConnectorOperations.SafeResult(raw, "mail_search").GetProperty("data");
        Assert.Equal("1791743400000", safe.GetProperty("sampledMessages")[0].GetProperty("internalDate").GetString());
        Assert.Contains("internalDate", Assert.Single(provider.Endpoints));
        Assert.DoesNotContain("Fixture body.", safe.GetRawText());
    }

    [Fact]
    public void Full_message_keeps_only_observed_attachment_metadata_and_never_binary_data()
    {
        var message = WithParts(new object[]
        {
            Text("Fixture body."), Attachment("fixture-report.pdf", "application/pdf", "observed_attachment", 12345)
        });
        var safe = ConnectorOperations.SafeResult(message, "mail_read");
        var attachment = Assert.Single(safe.GetProperty("data").GetProperty("attachments").EnumerateArray());
        Assert.Equal("fixture-report.pdf", attachment.GetProperty("filename").GetString());
        Assert.Equal("application/pdf", attachment.GetProperty("mimeType").GetString());
        Assert.Equal("observed_attachment", attachment.GetProperty("attachmentId").GetString());
        Assert.Equal(12345, attachment.GetProperty("size").GetInt64());
        Assert.False(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.Equal("Fixture body.", safe.GetProperty("data").GetProperty("text").GetString());
        Assert.DoesNotContain("PRIVATE_BINARY_FIXTURE", safe.GetRawText());
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("PRIVATE_BINARY_FIXTURE")), safe.GetRawText());
        Assert.False(attachment.TryGetProperty("data", out _));
    }

    [Fact]
    public void Attachment_metadata_bound_is_disclosed_and_does_not_claim_content_was_downloaded()
    {
        var parts = Enumerable.Range(0, 33).Select(index => Attachment("fixture-" + index + ".pdf", "application/pdf", "attachment_" + index, 10)).ToArray();
        var safe = ConnectorOperations.SafeResult(WithParts(parts), "mail_read");
        Assert.InRange(safe.GetProperty("data").GetProperty("attachments").GetArrayLength(), 1, 20);
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("PRIVATE_BINARY_FIXTURE", safe.GetRawText());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"123\"")]
    [InlineData("{}")]
    public void Invalid_attachment_size_is_not_presented_as_a_valid_byte_count(string raw)
    {
        var part = new { filename = "fixture.pdf", mimeType = "application/pdf", body = new
        {
            attachmentId = "observed_attachment", size = JsonSerializer.Deserialize<JsonElement>(raw)
        } };
        var safe = ConnectorOperations.SafeResult(WithParts(new object[] { part }), "mail_read");
        var attachment = Assert.Single(safe.GetProperty("data").GetProperty("attachments").EnumerateArray());
        Assert.True(Unknown(attachment, "size"));
        Assert.True(safe.GetProperty("data").GetProperty("attachmentMetadataTruncated").GetBoolean());
    }

    [Fact]
    public void Bounded_metadata_precedes_a_large_body_and_remains_owned_by_its_thread_message()
    {
        var message = WithParts(new object[]
        {
            Text(new string('x', 20000)), Attachment("fixture.pdf", "application/pdf", "observed_attachment", 10)
        });
        var source = JsonSerializer.SerializeToElement(new { id = "thread1", messages = new[] { message } });
        var safe = ConnectorOperations.SafeResult(source, "mail_thread");
        var normalized = Assert.Single(safe.GetProperty("data").GetProperty("messages").EnumerateArray());
        Assert.Equal("m1", normalized.GetProperty("id").GetString());
        Assert.Equal("1791743400000", normalized.GetProperty("internalDate").GetString());
        Assert.Equal("observed_attachment", Assert.Single(normalized.GetProperty("attachments").EnumerateArray()).GetProperty("attachmentId").GetString());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
    }

    private static bool Unknown(JsonElement data, string field) => !data.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null;
    private static object Text(string text) => new { mimeType = "text/plain", body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) } };
    private static object Attachment(string filename, string mimeType, string attachmentId, long size) => new
    {
        filename, mimeType, body = new { attachmentId, size, data = Convert.ToBase64String(Encoding.UTF8.GetBytes("PRIVATE_BINARY_FIXTURE")) }
    };
    private static JsonElement WithParts(object[] parts) => JsonSerializer.SerializeToElement(new
    {
        id = "m1", threadId = "thread1", internalDate = "1791743400000",
        payload = new { mimeType = "multipart/mixed", parts }
    });
    private static JsonElement Message(string id, JsonElement? internalDate)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = id, ["threadId"] = "thread1", ["payload"] = new
            {
                mimeType = "text/plain", headers = new[] { new { name = "Date", value = "Tue, 01 Jan 2030 00:00:00 +0000" } },
                body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("Fixture body.")) }
            }
        };
        if (internalDate.HasValue) result["internalDate"] = internalDate.Value;
        return JsonSerializer.SerializeToElement(result);
    }
    private sealed class MetadataProvider(JsonElement message) : IConnectorProvider
    {
        internal readonly List<string> Endpoints = [];
        public Task<JsonElement> RequestAsync(string account, string endpoint, string method, object? body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Endpoints.Add(endpoint);
            Assert.Equal("ca_test123", account); Assert.Equal("GET", method); Assert.Null(body);
            Assert.Contains("format=metadata", endpoint); Assert.Contains("internalDate", endpoint);
            return Task.FromResult(message);
        }
        public Task<ProviderConnectReply> ConnectAsync(string user, string config, string callback, string alias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderConnection>> ListAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderConnection?> GetAsync(string account, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync(string account, CancellationToken ct) => throw new NotSupportedException();
    }
}