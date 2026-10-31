using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Actual existing refresh/Personal authorization, isolated synthetic DPAPI and
// injected .test HTTP only. No app, real account, browser, network or provider.
// This is operation-lifetime proof, not a claim of complete shutdown coverage.
public sealed class PaidOnDemandRefreshUpdateWorkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string AcquireName = "AcquireAuthenticationUpdateWork";
    private static readonly FieldInfo Tokens = typeof(PaidAccountService).GetField("_tokens", PrivateInstance)!;
    private static readonly FieldInfo AuthGate = typeof(PaidAccountService).GetField("_authGate", PrivateInstance)!;
    private static readonly FieldInfo Timer = typeof(PaidAccountService).GetField("_proactiveTokenRefreshTimer", PrivateInstance)!;
    private static readonly FieldInfo ProactiveRunning = typeof(PaidAccountService).GetField("_proactiveTokenRefreshRunning", PrivateInstance)!;

    [Fact]
    public void Authentication_acquisition_is_first_before_semaphore_and_existing_provider_catch()
    {
        RequireContract();
    }

    [Fact]
    public async Task Frozen_direct_refresh_refuses_before_HTTP_semaphore_or_session_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Refresh());
            Assert.Contains("update", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, fixture.Snapshot());
            Assert.Empty(fixture.Handler.Requests);
            Assert.Equal(1, fixture.AuthenticationCount);
            Assert.False(fixture.Gate.TryFreeze(out _));
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Frozen_expired_access_Personal_authorization_has_no_authentication_or_state_effects()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AuthorizePersonal());
            Assert.Contains("update", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(failure.InnerException);
            Assert.Equal(before, fixture.Snapshot());
            Assert.Empty(fixture.Handler.Requests);
            Assert.Equal(1, fixture.AuthenticationCount);
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Valid_access_Personal_fast_path_remains_inert_and_allowed_without_refresh()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Snapshot();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            await fixture.AuthorizePersonal().WaitAsync(TestTimeout);
            Assert.Empty(fixture.Handler.Requests);
            Assert.Equal(before, fixture.Snapshot());
            Assert.Equal(1, fixture.AuthenticationCount);
        }
        finally { frozen.Dispose(); }
    }

    [Fact]
    public async Task Original_refresh_waiting_for_authentication_is_owned_until_cancel_unwinds()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        await fixture.HoldAuthenticationAsync();
        using var cancellation = new CancellationTokenSource();
        var original = fixture.Refresh(cancellation.Token);
        Assert.False(original.IsCompleted);
        Assert.Empty(fixture.Handler.Requests);
        AssertBusy(fixture.Gate);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.WaitAsync(TestTimeout));
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(0, fixture.AuthenticationCount); // Our hold remains; waiter did not release it.
        AssertIdle(fixture.Gate);
        fixture.ReleaseAuthentication();
        Assert.Equal(1, fixture.AuthenticationCount);
    }

    [Theory]
    [InlineData("headers", false)]
    [InlineData("body", true)]
    public async Task Actual_headers_body_and_Personal_route_remain_owned_when_caller_abandons_wait(
        string stage, bool personal)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue(stage);
        var original = personal ? fixture.AuthorizePersonal() : fixture.Refresh();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Equal(before, fixture.Snapshot());
        using var stoppedWaiting = new CancellationTokenSource();
        var waiter = original.WaitAsync(stoppedWaiting.Token);
        stoppedWaiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        fixture.StopTimer();
        Assert.Single(fixture.Handler.Requests);
        fixture.AssertSavedSuccess(before);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("headers", false)]
    [InlineData("body", true)]
    public async Task Cancellation_ignoring_HTTP_child_is_not_completed_by_caller_cancellation(
        string stage, bool personal)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue(stage);
        using var cancellation = new CancellationTokenSource();
        var original = personal ? fixture.AuthorizePersonal(cancellation.Token) : fixture.Refresh(cancellation.Token);
        await hold.Entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Equal(before, fixture.Snapshot());
        hold.Release.TrySetResult(true);
        // HttpClient may observe cancellation after the ignored dependency
        // returns. Test the actual outcome, not an invented successful refresh.
        var cancelled = false;
        try { await original.WaitAsync(TestTimeout); }
        catch (OperationCanceledException) when (original.IsCanceled) { cancelled = true; }
        Assert.True(original.IsCompleted);
        await fixture.Handler.JoinChildrenAsync();
        fixture.StopTimer();
        Assert.Single(fixture.Handler.Requests);
        if (cancelled) Assert.Equal(before, fixture.Snapshot());
        else fixture.AssertSavedSuccess(before);
        Assert.Equal(1, fixture.AuthenticationCount);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authoritative_refresh_rejection_retains_existing_session_invalidation(HttpStatusCode status)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var hold = fixture.Handler.Enqueue("body", status);
        var original = fixture.Refresh();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.True(fixture.Account.IsSignedIn);
        hold.Release.TrySetResult(true);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => original.WaitAsync(TestTimeout));
        Assert.Equal(status, Assert.IsType<PaidApiException>(failure.InnerException).StatusCode);
        Assert.False(fixture.Account.IsSignedIn);
        Assert.Null(fixture.Account.Account);
        Assert.Null(fixture.Store.Load());
        Assert.Contains("expired or was revoked", fixture.Account.LastError, StringComparison.Ordinal);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(1, fixture.AuthenticationCount);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Network_failure_preserves_session_and_next_explicit_refresh_can_succeed()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue("headers", networkFailure: true);
        var original = fixture.Refresh();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await Assert.ThrowsAsync<HttpRequestException>(() => original.WaitAsync(TestTimeout));
        Assert.Equal(before, fixture.Snapshot());
        AssertIdle(fixture.Gate);
        await fixture.Refresh().WaitAsync(TestTimeout);
        fixture.StopTimer();
        Assert.Equal(2, fixture.Handler.Requests.Count);
        fixture.AssertSavedSuccess(before);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Malformed_JSON_preserves_session_and_next_explicit_refresh_can_succeed()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue("body", malformedJson: true);
        var original = fixture.Refresh();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await Assert.ThrowsAsync<JsonException>(() => original.WaitAsync(TestTimeout));
        Assert.Equal(before, fixture.Snapshot());
        AssertIdle(fixture.Gate);
        await fixture.Refresh().WaitAsync(TestTimeout);
        fixture.StopTimer();
        Assert.Equal(2, fixture.Handler.Requests.Count);
        fixture.AssertSavedSuccess(before);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Missing_or_expired_refresh_session_has_original_truthful_failure_and_no_HTTP()
    {
        foreach (var missing in new[] { true, false })
        {
            await using var fixture = await Fixture.CreateAsync();
            Tokens.SetValue(fixture.Account, missing ? null : fixture.CurrentTokens with
            {
                RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Refresh().WaitAsync(TestTimeout));
            Assert.Contains("session expired", failure.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Handler.Requests);
            Assert.Null(fixture.CurrentTokens);
            Assert.Null(fixture.Account.Account);
            Assert.Null(fixture.Store.Load());
            Assert.False(fixture.Account.IsSignedIn);
            Assert.Equal(1, fixture.AuthenticationCount);
            AssertIdle(fixture.Gate);
        }
    }

    [Fact]
    public async Task Null_gate_legacy_refresh_is_unchanged_by_an_unrelated_frozen_gate()
    {
        await using var fixture = await Fixture.CreateAsync(useGate: false);
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        Assert.True(fixture.Gate.TryFreeze(out var unrelatedFreeze));
        try
        {
            var hold = fixture.Handler.Enqueue("body");
            var original = fixture.Refresh();
            await hold.Entered.Task.WaitAsync(TestTimeout);
            Assert.False(original.IsCompleted);
            Assert.Equal(before, fixture.Snapshot());
            hold.Release.TrySetResult(true);
            await original.WaitAsync(TestTimeout);
            fixture.StopTimer();
            fixture.AssertSavedSuccess(before);
            Assert.Single(fixture.Handler.Requests);
        }
        finally { unrelatedFreeze.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Proactive_and_on_demand_originals_keep_same_gate_owned_across_authentication_children()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        var first = fixture.Handler.Enqueue("headers");
        var second = fixture.Handler.Enqueue("body");
        var onDemand = fixture.Refresh(force: false);
        await first.Entered.Task.WaitAsync(TestTimeout);
        var proactive = fixture.Proactive();
        Assert.False(proactive.IsCompleted);
        Assert.Equal(1, fixture.ProactiveCount);
        AssertBusy(fixture.Gate);
        Assert.Single(fixture.Handler.Requests);
        first.Release.TrySetResult(true);
        await onDemand.WaitAsync(TestTimeout);
        await second.Entered.Task.WaitAsync(TestTimeout);
        Assert.False(proactive.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Equal(2, fixture.Handler.Requests.Count); // Existing proactive force-refresh policy unchanged.
        second.Release.TrySetResult(true);
        await proactive.WaitAsync(TestTimeout);
        fixture.StopTimer();
        Assert.Equal(0, fixture.ProactiveCount);
        fixture.AssertSavedSuccess(before);
        Assert.Equal(1, fixture.AuthenticationCount);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Fifty_original_refresh_and_waiting_fast_path_cycles_have_no_owner_leak_or_duplicate_HTTP()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var cycle = 0; cycle < 50; cycle++)
        {
            fixture.MakeAccessExpired();
            var before = fixture.Snapshot();
            var hold = fixture.Handler.Enqueue(cycle % 2 == 0 ? "headers" : "body");
            var original = fixture.Refresh();
            await hold.Entered.Task.WaitAsync(TestTimeout);
            var waiting = fixture.Refresh(force: false);
            Assert.False(original.IsCompleted);
            Assert.False(waiting.IsCompleted);
            AssertBusy(fixture.Gate);
            Assert.Equal(cycle + 1, fixture.Handler.Requests.Count);
            hold.Release.TrySetResult(true);
            await Task.WhenAll(original, waiting).WaitAsync(TestTimeout);
            fixture.StopTimer();
            fixture.AssertSavedSuccess(before);
            Assert.Equal("synthetic-refreshed-access", await waiting);
            Assert.Equal(cycle + 1, fixture.Handler.Requests.Count);
            Assert.Equal(1, fixture.AuthenticationCount);
            AssertIdle(fixture.Gate);
        }
    }

    private static MethodInfo RequireContract()
    {
        var acquire = typeof(PaidAccountService).GetMethod(AcquireName, PrivateInstance, Type.EmptyTypes);
        Assert.NotNull(acquire);
        Assert.Equal(typeof(IDisposable), acquire.ReturnType);
        var refresh = RequiredMethod("RefreshTokensAsync");
        var state = refresh.GetCustomAttribute<AsyncStateMachineAttribute>();
        Assert.NotNull(state);
        var moveNext = state.StateMachineType.GetMethod("MoveNext", PrivateInstance);
        Assert.NotNull(moveNext);
        var calls = Calls(moveNext);
        var admission = Assert.Single(calls, call => call.Method.DeclaringType == typeof(PaidAccountService)
            && call.Method.Name == AcquireName);
        Assert.Equal(admission, calls.First());
        var wait = Assert.Single(calls, call => call.Method.DeclaringType == typeof(SemaphoreSlim)
            && call.Method.Name == nameof(SemaphoreSlim.WaitAsync));
        Assert.True(admission.Offset < wait.Offset);
        // Roslyn's outer async SetException catch necessarily surrounds all
        // user statements. Admission must be outside the existing provider
        // filter, so refusal cannot invalidate credentials as a provider error.
        var providerCatches = moveNext.GetMethodBody()!.ExceptionHandlingClauses
            .Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Filter).ToArray();
        Assert.NotEmpty(providerCatches);
        Assert.All(providerCatches, clause => Assert.True(admission.Offset < clause.TryOffset));
        return refresh;
    }

    private static MethodInfo RequiredMethod(string name)
    {
        var method = typeof(PaidAccountService).GetMethod(name, PrivateInstance);
        Assert.NotNull(method);
        return method;
    }

    private static object Invoke(PaidAccountService account, MethodInfo method, object[] arguments)
    {
        try { return method.Invoke(account, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryFreeze(out var freeze);
        freeze?.Dispose();
        Assert.False(accepted, "Original authentication work must prevent update freeze until it unwinds.");
    }

    private static void AssertIdle(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var freeze));
        freeze.Dispose();
    }

    private sealed record State(PaidSessionTokens Tokens, PaidAccountSnapshot Account, string LastError,
        string SessionHash, string SignInState);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly MethodInfo _refresh;
        private readonly List<Task> _workers = [];
        private bool _authenticationHeld;
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal SyntheticHandler Handler { get; } = new();
        internal PaidSessionStore Store { get; }
        internal PaidAccountService Account { get; }
        internal string SessionPath { get; }
        internal PaidSessionTokens CurrentTokens => (PaidSessionTokens)Tokens.GetValue(Account)!;
        internal int AuthenticationCount => ((SemaphoreSlim)AuthGate.GetValue(Account)!).CurrentCount;
        internal int ProactiveCount => (int)ProactiveRunning.GetValue(Account)!;

        private Fixture(MethodInfo refresh, bool useGate)
        {
            Assert.True(OperatingSystem.IsWindows(), "Synthetic DPAPI lifetime fixtures require Windows.");
            _refresh = refresh;
            _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-on-demand-work-tests", Guid.NewGuid().ToString("N"));
            SessionPath = Path.Combine(_directory, "session.dat");
            Store = new PaidSessionStore(SessionPath);
            Store.Save(new PaidSessionTokens("synthetic-access", DateTimeOffset.UtcNow.AddHours(2),
                "synthetic-refresh", DateTimeOffset.UtcNow.AddDays(1),
                "synthetic-session", "synthetic-device", "synthetic-installation"));
            var configuration = new PaidStagingConfiguration(true, false,
                new Uri("https://paid-api.example.test/"), new Uri("https://paid.example.test/"));
            Account = new PaidAccountService(configuration, Store, Handler,
                _ => throw new InvalidOperationException("Synthetic refresh must never launch a browser."),
                useGate ? Gate : null);
        }

        internal static async Task<Fixture> CreateAsync(bool useGate = true)
        {
            // Honest RED before GUID files, DPAPI, account creation or held work,
            // including legacy/valid-token controls that would otherwise pass.
            var refresh = RequireContract();
            var fixture = new Fixture(refresh, useGate);
            try
            {
                var initialization = fixture.Track(fixture.Account.InitializeAsync());
                await initialization.WaitAsync(TestTimeout);
                fixture.StopTimer();
                fixture.Handler.Requests.Clear();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private T Track<T>(T task) where T : Task { _workers.Add(task); return task; }

        internal Task<string> Refresh(CancellationToken token = default, bool force = true) =>
            Track((Task<string>)Invoke(Account, _refresh, [token, force]));

        internal Task AuthorizePersonal(CancellationToken token = default) =>
            Track(Account.AuthorizePersonalAsync(token));

        internal Task Proactive() => Track((Task)Invoke(Account,
            RequiredMethod("RefreshAccessTokenProactivelyAsync"), []));

        internal void MakeAccessExpired() => Tokens.SetValue(Account,
            CurrentTokens with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-30) });

        internal State Snapshot() => new(CurrentTokens, Account.Account, Account.LastError,
            File.Exists(SessionPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SessionPath))) : null,
            Account.SignInState);

        internal void AssertSavedSuccess(State before)
        {
            Assert.Equal("synthetic-refreshed-access", CurrentTokens.AccessToken);
            Assert.Equal("synthetic-refresh", CurrentTokens.RefreshToken);
            Assert.Equal(before.Tokens.DeviceInstallationId, CurrentTokens.DeviceInstallationId);
            Assert.Equal(CurrentTokens, Store.Load());
            Assert.Equal(before.Account, Account.Account);
            Assert.Equal(before.LastError, Account.LastError);
            Assert.Equal(before.SignInState, Account.SignInState);
            Assert.True(Account.IsSignedIn);
        }

        internal async Task HoldAuthenticationAsync()
        {
            Assert.True(await ((SemaphoreSlim)AuthGate.GetValue(Account)!).WaitAsync(TestTimeout));
            _authenticationHeld = true;
        }

        internal void ReleaseAuthentication()
        {
            if (!_authenticationHeld) return;
            ((SemaphoreSlim)AuthGate.GetValue(Account)!).Release();
            _authenticationHeld = false;
        }

        internal void StopTimer() => ((System.Threading.Timer)Timer.GetValue(Account)!)
            .Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public async ValueTask DisposeAsync()
        {
            StopTimer();
            Handler.ReleaseAll();
            ReleaseAuthentication();
            var originals = Task.WhenAll(_workers);
            try { await originals.WaitAsync(TestTimeout); }
            catch when (originals.IsCompleted) { /* Asserted original faults/cancellation only. */ }
            // Original worker termination alone is not a shortcut around a
            // cancellation-ignoring handler/content child. Join those too.
            await Handler.JoinChildrenAsync();
            StopTimer(); // Completed refresh may have scheduled the existing timer.
            Account.Dispose();
            if (!Directory.Exists(_directory)) return;
            var resolved = Path.GetFullPath(_directory);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "keyboard-wtf-on-demand-work-tests"));
            Assert.Equal(parent, Path.GetDirectoryName(resolved));
            Assert.Matches("\\A[a-f0-9]{32}\\z", Path.GetFileName(resolved));
            Assert.False((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
            Assert.False((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0);
            var allowedNames = new[] { "session.dat", "session.dat.tmp" };
            var inventory = Directory.GetFileSystemEntries(resolved, "*", SearchOption.TopDirectoryOnly);
            foreach (var path in inventory)
            {
                var attributes = File.GetAttributes(path);
                Assert.False((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0);
                Assert.Contains(Path.GetFileName(path), allowedNames);
                Assert.Equal(resolved, Path.GetDirectoryName(Path.GetFullPath(path)));
            }
            // Any unfinished join timeout escapes BEFORE disposal/deletion;
            // uncertain evidence remains rather than deleting under work.
            // Inventory validation is complete before deleting only these
            // explicit leaves; no recursive traversal or deletion follows links.
            foreach (var name in allowedNames) File.Delete(Path.Combine(resolved, name));
            Directory.Delete(resolved, recursive: false);
        }
    }

    private sealed class Hold(string stage, HttpStatusCode status, bool networkFailure, bool malformedJson)
    {
        internal string Stage { get; } = stage;
        internal HttpStatusCode Status { get; } = status;
        internal bool NetworkFailure { get; } = networkFailure;
        internal bool MalformedJson { get; } = malformedJson;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SyntheticHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Hold> _pending = new();
        private readonly ConcurrentQueue<Hold> _all = new();
        private readonly ConcurrentBag<Task> _children = new();
        internal ConcurrentQueue<string> Requests { get; } = new();

        internal Hold Enqueue(string stage, HttpStatusCode status = HttpStatusCode.OK,
            bool networkFailure = false, bool malformedJson = false)
        {
            var hold = new Hold(stage, status, networkFailure, malformedJson);
            _pending.Enqueue(hold);
            _all.Enqueue(hold);
            return hold;
        }

        internal void ReleaseAll() { foreach (var hold in _all) hold.Release.TrySetResult(true); }

        internal async Task JoinChildrenAsync()
        {
            // Called only after original account workers have joined, so no
            // new dependency can register behind this actual-child snapshot.
            var originals = Task.WhenAll(_children);
            try { await originals.WaitAsync(TestTimeout); }
            catch when (originals.IsCompleted) { /* Synthetic failures observed by the parent worker. */ }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var original = SendCoreAsync(request, token);
            _children.Add(original);
            return original;
        }

        private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("paid-api.example.test", request.RequestUri!.Host);
            Assert.True(request.Headers.Contains("X-Keyboard-Wtf-Desktop-Version"));
            var path = request.RequestUri.AbsolutePath;
            if (path == "/v1/account")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("synthetic-access", request.Headers.Authorization?.Parameter);
                Requests.Enqueue(path);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        userId = "synthetic-user", email = "person@example.test", displayName = "Synthetic User",
                        status = "active", planId = "none", availableCreditMicros = 123,
                        reservedCreditMicros = 0, cycleUsageCreditMicros = 0, cycleEndsAt = (DateTimeOffset?)null,
                        devices = Array.Empty<object>(), recentUsage = Array.Empty<object>(), recentPurchases = Array.Empty<object>(),
                        subscriptionStatus = "none",
                    }), Encoding.UTF8, "application/json"),
                };
            }
            Assert.Equal("/v1/sessions/refresh", path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("synthetic-refresh", body.RootElement.GetProperty("refreshToken").GetString());
            Requests.Enqueue(path);
            _pending.TryDequeue(out var hold);
            if (hold?.Stage == "headers")
            {
                hold.Entered.TrySetResult(true);
                await hold.Release.Task; // Intentionally cancellation-ignoring actual handler task.
            }
            if (hold?.NetworkFailure == true) throw new HttpRequestException("Synthetic refresh network failure");
            var status = hold?.Status ?? HttpStatusCode.OK;
            var text = hold?.MalformedJson == true ? "{ malformed synthetic JSON" : status == HttpStatusCode.OK
                ? JsonSerializer.Serialize(new
                {
                    accessToken = "synthetic-refreshed-access", accessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
                    refreshToken = "synthetic-refresh", refreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                    sessionId = "synthetic-session", deviceId = "synthetic-device",
                }) : "{\"title\":\"Synthetic refresh refusal\"}";
            return new HttpResponseMessage(status)
            {
                Content = hold?.Stage == "body" ? new HeldContent(text, hold, _children)
                    : new StringContent(text, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class HeldContent(string text, Hold hold, ConcurrentBag<Task> children) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            var original = SerializeCoreAsync(stream);
            children.Add(original);
            return original;
        }

        private async Task SerializeCoreAsync(Stream stream)
        {
            hold.Entered.TrySetResult(true);
            await hold.Release.Task; // Real body task ignores cancellation until released.
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed record Call(int Offset, MethodBase Method);
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null))
        .GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    private static IReadOnlyList<Call> Calls(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var calls = new List<Call>();
        for (var offset = 0; offset < il.Length;)
        {
            var start = offset;
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(OpCodesByValue.TryGetValue(value, out var code), "Unknown IL opcode in authentication wiring check.");
            if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                calls.Add(new Call(start, method.Module.ResolveMethod(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method is MethodInfo info ? info.GetGenericArguments() : Type.EmptyTypes)!));
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
                    or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok
                    or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(il, offset)),
                _ => throw new InvalidOperationException("Unsupported IL operand in authentication wiring check."),
            };
        }
        return calls;
    }
}
