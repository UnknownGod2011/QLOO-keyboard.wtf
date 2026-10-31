namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorSessionGuidanceMetadataTests
{
    private static JsonElement Mail(string extra = "", string date = "Sat, 18 Jul 2026 17:58:00 +0530") => JsonDocument.Parse(
        "{\"state\":\"completed\",\"verified\":true,\"result\":{\"truncated\":false,\"data\":{\"id\":\"aabbccdd11\",\"threadId\":\"aabbccdd22\","
        + "\"text\":\"PRIVATE unrelated body.\",\"headers\":[{\"name\":\"Date\",\"value\":" + JsonSerializer.Serialize(date) + "}]" + extra + "}}}")
        .RootElement.Clone();
    private static ConnectorSessionResult Context(params JsonElement[] receipts) => ConnectorSessionResult.Create("task", "gmail", "account",
        "reader@example.test", "Read this email", JsonSerializer.SerializeToElement(new { result = new { truncated = false, data = new { answer = "Verified result." } } }),
        receipts, evidenceCapabilities: receipts.Select(_ => "mail_read").ToArray());

    [Theory]
    [InlineData("\"0\"", "0")]
    [InlineData("0", "0")]
    [InlineData("\"000123\"", "123")]
    [InlineData("\"123PRIVATE\"", null)]
    [InlineData("\" 123\"", null)]
    [InlineData("\"+123\"", null)]
    [InlineData("1e3", null)]
    [InlineData("\"9223372036854775808\"", null)]
    [InlineData("\"253402300800000\"", null)]
    [InlineData("null", null)]
    public void Only_whole_valid_internalDate_becomes_canonical_epoch_milliseconds(string json, string expected)
    {
        var context = Context(Mail(",\"internalDate\":" + json)); var metadata = Assert.Single(context.Resources);
        var value = metadata.GetProperty("internalDate");
        if (expected is null) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected, value.GetString());
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(context), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_internalDate_has_no_Date_header_fallback()
    {
        var metadata = Assert.Single(Context(Mail()).Resources);
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("internalDate").ValueKind);
        Assert.Equal("Sat, 18 Jul 2026 17:58:00 +0530", metadata.GetProperty("headers").GetProperty("Date").GetString());
    }

    [Fact]
    public void Internal_received_timestamp_and_Date_header_remain_distinct_observed_metadata()
    {
        var metadata = Assert.Single(Context(Mail(",\"internalDate\":\"1791743400123\"")).Resources);
        Assert.Equal("1791743400123", metadata.GetProperty("internalDate").GetString());
        Assert.Equal("Sat, 18 Jul 2026 17:58:00 +0530", metadata.GetProperty("headers").GetProperty("Date").GetString());
    }

    [Fact]
    public void Later_same_resource_without_internalDate_does_not_erase_a_verified_observed_value()
    {
        var metadata = Assert.Single(Context(Mail(",\"internalDate\":\"1791743400123\""), Mail()).Resources);
        Assert.Equal("1791743400123", metadata.GetProperty("internalDate").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retained_guidance_cannot_exceed_revision_or_aggregate_bounds(bool oversized)
    {
        Assert.Throws<ArgumentException>(() => ConnectorSessionResult.Create("task", "gmail", "account", "reader@example.test", "Read this email",
            JsonSerializer.SerializeToElement(new { result = new { data = new { answer = "Verified result." } } }), [Mail()],
            evidenceCapabilities: ["mail_read"], goalRevision: oversized ? 2 : 1,
            acceptedReadGuidance: oversized ? [new string('a', 2000), new string('b', 2000)] : [], evidenceGoalRevisions: [0]));
    }
}
