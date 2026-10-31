using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class AccountDeletionReviewEmulatorTests
{
    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("active", false)]
    [InlineData("active", true)]
    [InlineData("deleting", false)]
    [InlineData("deleting", true)]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    public async Task Operational_deletion_removes_only_the_owners_review_and_adjusts_aggregate_once(
        string initialStatus, bool existingReview)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var (otherDb, _, other) = await AccountingEmulatorTests.CreateAccountAsync();
        Assert.Equal(db.ProjectId, otherDb.ProjectId);
        var reviews = new FirestoreReviewService(db, new SystemClock(), Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }));
        await reviews.UpsertAsync(other, 4, "Other synthetic review must remain.", CancellationToken.None);
        var otherBefore = await Review(db, other).GetSnapshotAsync();
        var baseline = await StatsAsync(db);
        if (existingReview)
            await reviews.UpsertAsync(caller, 5, "Synthetic owner content must be deleted.", CancellationToken.None);
        var account = db.Collection("accounts").Document(caller.UserId);
        var ledgerBefore = await account.Collection("ledger").GetSnapshotAsync();
        var purchase = account.Collection("purchases").Document("synthetic-retained-finance");
        await purchase.CreateAsync(new Dictionary<string, object> { ["status"] = "pending", ["amountMinor"] = 100 });
        var purchaseBefore = await purchase.GetSnapshotAsync();
        await account.UpdateAsync("status", initialStatus);

        for (var retry = 0; retry < 2; retry++)
            await store.DeleteAccountOperationalDataAsync(caller.UserId, CancellationToken.None);

        Assert.False((await Review(db, caller).GetSnapshotAsync()).Exists);
        var remaining = await Review(db, other).GetSnapshotAsync();
        Assert.Equal(otherBefore.UpdateTime, remaining.UpdateTime);
        Assert.Equal("Other synthetic review must remain.", remaining.GetValue<string>("text"));
        Assert.Equal(baseline, await StatsAsync(db));
        Assert.Equal(purchaseBefore.UpdateTime, (await purchase.GetSnapshotAsync()).UpdateTime);
        var ledgerAfter = await account.Collection("ledger").GetSnapshotAsync();
        Assert.Equal(ledgerBefore.Count, ledgerAfter.Count);
        foreach (var financialRecord in ledgerBefore.Documents)
            Assert.Equal(financialRecord.UpdateTime, (await financialRecord.Reference.GetSnapshotAsync()).UpdateTime);
        var deleted = await account.GetSnapshotAsync();
        // Operational cleanup alone is not identity deletion/final success.
        Assert.Equal(initialStatus == "deleted" ? "deleted" : "deleting", deleted.GetValue<string>("status"));
        Assert.True(deleted.ContainsField("operationalCleanupCompletedAt"));
        Assert.False(deleted.ContainsField("personalDataDeleted"));
        Assert.Equal("", deleted.GetValue<string>("email"));
        Assert.Equal("Deleted user", deleted.GetValue<string>("displayName"));
        var audits = await db.Collection("reviewAudit").WhereEqualTo("targetOwnerUserId", caller.UserId).GetSnapshotAsync();
        Assert.Equal(existingReview ? 1 : 0, audits.Count);
        Assert.All(audits.Documents, audit => Assert.False(audit.ContainsField("text")));
        // Exact synthetic other-owner fixture only; not a cleanup of real data.
        await reviews.DeleteOwnAsync(other.UserId, CancellationToken.None);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Missing_account_cleanup_removes_its_orphan_review_without_recreating_profile_or_erasing_finance()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var reviews = new FirestoreReviewService(db, new SystemClock(), Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }));
        var baseline = await StatsAsync(db);
        await reviews.UpsertAsync(caller, 3, "Synthetic orphan public review.", CancellationToken.None);
        var account = db.Collection("accounts").Document(caller.UserId);
        var ledger = await account.Collection("ledger").GetSnapshotAsync();
        await account.DeleteAsync();

        await store.DeleteAccountOperationalDataAsync(caller.UserId, CancellationToken.None);
        await store.DeleteAccountOperationalDataAsync(caller.UserId, CancellationToken.None);

        Assert.False((await account.GetSnapshotAsync()).Exists);
        Assert.False((await Review(db, caller).GetSnapshotAsync()).Exists);
        Assert.Equal(baseline, await StatsAsync(db));
        foreach (var record in ledger.Documents)
            Assert.Equal(record.UpdateTime, (await record.Reference.GetSnapshotAsync()).UpdateTime);
        Assert.Single((await db.Collection("reviewAudit").WhereEqualTo("targetOwnerUserId", caller.UserId).GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task An_active_request_that_blocks_deletion_cannot_remove_review_or_change_aggregate()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        var reviews = new FirestoreReviewService(db, new SystemClock(), Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }));
        await reviews.UpsertAsync(caller, 5, "Synthetic review while paid request is active.", CancellationToken.None);
        var review = await Review(db, caller).GetSnapshotAsync();
        var stats = await StatsAsync(db);
        await store.ReserveAsync(caller, "synthetic-review-preflight", "synthetic-model", "synthetic-pricing",
            100_000, 500_000, 100, 1, 1_000_000_000, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAccountOperationalDataAsync(caller.UserId, CancellationToken.None));

        Assert.Equal(review.UpdateTime, (await Review(db, caller).GetSnapshotAsync()).UpdateTime);
        Assert.Equal(stats, await StatsAsync(db));
        Assert.Equal("active", (await db.Collection("accounts").Document(caller.UserId).GetSnapshotAsync()).GetValue<string>("status"));
        Assert.Empty((await db.Collection("reviewAudit").WhereEqualTo("targetOwnerUserId", caller.UserId).GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Review_upsert_racing_deletion_cannot_leave_public_content_after_completed_cleanup()
    {
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            var reviews = new FirestoreReviewService(db, new SystemClock(), Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }));
            var baseline = await StatsAsync(db);
            await reviews.UpsertAsync(caller, 1, "Original synthetic race content.", CancellationToken.None);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task DeleteAsync()
            {
                await start.Task;
                await store.DeleteAccountOperationalDataAsync(caller.UserId, CancellationToken.None);
            }
            async Task UpsertAsync()
            {
                await start.Task;
                try { await reviews.UpsertAsync(caller, 5, "Racing synthetic review.", CancellationToken.None); }
                catch (UnauthorizedAccessException exception) when (exception.Message == "The account is not active.") { }
                catch (Grpc.Core.RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.Aborted) { }
            }
            var deletion = DeleteAsync();
            var upsert = UpsertAsync();
            start.SetResult(true);
            // A failed deletion is not completed cleanup and must fail this test.
            await Task.WhenAll(deletion, upsert);
            Assert.False((await Review(db, caller).GetSnapshotAsync()).Exists);
            Assert.Equal(baseline, await StatsAsync(db));
            Assert.Single((await db.Collection("reviewAudit").WhereEqualTo("targetOwnerUserId", caller.UserId).GetSnapshotAsync()).Documents);
        }
    }

    private static DocumentReference Review(FirestoreDb db, CallerIdentity caller) =>
        db.Collection("reviews").Document($"review_{Crypto.Sha256($"review:{caller.UserId}")[..32]}");

    private static async Task<(long Count, long Rating)> StatsAsync(FirestoreDb db)
    {
        var snapshot = await db.Collection("reviewMetadata").Document("public").GetSnapshotAsync();
        return snapshot.Exists ? (snapshot.GetValue<long>("reviewCount"), snapshot.GetValue<long>("totalRating")) : (0, 0);
    }
}
