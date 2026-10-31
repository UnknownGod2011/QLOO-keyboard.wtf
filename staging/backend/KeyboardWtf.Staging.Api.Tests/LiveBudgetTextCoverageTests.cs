using System.Text.Json;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class LiveBudgetTextCoverageTests
{
    private static readonly PricingSnapshot Pricing = new("fixture", "fixture", 300_000, 2_500_000,
        30_000, 2_500_000, 10_000, 10_000, DateTimeOffset.UnixEpoch, 1_000_000,
        500_000, 3_000_000, 3_000_000, 2_000_000, 12_000_000);
    private readonly CatalogService _catalog = new(null!, Options.Create(new PaidSystemOptions()), new Clock());
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"test"}]}],"turnComplete":true}}""";
    private const string Output = """{"serverContent":{"modelTurn":{"parts":[{"text":"OK"}]}}}""";

    [Fact]
    public void Complete_response_covers_setup_text_by_response_identity_not_character_to_token_ratio()
    {
        for (var cycle = 0; cycle < 50; cycle++)
        {
            var usage = new LiveUsageAccumulator(1_000_000);
            usage.ObserveClient(JsonSerializer.Serialize(new { setup = new {
                systemInstruction = new { parts = new[] { new { text = new string('a', 18_000) } } }
            } }));
            usage.ObserveClient(Question);
            usage.ObserveProvider(Output);
            usage.ObserveProvider(Final(4_500));
            var exact = usage.Snapshot();
            Assert.True(exact.Known);
            Assert.Equal(4_500, exact.InputTokens);
            Assert.Equal(0, usage.BudgetView().Unreported.InputTokens);
            Assert.Equal(_catalog.CalculateLiveActualCost(Pricing, exact, 12_500).ChargeCreditMicros,
                LiveBudgetProjection.Charge(_catalog, Pricing, usage.BudgetView(), 12_500));
            Assert.Equal(exact, usage.Snapshot());
        }
    }

    [Fact]
    public void Partial_receipt_does_not_acknowledge_all_setup_text()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(JsonSerializer.Serialize(new { setup = new { text = new string('a', 18_000) } }));
        usage.ObserveClient(Question);
        usage.ObserveProvider(Output);
        usage.ObserveProvider(Final(4_500).Replace("\"turnComplete\":true", "\"turnComplete\":false"));
        Assert.True(usage.BudgetView().Unreported.InputTokens > 0);
    }

    [Fact]
    public void New_text_after_response_started_is_not_erased_by_an_earlier_response()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Output);
        usage.ObserveClient(JsonSerializer.Serialize(new { toolResponse = new {
            functionResponses = new[] { new { id = "new", name = "read", response = new { text = new string('x', 18_000) } } }
        } }));
        usage.ObserveProvider(Final(10));
        Assert.True(usage.BudgetView().Unreported.InputTokens > 17_000);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void Terminal_without_its_own_receipt_does_not_clear_the_conservative_tail()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(JsonSerializer.Serialize(new { setup = new { text = new string('a', 18_000) } }));
        usage.ObserveClient(Question);
        usage.ObserveProvider(Output);
        usage.ObserveProvider(Final(4_500).Replace("\"turnComplete\":true", "\"turnComplete\":false"));
        var before = usage.BudgetView();
        usage.ObserveProvider("""{"serverContent":{"turnComplete":true}}""");
        Assert.Equal(before, usage.BudgetView());
    }

    [Fact]
    public void Acknowledged_text_does_not_erase_continuous_microphone_audio_tail()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(JsonSerializer.Serialize(new { setup = new { text = new string('a', 18_000) } }));
        usage.ObserveClient(Question);
        usage.ObserveProvider(Output);
        usage.ObserveClient(JsonSerializer.Serialize(new { realtimeInput = new { audio = new {
            data = Convert.ToBase64String(new byte[32_000])
        } } }));
        usage.ObserveProvider(Final(4_500));
        Assert.Equal(25, usage.BudgetView().Unreported.InputAudioTokens);
        Assert.Equal(25, usage.BudgetView().Unreported.InputTokens);
    }

    [Fact]
    public void Real_reported_cost_still_reaches_the_existing_ten_credit_cap()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        usage.ObserveProvider(Output);
        usage.ObserveProvider(Final(250_000));
        Assert.True(LiveBudgetProjection.Charge(_catalog, Pricing, usage.BudgetView(), 12_500) >= 10_000_000);
        Assert.True(usage.Snapshot().Known);
        Assert.Equal(250_000, usage.Snapshot().InputTokens);
    }

    private static string Final(int input) => JsonSerializer.Serialize(new {
        serverContent = new { turnComplete = true },
        usageMetadata = new {
            promptTokenCount = input, candidatesTokenCount = 2, totalTokenCount = input + 2,
            promptTokensDetails = new[] { new { modality = "TEXT", tokenCount = input } },
            candidatesTokensDetails = new[] { new { modality = "TEXT", tokenCount = 2 } }
        }
    });
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}
