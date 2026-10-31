using System.Reflection;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Financial evidence created before an admission fence remains accountable.
// Every case is guarded by the existing loopback-only EmulatorOnly helper;
// provider inspections below are immutable synthetic responses, never network.
[Collection("Local accounting emulator")]
public sealed class DeletedAccountFinancialContinuityEmulatorTests
{
    private const long InitialCredits = 1_000_000;
    private const long ReservedCredits = 100_000;
    private const long ChargedCredits = 25_000;
    private const long ProviderCost = 123;
    private static readonly ProviderUsage Usage = new(10, 5, 0, 0, 0, 0, 15, "synthetic-financial-evidence");
    private static readonly CatalogProduct CreditProduct = new(
        "local-financial-continuity", "credits", "Synthetic local financial continuity",
        4_900, "INR", 100_000_000, null, true);

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleting")]
    [InlineData("deleted")]
    public async Task Pre_fence_dispatched_reservation_can_record_completion_and_settle_exactly_once(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        const string requestId = "pre-fence-settlement";
        await ReserveAsync(store, caller, requestId);
        await store.MarkReservationProviderStartedAsync(caller.UserId, requestId, CancellationToken.None);
        await FenceAsync(account, status);

        await store.RecordProviderCompletionAsync(caller.UserId, requestId, Usage,
            ChargedCredits, ProviderCost, "completed", CancellationToken.None);
        var result = await store.SettleAsync(caller.UserId, requestId, Usage,
            ChargedCredits, ProviderCost, "completed", CancellationToken.None);
        Assert.True(result.Settled);
        Assert.Equal(ChargedCredits, result.ChargedCreditMicros);
        Assert.Equal(ReservedCredits - ChargedCredits, result.ReleasedCreditMicros);

        var repeated = await store.SettleAsync(caller.UserId, requestId, Usage,
            ChargedCredits, ProviderCost, "completed", CancellationToken.None);
        Assert.False(repeated.Settled);
        Assert.Equal(ChargedCredits, repeated.ChargedCreditMicros);
        await AssertSettledAsync(account, requestId, status);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleting")]
    [InlineData("deleted")]
    public async Task Pre_fence_reservation_can_release_once_only_with_existing_safe_evidence(
        string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        const string requestId = "pre-fence-release";
        await ReserveAsync(store, caller, requestId);
        await store.MarkReservationProviderStartedAsync(caller.UserId, requestId, CancellationToken.None);
        await store.RecordTerminalProviderRejectionAsync(caller.UserId, requestId, 429, CancellationToken.None);
        await FenceAsync(account, status);

        await store.ReleaseReservationAsync(caller.UserId, requestId, "provider_rejected", CancellationToken.None);
        await store.ReleaseReservationAsync(caller.UserId, requestId, "provider_rejected", CancellationToken.None);

        await AssertReleasedAsync(account, requestId, status);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleting")]
    [InlineData("deleted")]
    public async Task Deferred_usage_maintenance_can_settle_after_fence_without_rehydrating_identity(
        string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        const string requestId = "pre-fence-maintenance";
        await ReserveAsync(store, caller, requestId);
        await store.MarkReservationProviderStartedAsync(caller.UserId, requestId, CancellationToken.None);
        await store.RecordProviderCompletionAsync(caller.UserId, requestId, Usage,
            ChargedCredits, ProviderCost, "completed", CancellationToken.None);

        if (status == "deleted")
        {
            // Current orchestration blocks an active request, but deferred exact
            // evidence releases that slot without discarding its financial hold.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.DeleteAccountOperationalDataAsync(caller.UserId, CancellationToken.None));
            Assert.Equal("active", (await account.GetSnapshotAsync()).GetValue<string>("status"));
            Assert.Equal(caller.Email, (await account.GetSnapshotAsync()).GetValue<string>("email"));
        }
        await store.MarkReservationForReconciliationAsync(caller.UserId, requestId,
            "synthetic_acknowledgement_loss", CancellationToken.None);
        Assert.Equal(0L, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        Assert.Equal(ReservedCredits, (await account.GetSnapshotAsync()).GetValue<long>("reservedCreditMicros"));
        if (status == "deleted")
            await ApplyFenceOrExistingDeletionAsync(store, caller, account, status);
        else
            await FenceAsync(account, status);

        var reservation = account.Collection("reservations").Document(requestId);
        var service = new UsageReconciliationService(db, store, new SystemClock(),
            Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }),
            NullLogger<UsageReconciliationService>.Instance);
        for (var attempt = 0; attempt < 2; attempt++)
            await InvokeWorkerAsync(service, "ReconcileReservationAsync",
                await reservation.GetSnapshotAsync(), "synthetic-financial-usage-" + Guid.NewGuid().ToString("N"));

        await AssertSettledAsync(account, requestId, status);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleting", "credits")]
    [InlineData("deleting", "subscription")]
    [InlineData("deleted", "credits")]
    [InlineData("deleted", "subscription")]
    public async Task Legitimate_pre_fence_purchase_completion_and_retries_preserve_one_credit_and_account_fence(
        string status, string kind)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        var purchase = await CreatePurchaseAsync(store, caller, kind);
        if (status == "deleted" && purchase.Subscription)
            // Legacy tombstones may predate subscription-safe deletion. This
            // financial-continuity fixture is NOT proof of safe new deletion.
            await FenceAsync(account, status);
        else
            await ApplyFenceOrExistingDeletionAsync(store, caller, account, status);
        Assert.True((await account.Collection("purchases").Document(purchase.PurchaseId).GetSnapshotAsync()).Exists);
        var eventId = "synthetic-late-payment-" + caller.UserId;

        var first = await ApplyPaymentAsync(store, purchase, eventId);
        Assert.False(first.Duplicate);
        Assert.Equal("credited", first.Outcome);
        var sameEvent = await ApplyPaymentAsync(store, purchase, eventId);
        Assert.True(sameEvent.Duplicate);
        // A separate delivery/event for the same captured payment must not add
        // another allowance or ledger effect either.
        await ApplyPaymentAsync(store, purchase, eventId + "-second-delivery");

        await AssertPurchaseCreditedAsync(account, purchase, status);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleting")]
    [InlineData("deleted")]
    public async Task Synthetic_payment_maintenance_finishes_pre_fence_purchase_once_without_profile_rehydration(
        string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        var purchase = await CreatePurchaseAsync(store, caller, "credits");
        await ApplyFenceOrExistingDeletionAsync(store, caller, account, status);
        var provider = DispatchProxy.Create<IPaymentProvider, SyntheticInspectionProvider>();
        var inspections = (SyntheticInspectionProvider)provider;
        inspections.Purchase = purchase;
        var clock = new SystemClock();
        var subscriptions = new SubscriptionManagementService(db, provider, clock,
            NullLogger<SubscriptionManagementService>.Instance);
        var service = new PaymentReconciliationService(db, store, provider, clock,
            Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }),
            NullLogger<PaymentReconciliationService>.Instance, subscriptions);
        var reference = account.Collection("purchases").Document(purchase.PurchaseId);

        for (var attempt = 0; attempt < 2; attempt++)
            await InvokeWorkerAsync(service, "ReconcilePurchaseAsync",
                await reference.GetSnapshotAsync(), "synthetic-financial-payment-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(2, inspections.InspectionCalls);
        await AssertPurchaseCreditedAsync(account, purchase, status);
        Assert.Equal("consistent", (await reference.GetSnapshotAsync()).GetValue<string>("reconciliationStatus"));
    }

    private static DocumentReference Account(FirestoreDb db, CallerIdentity caller) =>
        db.Collection("accounts").Document(caller.UserId);

    private static Task<ReservationResult> ReserveAsync(FirestoreAccountStore store, CallerIdentity caller, string id) =>
        store.ReserveAsync(caller, id, "synthetic-financial-model", "synthetic-financial-pricing",
            ReservedCredits, 500_000, 100, 1, 1_000_000_000, CancellationToken.None);

    private static Task<WriteResult> FenceAsync(DocumentReference account, string status) =>
        account.UpdateAsync(new Dictionary<string, object>
        {
            ["status"] = status, ["email"] = "", ["displayName"] = "Deleted user",
        });

    private static async Task ApplyFenceOrExistingDeletionAsync(
        FirestoreAccountStore store, CallerIdentity caller, DocumentReference account, string status)
    {
        if (status == "deleted")
        {
            await store.BeginConfirmedAccountDeletionAsync(caller with
                { AuthenticationKind = "firebase", AuthenticatedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
            await store.DeleteAccountOperationalDataAsync(caller.UserId, CancellationToken.None);
            // Financial continuity fixture supplies successful identity deletion;
            // actual orchestration/identity ordering is tested independently.
            await store.CompleteAccountDeletionAsync(caller.UserId, CancellationToken.None);
        }
        else
            await FenceAsync(account, status);
    }

    private static async Task AssertProfileAndWalletAsync(DocumentReference account, string status, long available)
    {
        var snapshot = await account.GetSnapshotAsync();
        Assert.Equal(status, snapshot.GetValue<string>("status"));
        Assert.Equal("", snapshot.GetValue<string>("email"));
        Assert.Equal("Deleted user", snapshot.GetValue<string>("displayName"));
        Assert.Equal(available, snapshot.GetValue<long>("availableCreditMicros"));
        Assert.Equal(0L, snapshot.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(0L, snapshot.GetValue<long>("activeRequests"));
        Assert.Equal(0L, snapshot.GetValue<long>("creditDebtMicros"));
    }

    private static async Task AssertSettledAsync(DocumentReference account, string id, string status)
    {
        await AssertProfileAndWalletAsync(account, status, InitialCredits - ChargedCredits);
        var reservation = await account.Collection("reservations").Document(id).GetSnapshotAsync();
        Assert.Equal("settled", reservation.GetValue<string>("status"));
        Assert.Equal(ChargedCredits, reservation.GetValue<long>("chargedCreditMicros"));
        Assert.Equal(ReservedCredits - ChargedCredits, reservation.GetValue<long>("releasedCreditMicros"));
        Assert.True(reservation.GetValue<bool>("concurrencyReleased"));
        var usage = Assert.Single((await account.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Equal(id, usage.Id);
        Assert.Equal(ChargedCredits, usage.GetValue<long>("chargedCreditMicros"));
        Assert.Equal(ProviderCost, usage.GetValue<long>("providerCostUsdMicros"));
        Assert.Single((await account.Collection("ledger").WhereEqualTo("type", LedgerTypes.UsageSettlement)
            .GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("ledger").WhereEqualTo("type", LedgerTypes.ReservationRelease)
            .GetSnapshotAsync()).Documents);
    }

    private static async Task AssertReleasedAsync(DocumentReference account, string id, string status)
    {
        await AssertProfileAndWalletAsync(account, status, InitialCredits);
        var reservation = await account.Collection("reservations").Document(id).GetSnapshotAsync();
        Assert.Equal("released", reservation.GetValue<string>("status"));
        Assert.True(reservation.GetValue<bool>("concurrencyReleased"));
        Assert.Single((await account.Collection("ledger").WhereEqualTo("type", LedgerTypes.ReservationRelease)
            .GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("ledger").WhereEqualTo("type", LedgerTypes.UsageSettlement)
            .GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("usage").GetSnapshotAsync()).Documents);
    }

    private static async Task<PurchaseFixture> CreatePurchaseAsync(
        FirestoreAccountStore store, CallerIdentity caller, string kind)
    {
        var product = kind == "credits" ? CreditProduct : CreditProduct with
        {
            Kind = "subscription", RazorpayPlanId = "synthetic-plan",
        };
        var now = DateTimeOffset.UtcNow;
        var fixture = new PurchaseFixture(
            "synthetic-purchase-" + caller.UserId,
            (kind == "credits" ? "order_" : "sub_") + caller.UserId,
            "pay_" + caller.UserId,
            product,
            now.AddMinutes(-1),
            now.AddDays(30),
            now);
        Assert.True(await store.BeginPurchaseAsync(caller.UserId, fixture.PurchaseId, product,
            "synthetic-checkout-" + caller.UserId, CancellationToken.None));
        await store.AttachPurchaseProviderObjectAsync(caller.UserId, fixture.PurchaseId, fixture.ProviderObjectId,
            kind == "credits" ? "order" : "subscription", CancellationToken.None);
        return fixture;
    }

    private static Task<PaymentWebhookResult> ApplyPaymentAsync(
        FirestoreAccountStore store, PurchaseFixture purchase, string eventId) => store.ApplyPaymentWebhookAsync(
            eventId, purchase.Subscription ? "subscription.charged" : "order.paid",
            purchase.ProviderObjectId, purchase.PaymentId,
            purchase.Subscription ? null : purchase.ProviderObjectId,
            purchase.Subscription ? purchase.ProviderObjectId : null,
            purchase.PurchaseId, purchase.PaymentId, purchase.Product.AmountMinor,
            purchase.Subscription ? "active" : "paid", CancellationToken.None,
            purchase.Subscription ? purchase.CycleEnd : null,
            purchase.Product.Currency, purchase.Product.Sku);

    private static async Task AssertPurchaseCreditedAsync(DocumentReference account, PurchaseFixture purchase, string status)
    {
        await AssertProfileAndWalletAsync(account, status, InitialCredits + purchase.Product.CreditMicros);
        var snapshot = await account.Collection("purchases").Document(purchase.PurchaseId).GetSnapshotAsync();
        Assert.True(snapshot.GetValue<bool>("credited"));
        Assert.Equal(purchase.PaymentId, snapshot.GetValue<string>("providerPaymentId"));
        Assert.Single((await account.Collection("ledger").WhereEqualTo("type",
                purchase.Subscription ? LedgerTypes.MonthlyAllowance : LedgerTypes.PurchaseCredit)
            .GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("devices").GetSnapshotAsync()).Documents);
    }

    private static async Task InvokeWorkerAsync(object service, string methodName, DocumentSnapshot document, string runId)
    {
        var method = service.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(service, [document, runId, CancellationToken.None]));
        await task;
    }

    public sealed record PurchaseFixture(string PurchaseId, string ProviderObjectId, string PaymentId,
        CatalogProduct Product, DateTimeOffset CycleStart, DateTimeOffset CycleEnd, DateTimeOffset CapturedAt)
    {
        public bool Subscription => Product.Kind == "subscription";
        public ProviderPayment Payment => new(PaymentId, "captured", Product.AmountMinor, Product.Currency,
            Subscription ? null : ProviderObjectId, Subscription ? ProviderObjectId : null, true, CapturedAt,
            CapturedAt, CapturedAt);
    }

    public class SyntheticInspectionProvider : DispatchProxy
    {
        public PurchaseFixture Purchase { get; set; } = null!;
        public int InspectionCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            switch (targetMethod.Name)
            {
                case nameof(IPaymentProvider.InspectOrderAsync):
                    Assert.False(Purchase.Subscription);
                    Assert.Equal(Purchase.ProviderObjectId, (string)args[0]!);
                    InspectionCalls++;
                    return Task.FromResult(new ProviderOrderInspection(Purchase.ProviderObjectId, "paid",
                        Purchase.Product.AmountMinor, Purchase.Product.Currency, Purchase.PurchaseId, Purchase.Product.Sku,
                        [Purchase.Payment]));
                case nameof(IPaymentProvider.InspectSubscriptionAsync):
                    Assert.True(Purchase.Subscription);
                    Assert.Equal(Purchase.ProviderObjectId, (string)args[0]!);
                    InspectionCalls++;
                    return Task.FromResult(new ProviderSubscriptionInspection(Purchase.ProviderObjectId, "active",
                        Purchase.PurchaseId, Purchase.Product.Sku, Purchase.CycleStart, Purchase.CycleEnd,
                        [Purchase.Payment]));
                case nameof(IPaymentProvider.InspectRefundsAsync):
                    Assert.Equal(Purchase.PaymentId, (string)args[0]!);
                    return Task.FromResult<IReadOnlyList<ProviderRefund>>([]);
                case nameof(IPaymentProvider.InspectDisputesAsync):
                    Assert.Equal(Purchase.PaymentId, (string)args[0]!);
                    return Task.FromResult<IReadOnlyList<ProviderDispute>>([]);
                default:
                    // In particular, no creation/cancellation/provider call can
                    // be hidden by the inspection-only offline test double.
                    throw new InvalidOperationException("Unexpected synthetic provider operation.");
            }
        }
    }
}
