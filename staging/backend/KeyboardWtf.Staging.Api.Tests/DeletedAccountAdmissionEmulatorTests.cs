using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Staging.Api.Tests;

// Every fixture uses the existing loopback-only, EmulatorOnly helper and a new
// synthetic GUID account. No Firebase, HTTP provider, credentials, or cloud fallback.
[Collection("Local accounting emulator")]
public sealed class DeletedAccountAdmissionEmulatorTests
{
    private readonly ITestOutputHelper _output;

    public DeletedAccountAdmissionEmulatorTests(ITestOutputHelper output) => _output = output;

    private static readonly CatalogProduct Subscription = new(
        "local-deletion-monthly", "subscription", "Synthetic local subscription",
        99_900, "INR", 500_000_000, "plan_local", true);
    private static readonly CatalogProduct Credits = Subscription with { Kind = "credits", RazorpayPlanId = null };

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    [InlineData("deleting", false)]
    [InlineData("deleting", true)]
    [InlineData("suspended", false)]
    [InlineData("suspended", true)]
    public async Task Nonactive_identity_sync_rejects_changed_and_unchanged_identity_without_writes(
        string status, bool unchangedIdentity)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        await FenceAsync(account, status);
        var before = await account.GetSnapshotAsync();
        // The identical identity exercises the optimistic no-write fast path;
        // it must not turn a nonactive account into a successfully verified caller.
        var attemptedCaller = unchangedIdentity
            ? caller with { Email = "", DisplayName = "Deleted user" }
            : caller;

        await AssertAdmissionRejectedAsync(() => store.EnsureAccountAsync(attemptedCaller, CancellationToken.None));

