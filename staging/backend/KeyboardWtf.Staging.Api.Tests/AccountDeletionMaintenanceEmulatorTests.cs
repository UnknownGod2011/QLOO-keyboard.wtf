using System.Reflection;
using EmulatorDetection = Google.Api.Gax.EmulatorDetection;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class AccountDeletionMaintenanceEmulatorTests
{
    private const string Job = "account-deletion-completion";

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Bounded_scan_reports_lease_truthfully_and_resumes_cursor_without_skipping_pending_intent()
    {
        var f = Fixture.Create();
        var invalid = await f.AddAsync("a-unconfirmed", confirmed: false);
        var second = await f.AddAsync("b-confirmed");
        var third = await f.AddAsync("c-confirmed");
        var first = await f.Maintenance.RunAsync(default);
        Assert.True(first.LeaseAcquired); // Acquired lease is not the same as completed scan.
        Assert.Equal(1, first.Examined);
        Assert.Equal(1, first.Quarantined);
        Assert.Equal(0, first.Repaired);
        Assert.Empty(f.Identity.Deleted);
        Assert.Equal(invalid.Path, (await f.Cursor.GetSnapshotAsync()).GetValue<string>("lastDocumentPath"));
        Assert.False((await f.Cursor.GetSnapshotAsync()).GetValue<bool>("completedScan"));

        var next = await f.Maintenance.RunAsync(default);
        Assert.True(next.LeaseAcquired);
        Assert.Equal(1, next.Repaired);
        Assert.Equal("deleted", (await second.GetSnapshotAsync()).GetValue<string>("status"));
        Assert.Equal("deleting", (await third.GetSnapshotAsync()).GetValue<string>("status"));
        await f.Maintenance.RunAsync(default);
        Assert.Equal("deleted", (await third.GetSnapshotAsync()).GetValue<string>("status"));
        var exhausted = await f.Maintenance.RunAsync(default);
        Assert.True(exhausted.LeaseAcquired);
        Assert.Equal(0, exhausted.Examined);
        Assert.True((await f.Cursor.GetSnapshotAsync()).GetValue<bool>("completedScan"));
        Assert.Equal("", (await f.Cursor.GetSnapshotAsync()).GetValue<string>("lastDocumentPath"));
        Assert.Equal("deleting", (await invalid.GetSnapshotAsync()).GetValue<string>("status"));
        Assert.Equal(new[] { second.Id, third.Id }, f.Identity.Deleted);
        var revisit = await f.Maintenance.RunAsync(default);
        Assert.Equal(1, revisit.Quarantined); // Unknown consent is never silently deleted.
        Assert.Equal(2, f.Identity.Deleted.Count);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Held_lease_defers_without_creating_completion_or_mutating_account()
    {
        var f = Fixture.Create();
        var account = await f.AddAsync("confirmed");
        var before = await account.GetSnapshotAsync();
        await using (var lease = await new FirestoreMaintenanceLease(f.Db, new SystemClock())
            .TryAcquireAsync(Job, TimeSpan.FromMinutes(5), default))
        {
            Assert.NotNull(lease);
            var result = await f.Maintenance.RunAsync(default);
            Assert.False(result.LeaseAcquired);
            Assert.Equal(0, result.Examined);
            Assert.Empty(f.Identity.Deleted);
            Assert.Equal(before.UpdateTime, (await account.GetSnapshotAsync()).UpdateTime);
        }
        Assert.Equal(1, (await f.Maintenance.RunAsync(default)).Repaired);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Cancellation_keeps_pending_intent_and_releases_lease_for_next_run()
    {
        var f = Fixture.Create();
        var account = await f.AddAsync("confirmed");
        using var cancellation = new CancellationTokenSource();
        f.Identity.BeforeDelete = () => { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Maintenance.RunAsync(cancellation.Token));
        var pending = await account.GetSnapshotAsync();
        Assert.Equal("deleting", pending.GetValue<string>("status"));
        Assert.False(pending.ContainsField("personalDataDeleted"));
        Assert.Empty(f.Identity.Deleted);
        Assert.Equal("idle", (await f.Db.Collection("maintenanceLeases").Document(Job).GetSnapshotAsync()).GetValue<string>("status"));
        f.Identity.BeforeDelete = () => Task.CompletedTask;
        Assert.Equal(1, (await f.Maintenance.RunAsync(default)).Repaired);
        Assert.Single(f.Identity.Deleted);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Pending_run_receipt_has_only_operational_counts_not_identity_or_error_content()
    {
        var f = Fixture.Create();
        var account = await f.AddAsync("confirmed");
        f.Identity.BeforeDelete = () => Task.FromException(new IOException("synthetic private error content"));
        var result = await f.Maintenance.RunAsync(default);
        var receipt = await f.Db.Collection("maintenanceRuns").Document(result.RunId).GetSnapshotAsync();
        Assert.True(receipt.Exists);
        Assert.Equal("attention_required", receipt.GetValue<string>("status"));
        Assert.Equal(1L, receipt.GetValue<long>("quarantined"));
        Assert.Equal(0L, receipt.GetValue<long>("repaired"));
        Assert.DoesNotContain(account.Id, string.Join(" ", receipt.ToDictionary().Values));
        Assert.DoesNotContain("private error", string.Join(" ", receipt.ToDictionary().Values));
        Assert.Equal("deleting", (await account.GetSnapshotAsync()).GetValue<string>("status"));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_lease_during_identity_work_cannot_overwrite_successor_cursor(bool replaced)
    {
        var f = Fixture.Create();
        var account = await f.AddAsync("confirmed");
        f.Identity.BeforeDelete = async () =>
        {
            var change = new Dictionary<string, object>
            {
                ["expiresAt"] = Timestamp.FromDateTime(replaced ? DateTime.UtcNow.AddMinutes(5) : DateTime.UtcNow.AddMinutes(-1)),
            };
            if (replaced) change["owner"] = "synthetic-successor";
            await f.Db.Collection("maintenanceLeases").Document(Job).UpdateAsync(change);
            await f.Cursor.SetAsync(new Dictionary<string, object>
            {
                ["lastDocumentPath"] = "accounts/synthetic-successor-cursor", ["completedScan"] = false,
            });
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Maintenance.RunAsync(default));
        Assert.Equal("accounts/synthetic-successor-cursor", (await f.Cursor.GetSnapshotAsync()).GetValue<string>("lastDocumentPath"));
        Assert.Equal("deleted", (await account.GetSnapshotAsync()).GetValue<string>("status"));
        if (replaced) Assert.Equal("synthetic-successor", (await f.Db.Collection("maintenanceLeases").Document(Job).GetSnapshotAsync()).GetValue<string>("owner"));
        Assert.Empty((await f.Db.Collection("maintenanceRuns").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_lease_blocks_both_empty_scan_reset_and_per_record_checkpoint(bool completedScan)
    {
        var f = Fixture.Create();
        var account = await f.AddAsync("confirmed");
        await using var lease = await new FirestoreMaintenanceLease(f.Db, new SystemClock()).TryAcquireAsync(Job, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(lease);
        await f.Db.Collection("maintenanceLeases").Document(Job).UpdateAsync("expiresAt", Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)));
        var snapshot = await account.GetSnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MaintenanceScanCursor(f.Db).SaveOwnedAsync(Job,
            completedScan ? null : snapshot, completedScan, DateTimeOffset.UtcNow, lease, default));
        Assert.False((await f.Cursor.GetSnapshotAsync()).Exists);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cursor_round_trip_accepts_own_full_or_relative_document_path(bool fullPath)
    {
        var f = Fixture.Create();
        var account = await f.AddAsync("confirmed");
        await f.Cursor.SetAsync(new Dictionary<string, object> { ["lastDocumentPath"] = fullPath ? account.Path : "accounts/confirmed" });
        var loaded = await new MaintenanceScanCursor(f.Db).LoadAsync(Job, default);
        Assert.Equal(account.Path, loaded!.Reference.Path);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Cursor_cannot_resolve_another_project_or_database()
    {
        var f = Fixture.Create();
        await f.Cursor.SetAsync(new Dictionary<string, object> { ["lastDocumentPath"] = "projects/another-project/databases/(default)/documents/accounts/confirmed" });
        var before = await f.Cursor.GetSnapshotAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new MaintenanceScanCursor(f.Db).LoadAsync(Job, default));
        Assert.Equal(before.UpdateTime, (await f.Cursor.GetSnapshotAsync()).UpdateTime);
    }

    private sealed class Fixture
    {
        internal FirestoreDb Db { get; }
        internal FirestoreAccountStore Store { get; }
        internal Identity Identity { get; } = new();
        internal AccountDeletionMaintenanceService Maintenance { get; }
        internal DocumentReference Cursor => Db.Collection("maintenanceCursors").Document(Crypto.Sha256(Job));

        private Fixture(FirestoreDb db)
        {
            Db = db;
            var options = Options.Create(new PaidSystemOptions
            {
                ProjectId = db.ProjectId, GlobalUsageShardCount = 1,
                ReconciliationBatchSize = 1, ReconciliationMaxRecordsPerRun = 1,
            });
            Store = new(db, new SystemClock(), options);
            var service = new AccountDeletionService(Store, Identity, new SystemClock(),
                DispatchProxy.Create<IPaymentProvider, NoPayments>());
            Maintenance = new(db, service, new SystemClock(), options, NullLogger<AccountDeletionMaintenanceService>.Instance);
        }

        internal static Fixture Create()
        {
            // A new isolated in-memory emulator namespace, never ADC or a cloud project.
            var host = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
            Assert.False(string.IsNullOrWhiteSpace(host));
            var endpoint = new Uri("http://" + host);
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.Empty(endpoint.UserInfo);
            Assert.Equal("/", endpoint.AbsolutePath);
            return new(new FirestoreDbBuilder
            {
                ProjectId = "kwt-deletion-local-" + Guid.NewGuid().ToString("N"),
                EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build());
        }

        internal async Task<DocumentReference> AddAsync(string id, bool confirmed = true)
        {
            var caller = new CallerIdentity(id, id + "@example.invalid", "Synthetic", "firebase", AuthenticatedAt: DateTimeOffset.UtcNow);
            await Store.EnsureAccountAsync(caller, default);
            if (confirmed) await Store.BeginConfirmedAccountDeletionAsync(caller, default);
            else await Store.BeginAccountDeletionAsync(id, default);
            return Db.Collection("accounts").Document(id);
        }
    }

    private sealed class Identity : IIdentityService
    {
        internal List<string> Deleted { get; } = new();
        internal Func<Task> BeforeDelete { get; set; } = () => Task.CompletedTask;
        public async Task DeleteFirebaseUserAsync(string id, CancellationToken token)
        { token.ThrowIfCancellationRequested(); await BeforeDelete(); token.ThrowIfCancellationRequested(); Deleted.Add(id); }
        public Task<CallerIdentity?> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken token) => throw new NotSupportedException();
        public Task<CallerIdentity> VerifyFirebaseAsync(string token, CancellationToken cancellation) => throw new NotSupportedException();
    }

    public class NoPayments : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => throw new InvalidOperationException("No remote mandates in local fixture.");
    }
}
