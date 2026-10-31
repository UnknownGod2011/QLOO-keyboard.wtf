using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using KeyboardWtf.Services.Updates;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Tests;

// Inert console launches only. Its apphost shares the validated filename,
// but links only updater primitives, never the real app or profile/providers.
public sealed class UpdateWorkerHandshakeProcessTests
{
    private readonly ITestOutputHelper _output;

    public UpdateWorkerHandshakeProcessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Separate_observer_characterizes_existing_image_access_without_certifying_native_idle()
    {
        await using var targetFixture = new Fixture();
        await using var observerFixture = new Fixture();
        Assert.NotEqual(targetFixture.Executable, observerFixture.Executable, StringComparer.OrdinalIgnoreCase);
        var targetBefore = ImageHash(targetFixture.Executable);
        var observerBefore = ImageHash(observerFixture.Executable);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var originals = new List<Task>();
        var identities = new List<UpdateProcessIdentityLease>();
        UpdateInstallationIngressLease reservation = null;
        Process observer = null;
        UpdateProcessIdentityLease observerIdentity = null;
        try
        {
            var first = targetFixture.Start("original-release-hold");
            var firstError = first.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(firstError);
            Assert.Equal("ORIGINAL_ACTIVITY_HELD", await ReadLineAsync(first));
            var firstDeclaration = new DeclaredUpdateParent(first.Id, first.StartTime.ToUniversalTime().Ticks,
                targetFixture.Executable);
            var firstIdentity = UpdateProcessIdentityLease.Acquire(firstDeclaration, targetFixture.Executable);
            identities.Add(firstIdentity);

            var second = targetFixture.Start("original-release-hold");
            var secondError = second.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(secondError);
            Assert.Equal("ORIGINAL_ACTIVITY_HELD", await ReadLineAsync(second));
            var secondDeclaration = new DeclaredUpdateParent(second.Id, second.StartTime.ToUniversalTime().Ticks,
                targetFixture.Executable);
            var secondIdentity = UpdateProcessIdentityLease.Acquire(secondDeclaration, targetFixture.Executable);
            identities.Add(secondIdentity);

            reservation = UpdateInstallationIngressLease.TryReserve(targetFixture.Executable);
            Assert.NotNull(reservation);
            Assert.False(reservation.TryAcquireExclusive());
            observer = observerFixture.Start("image-access-observer", targetFixture.Executable,
                firstDeclaration.ProcessId.ToString(CultureInfo.InvariantCulture),
                firstDeclaration.CreationTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                secondDeclaration.ProcessId.ToString(CultureInfo.InvariantCulture),
                secondDeclaration.CreationTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
            var observerError = observer.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(observerError);
            observerIdentity = UpdateProcessIdentityLease.Acquire(new(observer.Id,
                observer.StartTime.ToUniversalTime().Ticks, observerFixture.Executable), observerFixture.Executable);
            identities.Add(observerIdentity);
            Assert.Equal("IMAGE_OBSERVER_READY", await ReadLineAsync(observer));

            var before = await ProbeAsync("before-exit", false, false);
            Send(first, "release-activity");
            Send(second, "release-activity");
            Assert.Equal("ORIGINAL_ACTIVITY_RELEASED_OS_ALIVE", await ReadLineAsync(first));
            Assert.Equal("ORIGINAL_ACTIVITY_RELEASED_OS_ALIVE", await ReadLineAsync(second));
            Assert.False(firstIdentity.HasExited);
            Assert.False(secondIdentity.HasExited);
            Assert.True(reservation.TryAcquireExclusive()); // Deliberately NOT an OS-exit certificate.
            var afterScope = await ProbeAsync("scope-released", false, false);

            first.StandardInput.Close();
            await PositiveExitAsync(first, firstIdentity);
            Assert.False(secondIdentity.HasExited);
            var afterOne = await ProbeAsync("one-exited", true, false);
            second.StandardInput.Close();
            await PositiveExitAsync(second, secondIdentity);

            // Windows image-section teardown may lag a signaled original. Retry
            // this positive CONTROL only; exhaustion fails, never certifies idle.
            var clock = Stopwatch.StartNew();
            ImageAccessReceipt afterBoth;
            do
            {
                afterBoth = await ProbeAsync("both-exited", true, true);
                if (afterBoth.Permitted) break;
                await Task.Delay(50, deadline.Token);
            } while (clock.Elapsed < TimeSpan.FromSeconds(2));
            Assert.True(afterBoth.Permitted, "The bounded post-exit access control never permitted access.");

            using (var held = new FileStream(targetFixture.Executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var locked = await ProbeAsync("held-file-lock", true, true);
                Assert.False(locked.Permitted);
                Assert.Equal(32, locked.LowWin32);
                Assert.Equal(0x80070020u, locked.HResult);
            }
            Assert.True((await ProbeAsync("released-file-lock", true, true)).Permitted);
            if (before.Permitted || afterScope.Permitted || afterOne.Permitted)
                _output.WriteLine("LIVE_IMAGE_ACCESS_PERMITTED: this probe is NOT an image/native-idle certificate.");
            else
                _output.WriteLine("LIVE_IMAGE_ACCESS_REFUSED: characterization of these two known originals only; NOT complete native-idle proof.");

            observer.StandardInput.Close();
            await PositiveExitAsync(observer, observerIdentity);
            Assert.Equal("IMAGE_OBSERVER_DONE" + Environment.NewLine, await ReadTailAsync(observer));
            Assert.Equal("ORIGINAL_EXITING" + Environment.NewLine, await ReadTailAsync(first));
            Assert.Equal("ORIGINAL_EXITING" + Environment.NewLine, await ReadTailAsync(second));
            Assert.Equal("", await firstError);
            Assert.Equal("", await secondError);
            Assert.Equal("", await observerError);
            Assert.Equal(targetBefore, ImageHash(targetFixture.Executable));
            Assert.Equal(observerBefore, ImageHash(observerFixture.Executable));
            _output.WriteLine("IMAGE_SHA256_UNCHANGED|target=" + targetBefore + "|observer=" + observerBefore);
        }
        finally
        {
            targetFixture.ReleaseInputs();
            observerFixture.ReleaseInputs();
            try { await Task.WhenAll(targetFixture.JoinAllAsync(), observerFixture.JoinAllAsync()); }
            finally
            {
                deadline.Cancel();
                foreach (var original in originals) await ObserveAsync(original);
                foreach (var identity in identities) identity.Dispose();
                reservation?.Dispose();
            }
        }

        async Task<string> ReadLineAsync(Process process)
        {
            var original = process.StandardOutput.ReadLineAsync(deadline.Token).AsTask();
            originals.Add(original);
            var line = await original;
            Assert.NotNull(line);
            return line;
        }

        async Task<string> ReadTailAsync(Process process)
        {
            var original = process.StandardOutput.ReadToEndAsync(deadline.Token);
            originals.Add(original);
            return await original;
        }

        async Task PositiveExitAsync(Process process, UpdateProcessIdentityLease identity)
        {
            var original = identity.WaitForExitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            originals.Add(original);
            Assert.True(await original, "The same original process handle did not positively signal exit.");
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
        }

        async Task<ImageAccessReceipt> ProbeAsync(string stage, bool firstExited, bool secondExited)
        {
            Assert.False(observerIdentity.HasExited);
            Send(observer, stage);
            var line = await ReadLineAsync(observer);
            _output.WriteLine(line);
            var fields = line.Split('|');
            Assert.Equal(7, fields.Length);
            Assert.Equal("IMAGE_ACCESS", fields[0]);
            Assert.Equal(stage, fields[1]);
            Assert.Equal(firstExited, bool.Parse(fields[2]));
            Assert.Equal(secondExited, bool.Parse(fields[3]));
            Assert.True(fields[4] is "PERMITTED" or "REFUSED", line);
            Assert.StartsWith("0x", fields[6]);
            var receipt = new ImageAccessReceipt(fields[4] == "PERMITTED",
                int.Parse(fields[5], CultureInfo.InvariantCulture),
                uint.Parse(fields[6].AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            if (receipt.Permitted)
            {
                Assert.Equal(0, receipt.LowWin32);
                Assert.Equal(0u, receipt.HResult);
            }
            else Assert.NotEqual(0u, receipt.HResult);
            Assert.False(observerIdentity.HasExited);
            return receipt;
        }
    }

    [Fact]
    public async Task Image_observer_refuses_own_or_nonfixture_targets_before_target_process_acquisition()
    {
        await using var fixture = new Fixture();
        var before = ImageHash(fixture.Executable);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var originals = new List<Task>();
        try
        {
            foreach (var target in new[]
            {
                fixture.Executable,
                Path.Combine(Path.GetTempPath(), "not-an-owned-fixture-" + Guid.NewGuid().ToString("N"), "keyboard.wtf.exe"),
                @"C:\Synthetic Installed\keyboard.wtf.exe",
            })
            {
                var observer = fixture.Start("image-access-observer", target, "1", "1", "2", "1");
                var output = observer.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = observer.StandardError.ReadToEndAsync(deadline.Token);
                originals.Add(output);
                originals.Add(error);
                await observer.WaitForExitAsync(deadline.Token);
                Assert.Equal(1, observer.ExitCode);
                Assert.Empty(await output);
                Assert.Contains("The image probe accepts only a different GUID-local inert fixture image.", await error);
            }
            Assert.Equal(before, ImageHash(fixture.Executable));
        }
        finally
        {
            fixture.ReleaseInputs();
            try { await fixture.JoinAllAsync(); }
            finally
            {
                deadline.Cancel();
                foreach (var original in originals) await ObserveAsync(original);
            }
        }
    }

    private sealed record ImageAccessReceipt(bool Permitted, int LowWin32, uint HResult);

    private static string ImageHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Theory]
    [InlineData("success")]
    [InlineData("callback-cancel")]
    [InlineData("callback-fault")]
    [InlineData("callback-null")]
    public async Task Reserved_worker_keeps_exclusion_across_original_OS_exits_and_original_boundary(string variation)
    {
        await using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var originals = new List<Task>();
        Task<string> boundaryLine = null;
        try
        {
            var parent = fixture.Start("reserved-parent");
            var parentError = parent.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(parentError);
            var config = await ReadConfigAsync(parent, deadline.Token);
            // Await completed parent admission before the second cold entrant;
            // brief reservation contention is legitimate, not a fixture failure.
            var extra = fixture.Start("original-release-hold");
            var extraError = extra.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(extraError);
            Assert.Equal("ORIGINAL_ACTIVITY_HELD", await extra.StandardOutput.ReadLineAsync(deadline.Token));
            var worker = fixture.Start(WorkerArguments(config, extra, variation));
            var workerError = worker.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(workerError);
            Assert.Equal("RESERVED_ATTEMPT_STARTED", await worker.StandardOutput.ReadLineAsync(deadline.Token));
            Assert.Equal("CONCURRENT_ATTEMPT_REFUSED", await worker.StandardOutput.ReadLineAsync(deadline.Token));
            await ProbeAsync("ingress-probe"); // Before parent starts READY/ARM exchange.
            Send(parent, worker.Id + "|" + worker.StartTime.ToUniversalTime().Ticks);
            Assert.Equal("RESERVED_PARENT_ARMED", await parent.StandardOutput.ReadLineAsync(deadline.Token));
            boundaryLine = worker.StandardOutput.ReadLineAsync(deadline.Token).AsTask();
            originals.Add(boundaryLine);
            await ProbeAsync("entry-gui-probe");
            Assert.False(boundaryLine.IsCompleted); // Parent AND extra originals still alive.

            Send(parent, "release-activity");
            Send(extra, "release-activity");
            Assert.Equal("PARENT_ACTIVITY_RELEASED_OS_ALIVE", await parent.StandardOutput.ReadLineAsync(deadline.Token));
            Assert.Equal("ORIGINAL_ACTIVITY_RELEASED_OS_ALIVE", await extra.StandardOutput.ReadLineAsync(deadline.Token));
            Assert.False(parent.HasExited);
            Assert.False(extra.HasExited);
            await ProbeAsync("entry-native-probe");
            Assert.False(boundaryLine.IsCompleted); // Free activity scopes are NOT OS exits.

            parent.StandardInput.Close();
            await parent.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, parent.ExitCode);
            Assert.False(extra.HasExited);
            await ProbeAsync("entry-restart-probe");
            Assert.False(boundaryLine.IsCompleted); // Additional original remains positively held.

            extra.StandardInput.Close();
            await extra.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, extra.ExitCode);
            Assert.Equal(variation == "callback-null" ? "INERT_BOUNDARY_NULL" : "INERT_BOUNDARY_STARTED", await boundaryLine);
            if (variation != "callback-null")
            {
                await ProbeAsync("entry-restart-probe");
                Assert.False(worker.HasExited); // Includes cancellation DURING the original boundary.
                Send(worker, "complete-original-boundary");
            }
            var tail = worker.StandardOutput.ReadToEndAsync(deadline.Token);
            originals.Add(tail);
            await worker.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, worker.ExitCode);
            var output = await tail;
            Assert.Contains("INERT_BOUNDARY_CALLS|1", output);
            Assert.Contains(variation switch
            {
                "callback-null" => "RESERVED_ATTEMPT_REJECTED|InvalidOperationException",
                "callback-fault" => "RESERVED_ATTEMPT_REJECTED|InvalidDataException",
                _ => "RESERVED_ATTEMPT_RESULT|True",
            }, output);
            Assert.Equal("", await parentError);
            Assert.Equal("", await extraError);
            Assert.Equal("", await workerError);
            await ProbeAsync("entry-gui-probe", accepted: true);
        }
        finally
        {
            fixture.ReleaseInputs();
            try { await fixture.JoinAllAsync(); }
            finally
            {
                deadline.Cancel();
                foreach (var original in originals) await ObserveAsync(original);
            }
        }

        async Task ProbeAsync(string mode, bool accepted = false)
        {
            var probe = fixture.Start(mode);
            var output = probe.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = probe.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(output);
            originals.Add(error);
            await probe.WaitForExitAsync(deadline.Token);
            Assert.Equal(accepted || mode == "ingress-probe" ? 0 : 1, probe.ExitCode);
            if (accepted)
            {
                Assert.Equal("ENTRY_BODY|" + mode + Environment.NewLine, await output);
                Assert.Equal("", await error);
            }
            else if (mode == "ingress-probe")
            {
                Assert.Equal("INGRESS_REFUSED" + Environment.NewLine, await output);
                Assert.Equal("", await error);
            }
            else
            {
                Assert.Empty(await output);
                Assert.StartsWith("ENTRY_REFUSED|", await error);
            }
        }
    }

