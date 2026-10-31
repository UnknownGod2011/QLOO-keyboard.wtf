using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Existing proactive worker only: synthetic DPAPI data in a GUID-owned directory
// and injected .test HTTP. No app/Settings singleton, browser or real account.
public sealed class PaidProactiveRefreshUpdateWorkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Worker = typeof(PaidAccountService).GetMethod(
        "RefreshAccessTokenProactivelyAsync", PrivateInstance)!;
    private static readonly FieldInfo Tokens = typeof(PaidAccountService).GetField("_tokens", PrivateInstance)!;
    private static readonly FieldInfo Running = typeof(PaidAccountService).GetField("_proactiveTokenRefreshRunning", PrivateInstance)!;
    private static readonly FieldInfo AuthGate = typeof(PaidAccountService).GetField("_authGate", PrivateInstance)!;
    private static readonly FieldInfo Timer = typeof(PaidAccountService).GetField("_proactiveTokenRefreshTimer", PrivateInstance)!;

    [Fact]
    public void Same_gate_constructor_seam_is_optional_and_preserves_existing_parameters()
    {
        var constructor = RequireGateConstructor();
        Assert.Equal(new[] { typeof(PaidStagingConfiguration), typeof(PaidSessionStore),
            typeof(HttpMessageHandler), typeof(Action<string>), typeof(UpdateWorkAdmissionGate) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.All(constructor.GetParameters(), parameter => Assert.True(parameter.IsOptional));
        Assert.Null(constructor.GetParameters()[4].DefaultValue);
    }

    [Fact]
    public async Task Frozen_callback_is_completed_without_authentication_or_local_state_effects()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        var before = fixture.Snapshot();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var work = fixture.Start();
            await work.WaitAsync(TestTimeout);
            Assert.True(work.IsCompletedSuccessfully);
            Assert.Empty(fixture.Handler.Requests);
            Assert.Equal(before, fixture.Snapshot());
            Assert.Equal(0, fixture.RunningCount);
            Assert.False(fixture.Gate.TryFreeze(out _));
        }
        finally { frozen.Dispose(); }

        // Thaw alone does not replay a consumed one-shot tick. Invoke explicitly.
        Assert.Empty(fixture.Handler.Requests);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal("synthetic-refreshed-access", fixture.CurrentTokens.AccessToken);
        Assert.Equal(fixture.CurrentTokens, fixture.Store.Load());
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Original_worker_remains_owned_while_waiting_for_existing_authentication_semaphore()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        await fixture.HoldAuthenticationAsync();
        var work = fixture.Start();
        Assert.False(work.IsCompleted);
        Assert.Equal(1, fixture.RunningCount);
        Assert.Empty(fixture.Handler.Requests);
        AssertBusy(fixture.Gate);
        fixture.ReleaseAuthentication();
        await work.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.RunningCount);
        Assert.Single(fixture.Handler.Requests);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("headers", false)]
    [InlineData("headers", true)]
    [InlineData("body", false)]
    [InlineData("body", true)]
    public async Task Headers_body_and_existing_failure_handling_remain_owned_until_original_completion(
        string stage, bool fail)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue(stage, fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        var work = fixture.Start();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.Equal(1, fixture.RunningCount);
        Assert.False(work.IsCompleted);
        Assert.Equal(before, fixture.Snapshot());

        using var stoppedWaiting = new CancellationTokenSource();
        var callerWait = work.WaitAsync(stoppedWaiting.Token);
        stoppedWaiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerWait);
        Assert.False(work.IsCompleted);
        AssertBusy(fixture.Gate);

        hold.Release.TrySetResult(true);
        // The existing non-cancellation catch handles transient failures.
        await work.WaitAsync(TestTimeout);
        fixture.StopTimer();
        AssertIdle(fixture.Gate);
        Assert.Equal(0, fixture.RunningCount);
        Assert.Single(fixture.Handler.Requests);
        if (fail)
        {
            Assert.Equal(before, fixture.Snapshot());
            Assert.True(fixture.Account.IsSignedIn);
        }
        else
        {
            Assert.Equal("synthetic-refreshed-access", fixture.CurrentTokens.AccessToken);
            Assert.Equal(fixture.CurrentTokens, fixture.Store.Load());
            Assert.Equal(before.Account, fixture.Account.Account);
            Assert.Equal(before.LastError, fixture.Account.LastError);
        }
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task Duplicate_callback_does_not_dispatch_or_release_an_unfinished_worker(string stage)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        var hold = fixture.Handler.Enqueue(stage);
        var original = fixture.Start();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(1, fixture.RunningCount);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task Service_disposal_is_not_completion_of_a_cancellation_ignoring_dependency(string stage)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        var hold = fixture.Handler.Enqueue(stage);
        var original = fixture.Start();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        fixture.DisposeAccount();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.RunningCount);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Existing_authoritative_refresh_rejection_still_invalidates_the_synthetic_session(
        HttpStatusCode status)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        var hold = fixture.Handler.Enqueue("body", status);
        var work = fixture.Start();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.True(fixture.Account.IsSignedIn);
        hold.Release.TrySetResult(true);
        await work.WaitAsync(TestTimeout);
        fixture.StopTimer();
        Assert.False(fixture.Account.IsSignedIn);
        Assert.Null(fixture.Account.Account);
        Assert.Null(fixture.Store.Load());
        Assert.Contains("expired or was revoked", fixture.Account.LastError, StringComparison.Ordinal);
        Assert.Equal(0, fixture.RunningCount);
        Assert.Single(fixture.Handler.Requests);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Existing_network_failure_preserves_credentials_and_next_explicit_refresh_can_succeed()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        var before = fixture.Snapshot();
        var hold = fixture.Handler.Enqueue("headers", networkFailure: true);
        var work = fixture.Start();
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await work.WaitAsync(TestTimeout);
        fixture.StopTimer();
        Assert.Equal(before, fixture.Snapshot());
        AssertIdle(fixture.Gate);
        Assert.Equal(TimeSpan.FromMinutes(1), PaidAccountService.ProactiveTokenRefreshRetry);
        Assert.Equal(TimeSpan.FromMinutes(5), PaidAccountService.ProactiveTokenRefreshLead);

        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Equal("synthetic-refreshed-access", fixture.CurrentTokens.AccessToken);
        Assert.Equal(fixture.CurrentTokens, fixture.Store.Load());
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Missing_tokens_leave_no_work_or_HTTP_and_do_not_create_a_session_file()
    {
        await using var fixture = await Fixture.CreateAsync(signedIn: false);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Null(fixture.CurrentTokens);
        Assert.False(File.Exists(fixture.SessionPath));
        Assert.Equal(0, fixture.RunningCount);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Disposed_callback_remains_inert_even_when_the_gate_is_frozen()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.MakeTokenDue();
        var before = fixture.Snapshot();
        fixture.DisposeAccount();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(before, fixture.Snapshot());
            Assert.Empty(fixture.Handler.Requests);
            Assert.Equal(0, fixture.RunningCount);
        }
        finally { frozen.Dispose(); }
    }

    [Fact]
    public async Task Far_future_token_only_runs_the_existing_schedule_path_without_authentication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Snapshot();
        await fixture.Start().WaitAsync(TestTimeout);
        fixture.StopTimer();
        Assert.Equal(before, fixture.Snapshot());
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal(0, fixture.RunningCount);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Null_gate_legacy_constructor_preserves_the_existing_refresh_worker()
    {
        await using var fixture = await Fixture.CreateAsync(useGate: false);
        fixture.MakeTokenDue();
        Assert.True(fixture.Gate.TryFreeze(out var unrelatedFreeze));
        try
        {
            var hold = fixture.Handler.Enqueue("body");
            var work = fixture.Start();
            await hold.Entered.Task.WaitAsync(TestTimeout);
            Assert.False(work.IsCompleted);
            hold.Release.TrySetResult(true);
            await work.WaitAsync(TestTimeout);
            Assert.Single(fixture.Handler.Requests);
            Assert.Equal("synthetic-refreshed-access", fixture.CurrentTokens.AccessToken);
            Assert.Equal(fixture.CurrentTokens, fixture.Store.Load());
        }
        finally { unrelatedFreeze.Dispose(); }
    }

    [Fact]
    public async Task Fifty_local_original_worker_and_child_overlap_cycles_have_no_early_idle_or_duplicate_dispatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var iteration = 0; iteration < 50; iteration++)
        {
            fixture.MakeTokenDue();
            var hold = fixture.Handler.Enqueue(iteration % 2 == 0 ? "headers" : "body");
            var work = fixture.Start();
            await hold.Entered.Task.WaitAsync(TestTimeout);
            AssertBusy(fixture.Gate);
            await fixture.Start().WaitAsync(TestTimeout); // Existing running flag owns duplicate behavior.
            Assert.Equal(iteration + 1, fixture.Handler.Requests.Count);
            Assert.False(work.IsCompleted);
            AssertBusy(fixture.Gate);
            hold.Release.TrySetResult(true);
            await work.WaitAsync(TestTimeout);
            fixture.StopTimer();
            Assert.Equal(0, fixture.RunningCount);
            AssertIdle(fixture.Gate);
        }
    }

    private static ConstructorInfo RequireGateConstructor()
    {
        var constructors = typeof(PaidAccountService).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var constructor = constructors.SingleOrDefault(candidate => candidate.GetParameters() is var parameters &&
            parameters.Length == 5 && parameters[4].ParameterType == typeof(UpdateWorkAdmissionGate));
        Assert.NotNull(constructor);
        Assert.True(constructor.GetParameters()[4].IsOptional);
        return constructor;
    }

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryFreeze(out var freeze);
        freeze?.Dispose();
        Assert.False(accepted, "Unfinished original proactive work must prevent update freeze.");
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
        private readonly List<Task> _workers = [];
        private bool _authenticationHeld;
        private bool _accountDisposed;
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal SyntheticHandler Handler { get; } = new();
        internal PaidSessionStore Store { get; }
        internal PaidAccountService Account { get; }
        internal string SessionPath { get; }
        internal int RunningCount => (int)Running.GetValue(Account)!;
        internal PaidSessionTokens CurrentTokens => (PaidSessionTokens)Tokens.GetValue(Account)!;

        private Fixture(ConstructorInfo constructor, bool signedIn)
        {
            Assert.True(OperatingSystem.IsWindows(), "Synthetic DPAPI lifetime fixtures require Windows.");
            _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-proactive-work-tests", Guid.NewGuid().ToString("N"));
            SessionPath = Path.Combine(_directory, "session.dat");
            Store = new PaidSessionStore(SessionPath);
            if (signedIn)
                Store.Save(new PaidSessionTokens("synthetic-access", DateTimeOffset.UtcNow.AddHours(2),
                    "synthetic-refresh", DateTimeOffset.UtcNow.AddDays(1),
                    "synthetic-session", "synthetic-device", "synthetic-installation"));
            var configuration = new PaidStagingConfiguration(true, false,
                new Uri("https://paid-api.example.test/"), new Uri("https://paid.example.test/"));
            Action<string> noBrowser = _ => throw new InvalidOperationException("Synthetic refresh must never launch a browser.");
            Account = constructor is null
                ? new PaidAccountService(configuration, Store, Handler, noBrowser)
                : (PaidAccountService)constructor.Invoke([configuration, Store, Handler, noBrowser, Gate]);
        }

        internal static async Task<Fixture> CreateAsync(bool signedIn = true, bool useGate = true)
        {
            // Stable RED must fail on the missing seam BEFORE creating any
            // synthetic vault, starting a worker or holding a dependency.
            var constructor = useGate ? RequireGateConstructor() : null;
            var fixture = new Fixture(constructor, signedIn);
            try
            {
                var initialization = fixture.Account.InitializeAsync();
                fixture._workers.Add(initialization);
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

        internal void MakeTokenDue() => Tokens.SetValue(Account,
            CurrentTokens with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4) });

        internal State Snapshot() => new(CurrentTokens, Account.Account, Account.LastError,
            File.Exists(SessionPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SessionPath))) : null,
            Account.SignInState);

        internal Task Start()
        {
            Task task;
            try { task = (Task)Worker.Invoke(Account, null)!; }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            _workers.Add(task);
            return task;
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

        internal void StopTimer()
        {
            if (!_accountDisposed)
                ((System.Threading.Timer)Timer.GetValue(Account)!).Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal void DisposeAccount()
        {
            StopTimer();
            Account.Dispose();
            _accountDisposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            StopTimer();
            Handler.ReleaseAll();
            ReleaseAuthentication();
            var originals = Task.WhenAll(_workers);
            try { await originals.WaitAsync(TestTimeout); }
            catch when (originals.IsCompleted)
            {
                // Only already-completed synthetic faults/cancellation may be
                // observed here. A timeout with an unfinished worker escapes,
                // and its GUID evidence is retained rather than deleted.
            }
            DisposeAccount();
            if (!Directory.Exists(_directory)) return;
            var resolved = Path.GetFullPath(_directory);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "keyboard-wtf-proactive-work-tests"));
            Assert.Equal(parent, Path.GetDirectoryName(resolved));
            Assert.Matches("\\A[a-f0-9]{32}\\z", Path.GetFileName(resolved));
            Assert.False((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories),
                path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class Hold(string stage, HttpStatusCode status, bool networkFailure)
    {
        internal string Stage { get; } = stage;
        internal HttpStatusCode Status { get; } = status;
        internal bool NetworkFailure { get; } = networkFailure;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SyntheticHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Hold> _pending = new();
        private readonly ConcurrentQueue<Hold> _all = new();
        internal ConcurrentQueue<string> Requests { get; } = new();

        internal Hold Enqueue(string stage, HttpStatusCode status = HttpStatusCode.OK, bool networkFailure = false)
        {
            var hold = new Hold(stage, status, networkFailure);
            _pending.Enqueue(hold);
            _all.Enqueue(hold);
            return hold;
        }

        internal void ReleaseAll()
        {
            foreach (var hold in _all) hold.Release.TrySetResult(true);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("paid-api.example.test", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/v1/account")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("synthetic-access", request.Headers.Authorization?.Parameter);
                Requests.Enqueue(path);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    userId = "synthetic-user", email = "person@example.test", displayName = "Synthetic User",
                    status = "active", planId = "none", availableCreditMicros = 123,
                    reservedCreditMicros = 0, cycleUsageCreditMicros = 0, cycleEndsAt = (DateTimeOffset?)null,
                    devices = Array.Empty<object>(), recentUsage = Array.Empty<object>(), recentPurchases = Array.Empty<object>(),
                    subscriptionStatus = "none",
                }), Encoding.UTF8, "application/json") };
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
                await hold.Release.Task; // Deliberately ignore transport cancellation.
            }
            if (hold?.NetworkFailure == true) throw new HttpRequestException("Synthetic refresh network failure");
            var status = hold?.Status ?? HttpStatusCode.OK;
            var text = status == HttpStatusCode.OK ? JsonSerializer.Serialize(new
            {
                accessToken = "synthetic-refreshed-access", accessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
                refreshToken = "synthetic-refresh", refreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                sessionId = "synthetic-session", deviceId = "synthetic-device",
            }) : "{\"title\":\"Synthetic refresh refusal\"}";
            return new HttpResponseMessage(status)
            {
                Content = hold?.Stage == "body" ? new HeldContent(text, hold) : new StringContent(text, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class HeldContent(string text, Hold hold) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            hold.Entered.TrySetResult(true);
            await hold.Release.Task; // Body completion remains a real child of the original worker.
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
