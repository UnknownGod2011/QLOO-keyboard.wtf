namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorTruncatedThreadTargetTests
{
    private const string Thread = "aaaabbbbccccdddd";

    private static JsonElement Receipt(bool truncated, bool references, string childThread = "", string childId = "", bool childClipped = true)
        => JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { truncated, data = new { id = Thread,
                messageReferences = references ? new[] { new { id = "1111222233334444", threadId = Thread }, new { id = "5555666677778888", threadId = Thread } } : [],
                messages = new object[] { new { id = "1111222233334444", threadId = Thread, text = "Actual retrieved message." },
                    new { id = childId, threadId = childThread, text = "", contentTruncated = childClipped } } } } });

    [Theory]
    [InlineData(true, true, "", "", true, true)]
    [InlineData(false, true, "", "", true, false)]
    [InlineData(true, false, "", "", true, false)]
    [InlineData(true, true, "", "", false, false)]
    [InlineData(true, true, "otherthread", "5555666677778888", true, false)]
    [InlineData(true, true, Thread, "", true, false)]
    public void Clipped_child_payload_does_not_erase_verified_thread_identity(bool clipped, bool references,
        string childThread, string childId, bool childClipped, bool expected)
    {
        var args = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(Thread) };
        Assert.Equal(expected, ConnectorReadTargetSelection.HasUniqueObject("gmail", "mail_thread", args,
            Receipt(clipped, references, childThread, childId, childClipped)));
    }
}
