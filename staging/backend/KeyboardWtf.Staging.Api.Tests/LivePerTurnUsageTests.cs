using KeyboardWtf.Staging.Api.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class LivePerTurnUsageTests
{
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"arithmetic"}]}],"turnComplete":true}}""";
    private const string Audio = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAAA"}}]}}}""";
    private const string FirstFinal = """{"serverContent":{"turnComplete":true},"usageMetadata":{"promptTokenCount":92,"candidatesTokenCount":58,"totalTokenCount":150,"promptTokensDetails":[{"modality":"TEXT","tokenCount":92}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":52}]}}""";
    private const string SecondFinal = """{"serverContent":{"turnComplete":true},"usageMetadata":{"promptTokenCount":162,"candidatesTokenCount":23,"totalTokenCount":185,"promptTokensDetails":[{"modality":"TEXT","tokenCount":110},{"modality":"AUDIO","tokenCount":52}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":1},{"modality":"AUDIO","tokenCount":22}]}}""";

    [Fact]
    public void Fifty_observed_shorter_second_turns_sum_coherent_provider_vectors()
    {
        for (var cycle = 0; cycle < 50; cycle++)
        {
            var usage = new LiveUsageAccumulator(1_000_000);
            Complete(usage, FirstFinal);
            Assert.True(usage.Snapshot().Known);
            Complete(usage, SecondFinal);
            var result = usage.Snapshot();
            Assert.True(result.Known);
            Assert.False(result.Estimated);
            Assert.Equal(254, result.InputTokens);
            Assert.Equal(81, result.OutputTokens);
            Assert.Equal(335, result.TotalTokens);
            Assert.Equal(52, result.InputAudioTokens);
            Assert.Equal(74, result.OutputAudioTokens);
            Assert.Equal(126, result.AudioTokens);
            Assert.Equal(usage.CloseoutEvidence().OutputGeneration, usage.CloseoutEvidence().CoveredOutputGeneration);
        }
    }

    [Fact]
    public void Identical_counts_in_distinct_completed_turns_count_twice_but_terminal_duplicate_does_not()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveProvider(FirstFinal);
        Assert.Equal(150, usage.Snapshot().TotalTokens);
        Complete(usage, FirstFinal);
        usage.ObserveProvider(FirstFinal);
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(300, usage.Snapshot().TotalTokens);
    }

    [Fact]
    public void Missing_second_receipt_does_not_certify_old_usage_or_new_output()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider("""{"serverContent":{"turnComplete":true}}""");
        Assert.False(usage.Snapshot().Known);
        // A later complete turn cannot erase an earlier uncovered turn.
        Complete(usage, SecondFinal);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void A_bare_stale_report_cannot_cover_the_next_turn()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(FirstFinal.Replace("\"serverContent\":{\"turnComplete\":true},", ""));
        Assert.False(usage.Snapshot().Known);
        usage.ObserveProvider("""{"serverContent":{"turnComplete":true}}""");
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void Old_terminal_after_new_input_without_a_new_response_cannot_charge_again()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveClient(Question);
        usage.ObserveProvider(FirstFinal);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void Continuous_microphone_frames_cannot_reclassify_or_poison_a_frozen_completed_turn()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        var completed = usage.Snapshot();
        usage.ObserveClient("""{"realtimeInput":{"audio":{"data":"AAAA"}}}""");
        Assert.Equal(completed with { Known = false }, usage.Snapshot());
        usage.ObserveProvider(Audio);
        Assert.False(usage.Snapshot().Known);
        usage.ObserveProvider(SecondFinal);
        var result = usage.Snapshot();
        Assert.True(result.Known);
        Assert.Equal(335, result.TotalTokens);
        Assert.Equal(52, result.InputAudioTokens);
    }

    [Fact]
    public void Duplicate_final_content_cannot_prove_a_fresh_response_to_new_input()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        var final = FirstFinal.Replace("\"turnComplete\":true", "\"modelTurn\":{\"parts\":[{\"inlineData\":{\"data\":\"AAAA\"}}]},\"turnComplete\":true");
        Complete(usage, final);
        usage.ObserveClient(Question);
        usage.ObserveProvider(final);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void A_coherent_later_zero_output_receipt_is_not_poisoned_by_previous_audio_output()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveClient(Question);
        usage.ObserveProvider("""{"serverContent":{"turnComplete":true},"usageMetadata":{"promptTokenCount":10,"totalTokenCount":10}}""");
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(160, usage.Snapshot().TotalTokens);
    }

    [Fact]
    public void New_microphone_input_without_an_attributable_provider_report_is_an_uncertain_tail()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveClient("""{"realtimeInput":{"audio":{"data":"AAAA"}}}""");
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(150, usage.Snapshot().TotalTokens);
    }

    [Fact]
    public void Reserialized_duplicate_terminal_with_final_audio_content_does_not_double_count()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        var final = FirstFinal.Replace("\"turnComplete\":true", "\"modelTurn\":{\"parts\":[{\"inlineData\":{\"data\":\"AAAA\"}}]},\"turnComplete\":true");
        Complete(usage, final);
        var node = JsonNode.Parse(final)!;
        var duplicate = new JsonObject {
            ["usageMetadata"] = node["usageMetadata"]!.DeepClone(),
            ["serverContent"] = node["serverContent"]!.DeepClone()
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        usage.ObserveProvider(duplicate);
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(150, usage.Snapshot().TotalTokens);
    }

    [Fact]
    public void Unprompted_new_content_after_completion_cannot_certify_an_extra_charge()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(SecondFinal);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void New_input_racing_an_old_response_final_is_not_certified()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveClient(Question);
        usage.ObserveProvider(FirstFinal);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void Tool_response_mid_generation_does_not_infer_full_chain_coverage()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider("""{"toolCall":{"functionCalls":[{"id":"test","name":"system_status","args":{}}]}}""");
        usage.ObserveProvider(FirstFinal.Replace("\"serverContent\":{\"turnComplete\":true},", ""));
        usage.ObserveClient("""{"toolResponse":{"functionResponses":[{"id":"test","name":"system_status","response":{"ok":true}}]}}""");
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(SecondFinal);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void New_tool_input_after_completion_is_an_uncovered_tail()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveClient("""{"toolResponse":{"functionResponses":[{"id":"test","name":"system_status","response":{"ok":true}}]}}""");
        Assert.False(usage.Snapshot().Known);
        Assert.Equal(150, usage.Snapshot().TotalTokens);
    }

    [Fact]
    public void Interrupted_second_turn_stays_uncertain_even_if_it_reports_usage()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Complete(usage, FirstFinal);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(SecondFinal.Replace("\"turnComplete\":true", "\"interrupted\":true,\"turnComplete\":true"));
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public async Task Concurrent_snapshots_never_mix_two_response_vectors()
    {
        var usage = new LiveUsageAccumulator(10_000_000);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) Complete(usage, i % 2 == 0 ? FirstFinal : SecondFinal);
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var snapshot = usage.Snapshot();
                Assert.Equal(snapshot.InputTokens + snapshot.OutputTokens + snapshot.ReasoningTokens, snapshot.TotalTokens);
                Assert.Equal(snapshot.InputAudioTokens + snapshot.OutputAudioTokens, snapshot.AudioTokens);
            }
        });
        await Task.WhenAll(writer, reader);
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(25 * 335, usage.Snapshot().TotalTokens);
    }

    private static void Complete(LiveUsageAccumulator usage, string final)
    {
        usage.ObserveClient(Question);
        usage.ObserveProvider(Audio);
        usage.ObserveProvider(final);
    }
}
