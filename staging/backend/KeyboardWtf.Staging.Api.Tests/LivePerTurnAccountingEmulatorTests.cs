using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class LivePerTurnAccountingEmulatorTests
{
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"arithmetic"}]}],"turnComplete":true}}""";
    private const string Output = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAAA"}}]}}}""";
    private const string FirstFinal = """{"serverContent":{"turnComplete":true},"usageMetadata":{"promptTokenCount":92,"candidatesTokenCount":58,"totalTokenCount":150,"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":52}]}}""";
    private const string SecondFinal = """{"serverContent":{"turnComplete":true},"usageMetadata":{"promptTokenCount":162,"candidatesTokenCount":23,"totalTokenCount":185,"promptTokensDetails":[{"modality":"TEXT","tokenCount":110},{"modality":"AUDIO","tokenCount":52}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":1},{"modality":"AUDIO","tokenCount":22}]}}""";

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Coherent_turn_aggregate_checkpoints_and_settlement_retry_have_one_financial_effect()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        const string id = "local-per-turn";
        await store.ReserveAsync(caller, id, "synthetic-model", "synthetic-pricing", 250_000, 500_000,
            100, 1, 1_000_000_000, CancellationToken.None, "live_audio");
        await store.MarkReservationProviderStartedAsync(caller.UserId, id, CancellationToken.None);
        var catalog = new CatalogService(db, Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }), new SystemClock());
        var pricing = new PricingSnapshot("frozen-rates-local", "gemini-live-2.5-flash-native-audio",
            500_000, 2_000_000, 0, 2_000_000, 10_000, 10_000, DateTimeOffset.Parse("2026-10-06T00:00:00Z"));
        var accumulator = new LiveUsageAccumulator(1_000_000);
        Complete(accumulator, FirstFinal);
        var partial = accumulator.Snapshot();
        var firstCost = catalog.CalculateLiveActualCost(pricing, partial, 10_000);
        await store.CheckpointReservationUsageAsync(caller.UserId, id, partial, firstCost.ChargeCreditMicros,
            firstCost.ProviderCostUsdMicros, CancellationToken.None);
        Complete(accumulator, SecondFinal);
        var aggregate = accumulator.Snapshot();
        Assert.True(aggregate.Known);
        Assert.Equal(335, aggregate.TotalTokens);
        var cost = catalog.CalculateLiveActualCost(pricing, aggregate, 10_000);
        Assert.Equal(1_159, cost.ProviderCostUsdMicros);
        Assert.Equal(120_000, cost.ChargeCreditMicros);
        for (var i = 0; i < 2; i++)
            await store.CheckpointReservationUsageAsync(caller.UserId, id, aggregate, cost.ChargeCreditMicros,
                cost.ProviderCostUsdMicros, CancellationToken.None);
        Assert.Equal(0, (await store.GetAccountAsync(caller.UserId, CancellationToken.None)).CycleUsageCreditMicros);
        for (var i = 0; i < 2; i++)
            await store.RecordProviderCompletionAsync(caller.UserId, id, aggregate, cost.ChargeCreditMicros,
                cost.ProviderCostUsdMicros, "completed", CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.SettleAsync(caller.UserId,
            id, aggregate, cost.ChargeCreditMicros, cost.ProviderCostUsdMicros, "completed", CancellationToken.None)));
        Assert.Single(results, result => result.Settled);
        var account = await store.GetAccountAsync(caller.UserId, CancellationToken.None);
        Assert.Equal(880_000, account.AvailableCreditMicros);
        Assert.Equal(0, account.ReservedCreditMicros);
        Assert.Equal(120_000, account.CycleUsageCreditMicros);
        var accountRef = db.Collection("accounts").Document(caller.UserId);
        Assert.Single((await accountRef.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Single((await accountRef.Collection("ledger").WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
        Assert.Equal(0, (await accountRef.GetSnapshotAsync()).GetValue<long>("activeLiveRequests"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Uncovered_next_turn_cannot_be_settled_and_releases_only_its_active_slot()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        const string id = "local-uncovered-turn";
        await store.ReserveAsync(caller, id, "synthetic-model", "synthetic-pricing", 250_000, 500_000,
            100, 1, 1_000_000_000, CancellationToken.None, "live_audio");
        await store.MarkReservationProviderStartedAsync(caller.UserId, id, CancellationToken.None);
        var accumulator = new LiveUsageAccumulator(1_000_000);
        Complete(accumulator, FirstFinal);
        accumulator.ObserveClient(Question);
        accumulator.ObserveProvider(Output);
        var uncertain = accumulator.Snapshot();
        Assert.False(uncertain.Known);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordProviderCompletionAsync(caller.UserId,
            id, uncertain, 120_000, 1_159, "completed", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SettleAsync(caller.UserId,
            id, uncertain, 120_000, 1_159, "completed", CancellationToken.None));
        await store.MarkReservationForReconciliationAsync(caller.UserId, id, "local_missing_final_usage", CancellationToken.None);
        var accountRef = db.Collection("accounts").Document(caller.UserId);
        var account = await accountRef.GetSnapshotAsync();
        Assert.Equal(750_000, account.GetValue<long>("availableCreditMicros"));
        Assert.Equal(0, account.GetValue<long>("cycleUsageCreditMicros"));
        Assert.Equal(0, account.GetValue<long>("activeRequests"));
        Assert.Equal(0, account.GetValue<long>("activeLiveRequests"));
        Assert.Equal(250_000, account.GetValue<long>("reservedCreditMicros"));
        var reservation = await accountRef.Collection("reservations").Document(id).GetSnapshotAsync();
        Assert.Equal("reconciliation_pending", reservation.GetValue<string>("status"));
        Assert.False(reservation.TryGetValue<bool>("providerCompletionRecorded", out var completed) && completed);
        Assert.Empty((await accountRef.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Empty((await accountRef.Collection("ledger").WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
    }

    private static void Complete(LiveUsageAccumulator usage, string final)
    {
        usage.ObserveClient(Question);
        usage.ObserveProvider(Output);
        usage.ObserveProvider(final);
    }
}
