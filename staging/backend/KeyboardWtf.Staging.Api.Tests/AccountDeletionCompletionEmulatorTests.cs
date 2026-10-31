using System.Reflection;
using System.Runtime.ExceptionServices;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Loopback-only GUID accounts; the identity/payment doubles never contact a
// provider. Final identity success is deliberately held or its ACK is lost.
[Collection("Local accounting emulator")]
public sealed class AccountDeletionCompletionEmulatorTests
{
    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Cleanup_and_identity_must_finish_before_final_deleted_flags_and_audit()
    {
        var f = await Fixture.CreateAsync();
        await f.Account.Collection("devices").Document("inert-device").SetAsync(new Dictionary<string, object> { ["name"] = "synthetic" });
        var entered = Signal();
        var release = Signal();
        f.Identity.Delete = async () => { entered.TrySetResult(true); await release.Task; };
        var deletion = f.DeleteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pending = await f.Account.GetSnapshotAsync();
            Assert.Equal("deleting", pending.GetValue<string>("status"));
            Assert.False(pending.ContainsField("personalDataDeleted"));
            Assert.False(pending.ContainsField("deletedAt"));
            Assert.True(pending.ContainsField("operationalCleanupCompletedAt"));
            Assert.False((await f.Account.Collection("audit").Document("account_deleted").GetSnapshotAsync()).Exists);
            Assert.Empty((await f.Account.Collection("devices").GetSnapshotAsync()).Documents);
        }
        finally { release.TrySetResult(true); await deletion.WaitAsync(TimeSpan.FromSeconds(10)); }
        var completed = await f.Account.GetSnapshotAsync();
        Assert.Equal("deleted", completed.GetValue<string>("status"));
        Assert.True(completed.GetValue<bool>("personalDataDeleted"));
        Assert.True(completed.ContainsField("deletedAt"));
        Assert.Equal(1, f.Identity.Calls);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("lost-ack")]
    public async Task Identity_failure_or_lost_ack_keeps_durable_consent_and_maintenance_can_finish(string failure)
    {
        var f = await Fixture.CreateAsync();
        var before = (await f.Account.GetSnapshotAsync()).GetValue<long>("availableCreditMicros");
        f.Identity.Delete = () => failure == "cancel"
            ? Task.FromException(new OperationCanceledException("Synthetic identity cancellation"))
            : Task.FromException(new IOException("Synthetic identity failure/ack loss"));
        await Assert.ThrowsAnyAsync<Exception>(f.DeleteAsync);
        var pending = await f.Account.GetSnapshotAsync();
        Assert.Equal("deleting", pending.GetValue<string>("status"));
        Assert.False(pending.ContainsField("personalDataDeleted"));
        Assert.False(pending.ContainsField("deletedAt"));
        Assert.Equal(1L, pending.GetValue<long>("deletionIntentVersion"));
        Assert.Equal(f.Caller.UserId, pending.GetValue<string>("deletionRequestedBy"));
        f.Identity.Delete = () => Task.CompletedTask; // Exact-UID absence/success in provider adapter.
        await Resume(f.Service, f.Caller.UserId);
        var completed = await f.Account.GetSnapshotAsync();
        Assert.Equal("deleted", completed.GetValue<string>("status"));
        Assert.Equal(before, completed.GetValue<long>("availableCreditMicros"));
        var audit = await f.Account.Collection("audit").Document("account_deleted").GetSnapshotAsync();
        await Resume(f.Service, f.Caller.UserId); // Already finalized: no second identity mutation.
        Assert.Equal(2, f.Identity.Calls);
        Assert.Equal(audit.UpdateTime, (await audit.Reference.GetSnapshotAsync()).UpdateTime);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("active")]
    [InlineData("unconfirmed")]
    [InlineData("wrong-owner")]
    [InlineData("stale-auth")]
    [InlineData("unknown-version")]
    public async Task Internal_resume_cannot_invent_or_relax_original_verified_deletion_consent(string invalid)
    {
        var f = await Fixture.CreateAsync();
        if (invalid != "active")
        {
            var now = Timestamp.GetCurrentTimestamp();
            await f.Account.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = "deleting", ["deletionRequestedAt"] = now,
                ["deletionConfirmedAt"] = now,
                ["deletionAuthenticatedAt"] = invalid == "stale-auth" ? Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1)) : now,
                ["deletionRequestedBy"] = invalid == "wrong-owner" ? "another-uid" : f.Caller.UserId,
                ["deletionIntentVersion"] = invalid == "unknown-version" ? 2L : 1L,
            });
            if (invalid == "unconfirmed") await f.Account.UpdateAsync("deletionConfirmedAt", FieldValue.Delete);
        }
        var before = await f.Account.GetSnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resume(f.Service, f.Caller.UserId));
        Assert.Equal(0, f.Identity.Calls);
        Assert.Equal(before.UpdateTime, (await f.Account.GetSnapshotAsync()).UpdateTime);
    }

    private sealed record Fixture(FirestoreDb Db, FirestoreAccountStore Store, CallerIdentity Caller,
        Identity Identity, AccountDeletionService Service)
    {
        internal DocumentReference Account => Db.Collection("accounts").Document(Caller.UserId);
        internal static async Task<Fixture> CreateAsync()
        {
            var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
            caller = caller with { AuthenticationKind = "firebase", AuthenticatedAt = DateTimeOffset.UtcNow };
            var identity = new Identity();
            var payments = DispatchProxy.Create<IPaymentProvider, NoPayments>();
            return new(db, store, caller, identity, new AccountDeletionService(store, identity, new SystemClock(), payments));
        }
        internal Task DeleteAsync() => Service.DeleteAsync(Caller, true, default);
    }

    public class NoPayments : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) =>
            throw new InvalidOperationException("No mandates exist; no payment API may run.");
    }

    private sealed class Identity : IIdentityService
    {
        internal Func<Task> Delete { get; set; } = () => Task.CompletedTask;
        internal int Calls;
        public Task DeleteFirebaseUserAsync(string userId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Delete(); }
        public Task<CallerIdentity?> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken token) => throw new NotSupportedException();
        public Task<CallerIdentity> VerifyFirebaseAsync(string token, CancellationToken cancellation) => throw new NotSupportedException();
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Resume(AccountDeletionService service, string userId)
    {
        var method = typeof(AccountDeletionService).GetMethod("ResumePendingDeletionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method); // Missing-seam RED remains compilable.
        try { return (Task)method.Invoke(service, [userId, CancellationToken.None])!; }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
}
