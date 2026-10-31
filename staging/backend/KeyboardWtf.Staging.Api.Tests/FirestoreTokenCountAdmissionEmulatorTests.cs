using EmulatorDetection = Google.Api.Gax.EmulatorDetection;
using Google.Cloud.Firestore;
using Grpc.Core;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Staging.Api.Tests;

// Same loopback-only/EmulatorOnly convention as AccountingEmulatorTests, with
// a GUID-owned project per fixture so the fixed project counter is independent.
[Collection("Local accounting emulator")]
public sealed class FirestoreTokenCountAdmissionEmulatorTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Window = DateTimeOffset.Parse("2026-10-04T00:10:00Z");
    private const long TaskCap = 100_000;

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("active", false)]
    [InlineData("active", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("null", false)]
    [InlineData("null", true)]
    public async Task Active_and_legacy_accounts_get_one_permit_per_actual_call_without_any_financial_write(
        string status, bool legacyWallet)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        if (status == "missing") await fixture.Account.UpdateAsync("status", FieldValue.Delete);
        if (status == "null") await fixture.Account.UpdateAsync("status", null!);
        if (legacyWallet) await fixture.Account.UpdateAsync("allowanceBuckets", FieldValue.Delete);
        var financialBefore = await fixture.StateAsync();
        var requestId = fixture.RequestId;

        await api.AdmitAsync(fixture, fixture.Caller, requestId);
        await fixture.AssertCounterAsync(fixture.ProjectCounter, 1);
        await fixture.AssertCounterAsync(fixture.UserCounter(fixture.Caller), 1);
        // Candidate two is a second actual CountTokens call, not a reservation
        // of two permits up front or a replay after monetary reservation.
        await api.AdmitAsync(fixture, fixture.Caller, requestId);

        await fixture.AssertCounterAsync(fixture.ProjectCounter, 2);
        await fixture.AssertCounterAsync(fixture.UserCounter(fixture.Caller), 2);
        Assert.Equal(2, (await fixture.Preflight.GetSnapshotAsync()).Documents.Count);
        Assert.Equal(financialBefore, await fixture.StateAsync());
        Assert.False((await fixture.ReserveIdempotency(fixture.Caller, requestId).GetSnapshotAsync()).Exists);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("missing-account")]
    [InlineData("deleted")]
    [InlineData("deleting")]
    [InlineData("suspended")]
    [InlineData("unknown")]
    [InlineData("ACTIVE")]
    public async Task Missing_or_nonactive_current_account_is_rejected_without_consuming_permits(string status)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        if (status == "missing-account") await fixture.Account.DeleteAsync();
        else await fixture.Account.UpdateAsync("status", status);
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("poor-legacy")]
    [InlineData("poor-bucket")]
    [InlineData("expired-bucket")]
    public async Task Detached_wallet_requires_the_full_task_cap_and_does_not_persist_migration_or_expiry(string wallet)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        var available = wallet == "expired-bucket" ? 1_000_000L : TaskCap - 1;
        await fixture.Account.UpdateAsync(new Dictionary<string, object>
        {
            ["availableCreditMicros"] = available,
            ["allowanceBuckets"] = wallet == "poor-legacy" ? FieldValue.Delete
                : Fixture.Buckets(available, wallet == "expired-bucket" ? Window.AddSeconds(-1).ToUnixTimeSeconds() : 0),
        });
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAsync<ManagedAllowanceRefusedException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(0L, 0L, true)]
    [InlineData(1L, 1L, true)]
    [InlineData(1L, 0L, false)]
    [InlineData(2L, 1L, false)]
    public async Task Current_one_shot_capacity_blocks_saturation_but_allows_a_healthy_Live_session(
        long active, long live, bool allowed)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Account.UpdateAsync(new Dictionary<string, object>
        {
            ["activeRequests"] = active, ["activeLiveRequests"] = live,
        });
        var before = await fixture.StateAsync();

        if (allowed)
        {
            await api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId);
            await fixture.AssertCounterAsync(fixture.ProjectCounter, 1);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));
            Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
        }

        Assert.Equal(before, await fixture.StateAsync());
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Already_reserved_request_id_is_rejected_by_the_existing_idempotency_document_before_permit()
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ReserveIdempotency(fixture.Caller, fixture.RequestId).SetAsync(new Dictionary<string, object>
        {
            ["scope"] = "usage-reserve", ["userId"] = fixture.Caller.UserId,
            ["resourceId"] = fixture.RequestId, ["createdAt"] = Ts(Window),
        });
        await fixture.Account.Collection("reservations").Document(fixture.RequestId).SetAsync(new Dictionary<string, object>
        {
            ["requestId"] = fixture.RequestId, ["status"] = "settled", ["reservedCreditMicros"] = 0L,
        });
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(1, 2L)]
    [InlineData(12, 24L)]
    [InlineData(750, 1_500L)]
    public async Task UID_limit_is_twice_current_request_limit_and_denial_cannot_increment_project_counter(int requests, long cap)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync(projectLimit: 1_500, userRequests: requests);
        await fixture.UserCounter(fixture.Caller).SetAsync(Counter(Window, cap - 1));
        var financialBefore = await fixture.StateAsync();

        await api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId);
        await fixture.AssertCounterAsync(fixture.UserCounter(fixture.Caller), cap);
        await fixture.AssertCounterAsync(fixture.ProjectCounter, 1);
        var afterPermit = await fixture.StateAsync(includePreflight: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));

        Assert.Equal(afterPermit, await fixture.StateAsync(includePreflight: true));
        Assert.Equal(financialBefore, await fixture.StateAsync());
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Shared_project_limit_blocks_another_UID_without_creating_or_charging_its_counter()
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync(projectLimit: 2);
        var second = await fixture.AddCallerAsync();
        await api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId);
        await api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId);
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.AdmitAsync(fixture, second, fixture.RequestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.False((await fixture.UserCounter(second).GetSnapshotAsync()).Exists);
        Assert.Equal(2, (await fixture.Preflight.GetSnapshotAsync()).Documents.Count);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Minute_rollover_overwrites_fixed_documents_and_uses_UTC_window_end_based_TTL()
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync(projectLimit: 1, userRequests: 1);
        fixture.Clock.Now = Window.AddSeconds(59).ToOffset(TimeSpan.FromMinutes(330));
        var financialBefore = await fixture.StateAsync();
        await api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId);
        await fixture.AssertCounterAsync(fixture.ProjectCounter, 1);
        fixture.Clock.Now = Window.AddMinutes(1);

        await api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId);

        await fixture.AssertCounterAsync(fixture.ProjectCounter, 1, Window.AddMinutes(1));
        await fixture.AssertCounterAsync(fixture.UserCounter(fixture.Caller), 1, Window.AddMinutes(1));
        Assert.Equal(new[] { "count-tokens-project", "user-" + Crypto.Sha256(fixture.Caller.UserId) }.Order(StringComparer.Ordinal),
            (await fixture.Preflight.GetSnapshotAsync()).Documents.Select(doc => doc.Id).Order(StringComparer.Ordinal));
        Assert.Equal(financialBefore, await fixture.StateAsync());
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("project")]
    [InlineData("user")]
    public async Task Future_window_from_clock_rollback_denies_without_resetting_or_mutating_any_document(string scope)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CounterFor(scope).SetAsync(Counter(Window.AddMinutes(1), 1));
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
    }

    public static IEnumerable<object[]> MalformedCounters()
    {
        foreach (var scope in new[] { "project", "user" })
        foreach (var expired in new[] { false, true })
        foreach (var shape in new[] { "missing-window", "string-window", "unaligned-window", "missing-count",
                     "string-count", "fractional-count", "negative-count", "bad-updatedAt", "missing-updatedAt",
                     "updated-before-window", "updated-after-window", "bad-expiresAt", "missing-expiresAt",
                     "early-expiry", "late-expiry" })
            yield return [scope, expired, shape];
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(MalformedCounters))]
    public async Task Malformed_current_or_expired_prototype_counter_fails_closed_instead_of_resetting(
        string scope, bool expired, string shape)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        var start = expired ? Window.AddMinutes(-10) : Window;
        var data = Counter(start, 0);
        switch (shape)
        {
            case "missing-window": data.Remove("windowStart"); break;
            case "string-window": data["windowStart"] = "2026-10-04T00:10:00Z"; break;
            case "unaligned-window": data["windowStart"] = Ts(start.AddSeconds(1)); break;
            case "missing-count": data.Remove("count"); break;
            case "string-count": data["count"] = "0"; break;
            case "fractional-count": data["count"] = 0.5; break;
            case "negative-count": data["count"] = -1L; break;
            case "bad-updatedAt": data["updatedAt"] = "timestamp"; break;
            case "missing-updatedAt": data.Remove("updatedAt"); break;
            case "updated-before-window": data["updatedAt"] = Ts(start.AddMilliseconds(-1)); break;
            case "updated-after-window": data["updatedAt"] = Ts(start.AddMinutes(1)); break;
            case "bad-expiresAt": data["expiresAt"] = "timestamp"; break;
            case "missing-expiresAt": data.Remove("expiresAt"); break;
            case "early-expiry": data["expiresAt"] = Ts(start.AddMinutes(3).AddMilliseconds(-1)); break;
            case "late-expiry": data["expiresAt"] = Ts(start.AddMinutes(3).AddMilliseconds(1)); break;
        }
        await fixture.CounterFor(scope).SetAsync(data);
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("activeRequests", "negative")]
    [InlineData("activeRequests", "string")]
    [InlineData("activeRequests", "fractional")]
    [InlineData("activeRequests", "boolean")]
    [InlineData("activeLiveRequests", "negative")]
    [InlineData("activeLiveRequests", "string")]
    [InlineData("activeLiveRequests", "fractional")]
    [InlineData("activeLiveRequests", "boolean")]
    public async Task Malformed_account_concurrency_is_not_coerced_into_an_admission(string field, string shape)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        object value = shape switch
        {
            "negative" => -1L,
            "string" => "0",
            "fractional" => 0.5,
            "boolean" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        await fixture.Account.UpdateAsync(field, value);
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("missing")]
    [InlineData("null")]
    public async Task Legacy_missing_or_null_concurrency_fields_remain_zero_without_account_migration(string shape)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Account.UpdateAsync(new Dictionary<string, object>
        {
            ["activeRequests"] = shape == "missing" ? FieldValue.Delete : null!,
            ["activeLiveRequests"] = shape == "missing" ? FieldValue.Delete : null!,
        });
        var before = await fixture.StateAsync();

        await api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId);

        await fixture.AssertCounterAsync(fixture.ProjectCounter, 1);
        await fixture.AssertCounterAsync(fixture.UserCounter(fixture.Caller), 1);
        Assert.Equal(before, await fixture.StateAsync());
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Healthy_replicas_with_same_minute_clock_skew_share_the_atomic_counter()
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        var ahead = new MutableClock { Now = Window.AddSeconds(30).AddMilliseconds(50) };
        var aheadStore = api.Create(fixture.Db, ahead, fixture.Options);
        var behindStore = api.Create(fixture.Db, fixture.Clock, fixture.Options);
        var before = await fixture.StateAsync();

        await api.InvokeAsync(aheadStore, fixture.Caller, fixture.RequestId, CancellationToken.None);
        Assert.Equal(Ts(ahead.Now), (await fixture.ProjectCounter.GetSnapshotAsync()).GetValue<Timestamp>("updatedAt"));
        Assert.True(ahead.Now > fixture.Clock.Now);
        await api.InvokeAsync(behindStore, fixture.Caller, fixture.RequestId, CancellationToken.None);

        await fixture.AssertCounterAsync(fixture.ProjectCounter, 2);
        await fixture.AssertCounterAsync(fixture.UserCounter(fixture.Caller), 2);
        Assert.Equal(before, await fixture.StateAsync());
        Assert.Equal(2, (await fixture.Preflight.GetSnapshotAsync()).Documents.Count);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Permit_committed_in_the_previous_minute_is_consumed_but_never_acknowledged_for_dispatch()
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.StateAsync();
        var clock = new CommitBoundaryClock();
        var store = api.Create(fixture.Db, clock, fixture.Options);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.InvokeAsync(store, fixture.Caller, fixture.RequestId, CancellationToken.None));

        Assert.Contains("expired", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, clock.Reads); // No hidden re-admission or transaction retry may make this evidence pass.
        foreach (var reference in new[] { fixture.ProjectCounter, fixture.UserCounter(fixture.Caller) })
        {
            var snapshot = await reference.GetSnapshotAsync();
            Assert.Equal(1L, snapshot.GetValue<long>("count"));
            Assert.Equal(Ts(Window), snapshot.GetValue<Timestamp>("windowStart"));
            Assert.Equal(Ts(Window.AddSeconds(59)), snapshot.GetValue<Timestamp>("updatedAt"));
            Assert.Equal(Ts(Window.AddMinutes(3)), snapshot.GetValue<Timestamp>("expiresAt"));
        }
        Assert.Equal(before, await fixture.StateAsync());
        Assert.Equal(2, (await fixture.Preflight.GetSnapshotAsync()).Documents.Count);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("project-zero")]
    [InlineData("project-negative")]
    [InlineData("project-over-limit")]
    [InlineData("project-overflow")]
    [InlineData("user-zero")]
    [InlineData("user-negative")]
    [InlineData("user-over-limit")]
    [InlineData("user-overflow")]
    [InlineData("concurrent-zero")]
    [InlineData("task-cap-zero")]
    public async Task Unsafe_server_options_are_rejected_without_permit_or_money_writes(string invalid)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        var options = OptionsFor(fixture.Db.ProjectId, invalid switch
        {
            "project-zero" => 0, "project-negative" => -1, "project-over-limit" => 1_501,
            "project-overflow" => int.MaxValue, _ => 1_000,
        }, invalid switch
        {
            "user-zero" => 0, "user-negative" => -1, "user-over-limit" => 751,
            "user-overflow" => int.MaxValue, _ => 12,
        }, concurrent: invalid == "concurrent-zero" ? 0 : 1, taskCap: invalid == "task-cap-zero" ? 0 : TaskCap);
        var before = await fixture.StateAsync(includePreflight: true);

        await AssertRejectedAsync(() => api.AdmitAsync(fixture, fixture.Caller, fixture.RequestId, options));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("uid-empty")]
    [InlineData("uid-space")]
    [InlineData("uid-slash")]
    [InlineData("uid-long")]
    [InlineData("request-empty")]
    [InlineData("request-space")]
    [InlineData("request-slash")]
    [InlineData("request-long")]
    public async Task Invalid_UID_or_request_identifier_cannot_create_counter_or_financial_records(string invalid)
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        var value = invalid switch
        {
            var name when name.EndsWith("empty", StringComparison.Ordinal) => "",
            var name when name.EndsWith("space", StringComparison.Ordinal) => " ",
            var name when name.EndsWith("slash", StringComparison.Ordinal) => "unowned/path",
            _ => new string('a', 513),
        };
        var caller = invalid.StartsWith("uid", StringComparison.Ordinal) ? fixture.Caller with { UserId = value } : fixture.Caller;
        var requestId = invalid.StartsWith("request", StringComparison.Ordinal) ? value : fixture.RequestId;
        var before = await fixture.StateAsync(includePreflight: true);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => api.AdmitAsync(fixture, caller, requestId));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Separate_store_instances_contend_for_one_project_permit_and_SDK_abort_is_not_admission()
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync(projectLimit: 1);
        var second = await fixture.AddCallerAsync();
        var firstStore = api.Create(fixture.Db, fixture.Clock, fixture.Options);
        var secondStore = api.Create(fixture.Db, fixture.Clock, fixture.Options);
        Assert.NotSame(firstStore, secondStore);
        var financialBefore = await fixture.StateAsync();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Outcome> AttemptAsync(object store, CallerIdentity caller)
        {
            await start.Task;
            try { await api.InvokeAsync(store, caller, fixture.RequestId, CancellationToken.None); return Outcome.Admitted; }
            catch (InvalidOperationException) { return Outcome.Denied; }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.Aborted) { return Outcome.SdkAborted; }
        }
        var first = AttemptAsync(firstStore, fixture.Caller);
        var other = AttemptAsync(secondStore, second);
        start.SetResult(true);
        var results = await Task.WhenAll(first, other);

        Assert.Single(results, result => result == Outcome.Admitted);
        Assert.Single(results, result => result is Outcome.Denied or Outcome.SdkAborted);
        output.WriteLine("Count admission contention: admitted={0}, denied={1}, sdkAborted={2}",
            results.Count(value => value == Outcome.Admitted), results.Count(value => value == Outcome.Denied),
            results.Count(value => value == Outcome.SdkAborted));
        await fixture.AssertCounterAsync(fixture.ProjectCounter, 1);
        var documents = (await fixture.Preflight.GetSnapshotAsync()).Documents;
        Assert.Equal(2, documents.Count);
        Assert.Equal(1L, documents.Where(doc => doc.Id.StartsWith("user-", StringComparison.Ordinal)).Sum(doc => doc.GetValue<long>("count")));
        Assert.Equal(financialBefore, await fixture.StateAsync());
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Already_cancelled_admission_creates_no_permit_and_no_financial_change()
    {
        var api = AdmissionContract.Load();
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.StateAsync(includePreflight: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.AdmitAsync(
            fixture, fixture.Caller, fixture.RequestId, token: cancellation.Token));

        Assert.Equal(before, await fixture.StateAsync(includePreflight: true));
        Assert.Empty((await fixture.Preflight.GetSnapshotAsync()).Documents);
    }

    private static Dictionary<string, object> Counter(DateTimeOffset start, long count) => new()
    {
        ["windowStart"] = Ts(start), ["count"] = count,
        ["updatedAt"] = Ts(start.AddSeconds(5)), ["expiresAt"] = Ts(start.AddMinutes(3)),
    };

    private static Timestamp Ts(DateTimeOffset value) => Timestamp.FromDateTime(value.UtcDateTime);

    private static PaidSystemOptions OptionsFor(string project, int projectLimit = 1_000, int userRequests = 12,
        int concurrent = 1, long taskCap = TaskCap)
    {
        var property = typeof(PaidSystemOptions).GetProperty("TokenCountCallsPerMinute");
        Assert.NotNull(property); // Reflection assertions occur before all expected rejection assertions.
        Assert.Equal(typeof(int), property.PropertyType);
        Assert.Equal(1_000, property.GetValue(new PaidSystemOptions()));
        var options = new PaidSystemOptions
        {
            ProjectId = project, DefaultTaskCreditMicrosCap = taskCap,
            MaxRequestsPerMinutePerUser = userRequests, MaxConcurrentRequestsPerUser = concurrent,
        };
        property.SetValue(options, projectLimit);
        return options;
    }

    private static async Task AssertRejectedAsync(Func<Task> action)
    {
        var failure = await Record.ExceptionAsync(action);
        Assert.NotNull(failure);
        Assert.True(failure is ArgumentException or InvalidOperationException,
            $"Expected admission/configuration rejection, not infrastructure failure: {failure}");
    }

    private enum Outcome { Admitted, Denied, SdkAborted }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset Now { get; set; } = Window.AddSeconds(30);
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class CommitBoundaryClock : IClock
    {
        internal int Reads { get; private set; }
        public DateTimeOffset UtcNow => ++Reads == 1 ? Window.AddSeconds(59) : Window.AddMinutes(1);
    }

    private sealed class AdmissionContract(ConstructorInfo constructor, MethodInfo method)
    {
        internal static AdmissionContract Load()
        {
            var type = typeof(FirestoreAccountStore).Assembly.GetType(
                "KeyboardWtf.Staging.Api.Services.FirestoreTokenCountAdmission");
            Assert.NotNull(type);
            var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, [typeof(FirestoreDb), typeof(IClock), typeof(IOptions<PaidSystemOptions>)], modifiers: null);
            var method = type.GetMethod("AdmitAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, [typeof(CallerIdentity), typeof(string), typeof(CancellationToken)], modifiers: null);
            Assert.NotNull(constructor);
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method.ReturnType);
            return new AdmissionContract(constructor, method);
        }

        internal object Create(FirestoreDb db, IClock clock, PaidSystemOptions options)
        {
            try { return constructor.Invoke([db, clock, Options.Create(options)]); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        internal Task AdmitAsync(Fixture fixture, CallerIdentity caller, string requestId,
            PaidSystemOptions? options = null, CancellationToken token = default)
        {
            try { return InvokeAsync(Create(fixture.Db, fixture.Clock, options ?? fixture.Options), caller, requestId, token); }
            catch (Exception exception) { return Task.FromException(exception); }
        }

        internal Task InvokeAsync(object instance, CallerIdentity caller, string requestId, CancellationToken token)
        {
            try { return (Task)method.Invoke(instance, [caller, requestId, token])!; }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                return Task.FromException(exception.InnerException);
            }
        }
    }

    private sealed record DocumentState(string Path, bool Exists, Timestamp? Updated, string Data);

    private sealed class Fixture : IAsyncDisposable
    {
        private const string ProjectPrefix = "kwt-count-local-";
        private readonly List<CallerIdentity> _callers = [];
        internal FirestoreDb Db { get; }
        internal PaidSystemOptions Options { get; }
        internal MutableClock Clock { get; } = new();
        internal CallerIdentity Caller => _callers[0];
        internal string RequestId { get; } = "synthetic-count-" + Guid.NewGuid().ToString("N");
        internal DocumentReference Account => AccountFor(Caller);
        internal CollectionReference Preflight => Db.Collection("providerPreflight");
        internal DocumentReference ProjectCounter => Preflight.Document("count-tokens-project");
        internal DocumentReference UserCounter(CallerIdentity caller) => Preflight.Document("user-" + Crypto.Sha256(caller.UserId));
        internal DocumentReference CounterFor(string scope) => scope == "project" ? ProjectCounter : UserCounter(Caller);
        internal DocumentReference AccountFor(CallerIdentity caller) => Db.Collection("accounts").Document(caller.UserId);
        internal DocumentReference ReserveIdempotency(CallerIdentity caller, string requestId) =>
            Db.Collection("idempotency").Document(Crypto.Sha256($"usage-reserve:{caller.UserId}:{requestId}"));

        private Fixture(FirestoreDb db, PaidSystemOptions options) { Db = db; Options = options; }

        internal static async Task<Fixture> CreateAsync(int projectLimit = 1_000, int userRequests = 12)
        {
            var host = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
            Assert.False(string.IsNullOrWhiteSpace(host));
            var endpoint = new Uri("http://" + host);
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.Empty(endpoint.UserInfo);
            Assert.Equal("/", endpoint.AbsolutePath);
            var project = ProjectPrefix + Guid.NewGuid().ToString("N");
            var options = OptionsFor(project, projectLimit, userRequests);
            var db = new FirestoreDbBuilder { ProjectId = project, EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build();
            var fixture = new Fixture(db, options);
            await fixture.AddCallerAsync();
            // Financial sentinels make an accidental accounting/global-counter
            // path detectable even though admission should never consult them.
            await db.Collection("globalUsageShards").Document("synthetic-shard").SetAsync(new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = 123L, ["settledCreditMicros"] = 456L,
            });
            await db.Collection("idempotency").Document("synthetic-financial-sentinel").SetAsync(new Dictionary<string, object>
            {
                ["scope"] = "synthetic-ledger", ["createdAt"] = Ts(Window),
            });
            return fixture;
        }

        internal async Task<CallerIdentity> AddCallerAsync()
        {
            var uid = "synthetic-count-user-" + Guid.NewGuid().ToString("N");
            var caller = new CallerIdentity(uid, uid + "@example.invalid", "Synthetic count admission", "test");
            _callers.Add(caller);
            var account = AccountFor(caller);
            await account.SetAsync(new Dictionary<string, object>
            {
                ["uid"] = uid, ["email"] = caller.Email, ["displayName"] = caller.DisplayName,
                ["status"] = "active", ["planId"] = "none", ["subscriptionStatus"] = "none",
                ["availableCreditMicros"] = 1_000_000L, ["reservedCreditMicros"] = 0L,
                ["creditDebtMicros"] = 0L, ["cycleUsageCreditMicros"] = 10L,
                ["allowanceRulesVersion"] = "allowance-v1", ["allowanceBuckets"] = Buckets(1_000_000, 0),
                ["activeRequests"] = 0L, ["activeLiveRequests"] = 0L,
                ["rateWindowCount"] = 7L, ["rateWindowStartedAt"] = Ts(Window),
                ["createdAt"] = Ts(Window), ["updatedAt"] = Ts(Window),
            });
            await account.Collection("ledger").Document("synthetic-existing-event").SetAsync(new Dictionary<string, object>
            {
                ["type"] = "SYNTHETIC_SENTINEL", ["availableDeltaCreditMicros"] = 7L,
            });
            return caller;
        }

        internal static Dictionary<string, object> Buckets(long available, long expiry) => new()
        {
            ["synthetic-bucket"] = new Dictionary<string, object>
            {
                ["id"] = "synthetic-bucket", ["kind"] = "topup", ["sourceType"] = "synthetic",
                ["sourceId"] = "synthetic-credit", ["originalCreditMicros"] = available,
                ["availableCreditMicros"] = available, ["reservedCreditMicros"] = 0L,
                ["consumedCreditMicros"] = 0L, ["reversedCreditMicros"] = 0L,
                ["expiresAtUnix"] = expiry, ["createdAtUnix"] = Window.ToUnixTimeSeconds(),
                ["expired"] = false, ["rulesVersion"] = "allowance-v1",
            },
        };

        internal async Task AssertCounterAsync(DocumentReference reference, long count, DateTimeOffset? window = null)
        {
            var snapshot = await reference.GetSnapshotAsync();
            Assert.True(snapshot.Exists);
            var start = window ?? Window;
            Assert.Equal(Ts(start), snapshot.GetValue<Timestamp>("windowStart"));
            Assert.Equal(count, snapshot.GetValue<long>("count"));
            Assert.Equal(Ts(Clock.Now), snapshot.GetValue<Timestamp>("updatedAt"));
            Assert.Equal(Ts(start.AddMinutes(3)), snapshot.GetValue<Timestamp>("expiresAt"));
        }

        internal async Task<DocumentState[]> StateAsync(bool includePreflight = false)
        {
            var states = new Dictionary<string, DocumentState>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            async Task VisitDocumentAsync(DocumentReference reference)
            {
                if (!visited.Add(reference.Path)) return;
                var snapshot = await reference.GetSnapshotAsync();
                states[reference.Path] = new DocumentState(reference.Path, snapshot.Exists, snapshot.UpdateTime,
                    snapshot.Exists ? Canonical(snapshot.ToDictionary()) : "missing");
                await foreach (var child in reference.ListCollectionsAsync()) await VisitCollectionAsync(child);
            }
            async Task VisitCollectionAsync(CollectionReference collection)
            {
                foreach (var document in (await collection.GetSnapshotAsync()).Documents)
                    await VisitDocumentAsync(document.Reference);
            }
            // A deleted root account may still have subcollections. Follow the
            // known owned references explicitly so those records remain covered.
            foreach (var caller in _callers) await VisitDocumentAsync(AccountFor(caller));
            await foreach (var collection in Db.ListRootCollectionsAsync())
                if (includePreflight || collection.Id != "providerPreflight") await VisitCollectionAsync(collection);
            return states.Values.OrderBy(state => state.Path, StringComparer.Ordinal).ToArray();
        }

        private static string Canonical(object? value) => value switch
        {
            IDictionary<string, object> map => "{" + string.Join(",", map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
            Timestamp stamp => "timestamp:" + stamp.ToDateTime().ToString("O"),
            IEnumerable<object> items => "[" + string.Join(",", items.Select(Canonical)) + "]",
            _ => JsonSerializer.Serialize(value),
        };

        public async ValueTask DisposeAsync()
        {
            Assert.True(Db.ProjectId.StartsWith(ProjectPrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(Db.ProjectId[ProjectPrefix.Length..], "N", out _));
            var prefix = $"projects/{Db.ProjectId}/databases/{Db.DatabaseId}/documents/";
            foreach (var document in (await StateAsync(includePreflight: true)).OrderByDescending(state => state.Path.Length))
            {
                Assert.StartsWith(prefix, document.Path, StringComparison.Ordinal);
                await Db.Document(document.Path[prefix.Length..]).DeleteAsync();
            }
        }
    }
}
