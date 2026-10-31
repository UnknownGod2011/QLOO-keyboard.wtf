using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class LiveBudgetProjectionTests
{
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"arithmetic"}]}],"turnComplete":true}}""";
    private const string Audio = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAAA"}}]}}}""";
    private const string Metadata = """{"usageMetadata":{"promptTokenCount":92,"candidatesTokenCount":58,"totalTokenCount":150,"promptTokensDetails":[{"modality":"TEXT","tokenCount":92}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":52}]}}""";
    private const string Terminal = """{"serverContent":{"turnComplete":true}}""";
    private static readonly PricingSnapshot Pricing = new("fixture", "fixture", 300_000, 2_500_000,
        30_000, 2_500_000, 10_000, 10_000, DateTimeOffset.UnixEpoch, 1_000_000,
        500_000, 3_000_000, 3_000_000, 2_000_000, 12_000_000);
    private readonly CatalogService _catalog = new(null!, Options.Create(new PaidSystemOptions()), new Clock());

    [Fact]
    public void No_observed_work_creates_no_guard_charge()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Assert.Equal(0, Charge(usage));
        Assert.False(usage.BudgetView().Floor.Known);
        Assert.False(usage.BudgetView().Unreported.Known);
    }

    [Fact]
    public void Nonterminal_metadata_no_longer_masks_new_input_audio_and_does_not_change_closeout()
    {
        var usage = Reported();
        var before = usage.Snapshot();
        Assert.Equal(70_000, Charge(usage));
        usage.ObserveClient(Microphone(32_000));
        Assert.Equal(before with { InputAudioTokens = 25, AudioTokens = 77, Estimated = true, Known = false },
            usage.Snapshot()); // Existing inferred-modality behavior, not final invoice proof.
        Assert.Equal(25, usage.BudgetView().Unreported.InputAudioTokens);
        Assert.Equal(82_500, Charge(usage));
    }

    [Fact]
    public void Postterminal_microphone_tail_reserves_next_context_once_without_certifying_usage()
    {
        var usage = Reported();
        usage.ObserveProvider(Terminal);
        var completed = usage.Snapshot();
        usage.ObserveClient(Microphone(32_000));
        var view = usage.BudgetView();
        Assert.Equal(completed with { Known = false }, usage.Snapshot());
        Assert.Equal(98, view.Unreported.InputTokens - view.Unreported.InputAudioTokens);
        Assert.Equal(77, view.Unreported.InputAudioTokens); // 52 retained + 25 new.
        Assert.Equal(107_500, Charge(usage));
        for (var i = 0; i < 50; i++) Assert.Equal(view, usage.BudgetView());
    }

    [Fact]
    public void Audio_estimates_round_aggregate_bytes_not_each_fragment()
    {
        var whole = Reported();
        whole.ObserveClient(Microphone(1_280));
        var fragments = Reported();
        for (var i = 0; i < 1_280; i++) fragments.ObserveClient(Microphone(1));
        Assert.Equal(whole.BudgetView(), fragments.BudgetView());
        Assert.Equal(Charge(whole), Charge(fragments));
    }

    [Fact]
    public void Identical_metadata_cannot_reset_unreported_input_or_output_anchors()
    {
        var usage = Reported();
        usage.ObserveClient(Microphone(32_000));
        usage.ObserveProvider(OutputAudio(48_000));
        var before = usage.BudgetView();
        usage.ObserveProvider(Metadata);
        Assert.Equal(before, usage.BudgetView());
        Assert.Equal(120_000, Charge(usage));
    }

    [Fact]
    public void Output_after_metadata_grows_guard_without_a_new_receipt()
    {
        var usage = Reported();
        usage.ObserveProvider(OutputAudio(48_000));
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(25, usage.BudgetView().Unreported.OutputAudioTokens);
        Assert.Equal(107_500, Charge(usage));
    }

    [Fact]
    public void Text_only_receipt_advances_cannot_erase_outstanding_audio_estimates()
    {
        var usage = Reported();
        usage.ObserveClient(Microphone(32_000));
        usage.ObserveProvider(OutputAudio(48_000));
        usage.ObserveProvider(Metadata.Replace("92", "93").Replace("58", "59")
            .Replace("150", "152").Replace("\"tokenCount\":6", "\"tokenCount\":7"));
        Assert.Equal(25, usage.BudgetView().Unreported.InputAudioTokens);
        Assert.Equal(25, usage.BudgetView().Unreported.OutputAudioTokens);
        usage.ObserveClient(Microphone(32_000));
        usage.ObserveProvider(OutputAudio(48_000));
        usage.ObserveProvider(Metadata.Replace("92", "94").Replace("58", "60")
            .Replace("150", "154").Replace("\"tokenCount\":6", "\"tokenCount\":8"));
        Assert.Equal(50, usage.BudgetView().Unreported.InputAudioTokens);
        Assert.Equal(50, usage.BudgetView().Unreported.OutputAudioTokens);
    }

    [Fact]
    public void Partial_audio_receipt_consumes_only_its_reported_increment_not_the_entire_tail()
    {
        var usage = Reported();
        usage.ObserveProvider(OutputAudio(48_000));
        usage.ObserveProvider(Metadata.Replace("58", "59").Replace("150", "151")
            .Replace("\"tokenCount\":52", "\"tokenCount\":53"));
        Assert.Equal(24, usage.BudgetView().Unreported.OutputAudioTokens);
        usage.ObserveProvider(OutputAudio(48_000));
        usage.ObserveProvider(Metadata.Replace("58", "60").Replace("150", "152")
            .Replace("\"tokenCount\":52", "\"tokenCount\":54"));
        Assert.Equal(48, usage.BudgetView().Unreported.OutputAudioTokens);
    }

    [Fact]
    public void Same_total_modality_change_updates_floor_and_next_context_without_resetting_other_tails()
    {
        var usage = Reported();
        usage.ObserveProvider(OutputAudio(48_000));
        var prior = Charge(usage);
        usage.ObserveProvider(Metadata.Replace("\"modality\":\"TEXT\",\"tokenCount\":92",
            "\"modality\":\"AUDIO\",\"tokenCount\":92"));
        Assert.Equal(92, usage.BudgetView().Floor.InputAudioTokens);
        Assert.Equal(25, usage.BudgetView().Unreported.OutputAudioTokens);
        Assert.True(Charge(usage) > prior);
        usage.ObserveProvider(Terminal);
        usage.ObserveClient(Microphone(32_000));
        Assert.Equal(92 + 52 + 25, usage.BudgetView().Unreported.InputAudioTokens);
    }

    [Fact]
    public void First_partial_receipt_does_not_assume_all_observed_audio_was_reported()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Microphone(32_000));
        usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":0,"totalTokenCount":1,"promptTokensDetails":[{"modality":"AUDIO","tokenCount":1}]}}""");
        Assert.Equal(1, usage.BudgetView().Floor.InputAudioTokens);
        Assert.Equal(24, usage.BudgetView().Unreported.InputAudioTokens);
    }

    [Fact]
    public void Repeated_modality_recategorization_cannot_consume_the_same_audio_report_twice()
    {
        var usage = Reported();
        var asText = Metadata.Replace("{\"modality\":\"TEXT\",\"tokenCount\":6},{\"modality\":\"AUDIO\",\"tokenCount\":52}",
            "{\"modality\":\"TEXT\",\"tokenCount\":58}");
        for (var i = 1; i <= 50; i++)
        {
            usage.ObserveProvider(OutputAudio(1_920));
            usage.ObserveProvider(asText);
            usage.ObserveProvider(Metadata);
            Assert.Equal(i, usage.BudgetView().Unreported.OutputAudioTokens);
        }
    }

    [Fact]
    public void Structured_tool_response_and_non_ascii_text_are_budgeted_without_becoming_invoice_evidence()
    {
        var usage = Reported();
        usage.ObserveClient("""{"toolResponse":{"functionResponses":[{"id":"a","name":"read","response":{"value":"雪","number":123}}]}}""");
        var view = usage.BudgetView();
        Assert.True(view.Unreported.InputTokens > 0);
        Assert.True(view.Unreported.Estimated);
        Assert.False(view.Unreported.Known);
        Assert.False(usage.Snapshot().Known);
        Assert.True(Charge(usage) > 70_000);
    }

    [Fact]
    public void Transcription_echo_is_not_added_as_independent_guard_output()
    {
        var usage = Reported();
        var before = usage.BudgetView();
        usage.ObserveProvider("""{"serverContent":{"inputTranscription":{"text":"heard"},"outputTranscription":{"text":"spoken"}}}""");
        Assert.Equal(before, usage.BudgetView());
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(12_500)]
    [InlineData(20_000)]
    public void Known_receipt_is_not_resafetied_when_only_increment_is_estimated(int basisPoints)
    {
        var usage = Reported();
        var receipt = usage.Snapshot();
        Assert.Equal(_catalog.CalculateLiveActualCost(Pricing, receipt, basisPoints).ChargeCreditMicros,
            LiveBudgetProjection.Charge(_catalog, Pricing, usage.BudgetView(), basisPoints));
        usage.ObserveClient(Microphone(32_000));
        var view = usage.BudgetView();
        var combined = LiveBudgetProjection.Add(view.Floor, view.Unreported) with { Estimated = true };
        var expected = _catalog.CalculateLiveActualCost(Pricing, receipt, basisPoints).ChargeCreditMicros
            + _catalog.CalculateLiveActualCost(Pricing, combined, basisPoints).ChargeCreditMicros
            - _catalog.CalculateLiveActualCost(Pricing, view.Floor with { Estimated = true }, basisPoints).ChargeCreditMicros;
        Assert.Equal(expected, LiveBudgetProjection.Charge(_catalog, Pricing, view, basisPoints));
    }

    [Fact]
    public void Projection_reads_leave_exact_receipt_and_closeout_evidence_unchanged()
    {
        var usage = Reported();
        var exact = usage.Snapshot();
        var evidence = usage.CloseoutEvidence();
        for (var i = 0; i < 50; i++)
        {
            _ = Charge(usage);
            Assert.Equal(exact, usage.Snapshot());
            Assert.Equal(evidence, usage.CloseoutEvidence());
            Assert.False(usage.BudgetView().Floor.Known);
        }
    }

    private long Charge(LiveUsageAccumulator usage) =>
        LiveBudgetProjection.Charge(_catalog, Pricing, usage.BudgetView(), 12_500);

    private static LiveUsageAccumulator Reported()
    {
        var usage = new LiveUsageAccumulator(4_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(Metadata);
        return usage;
    }

    private static string Microphone(int bytes) =>
        "{\"realtimeInput\":{\"audio\":{\"data\":\"" + Convert.ToBase64String(new byte[bytes]) + "\"}}}";
    private static string OutputAudio(int bytes) =>
        "{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"data\":\""
        + Convert.ToBase64String(new byte[bytes]) + "\"}}]}}}";

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}
