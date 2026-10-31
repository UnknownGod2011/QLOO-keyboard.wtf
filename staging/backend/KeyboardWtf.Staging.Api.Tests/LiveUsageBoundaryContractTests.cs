using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Characterization, not provider-invoice acceptance. These offline messages
// never dispatch a model/action or authorize charging an existing reservation.
[Trait("Category", "LiveBoundaryCharacterization")]
public sealed class LiveUsageBoundaryContractTests
{
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"arithmetic"}]}],"turnComplete":true}}""";
    private const string Audio = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAAA"}}]}}}""";
    private const string Tool = """{"toolCall":{"functionCalls":[{"id":"offline-status","name":"system_status","args":{}}]}}""";
    private const string ToolResult = """{"toolResponse":{"functionResponses":[{"id":"offline-status","name":"system_status","response":{"ok":true}}]}}""";
    private const string FirstReceipt = """{"usageMetadata":{"promptTokenCount":92,"candidatesTokenCount":58,"totalTokenCount":150,"promptTokensDetails":[{"modality":"TEXT","tokenCount":92}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":52}]}}""";
    private const string SecondReceipt = """{"usageMetadata":{"promptTokenCount":162,"candidatesTokenCount":23,"totalTokenCount":185,"promptTokensDetails":[{"modality":"TEXT","tokenCount":110},{"modality":"AUDIO","tokenCount":52}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":1},{"modality":"AUDIO","tokenCount":22}]}}""";
    private const string Terminal = """{"serverContent":{"turnComplete":true}}""";
    private static string AtTerminal(string receipt) => receipt.Replace("{\"usageMetadata\"", "{\"serverContent\":{\"turnComplete\":true},\"usageMetadata\"");

    [Fact]
    public void Staged_tool_terminal_then_tool_response_then_final_receipt_has_exact_aggregate()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Tool);
        usage.ObserveProvider(AtTerminal(FirstReceipt));
        Assert.True(usage.Snapshot().Known);
        usage.ObserveClient(ToolResult);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(AtTerminal(SecondReceipt));
        var result = usage.Snapshot();
        Assert.True(result.Known);
        Assert.False(result.Estimated);
        Assert.Equal(254, result.InputTokens);
        Assert.Equal(81, result.OutputTokens);
        Assert.Equal(335, result.TotalTokens);
        usage.ObserveProvider(AtTerminal(SecondReceipt));
        Assert.Equal(result, usage.Snapshot());
    }

    [Fact]
    public void Detached_second_receipt_after_terminal_currently_does_not_repair_unknown_coverage()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstReceipt);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(Terminal);
        var before = usage.Snapshot();
        Assert.False(before.Known);
        Assert.True(before.Estimated);
        usage.ObserveProvider(SecondReceipt);
        Assert.Equal(before, usage.Snapshot());
        // This preserves a promotion limitation. It is not proof that a real
        // detached receipt is stale, nor approval to ignore legitimate invoices.
    }

    [Fact]
    public void Legacy_first_nonterminal_metadata_can_be_known_without_close_time_finality_proof()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(FirstReceipt);
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(150, usage.Snapshot().TotalTokens);
        // Known here is the existing local coverage predicate, not an independently
        // verified final provider invoice when the connection ends abruptly.
    }

    [Theory]
    [InlineData("microphone")]
    [InlineData("interruption")]
    [InlineData("unfinished-tool")]
    public void Incomplete_post_terminal_boundaries_cannot_become_authoritative(string boundary)
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstReceipt);
        if (boundary == "microphone")
            usage.ObserveClient("""{"realtimeInput":{"audio":{"data":"AAAA"}}}""");
        else if (boundary == "unfinished-tool")
            usage.ObserveClient(ToolResult);
        else
        {
            usage.ObserveClient(Question);
            usage.ObserveProvider(Audio);
            usage.ObserveProvider(AtTerminal(SecondReceipt).Replace("\"turnComplete\":true", "\"interrupted\":true,\"turnComplete\":true"));
        }
        Assert.False(usage.Snapshot().Known);
        Assert.False(ProviderUsageMetadata.IsAuthoritative(usage.Snapshot()));
    }

    private static void Complete(LiveUsageAccumulator usage, string receipt)
    {
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(AtTerminal(receipt));
    }
}
