using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ProviderUsageBoundaryTests
{
    internal static ProviderUsage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var response = new HttpResponseMessage();
        return (ProviderUsage)typeof(VertexAiClient).GetMethod("ExtractUsage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [document.RootElement, response.Headers])!;
    }

    private static bool Known(ProviderUsage usage) =>
        (bool?)typeof(ProviderUsage).GetProperty("Known")?.GetValue(usage) ?? true;

    public static TheoryData<string> Unknown => new()
    {
        "{}", "{\"usageMetadata\":null}", "{\"usageMetadata\":{}}", "{\"usageMetadata\":[]}",
        "{\"usageMetadata\":{\"promptTokenCount\":-1,\"candidatesTokenCount\":1,\"totalTokenCount\":0}}",
        "{\"usageMetadata\":{\"promptTokenCount\":1.5,\"totalTokenCount\":2}}",
        "{\"usageMetadata\":{\"promptTokenCount\":\"10\",\"totalTokenCount\":10}}",
        "{\"usageMetadata\":{\"promptTokenCount\":9223372036854775807,\"candidatesTokenCount\":1,\"totalTokenCount\":9223372036854775807}}",
        "{\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":2,\"totalTokenCount\":99}}",
        "{\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":2,\"totalTokenCount\":12,\"cachedContentTokenCount\":11}}",
        "{\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":2,\"totalTokenCount\":12,\"promptTokensDetails\":[{\"modality\":\"AUDIO\",\"tokenCount\":11}]}}",
        "{\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":2,\"totalTokenCount\":12,\"promptTokensDetails\":[{\"modality\":4,\"tokenCount\":1}]}}",
        "{\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":2,\"totalTokenCount\":12,\"candidatesTokensDetails\":{}}}",
        "{\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":2,\"totalTokenCount\":12,\"cachedContentTokenCount\":6,\"promptTokensDetails\":[{\"modality\":\"AUDIO\",\"tokenCount\":5}]}}",
        "{\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":2,\"totalTokenCount\":13,\"toolUsePromptTokenCount\":1}}",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"result\"}]}}],\"usageMetadata\":{\"promptTokenCount\":10,\"totalTokenCount\":10}}",
    };

    [Theory]
    [MemberData(nameof(Unknown))]
    public void Missing_or_invalid_usage_is_unknown_not_measured_zero(string json) => Assert.False(Known(Parse(json)));

    [Fact]
    public void Explicit_coherent_zero_and_blocked_output_are_known()
    {
        var zero = Parse("""{"usageMetadata":{"promptTokenCount":0,"candidatesTokenCount":0,"totalTokenCount":0}}""");
        Assert.True(Known(zero));
        Assert.Equal(0, zero.TotalTokens);
        var blocked = Parse("""{"promptFeedback":{"blockReason":"SAFETY"},"usageMetadata":{"promptTokenCount":10,"totalTokenCount":10}}""");
        Assert.True(Known(blocked));
        Assert.Equal(10, blocked.InputTokens);
        Assert.Equal(0, blocked.OutputTokens);
        var thoughts = Parse("""{"candidates":[{"content":{"parts":[{"thought":true,"text":"internal reasoning"}]}}],"usageMetadata":{"promptTokenCount":10,"thoughtsTokenCount":3,"totalTokenCount":13}}""");
        Assert.True(Known(thoughts));
        Assert.Equal(3, thoughts.ReasoningTokens);
    }

    [Fact]
    public void Cache_reasoning_and_modalities_preserve_exact_provider_counts()
    {
        for (var i = 1; i <= 50; i++)
        {
            var usage = Parse(JsonSerializer.Serialize(new
            {
                responseId = "synthetic-" + i,
                usageMetadata = new
                {
                    promptTokenCount = 100 * i, candidatesTokenCount = 20 * i,
                    thoughtsTokenCount = 3 * i, totalTokenCount = 123 * i,
                    cachedContentTokenCount = 10 * i,
                    promptTokensDetails = new[] { new { modality = "AUDIO", tokenCount = 5 * i } },
                },
            }));
            Assert.True(Known(usage));
            Assert.False(usage.Estimated);
            Assert.Equal(100 * i, usage.InputTokens);
            Assert.Equal(20 * i, usage.OutputTokens);
            Assert.Equal(3 * i, usage.ReasoningTokens);
            Assert.Equal(10 * i, usage.CachedInputTokens);
            Assert.Equal(5 * i, usage.InputAudioTokens);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"promptTokenCount\":-1,\"totalTokenCount\":10}")]
    public void Malformed_live_metadata_does_not_disable_budget_estimates_or_crash(string metadata)
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveClient("""{"clientContent":{"turns":[{"parts":[{"text":"12345678"}]}]}}""");
        accumulator.ObserveProvider("{\"usageMetadata\":" + metadata + "}");
        var result = accumulator.Snapshot();
        Assert.True(result.Estimated);
        Assert.False(Known(result));
        Assert.Equal(2, result.InputTokens);
    }

    [Fact]
    public void Invalid_snapshot_cannot_replace_valid_cumulative_live_usage()
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":999,"totalTokenCount":1}}""");
        accumulator.ObserveProvider("""{"usageMetadata":{}}""");
        var result = accumulator.Snapshot();
        Assert.True(Known(result));
        Assert.False(result.Estimated);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(15, result.TotalTokens);
    }

    [Fact]
    public void Unreported_live_output_tail_is_unknown_until_a_newer_valid_snapshot()
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        accumulator.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"text":"additional output"}]}}}""");
        Assert.False(Known(accumulator.Snapshot()));
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        Assert.False(Known(accumulator.Snapshot())); // Duplicate cannot prove the later tail.
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":10,"totalTokenCount":20}}""");
        Assert.True(Known(accumulator.Snapshot()));
    }

    [Theory]
    [InlineData("{\"toolCall\":{\"functionCalls\":[{\"id\":\"call-1\",\"name\":\"open_url\",\"args\":{}}]}}")]
    [InlineData("{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"text\":\"answer\"}]}}}")]
    public void Later_input_counts_cannot_cover_an_unreported_output_tail(string message)
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        accumulator.ObserveProvider(message);
        Assert.False(Known(accumulator.Snapshot()));
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":5,"totalTokenCount":25}}""");
        Assert.False(Known(accumulator.Snapshot()));
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":10,"totalTokenCount":30}}""");
        Assert.True(Known(accumulator.Snapshot()));
    }

    [Theory]
    [InlineData("{\"clientContent\":{\"turns\":[{\"role\":\"user\",\"parts\":[{\"text\":\"next question\"}]}]}}")]
    [InlineData("{\"realtimeInput\":{\"text\":\"next question\"}}")]
    [InlineData("{\"toolResponse\":{\"functionResponses\":[{\"id\":\"call-1\",\"name\":\"open_url\",\"response\":{\"ok\":true}}]}}")]
    public void Later_output_counts_cannot_cover_an_unreported_semantic_input_tail(string message)
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        accumulator.ObserveClient(message);
        Assert.False(Known(accumulator.Snapshot()));
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":10,"totalTokenCount":20}}""");
        Assert.False(Known(accumulator.Snapshot()));
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":10,"totalTokenCount":30}}""");
        Assert.True(Known(accumulator.Snapshot()));
    }

    [Fact]
    public void Delayed_audio_transcriptions_are_not_new_billable_content()
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveClient("""{"realtimeInput":{"audio":{"data":"AQID"}}}""");
        accumulator.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AQID"}}]}},"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15,"promptTokensDetails":[{"modality":"AUDIO","tokenCount":10}],"candidatesTokensDetails":[{"modality":"AUDIO","tokenCount":5}]}}""");
        Assert.True(Known(accumulator.Snapshot()));
        accumulator.ObserveProvider("""{"serverContent":{"inputTranscription":{"text":"hello"},"outputTranscription":{"text":"answer"}}}""");
        accumulator.ObserveProvider("""{"serverContent":{"turnComplete":true}}""");
        Assert.True(Known(accumulator.Snapshot()));
        Assert.Equal(15, accumulator.Snapshot().TotalTokens);
        accumulator.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AQID"}}]}}}""");
        accumulator.ObserveProvider("""{"serverContent":{"outputTranscription":{"text":"later output"}}}""");
        Assert.False(Known(accumulator.Snapshot()));
    }

    [Theory]
    [InlineData("{\"toolCall\":{\"functionCalls\":[{\"id\":\"call-1\",\"name\":\"open_url\",\"args\":{}}]}}")]
    [InlineData("{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"text\":\"answer\"}]}}}")]
    public void Reasoning_only_growth_cannot_cover_new_visible_output(string message)
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        accumulator.ObserveProvider(message);
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"thoughtsTokenCount":3,"totalTokenCount":18}}""");
        Assert.False(Known(accumulator.Snapshot()));
        accumulator.ObserveProvider("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":10,"thoughtsTokenCount":3,"totalTokenCount":23}}""");
        Assert.True(Known(accumulator.Snapshot()));
    }

    [Fact]
    public void Live_tool_usage_is_exact_input_but_one_shot_without_tools_rejects_it()
    {
        const string metadata = """{"promptTokenCount":10,"candidatesTokenCount":5,"toolUsePromptTokenCount":3,"totalTokenCount":18,"toolUsePromptTokensDetails":[{"modality":"TEXT","tokenCount":3}]}""";
        Assert.False(Known(Parse("{\"usageMetadata\":" + metadata + "}")));
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("{\"usageMetadata\":" + metadata + "}");
        var usage = accumulator.Snapshot();
        Assert.True(Known(usage));
        Assert.Equal(13, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(18, usage.TotalTokens);
    }

    [Theory]
    [InlineData("{\"promptTokenCount\":10,\"candidatesTokenCount\":5,\"toolUsePromptTokenCount\":3,\"totalTokenCount\":18}")]
    [InlineData("{\"promptTokenCount\":10,\"candidatesTokenCount\":5,\"toolUsePromptTokenCount\":3,\"totalTokenCount\":18,\"toolUsePromptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":1}]}")]
    [InlineData("{\"promptTokenCount\":10,\"candidatesTokenCount\":5,\"toolUsePromptTokenCount\":3,\"totalTokenCount\":18,\"toolUsePromptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":4}]}")]
    [InlineData("{\"promptTokenCount\":10,\"candidatesTokenCount\":5,\"toolUsePromptTokenCount\":3,\"totalTokenCount\":18,\"toolUsePromptTokensDetails\":[{\"modality\":\"FUTURE\",\"tokenCount\":3}]}")]
    [InlineData("{\"promptTokenCount\":10,\"cachedContentTokenCount\":11,\"candidatesTokenCount\":5,\"toolUsePromptTokenCount\":3,\"totalTokenCount\":18,\"toolUsePromptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":3}]}")]
    [InlineData("{\"promptTokenCount\":9223372036854775807,\"toolUsePromptTokenCount\":1,\"totalTokenCount\":9223372036854775807}")]
    public void Invalid_or_unclassified_tool_usage_is_not_authoritative(string metadata)
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("{\"usageMetadata\":" + metadata + "}");
        Assert.False(Known(accumulator.Snapshot()));
    }

    [Fact]
    public void Live_output_cannot_be_measured_zero_but_raw_microphone_frames_are_not_billable_evidence()
    {
        var accumulator = new LiveUsageAccumulator(1_000_000);
        accumulator.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"text":"answer"}]}},"usageMetadata":{"promptTokenCount":10,"totalTokenCount":10}}""");
        Assert.False(Known(accumulator.Snapshot()));
        var zero = new LiveUsageAccumulator(1_000_000);
        zero.ObserveProvider("""{"usageMetadata":{"promptTokenCount":0,"totalTokenCount":0}}""");
        Assert.True(Known(zero.Snapshot()));
        Assert.Equal(LiveAccountingDisposition.Settle, VertexLiveProxyService.DetermineAccountingDisposition(true, 0, true));
        Assert.Equal(LiveAccountingDisposition.Reconcile, VertexLiveProxyService.DetermineAccountingDisposition(true, 100, false));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Legacy_completed_usage_requires_coherent_fields_and_explicit_unknown_never_falls_back(bool explicitUnknown, bool broken, bool expected)
    {
        var data = new Dictionary<string, object>
        {
            ["completedInputTokens"] = 10L, ["completedOutputTokens"] = 5L, ["completedTotalTokens"] = 15L,
            ["completedCachedInputTokens"] = 0L, ["completedReasoningTokens"] = 0L,
            ["completedImageTokens"] = 0L, ["completedAudioTokens"] = 0L,
            ["completedInputAudioTokens"] = 0L, ["completedOutputAudioTokens"] = 0L,
            ["completedInputImageTokens"] = 0L, ["completedOutputImageTokens"] = 0L,
            ["completedEstimated"] = false,
        };
        if (explicitUnknown) data["completedKnown"] = false;
        if (broken) data.Remove("completedInputTokens");
        Assert.Equal(expected, ProviderUsageMetadata.ReadCompletion(data).Known);
    }
}
