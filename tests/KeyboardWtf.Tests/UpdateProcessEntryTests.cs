using System.Reflection;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Inert delegates and owned filesystem only. Never runs Program, GUI, native
// host, profile, provider or installer. Runtime caller shape is checked separately.
public sealed class UpdateProcessEntryTests
{
    [Fact]
    public void Original_body_runs_once_under_activity_and_cleans_up_before_release()
    {
        var run = Binding();
        var path = NewExecutable();
        var calls = 0;
        var cleaned = false;
        Assert.True(run(path, () =>
        {
            calls++;
            using var reserved = UpdateInstallationIngressLease.TryReserve(path);
            Assert.NotNull(reserved);
            Assert.False(reserved.TryAcquireExclusive());
            try { Assert.Null(UpdateInstallationIngressLease.TryEnter(path)); }
            finally
            {
                cleaned = true;
                Assert.False(reserved.TryAcquireExclusive());
            }
        }, _ => Assert.Fail("Healthy ingress was refused.")));
        Assert.Equal(1, calls);
        Assert.True(cleaned);
        using var after = UpdateInstallationIngressLease.TryReserve(path);
        Assert.True(after.TryAcquireExclusive());
    }

    [Fact]
    public void Original_body_failure_is_not_reclassified_or_swallowed()
    {
        var run = Binding();
        var path = NewExecutable();
        var original = new InvalidOperationException("OWNED BODY FAILURE");
        var failureReports = 0;
        var actual = Assert.Throws<InvalidOperationException>(() =>
            run(path, () => throw original, _ => failureReports++));
        Assert.Same(original, actual);
        Assert.Equal(0, failureReports);
        using var after = UpdateInstallationIngressLease.TryReserve(path);
        Assert.True(after.TryAcquireExclusive());
    }

    [Fact]
    public void Body_return_state_is_not_overwritten_and_failure_report_can_throw()
    {
        var run = Binding();
        var path = NewExecutable();
        var nativeReturn = -1;
        Assert.True(run(path, () => nativeReturn = 2, _ => Assert.Fail("Unexpected refusal.")));
        Assert.Equal(2, nativeReturn);
        var original = new InvalidOperationException("OWNED REFUSAL REPORT FAILURE");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() =>
            run("relative\\keyboard.wtf.exe", () => Assert.Fail("Unsafe body ran."), _ => throw original)));
    }

    [Theory]
    [InlineData("relative\\keyboard.wtf.exe")]
    [InlineData("\\\\server\\share\\keyboard.wtf.exe")]
    [InlineData("C:\\Synthetic\\other.exe")]
    public void Unsupported_path_refuses_before_any_original_effect(string path)
    {
        var run = Binding();
        var reports = new List<string>();
        Assert.False(run(path, () => Assert.Fail("Unsafe body ran."), reports.Add));
        Assert.Single(reports);
        Assert.DoesNotContain(path, reports[0]);
    }

    [Fact]
    public void Malformed_marker_refuses_without_starting_or_leaking_activity()
    {
        var run = Binding();
        var path = NewExecutable();
        var marker = Path.Combine(Path.GetDirectoryName(path), ".keyboard.wtf-update-reservation");
        File.WriteAllText(marker, "NOT AN UPDATE MARKER");
        var reports = new List<string>();
        Assert.False(run(path, () => Assert.Fail("Malformed admission ran."), reports.Add));
        Assert.Single(reports);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path), ".keyboard.wtf-update-activity")));
        File.Delete(marker); // Exact owned inert marker only, after failed original acquisition returned.
        using var after = UpdateInstallationIngressLease.TryReserve(path);
        Assert.True(after.TryAcquireExclusive());
    }

    [Fact]
    public void Continuously_reserved_installation_refuses_with_no_original_effect()
    {
        var run = Binding();
        var path = NewExecutable();
        using var reservation = UpdateInstallationIngressLease.TryReserve(path);
        var reports = new List<string>();
        Assert.False(run(path, () => Assert.Fail("Reserved installation ran."), reports.Add));
        Assert.Single(reports);
        Assert.Contains("busy", reports[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(reservation.TryAcquireExclusive());
    }

    [Fact]
    public async Task Transient_admission_contention_does_not_drop_an_ordinary_cold_start()
    {
        var run = Binding();
        var path = NewExecutable();
        var marker = Path.Combine(Path.GetDirectoryName(path), ".keyboard.wtf-update-reservation");
        var held = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var invoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = Task.Run(() =>
        {
            started.SetResult(true);
            return run(path, () => invoked.SetResult(true), _ => Assert.Fail("Short contention dropped startup."));
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100); // Observation window only, never production task latency.
            Assert.False(attempt.IsCompleted);
            Assert.False(invoked.Task.IsCompleted);
            held.Dispose();
            Assert.True(await attempt.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await invoked.Task);
        }
        finally
        {
            held.Dispose();
            await attempt; // Observe original work, not just the diagnostic waiter.
        }
    }

    [Fact]
    public async Task Fifty_paired_cold_starts_preserve_original_effects_and_release_activity()
    {
        var run = Binding();
        var path = NewExecutable();
        for (var cycle = 0; cycle < 50; cycle++)
        {
            using var start = new ManualResetEventSlim();
            var calls = 0;
            var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return run(path, () => Interlocked.Increment(ref calls), _ => Assert.Fail("Concurrent startup dropped."));
            })).ToArray();
            start.Set();
            var results = await Task.WhenAll(attempts);
            Assert.All(results, result => Assert.True(result));
            Assert.Equal(2, calls);
            using var after = UpdateInstallationIngressLease.TryReserve(path);
            Assert.True(after.TryAcquireExclusive());
        }
    }

    private static Func<string, Action, Action<string>, bool> Binding()
    {
        var type = typeof(WebSettingsService).Assembly.GetType("KeyboardWtf.Services.Updates.UpdateProcessEntry");
        Assert.NotNull(type);
        var method = type.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<string, Action, Action<string>, bool>>();
    }

    private static string NewExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "kwt-process-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "keyboard.wtf.exe"); // Inert identity declaration; no PE is created or run.
    }
}
