using EmulatorDetection = Google.Api.Gax.EmulatorDetection;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class DailySpendingCapsEmulatorTests
{
    [Fact]
    public void Daily_spending_caps_remain_enabled_by_default() =>
        Assert.True(new PaidSystemOptions().DailySpendingCapsEnabled);

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Disabled_daily_caps_preserve_initial_rolling_settlement_and_exact_ledger_tracking()
    {
        var f = await Fixture.CreateAsync(enabled: false, userCap: 10, globalCap: 20);
        const string id = "disabled-full-lifecycle";
        Assert.True((await f.ReserveAsync(id, 100_000)).Created);
        await AssertWalletAsync(f, 900_000, 100_000, 0);
        var markerBefore = await f.Marker.GetSnapshotAsync();
        Assert.Equal(20, markerBefore.GetValue<long>("globalDailyCapCreditMicros"));
        Assert.Equal(20, markerBefore.GetValue<long>("shardedDailyCapCreditMicros"));

        Assert.Equal(250_000, await f.Store.EnsureReservationCapacityAsync(
            f.Caller.UserId, id, 250_000, CancellationToken.None));
        // An identical extension has no second ledger or wallet effect.
        Assert.Equal(250_000, await f.Store.EnsureReservationCapacityAsync(
            f.Caller.UserId, id, 250_000, CancellationToken.None));
        await AssertWalletAsync(f, 750_000, 250_000, 0);
        var global = await f.GlobalAsync(id);
        await AssertCountersAsync(global, reserved: 250_000, settled: 0, providerCost: 0);
        await AssertCountersAsync(f.Daily, reserved: 250_000, settled: 0, providerCost: 0);
        Assert.Equal(20, (await global.GetSnapshotAsync()).GetValue<long>("dailyCapCreditMicros"));
        Assert.Equal(10, (await f.Daily.GetSnapshotAsync()).GetValue<long>("dailyCapCreditMicros"));
        var reservations = await f.Account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.UsageReservation).GetSnapshotAsync();
        Assert.Equal(2, reservations.Count);
        Assert.Equal(-250_000, reservations.Documents.Sum(row => row.GetValue<long>("availableDeltaMicros")));
        Assert.Equal(250_000, reservations.Documents.Sum(row => row.GetValue<long>("reservedDeltaMicros")));

        var usage = new ProviderUsage(10, 5, 0, 0, 0, 0, 15, "synthetic-provider");
        await f.Store.MarkReservationProviderStartedAsync(f.Caller.UserId, id, CancellationToken.None);
        await f.Store.RecordProviderCompletionAsync(f.Caller.UserId, id, usage, 75_000, 123,
            "completed", CancellationToken.None);
        Assert.True((await f.Store.SettleAsync(f.Caller.UserId, id, usage, 75_000, 123,
            "completed", CancellationToken.None)).Settled);
        Assert.False((await f.Store.SettleAsync(f.Caller.UserId, id, usage, 75_000, 123,
            "completed", CancellationToken.None)).Settled);
        await AssertWalletAsync(f, 925_000, 0, 75_000);
        await AssertCountersAsync(global, reserved: 0, settled: 75_000, providerCost: 123);
        await AssertCountersAsync(f.Daily, reserved: 0, settled: 75_000, providerCost: 123);
        var settlement = Assert.Single((await f.Account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
        Assert.Equal(175_000, settlement.GetValue<long>("availableDeltaMicros"));
        Assert.Equal(-250_000, settlement.GetValue<long>("reservedDeltaMicros"));
        Assert.Single((await f.Account.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Equal(4, (await f.Account.Collection("ledger").GetSnapshotAsync()).Count);
        Assert.Equal(0, (await f.Account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        Assert.Equal(0, (await f.Account.GetSnapshotAsync()).GetValue<long>("activeLiveRequests"));
        AssertMapEqual(markerBefore.ToDictionary(), (await f.Marker.GetSnapshotAsync()).ToDictionary());
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Disabling_daily_caps_reuses_existing_numeric_marker_without_migration_or_reset()
    {
        var f = await Fixture.CreateAsync(enabled: true, userCap: 50_000, globalCap: 50_000);
        await f.ReserveAsync("before-toggle", 25_000);
        await f.Store.ReleaseReservationAsync(f.Caller.UserId, "before-toggle", "cancelled_before_dispatch",
            CancellationToken.None);
        var markerBefore = (await f.Marker.GetSnapshotAsync()).ToDictionary();
        var disabled = f.StoreWith(enabled: false);
        Assert.True((await f.ReserveAsync("after-toggle", 100_000, disabled)).Created);
        Assert.Equal(250_000, await disabled.EnsureReservationCapacityAsync(
            f.Caller.UserId, "after-toggle", 250_000, CancellationToken.None));
        await AssertWalletAsync(f, 750_000, 250_000, 0);
        var global = await f.GlobalAsync("after-toggle");
        await AssertCountersAsync(global, 250_000, 0, 0);
        Assert.Equal(50_000, (await global.GetSnapshotAsync()).GetValue<long>("dailyCapCreditMicros"));
        AssertMapEqual(markerBefore, (await f.Marker.GetSnapshotAsync()).ToDictionary());
        // Re-enabling remains an admission policy, not a reset of accumulated usage.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store
            .EnsureReservationCapacityAsync(f.Caller.UserId, "after-toggle", 300_000, CancellationToken.None));
        Assert.Contains("daily", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertWalletAsync(f, 750_000, 250_000, 0);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(200_000L, 50_000L, "daily spending cap")]
    [InlineData(50_000L, 200_000L, "account daily")]
    public async Task Enabled_daily_caps_still_block_initial_admission(
        long userCap, long globalCap, string expectedMessage)
    {
        var f = await Fixture.CreateAsync(enabled: true, userCap, globalCap);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => f.ReserveAsync("blocked", 100_000));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertWalletAsync(f, 1_000_000, 0, 0);
        Assert.False((await f.Reservation("blocked").GetSnapshotAsync()).Exists);
        Assert.Single((await f.Account.Collection("ledger").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(200_000L, 50_000L, "daily provider")]
    [InlineData(50_000L, 200_000L, "account daily")]
    public async Task Enabled_daily_caps_still_block_rolling_admission(
        long userCap, long globalCap, string expectedMessage)
    {
        var f = await Fixture.CreateAsync(enabled: true, userCap, globalCap);
        await f.ReserveAsync("rolling-blocked", 25_000);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store
            .EnsureReservationCapacityAsync(f.Caller.UserId, "rolling-blocked", 100_000, CancellationToken.None));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertWalletAsync(f, 975_000, 25_000, 0);
        await AssertCountersAsync(await f.GlobalAsync("rolling-blocked"), 25_000, 0, 0);
        await AssertCountersAsync(f.Daily, 25_000, 0, 0);
        Assert.Single((await f.Account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.UsageReservation).GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Disabled_daily_caps_do_not_allow_initial_spending_without_balance()
    {
        var f = await Fixture.CreateAsync(enabled: false, userCap: 10, globalCap: 20);
        await Assert.ThrowsAsync<ManagedAllowanceRefusedException>(() => f.ReserveAsync("no-balance", 1_000_001));
        await AssertWalletAsync(f, 1_000_000, 0, 0);
        Assert.False((await f.Reservation("no-balance").GetSnapshotAsync()).Exists);
        Assert.Single((await f.Account.Collection("ledger").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Disabled_daily_caps_do_not_allow_rolling_spending_without_balance()
    {
        var f = await Fixture.CreateAsync(enabled: false, userCap: 10, globalCap: 20);
        await f.ReserveAsync("rolling-no-balance", 100_000);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store
            .EnsureReservationCapacityAsync(f.Caller.UserId, "rolling-no-balance", 1_000_001, CancellationToken.None));
        Assert.Contains("insufficient credit", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertWalletAsync(f, 900_000, 100_000, 0);
        await AssertCountersAsync(await f.GlobalAsync("rolling-no-balance"), 100_000, 0, 0);
        await AssertCountersAsync(f.Daily, 100_000, 0, 0);
        Assert.Single((await f.Account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.UsageReservation).GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Disabled_daily_caps_admit_over_cap_legacy_commitment_without_erasing_it()
    {
        var f = await Fixture.CreateAsync(enabled: false, userCap: 10, globalCap: 20);
        var legacy = await f.SeedOverCapLegacyAsync();
        Assert.True((await f.ReserveAsync("legacy-uncapped", 100_000)).Created);
        Assert.Equal(250_000, await f.Store.EnsureReservationCapacityAsync(
            f.Caller.UserId, "legacy-uncapped", 250_000, CancellationToken.None));
        await AssertWalletAsync(f, 750_000, 250_000, 0);
        var marker = await f.Marker.GetSnapshotAsync();
        Assert.Equal(20, marker.GetValue<long>("globalDailyCapCreditMicros"));
        Assert.Equal(60, marker.GetValue<long>("legacyCommittedCreditMicrosAtCutover"));
        Assert.Equal(0, marker.GetValue<long>("shardedDailyCapCreditMicros"));
        var global = await f.GlobalAsync("legacy-uncapped");
        Assert.Equal(0, (await global.GetSnapshotAsync()).GetValue<long>("dailyCapCreditMicros"));
        await AssertCountersAsync(global, 250_000, 0, 0);
        AssertMapEqual(legacy, (await f.Legacy.GetSnapshotAsync()).ToDictionary());
        var markerBefore = marker.ToDictionary();
        // A later legacy writer cannot reintroduce daily blocking when disabled.
        await f.Legacy.UpdateAsync("settledCreditMicros", 100L);
        Assert.Equal(300_000, await f.Store.EnsureReservationCapacityAsync(
            f.Caller.UserId, "legacy-uncapped", 300_000, CancellationToken.None));
        AssertMapEqual(markerBefore, (await f.Marker.GetSnapshotAsync()).ToDictionary());
        await AssertWalletAsync(f, 700_000, 300_000, 0);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Enabled_daily_caps_still_reject_over_cap_legacy_commitment()
    {
        var f = await Fixture.CreateAsync(enabled: true, userCap: 10, globalCap: 20);
        var legacy = await f.SeedOverCapLegacyAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => f.ReserveAsync("legacy-blocked", 100_000));
        Assert.Contains("Legacy daily usage", exception.Message, StringComparison.Ordinal);
        await AssertWalletAsync(f, 1_000_000, 0, 0);
        Assert.False((await f.Marker.GetSnapshotAsync()).Exists);
        Assert.False((await f.Reservation("legacy-blocked").GetSnapshotAsync()).Exists);
        AssertMapEqual(legacy, (await f.Legacy.GetSnapshotAsync()).ToDictionary());
        Assert.Single((await f.Account.Collection("ledger").GetSnapshotAsync()).Documents);
    }

    private static async Task AssertWalletAsync(Fixture f, long available, long reserved, long used)
    {
        var account = await f.Store.GetAccountAsync(f.Caller.UserId, CancellationToken.None);
        Assert.Equal(available, account.AvailableCreditMicros);
        Assert.Equal(reserved, account.ReservedCreditMicros);
        Assert.Equal(used, account.CycleUsageCreditMicros);
        Assert.Equal(0, (await f.Account.GetSnapshotAsync()).GetValue<long>("creditDebtMicros"));
    }

    private static async Task AssertCountersAsync(DocumentReference reference, long reserved, long settled, long providerCost)
    {
        var snapshot = await reference.GetSnapshotAsync();
        Assert.Equal(reserved, snapshot.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(settled, snapshot.GetValue<long>("settledCreditMicros"));
        Assert.Equal(providerCost, snapshot.ContainsField("providerCostUsdMicros")
            ? snapshot.GetValue<long>("providerCostUsdMicros") : 0);
    }

    private static void AssertMapEqual(IDictionary<string, object> expected, IDictionary<string, object> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(key => key, StringComparer.Ordinal),
            actual.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var entry in expected)
            Assert.Equal(entry.Value, actual[entry.Key]);
    }

    private sealed class Fixture
    {
        private readonly long _userCap;
        private readonly long _globalCap;
        internal FirestoreDb Db { get; }
        internal FirestoreAccountStore Store { get; }
        internal CallerIdentity Caller { get; }
        internal FixedClock Clock { get; } = new();
        internal DocumentReference Account => Db.Collection("accounts").Document(Caller.UserId);
        internal string Day => Clock.UtcNow.ToString("yyyyMMdd");
        internal DocumentReference Marker => Db.Collection("globalUsageMigrations").Document(Day);
        internal DocumentReference Legacy => Db.Collection("globalUsage").Document(Day);
        internal DocumentReference Daily => Account.Collection("dailyUsage").Document(Day);

        private Fixture(FirestoreDb db, bool enabled, long userCap, long globalCap)
        {
            Db = db;
            _userCap = userCap;
            _globalCap = globalCap;
            var id = "synthetic-daily-caps-" + Guid.NewGuid().ToString("N");
            Caller = new CallerIdentity(id, id + "@example.invalid", "Synthetic daily caps", "test");
            Store = StoreWith(enabled);
        }

        internal FirestoreAccountStore StoreWith(bool enabled) => new(Db, Clock, Options.Create(new PaidSystemOptions
        {
            ProjectId = Db.ProjectId,
            DailySpendingCapsEnabled = enabled,
            GlobalDailyCreditMicrosCap = _globalCap,
            PerUserDailyCreditMicrosCap = _userCap,
            GlobalUsageShardCount = 1,
            MaxRetries = 3,
        }));

        internal static async Task<Fixture> CreateAsync(bool enabled, long userCap, long globalCap)
        {
            var endpoint = new Uri("http://" + Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"));
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.Equal("/", endpoint.AbsolutePath);
            Assert.Empty(endpoint.UserInfo);
            Assert.Empty(endpoint.Query);
            Assert.Empty(endpoint.Fragment);
            var db = new FirestoreDbBuilder
            {
                ProjectId = "kwtf-daily-caps-" + Guid.NewGuid().ToString("N"),
                EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build();
            var fixture = new Fixture(db, enabled, userCap, globalCap);
            await fixture.Store.EnsureAccountAsync(fixture.Caller, CancellationToken.None);
            await fixture.Store.GrantCreditsAsync(fixture.Caller.UserId, 1_000_000, "synthetic allowance", "initial",
                "emulator-test", CancellationToken.None);
            return fixture;
        }

        internal Task<ReservationResult> ReserveAsync(string id, long amount, FirestoreAccountStore? store = null) =>
            (store ?? Store).ReserveAsync(Caller, id, "synthetic-model", "synthetic-pricing", amount,
                2_000_000, 200, 1, _globalCap, CancellationToken.None, "live_audio", 720);

        internal DocumentReference Reservation(string id) => Account.Collection("reservations").Document(id);

        internal async Task<DocumentReference> GlobalAsync(string id) => Db.Collection("globalUsageShards")
            .Document((await Reservation(id).GetSnapshotAsync()).GetValue<string>("globalUsageShardId"));

        internal async Task<Dictionary<string, object>> SeedOverCapLegacyAsync()
        {
            var fields = new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = 30L,
                ["settledCreditMicros"] = 25L,
                ["companyLossExposureCreditMicros"] = 5L,
                ["providerCostUsdMicros"] = 17L,
            };
            await Legacy.SetAsync(fields);
            return fields;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-07T12:00:00Z");
    }
}
