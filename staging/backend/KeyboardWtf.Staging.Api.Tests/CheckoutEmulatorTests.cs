using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class CheckoutEmulatorTests
{
    private static readonly CatalogProduct Subscription = new(
        "local-monthly", "subscription", "Synthetic local subscription", 99_900, "INR", 500_000_000, "plan_local", true);
    private static readonly CatalogProduct Credits = Subscription with { Kind = "credits", RazorpayPlanId = null };

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Distinct_checkout_keys_allow_only_one_provider_creation_per_account()
    {
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            var admitted = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
                store.BeginPurchaseAsync(caller.UserId, $"purchase-{i}", Subscription, $"key-{i}", CancellationToken.None)));
            Assert.Single(admitted, value => value);
            var account = db.Collection("accounts").Document(caller.UserId);
            Assert.Single((await account.Collection("purchases").GetSnapshotAsync()).Documents);
            Assert.StartsWith("purchase-", (await account.GetSnapshotAsync()).GetValue<string>("subscriptionCheckoutPurchaseId"));
        }
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task High_contention_aborted_transactions_never_reach_provider_creation()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var admitted = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try
            {
                return (Admitted: await store.BeginPurchaseAsync(caller.UserId, $"stress-{i}", Subscription, $"stress-{i}", CancellationToken.None), Aborted: false);
            }
            catch (Grpc.Core.RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.Aborted)
            {
                // The SDK's bounded transaction retries may exhaust under heavy
                // contention. Failure is NOT admission or provider creation.
                return (Admitted: false, Aborted: true);
            }
        }));
        var admittedCount = admitted.Count(value => value.Admitted);
        Assert.InRange(admittedCount, 0, 1);
        var purchases = db.Collection("accounts").Document(caller.UserId).Collection("purchases");
        Assert.Equal(admittedCount, (await purchases.GetSnapshotAsync()).Documents.Count);
        if (admittedCount == 0)
        {
            // Every bounded SDK attempt may abort. That is not admission, and
            // a subsequent uncontended request must still be able to proceed.
            Assert.All(admitted, value => Assert.True(value.Aborted));
            Assert.True(await store.BeginPurchaseAsync(caller.UserId, "after-contention", Subscription,
                "after-contention", CancellationToken.None));
        }
        Assert.Single((await purchases.GetSnapshotAsync()).Documents);
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "must-not-compete", Subscription,
            "must-not-compete", CancellationToken.None));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Multiple_unverified_legacy_subscriptions_and_history_overflow_fail_closed()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = db.Collection("accounts").Document(caller.UserId);
        foreach (var id in new[] { "legacy-A", "legacy-B" })
            await account.Collection("purchases").Document(id).SetAsync(new Dictionary<string, object> { ["kind"] = "subscription" });
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "new", Subscription, "new", CancellationToken.None));
        Assert.Equal("legacy-A", (await account.GetSnapshotAsync()).GetValue<string>("subscriptionCheckoutPurchaseId"));

        var (overflowDb, overflowStore, overflowCaller) = await AccountingEmulatorTests.CreateAccountAsync();
        var overflowAccount = overflowDb.Collection("accounts").Document(overflowCaller.UserId);
        var batch = overflowDb.StartBatch();
        for (var i = 0; i < 65; i++)
            batch.Set(overflowAccount.Collection("purchases").Document($"legacy-{i}"), new Dictionary<string, object> { ["kind"] = "subscription" });
        await batch.CommitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => overflowStore.BeginPurchaseAsync(
            overflowCaller.UserId, "new", Subscription, "new", CancellationToken.None));
        Assert.False((await overflowAccount.Collection("purchases").Document("new").GetSnapshotAsync()).Exists);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Provider_closure_preserves_paid_cycle_and_terminal_proof_survives_delayed_active_event()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var first = "first-" + caller.UserId;
        var account = db.Collection("accounts").Document(caller.UserId);
        await store.BeginPurchaseAsync(caller.UserId, first, Subscription, "first", CancellationToken.None);
        await store.AttachPurchaseProviderObjectAsync(caller.UserId, first, "sub_" + first, "subscription", CancellationToken.None);
        await account.Collection("purchases").Document(first).UpdateAsync("credited", true);
        await account.UpdateAsync(new Dictionary<string, object>
        {
            ["activeSubscriptionProviderObjectId"] = "sub_" + first,
            ["activeSubscriptionPurchaseId"] = first,
            ["subscriptionStatus"] = "active", ["planId"] = Subscription.Sku,
            ["cycleEndsAt"] = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(10)),
        });
        await LifecycleAsync(store, first, "sub_" + first, "cancelled", "closed");
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "too-early", Subscription, "too-early", CancellationToken.None));
        Assert.Equal("active", (await account.GetSnapshotAsync()).GetValue<string>("subscriptionStatus"));
        await LifecycleAsync(store, first, "sub_" + first, "active", "late-active");
        Assert.Equal("cancelled", (await account.Collection("purchases").Document(first).GetSnapshotAsync())
            .GetValue<string>("subscriptionTerminalProviderStatus"));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Delayed_activation_cannot_resurrect_a_provider_closed_subscription()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var first = "first-" + caller.UserId;
        var account = db.Collection("accounts").Document(caller.UserId);
        await store.BeginPurchaseAsync(caller.UserId, first, Subscription, "first", CancellationToken.None);
        await store.AttachPurchaseProviderObjectAsync(caller.UserId, first, "sub_" + first, "subscription", CancellationToken.None);
        await account.Collection("purchases").Document(first).UpdateAsync("credited", true);
        await account.UpdateAsync(new Dictionary<string, object>
        {
            ["activeSubscriptionProviderObjectId"] = "sub_" + first,
            ["subscriptionStatus"] = "active", ["planId"] = Subscription.Sku,
            ["cycleEndsAt"] = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-1)),
        });
        await LifecycleAsync(store, first, "sub_" + first, "cancelled", "closed");
        await LifecycleAsync(store, first, "sub_" + first, "active", "delayed", "subscription.activated");
        Assert.Equal("cancelled", (await account.GetSnapshotAsync()).GetValue<string>("subscriptionStatus"));
        Assert.True(await store.BeginPurchaseAsync(caller.UserId, "next", Subscription, "next", CancellationToken.None));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("active")]
    [InlineData("authenticated")]
    [InlineData("pending")]
    [InlineData("halted")]
    public async Task Active_subscription_eligibility_is_enforced_inside_the_transaction(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = db.Collection("accounts").Document(caller.UserId);
        await account.UpdateAsync("subscriptionStatus", status);
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "not-admitted", Subscription, "key", CancellationToken.None));
        Assert.Empty((await account.Collection("purchases").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("provider_failed")]
    [InlineData("paid")]
    public async Task Legacy_local_status_never_proves_remote_subscription_is_gone(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = db.Collection("accounts").Document(caller.UserId);
        await account.Collection("purchases").Document("legacy").SetAsync(new Dictionary<string, object>
        {
            ["kind"] = "subscription", ["status"] = status, ["providerObjectId"] = "sub_legacy",
        });
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "new", Subscription, "new-key", CancellationToken.None));
        Assert.Equal("legacy", (await account.GetSnapshotAsync()).GetValue<string>("subscriptionCheckoutPurchaseId"));
        Assert.Single((await account.Collection("purchases").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Cancellation_and_creation_failure_keep_unknown_remote_owner_but_do_not_block_credit_orders()
    {
        var (_, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        Assert.True(await store.BeginPurchaseAsync(caller.UserId, "first", Subscription, "first", CancellationToken.None));
        await store.CancelPendingPurchaseAsync(caller.UserId, "first", CancellationToken.None);
        await store.MarkPurchaseFailedAsync(caller.UserId, "first", "acknowledgement_lost", CancellationToken.None);
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "second", Subscription, "second", CancellationToken.None));
        Assert.True(await store.BeginPurchaseAsync(caller.UserId, "credits", Credits, "credits", CancellationToken.None));
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "credits", Credits, "credits", CancellationToken.None));
        var (_, otherStore, other) = await AccountingEmulatorTests.CreateAccountAsync();
        Assert.True(await otherStore.BeginPurchaseAsync(other.UserId, "first", Subscription, "first", CancellationToken.None));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("cancelled", true)]
    [InlineData("completed", true)]
    [InlineData("expired", true)]
    [InlineData("halted", false)]
    [InlineData("paused", false)]
    [InlineData("pending", false)]
    public async Task Only_verified_nonrenewing_provider_terminal_events_release_matching_owner(string status, bool canStart)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var first = "first-" + caller.UserId;
        Assert.True(await store.BeginPurchaseAsync(caller.UserId, first, Subscription, "first", CancellationToken.None));
        await store.AttachPurchaseProviderObjectAsync(caller.UserId, first, "sub_" + first, "subscription", CancellationToken.None);
        var result = await LifecycleAsync(store, first, "sub_" + first, status, "initial",
            PaymentReconciliationService.SubscriptionLifecycleEvent(status));
        Assert.False(result.Duplicate);
        Assert.Equal(canStart, await store.BeginPurchaseAsync(caller.UserId, "second", Subscription, "second", CancellationToken.None));
        if (canStart)
        {
            // A later retry/event from the old subscription cannot unlock the new owner.
            await LifecycleAsync(store, first, "sub_" + first, status, "old-callback");
            Assert.False(await store.BeginPurchaseAsync(caller.UserId, "third", Subscription, "third", CancellationToken.None));
            Assert.Equal("second", (await db.Collection("accounts").Document(caller.UserId).GetSnapshotAsync())
                .GetValue<string>("subscriptionCheckoutPurchaseId"));
        }
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Mismatched_or_non_lifecycle_evidence_cannot_release_owner()
    {
        var (_, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var first = "first-" + caller.UserId;
        await store.BeginPurchaseAsync(caller.UserId, first, Subscription, "first", CancellationToken.None);
        await store.AttachPurchaseProviderObjectAsync(caller.UserId, first, "sub_" + first, "subscription", CancellationToken.None);
        await LifecycleAsync(store, first, "sub_wrong", "cancelled", "wrong-object");
        await LifecycleAsync(store, first, "sub_" + first, "cancelled", "wrong-event", "subscription.pending");
        Assert.False(await store.BeginPurchaseAsync(caller.UserId, "second", Subscription, "second", CancellationToken.None));
    }

    private static Task<PaymentWebhookResult> LifecycleAsync(FirestoreAccountStore store, string purchase,
        string subscription, string status, string suffix, string? eventType = null) => store.ApplyPaymentWebhookAsync(
            $"local-{Guid.NewGuid():N}-{suffix}", eventType ?? "subscription." + status, subscription,
            null, null, subscription, purchase, subscription, null, status, CancellationToken.None,
            providerProductSku: Subscription.Sku);
}
