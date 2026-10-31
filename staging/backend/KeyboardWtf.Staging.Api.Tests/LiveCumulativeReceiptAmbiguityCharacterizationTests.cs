using System.Text.Json;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Counterexample to an assumed receipt contract, not evidence that Gemini emits
// these vectors. Neither synthetic metadata validity nor Known proves whether
// wire counts cover one response or a cumulative session. No provider, account,
// socket, price calculation or settlement is performed by this characterization.
[Trait("Category", "LiveBoundaryCharacterization")]
public sealed class LiveCumulativeReceiptAmbiguityCharacterizationTests
{
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"arithmetic"}]}],"turnComplete":true}}""";
    private const string Audio = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAAA"}}]}}}""";
    // The first vector reuses the established per-turn fixture's numbers.
    private const string FirstFinal = """{"serverContent":{"turnComplete":true},"usageMetadata":{"promptTokenCount":92,"candidatesTokenCount":58,"totalTokenCount":150,"promptTokensDetails":[{"modality":"TEXT","tokenCount":92}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":52}]}}""";
    // Hypothetical cumulative vector after a second 162-input/23-output response:
    // input 92+162=254; output 58+23=81; total 150+185=335.
    private const string HypotheticalCumulativeSecondFinal = """{"serverContent":{"turnComplete":true},"usageMetadata":{"promptTokenCount":254,"candidatesTokenCount":81,"totalTokenCount":335,"promptTokensDetails":[{"modality":"TEXT","tokenCount":202},{"modality":"AUDIO","tokenCount":52}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":7},{"modality":"AUDIO","tokenCount":74}]}}""";

    [Fact]
    public void If_wire_receipts_are_session_cumulative_the_draft_counts_the_prefix_twice_despite_locally_known_usage()
    {
        using var firstDocument = JsonDocument.Parse(FirstFinal);
        using var secondDocument = JsonDocument.Parse(HypotheticalCumulativeSecondFinal);
        Assert.True(ProviderUsageMetadata.TryParse(firstDocument.RootElement.GetProperty("usageMetadata"), out var first, allowToolInput: true));
        Assert.True(ProviderUsageMetadata.TryParse(secondDocument.RootElement.GetProperty("usageMetadata"), out var cumulative, allowToolInput: true));
        Assert.True(ProviderUsageMetadata.IsAuthoritative(first));
        Assert.True(ProviderUsageMetadata.IsAuthoritative(cumulative));
        Assert.Equal(150, first.TotalTokens);
        Assert.Equal(335, cumulative.TotalTokens);

        for (var cycle = 0; cycle < 30; cycle++)
        {
            var usage = new LiveUsageAccumulator(1_000_000);
            Complete(usage, FirstFinal);
            Complete(usage, HypotheticalCumulativeSecondFinal);
            var observed = usage.Snapshot();

            Assert.True(observed.Known);
            Assert.False(observed.Estimated);
            Assert.Equal(346, observed.InputTokens); // 92 + 254, not cumulative 254.
            Assert.Equal(139, observed.OutputTokens); // 58 + 81, not cumulative 81.
            Assert.Equal(485, observed.TotalTokens); // 150 + 335, not cumulative 335.
            Assert.Equal(first.InputTokens, observed.InputTokens - cumulative.InputTokens);
            Assert.Equal(first.OutputTokens, observed.OutputTokens - cumulative.OutputTokens);
            Assert.Equal(first.TotalTokens, observed.TotalTokens - cumulative.TotalTokens);
            Assert.Equal(126, observed.OutputAudioTokens); // 52 + 74, not cumulative 74.
            Assert.Equal(LiveAccountingDisposition.Settle,
                VertexLiveProxyService.DetermineAccountingDisposition(true, observed.TotalTokens, observed.Known));

            // Idle duplicate-terminal protection does not resolve the session-
            // cumulative versus per-response interpretation across distinct turns.
            usage.ObserveProvider(HypotheticalCumulativeSecondFinal);
            Assert.Equal(observed, usage.Snapshot());
        }

        // This conditional counterexample is a promotion hazard, not proof of
        // actual overbilling. The provider's exact receipt scope must be established
        // independently before interpreting locally valid counts as invoice truth.
    }

    private static void Complete(LiveUsageAccumulator usage, string final)
    {
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(final);
    }
}
