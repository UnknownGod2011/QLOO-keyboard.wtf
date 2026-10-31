using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Actual original account refresh and its signed-in SignIn ingress, using only
// GUID-owned synthetic DPAPI and injected .test HTTP. No app/profile singleton,
// real account, network, browser, provider or billing mutation. These checks
// do not prove Settings response tails, installed OAuth or complete shutdown.
public sealed class PaidAccountRefreshUpdateWorkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string OwnerStatement = "        using var accountUpdateWork = AcquireAuthenticationUpdateWork();\n";
    private static readonly FieldInfo Tokens = typeof(PaidAccountService).GetField("_tokens", PrivateInstance)!;
    private static readonly FieldInfo LastError = typeof(PaidAccountService).GetField("_lastError", PrivateInstance)!;
    private static readonly FieldInfo Timer = typeof(PaidAccountService).GetField("_proactiveTokenRefreshTimer", PrivateInstance)!;

    [Fact]
    public void Account_refresh_has_only_the_exact_admission_after_existing_eligibility()
    {
        RequireContract();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_valid_token_direct_or_signed_in_SignIn_refuses_before_HTTP_or_state_effects(bool signIn)
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Snapshot();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Refresh(signIn: signIn));
            Assert.Contains("update", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(failure.InnerException);
            Assert.Empty(fixture.Handler.Requests);
            Assert.Equal(before, fixture.Snapshot());
            Assert.False(fixture.Account.SignInPending);
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("headers", false)]
    [InlineData("body", true)]
    public async Task Actual_account_headers_body_and_signed_in_ingress_survive_abandoned_waiter(
        string stage, bool signIn)
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue("/v1/account", stage, credit: 456);
        var original = fixture.Refresh(signIn: signIn);
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
        var result = await original.WaitAsync(TestTimeout);
        Assert.Same(result, fixture.Account.Account);
        fixture.AssertAccountSuccess(before, 456, tokensRefreshed: false);
        Assert.Single(fixture.Handler.Requests);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task Cancellation_ignoring_HTTP_child_retains_owner_until_actual_dependency_completion(string stage)
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue("/v1/account", stage, credit: 456);
        using var cancellation = new CancellationTokenSource();
        var original = fixture.Refresh(cancellation.Token);
        await hold.Entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Equal(before, fixture.Snapshot());
        hold.Release.TrySetResult(true);
        // Actual HttpClient may observe the token after the ignored child
        // returns. Neither cancellation nor a canceled waiter proves completion.
        var cancelled = false;
        try { await original.WaitAsync(TestTimeout); }
        catch (OperationCanceledException) when (original.IsCanceled) { cancelled = true; }
        await fixture.Handler.JoinChildrenAsync();
        Assert.True(original.IsCompleted);
        if (cancelled) Assert.Equal(before, fixture.Snapshot());
        else fixture.AssertAccountSuccess(before, 456, tokensRefreshed: false);
        Assert.Single(fixture.Handler.Requests);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Expired_access_refresh_child_cannot_leave_a_zero_owner_gap_before_account_GET_and_publication()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeAccessExpired();
        var before = fixture.Snapshot();
        var authentication = fixture.Handler.Enqueue("/v1/sessions/refresh", "headers");
        var account = fixture.Handler.Enqueue("/v1/account", "body", credit: 456);
        var original = fixture.Refresh();
        await authentication.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.Equal(before, fixture.Snapshot());
        authentication.Release.TrySetResult(true);
        await account.Entered.Task.WaitAsync(TestTimeout);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Equal("synthetic-refreshed-access", fixture.CurrentTokens.AccessToken);
        Assert.Equal(fixture.CurrentTokens, fixture.Store.Load());
        Assert.Same(before.Account, fixture.Account.Account);
        Assert.Equal(before.LastError, fixture.Account.LastError);
        Assert.Equal(new[] { "/v1/sessions/refresh", "/v1/account" }, fixture.Handler.Requests.Select(request => request.Path));
        account.Release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        fixture.StopTimer();
        fixture.AssertAccountSuccess(before, 456, tokensRefreshed: true);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Existing_401_refresh_and_single_account_retry_stay_owned_through_the_final_body()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Snapshot();
        fixture.Handler.Enqueue("/v1/account", "none", HttpStatusCode.Unauthorized);
        var authentication = fixture.Handler.Enqueue("/v1/sessions/refresh", "headers");
        var retry = fixture.Handler.Enqueue("/v1/account", "body", credit: 456);
        var original = fixture.Refresh();
        await authentication.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.False(original.IsCompleted);
        Assert.Equal(before, fixture.Snapshot());
        authentication.Release.TrySetResult(true);
        await retry.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.False(original.IsCompleted);
        Assert.Same(before.Account, fixture.Account.Account);
        var requests = fixture.Handler.Requests.ToArray();
        Assert.Equal(new[] { "/v1/account", "/v1/sessions/refresh", "/v1/account" }, requests.Select(request => request.Path));
        Assert.Equal("synthetic-access", requests[0].AccessToken);
        Assert.Null(requests[1].AccessToken);
        Assert.Equal("synthetic-refreshed-access", requests[2].AccessToken);
        retry.Release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        fixture.StopTimer();
        fixture.AssertAccountSuccess(before, 456, tokensRefreshed: true);
        Assert.Equal(3, fixture.Handler.Requests.Count);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Repeated_account_401_retains_original_bounded_retry_and_authoritative_invalidation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Enqueue("/v1/account", "none", HttpStatusCode.Unauthorized);
        fixture.Handler.Enqueue("/v1/sessions/refresh", "none");
        var retry = fixture.Handler.Enqueue("/v1/account", "body", HttpStatusCode.Unauthorized);
        var original = fixture.Refresh();
        await retry.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.True(fixture.Account.IsSignedIn);
        retry.Release.TrySetResult(true);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => original.WaitAsync(TestTimeout));
        Assert.Equal(HttpStatusCode.Unauthorized, Assert.IsType<PaidApiException>(failure.InnerException).StatusCode);
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.False(fixture.Account.IsSignedIn);
        Assert.Null(fixture.CurrentTokens);
        Assert.Null(fixture.Account.Account);
        Assert.Null(fixture.Store.Load());
        Assert.Contains("signed out or revoked", fixture.Account.LastError, StringComparison.Ordinal);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_network_or_JSON_failure_preserves_prior_account_and_next_explicit_refresh_succeeds(bool json)
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue("/v1/account", json ? "body" : "headers",
            networkFailure: !json, malformedJson: json);
        var original = fixture.Refresh();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        if (json) await Assert.ThrowsAsync<JsonException>(() => original.WaitAsync(TestTimeout));
        else await Assert.ThrowsAsync<HttpRequestException>(() => original.WaitAsync(TestTimeout));
        Assert.Equal(before, fixture.Snapshot());
        AssertIdle(fixture.Gate);
        fixture.Handler.Enqueue("/v1/account", "none", credit: 456);
        await fixture.Refresh().WaitAsync(TestTimeout);
        fixture.AssertAccountSuccess(before, 456, tokensRefreshed: false);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Null_gate_legacy_refresh_is_unaffected_by_an_unrelated_freeze()
    {
        await using var fixture = await Fixture.CreateAsync(useGate: false);
        var before = fixture.Snapshot();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var hold = fixture.Handler.Enqueue("/v1/account", "body", credit: 456);
            var original = fixture.Refresh();
            await hold.Entered.Task.WaitAsync(TestTimeout);
            Assert.False(original.IsCompleted);
            Assert.Equal(before, fixture.Snapshot());
            hold.Release.TrySetResult(true);
            await original.WaitAsync(TestTimeout);
            fixture.AssertAccountSuccess(before, 456, tokensRefreshed: false);
            Assert.Single(fixture.Handler.Requests);
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Independent_original_account_workers_remain_owned_until_both_complete()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.Handler.Enqueue("/v1/account", "headers", credit: 456);
        var second = fixture.Handler.Enqueue("/v1/account", "body", credit: 789);
        var firstOriginal = fixture.Refresh();
        await first.Entered.Task.WaitAsync(TestTimeout);
        var secondOriginal = fixture.Refresh();
        await second.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        second.Release.TrySetResult(true);
        await secondOriginal.WaitAsync(TestTimeout);
        Assert.Equal(789, fixture.Account.Account.AvailableCreditMicros);
        Assert.False(firstOriginal.IsCompleted);
        AssertBusy(fixture.Gate);
        first.Release.TrySetResult(true);
        await firstOriginal.WaitAsync(TestTimeout);
        Assert.Equal(456, fixture.Account.Account.AvailableCreditMicros); // Existing completion order is unchanged.
        Assert.Equal(2, fixture.Handler.Requests.Count);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Fifty_local_account_cycles_keep_original_work_owned_without_duplicate_HTTP_or_credential_writes()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var cycle = 0; cycle < 50; cycle++)
        {
            var before = fixture.Snapshot();
            var hold = fixture.Handler.Enqueue("/v1/account", cycle % 2 == 0 ? "headers" : "body", credit: 1000 + cycle);
            var original = fixture.Refresh();
            await hold.Entered.Task.WaitAsync(TestTimeout);
            AssertBusy(fixture.Gate);
            Assert.False(original.IsCompleted);
            Assert.Equal(before, fixture.Snapshot());
            hold.Release.TrySetResult(true);
            await original.WaitAsync(TestTimeout);
            fixture.AssertAccountSuccess(before, 1000 + cycle, tokensRefreshed: false);
            Assert.Equal(cycle + 1, fixture.Handler.Requests.Count);
            AssertIdle(fixture.Gate);
        }
    }

    private static void RequireContract()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Services", "PaidStaging", "PaidAccountService.cs"))
            .ReplaceLineEndings("\n");
        var start = source.IndexOf("    public async Task<PaidAccountSnapshot> RefreshAccountAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("    public async Task WarmBackendAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var slice = source[start..end];
        var admitted = slice.IndexOf(OwnerStatement, StringComparison.Ordinal);
        Assert.True(admitted >= 0, "The exact passive account-refresh admission statement is missing.");
        Assert.Equal(-1, slice.IndexOf(OwnerStatement, admitted + OwnerStatement.Length, StringComparison.Ordinal));
        Assert.Contains("        EnsureEnabled();\n" + OwnerStatement, slice, StringComparison.Ordinal);
        var original = slice.Remove(admitted, OwnerStatement.Length);
        Assert.Equal("18268374653903C187F7065195D107B0B6568759F262AF400229CF947EA45FA4",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))));
        // Exact-source proof deliberately permits only this one statement.
        // Runtime tests below exercise the actual compiled account method.
    }

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "src", "Services", "PaidStaging", "PaidAccountService.cs")))
                return current.FullName;
        throw new InvalidOperationException("The account-refresh source checkout was not found.");
    }

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryFreeze(out var freeze);
        freeze?.Dispose();
        Assert.False(accepted, "Original account refresh must remain owned through its actual effects.");
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
        private readonly List<Task> _originals = [];
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal SyntheticHandler Handler { get; } = new();
        internal PaidSessionStore Store { get; }
        internal PaidAccountService Account { get; }
        internal string SessionPath { get; }
        internal PaidSessionTokens CurrentTokens => (PaidSessionTokens)Tokens.GetValue(Account)!;

        private Fixture(bool useGate)
        {
            Assert.True(OperatingSystem.IsWindows(), "Synthetic DPAPI lifetime fixtures require Windows.");
            _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-account-refresh-tests", Guid.NewGuid().ToString("N"));
            SessionPath = Path.Combine(_directory, "session.dat");
            Store = new PaidSessionStore(SessionPath);
            Store.Save(new PaidSessionTokens("synthetic-access", DateTimeOffset.UtcNow.AddHours(2),
                "synthetic-refresh", DateTimeOffset.UtcNow.AddDays(1), "synthetic-session", "synthetic-device", "synthetic-installation"));
            var configuration = new PaidStagingConfiguration(true, false,
                new Uri("https://paid-api.example.test/"), new Uri("https://paid.example.test/"));
            Account = new PaidAccountService(configuration, Store, Handler,
                _ => throw new InvalidOperationException("Synthetic account refresh must never launch a browser."),
                useGate ? Gate : null);
        }

        internal static async Task<Fixture> CreateAsync(bool useGate = true)
        {
            // Honest missing-statement RED before GUID/DPAPI/account/held work,
            // even for the null-gate control that could otherwise pass.
            RequireContract();
            var fixture = new Fixture(useGate);
            try
            {
                await fixture.Track(fixture.Account.InitializeAsync()).WaitAsync(TestTimeout);
                fixture.StopTimer();
                Assert.NotNull(fixture.Account.Account);
                fixture.Handler.Requests.Clear();
                LastError.SetValue(fixture.Account, "Synthetic prior notice");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private T Track<T>(T original) where T : Task { _originals.Add(original); return original; }

        internal Task<PaidAccountSnapshot> Refresh(CancellationToken token = default, bool signIn = false) =>
            Track(signIn ? Account.SignInAsync(token) : Account.RefreshAccountAsync(token));

        internal void MakeAccessExpired() => Tokens.SetValue(Account,
            CurrentTokens with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-30) });

        internal State Snapshot() => new(CurrentTokens, Account.Account, Account.LastError,
            File.Exists(SessionPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SessionPath))) : null,
            Account.SignInState);

        internal void AssertAccountSuccess(State before, long credit, bool tokensRefreshed)
        {
            Assert.Equal(credit, Account.Account.AvailableCreditMicros);
            Assert.Equal(before.Account.UserId, Account.Account.UserId);
            Assert.Equal("", Account.LastError);
            Assert.Equal(before.SignInState, Account.SignInState);
            Assert.True(Account.IsSignedIn);
            if (tokensRefreshed)
            {
                Assert.Equal("synthetic-refreshed-access", CurrentTokens.AccessToken);
                Assert.Equal(before.Tokens.DeviceInstallationId, CurrentTokens.DeviceInstallationId);
                Assert.Equal(CurrentTokens, Store.Load());
            }
            else
            {
                Assert.Equal(before.Tokens, CurrentTokens);
                Assert.Equal(before.SessionHash, Snapshot().SessionHash);
                Assert.Equal(CurrentTokens, Store.Load());
            }
        }

        internal void StopTimer() => ((System.Threading.Timer)Timer.GetValue(Account)!)
            .Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public async ValueTask DisposeAsync()
        {
            StopTimer();
            Handler.ReleaseAll();
            var originals = Task.WhenAll(_originals);
            try { await originals.WaitAsync(TestTimeout); }
            catch when (originals.IsCompleted) { /* Original asserted failure/cancellation only. */ }
            await Handler.JoinChildrenAsync();
            StopTimer(); // Original authentication child may schedule the existing timer.
            Account.Dispose();
            if (!Directory.Exists(_directory)) return;
            var resolved = Path.GetFullPath(_directory);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "keyboard-wtf-account-refresh-tests"));
            Assert.Equal(parent, Path.GetDirectoryName(resolved));
            Assert.Matches("\\A[a-f0-9]{32}\\z", Path.GetFileName(resolved));
            Assert.False((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
            Assert.False((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0);
            var allowedNames = new[] { "session.dat", "session.dat.tmp" };
            foreach (var path in Directory.GetFileSystemEntries(resolved, "*", SearchOption.TopDirectoryOnly))
            {
                Assert.False((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0);
                Assert.Contains(Path.GetFileName(path), allowedNames);
                Assert.Equal(resolved, Path.GetDirectoryName(Path.GetFullPath(path)));
            }
            // An incomplete original/child join escapes BEFORE disposal/delete;
            // unknown evidence is retained. Never recurse through unexpected data.
            foreach (var name in allowedNames) File.Delete(Path.Combine(resolved, name));
            Directory.Delete(resolved, recursive: false);
        }
    }

    private sealed record Request(string Path, string AccessToken);
    private sealed class Hold(string stage, HttpStatusCode status, long credit, bool networkFailure, bool malformedJson)
    {
        internal string Stage { get; } = stage;
        internal HttpStatusCode Status { get; } = status;
        internal long Credit { get; } = credit;
        internal bool NetworkFailure { get; } = networkFailure;
        internal bool MalformedJson { get; } = malformedJson;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SyntheticHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<Hold>> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<Hold> _all = new();
        private readonly ConcurrentBag<Task> _children = new();
        internal ConcurrentQueue<Request> Requests { get; } = new();

        internal Hold Enqueue(string path, string stage, HttpStatusCode status = HttpStatusCode.OK,
            long credit = 123, bool networkFailure = false, bool malformedJson = false)
        {
            var hold = new Hold(stage, status, credit, networkFailure, malformedJson);
            _pending.GetOrAdd(path, _ => new()).Enqueue(hold);
            _all.Enqueue(hold);
            return hold;
        }

        internal void ReleaseAll() { foreach (var hold in _all) hold.Release.TrySetResult(true); }

        internal async Task JoinChildrenAsync()
        {
            // Parent originals are already joined: no new dependency can be
            // registered behind this actual child-task snapshot.
            var originals = Task.WhenAll(_children);
            try { await originals.WaitAsync(TestTimeout); }
            catch when (originals.IsCompleted) { /* Synthetic fault propagated to its original worker. */ }
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
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Contains(request.Headers.Authorization?.Parameter, new[] { "synthetic-access", "synthetic-refreshed-access" });
            }
            else
            {
                Assert.Equal("/v1/sessions/refresh", path);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Null(request.Headers.Authorization);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("synthetic-refresh", body.RootElement.GetProperty("refreshToken").GetString());
            }
            Requests.Enqueue(new Request(path, request.Headers.Authorization?.Parameter));
            Hold hold = null;
            if (_pending.TryGetValue(path, out var pending)) pending.TryDequeue(out hold);
            if (hold?.Stage == "headers")
            {
                hold.Entered.TrySetResult(true);
                await hold.Release.Task; // Actual handler child intentionally ignores cancellation.
            }
            if (hold?.NetworkFailure == true) throw new HttpRequestException("Synthetic account network failure");
            var status = hold?.Status ?? HttpStatusCode.OK;
            string text;
            if (hold?.MalformedJson == true) text = "{ malformed synthetic JSON";
            else if (status != HttpStatusCode.OK) text = "{\"title\":\"Synthetic account refusal\"}";
            else if (path == "/v1/sessions/refresh") text = JsonSerializer.Serialize(new
            {
                accessToken = "synthetic-refreshed-access", accessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
                refreshToken = "synthetic-refresh", refreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                sessionId = "synthetic-session", deviceId = "synthetic-device",
            });
            else text = JsonSerializer.Serialize(new
            {
                userId = "synthetic-user", email = "person@example.test", displayName = "Synthetic User", status = "active",
                planId = "none", availableCreditMicros = hold?.Credit ?? 123, reservedCreditMicros = 0, cycleUsageCreditMicros = 0,
                cycleEndsAt = (DateTimeOffset?)null, devices = Array.Empty<object>(), recentUsage = Array.Empty<object>(),
                recentPurchases = Array.Empty<object>(), subscriptionStatus = "none",
            });
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
            await hold.Release.Task; // Original body child, not a cancelable waiter proxy.
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
