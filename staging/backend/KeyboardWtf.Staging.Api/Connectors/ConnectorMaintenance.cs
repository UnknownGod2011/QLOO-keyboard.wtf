using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;

namespace KeyboardWtf.Staging.Api.Connectors;

// Operational cleanup only, not a user automation/scheduler or another state
// owner. Reuses the existing job lease/cursor, validated connection owner and
// provider disconnect operation. No mail/doc content or credentials persisted.
public sealed class ConnectorMaintenance(FirestoreDb db, IServiceScopeFactory scopes, IClock clock,
    ILogger<ConnectorMaintenance> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        const string job = "connector-oauth-cleanup";
        var leases = new FirestoreMaintenanceLease(db, clock);
        var cursors = new MaintenanceScanCursor(db);
        var duration = TimeSpan.FromMinutes(10);
        await using var lease = await leases.TryAcquireAsync(job, duration, ct);
        if (lease is null) return;
        using var scope = scopes.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<ConnectorConnectionService>();
        if (!connections.Enabled) return;
        // Stable document ordering makes the existing cursor valid even after
        // this record is marked cleaned. The active 10-minute flows are skipped.
        var query = db.CollectionGroup("connectorAttempts").WhereEqualTo("cleanupRequired", true).OrderBy(FieldPath.DocumentId);
        var cursor = await cursors.LoadOwnedAsync(job, lease, ct);
        var examined = 0; var cleaned = 0; var pending = 0; var providerWork = 0; var completedScan = false;
        while (examined < 100 && providerWork < 20)
        {
            if (!await lease.RenewAsync(duration, ct)) throw new InvalidOperationException("Connector cleanup lost its lease.");
            var page = await (cursor is null ? query : query.StartAfter(cursor)).Limit(Math.Min(5, 100 - examined)).GetSnapshotAsync(ct);
            if (page.Count == 0) { completedScan = true; break; }
            foreach (var attempt in page.Documents)
            {
                ct.ThrowIfCancellationRequested(); examined++;
                var account = attempt.Reference.Parent.Parent;
                if (account?.Parent.Id != "accounts") throw new InvalidOperationException("Unexpected connector cleanup owner.");
                if (attempt.GetValue<Timestamp>("expiresAt").ToDateTime() <= clock.UtcNow.UtcDateTime)
                {
                    providerWork++;
                    try { await connections.CleanupExpiredAttemptAsync(account.Id, attempt.Id, ct); cleaned++; }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        pending++;
                        logger.LogWarning("Connector OAuth cleanup pending: attempt={Attempt} errorType={ErrorType}", attempt.Id, error.GetType().Name);
                    }
                }
                cursor = attempt;
                await cursors.SaveOwnedAsync(job, cursor, false, clock.UtcNow, lease, ct);
                if (providerWork >= 20) break;
            }
            if (page.Count < 5) { completedScan = true; break; }
        }
        await cursors.SaveOwnedAsync(job, cursor, completedScan, clock.UtcNow, lease, ct);
        logger.LogInformation("Connector OAuth cleanup examined={Examined} cleaned={Cleaned} pending={Pending}", examined, cleaned, pending);
        if (pending > 0) throw new InvalidOperationException("Some connector OAuth cleanup remains pending; no success was assumed.");
    }
}
