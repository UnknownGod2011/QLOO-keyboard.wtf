using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Offline characterization of the protected accumulator, not approval of its
// coverage predicate as a provider invoice. Synthetic frames never execute a
// tool, open a socket, access an account or authorize settlement of a real hold.
[Trait("Category", "LiveBoundaryCharacterization")]
public sealed class LiveAdditionalBoundaryCharacterizationTests
{
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"arithmetic"}]}],"turnComplete":true}}""";
    private const string InputAudio = """{"realtimeInput":{"audio":{"data":"AQID"}}}""";
    private const string OutputAudio = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AQID"}}]}}}""";
    private const string Terminal = """{"serverContent":{"turnComplete":true}}""";
    private const string Interrupted = """{"serverContent":{"interrupted":true}}""";
    private const string TwoCalls = """{"toolCall":{"functionCalls":[{"id":"offline-left","name":"system_status","args":{}},{"id":"offline-right","name":"system_status","args":{}}]}}""";
    private const string PartialResult = """{"toolResponse":{"functionResponses":[{"id":"offline-left","name":"system_status","response":{"ok":true}}]}}""";
    private const string OtherResult = """{"toolResponse":{"functionResponses":[{"id":"offline-right","name":"system_status","response":{"ok":true}}]}}""";
    private const string BothResults = """{"toolResponse":{"functionResponses":[{"id":"offline-left","name":"system_status","response":{"ok":true}},{"id":"offline-right","name":"system_status","response":{"ok":true}}]}}""";
    private const string FirstReceipt = """{"usageMetadata":{"promptTokenCount":92,"candidatesTokenCount":58,"totalTokenCount":150,"promptTokensDetails":[{"modality":"TEXT","tokenCount":40},{"modality":"AUDIO","tokenCount":52}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":52}]}}""";
    private const string SecondReceipt = """{"usageMetadata":{"promptTokenCount":162,"candidatesTokenCount":23,"totalTokenCount":185,"promptTokensDetails":[{"modality":"TEXT","tokenCount":110},{"modality":"AUDIO","tokenCount":52}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":1},{"modality":"AUDIO","tokenCount":22}]}}""";
    private const string DelayedReceipt = """{"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":60,"totalTokenCount":160,"promptTokensDetails":[{"modality":"TEXT","tokenCount":48},{"modality":"AUDIO","tokenCount":52}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":54}]}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mic_after_nonterminal_receipt_before_bare_terminal_can_remain_locally_known(bool closeTurn)
    {
        for (var cycle = 0; cycle < 30; cycle++)
        {
            var usage = BeginFirstResponse();
            usage.ObserveProvider(FirstReceipt);
            var before = usage.Snapshot();
            var beforeEvidence = usage.CloseoutEvidence();
            usage.ObserveClient(InputAudio);
            if (closeTurn) usage.ObserveProvider(Terminal);

            // Current audio-frame accounting does not increment semantic input
            // generation. A first nonterminal receipt can consequently remain
            // Known despite later microphone bytes. This is a promotion concern,
            // NOT evidence those bytes were accepted, covered or nonbillable.
            Assert.Equal(before, usage.Snapshot());
            Assert.True(usage.Snapshot().Known);
            Assert.False(usage.Snapshot().Estimated);
            var afterEvidence = usage.CloseoutEvidence();
            Assert.Equal(beforeEvidence.InputGeneration, afterEvidence.InputGeneration);
            Assert.Equal(beforeEvidence.CoveredInputGeneration, afterEvidence.CoveredInputGeneration);
            Assert.Equal(3, afterEvidence.InputAudioBytes - beforeEvidence.InputAudioBytes);
            Assert.Equal(LiveAccountingDisposition.Settle, Disposition(usage));
        }
    }

    [Fact]
    public void The_same_mic_frame_after_terminal_instead_makes_the_frozen_snapshot_unknown()
    {
        var usage = BeginFirstResponse();
        usage.ObserveProvider(AtTerminal(FirstReceipt));
        var completed = usage.Snapshot();
        usage.ObserveClient(InputAudio);
        Assert.Equal(completed with { Known = false }, usage.Snapshot());
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(LiveAccountingDisposition.Reconcile, Disposition(usage));
    }

    [Theory]
    [InlineData("before-metadata")]
    [InlineData("after-metadata")]
    [InlineData("after-terminal")]
    public void First_turn_interruption_has_different_current_semantics_before_and_after_freeze(string position)
    {
        var usage = BeginFirstResponse();
        if (position == "before-metadata") usage.ObserveProvider(Interrupted);
        usage.ObserveProvider(FirstReceipt);
        if (position == "after-metadata") usage.ObserveProvider(Interrupted);
        usage.ObserveProvider(Terminal);
        if (position == "after-terminal") usage.ObserveProvider(Interrupted);

        // A standalone interruption after the frozen terminal currently leaves
        // that snapshot unchanged. This does not establish that a real late
        // interruption is redundant or that a close-time invoice is complete.
        var locallyKnown = position == "after-terminal";
        Assert.Equal(locallyKnown, usage.Snapshot().Known);
        Assert.Equal(locallyKnown ? LiveAccountingDisposition.Settle : LiveAccountingDisposition.Reconcile,
            Disposition(usage));
        Assert.Equal(150, usage.Snapshot().TotalTokens);
    }

    [Fact]
    public void First_interruption_without_terminal_is_not_repaired_by_delayed_metadata_or_a_later_turn()
    {
        var usage = BeginFirstResponse();
        usage.ObserveProvider(FirstReceipt);
        usage.ObserveProvider(Interrupted);
        Assert.False(usage.Snapshot().Known);
        usage.ObserveProvider(DelayedReceipt);
        Assert.Equal(160, usage.Snapshot().TotalTokens);
        Assert.False(usage.Snapshot().Known);
        usage.ObserveProvider(AtTerminal(DelayedReceipt));
        Assert.False(usage.Snapshot().Known);

        usage.ObserveClient(Question);
        usage.ObserveProvider(OutputAudio);
        usage.ObserveProvider(AtTerminal(SecondReceipt));
        Assert.Equal(345, usage.Snapshot().TotalTokens);
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(LiveAccountingDisposition.Reconcile, Disposition(usage));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("combined")]
    [InlineData("split-before-output")]
    public void Multicall_partial_and_complete_responses_can_have_the_same_locally_known_aggregate(string results)
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(TwoCalls);
        usage.ObserveProvider(AtTerminal(FirstReceipt));
        Assert.True(usage.Snapshot().Known);
        usage.ObserveClient(results == "combined" ? BothResults : PartialResult);
        if (results == "split-before-output") usage.ObserveClient(OtherResult);
        usage.ObserveProvider(OutputAudio);
        usage.ObserveProvider(AtTerminal(SecondReceipt));

        // The accumulator counts content generations, not tool-ID discharge.
        // This predicate cannot prove that both proposed tools received results.
        // The supplied provider vectors also do not prove a real invoice.
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(335, usage.Snapshot().TotalTokens);
        Assert.Equal(results == "split-before-output" ? 3 : 2,
            usage.CloseoutEvidence().InputGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tool_result_before_proposal_terminal_remains_unknown_with_or_without_earlier_metadata(bool earlyReceipt)
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(TwoCalls);
        if (earlyReceipt) usage.ObserveProvider(FirstReceipt);
        usage.ObserveClient(PartialResult);
        usage.ObserveProvider(AtTerminal(FirstReceipt));
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(LiveAccountingDisposition.Reconcile, Disposition(usage));

        usage.ObserveClient(OtherResult);
        usage.ObserveProvider(OutputAudio);
        usage.ObserveProvider(AtTerminal(SecondReceipt));
        Assert.Equal(335, usage.Snapshot().TotalTokens);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void Partial_results_split_by_new_provider_output_do_not_infer_full_tool_chain_coverage()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(TwoCalls);
        usage.ObserveProvider(AtTerminal(FirstReceipt));
        usage.ObserveClient(PartialResult);
        usage.ObserveProvider(OutputAudio);
        usage.ObserveClient(OtherResult);
        usage.ObserveProvider(AtTerminal(SecondReceipt));
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(LiveAccountingDisposition.Reconcile, Disposition(usage));
    }

    [Fact]
    public void Tool_calls_receipt_and_terminal_colocated_are_currently_one_known_proposal_vector()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(AtTerminal(FirstReceipt).Replace(
            "{\"serverContent\"", TwoCalls[..^1] + ",\"serverContent\""));
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(150, usage.Snapshot().TotalTokens);
        Assert.Equal(1, usage.CloseoutEvidence().OutputGeneration);
        // One generation for two IDs does not establish tool execution, complete
        // chain coverage, or receipt attribution across an actual relay close.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detached_receipt_around_new_input_cannot_repair_an_uncovered_completed_turn(bool nextInputFirst)
    {
        var usage = BeginFirstResponse();
        usage.ObserveProvider(AtTerminal(FirstReceipt));
        usage.ObserveClient(Question);
        usage.ObserveProvider(OutputAudio);
        usage.ObserveProvider(Terminal);
        var uncovered = usage.Snapshot();
        Assert.False(uncovered.Known);
        if (nextInputFirst) usage.ObserveClient(Question);
        usage.ObserveProvider(SecondReceipt);

        if (!nextInputFirst)
        {
            // Detached metadata while the turn is frozen is currently ignored,
            // not independently identified as a stale versus delayed receipt.
            Assert.Equal(uncovered, usage.Snapshot());
            usage.ObserveClient(Question);
        }
        Assert.False(usage.Snapshot().Known);
        usage.ObserveProvider(OutputAudio);
        usage.ObserveProvider(AtTerminal(SecondReceipt));
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(LiveAccountingDisposition.Reconcile, Disposition(usage));
    }

    private static LiveUsageAccumulator BeginFirstResponse()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(OutputAudio);
        return usage;
    }

    private static string AtTerminal(string receipt) =>
        receipt.Replace("{\"usageMetadata\"", "{\"serverContent\":{\"turnComplete\":true},\"usageMetadata\"");

    private static LiveAccountingDisposition Disposition(LiveUsageAccumulator usage) =>
        VertexLiveProxyService.DetermineAccountingDisposition(true, usage.Snapshot().TotalTokens, usage.Snapshot().Known);
}
