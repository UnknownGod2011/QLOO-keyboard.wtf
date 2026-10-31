using Google.Api.Gax;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class ManagedLiveUnknownUsageLossTests
{
    private const long Hold = 1_000_000;
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
    private static readonly ProviderUsage Exact = new(10, 5, 0, 0, 0, 0, 15, "synthetic-receipt");

    [Fact]
    public void Policy_is_disabled_by_default_and_fixed_bounds_are_explicit()
    {
        Assert.False(new PaidSystemOptions().ManagedLiveUnknownUsageLossEnabled);
        Assert.Equal(TimeSpan.FromHours(24), FirestoreAccountStore.ManagedLiveLossWait);
        Assert.Equal(100_000_000, FirestoreAccountStore.ManagedLiveLossDailyReleaseLimit);
        Assert.Equal(2, FirestoreAccountStore.ManagedLiveLossDailyAccountLimit);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Full_transaction_releases_original_hold_once_without_customer_charge_or_fake_provider_cost()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("loss");
        await f.MakeEligibleAsync("loss");
        var before = await f.Account.GetSnapshotAsync();
        var initialGlobal = await f.Global("loss");
        var beforeGlobal = await initialGlobal.GetSnapshotAsync();
        var concurrency = before.GetValue<long>("activeRequests");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.ResolveAsync("loss")));
        Assert.Single(results, result => result == ManagedLiveLossResolution.Released);
        Assert.Equal(3, results.Count(result => result == ManagedLiveLossResolution.AlreadyResolved));
        var reservation = await f.Reservation("loss").GetSnapshotAsync();
        var after = await f.Account.GetSnapshotAsync();
        Assert.Equal(FirestoreAccountStore.ManagedLiveLossTerminalStatus, reservation.GetValue<string>("status"));
        Assert.Equal(before.GetValue<long>("availableCreditMicros") + Hold, after.GetValue<long>("availableCreditMicros"));
        Assert.Equal(0, after.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(before.GetValue<long>("cycleUsageCreditMicros"), after.GetValue<long>("cycleUsageCreditMicros"));
        Assert.Equal(concurrency, after.GetValue<long>("activeRequests"));
        Assert.Equal(before.GetValue<long>("activeLiveRequests"), after.GetValue<long>("activeLiveRequests"));
        Assert.False(reservation.GetValue<bool>("providerCostKnown"));
        Assert.False(reservation.GetValue<bool>("customerRetroactiveChargeAllowed"));
        Assert.False(reservation.ContainsField("providerHttpStatus"));
        Assert.False(reservation.ContainsField("providerCompletionRecorded"));
        Assert.False(reservation.ContainsField("chargedCreditMicros"));
        Assert.Equal(Hold, reservation.GetValue<long>("companyLossExposureCreditMicros"));
        var decision = Assert.Single((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        Assert.False(decision.GetValue<bool>("providerCostKnown"));
        Assert.False(decision.ContainsField("providerCostUsdMicros"));
        Assert.Equal(Hold, decision.GetValue<long>("retiredReservationCreditMicros"));
        Assert.Single((await f.Account.Collection("ledger").WhereEqualTo("type", LedgerTypes.ManagedLiveUnknownCostHoldRelease).GetSnapshotAsync()).Documents);
        var afterGlobal = await initialGlobal.GetSnapshotAsync();
        Assert.Equal(beforeGlobal.GetValue<long>("reservedCreditMicros") - Hold, afterGlobal.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(Hold, afterGlobal.GetValue<long>("companyLossExposureCreditMicros"));
        Assert.Equal(beforeGlobal.GetValue<long>("settledCreditMicros"), afterGlobal.GetValue<long>("settledCreditMicros"));
        Assert.Equal(beforeGlobal.GetValue<long>("providerCostUsdMicros"), afterGlobal.GetValue<long>("providerCostUsdMicros"));
        Assert.Equal(Hold, (await f.Account.Collection("dailyUsage").Document("20261007").GetSnapshotAsync()).GetValue<long>("companyLossExposureCreditMicros"));
        await f.AssertLedgerAsync();
        var frozen = await f.StateAsync();
        Assert.Equal(ManagedLiveLossResolution.AlreadyResolved, await f.ResolveAsync("loss"));
        Assert.Equal(frozen, await f.StateAsync());
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Exact_24_hour_after_expiry_boundary(int deltaSeconds, bool eligible)
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("boundary");
        await f.MakeEligibleAsync("boundary", deltaSeconds);
        Assert.Equal(eligible ? ManagedLiveLossResolution.Released : ManagedLiveLossResolution.NotEligible,
            await f.ResolveAsync("boundary"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Disabled_policy_and_legacy_holds_are_not_retroactively_enrolled()
    {
        await using var f = await Fixture.CreateAsync(enabled: false);
        await f.PrepareAsync("old");
        await f.MakeEligibleAsync("old");
        var before = await f.StateAsync();
        Assert.Equal(ManagedLiveLossResolution.NotEligible, await f.ResolveAsync("old"));
        Assert.Equal(before, await f.StateAsync());
        var enabled = f.StoreWith(true);
        await enabled.RecordManagedLiveSessionClosedAsync(f.Caller.UserId, "old", CancellationToken.None);
        Assert.Equal(ManagedLiveLossResolution.NotEligible,
            await enabled.ResolveManagedLiveUnknownUsageLossAsync(f.Caller.UserId, "old", CancellationToken.None));
        Assert.Equal(before, await f.StateAsync());
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("missing-closure")]
    [InlineData("missing-concurrency")]
    [InlineData("active-slot")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-activity")]
    [InlineData("old-accounting")]
    [InlineData("late-enrollment")]
    [InlineData("completion-recorded")]
    [InlineData("provider-rejected")]
    [InlineData("still-reserved")]
    [InlineData("future-close")]
    public async Task Missing_or_conflicting_eligibility_is_fail_closed(string variation)
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("ineligible");
        await f.MakeEligibleAsync("ineligible");
        var mutation = variation switch
        {
            "missing-closure" => new Dictionary<string, object> { ["managedLiveClosedAt"] = FieldValue.Delete },
            "missing-concurrency" => new() { ["concurrencyReleased"] = FieldValue.Delete },
            "active-slot" => new() { ["concurrencyReleased"] = false },
            "wrong-owner" => new() { ["userId"] = "unrelated-synthetic-owner" },
            "wrong-activity" => new() { ["activityType"] = "ai_request" },
            "old-accounting" => new() { ["accountingVersion"] = 1L },
            "late-enrollment" => new() { ["managedLiveLossEnrolledAt"] = Ts(f.Clock.Now) },
            "completion-recorded" => new() { ["providerCompletionRecorded"] = true },
            "provider-rejected" => new() { ["providerHttpStatus"] = 400L },
            "still-reserved" => new() { ["status"] = "reserved" },
            "future-close" => new() { ["managedLiveClosedAt"] = Ts(f.Clock.Now.AddSeconds(1)) },
            _ => throw new InvalidOperationException(),
        };
        await f.Reservation("ineligible").UpdateAsync(mutation);
        var before = await f.StateAsync();
        Assert.Equal(ManagedLiveLossResolution.NotEligible, await f.ResolveAsync("ineligible"));
        Assert.Equal(before, await f.StateAsync());
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Existing_release_guard_remains_strict_and_late_completion_cannot_debit_written_off_usage()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("strict");
        await f.MakeEligibleAsync("strict");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ReleaseReservationAsync(
            f.Caller.UserId, "strict", "provider_rejected", CancellationToken.None));
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("strict"));
        var before = await f.StateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.CompleteAsync("strict"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SettleAsync(f.Caller.UserId,
            "strict", Exact, 40_000, 12, "late", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.RecordTerminalProviderRejectionAsync(
            f.Caller.UserId, "strict", 400, CancellationToken.None));
        await f.Store.CheckpointReservationUsageAsync(f.Caller.UserId, "strict", Exact, 40_000, 12, CancellationToken.None);
        await f.Store.MarkReservationForReconciliationAsync(f.Caller.UserId, "strict", "late", CancellationToken.None);
        await f.Store.ReleaseReservationAsync(f.Caller.UserId, "strict", "provider_rejected", CancellationToken.None);
        Assert.Equal(before, await f.StateAsync());
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Completion_race_has_one_terminal_financial_owner()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("race");
        await f.MakeEligibleAsync("race");
        async Task<bool> Complete()
        {
            try { await f.CompleteAsync("race"); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var completion = Complete();
        var release = f.ResolveAsync("race");
        await Task.WhenAll(completion, release);
        if (await completion)
        {
            Assert.Equal(ManagedLiveLossResolution.NotEligible, await release);
            await f.Store.SettleAsync(f.Caller.UserId, "race", Exact, 40_000, 12, "completed", CancellationToken.None);
            Assert.Empty((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        }
        else
        {
            Assert.Equal(ManagedLiveLossResolution.Released, await release);
            Assert.Equal(0, (await f.Account.GetSnapshotAsync()).GetValue<long>("cycleUsageCreditMicros"));
        }
        Assert.Equal(0, (await f.Account.GetSnapshotAsync()).GetValue<long>("reservedCreditMicros"));
        await f.AssertLedgerAsync();
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Known_completion_and_verified_rejection_use_existing_accounting_not_loss()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("known");
        await f.CompleteAsync("known");
        await f.MakeEligibleAsync("known");
        Assert.Equal(ManagedLiveLossResolution.NotEligible, await f.ResolveAsync("known"));
        await f.Store.SettleAsync(f.Caller.UserId, "known", Exact, 40_000, 12, "completed", CancellationToken.None);
        await f.PrepareAsync("rejection");
        await f.Store.RecordTerminalProviderRejectionAsync(f.Caller.UserId, "rejection", 400, CancellationToken.None);
        await f.MakeEligibleAsync("rejection");
        Assert.Equal(ManagedLiveLossResolution.NotEligible, await f.ResolveAsync("rejection"));
        await f.Store.ReleaseReservationAsync(f.Caller.UserId, "rejection", "provider_rejected", CancellationToken.None);
        Assert.Empty((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        await f.AssertLedgerAsync();
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Expired_bucket_is_not_reissued_and_unreserved_expiry_gets_its_own_ledger_entry()
    {
        await using var f = await Fixture.CreateAsync(seed: 2 * Hold);
        await f.ExpireInitialBucketAsync();
        await f.PrepareAsync("expired");
        await f.MakeEligibleAsync("expired");
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("expired"));
        var account = await f.Account.GetSnapshotAsync();
        Assert.Equal(0, account.GetValue<long>("availableCreditMicros"));
        Assert.Equal(0, account.GetValue<long>("reservedCreditMicros"));
        var decision = Assert.Single((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        Assert.Equal(0, decision.GetValue<long>("restoredAvailableCreditMicros"));
        Assert.Equal(Hold, decision.GetValue<long>("expiredReservedCreditMicros"));
        Assert.Equal(Hold, (await f.Db.Collection("managedLiveLossDaily").Document(f.Day).GetSnapshotAsync()).GetValue<long>("releasedReservationCreditMicros"));
        Assert.Single((await f.Account.Collection("ledger").WhereEqualTo("type", LedgerTypes.Expiration).GetSnapshotAsync()).Documents);
        await f.AssertLedgerAsync();
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Mixed_original_allocations_return_only_the_still_valid_part()
    {
        await using var f = await Fixture.CreateAsync(seed: 2 * Hold);
        await f.ExpireInitialBucketAsync();
        await f.Store.GrantCreditsAsync(f.Caller.UserId, 2 * Hold, "synthetic second bucket", "second", "emulator", CancellationToken.None);
        await f.PrepareAsync("mixed", 3 * Hold);
        await f.MakeEligibleAsync("mixed");
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("mixed"));
        Assert.Equal(2 * Hold, (await f.Account.GetSnapshotAsync()).GetValue<long>("availableCreditMicros"));
        var decision = Assert.Single((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        Assert.Equal(Hold, decision.GetValue<long>("restoredAvailableCreditMicros"));
        Assert.Equal(2 * Hold, decision.GetValue<long>("expiredReservedCreditMicros"));
        await f.AssertLedgerAsync();
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Second_daily_loss_fences_only_new_live_and_does_not_unlock_at_midnight()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("first");
        await f.PrepareAsync("second");
        await f.PrepareAsync("third");
        await f.MakeEligibleAsync("third");
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("first"));
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("second"));
        Assert.Equal(ManagedLiveLossResolution.AccountDailyLimit, await f.ResolveAsync("third"));
        Assert.True((await f.Account.GetSnapshotAsync()).GetValue<bool>("managedLiveLossReviewRequired"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ReserveAsync("blocked"));
        var ordinary = await f.ReserveAsync("ordinary", activity: "ai_request");
        Assert.True(ordinary.Created);
        await f.Store.ReleaseReservationAsync(f.Caller.UserId, "ordinary", "not_dispatched", CancellationToken.None);
        f.Clock.Now = f.Clock.Now.AddDays(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ReserveAsync("blocked-next-day"));
        Assert.True((await f.Account.GetSnapshotAsync()).GetValue<bool>("managedLiveLossReviewRequired"));
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("third"));
        await f.AssertLedgerAsync();
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Global_daily_cap_is_atomic_across_different_owners()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("owner-one");
        var second = await f.AddCallerAsync();
        await f.PrepareAsync("owner-two", caller: second);
        var secondExpiry = (await f.Reservation("owner-two", second).GetSnapshotAsync()).GetValue<Timestamp>("expiresAt");
        f.Clock.Now = secondExpiry.ToDateTimeOffset().AddHours(24);
        await f.Db.Collection("managedLiveLossDaily").Document(f.Day).SetAsync(new Dictionary<string, object>
        {
            ["date"] = f.Day, ["releasedReservationCreditMicros"] = 99 * Hold, ["decisionCount"] = 99L,
        });
        var results = await Task.WhenAll(f.ResolveAsync("owner-one"), f.ResolveAsync("owner-two", second));
        Assert.Single(results, result => result == ManagedLiveLossResolution.Released);
        Assert.Single(results, result => result == ManagedLiveLossResolution.GlobalDailyLimit);
        Assert.Equal(100 * Hold, (await f.Db.Collection("managedLiveLossDaily").Document(f.Day).GetSnapshotAsync()).GetValue<long>("releasedReservationCreditMicros"));
        Assert.Single((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("decision-amount")]
    [InlineData("decision-policy")]
    [InlineData("decision-retrocharge")]
    [InlineData("idempotency-owner")]
    [InlineData("ledger-amount")]
    [InlineData("reservation-amount")]
    public async Task Retried_resolution_rejects_corrupt_identity_amount_or_policy_evidence(string variation)
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("retry-corrupt");
        await f.MakeEligibleAsync("retry-corrupt");
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("retry-corrupt"));
        var decision = Assert.Single((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        if (variation == "decision-amount")
            await decision.Reference.UpdateAsync("retiredReservationCreditMicros", Hold + 1);
        else if (variation == "decision-policy")
            await decision.Reference.UpdateAsync("policyVersion", 2L);
        else if (variation == "decision-retrocharge")
            await decision.Reference.UpdateAsync("customerRetroactiveChargeAllowed", true);
        else if (variation == "idempotency-owner")
            await f.Db.Collection("idempotency").Document(decision.Id).UpdateAsync("userId", "unrelated-owner");
        else if (variation == "ledger-amount")
            await f.Account.Collection("ledger").Document("live_loss_retry-corrupt").UpdateAsync("reservedDeltaMicros", -Hold - 1);
        else
            await f.Reservation("retry-corrupt").UpdateAsync("reservedCreditMicros", Hold + 1);
        var before = await f.StateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ResolveAsync("retry-corrupt"));
        Assert.Equal(before, await f.StateAsync());
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Completion_committed_before_lost_ack_preserves_exact_evidence_over_loss_policy()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("ack-lost");
        await f.CompleteAsync("ack-lost"); // Simulate discarded successful ACK, not a fabricated provider receipt.
        await f.Store.MarkReservationForReconciliationAsync(f.Caller.UserId, "ack-lost", "synthetic_ack_lost", CancellationToken.None);
        await f.MakeEligibleAsync("ack-lost");
        var before = await f.StateAsync();
        Assert.Equal(ManagedLiveLossResolution.NotEligible, await f.ResolveAsync("ack-lost"));
        Assert.Equal(before, await f.StateAsync());
        await f.Store.SettleAsync(f.Caller.UserId, "ack-lost", Exact, 40_000, 12, "completed", CancellationToken.None);
        Assert.Empty((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        Assert.Equal(40_000, (await f.Account.GetSnapshotAsync()).GetValue<long>("cycleUsageCreditMicros"));
        await f.AssertLedgerAsync();
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Retained_exposure_is_counted_by_new_reservation_and_rolling_extension()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("loss");
        await f.MakeEligibleAsync("loss");
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("loss"));
        // A same-day synthetic budget fixture proves the actual Reserve and
        // EnsureCapacity guards include company exposure, not wallet balance.
        var reservation = await f.Reservation("loss").GetSnapshotAsync();
        f.Clock.Now = reservation.GetValue<Timestamp>("createdAt").ToDateTimeOffset().AddSeconds(5);
        await f.Db.Collection("globalUsageShards").Document("20261007-000").UpdateAsync(new Dictionary<string, object>
        {
            ["companyLossExposureCreditMicros"] = f.Settings.GlobalDailyCreditMicrosCap - Hold,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ReserveAsync("global-blocked", 2 * Hold));
        await f.ReserveAsync("extend", Hold);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.EnsureReservationCapacityAsync(
            f.Caller.UserId, "extend", 2 * Hold, CancellationToken.None));
        await f.Store.ReleaseReservationAsync(f.Caller.UserId, "extend", "not_dispatched", CancellationToken.None);
        await f.Db.Collection("globalUsageShards").Document("20261007-000").UpdateAsync(new Dictionary<string, object>
        {
            ["companyLossExposureCreditMicros"] = Hold,
        });
        await f.Account.Collection("dailyUsage").Document("20261007").UpdateAsync(new Dictionary<string, object>
        {
            ["companyLossExposureCreditMicros"] = f.Settings.PerUserDailyCreditMicrosCap - Hold,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ReserveAsync("daily-blocked", 2 * Hold));
        await f.ReserveAsync("daily-extend", Hold);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.EnsureReservationCapacityAsync(
            f.Caller.UserId, "daily-extend", 2 * Hold, CancellationToken.None));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("allocations-missing")]
    [InlineData("allocations-wrong-total")]
    [InlineData("wallet-mismatch")]
    [InlineData("global-reserved-missing")]
    [InlineData("global-counter-malformed")]
    public async Task Malformed_financial_evidence_never_causes_a_partial_release(string variation)
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("malformed");
        await f.MakeEligibleAsync("malformed");
        if (variation == "allocations-missing")
            await f.Reservation("malformed").UpdateAsync("allowanceAllocations", FieldValue.Delete);
        else if (variation == "allocations-wrong-total")
            await f.Reservation("malformed").UpdateAsync("reservedCreditMicros", Hold + 1);
        else if (variation == "wallet-mismatch")
            await f.Account.UpdateAsync("availableCreditMicros", 123L);
        else if (variation == "global-reserved-missing")
            await (await f.Global("malformed")).UpdateAsync("reservedCreditMicros", 0L);
        else
            await f.Db.Collection("managedLiveLossDaily").Document(f.Day).SetAsync(new Dictionary<string, object>
            {
                ["date"] = f.Day, ["releasedReservationCreditMicros"] = "invalid", ["decisionCount"] = 0L,
            });
        var before = await f.StateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.ResolveAsync("malformed"));
        Assert.Equal(before, await f.StateAsync());
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Reconciliation_invokes_opted_in_resolution_and_financial_cleanup_survives_account_deletion()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("maintenance");
        await f.MakeEligibleAsync("maintenance");
        await f.Account.UpdateAsync("status", "deleted");
        var service = new UsageReconciliationService(f.Db, f.Store, f.Clock, Options.Create(f.Settings),
            NullLogger<UsageReconciliationService>.Instance);
        var method = typeof(UsageReconciliationService).GetMethod("ReconcileReservationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var operation = (Task)method.Invoke(service, [await f.Reservation("maintenance").GetSnapshotAsync(), "synthetic-loss-run", CancellationToken.None])!;
        await operation;
        Assert.Equal(FirestoreAccountStore.ManagedLiveLossTerminalStatus,
            (await f.Reservation("maintenance").GetSnapshotAsync()).GetValue<string>("status"));
        Assert.Equal("deleted", (await f.Account.GetSnapshotAsync()).GetValue<string>("status"));
        Assert.Single((await f.Db.Collection("managedLiveLossDecisions").GetSnapshotAsync()).Documents);
        await f.AssertLedgerAsync();
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Close_marker_is_idempotent_and_resolution_day_is_utc()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareAsync("utc");
        var before = await f.Reservation("utc").GetSnapshotAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(1);
        await f.Store.RecordManagedLiveSessionClosedAsync(f.Caller.UserId, "utc", CancellationToken.None);
        Assert.Equal(before.UpdateTime, (await f.Reservation("utc").GetSnapshotAsync()).UpdateTime);
        f.Clock.Now = DateTimeOffset.Parse("2026-10-09T01:00:00+05:30");
        Assert.Equal(ManagedLiveLossResolution.Released, await f.ResolveAsync("utc"));
        Assert.True((await f.Db.Collection("managedLiveLossDaily").Document("20261008").GetSnapshotAsync()).Exists);
        Assert.False((await f.Db.Collection("managedLiveLossDaily").Document("20261009").GetSnapshotAsync()).Exists);
    }

    private static Timestamp Ts(DateTimeOffset value) => Timestamp.FromDateTime(value.UtcDateTime);
    private sealed class Clock : KeyboardWtf.Staging.Api.Services.IClock { public DateTimeOffset Now { get; set; } = Start; public DateTimeOffset UtcNow => Now; }
    private sealed record DocumentState(string Path, Timestamp? Updated, string Data);

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Prefix = "kwt-live-loss-local-";
        private readonly List<CallerIdentity> _owners = [];
        private readonly long _seed;
        internal FirestoreDb Db { get; }
        internal Clock Clock { get; } = new();
        internal PaidSystemOptions Settings { get; }
        internal FirestoreAccountStore Store { get; }
        internal CallerIdentity Caller => _owners[0];
        internal DocumentReference Account => AccountFor(Caller);
        internal string Day => Clock.Now.ToUniversalTime().ToString("yyyyMMdd");

        private Fixture(FirestoreDb db, bool enabled, long seed)
        {
            Db = db; _seed = seed;
            Settings = OptionsFor(enabled);
            Store = new(db, Clock, Options.Create(Settings));
        }

        private PaidSystemOptions OptionsFor(bool enabled) => new()
        {
            ProjectId = Db.ProjectId, GlobalUsageShardCount = 1, ManagedLiveUnknownUsageLossEnabled = enabled,
            GlobalDailyCreditMicrosCap = 1_000_000_000, PerUserDailyCreditMicrosCap = 1_000_000_000,
            MaxRetries = 3, MaxConcurrentLiveSessionsPerUser = 1,
        };

        internal FirestoreAccountStore StoreWith(bool enabled) => new(Db, Clock, Options.Create(OptionsFor(enabled)));
        internal DocumentReference AccountFor(CallerIdentity caller) => Db.Collection("accounts").Document(caller.UserId);
        internal DocumentReference Reservation(string id, CallerIdentity? caller = null) => AccountFor(caller ?? Caller).Collection("reservations").Document(id);

        internal static async Task<Fixture> CreateAsync(bool enabled = true, long seed = 100 * Hold)
        {
            var endpoint = new Uri("http://" + Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"));
            Assert.Equal("127.0.0.1", endpoint.Host); Assert.Equal("/", endpoint.AbsolutePath);
            Assert.Empty(endpoint.UserInfo); Assert.Empty(endpoint.Query); Assert.Empty(endpoint.Fragment);
            var db = new FirestoreDbBuilder
            {
                ProjectId = Prefix + Guid.NewGuid().ToString("N"), EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build();
            var fixture = new Fixture(db, enabled, seed);
            await fixture.AddCallerAsync();
            return fixture;
        }

        internal async Task<CallerIdentity> AddCallerAsync()
        {
            var uid = "synthetic-live-loss-" + Guid.NewGuid().ToString("N");
            var caller = new CallerIdentity(uid, uid + "@example.invalid", "Synthetic Live loss", "test");
            _owners.Add(caller);
            await Store.EnsureAccountAsync(caller, CancellationToken.None);
            await Store.GrantCreditsAsync(uid, _seed, "synthetic allowance", "initial", "emulator", CancellationToken.None);
            return caller;
        }

        internal Task<ReservationResult> ReserveAsync(string id, long amount = Hold, string activity = "live_audio", CallerIdentity? caller = null) =>
            Store.ReserveAsync(caller ?? Caller, id, "synthetic-model", "synthetic-pricing", amount, Math.Max(amount, 10 * Hold),
                200, 1, Settings.GlobalDailyCreditMicrosCap, CancellationToken.None, activity, 720);

        internal async Task PrepareAsync(string id, long amount = Hold, CallerIdentity? caller = null)
        {
            caller ??= Caller;
            await ReserveAsync(id, amount, caller: caller);
            await Store.MarkReservationProviderStartedAsync(caller.UserId, id, CancellationToken.None);
            Clock.Now = Clock.Now.AddSeconds(1);
            await Store.RecordManagedLiveSessionClosedAsync(caller.UserId, id, CancellationToken.None);
            await Store.MarkReservationForReconciliationAsync(caller.UserId, id, "synthetic_missing_receipt", CancellationToken.None);
        }

        internal async Task MakeEligibleAsync(string id, int seconds = 0)
        {
            var reservation = await Reservation(id).GetSnapshotAsync();
            Clock.Now = reservation.GetValue<Timestamp>("expiresAt").ToDateTimeOffset().AddHours(24).AddSeconds(seconds);
        }

        internal Task<ManagedLiveLossResolution> ResolveAsync(string id, CallerIdentity? caller = null) =>
            Store.ResolveManagedLiveUnknownUsageLossAsync((caller ?? Caller).UserId, id, CancellationToken.None);
        internal Task CompleteAsync(string id) => Store.RecordProviderCompletionAsync(Caller.UserId, id, Exact, 40_000, 12,
            "completed", CancellationToken.None);

        internal async Task<DocumentReference> Global(string id)
        {
            var snapshot = await Reservation(id).GetSnapshotAsync();
            return Db.Collection("globalUsageShards").Document(snapshot.GetValue<string>("globalUsageShardId"));
        }

        internal async Task ExpireInitialBucketAsync()
        {
            var data = (await Account.GetSnapshotAsync()).ToDictionary();
            var buckets = (Dictionary<string, object>)data["allowanceBuckets"];
            var bucket = (Dictionary<string, object>)buckets.Single().Value;
            bucket["kind"] = "subscription"; bucket["expiresAtUnix"] = Start.AddHours(1).ToUnixTimeSeconds();
            await Account.UpdateAsync("allowanceBuckets", buckets);
        }

        internal async Task AssertLedgerAsync()
        {
            foreach (var owner in _owners)
            {
                var account = await AccountFor(owner).GetSnapshotAsync();
                var entries = (await AccountFor(owner).Collection("ledger").GetSnapshotAsync()).Documents;
                Assert.Equal(account.GetValue<long>("availableCreditMicros"), entries.Sum(entry => entry.GetValue<long>("availableDeltaMicros")));
                Assert.Equal(account.GetValue<long>("reservedCreditMicros"), entries.Sum(entry => entry.GetValue<long>("reservedDeltaMicros")));
            }
        }

        internal async Task<DocumentState[]> StateAsync()
        {
            var states = new List<DocumentState>();
            async Task Visit(CollectionReference collection)
            {
                foreach (var document in (await collection.GetSnapshotAsync()).Documents)
                {
                    states.Add(new(document.Reference.Path, document.UpdateTime, Canonical(document.ToDictionary())));
                    await foreach (var child in document.Reference.ListCollectionsAsync()) await Visit(child);
                }
            }
            await foreach (var collection in Db.ListRootCollectionsAsync()) await Visit(collection);
            return states.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        }

        private static string Canonical(object? value) => value switch
        {
            IDictionary<string, object> map => "{" + string.Join(",", map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
            Timestamp stamp => "timestamp:" + stamp.ToDateTime().ToString("O"),
            IEnumerable<object> rows => "[" + string.Join(",", rows.Select(Canonical)) + "]",
            _ => JsonSerializer.Serialize(value),
        };

        public async ValueTask DisposeAsync()
        {
            Assert.True(Db.ProjectId.StartsWith(Prefix, StringComparison.Ordinal)
                && Guid.TryParseExact(Db.ProjectId[Prefix.Length..], "N", out _));
            var prefix = $"projects/{Db.ProjectId}/databases/{Db.DatabaseId}/documents/";
            foreach (var item in (await StateAsync()).OrderByDescending(item => item.Path.Length))
            {
                Assert.StartsWith(prefix, item.Path, StringComparison.Ordinal);
                await Db.Document(item.Path[prefix.Length..]).DeleteAsync();
            }
        }
    }
}
