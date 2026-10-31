using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Services;

namespace KeyboardWtf.Staging.Api.Connectors;

// Only bounded metadata is persisted. The OAuth vendor remains the credential
// owner; email/document contents, arguments and model context are never stored.
public sealed class FirestoreConnectorStore(FirestoreDb db) : IConnectorStore
{
    internal const int MaximumActiveAccounts = 50;
    private DocumentReference User(string uid) => db.Collection("accounts").Document(FirestoreAccountStore.SafeId(uid));
    private DocumentReference Ref(string uid, string collection, string id) =>
        User(uid).Collection(collection).Document(ConnectorIds.RequireId(id));
    private Query ActiveAccounts(string uid) => User(uid).Collection("connectorAccounts")
        .WhereNotEqualTo("state", "disconnected").Limit(MaximumActiveAccounts + 1);
    public async Task EnsureActiveAsync(string uid, CancellationToken ct) =>
        FirestoreAccountStore.RequireActiveAccount(await User(uid).GetSnapshotAsync(ct));

    public async Task<ConnectorClaim<ConnectorAttempt>> ClaimAttemptAsync(string uid, ConnectorAttempt candidate,
        DateTimeOffset now, CancellationToken ct) => await db.RunTransactionAsync(async transaction =>
    {
        FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
        var slot = User(uid).Collection("connectorSlots").Document(candidate.Family);
        var snapshot = await transaction.GetSnapshotAsync(slot, ct);
        if (snapshot.Exists && snapshot.TryGetValue<string>("attemptId", out var previousId))
        {
            var previous = await transaction.GetSnapshotAsync(Ref(uid, "connectorAttempts", previousId), ct);
            if (previous.Exists)
            {
                var attempt = Attempt(previous);
                if (attempt.RequestId == candidate.RequestId || (attempt.ExpiresAt > now
                    && attempt.State is "creating" or "connecting" or "outcome_unknown"))
                    return new ConnectorClaim<ConnectorAttempt>(attempt, false);
            }
        }
        var budgetRef = User(uid).Collection("connectorAdmission").Document("oauth");
        var budget = await transaction.GetSnapshotAsync(budgetRef, ct);
        var minute = now.ToUnixTimeSeconds() / 60;
        var used = budget.Exists && budget.GetValue<long>("minute") == minute ? budget.GetValue<long>("used") : 0;
        if (used >= 12) throw new ConnectorProviderException("rate_limited");
        transaction.Create(Ref(uid, "connectorAttempts", candidate.Id), AttemptData(candidate));
        transaction.Set(slot, new { attemptId = candidate.Id, expiresAt = Ts(candidate.ExpiresAt.AddHours(1)) });
        transaction.Set(budgetRef, new { minute, used = used + 1, deleteAfter = Ts(now.AddMinutes(3)) });
        return new ConnectorClaim<ConnectorAttempt>(candidate, true);
    }, cancellationToken: ct);

