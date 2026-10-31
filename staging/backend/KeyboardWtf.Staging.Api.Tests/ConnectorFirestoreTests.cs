using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class ConnectorFirestoreTests
{
    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Concurrent_connection_clicks_have_one_durable_environment_user_family_claim()
    {
        var (db, store, uid) = await Fixture();
        var now = DateTimeOffset.UtcNow;
        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.ClaimAttemptAsync(uid,
            Attempt(now), now, default)));
        Assert.Single(claims, value => value.Created);
        Assert.Single(claims.Select(value => value.Item.Id).Distinct());
        Assert.Single((await db.Collection("accounts").Document(uid).Collection("connectorAttempts").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Claimed_write_is_not_claimed_twice_and_cancelled_task_cannot_dispatch()
    {
        var (db, store, uid) = await Fixture();
        var account = Account(); await SeedAccountAsync(db, uid, account);
        var task = TaskRecord(account.Id); await store.CreateTaskAsync(uid, task, default);
        var op = Operation(task, account);
        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.ClaimOperationAsync(uid, op, DateTimeOffset.UtcNow, default)));
        Assert.Single(claims, value => value.Created);
        await store.SaveOperationAsync(uid, op with { State = "completed", ResourceId = "safe-resource" }, default);
        await store.SaveOperationAsync(uid, op with { State = "outcome_unknown" }, default);
        var duplicate = await store.ClaimOperationAsync(uid, op, DateTimeOffset.UtcNow, default);
        Assert.Equal("completed", duplicate.Item.State);
        await store.CancelTaskAsync(uid, task.Id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimOperationAsync(uid,
            op with { Id = ConnectorIds.New() }, DateTimeOffset.UtcNow, default));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Disconnect_tombstone_wins_over_late_status_refresh_and_OAuth_callback()
    {
        var (db, store, uid) = await Fixture();
        var account = Account(); await SeedAccountAsync(db, uid, account);
        await store.SaveAccountAsync(uid, account with { State = "disconnecting" }, default);
        await store.SaveAccountAsync(uid, account, default);
        Assert.Equal("disconnecting", Assert.Single(await store.AccountsAsync(uid, default)).State);
        await store.RemoveAccountAsync(uid, account.Id, default);
        await store.SaveAccountAsync(uid, account, default);
        Assert.Empty(await store.AccountsAsync(uid, default));
        var doc = await db.Collection("accounts").Document(uid).Collection("connectorAccounts").Document(account.Id).GetSnapshotAsync();
        Assert.Equal("disconnected", doc.GetValue<string>("state"));
        Assert.True(doc.ContainsField("deleteAfter"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Deletion_fence_denies_new_work_but_records_existing_create_ack_for_cleanup()
    {
        var (db, store, uid) = await Fixture();
        var now = DateTimeOffset.UtcNow; var attempt = Attempt(now);
        await store.ClaimAttemptAsync(uid, attempt, now, default);
        await db.Collection("accounts").Document(uid).UpdateAsync("status", "deleting");
        await store.SaveAttemptAsync(uid, attempt with { ProviderId = "ca_bound", State = "connecting" }, default);
        Assert.Equal("ca_bound", Assert.Single(await store.AttemptsAsync(uid, default)).ProviderId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ClaimAttemptAsync(uid, Attempt(now), now, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAccountAsync(uid, Account(), default));
        await store.PurgeAsync(uid, default);
        Assert.Empty(await store.AttemptsAsync(uid, default));
        Assert.Empty(await store.AccountsAsync(uid, default));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Explicit_default_is_single_per_family_and_never_cross_user()
    {
        var (db, store, uid) = await Fixture();
        var a = Account(); var b = Account();
        await SeedAccountAsync(db, uid, a); await SeedAccountAsync(db, uid, b);
        await store.SetDefaultAsync(uid, a.Id, default); await store.SetDefaultAsync(uid, b.Id, default);
        Assert.Equal(b.Id, Assert.Single(await store.AccountsAsync(uid, default), value => value.IsDefault).Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetAttemptAsync(uid, ConnectorIds.New(), default));
        var (_, otherStore, otherUid) = await Fixture();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => otherStore.SetDefaultAsync(otherUid, a.Id, default));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Cleaned_tombstones_do_not_exhaust_active_account_management_or_defaults()
    {
        var (db, store, uid) = await Fixture();
        foreach (var _ in Enumerable.Range(0, 60)) await SeedAccountAsync(db, uid, Account() with { State = "disconnected" });
        var active = Account(); await SeedAccountAsync(db, uid, active);
        await store.SetDefaultAsync(uid, active.Id, default);
        Assert.Equal(active.Id, Assert.Single(await store.AccountsAsync(uid, default)).Id);
        Assert.True(Assert.Single(await store.AccountsAsync(uid, default)).IsDefault);
        Assert.Equal(61, (await db.Collection("accounts").Document(uid).Collection("connectorAccounts").GetSnapshotAsync()).Count);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Concurrent_OAuth_adoption_cannot_exceed_fifty_active_accounts_and_keeps_cleanup_evidence()
    {
        var (db, store, uid) = await Fixture(); var now = DateTimeOffset.UtcNow;
        foreach (var _ in Enumerable.Range(0, FirestoreConnectorStore.MaximumActiveAccounts - 1))
            await SeedAccountAsync(db, uid, Account());
        var a = Attempt(now) with { ProviderId = "ca_new_google", State = "connecting" };
        var b = Attempt(now) with { Family = "github", ProviderId = "ca_new_github", State = "connecting" };
        await store.ClaimAttemptAsync(uid, a, now, default); await store.ClaimAttemptAsync(uid, b, now, default);
        async Task<bool> Adopt(ConnectorAttempt attempt)
        {
            try { return await store.CompleteAttemptAsync(uid, attempt,
                Account() with { Family = attempt.Family, ProviderId = attempt.ProviderId! }, now, default); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Up to 50", StringComparison.Ordinal)) { return false; }
        }
        var adopted = await Task.WhenAll(Adopt(a), Adopt(b));
        Assert.Single(adopted, value => value);
        Assert.Equal(FirestoreConnectorStore.MaximumActiveAccounts, (await store.AccountsAsync(uid, default)).Count);
        var pending = Assert.Single(await store.AttemptsAsync(uid, default), value => value.State != "connected");
        Assert.True(pending.CleanupRequired); Assert.NotNull(pending.ProviderId);
        Assert.Null((await db.Collection("accounts").Document(uid).Collection("connectorAttempts").Document(pending.Id).GetSnapshotAsync())
            .GetValue<object>("deleteAfter"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Provider_absence_gets_a_cleanup_TTL_and_a_late_refresh_cannot_recreate_a_removed_account()
    {
        var (db, store, uid) = await Fixture(); var account = Account() with { IsDefault = true };
        await SeedAccountAsync(db, uid, account);
        await store.SaveAccountAsync(uid, account with { State = "disconnected" }, default);
        var reference = db.Collection("accounts").Document(uid).Collection("connectorAccounts").Document(account.Id);
        var tombstone = await reference.GetSnapshotAsync();
        Assert.IsType<Timestamp>(tombstone.GetValue<object>("deleteAfter")); Assert.False(tombstone.GetValue<bool>("isDefault"));
        Assert.Empty(await store.AccountsAsync(uid, default));
        await reference.DeleteAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SaveAccountAsync(uid, account, default));
        Assert.False((await reference.GetSnapshotAsync()).Exists);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Journals_have_TTL_metadata_but_no_content_tokens_or_action_arguments()
    {
        var (db, store, uid) = await Fixture();
        var a = Account(); await SeedAccountAsync(db, uid, a);
        var task = TaskRecord(a.Id); await store.CreateTaskAsync(uid, task, default);
        var op = Operation(task, a); await store.ClaimOperationAsync(uid, op, DateTimeOffset.UtcNow, default);
        var doc = await db.Collection("accounts").Document(uid).Collection("connectorOperations").Document(op.Id).GetSnapshotAsync();
        Assert.Equal(new[] { "accountId", "actionElapsedMs", "authSessionHash", "capabilityId", "deleteAfter", "digest", "errorCode",
            "logicalLiveSessionId", "providerActionRequestAttempts", "resourceId", "state", "taskId" }, doc.ToDictionary().Keys.Order().ToArray());
        Assert.True(doc.GetValue<Timestamp>("deleteAfter").ToDateTime() > DateTime.UtcNow);
        Assert.DoesNotContain("text", doc.ToDictionary().Keys); Assert.DoesNotContain("accessToken", doc.ToDictionary().Keys);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Correlation_roundtrips_under_existing_TTL_and_cannot_be_relabelled_by_rebind_claim_or_settlement()
    {
        var (db, store, uid) = await Fixture(); var now = DateTimeOffset.UtcNow;
        var a = Account(); var b = Account(); await SeedAccountAsync(db, uid, a); await SeedAccountAsync(db, uid, b);
        var origin = TaskRecord(a.Id) with { LogicalLiveSessionId = ConnectorIds.New(), AuthSessionHash = ConnectorIds.Hash("auth-session") };
        await store.CreateTaskAsync(uid, origin, default);
        // Match production ChangeAccountAsync: derive replacements from the
        // persisted authority, not a higher-precision pre-Firestore timestamp.
        origin = await store.GetTaskAsync(uid, origin.Id, default);
        var replacement = origin with { Id = ConnectorIds.New(), AccountId = b.Id,
            LogicalLiveSessionId = ConnectorIds.New(), AuthSessionHash = ConnectorIds.Hash("unrelated-auth-session") };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RebindTaskAsync(uid, origin.Id,
            replacement with { ExpiresAt = origin.ExpiresAt.AddSeconds(1) }, ["mail_send"], now, default));
        Assert.True(await store.RebindTaskAsync(uid, origin.Id, replacement, ["mail_send"], now, default));
        var rebound = await store.GetTaskAsync(uid, replacement.Id, default);
        Assert.Equal(origin.LogicalLiveSessionId, rebound.LogicalLiveSessionId); Assert.Equal(origin.AuthSessionHash, rebound.AuthSessionHash);
        var candidate = Operation(rebound, b) with { LogicalLiveSessionId = ConnectorIds.New(), AuthSessionHash = ConnectorIds.Hash("wrong") };
        var claim = await store.ClaimOperationAsync(uid, candidate, now, default);
        Assert.Equal(origin.LogicalLiveSessionId, claim.Item.LogicalLiveSessionId); Assert.Equal(origin.AuthSessionHash, claim.Item.AuthSessionHash);
        await store.SaveOperationAsync(uid, candidate with { State = "completed", ProviderRequestCount = 2,
            ElapsedMilliseconds = 123, ErrorCode = "DO_NOT_LEAK_UNREVIEWED_ERROR" }, default);
        var settled = Assert.Single(await store.OperationsAsync(uid, rebound.Id, default));
        Assert.Equal(origin.LogicalLiveSessionId, settled.LogicalLiveSessionId); Assert.Equal(origin.AuthSessionHash, settled.AuthSessionHash);
        Assert.Equal(2, settled.ProviderRequestCount); Assert.Equal(123, settled.ElapsedMilliseconds); Assert.Equal("unavailable", settled.ErrorCode);
        var taskDoc = await db.Collection("accounts").Document(uid).Collection("connectorTasks").Document(rebound.Id).GetSnapshotAsync();
        var opDoc = await db.Collection("accounts").Document(uid).Collection("connectorOperations").Document(settled.Id).GetSnapshotAsync();
        Assert.Equal(Timestamp.FromDateTime(origin.ExpiresAt.AddDays(1).UtcDateTime), taskDoc.GetValue<Timestamp>("deleteAfter"));
        var expectedOperationExpiry = candidate.ExpiresAt.UtcDateTime.Ticks;
        Assert.Equal(expectedOperationExpiry - expectedOperationExpiry % 10,
            opDoc.GetValue<Timestamp>("deleteAfter").ToDateTime().Ticks);
        Assert.DoesNotContain("DO_NOT_LEAK", System.Text.Json.JsonSerializer.Serialize(opDoc.ToDictionary()));
        // Old metadata-only documents remain readable without fabricating IDs.
        await opDoc.Reference.UpdateAsync(new Dictionary<string, object> { ["logicalLiveSessionId"] = FieldValue.Delete,
            ["authSessionHash"] = FieldValue.Delete, ["providerActionRequestAttempts"] = FieldValue.Delete,
            ["actionElapsedMs"] = FieldValue.Delete, ["errorCode"] = FieldValue.Delete });
        var legacy = Assert.Single(await store.OperationsAsync(uid, rebound.Id, default));
        Assert.Null(legacy.LogicalLiveSessionId); Assert.Null(legacy.AuthSessionHash); Assert.Equal(0, legacy.ProviderRequestCount);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Pending_OAuth_has_no_TTL_until_atomic_adoption_or_cleanup_and_terminal_state_wins()
    {
        var (db, store, uid) = await Fixture(); var now = DateTimeOffset.UtcNow;
        var attempt = Attempt(now); await store.ClaimAttemptAsync(uid, attempt, now, default);
        attempt = attempt with { State = "connecting", ProviderId = "ca_bound" };
        await store.SaveAttemptAsync(uid, attempt, default);
        var reference = db.Collection("accounts").Document(uid).Collection("connectorAttempts").Document(attempt.Id);
        Assert.Null((await reference.GetSnapshotAsync()).GetValue<object>("deleteAfter"));
        var account = Account() with { ProviderId = attempt.ProviderId! };
        Assert.True(await store.CompleteAttemptAsync(uid, attempt, account, now, default));
        var adopted = await reference.GetSnapshotAsync();
        Assert.False(adopted.GetValue<bool>("cleanupRequired")); Assert.IsType<Timestamp>(adopted.GetValue<object>("deleteAfter"));
        var other = Attempt(now) with { Family = "github", ProviderId = "ca_other" };
        await store.ClaimAttemptAsync(uid, other, now, default);
        await store.SaveAttemptAsync(uid, other with { State = "cancelled" }, default);
        await store.SaveAttemptAsync(uid, other with { State = "connecting" }, default);
        Assert.Equal("cancelled", (await store.GetAttemptAsync(uid, other.Id, default)).State);
        Assert.False(await store.CompleteAttemptAsync(uid, other, account with { Id = ConnectorIds.New(), Family = "github", ProviderId = "ca_other" }, now, default));
        await store.SaveAttemptAsync(uid, other with { State = "cancelled", CleanupRequired = false }, default);
        var cleaned = await db.Collection("accounts").Document(uid).Collection("connectorAttempts").Document(other.Id).GetSnapshotAsync();
        Assert.IsType<Timestamp>(cleaned.GetValue<object>("deleteAfter"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Account_correction_is_atomic_and_does_not_widen_the_original_task()
    {
        var (db, store, uid) = await Fixture(); var now = DateTimeOffset.UtcNow;
        var a = Account(); var b = Account();
        await SeedAccountAsync(db, uid, a); await SeedAccountAsync(db, uid, b);
        var original = TaskRecord(a.Id) with { AllowsWrites = false };
        await store.CreateTaskAsync(uid, original, default);
        // Use the persisted authority, just as ChangeAccountAsync does. Cloud
        // Firestore timestamps have microsecond rather than .NET tick precision.
        original = await store.GetTaskAsync(uid, original.Id, default);
        var replacement = original with { Id = ConnectorIds.New(), AccountId = b.Id };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RebindTaskAsync(uid, original.Id,
            replacement with { AllowsWrites = true }, ["mail_send"], now, default));
        var decisions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.RebindTaskAsync(uid, original.Id,
            replacement with { Id = ConnectorIds.New() }, ["mail_send"], now, default)));
        Assert.Single(decisions, value => value);
        Assert.Equal("cancelled", (await store.GetTaskAsync(uid, original.Id, default)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimOperationAsync(uid,
            Operation(original, a), now, default));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Write_claim_cancel_or_expiry_prevents_account_rebinding_even_if_result_is_unknown()
    {
        var (db, store, uid) = await Fixture(); var now = DateTimeOffset.UtcNow;
        var a = Account(); var b = Account();
        await SeedAccountAsync(db, uid, a); await SeedAccountAsync(db, uid, b);
        var original = TaskRecord(a.Id); await store.CreateTaskAsync(uid, original, default);
        var op = Operation(original, a); await store.ClaimOperationAsync(uid, op, now, default);
        await store.SaveOperationAsync(uid, op with { State = "outcome_unknown" }, default);
        Assert.False(await store.RebindTaskAsync(uid, original.Id, original with { Id = ConnectorIds.New(), AccountId = b.Id }, ["mail_send"], now, default));
        var expired = TaskRecord(a.Id) with { ExpiresAt = now.AddSeconds(-1) }; await store.CreateTaskAsync(uid, expired, default);
        Assert.False(await store.RebindTaskAsync(uid, expired.Id, expired with { Id = ConnectorIds.New(), AccountId = b.Id }, ["mail_send"], now, default));
        await store.CancelTaskAsync(uid, original.Id, default);
        Assert.False(await store.RebindTaskAsync(uid, original.Id, original with { Id = ConnectorIds.New(), AccountId = b.Id }, ["mail_send"], now, default));
    }

    private static async Task<(FirestoreDb Db, FirestoreConnectorStore Store, string Uid)> Fixture()
    {
        // Existing helper explicitly requires a loopback emulator; never cloud.
        var (db, _, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        return (db, new FirestoreConnectorStore(db), caller.UserId);
    }
    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Admission_is_UID_scoped_nonfinancial_and_releases_only_after_actual_settlement()
    {
        var (db, store, uid) = await Fixture();
        var a = Account(); await SeedAccountAsync(db, uid, a);
        var task = TaskRecord(a.Id); await store.CreateTaskAsync(uid, task, default);
        var first = Operation(task, a); var second = Operation(task, a); var third = Operation(task, a);
        await store.ClaimOperationAsync(uid, first, DateTimeOffset.UtcNow, default);
        await store.ClaimOperationAsync(uid, second, DateTimeOffset.UtcNow, default);
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => store.ClaimOperationAsync(uid, third, DateTimeOffset.UtcNow, default));
        Assert.Equal("rate_limited", error.Code);
        await store.SaveOperationAsync(uid, first with { State = "completed" }, default);
        Assert.True((await store.ClaimOperationAsync(uid, third, DateTimeOffset.UtcNow, default)).Created);
        var admission = await db.Collection("accounts").Document(uid).Collection("connectorAdmission").Document("execute").GetSnapshotAsync();
        Assert.True(admission.ContainsField("deleteAfter"));
        Assert.DoesNotContain("credits", admission.ToDictionary().Keys);
    }
    private static ConnectorAttempt Attempt(DateTimeOffset now) => new(ConnectorIds.New(), "google", ConnectorIds.Hash(ConnectorIds.New()), ConnectorIds.New(), "creating", now.AddMinutes(10));
    // Trusted fixture setup only. Production account creation is exclusively the
    // ownership-validated, bounded atomic OAuth-adoption transaction.
    private static Task<WriteResult> SeedAccountAsync(FirestoreDb db, string uid, StoredConnectorAccount account) =>
        db.Collection("accounts").Document(uid).Collection("connectorAccounts").Document(account.Id).SetAsync(new Dictionary<string, object>
        {
            ["family"] = account.Family, ["providerId"] = account.ProviderId, ["label"] = account.Label,
            ["state"] = account.State, ["isDefault"] = account.IsDefault,
            ["verifiedAt"] = Timestamp.FromDateTime(account.VerifiedAt!.Value.UtcDateTime)
        });
    private static StoredConnectorAccount Account() => new(ConnectorIds.New(), "google", "ca_test", "Synthetic test", "connected", false, DateTimeOffset.UtcNow);
    private static StoredConnectorTask TaskRecord(string account) => new(ConnectorIds.New(), "gmail", account, "ready", true, DateTimeOffset.UtcNow.AddMinutes(10));
    private static StoredConnectorOperation Operation(StoredConnectorTask task, StoredConnectorAccount account) => new(
        ConnectorIds.New(), task.Id, account.Id, "mail_send", ConnectorIds.Hash("synthetic-args"), "executing", null, DateTimeOffset.UtcNow.AddDays(1));
}
