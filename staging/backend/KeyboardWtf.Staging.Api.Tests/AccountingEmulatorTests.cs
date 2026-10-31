using EmulatorDetection = Google.Api.Gax.EmulatorDetection;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Unlike legacy opt-in cloud tests, an unconfigured emulator is explicitly
// SKIPPED, never reported as a successful transaction test.
public sealed class LocalFirestoreFactAttribute : FactAttribute
{
    public LocalFirestoreFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST")))
            Skip = "Local Firestore emulator is required; no cloud fallback is allowed.";
    }
}

public sealed class LocalFirestoreTheoryAttribute : TheoryAttribute
{
    public LocalFirestoreTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST")))
            Skip = "Local Firestore emulator is required; no cloud fallback is allowed.";
    }
}

[CollectionDefinition("Local accounting emulator", DisableParallelization = true)]
public sealed class LocalAccountingEmulatorCollection { }

[Collection("Local accounting emulator")]
public sealed class AccountingEmulatorTests
{
    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Unknown_usage_cannot_commit_and_settled_receipt_still_wins_on_retry()
    {
        var (db, store, caller) = await CreateAccountAsync();
        await ReserveAsync(store, caller, "unknown");
        await store.MarkReservationProviderStartedAsync(caller.UserId, "unknown", CancellationToken.None);
        var unknown = new ProviderUsage(10, 5, 0, 0, 0, 0, 15, "test", Known: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordProviderCompletionAsync(
            caller.UserId, "unknown", unknown, 10, 123, "completed", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SettleAsync(
            caller.UserId, "unknown", unknown, 10, 123, "completed", CancellationToken.None));
        var known = unknown with { Known = true };
        await store.RecordProviderCompletionAsync(caller.UserId, "unknown", known, 10, 123, "completed", CancellationToken.None);
        // Old records lack the additive marker, but still hold coherent exact evidence.
        await db.Collection("accounts").Document(caller.UserId).Collection("reservations").Document("unknown")
            .UpdateAsync("completedKnown", FieldValue.Delete);
        await store.SettleAsync(caller.UserId, "unknown", known, 10, 123, "completed", CancellationToken.None);
        var retry = await store.SettleAsync(caller.UserId, "unknown", unknown with { InputTokens = -100 }, 999, 999,
            "conflicting retry", CancellationToken.None);
        Assert.False(retry.Settled);
        Assert.Equal(10, retry.ChargedCreditMicros);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Legacy_estimated_completion_quarantines_funds_and_releases_only_its_slot()
    {
        var (db, store, caller) = await CreateAccountAsync();
        await ReserveAsync(store, caller, "legacy-unknown");
        await store.MarkReservationProviderStartedAsync(caller.UserId, "legacy-unknown", CancellationToken.None);
        await store.RecordProviderCompletionAsync(caller.UserId, "legacy-unknown",
            new ProviderUsage(10, 5, 0, 0, 0, 0, 15, "test"), 10, 123, "completed", CancellationToken.None);
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = account.Collection("reservations").Document("legacy-unknown");
        await reservation.UpdateAsync(new Dictionary<string, object>
        {
            ["completedKnown"] = FieldValue.Delete, ["completedEstimated"] = true,
        });
        var service = new UsageReconciliationService(db, store, new SystemClock(),
            Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }), NullLogger<UsageReconciliationService>.Instance);
        async Task ReconcileAsync()
        {
            var task = (Task)typeof(UsageReconciliationService).GetMethod("ReconcileReservationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [await reservation.GetSnapshotAsync(), "synthetic-local-run", CancellationToken.None])!;
            await task;
        }
        await ReconcileAsync();
        Assert.Equal(0, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        Assert.Equal(100_000, (await account.GetSnapshotAsync()).GetValue<long>("reservedCreditMicros"));
        Assert.Equal("reconciliation_pending", (await reservation.GetSnapshotAsync()).GetValue<string>("status"));
        await ReserveAsync(store, caller, "next");
        await ReconcileAsync();
        Assert.Equal(1, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        Assert.Empty((await account.Collection("usage").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Completion_ack_loss_keeps_exact_evidence_settleable_and_releases_only_its_slot()
    {
        var (db, store, caller) = await CreateAccountAsync();
        await ReserveAsync(store, caller, "completion-ack-loss");
        await store.MarkReservationProviderStartedAsync(caller.UserId, "completion-ack-loss", CancellationToken.None);
        await store.RecordProviderCompletionAsync(caller.UserId, "completion-ack-loss",
            new ProviderUsage(10, 5, 0, 0, 0, 0, 15, "test"), 10, 123, "completed", CancellationToken.None);
        await store.MarkReservationForReconciliationAsync(caller.UserId, "completion-ack-loss",
            "provider_completion_evidence_write_failed", CancellationToken.None);
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = account.Collection("reservations").Document("completion-ack-loss");
        Assert.Equal("provider_completed", (await reservation.GetSnapshotAsync()).GetValue<string>("status"));
        Assert.Equal(0, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        await ReserveAsync(store, caller, "subsequent");
        var service = new UsageReconciliationService(db, store, new SystemClock(),
            Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }), NullLogger<UsageReconciliationService>.Instance);
        for (var i = 0; i < 2; i++)
        {
            var task = (Task)typeof(UsageReconciliationService).GetMethod("ReconcileReservationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                // Each real maintenance run creates a unique run ID. Keep its
                // resolution audit identity distinct while retrying settlement.
                .Invoke(service, [await reservation.GetSnapshotAsync(), "synthetic-ack-loss-" + i, CancellationToken.None])!;
            await task;
        }
        Assert.Equal(1, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        Assert.Single((await account.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Single((await account.Collection("ledger").WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
        Assert.Equal(10, (await reservation.GetSnapshotAsync()).GetValue<long>("chargedCreditMicros"));
    }
    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Settled_retry_returns_authoritative_receipt_without_another_financial_effect()
    {
        var (db, store, caller) = await CreateAccountAsync();
        const string requestId = "settlement-retry";
        await ReserveAsync(store, caller, requestId);
        await store.MarkReservationProviderStartedAsync(caller.UserId, requestId, CancellationToken.None);
        var usage = new ProviderUsage(20, 5, 0, 0, 0, 0, 25, "synthetic-provider");
        await store.RecordProviderCompletionAsync(caller.UserId, requestId, usage, 10, 123, "completed", CancellationToken.None);
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = await account.Collection("reservations").Document(requestId).GetSnapshotAsync();
        var global = db.Collection("globalUsageShards").Document(reservation.GetValue<string>("globalUsageShardId"));
        var before = await global.GetSnapshotAsync();
        var costBefore = before.ContainsField("providerCostUsdMicros") ? before.GetValue<long>("providerCostUsdMicros") : 0;
        var countBefore = before.ContainsField("providerCostSettlementCount") ? before.GetValue<long>("providerCostSettlementCount") : 0;

        // Discard the first response, as with acknowledgement loss after commit.
        await store.SettleAsync(caller.UserId, requestId, usage, 10, 123, "completed", CancellationToken.None);
        var retries = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            store.SettleAsync(caller.UserId, requestId, usage, 10, 123, "completed", CancellationToken.None)));
        Assert.All(retries, result =>
        {
            Assert.False(result.Settled);
            Assert.Equal(10, result.ChargedCreditMicros);
            Assert.Equal(99_990, result.ReleasedCreditMicros);
            Assert.Equal(999_990, result.Account.AvailableCreditMicros);
            Assert.Equal(0, result.Account.ReservedCreditMicros);
        });
        Assert.Single((await account.Collection("ledger").WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
        Assert.Single((await account.Collection("usage").GetSnapshotAsync()).Documents);
        var after = await global.GetSnapshotAsync();
        Assert.Equal(costBefore + 123, after.GetValue<long>("providerCostUsdMicros"));
        Assert.Equal(countBefore + 1, after.GetValue<long>("providerCostSettlementCount"));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task First_settlement_still_requires_exact_durable_provider_evidence()
    {
        var (_, store, caller) = await CreateAccountAsync();
        const string requestId = "settlement-evidence";
        await ReserveAsync(store, caller, requestId);
        var usage = new ProviderUsage(20, 5, 0, 0, 0, 0, 25, "synthetic-provider");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SettleAsync(caller.UserId, requestId, usage, 10, 123, "completed", CancellationToken.None));
        await store.MarkReservationProviderStartedAsync(caller.UserId, requestId, CancellationToken.None);
        await store.RecordProviderCompletionAsync(caller.UserId, requestId, usage, 10, 123, "completed", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SettleAsync(caller.UserId, requestId, usage, 11, 123, "completed", CancellationToken.None));
        Assert.Equal(100_000, (await store.GetAccountAsync(caller.UserId, CancellationToken.None)).ReservedCreditMicros);
        Assert.True((await store.SettleAsync(caller.UserId, requestId, usage, 10, 123, "completed", CancellationToken.None)).Settled);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Reconciled_request_cannot_release_a_later_requests_concurrency_slot(bool live, bool legacy, bool settle)
    {
        var (db, store, caller) = await CreateAccountAsync();
        var activity = live ? "live_audio" : "ai_request";
        var account = db.Collection("accounts").Document(caller.UserId);
        var first = account.Collection("reservations").Document("first");
        await ReserveAsync(store, caller, "first", activity);
        await store.MarkReservationProviderStartedAsync(caller.UserId, "first", CancellationToken.None);
        await store.MarkReservationForReconciliationAsync(caller.UserId, "first", "provider_unknown", CancellationToken.None);
        await store.MarkReservationForReconciliationAsync(caller.UserId, "first", "provider_unknown", CancellationToken.None);
        Assert.Equal(0, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        await ReserveAsync(store, caller, "second", activity);
        var usage = new ProviderUsage(20, 5, 0, 0, 0, 0, 25, "synthetic-provider");
        if (settle)
            await store.RecordProviderCompletionAsync(caller.UserId, "first", usage, 10, 123, "completed", CancellationToken.None);
        if (legacy)
            await first.UpdateAsync("concurrencyReleased", FieldValue.Delete);
        if (settle)
        {
            await store.SettleAsync(caller.UserId, "first", usage, 10, 123, "completed", CancellationToken.None);
            await store.SettleAsync(caller.UserId, "first", usage, 10, 123, "completed", CancellationToken.None);
        }
        else
        {
            await store.RecordTerminalProviderRejectionAsync(caller.UserId, "first", 503, CancellationToken.None);
            await store.ReleaseReservationAsync(caller.UserId, "first", "provider_rejected", CancellationToken.None);
            await store.ReleaseReservationAsync(caller.UserId, "first", "provider_rejected", CancellationToken.None);
        }
        var state = await account.GetSnapshotAsync();
        Assert.Equal(1, state.GetValue<long>("activeRequests"));
        Assert.Equal(live ? 1 : 0, state.GetValue<long>("activeLiveRequests"));
        Assert.True((await first.GetSnapshotAsync()).GetValue<bool>("concurrencyReleased"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReserveAsync(store, caller, "third", activity));
        await store.ReleaseReservationAsync(caller.UserId, "second", "cancelled_before_dispatch", CancellationToken.None);
        Assert.Equal(0, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Legacy_unreconciled_reservation_still_releases_its_own_slot(bool live, bool settle)
    {
        var (db, store, caller) = await CreateAccountAsync();
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = account.Collection("reservations").Document("legacy-normal");
        await ReserveAsync(store, caller, "legacy-normal", live ? "live_audio" : "ai_request");
        if (settle)
        {
            var usage = new ProviderUsage(20, 5, 0, 0, 0, 0, 25, "synthetic-provider");
            await store.MarkReservationProviderStartedAsync(caller.UserId, "legacy-normal", CancellationToken.None);
            await store.RecordProviderCompletionAsync(caller.UserId, "legacy-normal", usage, 10, 123, "completed", CancellationToken.None);
            await reservation.UpdateAsync("concurrencyReleased", FieldValue.Delete);
            await store.SettleAsync(caller.UserId, "legacy-normal", usage, 10, 123, "completed", CancellationToken.None);
        }
        else
        {
            await reservation.UpdateAsync("concurrencyReleased", FieldValue.Delete);
            await store.ReleaseReservationAsync(caller.UserId, "legacy-normal", "cancelled_before_dispatch", CancellationToken.None);
        }
        var state = await account.GetSnapshotAsync();
        Assert.Equal(0, state.GetValue<long>("activeRequests"));
        Assert.Equal(0, state.GetValue<long>("activeLiveRequests"));
        Assert.True((await reservation.GetSnapshotAsync()).GetValue<bool>("concurrencyReleased"));
    }

    internal static async Task<(FirestoreDb Db, FirestoreAccountStore Store, CallerIdentity Caller)> CreateAccountAsync()
    {
        var host = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST")!;
        var endpoint = new Uri("http://" + host);
        Assert.Equal("127.0.0.1", endpoint.Host);
        Assert.Empty(endpoint.UserInfo);
        Assert.Equal("/", endpoint.AbsolutePath);
        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-release-local",
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
        }.Build();
        var store = new FirestoreAccountStore(db, new SystemClock(), Options.Create(new PaidSystemOptions
        {
            ProjectId = db.ProjectId,
            GlobalUsageShardCount = 1,
        }));
        var id = "local-accounting-" + Guid.NewGuid().ToString("N");
        var caller = new CallerIdentity(id, id + "@example.invalid", "Local accounting test", "test");
        await store.EnsureAccountAsync(caller, CancellationToken.None);
        await store.GrantCreditsAsync(id, 1_000_000, "synthetic local allowance", id, "emulator-test", CancellationToken.None);
        return (db, store, caller);
    }

    private static Task<ReservationResult> ReserveAsync(FirestoreAccountStore store, CallerIdentity caller,
        string requestId, string activity = "ai_request") =>
        store.ReserveAsync(caller, requestId, "synthetic-model", "synthetic-pricing", 100_000, 500_000,
            100, 1, 1_000_000_000, CancellationToken.None, activity);
}