        await AssertUnchangedAsync(before);
        var after = await account.GetSnapshotAsync();
        Assert.Equal("", after.GetValue<string>("email"));
        Assert.Equal("Deleted user", after.GetValue<string>("displayName"));
        Assert.Equal(status, after.GetValue<string>("status"));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted")]
    [InlineData("deleting")]
    [InlineData("suspended")]
    public async Task Nonactive_login_authorization_keeps_pending_attempt_unowned(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var login = await CreateLoginAsync(store);
        var account = Account(db, caller);
        await FenceAsync(account, status);
        var accountBefore = await account.GetSnapshotAsync();
        var attemptBefore = await Attempt(db, login).GetSnapshotAsync();

        await AssertAdmissionRejectedAsync(() => AuthorizeAsync(store, caller, login));

        await AssertUnchangedAsync(accountBefore);
        await AssertUnchangedAsync(attemptBefore);
        Assert.False((await Attempt(db, login).GetSnapshotAsync()).ContainsField("userId"));
        await AssertNoSessionsOrDevicesAsync(db, caller);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted")]
    [InlineData("deleting")]
    [InlineData("suspended")]
    public async Task Nonactive_login_exchange_keeps_authorized_code_and_creates_no_session(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var login = await CreateLoginAsync(store);
        var authorized = await AuthorizeAsync(store, caller, login);
        var account = Account(db, caller);
        await FenceAsync(account, status);
        var accountBefore = await account.GetSnapshotAsync();
        var attemptBefore = await Attempt(db, login).GetSnapshotAsync();

        await AssertAdmissionRejectedAsync(() => ExchangeAsync(store, login, authorized.AuthorizationCode));

        await AssertUnchangedAsync(accountBefore);
        await AssertUnchangedAsync(attemptBefore);
        Assert.Equal("authorized", (await Attempt(db, login).GetSnapshotAsync()).GetValue<string>("status"));
        await AssertNoSessionsOrDevicesAsync(db, caller);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted")]
    [InlineData("deleting")]
    [InlineData("suspended")]
    public async Task Nonactive_refresh_does_not_rotate_session_or_recreate_purged_device(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var tokens = await CreateSessionAsync(store, caller);
        var account = Account(db, caller);
        await FenceAsync(account, status);
        // Leave the previously valid session in place to exercise the cleanup
        // race, but remove its device as operational purging would do.
        await account.Collection("devices").Document(tokens.DeviceId).DeleteAsync();
        var accountBefore = await account.GetSnapshotAsync();
        var sessionBefore = await db.Collection("desktopSessions").Document(tokens.SessionId).GetSnapshotAsync();

        await AssertAdmissionRejectedAsync(() => store.RefreshSessionAsync(
            tokens.RefreshToken, TimeSpan.FromMinutes(10), TimeSpan.FromDays(1), CancellationToken.None));

        await AssertUnchangedAsync(accountBefore);
        await AssertUnchangedAsync(sessionBefore);
        Assert.Single((await Sessions(db, caller).GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("devices").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted", "credits")]
    [InlineData("deleted", "subscription")]
    [InlineData("deleting", "credits")]
    [InlineData("deleting", "subscription")]
    [InlineData("suspended", "credits")]
    [InlineData("suspended", "subscription")]
    public async Task Nonactive_checkout_creates_no_purchase_or_idempotency_record(string status, string kind)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        await FenceAsync(account, status);
        var before = await account.GetSnapshotAsync();
        var key = "local-deletion-checkout-" + Guid.NewGuid().ToString("N");
        var purchaseId = "local-deletion-purchase-" + Guid.NewGuid().ToString("N");
        var idempotency = db.Collection("idempotency").Document(Crypto.Sha256($"checkout:{caller.UserId}:{key}"));

        await AssertAdmissionRejectedAsync(() => store.BeginPurchaseAsync(
            caller.UserId, purchaseId, kind == "credits" ? Credits : Subscription, key, CancellationToken.None));

        await AssertUnchangedAsync(before);
        Assert.Empty((await account.Collection("purchases").GetSnapshotAsync()).Documents);
        Assert.False((await idempotency.GetSnapshotAsync()).Exists);
        Assert.False((await account.GetSnapshotAsync()).ContainsField("subscriptionCheckoutPurchaseId"));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    [InlineData("deleting", false)]
    [InlineData("deleting", true)]
    [InlineData("suspended", false)]
    [InlineData("suspended", true)]
    public async Task Nonactive_review_insert_or_update_leaves_public_content_and_aggregate_unchanged(
        string status, bool existingReview)
    {
        var (db, _, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var reviews = Reviews(db);
        if (existingReview)
            await reviews.UpsertAsync(caller, 4, "Original synthetic local review.", CancellationToken.None);
        var account = Account(db, caller);
        await FenceAsync(account, status);
        var accountBefore = await account.GetSnapshotAsync();
        var reviewBefore = await Review(db, caller).GetSnapshotAsync();
        var statsBefore = await db.Collection("reviewMetadata").Document("public").GetSnapshotAsync();

        // Keep the pre-fence caller to model a request authenticated before
        // deletion; authorization must be checked again in the write transaction.
        await AssertAdmissionRejectedAsync(() => reviews.UpsertAsync(
            caller, 5, "Rejected replacement synthetic local review.", CancellationToken.None));

        await AssertUnchangedAsync(accountBefore);
        await AssertUnchangedAsync(reviewBefore);
        await AssertUnchangedAsync(statsBefore);
        if (existingReview)
        {
            var after = await Review(db, caller).GetSnapshotAsync();
            Assert.Equal(4L, after.GetValue<long>("rating"));
            Assert.Equal("Original synthetic local review.", after.GetValue<string>("text"));
            Assert.Equal(caller.DisplayName, after.GetValue<string>("reviewerDisplayName"));
        }
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted")]
    [InlineData("deleting")]
    [InlineData("suspended")]
    public async Task Existing_ai_reservation_guard_rejects_nonactive_without_account_or_reservation_writes(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        await FenceAsync(account, status);
        var before = await account.GetSnapshotAsync();
        var requestId = "local-deletion-ai-" + Guid.NewGuid().ToString("N");

        // Preserve the established reservation exception contract, not the new
        // UnauthorizedAccessException contract used by admission guards above.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReserveAsync(store, caller, requestId));

        await AssertUnchangedAsync(before);
        Assert.Empty((await account.Collection("reservations").GetSnapshotAsync()).Documents);
        Assert.False((await db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-reserve:{caller.UserId}:{requestId}")).GetSnapshotAsync()).Exists);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted")]
    [InlineData("deleting")]
    [InlineData("suspended")]
    public async Task Existing_desktop_authentication_guard_returns_null_without_writes_for_nonactive_account(string status)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var tokens = await CreateSessionAsync(store, caller);
        var account = Account(db, caller);
        await FenceAsync(account, status);
        var accountBefore = await account.GetSnapshotAsync();
        var sessionBefore = await db.Collection("desktopSessions").Document(tokens.SessionId).GetSnapshotAsync();
        var deviceBefore = await account.Collection("devices").Document(tokens.DeviceId).GetSnapshotAsync();

        Assert.Null(await store.AuthenticateDesktopTokenAsync(tokens.AccessToken, CancellationToken.None));

        await AssertUnchangedAsync(accountBefore);
        await AssertUnchangedAsync(sessionBefore);
        await AssertUnchangedAsync(deviceBefore);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("active", false)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task Active_and_legacy_null_or_missing_status_keep_identity_and_desktop_login_behavior(
        string? status, bool storedNull)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        await SetStatusAsync(account, status, storedNull);
        var before = await account.GetSnapshotAsync();
        await store.EnsureAccountAsync(caller, CancellationToken.None);
        await AssertUnchangedAsync(before);
        var renamed = caller with { DisplayName = "Updated synthetic local identity" };
        await store.EnsureAccountAsync(renamed, CancellationToken.None);
        Assert.Equal(renamed.DisplayName, (await account.GetSnapshotAsync()).GetValue<string>("displayName"));

        var tokens = await CreateSessionAsync(store, renamed);
        var authenticated = await store.AuthenticateDesktopTokenAsync(tokens.AccessToken, CancellationToken.None);
        Assert.NotNull(authenticated);
        Assert.Equal(caller.UserId, authenticated.UserId);
        var refreshed = await store.RefreshSessionAsync(
            tokens.RefreshToken, TimeSpan.FromMinutes(10), TimeSpan.FromDays(1), CancellationToken.None);
        Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(tokens.SessionId, refreshed.SessionId);
        Assert.NotNull(await store.AuthenticateDesktopTokenAsync(refreshed.AccessToken, CancellationToken.None));
        Assert.Equal("consumed", (await db.Collection("desktopLoginAttempts")
            .WhereEqualTo("userId", caller.UserId).GetSnapshotAsync()).Documents.Single().GetValue<string>("status"));
        Assert.Single((await Sessions(db, caller).GetSnapshotAsync()).Documents);
        Assert.Single((await account.Collection("devices").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("active", false)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task Active_and_legacy_null_or_missing_status_keep_checkout_review_and_ai_admission_behavior(
        string? status, bool storedNull)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var account = Account(db, caller);
        await SetStatusAsync(account, status, storedNull);
        Assert.True(await store.BeginPurchaseAsync(caller.UserId, "local-credit-control", Credits,
            "local-credit-control", CancellationToken.None));
        Assert.True(await store.BeginPurchaseAsync(caller.UserId, "local-subscription-control", Subscription,
            "local-subscription-control", CancellationToken.None));
        Assert.Equal(2, (await account.Collection("purchases").GetSnapshotAsync()).Count);

        var reviews = Reviews(db);
        var before = await reviews.GetPageAsync(1, null, CancellationToken.None);
        var review = await reviews.UpsertAsync(caller, 5, "Accepted synthetic local review.", CancellationToken.None);
        Assert.Equal("Accepted synthetic local review.", review.Text);
        Assert.Equal(before.ReviewCount + 1, (await reviews.GetPageAsync(1, null, CancellationToken.None)).ReviewCount);
        Assert.True(await reviews.DeleteOwnAsync(caller.UserId, CancellationToken.None));
        Assert.Equal(before.ReviewCount, (await reviews.GetPageAsync(1, null, CancellationToken.None)).ReviewCount);

        Assert.True((await ReserveAsync(store, caller, "local-active-control")).Created);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted")]
    [InlineData("deleting")]
    public async Task Fence_racing_changed_identity_sync_never_restores_profile_after_fence(string status)
    {
        var outcomes = new List<RaceOutcome>();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            var account = Account(db, caller);
            var changed = caller with
            {
                Email = "changed-" + caller.UserId + "@example.invalid",
                DisplayName = "Changed synthetic local race identity",
            };
            var race = await RaceFenceAsync(account, status,
                () => store.EnsureAccountAsync(changed, CancellationToken.None));
            outcomes.Add(race.Outcome);

            await AssertFencedProfileAsync(account, status, race.Fence);
            var beforeReplay = await account.GetSnapshotAsync();
            await AssertAdmissionRejectedAsync(() => store.EnsureAccountAsync(changed, CancellationToken.None));
            await AssertUnchangedAsync(beforeReplay);
        }
        ReportOutcomes("identity:" + status, outcomes);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted", "authorize")]
    [InlineData("deleted", "exchange")]
    [InlineData("deleted", "refresh")]
    [InlineData("deleting", "authorize")]
    [InlineData("deleting", "exchange")]
    [InlineData("deleting", "refresh")]
    public async Task Fence_racing_desktop_admission_allows_only_pre_fence_commits(string status, string operation)
    {
        var outcomes = new List<RaceOutcome>();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            var account = Account(db, caller);
            var login = await CreateLoginAsync(store);
            Func<Task> admission;
            DocumentSnapshot? sessionBefore = null;
            DocumentSnapshot? deviceBefore = null;
            if (operation == "authorize")
            {
                admission = () => AuthorizeAsync(store, caller, login);
            }
            else
            {
                var authorized = await AuthorizeAsync(store, caller, login);
                if (operation == "exchange")
                {
                    admission = () => ExchangeAsync(store, login, authorized.AuthorizationCode);
                }
                else
                {
                    Assert.Equal("refresh", operation);
                    var tokens = await ExchangeAsync(store, login, authorized.AuthorizationCode);
                    sessionBefore = await db.Collection("desktopSessions").Document(tokens.SessionId).GetSnapshotAsync();
                    deviceBefore = await account.Collection("devices").Document(tokens.DeviceId).GetSnapshotAsync();
                    admission = () => store.RefreshSessionAsync(tokens.RefreshToken,
                        TimeSpan.FromMinutes(10), TimeSpan.FromDays(1), CancellationToken.None);
                }
            }
            var attemptBefore = await Attempt(db, login).GetSnapshotAsync();
            var race = await RaceFenceAsync(account, status, admission);
            outcomes.Add(race.Outcome);

            await AssertFencedProfileAsync(account, status, race.Fence);
            var attemptAfter = await Attempt(db, login).GetSnapshotAsync();
            AssertCommittedNoLaterThanFence(attemptAfter, race.Fence);
            var sessions = await Sessions(db, caller).GetSnapshotAsync();
            var devices = await account.Collection("devices").GetSnapshotAsync();
            Assert.All(sessions.Documents, snapshot => AssertCommittedNoLaterThanFence(snapshot, race.Fence));
            Assert.All(devices.Documents, snapshot => AssertCommittedNoLaterThanFence(snapshot, race.Fence));
            var expectedSessions = operation == "refresh"
                || (operation == "exchange" && race.Outcome == RaceOutcome.Admitted) ? 1 : 0;
            Assert.Equal(expectedSessions, sessions.Count);
            Assert.Equal(expectedSessions, devices.Count);
            if (race.Outcome != RaceOutcome.Admitted)
            {
                await AssertUnchangedAsync(attemptBefore);
                if (sessionBefore is not null)
                    await AssertUnchangedAsync(sessionBefore);
                if (deviceBefore is not null)
                    await AssertUnchangedAsync(deviceBefore);
            }
            if (operation == "authorize")
                Assert.Equal(race.Outcome == RaceOutcome.Admitted ? "authorized" : "pending",
                    attemptAfter.GetValue<string>("status"));
            if (operation == "exchange")
                Assert.Equal(race.Outcome == RaceOutcome.Admitted ? "consumed" : "authorized",
                    attemptAfter.GetValue<string>("status"));
        }
        ReportOutcomes(operation + ":" + status, outcomes);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted", "credits")]
    [InlineData("deleted", "subscription")]
    [InlineData("deleting", "credits")]
    [InlineData("deleting", "subscription")]
    public async Task Fence_racing_checkout_preserves_only_pre_fence_purchase_evidence(string status, string kind)
    {
        var outcomes = new List<RaceOutcome>();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            var account = Account(db, caller);
            var suffix = Guid.NewGuid().ToString("N");
            var key = "local-race-checkout-" + suffix;
            var purchaseId = "local-race-purchase-" + suffix;
            var purchase = account.Collection("purchases").Document(purchaseId);
            var idempotency = db.Collection("idempotency")
                .Document(Crypto.Sha256($"checkout:{caller.UserId}:{key}"));
            var race = await RaceFenceAsync(account, status, async () =>
                Assert.True(await store.BeginPurchaseAsync(caller.UserId, purchaseId,
                    kind == "credits" ? Credits : Subscription, key, CancellationToken.None)));
            outcomes.Add(race.Outcome);

            await AssertFencedProfileAsync(account, status, race.Fence);
            var purchaseAfter = await purchase.GetSnapshotAsync();
            var idempotencyAfter = await idempotency.GetSnapshotAsync();
            Assert.Equal(race.Outcome == RaceOutcome.Admitted, purchaseAfter.Exists);
            Assert.Equal(race.Outcome == RaceOutcome.Admitted, idempotencyAfter.Exists);
            Assert.Equal(race.Outcome == RaceOutcome.Admitted ? 1 : 0,
                (await account.Collection("purchases").GetSnapshotAsync()).Count);
            if (race.Outcome == RaceOutcome.Admitted)
            {
                AssertCommittedNoLaterThanFence(purchaseAfter, race.Fence);
                AssertCommittedNoLaterThanFence(idempotencyAfter, race.Fence);
                // These are legitimate financial records, not operational
                // cleanup candidates. No refund/cancellation/deletion is implied.
                Assert.Equal("creating", purchaseAfter.GetValue<string>("status"));
            }
            var after = await account.GetSnapshotAsync();
            Assert.Equal(kind == "subscription" && race.Outcome == RaceOutcome.Admitted,
                after.ContainsField("subscriptionCheckoutPurchaseId"));
        }
        ReportOutcomes(kind + ":" + status, outcomes);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    [InlineData("deleting", false)]
    [InlineData("deleting", true)]
    public async Task Fence_racing_review_upsert_keeps_content_and_aggregate_commits_before_fence(
        string status, bool existingReview)
    {
        var outcomes = new List<RaceOutcome>();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var (db, _, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            var account = Account(db, caller);
            var reviews = Reviews(db);
            if (existingReview)
                await reviews.UpsertAsync(caller, 4, "Original synthetic race review.", CancellationToken.None);
            var reviewBefore = await Review(db, caller).GetSnapshotAsync();
            var stats = db.Collection("reviewMetadata").Document("public");
            var statsBefore = await stats.GetSnapshotAsync();
            var countBefore = statsBefore.Exists ? statsBefore.GetValue<long>("reviewCount") : 0;
            var ratingBefore = statsBefore.Exists ? statsBefore.GetValue<long>("totalRating") : 0;
            var race = await RaceFenceAsync(account, status, () => reviews.UpsertAsync(
                caller, 5, "Admitted synthetic race review.", CancellationToken.None));
            outcomes.Add(race.Outcome);

            await AssertFencedProfileAsync(account, status, race.Fence);
            var reviewAfter = await Review(db, caller).GetSnapshotAsync();
            var statsAfter = await stats.GetSnapshotAsync();
            Assert.Equal(existingReview || race.Outcome == RaceOutcome.Admitted, reviewAfter.Exists);
            if (race.Outcome == RaceOutcome.Admitted)
            {
                AssertCommittedNoLaterThanFence(reviewAfter, race.Fence);
                AssertCommittedNoLaterThanFence(statsAfter, race.Fence);
                Assert.Equal(countBefore + (existingReview ? 0 : 1), statsAfter.GetValue<long>("reviewCount"));
                Assert.Equal(ratingBefore + (existingReview ? 1 : 5), statsAfter.GetValue<long>("totalRating"));
                Assert.Equal("Admitted synthetic race review.", reviewAfter.GetValue<string>("text"));
            }
            else
            {
                await AssertUnchangedAsync(reviewBefore);
                await AssertUnchangedAsync(statsBefore);
            }
            // A valid pre-fence review may remain until the separate review
            // purge stage; this test proves admission fencing, not full erasure.
            if (reviewAfter.Exists)
                AssertCommittedNoLaterThanFence(reviewAfter, race.Fence);
        }
        ReportOutcomes("review:" + status + ":existing=" + existingReview, outcomes);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("authorize")]
    [InlineData("exchange")]
    [InlineData("refresh")]
    [InlineData("credits")]
    [InlineData("subscription")]
    [InlineData("review")]
    public async Task Missing_account_rejects_transactional_admission_without_creating_account_or_user_data(string operation)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var missing = caller with { UserId = "local-missing-" + Guid.NewGuid().ToString("N") };
        var accountBefore = await Account(db, caller).GetSnapshotAsync();
        var statsBefore = await db.Collection("reviewMetadata").Document("public").GetSnapshotAsync();
        DocumentSnapshot? attemptBefore = null;
        DocumentSnapshot? sessionBefore = null;
        DocumentSnapshot? deviceBefore = null;
        Func<Task> admission;
        if (operation is "authorize" or "exchange" or "refresh")
        {
            var login = await CreateLoginAsync(store);
            if (operation == "authorize")
            {
                admission = () => AuthorizeAsync(store, missing, login);
            }
            else
            {
                var authorized = await AuthorizeAsync(store, caller, login);
                if (operation == "exchange")
                {
                    await Attempt(db, login).UpdateAsync("userId", missing.UserId);
                    admission = () => ExchangeAsync(store, login, authorized.AuthorizationCode);
                }
                else
                {
                    var tokens = await ExchangeAsync(store, login, authorized.AuthorizationCode);
                    var session = db.Collection("desktopSessions").Document(tokens.SessionId);
                    await session.UpdateAsync("userId", missing.UserId);
                    sessionBefore = await session.GetSnapshotAsync();
                    deviceBefore = await Account(db, caller).Collection("devices").Document(tokens.DeviceId).GetSnapshotAsync();
                    admission = () => store.RefreshSessionAsync(tokens.RefreshToken,
                        TimeSpan.FromMinutes(10), TimeSpan.FromDays(1), CancellationToken.None);
                }
            }
            attemptBefore = await Attempt(db, login).GetSnapshotAsync();
        }
        else if (operation == "review")
        {
            admission = () => Reviews(db).UpsertAsync(missing, 5, "Rejected missing-account review.", CancellationToken.None);
        }
        else
        {
            Assert.Contains(operation, new[] { "credits", "subscription" });
            admission = () => store.BeginPurchaseAsync(missing.UserId, "local-missing-purchase",
                operation == "credits" ? Credits : Subscription, "local-missing-checkout", CancellationToken.None);
        }

        if (operation == "exchange")
        {
            // Preserve exchange's pre-existing missing-document contract;
            // nonactive existing accounts use the new sanitized guard instead.
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(admission);
            Assert.Equal("The account is unavailable.", exception.Message);
        }
        else
        {
            await AssertAdmissionRejectedAsync(admission);
        }

        await AssertUnchangedAsync(accountBefore);
        await AssertUnchangedAsync(statsBefore);
        if (attemptBefore is not null)
            await AssertUnchangedAsync(attemptBefore);
        if (sessionBefore is not null)
            await AssertUnchangedAsync(sessionBefore);
        if (deviceBefore is not null)
            await AssertUnchangedAsync(deviceBefore);
        Assert.False((await Account(db, missing).GetSnapshotAsync()).Exists);
        // Refresh deliberately rebinds one existing synthetic session to the
        // missing UID; all other variants must leave that UID with no sessions.
        Assert.Equal(operation == "refresh" ? 1 : 0, (await Sessions(db, missing).GetSnapshotAsync()).Count);
        Assert.Empty((await Account(db, missing).Collection("devices").GetSnapshotAsync()).Documents);
        Assert.Empty((await Account(db, missing).Collection("purchases").GetSnapshotAsync()).Documents);
        Assert.False((await Review(db, missing).GetSnapshotAsync()).Exists);
        Assert.False((await db.Collection("idempotency")
            .Document(Crypto.Sha256($"checkout:{missing.UserId}:local-missing-checkout")).GetSnapshotAsync()).Exists);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Missing_account_identity_sync_still_creates_a_new_active_account()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var newCaller = caller with { UserId = "local-new-identity-" + Guid.NewGuid().ToString("N") };
        Assert.False((await Account(db, newCaller).GetSnapshotAsync()).Exists);

        await store.EnsureAccountAsync(newCaller, CancellationToken.None);

        var created = await Account(db, newCaller).GetSnapshotAsync();
        Assert.Equal("active", created.GetValue<string>("status"));
        Assert.Equal(newCaller.Email, created.GetValue<string>("email"));
        Assert.Equal(newCaller.DisplayName, created.GetValue<string>("displayName"));
    }

    private static DocumentReference Account(FirestoreDb db, CallerIdentity caller) =>
        db.Collection("accounts").Document(caller.UserId);

    private static DocumentReference Review(FirestoreDb db, CallerIdentity caller) =>
        db.Collection("reviews").Document($"review_{Crypto.Sha256($"review:{caller.UserId}")[..32]}");

    private static Query Sessions(FirestoreDb db, CallerIdentity caller) =>
        db.Collection("desktopSessions").WhereEqualTo("userId", caller.UserId);

    private static DocumentReference Attempt(FirestoreDb db, LoginFixture login) =>
        db.Collection("desktopLoginAttempts").Document(login.Attempt.AttemptId);

    private static FirestoreReviewService Reviews(FirestoreDb db) => new(
        db, new SystemClock(), Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }));

    private static Task SetStatusAsync(DocumentReference account, string? status, bool storedNull) =>
        account.UpdateAsync("status", storedNull ? null! : status is null ? FieldValue.Delete : status);

    private static Task<WriteResult> FenceAsync(DocumentReference account, string status) =>
        account.UpdateAsync(new Dictionary<string, object>
        {
            ["status"] = status,
            ["email"] = "",
            ["displayName"] = "Deleted user",
        });

    private static async Task AssertAdmissionRejectedAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(action);
        Assert.Equal("The account is not active.", exception.Message);
    }

    private static async Task AssertUnchangedAsync(DocumentSnapshot before)
    {
        var after = await before.Reference.GetSnapshotAsync();
        Assert.Equal(before.Exists, after.Exists);
        if (before.Exists)
            Assert.Equal(before.UpdateTime, after.UpdateTime);
    }

    private static async Task<(WriteResult Fence, RaceOutcome Outcome)> RaceFenceAsync(
        DocumentReference account, string status, Func<Task> admission)
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WriteResult> CommitFenceAsync()
        {
            await start.Task;
            return await FenceAsync(account, status);
        }
        async Task<RaceOutcome> AttemptAdmissionAsync()
        {
            await start.Task;
            try
            {
                await admission();
                return RaceOutcome.Admitted;
            }
            catch (UnauthorizedAccessException exception) when (exception.Message == "The account is not active.")
            {
                return RaceOutcome.Rejected;
            }
            catch (Grpc.Core.RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.Aborted)
            {
                // The SDK exhausted its bounded transaction retries. This is
                // contention, not rejection or admission; no generic error catch.
                return RaceOutcome.ContentionAborted;
            }
        }
        var fenceTask = CommitFenceAsync();
        var admissionTask = AttemptAdmissionAsync();
        start.SetResult(true);
        // A fence failure is not a completed tombstone and must fail the test.
        await Task.WhenAll(fenceTask, admissionTask);
        return (await fenceTask, await admissionTask);
    }

    private static async Task AssertFencedProfileAsync(DocumentReference account, string status, WriteResult fence)
    {
        var after = await account.GetSnapshotAsync();
        Assert.Equal(status, after.GetValue<string>("status"));
        Assert.Equal("", after.GetValue<string>("email"));
        Assert.Equal("Deleted user", after.GetValue<string>("displayName"));
        Assert.Equal<Timestamp?>(fence.UpdateTime, after.UpdateTime);
    }

    private static void AssertCommittedNoLaterThanFence(DocumentSnapshot snapshot, WriteResult fence)
    {
        Assert.True(snapshot.Exists);
        Assert.True(snapshot.UpdateTime.HasValue);
        Assert.True(snapshot.UpdateTime.GetValueOrDefault().CompareTo(fence.UpdateTime) <= 0,
            "An admission document was committed after the account fence.");
    }

    private void ReportOutcomes(string variant, IReadOnlyCollection<RaceOutcome> outcomes)
    {
        Assert.Equal(5, outcomes.Count);
        _output.WriteLine("Race {0}: admitted={1}, rejected={2}, sdkAborted={3}", variant,
            outcomes.Count(value => value == RaceOutcome.Admitted),
            outcomes.Count(value => value == RaceOutcome.Rejected),
            outcomes.Count(value => value == RaceOutcome.ContentionAborted));
    }

    private static async Task AssertNoSessionsOrDevicesAsync(FirestoreDb db, CallerIdentity caller)
    {
        Assert.Empty((await Sessions(db, caller).GetSnapshotAsync()).Documents);
        Assert.Empty((await Account(db, caller).Collection("devices").GetSnapshotAsync()).Documents);
    }

    private static async Task<LoginFixture> CreateLoginAsync(FirestoreAccountStore store)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stateHash = Crypto.Sha256("local-state-" + suffix);
        var nonceHash = Crypto.Sha256("local-nonce-" + suffix);
        var verifier = Crypto.RandomToken(48);
        var attempt = await store.CreateLoginAttemptAsync(
            stateHash, nonceHash, Crypto.PkceChallenge(verifier),
            "http://127.0.0.1:55123/keyboard-wtf/callback", DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        return new LoginFixture(attempt, stateHash, nonceHash, verifier, "local-installation-" + suffix);
    }

    private static Task<(DesktopLoginAttempt Attempt, string AuthorizationCode)> AuthorizeAsync(
        FirestoreAccountStore store, CallerIdentity caller, LoginFixture login) =>
        store.AuthorizeLoginAttemptAsync(login.Attempt.AttemptId, login.StateHash, login.NonceHash,
            caller.UserId, CancellationToken.None);

    private static Task<DesktopSessionTokens> ExchangeAsync(
        FirestoreAccountStore store, LoginFixture login, string authorizationCode) =>
        store.ExchangeLoginAttemptAsync(login.Attempt.AttemptId, login.StateHash, login.NonceHash,
            Crypto.Sha256(authorizationCode), login.Verifier, "Synthetic local device", login.InstallationId,
            TimeSpan.FromMinutes(10), TimeSpan.FromDays(1), CancellationToken.None);

    private static async Task<DesktopSessionTokens> CreateSessionAsync(FirestoreAccountStore store, CallerIdentity caller)
    {
        var login = await CreateLoginAsync(store);
        var authorized = await AuthorizeAsync(store, caller, login);
        return await ExchangeAsync(store, login, authorized.AuthorizationCode);
    }

    private static Task<ReservationResult> ReserveAsync(FirestoreAccountStore store, CallerIdentity caller, string requestId) =>
        store.ReserveAsync(caller, requestId, "synthetic-model", "synthetic-pricing", 100_000, 500_000,
            100, 1, 1_000_000_000, CancellationToken.None);

    private sealed record LoginFixture(
        DesktopLoginAttempt Attempt, string StateHash, string NonceHash, string Verifier, string InstallationId);

    private enum RaceOutcome { Admitted, Rejected, ContentionAborted }
}
