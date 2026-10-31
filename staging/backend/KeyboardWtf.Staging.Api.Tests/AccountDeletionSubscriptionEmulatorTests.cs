using System.Reflection;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class AccountDeletionSubscriptionEmulatorTests
{
    private static readonly CatalogProduct Product = new(
        "local-monthly", "subscription", "Synthetic subscription", 99_900, "INR", 500_000_000, "plan_local", true);

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Deletion_stops_renewal_before_cleanup_and_retains_financial_documents()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("first", "sub_first");
        var purchase = await f.Purchase("first").GetSnapshotAsync();
        var ledger = await f.Account.Collection("ledger").GetSnapshotAsync();
        var balance = (await f.Account.GetSnapshotAsync()).GetValue<long>("availableCreditMicros");
        await f.DeleteAsync();
        Assert.Equal(new[] { "inspect:sub_first", "cancel:sub_first", "identity-delete" }, f.Provider.Events);
        var deleted = await f.Account.GetSnapshotAsync();
        Assert.Equal("deleted", deleted.GetValue<string>("status"));
        Assert.Equal("", deleted.GetValue<string>("email"));
        Assert.Equal(balance, deleted.GetValue<long>("availableCreditMicros"));
        Assert.Equal(ledger.Count, (await f.Account.Collection("ledger").GetSnapshotAsync()).Count);
        var retained = await f.Purchase("first").GetSnapshotAsync();
        foreach (var field in new[] { "creditMicros", "amountMinor", "credited", "status" })
            Assert.Equal(purchase.ToDictionary()[field], retained.ToDictionary()[field]);
        Assert.Equal("cancelled", retained.GetValue<string>("subscriptionTerminalProviderStatus"));
        Assert.Equal(1, f.Identity.Deletes);
        await f.DeleteAsync();
        Assert.Equal(1, f.Provider.Cancels);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("creating"), InlineData("provider_failed"), InlineData("cancelled"), InlineData("expired")]
    public async Task Missing_provider_identity_blocks_deletion_even_if_locally_terminal(string status)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("unknown", "");
        await f.Purchase("unknown").UpdateAsync("status", status);
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Equal(0, f.Identity.Deletes);
        Assert.Empty(f.Provider.Events);
        var state = await f.Account.GetSnapshotAsync();
        Assert.Equal("deleting", state.GetValue<string>("status"));
        Assert.NotEqual("", state.GetValue<string>("email"));
        Assert.False(state.ContainsField("personalDataDeleted"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Store.BeginPurchaseAsync(
            f.Caller.UserId, "new", Product, "new", default));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task In_flight_create_can_attach_after_fence_then_retry_stops_it()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("delayed", "");
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        await f.Store.AttachPurchaseProviderObjectAsync(f.Caller.UserId, "delayed", "sub_delayed", "subscription", default);
        f.Provider.Bind("delayed", "sub_delayed");
        await f.DeleteAsync();
        Assert.Equal(1, f.Provider.Cancels);
        Assert.Equal(1, f.Identity.Deletes);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("id"), InlineData("purchase"), InlineData("sku"), InlineData("environment")]
    public async Task Unbound_remote_object_is_never_cancelled_or_accepted_as_proof(string wrong)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("bound", "sub_bound");
        f.Provider.WrongBinding = wrong;
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Equal(0, f.Provider.Cancels);
        Assert.Equal(0, f.Identity.Deletes);
        Assert.False((await f.Purchase("bound").GetSnapshotAsync()).ContainsField("subscriptionTerminalVerifiedAt"));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("id"), InlineData("purchase"), InlineData("sku"), InlineData("environment"), InlineData("missing")]
    public async Task Unbound_cancel_ack_needs_independently_bound_terminal_inspection(string wrong)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("after", "sub_after");
        f.Provider.CancelReplyBinding = wrong;
        await f.DeleteAsync();
        Assert.Equal(new[] { "inspect:sub_after", "cancel:sub_after", "inspect:sub_after", "identity-delete" }, f.Provider.Events);
        Assert.Equal(1, f.Identity.Deletes);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("id"), InlineData("purchase"), InlineData("sku"), InlineData("environment"), InlineData("missing")]
    public async Task Ownership_changed_after_initial_inspection_cannot_be_persisted_as_proof(string wrong)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("changed", "sub_changed");
        f.Provider.CancelReplyBinding = wrong;
        f.Provider.BindingAfterCancel = wrong;
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Equal(0, f.Identity.Deletes);
        Assert.False((await f.Purchase("changed").GetSnapshotAsync()).ContainsField("subscriptionTerminalVerifiedAt"));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("id"), InlineData("status"), InlineData("time")]
    public async Task Stale_or_nonterminal_stored_proof_cannot_skip_verification(string wrong)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("stale", "sub_stale");
        await f.Purchase("stale").UpdateAsync(new Dictionary<string, object>
        {
            ["subscriptionTerminalProviderObjectId"] = wrong == "id" ? "sub_other" : "sub_stale",
            ["subscriptionTerminalProviderStatus"] = wrong == "status" ? "paused" : "cancelled",
            ["subscriptionTerminalVerifiedAt"] = wrong == "time" ? "invalid" : Timestamp.GetCurrentTimestamp(),
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.DeleteAccountOperationalDataAsync(f.Caller.UserId, default));
        await f.DeleteAsync();
        Assert.Equal(1, f.Provider.Cancels);
        Assert.Equal(1, f.Identity.Deletes);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("userId"), InlineData("provider"), InlineData("providerObjectType")]
    [InlineData("environment"), InlineData("providerObjectId"), InlineData("productSku")]
    public async Task Invalid_local_subscription_ownership_is_not_sent_to_provider(string field)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("invalid", "sub_invalid");
        await f.Purchase("invalid").UpdateAsync(field, field == "productSku" ? "" : "other");
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Empty(f.Provider.Events);
        Assert.Equal(0, f.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Conflicting_provider_purchase_ownership_requires_review_without_remote_effect()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("a", "sub_duplicate");
        await f.AddAsync("b", "sub_duplicate");
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Empty(f.Provider.Events);
        Assert.Equal(0, f.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Lost_cancel_acknowledgement_is_inspected_and_retry_does_not_repeat_cancel()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("ack", "sub_ack");
        f.Provider.LoseCancelAcknowledgement = true;
        await f.DeleteAsync();
        Assert.Equal(new[] { "inspect:sub_ack", "cancel:sub_ack", "inspect:sub_ack", "identity-delete" }, f.Provider.Events);
        await f.DeleteAsync();
        Assert.Equal(1, f.Provider.Cancels);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Multiple_legacy_subscriptions_and_partial_failure_preserve_progress_until_retry()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("one", "sub_one");
        await f.AddAsync("two", "sub_two");
        await f.AddAsync("three", "sub_three");
        f.Provider.FailingId = "sub_two";
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Equal(0, f.Identity.Deletes);
        Assert.Contains("cancel:sub_one", f.Provider.Events);
        Assert.Contains("cancel:sub_three", f.Provider.Events);
        f.Provider.FailingId = null;
        await f.DeleteAsync();
        Assert.Equal(1, f.Identity.Deletes);
        Assert.Equal(1, f.Provider.Events.Count(value => value == "cancel:sub_one"));
        Assert.Equal(1, f.Provider.Events.Count(value => value == "cancel:sub_three"));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("active"), InlineData("pending"), InlineData("authenticated"), InlineData("paused"), InlineData("halted")]
    public async Task Nonterminal_cancel_result_never_allows_success(string status)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("nonterminal", "sub_nonterminal");
        f.Provider.CancelReturnsStatus = status;
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Equal(0, f.Identity.Deletes);
        Assert.False((await f.Account.GetSnapshotAsync()).ContainsField("personalDataDeleted"));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("cancelled"), InlineData("completed"), InlineData("expired")]
    public async Task Already_terminal_remote_subscription_needs_no_cancel(string status)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("terminal", "sub_terminal");
        f.Provider.States["sub_terminal"] = status;
        await f.DeleteAsync();
        Assert.Equal(0, f.Provider.Cancels);
        Assert.Equal(1, f.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Unmatched_active_pointer_or_missing_root_with_subscription_fails_closed()
    {
        var f = await Fixture.CreateAsync();
        await f.Account.UpdateAsync("activeSubscriptionProviderObjectId", "sub_orphan");
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Empty(f.Provider.Events);
        Assert.Equal(0, f.Identity.Deletes);
        var orphan = await Fixture.CreateAsync();
        await orphan.AddAsync("orphan", "sub_orphan");
        await orphan.Account.DeleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(orphan.DeleteAsync);
        Assert.False((await orphan.Account.GetSnapshotAsync()).Exists);
        Assert.Equal(0, orphan.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task History_overflow_and_active_work_fail_before_deletion_or_provider_effect()
    {
        var f = await Fixture.CreateAsync();
        for (var index = 0; index < 65; index++) await f.AddAsync("p" + index, "sub_p" + index);
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Equal(0, f.Identity.Deletes);
        Assert.Empty(f.Provider.Events);
        var busy = await Fixture.CreateAsync();
        await busy.Account.UpdateAsync("activeRequests", 1L);
        await Assert.ThrowsAsync<InvalidOperationException>(busy.DeleteAsync);
        Assert.Equal("active", (await busy.Account.GetSnapshotAsync()).GetValue<string>("status"));
        Assert.Empty(busy.Provider.Events);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Final_store_guard_cannot_skip_provider_proof_and_attachment_is_immutable()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("immutable", "sub_immutable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.DeleteAccountOperationalDataAsync(f.Caller.UserId, default));
        await f.Store.AttachPurchaseProviderObjectAsync(f.Caller.UserId, "immutable", "sub_immutable", "subscription", default);
        await f.Purchase("immutable").UpdateAsync("status", "active");
        await f.Store.AttachPurchaseProviderObjectAsync(f.Caller.UserId, "immutable", "sub_immutable", "subscription", default);
        Assert.Equal("active", (await f.Purchase("immutable").GetSnapshotAsync()).GetValue<string>("status"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.AttachPurchaseProviderObjectAsync(
            f.Caller.UserId, "immutable", "sub_other", "subscription", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.AttachPurchaseProviderObjectAsync(
            f.Caller.UserId, "immutable", "sub_immutable", "order", default));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(false), InlineData(true)]
    public async Task Unavailable_or_wrong_provider_never_dispatches_or_completes(bool wrongProvider)
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("config", "sub_config");
        f.Provider.Configured = wrongProvider;
        f.Provider.Name = wrongProvider ? "other" : "razorpay";
        await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
        Assert.Empty(f.Provider.Events);
        Assert.Equal(0, f.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Delayed_original_creation_spans_fence_and_is_joined_before_retry()
    {
        var f = await Fixture.CreateAsync();
        Assert.True(await f.Store.BeginPurchaseAsync(f.Caller.UserId, "delayedOriginal", Product, "original", default));
        var providerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task OriginalCreateAsync()
        {
            providerStarted.SetResult(true);
            await providerRelease.Task;
            // Synthetic remote create response; only the real store attachment is exercised.
            f.Provider.Bind("delayedOriginal", "sub_original");
            await f.Store.AttachPurchaseProviderObjectAsync(f.Caller.UserId, "delayedOriginal", "sub_original", "subscription", default);
        }
        var original = OriginalCreateAsync();
        try
        {
            await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidOperationException>(f.DeleteAsync);
            Assert.False(original.IsCompleted);
            Assert.Equal(0, f.Identity.Deletes);
            Assert.False((await f.Account.GetSnapshotAsync()).ContainsField("personalDataDeleted"));
        }
        finally
        {
            providerRelease.TrySetResult(true);
            await original.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await f.DeleteAsync();
        Assert.Equal(1, f.Provider.Cancels);
        Assert.Equal(1, f.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Checkout_admission_and_deletion_fence_serialize_on_the_same_account()
    {
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var f = await Fixture.CreateAsync();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> CheckoutAsync()
            {
                await start.Task;
                try { return await f.Store.BeginPurchaseAsync(f.Caller.UserId, "race", Product, "race", default); }
                catch (UnauthorizedAccessException) { return false; }
            }
            async Task<AccountDeletionSubscriptions> FenceAsync()
            {
                await start.Task;
                return await ((IAccountStore)f.Store).BeginAccountDeletionAsync(f.Caller.UserId, default);
            }
            var checkout = CheckoutAsync();
            var fence = FenceAsync();
            start.SetResult(true);
            await Task.WhenAll(checkout, fence);
            Assert.Equal("deleting", (await f.Account.GetSnapshotAsync()).GetValue<string>("status"));
            Assert.Equal(await checkout, (await f.Purchase("race").GetSnapshotAsync()).Exists);
            Assert.Equal(await checkout, (await fence).HasUnresolvedSubscription);
            if (await checkout)
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.DeleteAccountOperationalDataAsync(f.Caller.UserId, default));
            else
                await f.Store.DeleteAccountOperationalDataAsync(f.Caller.UserId, default);
            Assert.Empty(f.Provider.Events);
        }
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Request_cancelled_after_remote_success_keeps_identity_and_retry_inspects_without_replay()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("token", "sub_token");
        using var cancellation = new CancellationTokenSource();
        f.Provider.AfterCancel = cancellation.Cancel;
        var service = new AccountDeletionService(f.Store, f.Identity, new SystemClock(), f.Payments);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteAsync(f.Caller, true, cancellation.Token));
        Assert.Equal(0, f.Identity.Deletes);
        Assert.False((await f.Purchase("token").GetSnapshotAsync()).ContainsField("subscriptionTerminalVerifiedAt"));
        f.Provider.AfterCancel = null;
        await f.DeleteAsync();
        Assert.Equal(1, f.Provider.Cancels);
        Assert.Equal(1, f.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Proof_write_cancelled_after_provider_success_is_not_false_deletion_success()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("write", "sub_write");
        using var cancellation = new CancellationTokenSource();
        var wrapped = DispatchProxy.Create<IAccountStore, CancelProofStore>();
        var proxy = (CancelProofStore)wrapped;
        proxy.Target = f.Store;
        proxy.Cancel = cancellation.Cancel;
        var service = new AccountDeletionService(wrapped, f.Identity, new SystemClock(), f.Payments);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteAsync(f.Caller, true, cancellation.Token));
        Assert.Equal(0, f.Identity.Deletes);
        Assert.False((await f.Purchase("write").GetSnapshotAsync()).ContainsField("subscriptionTerminalVerifiedAt"));
        await f.DeleteAsync();
        Assert.Equal(1, f.Provider.Cancels);
        Assert.Equal(1, f.Identity.Deletes);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Late_captured_payment_after_verified_cancellation_stays_exactly_once_without_refund()
    {
        var f = await Fixture.CreateAsync();
        // Payment/fulfillment IDs are globally unique in the real provider, not
        // just inside an account. Keep repeated emulator runs independent too.
        var suffix = Crypto.Sha256(f.Caller.UserId)[..24];
        var purchaseId = "late" + suffix;
        var providerId = "sub_" + suffix;
        var paymentId = "pay_" + suffix;
        await f.AddAsync(purchaseId, providerId);
        var originalBalance = (await f.Account.GetSnapshotAsync()).GetValue<long>("availableCreditMicros");
        await f.DeleteAsync();
        for (var retry = 0; retry < 2; retry++)
            await f.Store.ApplyPaymentWebhookAsync("late-" + f.Caller.UserId + retry, "subscription.charged", providerId,
                paymentId, null, providerId, purchaseId, paymentId, Product.AmountMinor, "active", default,
                DateTimeOffset.UtcNow.AddDays(30), Product.Currency, Product.Sku);
        var account = await f.Account.GetSnapshotAsync();
        Assert.Equal("deleted", account.GetValue<string>("status"));
        Assert.Equal("", account.GetValue<string>("email"));
        Assert.Equal(originalBalance + Product.CreditMicros, account.GetValue<long>("availableCreditMicros"));
        Assert.Single((await f.Account.Collection("ledger").WhereEqualTo("type", LedgerTypes.MonthlyAllowance).GetSnapshotAsync()).Documents);
        Assert.Equal(1, f.Provider.Cancels);
        Assert.Equal("cancelled", (await f.Purchase(purchaseId).GetSnapshotAsync()).GetValue<string>("subscriptionTerminalProviderStatus"));
    }

    private sealed record Fixture(FirestoreDb Db, FirestoreAccountStore Store, CallerIdentity Caller,
        FakeProvider Provider, IPaymentProvider Payments, FakeIdentity Identity)
    {
        public DocumentReference Account => Db.Collection("accounts").Document(Caller.UserId);
        public DocumentReference Purchase(string id) => Account.Collection("purchases").Document(id);
        public static async Task<Fixture> CreateAsync()
        {
            var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            caller = caller with { AuthenticationKind = "firebase", AuthenticatedAt = DateTimeOffset.UtcNow };
            var payments = DispatchProxy.Create<IPaymentProvider, FakeProvider>();
            var provider = (FakeProvider)payments;
            return new(db, store, caller, provider, payments, new FakeIdentity(provider.Events));
        }
        public async Task AddAsync(string id, string providerId)
        {
            // Deliberately create legacy multiple purchases, not bypass the new-checkout policy in production.
            await Purchase(id).SetAsync(new Dictionary<string, object>
            {
                ["purchaseId"] = id, ["userId"] = Caller.UserId, ["kind"] = "subscription", ["provider"] = "razorpay",
                ["productSku"] = Product.Sku, ["environment"] = "staging", ["providerObjectId"] = providerId,
                ["providerObjectType"] = string.IsNullOrEmpty(providerId) ? "" : "subscription",
                ["status"] = "creating", ["amountMinor"] = Product.AmountMinor,
                ["currency"] = Product.Currency, ["creditMicros"] = Product.CreditMicros, ["credited"] = false,
            });
            if (providerId.Length > 0) Provider.Bind(id, providerId);
        }
        public Task DeleteAsync() => new AccountDeletionService(Store, Identity, new SystemClock(), Payments)
            .DeleteAsync(Caller, true, default);
    }

    public class FakeProvider : DispatchProxy
    {
        public List<string> Events { get; } = [];
        public Dictionary<string, string> States { get; } = [];
        private readonly Dictionary<string, string> _purchases = [];
        public string? WrongBinding { get; set; }
        public string? CancelReplyBinding { get; set; }
        public string? BindingAfterCancel { get; set; }
        public string? FailingId { get; set; }
        public string? CancelReturnsStatus { get; set; }
        public bool LoseCancelAcknowledgement { get; set; }
        public bool Configured { get; set; } = true;
        public string Name { get; set; } = "razorpay";
        public Action? AfterCancel { get; set; }
        public int Cancels { get; private set; }
        public void Bind(string purchase, string id) { _purchases[id] = purchase; States[id] = "active"; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(args);
            if (method.Name == "get_IsConfigured") return Configured;
            if (method.Name == "get_Name") return Name;
            var id = (string)args[0]!;
            var cancelling = method.Name == nameof(IPaymentProvider.CancelSubscriptionImmediatelyAsync);
            if (method.Name == nameof(IPaymentProvider.InspectSubscriptionCancellationStateAsync))
                Events.Add("inspect:" + id);
            else if (method.Name == nameof(IPaymentProvider.CancelSubscriptionImmediatelyAsync))
            {
                Events.Add("cancel:" + id); Cancels++;
                if (id != FailingId) States[id] = CancelReturnsStatus ?? "cancelled";
                AfterCancel?.Invoke();
                if (((CancellationToken)args[1]!).IsCancellationRequested)
                    return Task.FromCanceled<ProviderSubscriptionCancellation>((CancellationToken)args[1]!);
                if (LoseCancelAcknowledgement)
                    return Task.FromException<ProviderSubscriptionCancellation>(new IOException("Synthetic acknowledgement loss."));
            }
            else throw new InvalidOperationException("Unrelated payment/refund call.");
            if (id == FailingId) return Task.FromException<ProviderSubscriptionCancellation>(new IOException("Synthetic provider outage."));
            var wrong = cancelling ? CancelReplyBinding ?? WrongBinding
                : Cancels > 0 ? BindingAfterCancel ?? WrongBinding : WrongBinding;
            return Task.FromResult(new ProviderSubscriptionCancellation(wrong == "id" ? "sub_other" : id,
                States[id], false, null, null, null,
                wrong == "missing" ? null : wrong == "purchase" ? "other" : _purchases[id],
                wrong == "missing" ? null : wrong == "sku" ? "other" : Product.Sku,
                wrong == "missing" ? null : wrong == "environment" ? "production" : "staging"));
        }
    }

    public class CancelProofStore : DispatchProxy
    {
        public IAccountStore Target { get; set; } = null!;
        public Action Cancel { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(args);
            if (method.Name == nameof(IAccountStore.RecordAccountDeletionSubscriptionStoppedAsync))
            {
                Cancel();
                return Task.FromCanceled((CancellationToken)args[3]!);
            }
            return method.Invoke(Target, args);
        }
    }

    private sealed class FakeIdentity(List<string> events) : IIdentityService
    {
        public int Deletes { get; private set; }
        public Task<CallerIdentity?> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken token) => throw new NotSupportedException();
        public Task<CallerIdentity> VerifyFirebaseAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteFirebaseUserAsync(string userId, CancellationToken token)
        { Deletes++; events.Add("identity-delete"); return Task.CompletedTask; }
    }
}
