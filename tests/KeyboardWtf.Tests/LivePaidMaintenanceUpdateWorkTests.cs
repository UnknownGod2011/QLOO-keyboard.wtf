using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Finite paid maintenance only. All HTTP is an injected in-memory handler;
// the DPAPI vault contains synthetic tokens in an owned GUID-local directory.
// No app/singleton, default profile, Live session, provider, microphone or UI.
public sealed class LivePaidMaintenanceUpdateWorkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private const long InitialCredit = 100;

    [Theory]
    [InlineData("warm")]
    [InlineData("stop")]
    [InlineData("provider")]
    public async Task Actual_held_HTTP_completion_remains_owned_after_a_caller_stops_waiting(string method)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.CreateAsync();
        var hold = fixture.Handler.Hold(PathFor(method), credit: 201);
        var work = fixture.Start(method);
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        using var callerCancellation = new CancellationTokenSource();
        var callerWait = work.WaitAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerWait);
        Assert.False(work.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(InitialCredit, fixture.Account.Account.AvailableCreditMicros);

        hold.Release.TrySetResult(true);
        await work.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
        Assert.Equal(method == "warm" ? InitialCredit : 201,
            fixture.Account.Account.AvailableCreditMicros);
        Assert.Equal(new[] { PathFor(method) }, fixture.Handler.Requests.ToArray());
    }

    [Theory]
    [InlineData("warm", false)]
    [InlineData("warm", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("provider", false)]
    [InlineData("provider", true)]
    public async Task Success_and_existing_non_cancellation_failure_paths_release_only_after_HTTP_finishes(
        string method, bool fail)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.CreateAsync();
        var hold = fixture.Handler.Hold(PathFor(method), credit: 202, fail: fail);
        var work = fixture.Start(method);
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        Assert.False(work.IsCompleted);
        hold.Release.TrySetResult(true);
        // Existing maintenance methods swallow non-cancellation HTTP failures.
        // Passive accounting must not alter those catches or invent retries.
        await work.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
        Assert.Equal(!fail && method != "warm" ? 202 : InitialCredit,
            fixture.Account.Account.AvailableCreditMicros);
        Assert.Equal(new[] { PathFor(method) }, fixture.Handler.Requests.ToArray());
    }

    [Theory]
    [InlineData("warm")]
    [InlineData("stop")]
    [InlineData("provider")]
    public async Task Frozen_admission_refuses_before_HTTP_or_authoritative_account_mutation(string method)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Account.Account;
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Start(method));
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fixture.Handler.Requests);
            Assert.Same(before, fixture.Account.Account);
        }
        finally { frozen.Dispose(); }

        // Refusal must not consume the maintenance operation or change tokens.
        var hold = fixture.Handler.Hold(PathFor(method), credit: 203);
        var work = fixture.Start(method);
        await hold.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await work.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
        Assert.Equal(new[] { PathFor(method) }, fixture.Handler.Requests.ToArray());
    }

    [Theory]
    [InlineData("warm", true)]
    [InlineData("stop", true)]
    [InlineData("provider", true)]
    [InlineData("warm", false)]
    [InlineData("stop", false)]
    [InlineData("provider", false)]
    public async Task Unsigned_or_disabled_no_effect_paths_do_not_acquire_ownership(string method, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.CreateAsync(enabled: enabled, signedIn: false);
        Assert.False(fixture.Account.IsSignedIn);
        Assert.Null(fixture.Account.Account);
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            await fixture.Start(method).WaitAsync(TestTimeout);
            Assert.Empty(fixture.Handler.Requests);
            Assert.Null(fixture.Account.Account);
        }
        finally { frozen.Dispose(); }
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Theory]
    [InlineData("warm")]
    [InlineData("stop")]
    [InlineData("provider")]
    public async Task Null_account_no_effect_paths_preserve_legacy_completion_without_ownership(string method)
    {
        var gate = new UpdateWorkAdmissionGate();
        using var live = new GeminiLiveConversationService(null, null, null, null, null, gate);
        Assert.True(gate.TryFreeze(out var frozen));
        try { await Start(live, method, CancellationToken.None).WaitAsync(TestTimeout); }
        finally { frozen.Dispose(); }
        await AssertEventuallyIdle(gate);
    }

    [Theory]
    [InlineData("warm")]
    [InlineData("stop")]
    [InlineData("provider")]
    public async Task Null_gate_keeps_original_maintenance_execution_and_exception_behavior(string method)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.CreateAsync(useGate: false);
        var hold = fixture.Handler.Hold(PathFor(method), credit: 204);
        var work = fixture.Start(method);
        await hold.Entered.Task.WaitAsync(TestTimeout);
        Assert.False(work.IsCompleted);
        AssertIdle(fixture.Gate);
        hold.Release.TrySetResult(true);
        await work.WaitAsync(TestTimeout);
        Assert.Equal(method == "warm" ? InitialCredit : 204,
            fixture.Account.Account.AvailableCreditMicros);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Warm_caller_cancellation_does_not_release_an_HTTP_handler_that_has_not_unwound()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.CreateAsync();
        using var requestCancellation = new CancellationTokenSource();
        var hold = fixture.Handler.Hold("/v1/ready");
        var work = fixture.Start("warm", requestCancellation.Token);
        await hold.Entered.Task.WaitAsync(TestTimeout);
        requestCancellation.Cancel();
        // This injected handler intentionally ignores cancellation. Neither
        // the caller token nor a visible cancelled waiter proves completion.
        Assert.False(work.IsCompleted);
        AssertBusy(fixture.Gate);
        hold.Release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => work.WaitAsync(TestTimeout));
        await AssertEventuallyIdle(fixture.Gate);
        Assert.Equal(new[] { "/v1/ready" }, fixture.Handler.Requests.ToArray());
        Assert.Equal(InitialCredit, fixture.Account.Account.AvailableCreditMicros);
    }

    [Fact]
    public async Task Independent_post_stop_and_provider_refreshes_each_hold_the_same_gate_until_completion()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.Handler.Hold("/v1/account", credit: 301);
        var second = fixture.Handler.Hold("/v1/account", credit: 302);
        var stopWork = fixture.Start("stop");
        await first.Entered.Task.WaitAsync(TestTimeout);
        var providerWork = fixture.Start("provider");
        await second.Entered.Task.WaitAsync(TestTimeout);
        AssertBusy(fixture.Gate);
        first.Release.TrySetResult(true);
        await stopWork.WaitAsync(TestTimeout);
        Assert.Equal(301, fixture.Account.Account.AvailableCreditMicros);
        Assert.False(providerWork.IsCompleted);
        AssertBusy(fixture.Gate);
        second.Release.TrySetResult(true);
        await providerWork.WaitAsync(TestTimeout);
        await AssertEventuallyIdle(fixture.Gate);
        Assert.Equal(302, fixture.Account.Account.AvailableCreditMicros);
        Assert.Equal(new[] { "/v1/account", "/v1/account" }, fixture.Handler.Requests.ToArray());
    }

    private static string PathFor(string method) => method == "warm" ? "/v1/ready" : "/v1/account";

    private static Task Start(GeminiLiveConversationService live, string method, CancellationToken token)
    {
        if (method == "warm") return live.WarmPaidBackendAsync(token);
        var name = method == "stop" ? "RefreshAccountAfterStopAsync" : "RefreshAccountAfterProviderSessionAsync";
        var methodInfo = typeof(GeminiLiveConversationService)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(methodInfo);
        try { return Assert.IsAssignableFrom<Task>(methodInfo.Invoke(live, null)); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var froze = gate.TryFreeze(out var frozen);
        frozen?.Dispose();
        Assert.False(froze);
    }

    private static void AssertIdle(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
    }

    private static async Task AssertEventuallyIdle(UpdateWorkAdmissionGate gate)
    {
        // Preserve original completion identity if release uses a completion
        // continuation; joining that original task need not join the tail.
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (gate.TryFreeze(out var frozen))
            {
                frozen.Dispose();
                return;
            }
            await Task.Delay(5);
        }
        AssertIdle(gate);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly List<Task> _started = [];
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal SyntheticHandler Handler { get; } = new();
        internal PaidAccountService Account { get; }
        private GeminiLiveConversationService Live { get; }

        private Fixture(bool enabled, bool signedIn, bool useGate)
        {
            _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-paid-maintenance-tests", Guid.NewGuid().ToString("N"));
            var store = new PaidSessionStore(Path.Combine(_directory, "session.dat"));
            if (signedIn)
                store.Save(new PaidSessionTokens("synthetic-access", DateTimeOffset.UtcNow.AddHours(1),
                    "synthetic-refresh", DateTimeOffset.UtcNow.AddDays(1),
                    "synthetic-session", "synthetic-device", "synthetic-installation"));
            Account = new PaidAccountService(new PaidStagingConfiguration(enabled, false,
                new Uri("https://paid-api.example.test/"), new Uri("https://paid.example.test/")),
                store, Handler, _ => throw new InvalidOperationException("This fixture must never launch a browser."));
            Live = new GeminiLiveConversationService(null, null, null, Account, null, useGate ? Gate : null);
        }

        internal static async Task<Fixture> CreateAsync(bool enabled = true, bool signedIn = true, bool useGate = true)
        {
            var fixture = new Fixture(enabled, signedIn, useGate);
            try
            {
                await fixture.Account.InitializeAsync().WaitAsync(TestTimeout);
                fixture.Handler.Requests.Clear(); // Startup read is not this maintenance invocation.
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal Task Start(string method, CancellationToken token = default)
        {
            var task = LivePaidMaintenanceUpdateWorkTests.Start(Live, method, token);
            _started.Add(task);
            return task;
        }

        public async ValueTask DisposeAsync()
        {
            Handler.ReleaseAll();
            try { await Task.WhenAll(_started).WaitAsync(TestTimeout); }
            catch { /* Assertion paths still release/join owned synthetic workers. */ }
            Live.Dispose();
            Account.Dispose();
            if (Directory.Exists(_directory))
            {
                var resolved = Path.GetFullPath(_directory);
                var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "keyboard-wtf-paid-maintenance-tests"));
                Assert.Equal(parent, Path.GetDirectoryName(resolved));
                Assert.Matches("^[a-f0-9]{32}$", Path.GetFileName(resolved));
                Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories),
                    path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
                Assert.False((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private sealed class Hold(string path, long credit, bool fail)
    {
        internal string Path { get; } = path;
        internal long Credit { get; } = credit;
        internal bool Fail { get; } = fail;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SyntheticHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Hold> _pending = new();
        private readonly ConcurrentQueue<Hold> _all = new();
        internal ConcurrentQueue<string> Requests { get; } = new();

        internal Hold Hold(string path, long credit = 200, bool fail = false)
        {
            var hold = new Hold(path, credit, fail);
            _pending.Enqueue(hold);
            _all.Enqueue(hold);
            return hold;
        }

        internal void ReleaseAll()
        {
            foreach (var hold in _all) hold.Release.TrySetResult(true);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("paid-api.example.test", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-access", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            Assert.True(path is "/v1/account" or "/v1/ready", "Only non-AI maintenance HTTP is permitted.");
            Requests.Enqueue(path);
            var credit = InitialCredit;
            if (_pending.TryDequeue(out var hold))
            {
                Assert.Equal(hold.Path, path);
                hold.Entered.TrySetResult(true);
                await hold.Release.Task; // Deliberately ignores cancellation until actual HTTP completion.
                if (hold.Fail) throw new HttpRequestException("Synthetic maintenance failure");
                credit = hold.Credit;
            }
            var payload = path == "/v1/ready" ? "{\"status\":\"ready\"}" : JsonSerializer.Serialize(new
            {
                userId = "synthetic-user", email = "person@example.test", displayName = "Synthetic User",
                status = "active", planId = "none", availableCreditMicros = credit,
                reservedCreditMicros = 0, cycleUsageCreditMicros = 0,
                cycleEndsAt = (DateTimeOffset?)null, devices = Array.Empty<object>(),
                recentUsage = Array.Empty<object>(), recentPurchases = Array.Empty<object>(), subscriptionStatus = "none",
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
