using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Pure, UNWIRED notification core. No timer, UI/app, profile, installer, real
// network or provider. Synthetic signed HTTP uses an injected handler only.
// A true notify receipt means an actual trusted dispatch was attempted; these
// delegate tests do NOT prove OS/UI dispatch or a future host's channel recheck.
public sealed class PeriodicUpdateNotificationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Missing_contract_is_required_before_synthetic_work_and_has_only_notification_dependencies()
    {
        var binding = Binding.Require();
        Assert.Equal(typeof(Task), binding.CheckOnce.ReturnType);
        Assert.True(binding.CheckOnce.GetParameters()[0].IsOptional);
        Assert.True(typeof(IDisposable).IsAssignableFrom(binding.Type));
        Assert.Equal(new[] { typeof(UpdateWorkAdmissionGate), typeof(Func<bool>), typeof(Func<string>),
            typeof(Func<string, CancellationToken, Task<VerifiedUpdate>>),
            typeof(Func<VerifiedUpdate, CancellationToken, Task<bool>>) },
            binding.Constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Every_constructor_dependency_is_required_before_any_callback()
    {
        var binding = Binding.Require();
        var effects = 0;
        var args = new object[] { new UpdateWorkAdmissionGate(), (Func<bool>)(() => { effects++; return true; }),
            (Func<string>)(() => { effects++; return "stable"; }),
            (Func<string, CancellationToken, Task<VerifiedUpdate>>)((_, _) => { effects++; return Task.FromResult(Update()); }),
            (Func<VerifiedUpdate, CancellationToken, Task<bool>>)((_, _) => { effects++; return Task.FromResult(true); }) };
        for (var index = 0; index < args.Length; index++)
        {
            var missing = (object[])args.Clone();
            missing[index] = null;
            Assert.Throws<ArgumentNullException>(() => binding.Create(missing));
        }
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task Ineligible_build_performs_no_external_check_or_notice()
    {
        await using var fixture = new Fixture();
        fixture.Eligibility = () => false;
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Checks);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Pre_cancelled_attempt_performs_no_external_work_and_does_not_poison_next_check()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await fixture.Start(cancellation.Token).WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Checks);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Frozen_gate_skips_quietly_without_replaying_when_thawed()
    {
        await using var fixture = new Fixture();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(0, fixture.Checks);
            Assert.Equal(0, fixture.Notices);
            AssertUnavailable(fixture.Gate);
        }
        finally { frozen.Dispose(); }
        Assert.Equal(0, fixture.Checks);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Disposed_core_never_checks_or_notifies_again_and_disposal_is_idempotent()
    {
        await using var fixture = new Fixture();
        fixture.Stop();
        fixture.Stop();
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Checks);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Cancelled_caller_waiter_cannot_release_an_original_held_check()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Check = async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return Update();
        };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var waiter = original.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        Assert.Equal(0, fixture.Notices);
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Cancelled_attempt_retains_original_cancellation_ignoring_check_until_it_finishes()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Check = async (channel, token) =>
        {
            Assert.Equal("stable", channel);
            Assert.Equal(cancellation.Token, token);
            entered.TrySetResult(true);
            await release.Task; // Deliberately ignores cancellation.
            return Update();
        };
        var original = fixture.Start(cancellation.Token);
        await entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        fixture.Check = (_, _) => Task.FromResult(Update());
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Disposal_during_original_check_suppresses_notice_but_never_releases_early()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Check = async (_, _) => { entered.TrySetResult(true); await release.Task; return Update(); };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        fixture.Stop();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_release_the_original_awaited_notification()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Notify = async (_, _) => { entered.TrySetResult(true); await release.Task; return true; };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var waiter = original.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        AssertOpen(fixture.Gate);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices); // Actually dispatched receipt is deduped.
    }

    [Fact]
    public async Task Disposal_during_already_entered_notification_does_not_cancel_or_release_its_original_task()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Notify = async (_, _) => { entered.TrySetResult(true); await release.Task; return true; };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        fixture.Stop();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        AssertOpen(fixture.Gate);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Cancelled_notification_is_not_deduped_and_does_not_escape_as_a_faulted_periodic_task()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Notify = async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            entered.TrySetResult(true);
            await release.Task;
            token.ThrowIfCancellationRequested();
            return true;
        };
        var original = fixture.Start(cancellation.Token);
        await entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.True(original.IsCompletedSuccessfully);
        AssertOpen(fixture.Gate);
        fixture.Notify = (_, _) => Task.FromResult(true);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.Notices);
    }

    [Fact]
    public async Task True_dispatch_receipt_still_dedupes_if_cancellation_happens_after_the_actual_attempt()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Notify = (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel(); // The trusted synthetic dispatch was already attempted.
            return Task.FromResult(true);
        };
        await fixture.Start(cancellation.Token).WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
        fixture.Notify = (_, _) => Task.FromResult(true);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices); // No second notice merely because the caller cancelled later.
    }

    [Fact]
    public async Task Sixteen_overlapping_contenders_are_inert_while_original_check_is_held()
    {
        await using var fixture = new Fixture();
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Check = async (_, _) => { entered.TrySetResult(true); await release.Task; return Update(); };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        var contenders = Enumerable.Range(0, 16).Select(_ => Task.Run(() => fixture.Start())).ToArray();
        foreach (var task in contenders) fixture.Track(task);
        await Task.WhenAll(contenders).WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(0, fixture.Notices);
        Assert.False(original.IsCompleted);
        AssertBusy(fixture.Gate);
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Channel_normalization_reuses_existing_stable_beta_rules()
    {
        await using var fixture = new Fixture();
        foreach (var supplied in new[] { "BETA", "beta", "stable", "STABLE", "unknown", "", null })
        {
            var expected = SecureUpdateService.NormalizeChannel(supplied);
            fixture.Channel = () => supplied;
            fixture.Check = (channel, _) => { Assert.Equal(expected, channel); return Task.FromResult(Update() with { Channel = expected }); };
            await fixture.Start().WaitAsync(TestTimeout);
        }
        Assert.Equal(7, fixture.Checks);
        Assert.Equal(2, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Channel_change_while_check_awaits_suppresses_the_stale_offer()
    {
        await using var fixture = new Fixture();
        var channel = "stable";
        fixture.Channel = () => channel;
        var entered = fixture.Signal();
        var release = fixture.Signal();
        fixture.Check = async (requested, _) =>
        {
            Assert.Equal("stable", requested);
            entered.TrySetResult(true);
            await release.Task;
            return Update();
        };
        var original = fixture.Start();
        await entered.Task.WaitAsync(TestTimeout);
        channel = "beta";
        release.TrySetResult(true);
        await original.WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        channel = "stable";
        fixture.Check = (_, _) => Task.FromResult(Update());
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Returned_verified_tuple_must_match_the_current_channel_exactly()
    {
        await using var fixture = new Fixture();
        foreach (var channel in new[] { "beta", "STABLE", "stable ", "unknown", null })
        {
            fixture.Check = (_, _) => Task.FromResult(Update() with { Channel = channel });
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(0, fixture.Notices);
            AssertOpen(fixture.Gate);
        }
        fixture.Check = (_, _) => Task.FromResult(Update());
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Duplicate_suppression_binds_every_verified_record_field_not_only_version_or_reference()
    {
        await using var fixture = new Fixture();
        var original = Update();
        var candidates = new[] { original with { Version = new Version(9, 0, 1) },
            original with { MinimumVersion = new Version(1, 4, 18) }, original with { PublishedAt = original.PublishedAt.AddSeconds(1) },
            original with { PackageUri = new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/other/keyboard.wtf-setup.exe") },
            original with { Sha256 = new string('b', 64) }, original with { SizeBytes = original.SizeBytes + 1 },
            original with { ReleaseNotes = "Replacement notes" }, original with { KeyId = "replacement-key" } };
        var expected = 0;
        foreach (var candidate in candidates)
        {
            fixture.Check = (_, _) => Task.FromResult(original);
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(++expected, fixture.Notices);
            fixture.Check = (_, _) => Task.FromResult(candidate);
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(++expected, fixture.Notices);
            fixture.Check = (_, _) => Task.FromResult(candidate with { });
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(expected, fixture.Notices);
        }
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Stable_and_beta_each_remember_only_their_last_successfully_notified_tuple()
    {
        await using var fixture = new Fixture();
        var channel = "stable";
        var stable = Update();
        var beta = Update() with { Channel = "beta" };
        var offered = stable;
        fixture.Channel = () => channel;
        fixture.Check = (_, _) => Task.FromResult(offered);
        await fixture.Start().WaitAsync(TestTimeout);
        channel = "beta"; offered = beta;
        await fixture.Start().WaitAsync(TestTimeout);
        channel = "stable"; offered = stable with { };
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.Notices);
        offered = stable with { ReleaseNotes = "New same-version offer" };
        await fixture.Start().WaitAsync(TestTimeout);
        channel = "beta"; offered = beta with { };
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(3, fixture.Notices);
        channel = "stable"; offered = stable;
        await fixture.Start().WaitAsync(TestTimeout); // A replacement of the last stable tuple is eligible again.
        Assert.Equal(4, fixture.Notices);
        AssertOpen(fixture.Gate);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("throw")]
    [InlineData("fault")]
    [InlineData("null-task")]
    public async Task Unsuccessful_notification_is_contained_and_does_not_mark_the_tuple(string mode)
    {
        await using var fixture = new Fixture();
        fixture.Notify = (_, _) => mode switch
        {
            "false" => Task.FromResult(false),
            "throw" => throw new IOException("Synthetic notification refusal"),
            "fault" => Task.FromException<bool>(new IOException("Synthetic notification fault")),
            _ => null,
        };
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
        fixture.Notify = (_, _) => Task.FromResult(true);
        await fixture.Start().WaitAsync(TestTimeout);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.Notices);
    }

    [Fact]
    public async Task Check_fault_cancellation_null_task_or_no_update_are_contained_without_poisoning_singleflight()
    {
        await using var fixture = new Fixture();
        foreach (var mode in new[] { "throw", "fault", "cancel", "null-task", "no-update" })
        {
            fixture.Check = (_, _) => mode switch
            {
                "throw" => throw new IOException("Synthetic check refusal"),
                "fault" => Task.FromException<VerifiedUpdate>(new IOException("Synthetic check fault")),
                "cancel" => Task.FromCanceled<VerifiedUpdate>(new CancellationToken(canceled: true)),
                "null-task" => null,
                _ => Task.FromResult<VerifiedUpdate>(null),
            };
            var original = fixture.Start();
            await original.WaitAsync(TestTimeout);
            Assert.True(original.IsCompletedSuccessfully);
            Assert.Equal(0, fixture.Notices);
            AssertOpen(fixture.Gate);
        }
        fixture.Check = (_, _) => Task.FromResult(Update());
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(6, fixture.Checks);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Eligibility_and_channel_callback_exceptions_are_contained_without_external_work()
    {
        await using var fixture = new Fixture();
        fixture.Eligibility = () => throw new InvalidOperationException("Synthetic eligibility failure");
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Checks);
        AssertOpen(fixture.Gate);
        fixture.Eligibility = () => true;
        fixture.Channel = () => throw new InvalidOperationException("Synthetic channel failure");
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Checks);
        Assert.Equal(0, fixture.Notices);
        AssertOpen(fixture.Gate);
        fixture.Channel = () => "stable";
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Notices);
    }

    [Fact]
    public async Task Notify_may_reenter_check_and_dispose_without_duplicate_dispatch_or_deadlock()
    {
        await using var fixture = new Fixture();
        Task reentrant = null;
        fixture.Notify = (_, _) =>
        {
            reentrant = fixture.Start();
            Assert.True(reentrant.IsCompletedSuccessfully); // Single-flight refusal must not await its parent.
            fixture.Stop();
            return Task.FromResult(true);
        };
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.NotNull(reentrant);
        await reentrant.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Checks);
    }

    [Fact]
    public async Task Fifty_local_cycles_keep_actual_notification_owned_and_dedupe_only_completed_dispatch()
    {
        await using var fixture = new Fixture();
        for (var index = 0; index < 50; index++)
        {
            var tuple = Update() with { ReleaseNotes = "Synthetic cycle " + index };
            fixture.Check = (_, _) => Task.FromResult(tuple);
            fixture.Notify = (update, _) =>
            {
                Assert.Same(tuple, update);
                AssertBusy(fixture.Gate);
                return Task.FromResult(true);
            };
            await fixture.Start().WaitAsync(TestTimeout);
            await fixture.Start().WaitAsync(TestTimeout);
            Assert.Equal(index + 1, fixture.Notices);
            AssertOpen(fixture.Gate);
        }
        Assert.Equal(100, fixture.Checks);
    }

    [Fact]
    public async Task Periodic_success_no_update_and_failure_never_replace_or_consume_manual_consent()
    {
        await using var fixture = new Fixture();
        var consent = new VerifiedUpdateConsent();
        var approved = Update() with { Version = new Version(8, 0, 0), ReleaseNotes = "Manual approved offer" };
        var token = consent.Issue(approved);
        await fixture.Start().WaitAsync(TestTimeout);
        fixture.Check = (_, _) => Task.FromResult<VerifiedUpdate>(null);
        await fixture.Start().WaitAsync(TestTimeout);
        fixture.Check = (_, _) => Task.FromException<VerifiedUpdate>(new IOException("Synthetic periodic error"));
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.Same(approved, consent.Take(token, "stable"));
        Assert.Equal(1, fixture.Notices);
        AssertOpen(fixture.Gate);
        // No consent object is supplied to this core. Host integration must
        // independently prove that it never calls manual Issue/Clear/Take.
    }

    [Fact]
    public async Task Independently_signed_fake_HTTP_is_verified_before_one_notification_and_does_not_download()
    {
        await using var fixture = new Fixture();
        var key = fixture.Own(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var manifest = Sign(Manifest(), key);
        var handler = new SignedHandler(JsonSerializer.Serialize(manifest));
        var service = fixture.Own(new SecureUpdateService(handler, publicUpdateEligibility: () => true,
            verifier: Verifier(key), downloadRoot: @"C:\synthetic-unopened-notification-root"));
        fixture.Check = service.CheckAsync;
        VerifiedUpdate observed = null;
        fixture.Notify = (update, _) => { observed = update; AssertBusy(fixture.Gate); return Task.FromResult(true); };
        await fixture.Start().WaitAsync(TestTimeout);
        await fixture.Start().WaitAsync(TestTimeout);
        Assert.NotNull(observed);
        Assert.Equal(new Version(9, 0, 0), observed.Version);
        Assert.Equal(new Version(1, 4, 19), observed.MinimumVersion);
        Assert.Equal("stable", observed.Channel);
        Assert.Equal(manifest.ReleaseNotes, observed.ReleaseNotes);
        Assert.Equal(1, fixture.Notices);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Equal("https://keyboardwtf.com/releases/stable.json", uri));
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public async Task Tampered_signed_fake_HTTP_cannot_publish_and_periodic_failure_is_contained()
    {
        await using var fixture = new Fixture();
        var key = fixture.Own(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var signed = Sign(Manifest(), key);
        var tampered = signed with { ReleaseNotes = "Unsigned replacement notes" };
        var handler = new SignedHandler(JsonSerializer.Serialize(tampered));
        var service = fixture.Own(new SecureUpdateService(handler, publicUpdateEligibility: () => true,
            verifier: Verifier(key), downloadRoot: @"C:\synthetic-unopened-notification-root"));
        fixture.Check = service.CheckAsync;
        var original = fixture.Start();
        await original.WaitAsync(TestTimeout);
        Assert.True(original.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.Notices);
        Assert.Single(handler.Requests);
        AssertOpen(fixture.Gate);
    }

    [Fact]
    public void Notification_core_does_not_enable_unfinished_installation()
    {
        _ = Binding.Require();
        var readiness = typeof(WebSettingsService).GetProperty("SafeUpdateInstallationReady", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(readiness);
        Assert.False((bool)readiness.GetValue(null));
    }

    private static VerifiedUpdate Update() => new(new Version(9, 0, 0), new Version(1, 4, 19), "stable",
        new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/keyboard.wtf-setup.exe"),
        new string('a', 64), 1024, "Synthetic verified tuple", UpdateManifestVerifier.CurrentKeyId);

    private static SignedUpdateManifest Manifest() => new(1, "stable", "9.0.0", "1.4.19", DateTimeOffset.UtcNow,
        Update().PackageUri.ToString(), new string('a', 64), 1024, "Synthetic signed notification", UpdateManifestVerifier.CurrentKeyId, "");

    private static SignedUpdateManifest Sign(SignedUpdateManifest manifest, ECDsa key) => manifest with
    {
        Signature = Convert.ToBase64String(key.SignData(UpdateManifestVerifier.CanonicalPayload(manifest),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
    };

    private static UpdateManifestVerifier Verifier(ECDsa key) => new(new Dictionary<string, string>
    {
        [UpdateManifestVerifier.CurrentKeyId] = key.ExportSubjectPublicKeyInfoPem(),
    });

    private static void AssertBusy(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryFreeze(out var frozen);
        frozen?.Dispose();
        Assert.False(accepted);
    }

    private static void AssertUnavailable(UpdateWorkAdmissionGate gate)
    {
        var accepted = gate.TryAcquire(out var lease);
        lease?.Dispose();
        Assert.False(accepted);
        AssertBusy(gate);
    }

    private static void AssertOpen(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var frozen));
        frozen.Dispose();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        // Missing production binding fails before held work, key generation or HTTP.
        private readonly Binding _binding = Binding.Require();
        private readonly ConcurrentBag<TaskCompletionSource<bool>> _signals = new();
        private readonly ConcurrentBag<Task> _originals = new();
        private readonly ConcurrentBag<Task> _dependencies = new();
        private readonly List<IDisposable> _resources = [];
        private readonly object _core;
        private int _checks;
        private int _notices;
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal Func<bool> Eligibility { get; set; } = () => true;
        internal Func<string> Channel { get; set; } = () => "stable";
        internal Func<string, CancellationToken, Task<VerifiedUpdate>> Check { get; set; } = (_, _) => Task.FromResult(Update());
        internal Func<VerifiedUpdate, CancellationToken, Task<bool>> Notify { get; set; } = (_, _) => Task.FromResult(true);
        internal int Checks => Volatile.Read(ref _checks);
        internal int Notices => Volatile.Read(ref _notices);

        internal Fixture() => _core = _binding.Create([Gate, (Func<bool>)(() => Eligibility()),
            (Func<string>)(() => Channel()),
            (Func<string, CancellationToken, Task<VerifiedUpdate>>)((channel, token) =>
            {
                Interlocked.Increment(ref _checks);
                AssertBusy(Gate);
                var task = Check(channel, token);
                if (task is not null) _dependencies.Add(task);
                return task;
            }),
            (Func<VerifiedUpdate, CancellationToken, Task<bool>>)((update, token) =>
            {
                Interlocked.Increment(ref _notices);
                AssertBusy(Gate);
                var task = Notify(update, token);
                if (task is not null) _dependencies.Add(task);
                return task;
            })]);

        internal Task Start(CancellationToken token = default)
        {
            var task = _binding.Start(_core, token);
            _originals.Add(task);
            return task;
        }

        internal void Stop() => ((IDisposable)_core).Dispose();
        internal void Track(Task task) => _originals.Add(task);
        internal T Own<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
        internal TaskCompletionSource<bool> Signal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals.Add(signal);
            return signal;
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            foreach (var signal in _signals) signal.TrySetResult(true);
            var originals = Task.WhenAll(_originals);
            try { await originals.WaitAsync(TestTimeout); }
            catch (Exception) when (originals.IsCompleted) { /* Asserted completed errors are safe to observe. */ }
            var dependencies = Task.WhenAll(_dependencies);
            try { await dependencies.WaitAsync(TestTimeout); }
            catch (Exception) when (dependencies.IsCompleted) { /* Never swallow an unfinished callback timeout. */ }
            for (var index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
        }
    }

    private sealed class Binding
    {
        internal Type Type { get; }
        internal ConstructorInfo Constructor { get; }
        internal MethodInfo CheckOnce { get; }

        private Binding()
        {
            Type = typeof(UpdateWorkAdmissionGate).Assembly.GetType("KeyboardWtf.Services.Updates.PeriodicUpdateNotification");
            Assert.True(Type is not null, "The unwired periodic update notification core is missing.");
            Constructor = Type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(UpdateWorkAdmissionGate), typeof(Func<bool>), typeof(Func<string>),
                    typeof(Func<string, CancellationToken, Task<VerifiedUpdate>>),
                    typeof(Func<VerifiedUpdate, CancellationToken, Task<bool>>)], null);
            Assert.NotNull(Constructor);
            CheckOnce = Type.GetMethod("CheckOnceAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [typeof(CancellationToken)], null);
            Assert.NotNull(CheckOnce);
            Assert.Equal(typeof(Task), CheckOnce.ReturnType);
        }

        internal static Binding Require() => new();
        internal object Create(object[] args) => Invoke(() => Constructor.Invoke(args));
        internal Task Start(object core, CancellationToken token) => Assert.IsAssignableFrom<Task>(Invoke(() => CheckOnce.Invoke(core, [token])));
        private static object Invoke(Func<object> action)
        {
            try { return action(); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed class SignedHandler(string json) : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("keyboardwtf.com", request.RequestUri.Host);
            Assert.Equal("/releases/stable.json", request.RequestUri.AbsolutePath);
            Requests.Enqueue(request.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
