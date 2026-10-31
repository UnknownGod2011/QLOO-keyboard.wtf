using KeyboardWtf.Staging.Api.Domain;

namespace KeyboardWtf.Staging.Api.Services;

// Guard-only estimates. These values must never become provider completion,
// settlement evidence or a customer invoice.
internal readonly record struct LiveBudgetView(ProviderUsage Floor, ProviderUsage Unreported);

internal static class LiveBudgetProjection
{
    internal static ProviderUsage Empty => new(0, 0, 0, 0, 0, 0, 0, null, Known: false);

    internal static long Charge(ICatalogService catalog, PricingSnapshot pricing,
        LiveBudgetView view, int safetyBasisPoints)
    {
        var floor = view.Floor with { Known = false };
        var unreported = view.Unreported with { Estimated = true, Known = false };
        if (floor.TotalTokens == 0 && unreported.TotalTokens == 0)
            return 0; // The catalog's minimum-one rounding is not evidence of work.
        var floorCharge = floor.TotalTokens == 0 ? 0
            : catalog.CalculateLiveActualCost(pricing, floor, safetyBasisPoints).ChargeCreditMicros;
        if (unreported.TotalTokens == 0)
            return floorCharge;
        var estimatedFloor = floor.TotalTokens == 0 ? 0
            : catalog.CalculateLiveActualCost(pricing, floor with { Estimated = true }, safetyBasisPoints).ChargeCreditMicros;
        var combined = Add(floor, unreported) with { Estimated = true, Known = false };
        var combinedCharge = catalog.CalculateLiveActualCost(pricing, combined, safetyBasisPoints).ChargeCreditMicros;
        // Safety applies to the incremental estimate, not a second time to
        // already reported work. Aggregate before pricing; never round per frame.
        return checked(floorCharge + Math.Max(0, combinedCharge - estimatedFloor));
    }

    internal static ProviderUsage Add(ProviderUsage left, ProviderUsage right) => new(
        checked(left.InputTokens + right.InputTokens), checked(left.OutputTokens + right.OutputTokens),
        checked(left.CachedInputTokens + right.CachedInputTokens), checked(left.ReasoningTokens + right.ReasoningTokens),
        checked(left.ImageTokens + right.ImageTokens), checked(left.AudioTokens + right.AudioTokens),
        checked(left.TotalTokens + right.TotalTokens), null, null,
        checked(left.InputAudioTokens + right.InputAudioTokens), checked(left.OutputAudioTokens + right.OutputAudioTokens),
        checked(left.InputImageTokens + right.InputImageTokens), checked(left.OutputImageTokens + right.OutputImageTokens),
        left.Estimated || right.Estimated, Known: false);

    internal static ProviderUsage ContextCarry(ProviderUsage latestResponse) => new(
        checked(latestResponse.InputTokens + latestResponse.OutputTokens + latestResponse.ReasoningTokens),
        0, 0, 0, checked(latestResponse.InputImageTokens + latestResponse.OutputImageTokens),
        checked(latestResponse.InputAudioTokens + latestResponse.OutputAudioTokens),
        checked(latestResponse.InputTokens + latestResponse.OutputTokens + latestResponse.ReasoningTokens),
        null, null, checked(latestResponse.InputAudioTokens + latestResponse.OutputAudioTokens), 0,
        checked(latestResponse.InputImageTokens + latestResponse.OutputImageTokens), 0, Estimated: true, Known: false);
}
