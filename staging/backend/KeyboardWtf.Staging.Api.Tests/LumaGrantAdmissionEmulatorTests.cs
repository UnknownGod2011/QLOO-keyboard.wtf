using EmulatorDetection = Google.Api.Gax.EmulatorDetection;
using Google.Cloud.Firestore;
using Grpc.Core;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Staging.Api.Tests;

// Real transactions, but only in a GUID-owned loopback emulator project. These
// checks never mint a token, obtain credentials, contact a provider or use money.
[Collection("Local accounting emulator")]
public sealed class LumaGrantAdmissionEmulatorTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Window = DateTimeOffset.Parse("2026-10-07T12:00:00Z");

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task A_grant_writes_only_bounded_pseudonymous_admission_without_financial_effects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.StateAsync();

        await fixture.AdmitAsync(fixture.Owner, Window);

        var global = await fixture.Global.GetSnapshotAsync();
        var owner = await fixture.OwnerCounter(fixture.Owner).GetSnapshotAsync();
        Assert.Equal(new[] { "count", "day", "expiresAt", "leases" }, global.ToDictionary().Keys.Order().ToArray());
        Assert.Equal(new[] { "count", "day", "expiresAt" }, owner.ToDictionary().Keys.Order().ToArray());
        Assert.Equal(1, global.GetValue<long>("count"));
        Assert.Equal(1, owner.GetValue<long>("count"));
        Assert.Equal(Ts(Window.UtcDateTime.Date), global.GetValue<Timestamp>("day"));
        Assert.Equal(Ts(Window.UtcDateTime.Date.AddDays(2)), global.GetValue<Timestamp>("expiresAt"));
        Assert.Equal(global.GetValue<Timestamp>("expiresAt"), owner.GetValue<Timestamp>("expiresAt"));
        var leases = global.GetValue<Dictionary<string, object>>("leases");
        Assert.Equal(Ts(Window.AddSeconds(150)), Assert.Single(leases).Value);
        Assert.Equal(OwnerHash(fixture.Owner), Assert.Single(leases).Key);
        Assert.DoesNotContain(fixture.Owner, Canonical(global.ToDictionary()));
        Assert.Equal(2, (await fixture.Admission.GetSnapshotAsync()).Count);
        Assert.Equal(before, await fixture.StateAsync());
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Concurrent_same_owner_attempts_commit_one_lease_and_one_count()
    {
        await using var fixture = await Fixture.CreateAsync();
        var financialBefore = await fixture.StateAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => RecordConcurrentAttemptAsync(fixture, fixture.Owner, Window)));
        WriteConcurrentReceipt("same-owner", attempts);

        Assert.Single(attempts, value => value == AdmissionOutcome.Accepted);
        Assert.Equal(1, (await fixture.Global.GetSnapshotAsync()).GetValue<long>("count"));
        Assert.Equal(1, (await fixture.OwnerCounter(fixture.Owner).GetSnapshotAsync()).GetValue<long>("count"));
        Assert.Single((await fixture.Global.GetSnapshotAsync()).GetValue<Dictionary<string, object>>("leases"));
        Assert.Equal(2, (await fixture.Admission.GetSnapshotAsync()).Count);
        Assert.Equal(financialBefore, await fixture.StateAsync());
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("concurrency")]
    [InlineData("global-day")]
    public async Task Concurrent_distinct_owners_cannot_exceed_the_transactional_project_cap(string cap)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.MaxConcurrentSessions = cap == "concurrency" ? 3 : 20;
        fixture.Settings.MaxGrantsGlobalPerDay = cap == "global-day" ? 3 : 40;
        var owners = new List<string> { fixture.Owner };
        for (var i = 1; i < 8; i++) owners.Add(await fixture.AddOwnerAsync());
        var financialBefore = await fixture.StateAsync();

        var attempts = await Task.WhenAll(owners.Select(owner => RecordConcurrentAttemptAsync(fixture, owner, Window)));
        WriteConcurrentReceipt(cap, attempts);

        Assert.Equal(3, attempts.Count(value => value == AdmissionOutcome.Accepted));
        var global = await fixture.Global.GetSnapshotAsync();
        Assert.Equal(3, global.GetValue<long>("count"));
        Assert.Equal(3, global.GetValue<Dictionary<string, object>>("leases").Count);
        Assert.Equal(4, (await fixture.Admission.GetSnapshotAsync()).Count);
        foreach (var (owner, index) in owners.Select((owner, index) => (owner, index)))
        {
            var counter = await fixture.OwnerCounter(owner).GetSnapshotAsync();
            Assert.Equal(attempts[index] == AdmissionOutcome.Accepted, counter.Exists);
            if (counter.Exists) Assert.Equal(1, counter.GetValue<long>("count"));
        }
        Assert.Equal(financialBefore, await fixture.StateAsync());
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("owner-day")]
    [InlineData("global-day")]
    public async Task Expired_connections_do_not_reset_same_day_grant_counts(string cap)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (cap == "owner-day") fixture.Settings.MaxGrantsPerOwnerPerDay = 2;
        else fixture.Settings.MaxGrantsGlobalPerDay = 2;
        await fixture.AdmitAsync(fixture.Owner, Window);
        await fixture.AdmitAsync(fixture.Owner, Window.AddSeconds(151));
        var before = await fixture.StateAsync(includeAdmission: true);

        await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => fixture.AdmitAsync(fixture.Owner, Window.AddSeconds(302)));

        Assert.Equal(2, (await fixture.Global.GetSnapshotAsync()).GetValue<long>("count"));
        Assert.Equal(2, (await fixture.OwnerCounter(fixture.Owner).GetSnapshotAsync()).GetValue<long>("count"));
        Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Sequential_concurrency_cap_is_exact_and_reopens_only_after_existing_leases_expire()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.MaxConcurrentSessions = 2;
        var second = await fixture.AddOwnerAsync();
        var third = await fixture.AddOwnerAsync();
        var financialBefore = await fixture.StateAsync();
        await fixture.AdmitAsync(fixture.Owner, Window);
        await fixture.AdmitAsync(second, Window);
        var saturated = await fixture.StateAsync(includeAdmission: true);

        await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => fixture.AdmitAsync(third, Window));

        Assert.Equal(saturated, await fixture.StateAsync(includeAdmission: true));
        Assert.False((await fixture.OwnerCounter(third).GetSnapshotAsync()).Exists);
        var full = await fixture.Global.GetSnapshotAsync();
        Assert.Equal(2, full.GetValue<long>("count"));
        Assert.Equal(2, full.GetValue<Dictionary<string, object>>("leases").Count);
        await fixture.AdmitAsync(third, Window.AddSeconds(150));
        var reopened = await fixture.Global.GetSnapshotAsync();
        Assert.Equal(3, reopened.GetValue<long>("count"));
        Assert.Equal(OwnerHash(third), Assert.Single(reopened.GetValue<Dictionary<string, object>>("leases")).Key);
        Assert.Equal(1, (await fixture.OwnerCounter(third).GetSnapshotAsync()).GetValue<long>("count"));
        Assert.Equal(financialBefore, await fixture.StateAsync());
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(149, false)]
    [InlineData(150, true)]
    [InlineData(151, true)]
    public async Task Lease_expiry_includes_the_full_provider_grant_and_thirty_second_grace(int seconds, bool admitted)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync(fixture.Owner, Window);
        var before = await fixture.StateAsync(includeAdmission: true);

        Assert.Equal(admitted, await TryAdmitAsync(fixture, fixture.Owner, Window.AddSeconds(seconds)));

        if (admitted)
        {
            var global = await fixture.Global.GetSnapshotAsync();
            Assert.Equal(2, global.GetValue<long>("count"));
            Assert.Equal(Ts(Window.AddSeconds(seconds + 150)),
                global.GetValue<Dictionary<string, object>>("leases")[OwnerHash(fixture.Owner)]);
        }
        else Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Utc_day_rollover_resets_daily_counts_but_preserves_an_active_previous_day_lease()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.MaxGrantsPerOwnerPerDay = 1;
        fixture.Settings.MaxGrantsGlobalPerDay = 1;
        var beforeMidnight = DateTimeOffset.Parse("2026-10-07T23:59:40Z");
        await fixture.AdmitAsync(fixture.Owner, beforeMidnight);
        var afterMidnight = beforeMidnight.AddSeconds(21);
        var before = await fixture.StateAsync(includeAdmission: true);
        await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => fixture.AdmitAsync(fixture.Owner, afterMidnight));
        Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));

        var other = await fixture.AddOwnerAsync();
        await fixture.AdmitAsync(other, afterMidnight);
        var global = await fixture.Global.GetSnapshotAsync();
        Assert.Equal(1, global.GetValue<long>("count"));
        Assert.Equal(Ts(afterMidnight.UtcDateTime.Date), global.GetValue<Timestamp>("day"));
        var leases = global.GetValue<Dictionary<string, object>>("leases");
        Assert.Equal(2, leases.Count);
        Assert.Equal(Ts(beforeMidnight.AddSeconds(150)), leases[OwnerHash(fixture.Owner)]);
        Assert.Equal(Ts(afterMidnight.AddSeconds(150)), leases[OwnerHash(other)]);
        Assert.Equal(Ts(beforeMidnight.UtcDateTime.Date),
            (await fixture.OwnerCounter(fixture.Owner).GetSnapshotAsync()).GetValue<Timestamp>("day"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Daily_key_uses_utc_not_the_input_timestamp_offset()
    {
        await using var fixture = await Fixture.CreateAsync();
        var offsetTime = DateTimeOffset.Parse("2026-10-08T00:15:00+05:30");

        await fixture.AdmitAsync(fixture.Owner, offsetTime);

        var global = await fixture.Global.GetSnapshotAsync();
        Assert.Equal(Ts(DateTime.SpecifyKind(new DateTime(2026, 10, 7), DateTimeKind.Utc)), global.GetValue<Timestamp>("day"));
        Assert.Equal(Ts(offsetTime.AddSeconds(150)),
            global.GetValue<Dictionary<string, object>>("leases")[OwnerHash(fixture.Owner)]);
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Expired_leases_are_removed_on_admission_without_waiting_for_ttl_cleanup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var stale = await fixture.AddOwnerAsync();
        await fixture.Global.SetAsync(GlobalState(Window, 5, new Dictionary<string, object>
        {
            [OwnerHash(stale)] = Ts(Window.AddTicks(-1)),
        }));

        await fixture.AdmitAsync(fixture.Owner, Window);

        var global = await fixture.Global.GetSnapshotAsync();
        Assert.Equal(6, global.GetValue<long>("count"));
        Assert.Equal(OwnerHash(fixture.Owner), Assert.Single(global.GetValue<Dictionary<string, object>>("leases")).Key);
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("missing-account")]
    [InlineData("deleting")]
    [InlineData("deleted")]
    [InlineData("suspended")]
    [InlineData("unknown")]
    [InlineData("ACTIVE")]
    [InlineData("invalid-type")]
    public async Task Missing_or_inactive_accounts_cannot_consume_any_admission(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (state == "missing-account") await fixture.Account(fixture.Owner).DeleteAsync();
        else await fixture.Account(fixture.Owner).UpdateAsync("status", state == "invalid-type" ? 42L : state);
        var before = await fixture.StateAsync(includeAdmission: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AdmitAsync(fixture.Owner, Window));

        Assert.Empty((await fixture.Admission.GetSnapshotAsync()).Documents);
        Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("missing")]
    [InlineData("null")]
    public async Task Existing_legacy_active_status_compatibility_is_preserved(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Account(fixture.Owner).UpdateAsync("status", state == "missing" ? FieldValue.Delete : null!);

        await fixture.AdmitAsync(fixture.Owner, Window);

        Assert.Equal(1, (await fixture.Global.GetSnapshotAsync()).GetValue<long>("count"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task Account_deletion_after_a_grant_denies_the_next_attempt_without_releasing_the_existing_lease()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync(fixture.Owner, Window);
        await fixture.Account(fixture.Owner).UpdateAsync("status", "deleted");
        var before = await fixture.StateAsync(includeAdmission: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AdmitAsync(fixture.Owner, Window.AddSeconds(1)));

        Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData("global", "day-missing")]
    [InlineData("global", "day-wrong-type")]
    [InlineData("global", "count-missing")]
    [InlineData("global", "count-negative")]
    [InlineData("global", "count-fraction")]
    [InlineData("owner", "day-missing")]
    [InlineData("owner", "day-wrong-type")]
    [InlineData("owner", "count-missing")]
    [InlineData("owner", "count-negative")]
    [InlineData("owner", "count-fraction")]
    [InlineData("global", "leases-missing")]
    [InlineData("global", "leases-wrong-type")]
    [InlineData("global", "leases-over-cap")]
    [InlineData("global", "lease-owner-invalid")]
    [InlineData("global", "lease-expiry-invalid")]
    public async Task Malformed_stored_admission_is_rejected_without_repairing_or_resetting_counters(string scope, string defect)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings.MaxConcurrentSessions = 2;
        var global = GlobalState(Window, 1, new Dictionary<string, object>());
        await fixture.Global.SetAsync(global);
        var target = scope == "global" ? fixture.Global : fixture.OwnerCounter(fixture.Owner);
        var data = scope == "global" ? global : CounterState(Window, 1);
        switch (defect)
        {
            case "day-missing": data.Remove("day"); break;
            case "day-wrong-type": data["day"] = "invalid"; break;
            case "count-missing": data.Remove("count"); break;
            case "count-negative": data["count"] = -1L; break;
            case "count-fraction": data["count"] = 1.5; break;
            case "leases-missing": data.Remove("leases"); break;
            case "leases-wrong-type": data["leases"] = "invalid"; break;
            case "leases-over-cap": data["leases"] = Enumerable.Range(0, 3).ToDictionary(
                i => OwnerHash("synthetic-stale-" + i), _ => (object)Ts(Window.AddSeconds(150))); break;
            case "lease-owner-invalid": data["leases"] = new Dictionary<string, object>
                { [new string('z', 64)] = Ts(Window.AddSeconds(150)) }; break;
            case "lease-expiry-invalid": data["leases"] = new Dictionary<string, object>
                { [OwnerHash(fixture.Owner)] = "invalid" }; break;
            default: throw new InvalidOperationException("Unknown synthetic defect.");
        }
        await target.SetAsync(data);
        var before = await fixture.StateAsync(includeAdmission: true);

        await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => fixture.AdmitAsync(fixture.Owner, Window));

        Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));
    }

    [LocalFirestoreTheory, Trait("Category", "FirestoreEmulator")]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(121)]
    public async Task Invalid_requested_expiry_is_rejected_before_any_transaction_write(int seconds)
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.StateAsync(includeAdmission: true);

        await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => fixture.Service.AdmitAsync(
            fixture.Owner, Window, Window.AddSeconds(seconds), CancellationToken.None));

        Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task An_ambiguous_post_admission_failure_keeps_one_lease_across_repeated_retries()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Simulate only the boundary after committed admission. No token mint,
        // provider, credential, HTTP request or early-release callback exists here.
        await fixture.AdmitAsync(fixture.Owner, Window);
        var committed = await fixture.StateAsync(includeAdmission: true);

        for (var i = 0; i < 8; i++)
        {
            await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => fixture.AdmitAsync(fixture.Owner, Window.AddSeconds(i + 1)));
            Assert.Equal(committed, await fixture.StateAsync(includeAdmission: true));
        }

        await fixture.AdmitAsync(fixture.Owner, Window.AddSeconds(150));
        Assert.Equal(2, (await fixture.Global.GetSnapshotAsync()).GetValue<long>("count"));
        Assert.Equal(2, (await fixture.OwnerCounter(fixture.Owner).GetSnapshotAsync()).GetValue<long>("count"));
    }

    [LocalFirestoreFact, Trait("Category", "FirestoreEmulator")]
    public async Task A_pre_cancelled_attempt_has_no_admission_or_financial_write()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.StateAsync(includeAdmission: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(() => fixture.Service.AdmitAsync(
            fixture.Owner, Window, Window.AddSeconds(120), cancellation.Token));
        // Firestore's gRPC transport can preserve cancellation as its own status
        // instead of wrapping it in OperationCanceledException. Neither is success.
        Assert.True(exception is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled },
            "The explicitly cancelled attempt must end with a cancellation, not success or another failure.");

        Assert.Equal(before, await fixture.StateAsync(includeAdmission: true));
    }

    private static async Task<bool> TryAdmitAsync(Fixture fixture, string owner, DateTimeOffset now)
    {
        try { await fixture.AdmitAsync(owner, now); return true; }
        catch (LumaVoiceUnavailableException) { return false; }
    }

    private enum AdmissionOutcome { Accepted, PolicyDenied, TransactionAborted }

    private static async Task<AdmissionOutcome> RecordConcurrentAttemptAsync(Fixture fixture, string owner, DateTimeOffset now)
    {
        try { await fixture.AdmitAsync(owner, now); return AdmissionOutcome.Accepted; }
        catch (LumaVoiceUnavailableException) { return AdmissionOutcome.PolicyDenied; }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Aborted)
        {
            // A contention abort is NOT a successful policy rejection. Preserve
            // that distinction in the receipt and still demand exact committed
            // counts/owner documents/leases below. No test-side retry is added.
            return AdmissionOutcome.TransactionAborted;
        }
    }

    private void WriteConcurrentReceipt(string scenario, AdmissionOutcome[] attempts) => output.WriteLine(
        "scenario={0}; accepted={1}; policyDenied={2}; sdkTransactionAborted={3}; availabilityNotCertified={4}",
        scenario, attempts.Count(value => value == AdmissionOutcome.Accepted),
        attempts.Count(value => value == AdmissionOutcome.PolicyDenied),
        attempts.Count(value => value == AdmissionOutcome.TransactionAborted),
        attempts.Any(value => value == AdmissionOutcome.TransactionAborted));

    private static string OwnerHash(string owner) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("luma-owner:" + owner))).ToLowerInvariant();
    private static Timestamp Ts(DateTimeOffset value) => Timestamp.FromDateTime(value.UtcDateTime);
    private static Timestamp Ts(DateTime value) => Timestamp.FromDateTime(value);
    private static Dictionary<string, object> CounterState(DateTimeOffset now, long count) => new()
    {
        ["day"] = Ts(now.UtcDateTime.Date), ["count"] = count,
        ["expiresAt"] = Ts(now.UtcDateTime.Date.AddDays(2)),
    };
    private static Dictionary<string, object> GlobalState(DateTimeOffset now, long count, Dictionary<string, object> leases)
    {
        var result = CounterState(now, count);
        result["leases"] = leases;
        return result;
    }

    private sealed record DocumentState(string Path, bool Exists, Timestamp? Updated, string Data);
    private static string Canonical(object? value) => value switch
    {
        IDictionary<string, object> map => "{" + string.Join(",", map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        Timestamp stamp => "timestamp:" + stamp.ToDateTime().ToString("O"),
        IEnumerable<object> items => "[" + string.Join(",", items.Select(Canonical)) + "]",
        _ => JsonSerializer.Serialize(value),
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private const string ProjectPrefix = "kwt-luma-local-";
        private readonly List<string> _owners = [];
        internal FirestoreDb Db { get; }
        internal LumaLiveOptions Settings { get; } = new()
        {
            Enabled = true, ModelEligibilityVerified = true, RetentionPolicyVerified = true,
            AllowedOrigins = ["https://keyboardwtf.com"],
        };
        internal FirestoreLumaGrantAdmission Service { get; }
        internal string Owner => _owners[0];
        internal CollectionReference Admission => Db.Collection("lumaAdmission");
        internal DocumentReference Global => Admission.Document("global");
        internal DocumentReference OwnerCounter(string owner) => Admission.Document("owner-" + OwnerHash(owner));
        internal DocumentReference Account(string owner) => Db.Collection("accounts").Document(FirestoreAccountStore.SafeId(owner));

        private Fixture(FirestoreDb db)
        {
            Db = db;
            Service = new(db, Options.Create(Settings));
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var host = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
            Assert.False(string.IsNullOrWhiteSpace(host));
            var endpoint = new Uri("http://" + host);
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.Empty(endpoint.UserInfo);
            Assert.Equal("/", endpoint.AbsolutePath);
            Assert.Empty(endpoint.Query);
            Assert.Empty(endpoint.Fragment);
            var db = new FirestoreDbBuilder
            {
                ProjectId = ProjectPrefix + Guid.NewGuid().ToString("N"),
                EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build();
            var fixture = new Fixture(db);
            await fixture.AddOwnerAsync();
            await db.Collection("globalUsageShards").Document("synthetic-financial-sentinel").SetAsync(new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = 123L, ["settledCreditMicros"] = 456L,
            });
            await db.Collection("idempotency").Document("synthetic-financial-sentinel").SetAsync(new Dictionary<string, object>
            {
                ["scope"] = "synthetic-financial-only", ["createdAt"] = Ts(Window),
            });
            return fixture;
        }

        internal async Task<string> AddOwnerAsync()
        {
            var owner = "synthetic-luma-user-" + Guid.NewGuid().ToString("N");
            _owners.Add(owner);
            var account = Account(owner);
            await account.SetAsync(new Dictionary<string, object>
            {
                ["status"] = "active", ["availableCreditMicros"] = 321L,
                ["reservedCreditMicros"] = 123L, ["cycleUsageCreditMicros"] = 42L,
                ["activeRequests"] = 0L, ["activeLiveRequests"] = 0L,
                ["updatedAt"] = Ts(Window),
            });
            await account.Collection("ledger").Document("synthetic-financial-sentinel").SetAsync(new Dictionary<string, object>
            {
                ["type"] = "SYNTHETIC_ONLY", ["availableDeltaCreditMicros"] = 7L,
            });
            return owner;
        }

        internal Task AdmitAsync(string owner, DateTimeOffset now) =>
            Service.AdmitAsync(owner, now, now.AddSeconds(Settings.SessionSeconds), CancellationToken.None);

        internal async Task<DocumentState[]> StateAsync(bool includeAdmission = false)
        {
            var states = new Dictionary<string, DocumentState>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            async Task VisitDocumentAsync(DocumentReference reference)
            {
                if (!visited.Add(reference.Path)) return;
                var snapshot = await reference.GetSnapshotAsync();
                states[reference.Path] = new(reference.Path, snapshot.Exists, snapshot.UpdateTime,
                    snapshot.Exists ? Canonical(snapshot.ToDictionary()) : "missing");
                await foreach (var child in reference.ListCollectionsAsync()) await VisitCollectionAsync(child);
            }
            async Task VisitCollectionAsync(CollectionReference collection)
            {
                foreach (var document in (await collection.GetSnapshotAsync()).Documents)
                    await VisitDocumentAsync(document.Reference);
            }
            foreach (var owner in _owners) await VisitDocumentAsync(Account(owner));
            await foreach (var collection in Db.ListRootCollectionsAsync())
                if (includeAdmission || collection.Id != "lumaAdmission") await VisitCollectionAsync(collection);
            return states.Values.OrderBy(state => state.Path, StringComparer.Ordinal).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            Assert.True(Db.ProjectId.StartsWith(ProjectPrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(Db.ProjectId[ProjectPrefix.Length..], "N", out _));
            var prefix = $"projects/{Db.ProjectId}/databases/{Db.DatabaseId}/documents/";
            foreach (var document in (await StateAsync(includeAdmission: true)).OrderByDescending(state => state.Path.Length))
            {
                Assert.StartsWith(prefix, document.Path, StringComparison.Ordinal);
                await Db.Document(document.Path[prefix.Length..]).DeleteAsync();
            }
        }
    }
}