    [Theory]
    [InlineData("short-exit", false)]
    [InlineData("short-exit", true)]
    [InlineData("unlisted-activity", true)]
    [InlineData("identity", false)]
    [InlineData("stale-original", false)]
    [InlineData("short-handshake", false)]
    [InlineData("nonce", false)]
    [InlineData("busy", false)]
    [InlineData("cancel-exit", false)]
    [InlineData("cancel-exit", true)]
    public async Task Reserved_worker_refusal_never_invokes_boundary_or_replays(string variation, bool exitParent)
    {
        await using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var originals = new List<Task>();
        UpdateInstallationIngressLease otherReservation = null;
        try
        {
            var parent = fixture.Start("reserved-parent");
            var parentError = parent.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(parentError);
            var config = await ReadConfigAsync(parent, deadline.Token);
            var extra = fixture.Start("original-release-hold");
            var extraError = extra.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(extraError);
            Assert.Equal("ORIGINAL_ACTIVITY_HELD", await extra.StandardOutput.ReadLineAsync(deadline.Token));
            if (variation == "busy")
            {
                otherReservation = UpdateInstallationIngressLease.TryReserve(fixture.Executable);
                Assert.NotNull(otherReservation);
            }
            var worker = fixture.Start(WorkerArguments(config, variation == "unlisted-activity" ? null : extra,
                variation == "unlisted-activity" ? "success" : variation));
            var workerError = worker.StandardError.ReadToEndAsync(deadline.Token);
            var output = worker.StandardOutput.ReadToEndAsync(deadline.Token);
            originals.Add(workerError);
            originals.Add(output);
            if (variation is "short-exit" or "unlisted-activity" or "nonce" or "cancel-exit")
            {
                Send(parent, worker.Id + "|" + worker.StartTime.ToUniversalTime().Ticks);
                if (variation != "nonce")
                {
                    Assert.Equal("RESERVED_PARENT_ARMED", await parent.StandardOutput.ReadLineAsync(deadline.Token));
                    if (exitParent)
                    {
                        parent.StandardInput.Close();
                        await parent.WaitForExitAsync(deadline.Token);
                        Assert.Equal(0, parent.ExitCode);
                    }
                    if (variation == "cancel-exit") Send(worker, "cancel-original-wait");
                }
            }
            await worker.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, worker.ExitCode);
            var text = await output;
            Assert.Contains("INERT_BOUNDARY_CALLS|0", text);
            Assert.DoesNotContain("INERT_BOUNDARY_STARTED", text);
            Assert.DoesNotContain("RESERVED_ATTEMPT_RESULT|True", text);
            Assert.Contains(variation switch
            {
                "identity" or "stale-original" => "RESERVED_ATTEMPT_REJECTED|InvalidDataException",
                "short-handshake" or "cancel-exit" => "RESERVED_ATTEMPT_REJECTED|",
                "nonce" => "RESERVED_ATTEMPT_REJECTED|",
                _ => "RESERVED_ATTEMPT_RESULT|False",
            }, text);
            if (variation is "short-handshake" or "cancel-exit")
                Assert.True(text.Contains("RESERVED_ATTEMPT_REJECTED|OperationCanceledException", StringComparison.Ordinal)
                    || text.Contains("RESERVED_ATTEMPT_REJECTED|TaskCanceledException", StringComparison.Ordinal), text);
            Assert.False(extra.HasExited); // Unknown/alive extra never pretended to be idle.
            if (!exitParent && variation != "nonce") Assert.False(parent.HasExited);
            Assert.Equal("", await workerError);
            if (variation == "busy")
                Assert.Null(UpdateInstallationIngressLease.TryEnter(fixture.Executable));
            otherReservation?.Dispose();
            otherReservation = null;
            // After the refused ORIGINAL attempt ends, shared ingress resumes;
            // alive old originals still retain their scopes, not a fake exclusive lock.
            using var admitted = UpdateInstallationIngressLease.TryEnter(fixture.Executable);
            Assert.NotNull(admitted);
            parent.StandardInput.Close();
            extra.StandardInput.Close();
            await fixture.JoinAllAsync();
            Assert.Equal(0, extra.ExitCode);
            Assert.Equal("", await extraError);
            if (variation == "nonce")
                Assert.Contains("InvalidDataException", await parentError);
            else
            {
                Assert.Equal(0, parent.ExitCode);
                Assert.Equal("", await parentError);
            }
        }
        finally
        {
            fixture.ReleaseInputs();
            try { await fixture.JoinAllAsync(); }
            finally
            {
                otherReservation?.Dispose();
                deadline.Cancel();
                foreach (var original in originals) await ObserveAsync(original);
            }
        }
    }

    private static async Task<string[]> ReadConfigAsync(Process parent, CancellationToken token)
    {
        var line = await parent.StandardOutput.ReadLineAsync(token);
        Assert.NotNull(line);
        var fields = line.Split('|');
        Assert.Equal(8, fields.Length);
        Assert.Equal("RESERVED_PARENT", fields[0]);
        Assert.Equal(parent.Id.ToString(CultureInfo.InvariantCulture), fields[2]);
        Assert.Equal(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), fields[3]);
        return fields;
    }

    private static string[] WorkerArguments(string[] config, Process extra, string variation) =>
        ["reserved-worker", config[1], config[2], config[3], config[4], config[5], config[6], config[7],
            extra?.Id.ToString(CultureInfo.InvariantCulture) ?? "0",
            extra?.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? "0", variation];

    private static void Send(Process process, string line)
    {
        process.StandardInput.WriteLine(line);
        process.StandardInput.Flush();
    }

    [Fact]
    public async Task Actual_cold_process_entry_guards_GUI_native_and_restart_effects_until_release()
    {
        await using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors = new List<Task<string>>();
        var originals = new List<Task>();
        using var nativeFrame = new MemoryStream();
        try
        {
            foreach (var mode in new[] { "entry-gui-hold", "entry-native-hold" })
            {
                var process = fixture.Start(mode);
                errors.Add(process.StandardError.ReadToEndAsync(deadline.Token));
                originals.Add(errors[^1]);
                Assert.Equal("ENTRY_BODY|" + mode, await process.StandardOutput.ReadLineAsync(deadline.Token));
            }
            using (var reservation = UpdateInstallationIngressLease.TryReserve(fixture.Executable))
            {
                Assert.NotNull(reservation);
                Assert.False(reservation.TryAcquireExclusive());
                foreach (var mode in new[] { "entry-gui-probe", "entry-native-probe", "entry-restart-probe" })
                {
                    var denied = fixture.Start(mode);
                    var originalOutput = denied.StandardOutput.ReadToEndAsync(deadline.Token);
                    var originalError = denied.StandardError.ReadToEndAsync(deadline.Token);
                    originals.Add(originalOutput);
                    originals.Add(originalError);
                    await denied.WaitForExitAsync(deadline.Token);
                    Assert.Equal(1, denied.ExitCode);
                    Assert.Empty(await originalOutput);
                    Assert.StartsWith("ENTRY_REFUSED|", await originalError);
                    Assert.False(reservation.TryAcquireExclusive());
                }
                fixture.ReleaseInputs();
                await fixture.JoinAllAsync(); // Original OS exits, not lease release alone.
                Assert.True(reservation.TryAcquireExclusive());
            }
            var next = fixture.Start("entry-native-probe");
            var nextOutput = next.StandardOutput.BaseStream.CopyToAsync(nativeFrame, deadline.Token);
            var nextError = next.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(nextOutput);
            originals.Add(nextError);
            await next.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, next.ExitCode);
            Assert.Equal("", await nextError);
            await nextOutput;
            var frame = nativeFrame.ToArray();
            Assert.Equal("{\"inert\":\"native\"}", System.Text.Encoding.UTF8.GetString(frame.AsSpan(4)));
            Assert.Equal(frame.Length - 4, BitConverter.ToInt32(frame, 0));
            foreach (var original in errors) Assert.Equal("", await original);
        }
        finally
        {
            fixture.ReleaseInputs();
            try { await fixture.JoinAllAsync(); }
            finally
            {
                deadline.Cancel();
                foreach (var original in originals) await ObserveAsync(original);
            }
        }
    }

    [Fact]
    public async Task Actual_two_entrants_finish_while_reserved_new_ingress_remains_excluded()
    {
        await using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var originals = new List<Task>();
        var holders = new List<Process>();
        UpdateInstallationIngressLease reservation = null;
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var process = fixture.Start("ingress-hold");
                holders.Add(process);
                originals.Add(process.StandardError.ReadToEndAsync(deadline.Token));
                Assert.Equal("INGRESS_ACCEPTED", await process.StandardOutput.ReadLineAsync(deadline.Token));
                var identity = await process.StandardOutput.ReadLineAsync(deadline.Token);
                Assert.StartsWith("INSTALLATION_ID|", identity);
                Assert.Equal("INGRESS_HELD", await process.StandardOutput.ReadLineAsync(deadline.Token));
                Assert.False(process.HasExited);
            }
            reservation = UpdateInstallationIngressLease.TryReserve(fixture.Executable);
            Assert.NotNull(reservation);
            Assert.False(reservation.TryAcquireExclusive());
            await ProbeAsync("INGRESS_REFUSED");

            holders[0].StandardInput.Close();
            await holders[0].WaitForExitAsync(deadline.Token);
            Assert.Equal(0, holders[0].ExitCode);
            Assert.Contains("INGRESS_RELEASED", await holders[0].StandardOutput.ReadToEndAsync(deadline.Token));
            Assert.False(reservation.TryAcquireExclusive());
            await ProbeAsync("INGRESS_REFUSED");

            holders[1].StandardInput.Close();
            await holders[1].WaitForExitAsync(deadline.Token);
            Assert.Equal(0, holders[1].ExitCode);
            Assert.Contains("INGRESS_RELEASED", await holders[1].StandardOutput.ReadToEndAsync(deadline.Token));
            Assert.True(reservation.TryAcquireExclusive());
            await ProbeAsync("INGRESS_REFUSED");
            reservation.Dispose();
            await ProbeAsync("INGRESS_ACCEPTED");
            foreach (var error in originals.Cast<Task<string>>()) Assert.Equal("", await error);
        }
        finally
        {
            fixture.ReleaseInputs();
            await fixture.JoinAllAsync();
            reservation?.Dispose();
            deadline.Cancel();
            foreach (var original in originals) await ObserveAsync(original);
        }

        async Task ProbeAsync(string expected)
        {
            var probe = fixture.Start("ingress-probe");
            var output = probe.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = probe.StandardError.ReadToEndAsync(deadline.Token);
            originals.Add(errors);
            originals.Add(output);
            await probe.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, probe.ExitCode);
            var text = await output;
            Assert.StartsWith(expected + Environment.NewLine, text);
            Assert.Equal("", await errors);
            // Output tasks are already positively joined; do not treat their
            // content as stderr when asserting all retained error tasks above.
            originals.Remove(output);
        }
    }

    [Fact]
    public async Task Actual_GUID_local_fixture_runs_forty_exchanges_and_original_IO_refusal_cases()
    {
        await using var fixture = new Fixture();
        var process = fixture.Start("suite");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var text = await output;
            var error = await errors;
            Assert.True(process.ExitCode == 0, text + Environment.NewLine + error);
            Assert.Contains("CYCLES_PASS|40", text);
            foreach (var name in new[]
            {
                "drop-ready", "partial-ready", "drop-armed", "cancel-after-arm",
                "cancel-partial-prefix", "cancel-partial-payload", "reject-length", "reject-magic",
                "reject-version", "reject-phase", "reject-nonce", "reject-digest", "drop-arm",
                "cancel-await-arm", "wrong-arm-phase", "connect-timeout-consumed",
            })
                Assert.Contains("CASE_PASS|" + name, text);
            Assert.Contains("SUITE_PASS", text);
            Assert.DoesNotContain("INERT_FIXTURE_FAILED", error);
        }
        finally
        {
            fixture.ReleaseInputs();
            await fixture.JoinAllAsync();
            deadline.Cancel();
            await ObserveAsync(output);
            await ObserveAsync(errors);
        }
    }

    [Fact]
    public async Task Authenticated_worker_retains_original_parent_handle_until_actual_parent_exit()
    {
        await using var fixture = new Fixture();
        var parent = fixture.Start("parent");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors = parent.StandardError.ReadToEndAsync(deadline.Token);
        var lines = new List<string>();
        Process worker = null;
        var parentComplete = false;
        var originalWaitStarted = false;
        try
        {
            while (worker is null || !parentComplete || !originalWaitStarted)
            {
                // Await the ORIGINAL redirected IO. No detached timed waiter.
                var line = await parent.StandardOutput.ReadLineAsync(deadline.Token);
                Assert.NotNull(line);
                lines.Add(line);
                if (line.StartsWith("WORKER_PID|", StringComparison.Ordinal))
                    worker = fixture.ObserveWorker(int.Parse(line["WORKER_PID|".Length..], CultureInfo.InvariantCulture));
                if (line == "PARENT_EXCHANGE_COMPLETE") parentComplete = true;
                if (line == "WORKER_WAIT_STARTED|" + parent.Id.ToString(CultureInfo.InvariantCulture))
                    originalWaitStarted = true;
            }
            Assert.False(parent.HasExited);
            Assert.False(worker.HasExited);
            Assert.DoesNotContain("WORKER_PARENT_EXIT_VERIFIED", lines);
            parent.StandardInput.WriteLine();
            parent.StandardInput.Flush();
            parent.StandardInput.Close();
            await parent.WaitForExitAsync(deadline.Token);
            await worker.WaitForExitAsync(deadline.Token);
            var tail = await parent.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = await errors;
            Assert.True(parent.ExitCode == 0 && worker.ExitCode == 0,
                string.Join(Environment.NewLine, lines) + Environment.NewLine + tail + Environment.NewLine + error);
            Assert.Contains("PARENT_RELEASED", tail);
            Assert.Contains("WORKER_PARENT_EXIT_VERIFIED", tail);
            Assert.DoesNotContain("INERT_FIXTURE_FAILED", error);
        }
        finally
        {
            fixture.ReleaseInputs();
            await fixture.JoinAllAsync();
            deadline.Cancel();
            await ObserveAsync(errors);
        }
    }

    private static async Task ObserveAsync(Task original)
    {
        try { await original; }
        catch { /* Original IO cancellation/failure is observed during cleanup. */ }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<(Process Process, bool OwnInput)> _processes = [];
        private readonly string _tempParent = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private readonly string _name = "kwt-update-peer-" + Guid.NewGuid().ToString("N");
        private readonly string _root;
        private readonly string _executable;
        internal string Executable => _executable;

        internal Fixture()
        {
            var repository = FindRepository();
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var output = Path.Combine(repository, "tools", "KeyboardWtf.UpdateTestPeer", "bin",
                configuration, "net8.0-windows");
            var names = new[] { "keyboard.wtf.exe", "keyboard.wtf.dll", "keyboard.wtf.deps.json", "keyboard.wtf.runtimeconfig.json" };
            foreach (var name in names)
                Assert.True(File.Exists(Path.Combine(output, name)), "The separately built inert fixture is missing: " + name);
            _root = Path.GetFullPath(Path.Combine(_tempParent, _name));
            RequireOwnedRoot();
            Directory.CreateDirectory(_root);
            foreach (var name in names)
                File.Copy(Path.Combine(output, name), Path.Combine(_root, name), overwrite: false);
            _executable = Path.Combine(_root, "keyboard.wtf.exe");
        }

        internal Process Start(params string[] arguments)
        {
            var start = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var process = Process.Start(start);
            Assert.NotNull(process);
            _processes.Add((process, true));
            return process;
        }

        internal Process ObserveWorker(int processId)
        {
            var process = Process.GetProcessById(processId);
            _ = process.Handle; // Capture while parent remains held; positively join this original worker.
            _processes.Add((process, false));
            Assert.Equal(_executable, process.MainModule.FileName, StringComparer.OrdinalIgnoreCase);
            return process;
        }

        internal void ReleaseInputs()
        {
            foreach (var owned in _processes.Where(item => item.OwnInput))
            {
                try { owned.Process.StandardInput.Close(); }
                catch (IOException) { /* Already-exited fixture input is closed. */ }
                catch (ObjectDisposedException) { /* Release is once-only in effect. */ }
            }
        }

        internal async Task JoinAllAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            foreach (var owned in _processes)
                await owned.Process.WaitForExitAsync(deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseInputs();
            try
            {
                // A timeout is a failed cleanup, never proof that an owned child
                // exited. Preserve its fixture directory on failure; never kill.
                await JoinAllAsync();
            }
            catch
            {
                foreach (var owned in _processes) owned.Process.Dispose();
                throw;
            }
            foreach (var owned in _processes) owned.Process.Dispose();
            RequireOwnedRoot();
            // A signaled ORIGINAL process is not an immediate guarantee that
            // Windows has finished image-section teardown. Retry only deletion
            // of this validated, owned fixture AFTER every original is joined;
            // on unresolved lock/access failure preserve it and fail cleanup.
            for (var retry = 0; ; retry++)
            {
                try { Directory.Delete(_root, recursive: true); break; }
                catch (Exception error) when (retry < 19 && error is IOException or UnauthorizedAccessException)
                { await Task.Delay(50); }
            }
        }

        private void RequireOwnedRoot()
        {
            Assert.Equal(_tempParent, Path.GetDirectoryName(_root), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(_name, Path.GetFileName(_root));
            Assert.True(_name.StartsWith("kwt-update-peer-", StringComparison.Ordinal)
                && Guid.TryParseExact(_name["kwt-update-peer-".Length..], "N", out _));
        }

        private static string FindRepository()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "tools", "KeyboardWtf.UpdateTestPeer", "KeyboardWtf.UpdateTestPeer.csproj")))
                    return current.FullName;
            throw new InvalidOperationException("The inert fixture project was not found in this checkout.");
        }
    }
}
