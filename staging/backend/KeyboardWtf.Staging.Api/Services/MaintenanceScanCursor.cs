using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

internal sealed class MaintenanceScanCursor
{
    private readonly FirestoreDb _db;

    public MaintenanceScanCursor(FirestoreDb db) => _db = db;

    public Task<DocumentSnapshot?> LoadAsync(string job, CancellationToken cancellationToken) =>
        LoadAsync(job, null, cancellationToken);

    internal Task<DocumentSnapshot?> LoadOwnedAsync(string job, MaintenanceLeaseHandle lease, CancellationToken cancellationToken) =>
        LoadAsync(job, lease, cancellationToken);

    private async Task<DocumentSnapshot?> LoadAsync(
        string job,
        MaintenanceLeaseHandle? lease,
        CancellationToken cancellationToken)
    {
        var state = await State(job).GetSnapshotAsync(cancellationToken);
        if (!state.Exists)
            return null;
        var data = state.ToDictionary();
        var path = data.TryGetValue("lastDocumentPath", out var value)
            ? Convert.ToString(value)
            : null;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        // DocumentReference.Path is fully qualified, while Db.Document takes a
        // relative path. Accept only this database's prefix (including existing
        // saved cursors); never reinterpret another project's resource name.
        if (path.StartsWith("projects/", StringComparison.Ordinal))
        {
            var prefix = $"projects/{_db.ProjectId}/databases/{_db.DatabaseId}/documents/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("The maintenance cursor belongs to another database.");
            path = path[prefix.Length..];
        }
        var cursor = await _db.Document(path).GetSnapshotAsync(cancellationToken);
        if (cursor.Exists)
            return cursor;
        if (lease is null) await ResetAsync(job, cancellationToken);
        else await SaveOwnedAsync(job, null, true, DateTimeOffset.UtcNow, lease, cancellationToken);
        return null;
    }

    public Task SaveAsync(
        string job,
        DocumentSnapshot? lastDocument,
        bool completedScan,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        State(job).SetAsync(CursorState(lastDocument, completedScan, now), SetOptions.MergeAll, cancellationToken);

    internal async Task SaveOwnedAsync(string job, DocumentSnapshot? lastDocument, bool completedScan,
        DateTimeOffset now, MaintenanceLeaseHandle lease, CancellationToken cancellationToken)
    {
        if (!await lease.TryCheckpointAsync(transaction => transaction.Set(State(job),
            CursorState(lastDocument, completedScan, now), SetOptions.MergeAll), cancellationToken))
            throw new InvalidOperationException("The maintenance cursor lease is no longer owned.");
    }

    private static Dictionary<string, object> CursorState(DocumentSnapshot? lastDocument, bool completedScan, DateTimeOffset now) =>
        new()
        {
            ["lastDocumentPath"] = completedScan || lastDocument is null
                ? ""
                : lastDocument.Reference.Path,
            ["completedScan"] = completedScan,
            ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
        };

    private Task ResetAsync(string job, CancellationToken cancellationToken) =>
        State(job).SetAsync(new Dictionary<string, object>
        {
            ["lastDocumentPath"] = "",
            ["completedScan"] = true,
            ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow),
        }, SetOptions.MergeAll, cancellationToken);

    private DocumentReference State(string job) =>
        _db.Collection("maintenanceCursors").Document(Crypto.Sha256(job));
}