    public async Task<ConnectorAttempt> GetAttemptAsync(string uid, string id, CancellationToken ct)
    {
        await EnsureActiveAsync(uid, ct);
        var value = await Ref(uid, "connectorAttempts", id).GetSnapshotAsync(ct);
        return value.Exists ? Attempt(value) : throw new KeyNotFoundException("This connection attempt was not found.");
    }
    public async Task SaveAttemptAsync(string uid, ConnectorAttempt value, CancellationToken ct)
    {
        // A pre-deletion provider create may acknowledge after the account fence.
        // Record that exact existing claim for deletion cleanup, but never create
        // a new attempt or account through this acknowledgement path.
        await db.RunTransactionAsync(async transaction =>
        {
            var reference = Ref(uid, "connectorAttempts", value.Id);
            var previous = await transaction.GetSnapshotAsync(reference, ct);
            if (!previous.Exists) throw new KeyNotFoundException("Connection attempt has expired.");
            var owned = Attempt(previous);
            if (owned.StateHash != value.StateHash || owned.Family != value.Family
                || (owned.ProviderId is not null && owned.ProviderId != value.ProviderId))
                throw new UnauthorizedAccessException("Connection ownership could not be confirmed.");
            if (owned.State == "connected") return 0;
            if (owned.State is "expired" or "cancelled") value = value with { State = owned.State, RedirectUrl = null };
            transaction.Set(reference, AttemptData(value));
            return 0;
        }, cancellationToken: ct);
    }
    public async Task<bool> CompleteAttemptAsync(string uid, ConnectorAttempt attempt, StoredConnectorAccount account,
        DateTimeOffset now, CancellationToken ct) => await db.RunTransactionAsync(async transaction =>
    {
        FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
        var attemptRef = Ref(uid, "connectorAttempts", attempt.Id);
        var current = await transaction.GetSnapshotAsync(attemptRef, ct);
        var accountRef = Ref(uid, "connectorAccounts", account.Id);
        var existing = await transaction.GetSnapshotAsync(accountRef, ct);
        if (!current.Exists) return false;
        var owned = Attempt(current);
        if (owned.StateHash != attempt.StateHash || owned.Family != account.Family || owned.ProviderId != account.ProviderId)
            throw new UnauthorizedAccessException("Connection ownership could not be confirmed.");
        if (owned.ExpiresAt <= now || owned.State is "expired" or "cancelled" or "failed"
            || existing.Exists && Account(existing).State is "disconnecting" or "disconnected") return false;
        if (owned.State == "connected") return owned.AccountId == account.Id;
        if (!existing.Exists)
        {
            var active = await transaction.GetSnapshotAsync(ActiveAccounts(uid), ct);
            if (active.Count >= MaximumActiveAccounts)
                throw new InvalidOperationException("Up to 50 connected service accounts are supported. Disconnect an account before adding another.");
        }
        if (existing.Exists) account = account with { IsDefault = Account(existing).IsDefault };
        transaction.Set(accountRef, AccountData(account));
        transaction.Set(attemptRef, AttemptData(attempt with { State = "connected", AccountId = account.Id,
            RedirectUrl = null, CleanupRequired = false }));
        return true;
    }, cancellationToken: ct);
    public async Task<IReadOnlyList<ConnectorAttempt>> AttemptsAsync(string uid, CancellationToken ct)
    {
        var values = await User(uid).Collection("connectorAttempts").GetSnapshotAsync(ct);
        return values.Documents.Select(Attempt).ToArray();
    }
    public async Task<IReadOnlyList<StoredConnectorAccount>> AccountsAsync(string uid, CancellationToken ct)
    {
        var values = await ActiveAccounts(uid).GetSnapshotAsync(ct);
        if (values.Count > MaximumActiveAccounts) throw new InvalidOperationException("Account management requires support.");
        return values.Documents.Select(Account).ToArray();
    }
    public async Task SaveAccountAsync(string uid, StoredConnectorAccount value, CancellationToken ct)
    {
        await db.RunTransactionAsync(async transaction =>
        {
            FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
            var reference = Ref(uid, "connectorAccounts", value.Id);
            var previous = await transaction.GetSnapshotAsync(reference, ct);
            // Status I/O can finish after a tombstone was cleaned. Only atomic
            // OAuth adoption creates accounts; a refresh must never recreate one.
            if (!previous.Exists) throw new KeyNotFoundException("That connected account is no longer present.");
            if (previous.Exists && Account(previous).State is "disconnecting" or "disconnected") return 0;
            if (previous.Exists && value.State != "disconnecting") value = value with { IsDefault = Account(previous).IsDefault };
            transaction.Set(reference, AccountData(value));
            return 0;
        }, cancellationToken: ct);
    }
    public async Task RemoveAccountAsync(string uid, string id, CancellationToken ct)
    {
        await db.RunTransactionAsync(async transaction =>
        {
            FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
            // Short tombstone prevents an in-flight status refresh or callback
            // from recreating credentials that the user just disconnected.
            transaction.Update(Ref(uid, "connectorAccounts", id), new Dictionary<string, object>
            {
                ["state"] = "disconnected", ["isDefault"] = false,
                ["deleteAfter"] = Ts(DateTimeOffset.UtcNow.AddDays(7))
            });
            return 0;
        }, cancellationToken: ct);
    }
    public async Task SetDefaultAsync(string uid, string id, CancellationToken ct)
    {
        ConnectorIds.RequireId(id);
        await db.RunTransactionAsync(async transaction =>
        {
            FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
            var snapshots = await transaction.GetSnapshotAsync(ActiveAccounts(uid), ct);
            var values = snapshots.Documents.Select(Account).ToArray();
            if (values.Length > MaximumActiveAccounts) throw new InvalidOperationException("Account management requires support.");
            var target = values.SingleOrDefault(value => value.Id == id && value.State == "connected")
                ?? throw new KeyNotFoundException("The connected account was not found.");
            foreach (var value in values.Where(value => value.Family == target.Family))
                transaction.Update(Ref(uid, "connectorAccounts", value.Id), "isDefault", value.Id == id);
            return 0;
        }, cancellationToken: ct);
    }
    public Task CreateTaskAsync(string uid, StoredConnectorTask task, CancellationToken ct) =>
        ActiveWrite(uid, Ref(uid, "connectorTasks", task.Id), TaskData(task), ct);
    private static Dictionary<string, object> TaskData(StoredConnectorTask task) => new()
        {
            ["connectorId"] = task.ConnectorId, ["accountId"] = task.AccountId ?? "",
            ["state"] = task.State, ["allowsWrites"] = task.AllowsWrites,
            ["logicalLiveSessionId"] = ConnectorTelemetry.LogicalLiveId(task.LogicalLiveSessionId) ?? "",
            ["authSessionHash"] = ConnectorTelemetry.HashValue(task.AuthSessionHash) ?? "",
            ["expiresAt"] = Ts(task.ExpiresAt), ["deleteAfter"] = Ts(task.ExpiresAt.AddDays(1))
        };
    public async Task<bool> RebindTaskAsync(string uid, string previousId, StoredConnectorTask replacement,
        IReadOnlyList<string> writeCapabilities, DateTimeOffset now, CancellationToken ct) => await db.RunTransactionAsync(async transaction =>
    {
        FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
        var previousRef = Ref(uid, "connectorTasks", previousId);
        var previous = await transaction.GetSnapshotAsync(previousRef, ct);
        var account = await transaction.GetSnapshotAsync(Ref(uid, "connectorAccounts", replacement.AccountId!), ct);
        var operations = await transaction.GetSnapshotAsync(User(uid).Collection("connectorOperations").WhereEqualTo("taskId", previousId).Limit(31), ct);
        if (!previous.Exists || !account.Exists || Account(account).State != "connected") return false;
        var original = TaskRecord(previous);
        if (original.State is not ("ready" or "account_required") || original.ExpiresAt <= now
            || operations.Documents.Any(value => writeCapabilities.Contains(Operation(value).CapabilityId))) return false;
        if (replacement.ConnectorId != original.ConnectorId || replacement.AllowsWrites != original.AllowsWrites
            || replacement.ExpiresAt != original.ExpiresAt || replacement.Id == previousId)
            throw new UnauthorizedAccessException("Account correction cannot widen task authority.");
        replacement = replacement with { LogicalLiveSessionId = original.LogicalLiveSessionId,
            AuthSessionHash = original.AuthSessionHash };
        transaction.Update(previousRef, "state", "cancelled");
        transaction.Create(Ref(uid, "connectorTasks", replacement.Id), TaskData(replacement));
        return true;
    }, cancellationToken: ct);
    public async Task<StoredConnectorTask> GetTaskAsync(string uid, string id, CancellationToken ct)
    {
        await EnsureActiveAsync(uid, ct);
        var value = await Ref(uid, "connectorTasks", id).GetSnapshotAsync(ct);
        return value.Exists ? TaskRecord(value) : throw new KeyNotFoundException("That connector task was not found.");
    }
    public async Task CancelTaskAsync(string uid, string id, CancellationToken ct)
        => await EndTaskAsync(uid, id, "cancelled", ct);
    public async Task FinishTaskAsync(string uid, string id, CancellationToken ct)
        => await EndTaskAsync(uid, id, "completed", ct);
    public async Task EndTaskAsync(string uid, string id, string state, CancellationToken ct)
    {
        if (state is not ("completed" or "failed" or "cancelled")) throw new ArgumentException("Invalid terminal task state.");
        await db.RunTransactionAsync(async transaction =>
        {
            FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
            var target = Ref(uid, "connectorTasks", id);
            var value = await transaction.GetSnapshotAsync(target, ct);
            if (!value.Exists) throw new KeyNotFoundException("That connector task was not found.");
            if (TaskRecord(value).State is "ready" or "account_required") transaction.Update(target, "state", state);
            return 0;
        }, cancellationToken: ct);
    }
    public async Task<ConnectorClaim<StoredConnectorOperation>> ClaimOperationAsync(string uid,
        StoredConnectorOperation candidate, DateTimeOffset now, CancellationToken ct) =>
        await db.RunTransactionAsync(async transaction =>
        {
            FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
            var task = await transaction.GetSnapshotAsync(Ref(uid, "connectorTasks", candidate.TaskId), ct);
            var account = await transaction.GetSnapshotAsync(Ref(uid, "connectorAccounts", candidate.AccountId), ct);
            var operationRef = Ref(uid, "connectorOperations", candidate.Id);
            var previous = await transaction.GetSnapshotAsync(operationRef, ct);
            var operations = await transaction.GetSnapshotAsync(User(uid).Collection("connectorOperations")
                .WhereEqualTo("taskId", candidate.TaskId).Limit(31), ct);
            if (!task.Exists || TaskRecord(task).State != "ready" || TaskRecord(task).ExpiresAt <= now
                || !account.Exists || Account(account).State != "connected"
                || TaskRecord(task).AccountId != candidate.AccountId)
                throw new InvalidOperationException("This task or account is no longer active.");
            if (previous.Exists) return new ConnectorClaim<StoredConnectorOperation>(Operation(previous), false);
            var origin = TaskRecord(task);
            candidate = candidate with { LogicalLiveSessionId = origin.LogicalLiveSessionId, AuthSessionHash = origin.AuthSessionHash };
            if (operations.Count >= 30) throw new InvalidOperationException("This connector task has reached its action limit. Start a new request.");
            // Per-UID admission is shared across Cloud Run instances. These are
            // nonfinancial abuse/parallelism guards, not credits or AI limits.
            var budgetRef = User(uid).Collection("connectorAdmission").Document("execute");
            var budget = await transaction.GetSnapshotAsync(budgetRef, ct);
            var minute = now.ToUnixTimeSeconds() / 60;
            var used = budget.Exists && budget.GetValue<long>("minute") == minute ? budget.GetValue<long>("used") : 0;
            var leases = budget.Exists && budget.TryGetValue<Dictionary<string, Timestamp>>("leases", out var previousLeases)
                ? previousLeases.Where(pair => pair.Value.ToDateTime() > now.UtcDateTime).ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, Timestamp>();
            if (used >= 60 || leases.Count >= 2) throw new ConnectorProviderException("rate_limited");
            leases[candidate.Id] = Ts(now.AddSeconds(90));
            transaction.Create(operationRef, OperationData(candidate));
            transaction.Set(budgetRef, new { minute, used = used + 1, leases, deleteAfter = Ts(now.AddMinutes(3)) });
            return new ConnectorClaim<StoredConnectorOperation>(candidate, true);
        }, cancellationToken: ct);
    public async Task SaveOperationAsync(string uid, StoredConnectorOperation value, CancellationToken ct)
    {
        // A dispatched operation must be recorded even when account deletion has
        // fenced new work. This only updates an existing owned claim; it creates
        // no new operation and cannot resurrect its task/account.
        var target = Ref(uid, "connectorOperations", value.Id);
        await db.RunTransactionAsync(async transaction =>
        {
            var previous = await transaction.GetSnapshotAsync(target, ct);
            if (!previous.Exists) return 0;
            var existing = Operation(previous);
            if (existing.TaskId != value.TaskId || existing.Digest != value.Digest)
                throw new UnauthorizedAccessException("Operation ownership could not be confirmed.");
            if (existing.State == "completed") return 0;
            value = value with { LogicalLiveSessionId = existing.LogicalLiveSessionId, AuthSessionHash = existing.AuthSessionHash };
            var budgetRef = User(uid).Collection("connectorAdmission").Document("execute");
            var budget = await transaction.GetSnapshotAsync(budgetRef, ct);
            transaction.Set(target, OperationData(value));
            if (budget.Exists && budget.TryGetValue<Dictionary<string, Timestamp>>("leases", out var leases) && leases.Remove(value.Id))
                transaction.Update(budgetRef, "leases", leases);
            return 0;
        }, cancellationToken: ct);
    }
    public async Task PurgeAsync(string uid, CancellationToken ct)
    {
        foreach (var name in new[] { "connectorAttempts", "connectorSlots", "connectorAccounts", "connectorTasks", "connectorOperations", "connectorAdmission" })
        {
            while (true)
            {
                var page = await User(uid).Collection(name).Limit(200).GetSnapshotAsync(ct);
                if (page.Count == 0) break;
                var batch = db.StartBatch();
                foreach (var value in page.Documents) batch.Delete(value.Reference);
                await batch.CommitAsync(ct);
            }
        }
    }
    public async Task<IReadOnlyList<StoredConnectorOperation>> OperationsAsync(string uid, string taskId, CancellationToken ct)
    {
        await EnsureActiveAsync(uid, ct);
        ConnectorIds.RequireId(taskId);
        var records = await User(uid).Collection("connectorOperations").WhereEqualTo("taskId", taskId).Limit(30).GetSnapshotAsync(ct);
        return records.Documents.Select(Operation).ToArray();
    }
    private async Task ActiveWrite(string uid, DocumentReference reference, object data, CancellationToken ct)
    {
        await db.RunTransactionAsync(async transaction =>
        {
            FirestoreAccountStore.RequireActiveAccount(await transaction.GetSnapshotAsync(User(uid), ct));
            transaction.Set(reference, data);
            return 0;
        }, cancellationToken: ct);
    }
    private static Dictionary<string, object> AttemptData(ConnectorAttempt value) => new()
    {
        ["family"] = value.Family, ["stateHash"] = value.StateHash, ["requestId"] = value.RequestId,
        ["state"] = value.State, ["expiresAt"] = Ts(value.ExpiresAt),
        // Never TTL-delete the only reference to an unclaimed OAuth credential.
        // Provider cleanup/atomic account adoption must finish first.
        ["deleteAfter"] = value.CleanupRequired ? null! : Ts(value.ExpiresAt.AddDays(1)),
        ["cleanupRequired"] = value.CleanupRequired, ["providerId"] = value.ProviderId ?? "",
        ["redirectUrl"] = value.RedirectUrl ?? "", ["accountId"] = value.AccountId ?? ""
    };
    private static Dictionary<string, object> AccountData(StoredConnectorAccount value)
    {
        var data = new Dictionary<string, object>
        {
            ["family"] = value.Family, ["providerId"] = value.ProviderId, ["label"] = value.Label,
            ["state"] = value.State, ["isDefault"] = value.State == "disconnected" ? false : value.IsDefault,
            ["verifiedAt"] = value.VerifiedAt is { } time ? Ts(time) : (object)""
        };
        // A verified provider 404 is the same cleaned-credential tombstone as
        // explicit disconnect. Keep the race fence, then let metadata expire.
        if (value.State == "disconnected") data["deleteAfter"] = Ts(DateTimeOffset.UtcNow.AddDays(7));
        return data;
    }
    private static Dictionary<string, object> OperationData(StoredConnectorOperation value) => new()
    {
        ["taskId"] = value.TaskId, ["accountId"] = value.AccountId, ["capabilityId"] = value.CapabilityId,
        ["digest"] = value.Digest, ["state"] = value.State, ["resourceId"] = value.ResourceId ?? "",
        ["logicalLiveSessionId"] = ConnectorTelemetry.LogicalLiveId(value.LogicalLiveSessionId) ?? "",
        ["authSessionHash"] = ConnectorTelemetry.HashValue(value.AuthSessionHash) ?? "",
        ["providerActionRequestAttempts"] = Math.Max(0, value.ProviderRequestCount),
        ["actionElapsedMs"] = Math.Max(0, value.ElapsedMilliseconds),
        ["errorCode"] = ConnectorTelemetry.Error(value.ErrorCode) ?? "",
        ["deleteAfter"] = Ts(value.ExpiresAt)
    };
    private static ConnectorAttempt Attempt(DocumentSnapshot doc) => new(doc.Id, Text(doc, "family"), Text(doc, "stateHash"),
        Text(doc, "requestId"), Text(doc, "state"), Date(doc, "expiresAt")!.Value,
        NullableText(doc, "providerId"), NullableText(doc, "redirectUrl"), NullableText(doc, "accountId"),
        !doc.TryGetValue<bool>("cleanupRequired", out var required) || required);
    private static StoredConnectorAccount Account(DocumentSnapshot doc) => new(doc.Id, Text(doc, "family"), Text(doc, "providerId"),
        Text(doc, "label"), Text(doc, "state"), doc.GetValue<bool>("isDefault"), Date(doc, "verifiedAt"));
    private static StoredConnectorTask TaskRecord(DocumentSnapshot doc) => new(doc.Id, Text(doc, "connectorId"),
        NullableText(doc, "accountId"), Text(doc, "state"), doc.GetValue<bool>("allowsWrites"), Date(doc, "expiresAt")!.Value)
    {
        LogicalLiveSessionId = ConnectorTelemetry.LogicalLiveId(NullableText(doc, "logicalLiveSessionId")),
        AuthSessionHash = ConnectorTelemetry.HashValue(NullableText(doc, "authSessionHash"))
    };
    private static StoredConnectorOperation Operation(DocumentSnapshot doc) => new(doc.Id, Text(doc, "taskId"), Text(doc, "accountId"),
        Text(doc, "capabilityId"), Text(doc, "digest"), Text(doc, "state"), NullableText(doc, "resourceId"), Date(doc, "deleteAfter")!.Value)
    {
        LogicalLiveSessionId = ConnectorTelemetry.LogicalLiveId(NullableText(doc, "logicalLiveSessionId")),
        AuthSessionHash = ConnectorTelemetry.HashValue(NullableText(doc, "authSessionHash")),
        ProviderRequestCount = doc.TryGetValue<long>("providerActionRequestAttempts", out var count) ? (int)Math.Clamp(count, 0, int.MaxValue) : 0,
        ElapsedMilliseconds = doc.TryGetValue<long>("actionElapsedMs", out var elapsed) ? Math.Max(0, elapsed) : 0,
        ErrorCode = ConnectorTelemetry.Error(NullableText(doc, "errorCode"))
    };
    private static string Text(DocumentSnapshot doc, string field) => doc.TryGetValue<string>(field, out var value) ? value : "";
    private static string? NullableText(DocumentSnapshot doc, string field) => Text(doc, field) is { Length: > 0 } value ? value : null;
    private static DateTimeOffset? Date(DocumentSnapshot doc, string field) => doc.TryGetValue<object>(field, out var value)
        && value is Timestamp time ? new DateTimeOffset(time.ToDateTime()) : null;
    private static Timestamp Ts(DateTimeOffset time) => Timestamp.FromDateTime(time.UtcDateTime);
}
