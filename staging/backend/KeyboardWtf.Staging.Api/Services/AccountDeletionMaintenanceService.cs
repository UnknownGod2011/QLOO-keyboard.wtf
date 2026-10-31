using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

// Resumes ONLY previously verified, confirmed deletion intent. No HTTP route,
// new identity, speculative mandate discovery or bypass of cancellation proof.
public sealed class AccountDeletionMaintenanceService
{
    private const string Job = "account-deletion-completion";
    private readonly FirestoreDb _db;
    private readonly AccountDeletionService _deletion;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;
    private readonly FirestoreMaintenanceLease _leases;
    private readonly MaintenanceScanCursor _cursor;
    private readonly ILogger<AccountDeletionMaintenanceService> _logger;

    public AccountDeletionMaintenanceService(FirestoreDb db, AccountDeletionService deletion,
        IClock clock, IOptions<PaidSystemOptions> options, ILogger<AccountDeletionMaintenanceService> logger)
    {
        _db = db; _deletion = deletion; _clock = clock; _options = options.Value;
        _leases = new(db, clock); _cursor = new(db); _logger = logger;
    }

    public async Task<ReconciliationRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;
        var runId = "deletionrec_" + Crypto.RandomToken(12);
        var duration = TimeSpan.FromSeconds(_options.ReconciliationLeaseSeconds);
        await using var lease = await _leases.TryAcquireAsync(Job, duration, cancellationToken);
        if (lease is null) return new(Job, runId, 0, 0, 0, 0, false, startedAt, _clock.UtcNow);
        var examined = 0; var repaired = 0; var pending = 0;
        var query = _db.Collection("accounts").WhereEqualTo("status", "deleting").OrderBy(FieldPath.DocumentId);
        var cursor = await _cursor.LoadOwnedAsync(Job, lease, cancellationToken);
        var completedScan = false;
        while (examined < _options.ReconciliationMaxRecordsPerRun)
        {
            if (!await lease.RenewAsync(duration, cancellationToken))
                throw new InvalidOperationException("Account deletion lost its maintenance lease.");
            var count = Math.Min(_options.ReconciliationBatchSize, _options.ReconciliationMaxRecordsPerRun - examined);
            var page = await (cursor is null ? query : query.StartAfter(cursor)).Limit(count).GetSnapshotAsync(cancellationToken);
            if (page.Count == 0) { completedScan = true; break; }
            foreach (var account in page.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await lease.RenewAsync(duration, cancellationToken))
                    throw new InvalidOperationException("Account deletion lost its maintenance lease.");
                examined++;
                try { await _deletion.ResumePendingDeletionAsync(account.Id, cancellationToken); repaired++; }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    pending++;
                    _logger.LogWarning("Account deletion remains pending account={AccountHash} category={Category}",
                        Crypto.Sha256(account.Id)[..12], error.GetType().Name);
                }
                cursor = account;
                await _cursor.SaveOwnedAsync(Job, cursor, false, _clock.UtcNow, lease, cancellationToken);
            }
            if (page.Count < count) { completedScan = true; break; }
        }
        await _cursor.SaveOwnedAsync(Job, cursor, completedScan, _clock.UtcNow, lease, cancellationToken);
        var result = new ReconciliationRunResult(Job, runId, examined, repaired, pending, 0, true, startedAt, _clock.UtcNow);
        await _db.Collection("maintenanceRuns").Document(runId).CreateAsync(new Dictionary<string, object>
        {
            ["job"] = Job, ["examined"] = examined, ["repaired"] = repaired,
            ["quarantined"] = pending, ["unchanged"] = 0,
            ["startedAt"] = Timestamp.FromDateTime(startedAt.UtcDateTime),
            ["completedAt"] = Timestamp.FromDateTime(result.CompletedAt.UtcDateTime),
            ["status"] = pending > 0 ? "attention_required" : "completed",
            ["scanCompleted"] = completedScan,
        }, cancellationToken);
        _logger.LogInformation("Account deletion maintenance completed run={RunId} examined={Examined} completed={Completed} pending={Pending}",
            runId, examined, repaired, pending);
        return result;
    }
}
