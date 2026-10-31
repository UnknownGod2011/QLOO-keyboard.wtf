using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using KeyboardWtf.Services.Updates;

namespace KeyboardWtf.UpdateTestPeer;

// Inert console fixture only. No GUI, Settings, profile, provider, installer or
// native-host sources are linked. All execution is confined to a GUID temp root.
internal static class Program
{
    private static readonly TimeSpan ExchangeLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeerLimit = TimeSpan.FromSeconds(8);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            RequireFixtureDirectory();
            if (args.Length == 1 && args[0] == "suite")
                await RunSuiteAsync();
            else if (args.Length == 1 && args[0] == "parent")
                await RunParentAsync();
            else if (args.Length == 6 && args[0] == "worker")
                await RunWorkerAsync(args);
            else if (args.Length == 1 && args[0] == "reserved-parent")
                await RunReservedParentAsync();
            else if (args.Length == 1 && args[0] == "original-release-hold")
                await RunOriginalReleaseHoldAsync();
            else if (args.Length == 11 && args[0] == "reserved-worker")
                await RunReservedWorkerAsync(args);
            else if (args.Length == 6 && args[0] == "image-access-observer")
                await RunImageAccessObserverAsync(args);
            else if (args.Length == 1 && args[0] is "ingress-hold" or "ingress-probe")
                await RunIngressAsync(args[0]);
            else if (args.Length == 1 && args[0] is "entry-gui-hold" or "entry-native-hold"
                or "entry-gui-probe" or "entry-native-probe" or "entry-restart-probe")
                return RunEntry(args[0]);
            else
                throw new InvalidDataException("Unknown inert fixture mode.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("INERT_FIXTURE_FAILED|" + error);
            return 1;
        }
    }

    private static async Task RunSuiteAsync()
    {
        for (var cycle = 0; cycle < 40; cycle++)
        {
            var binding = Binding(CurrentIdentity(), Nonce());
            using var self = UpdateProcessIdentityLease.Acquire(binding.Parent, Environment.ProcessPath);
            using var server = UpdateWorkerHandshake.CreateParentPipe(out var name);
            using var client = Client(name);
            var parent = new UpdateWorkerHandshake(binding);
            var worker = new UpdateWorkerHandshake(binding);
            var parentOriginal = parent.ExchangeParentAsync(server, self, ExchangeLimit, CancellationToken.None);
            var workerOriginal = worker.ExchangeWorkerAsync(client, self, ExchangeLimit, CancellationToken.None);
            await Task.WhenAll(parentOriginal, workerOriginal);
            Check(!self.HasExited, "Loopback exchange manufactured process exit.");
            await MustRejectAsync(parent.ExchangeParentAsync(server, self, ExchangeLimit, CancellationToken.None),
                typeof(InvalidOperationException));
            await MustRejectAsync(worker.ExchangeWorkerAsync(client, self, ExchangeLimit, CancellationToken.None),
                typeof(InvalidOperationException));
        }
        Print("CYCLES_PASS|40");

        await ParentAttackAsync("drop-ready", async (_, client, _, token) =>
        {
            await Task.CompletedTask;
            client.Dispose();
        }, typeof(EndOfStreamException));
        await ParentAttackAsync("partial-ready", async (binding, client, _, token) =>
        {
            var packet = Packet(binding.CreateFrame(1));
            await client.WriteAsync(packet.AsMemory(0, 12), token);
            await client.FlushAsync(token);
            client.Dispose();
        }, typeof(EndOfStreamException));
        await ParentAttackAsync("drop-armed", async (binding, client, _, token) =>
        {
            await SendAsync(client, binding.CreateFrame(1), token);
            await ReceiveAsync(client, binding, 2, token);
            client.Dispose();
        }, typeof(EndOfStreamException));
        await ParentAttackAsync("cancel-after-arm", async (binding, client, cancellation, token) =>
        {
            await SendAsync(client, binding.CreateFrame(1), token);
            await ReceiveAsync(client, binding, 2, token);
            cancellation.Cancel();
        }, typeof(OperationCanceledException));
        await ParentAttackAsync("cancel-partial-prefix", async (_, client, cancellation, token) =>
        {
            await client.WriteAsync(new byte[] { 0 }, token);
            await client.FlushAsync(token);
            cancellation.Cancel();
        }, typeof(OperationCanceledException));
        await ParentAttackAsync("cancel-partial-payload", async (binding, client, cancellation, token) =>
        {
            await client.WriteAsync(Packet(binding.CreateFrame(1)).AsMemory(0, 12), token);
            await client.FlushAsync(token);
            cancellation.Cancel();
        }, typeof(OperationCanceledException));

        foreach (var attack in new[] { "length", "magic", "version", "phase", "nonce", "digest" })
        {
            await ParentAttackAsync("reject-" + attack, async (binding, client, _, token) =>
            {
                var frame = binding.CreateFrame(1);
                if (attack == "magic") frame[0] ^= 1;
                else if (attack == "version") frame[4] = 2;
                else if (attack == "phase") frame[5] = 3;
                else if (attack == "nonce") frame[6] ^= 1;
                else if (attack == "digest") frame[38] ^= 1;
                var packet = Packet(frame);
                if (attack == "length") BinaryPrimitives.WriteUInt16BigEndian(packet, ushort.MaxValue);
                // A refused length means the parent intentionally never reads
                // a payload. Write ONLY that prefix; awaiting an unread payload
                // on a zero-buffer Windows pipe would deadlock this test peer.
                await client.WriteAsync(packet.AsMemory(0, attack == "length" ? 2 : packet.Length), token);
                await client.FlushAsync(token);
            }, typeof(InvalidDataException));
        }

        await WorkerAttackAsync("drop-arm", async (_, server, _, _) =>
        {
            await Task.CompletedTask;
            server.Dispose();
        }, typeof(EndOfStreamException));
        await WorkerAttackAsync("cancel-await-arm", async (_, _, cancellation, _) =>
        {
            await Task.CompletedTask;
            cancellation.Cancel();
        }, typeof(OperationCanceledException));
        await WorkerAttackAsync("wrong-arm-phase", async (binding, server, _, token) =>
        {
            await SendAsync(server, binding.CreateFrame(3), token);
        }, typeof(InvalidDataException));

        // A deadline cancels the ORIGINAL unconnected operation; it is not a
        // separately timed waiter which could leave the original task behind.
        var timeoutBinding = Binding(CurrentIdentity(), Nonce());
        using (var self = UpdateProcessIdentityLease.Acquire(timeoutBinding.Parent, Environment.ProcessPath))
        using (var server = UpdateWorkerHandshake.CreateParentPipe(out _))
        {
            var attempt = new UpdateWorkerHandshake(timeoutBinding);
            await MustRejectAsync(attempt.ExchangeParentAsync(server, self,
                TimeSpan.FromMilliseconds(80), CancellationToken.None), typeof(OperationCanceledException));
            await MustRejectAsync(attempt.ExchangeParentAsync(server, self,
                ExchangeLimit, CancellationToken.None), typeof(InvalidOperationException));
        }
        Print("CASE_PASS|connect-timeout-consumed");
        Print("SUITE_PASS");
    }

    private static async Task RunIngressAsync(string mode)
    {
        using var entry = UpdateInstallationIngressLease.TryEnter(Environment.ProcessPath);
        if (entry is null)
        {
            Print("INGRESS_REFUSED");
            return;
        }
        Print("INGRESS_ACCEPTED");
        Print("INSTALLATION_ID|" + entry.InstallationIdentity);
        if (mode == "ingress-hold")
        {
            Print("INGRESS_HELD");
            await Console.In.ReadLineAsync();
            Print("INGRESS_RELEASED");
        }
    }

    private static int RunEntry(string mode)
    {
        // Same admission boundary, synthetic body ONLY. A refused native-mode
        // probe emits no stdout/frame and can never auto-start a GUI.
        var accepted = UpdateProcessEntry.Run(Environment.ProcessPath, () =>
        {
            if (mode == "entry-native-probe")
            {
                using var output = Console.OpenStandardOutput();
                var payload = System.Text.Encoding.UTF8.GetBytes("{\"inert\":\"native\"}");
                output.Write(BitConverter.GetBytes(payload.Length));
                output.Write(payload);
                output.Flush();
                return;
            }
            Print("ENTRY_BODY|" + mode);
            if (mode.EndsWith("hold", StringComparison.Ordinal)) Console.In.ReadLine();
        }, refusal => Console.Error.WriteLine("ENTRY_REFUSED|" + refusal));
        return accepted ? 0 : 1;
    }

    private static async Task ParentAttackAsync(string label,
        Func<UpdateWorkerHandshakeBinding, NamedPipeClientStream, CancellationTokenSource, CancellationToken, Task> attack,
        Type refusal)
    {
        var binding = Binding(CurrentIdentity(), Nonce());
        using var self = UpdateProcessIdentityLease.Acquire(binding.Parent, Environment.ProcessPath);
        using var server = UpdateWorkerHandshake.CreateParentPipe(out var name);
        using var client = Client(name);
        using var cancellation = new CancellationTokenSource();
        using var peerDeadline = new CancellationTokenSource(PeerLimit);
        var attempt = new UpdateWorkerHandshake(binding);
        await Task.WhenAll(server.WaitForConnectionAsync(peerDeadline.Token), client.ConnectAsync(peerDeadline.Token));
        // With both endpoints connected, the original method synchronously
        // authenticates and reaches its first pending prefix read. Partial IO
        // cancellation below is therefore after that original read has started,
        // not merely cancellation of a still-pending connection.
        var original = attempt.ExchangeParentAsync(server, self, ExchangeLimit, cancellation.Token);
        try
        {
            Check(!original.IsCompleted, "Parent accepted without READY and ARMED.");
            await MustRejectAsync(attempt.ExchangeParentAsync(server, self, ExchangeLimit, CancellationToken.None),
                typeof(InvalidOperationException));
            await attack(binding, client, cancellation, peerDeadline.Token);
            await MustRejectAsync(original, refusal);
            await MustRejectAsync(attempt.ExchangeParentAsync(server, self, ExchangeLimit, CancellationToken.None),
                typeof(InvalidOperationException));
            Check(!self.HasExited, "Rejected parent exchange manufactured exit.");
        }
        finally
        {
            cancellation.Cancel();
            client.Dispose();
            await ObserveAsync(original);
        }
        Print("CASE_PASS|" + label);
    }

    private static async Task WorkerAttackAsync(string label,
        Func<UpdateWorkerHandshakeBinding, NamedPipeServerStream, CancellationTokenSource, CancellationToken, Task> attack,
        Type refusal)
    {
        var binding = Binding(CurrentIdentity(), Nonce());
        using var self = UpdateProcessIdentityLease.Acquire(binding.Parent, Environment.ProcessPath);
        using var server = UpdateWorkerHandshake.CreateParentPipe(out var name);
        using var client = Client(name);
        using var cancellation = new CancellationTokenSource();
        using var peerDeadline = new CancellationTokenSource(PeerLimit);
        var attempt = new UpdateWorkerHandshake(binding);
        var original = attempt.ExchangeWorkerAsync(client, self, ExchangeLimit, cancellation.Token);
        try
        {
            await server.WaitForConnectionAsync(peerDeadline.Token);
            await ReceiveAsync(server, binding, 1, peerDeadline.Token);
            Check(!original.IsCompleted, "Worker accepted without ARM.");
            await MustRejectAsync(attempt.ExchangeWorkerAsync(client, self, ExchangeLimit, CancellationToken.None),
                typeof(InvalidOperationException));
            await attack(binding, server, cancellation, peerDeadline.Token);
            await MustRejectAsync(original, refusal);
            await MustRejectAsync(attempt.ExchangeWorkerAsync(client, self, ExchangeLimit, CancellationToken.None),
                typeof(InvalidOperationException));
            Check(!self.HasExited, "Rejected worker exchange manufactured exit.");
        }
        finally
        {
            cancellation.Cancel();
            server.Dispose();
            await ObserveAsync(original);
        }
        Print("CASE_PASS|" + label);
    }

    private static async Task RunParentAsync()
    {
        var identity = CurrentIdentity();
        var nonce = Nonce();
        var binding = Binding(identity, nonce);
        using var server = UpdateWorkerHandshake.CreateParentPipe(out var name);
        var start = new ProcessStartInfo(Environment.ProcessPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            // Redirecting this unused input makes inherited stdout/stderr
            // explicit. The test runner can observe the worker after parent exit.
            RedirectStandardInput = true,
        };
        foreach (var arg in new[] { "worker", name, identity.ProcessId.ToString(CultureInfo.InvariantCulture),
            identity.CreationTimeUtcTicks.ToString(CultureInfo.InvariantCulture), identity.ExecutablePath, nonce })
            start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("No inert worker was created.");
        Print("WORKER_PID|" + worker.Id.ToString(CultureInfo.InvariantCulture));
        var workerIdentity = new DeclaredUpdateParent(worker.Id, worker.StartTime.ToUniversalTime().Ticks,
            Environment.ProcessPath);
        using var knownWorker = UpdateProcessIdentityLease.Acquire(workerIdentity, Environment.ProcessPath);
        await new UpdateWorkerHandshake(binding).ExchangeParentAsync(server, knownWorker, ExchangeLimit, CancellationToken.None);
        Print("PARENT_EXCHANGE_COMPLETE");
        // EOF is also cooperative release, so test failure/cleanup cannot leave
        // this deliberately held parent orphaned. No process is force-killed.
        await Console.In.ReadLineAsync();
        Print("PARENT_RELEASED");
    }

    private static async Task RunWorkerAsync(string[] args)
    {
        if (!args[1].StartsWith("kwt-update-worker-", StringComparison.Ordinal)
            || args[1].Length != "kwt-update-worker-".Length + 32
            || !args[1].AsSpan("kwt-update-worker-".Length).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("The inert worker pipe name is invalid.");
        var declared = new DeclaredUpdateParent(int.Parse(args[2], CultureInfo.InvariantCulture),
            long.Parse(args[3], CultureInfo.InvariantCulture), args[4]);
        // Both inert peers execute this SAME GUID-local test binary. This is an
        // independent trusted target, not a synthetic GUI path accepted as OS proof.
        using var parent = UpdateProcessIdentityLease.Acquire(declared, Environment.ProcessPath);
        var binding = Binding(declared, args[5]);
        using var client = Client(args[1]);
        await new UpdateWorkerHandshake(binding).ExchangeWorkerAsync(client, parent, ExchangeLimit, CancellationToken.None);
        var originalWait = parent.WaitForExitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        Check(!originalWait.IsCompleted, "The fixture did not observe a held original parent wait.");
        Print("WORKER_WAIT_STARTED|" + parent.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (!await originalWait)
            throw new TimeoutException("The held parent did not positively exit; timeout is not completion.");
        Print("WORKER_PARENT_EXIT_VERIFIED");
    }

    private static async Task RunOriginalReleaseHoldAsync()
    {
        using var activity = UpdateInstallationIngressLease.TryEnter(Environment.ProcessPath);
        Check(activity is not null, "The inert original was refused admission.");
        Print("ORIGINAL_ACTIVITY_HELD");
        await Console.In.ReadLineAsync();
        activity.Dispose();
        Print("ORIGINAL_ACTIVITY_RELEASED_OS_ALIVE");
        // Explicitly distinguish scope completion from ORIGINAL OS exit.
        await Console.In.ReadLineAsync();
        Print("ORIGINAL_EXITING");
    }

    private static async Task RunImageAccessObserverAsync(string[] args)
    {
        // This characterization intentionally runs from a DIFFERENT inert image.
        // A supplied real app/install path is never accepted, even for reading.
        var target = RequireDifferentFixtureImage(args[1]);
        var firstDeclaration = new DeclaredUpdateParent(int.Parse(args[2], CultureInfo.InvariantCulture),
            long.Parse(args[3], CultureInfo.InvariantCulture), target);
        var secondDeclaration = new DeclaredUpdateParent(int.Parse(args[4], CultureInfo.InvariantCulture),
            long.Parse(args[5], CultureInfo.InvariantCulture), target);
        Check(firstDeclaration.ProcessId != secondDeclaration.ProcessId
            && firstDeclaration.ProcessId != Environment.ProcessId && secondDeclaration.ProcessId != Environment.ProcessId,
            "The inert observer needs two distinct target originals, not itself.");
        using var first = UpdateProcessIdentityLease.Acquire(firstDeclaration, target);
        using var second = UpdateProcessIdentityLease.Acquire(secondDeclaration, target);
        using var self = UpdateProcessIdentityLease.Acquire(CurrentIdentity(), Environment.ProcessPath);
        Print("IMAGE_OBSERVER_READY");

        while (await Console.In.ReadLineAsync() is { } stage)
        {
            Check(stage is "before-exit" or "scope-released" or "one-exited" or "both-exited"
                or "held-file-lock" or "released-file-lock", "Unknown inert image probe stage.");
            Check(!self.HasExited, "The observer's original identity was lost.");
            var firstExited = first.HasExited;
            var secondExited = second.HasExited;
            Check(stage switch
            {
                "before-exit" or "scope-released" => !firstExited && !secondExited,
                "one-exited" => firstExited && !secondExited,
                _ => firstExited && secondExited,
            }, "The probe stage did not match the same original process handles.");

            var outcome = "PERMITTED";
            var lowWin32 = 0;
            var hresult = 0;
            try
            {
                // Existing image, requested access only. Never write bytes,
                // change metadata, truncate, rename, replace or delete anything.
                using var probe = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                outcome = "REFUSED";
                hresult = error.HResult;
                // Preserve non-Win32 HRESULTs as unknown, not invented error32.
                lowWin32 = (unchecked((uint)hresult) & 0xffff0000u) == 0x80070000u
                    ? hresult & 0xffff : -1;
            }
            Print(string.Join('|', "IMAGE_ACCESS", stage, firstExited, secondExited, outcome,
                lowWin32.ToString(CultureInfo.InvariantCulture), "0x" + unchecked((uint)hresult).ToString("X8", CultureInfo.InvariantCulture)));
        }
        Print("IMAGE_OBSERVER_DONE");
    }

    private static string RequireDifferentFixtureImage(string path)
    {
        UpdatePathIdentity.RequireCanonicalLocalPath(path);
        const string prefix = "kwt-update-peer-";
        var root = Path.GetDirectoryName(path);
        var name = Path.GetFileName(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Check(string.Equals(Path.GetFileName(path), "keyboard.wtf.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[prefix.Length..], "N", out _)
            && !string.Equals(root, Path.GetDirectoryName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase),
            "The image probe accepts only a different GUID-local inert fixture image.");
        // Only after structural confinement succeeds may these existing objects
        // be inspected. Do not follow an owned root/image replaced by a reparse.
        Check((File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory
            && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0,
            "The inert image probe target is not an ordinary fixture image.");
        return path;
    }

    private static async Task RunReservedParentAsync()
    {
        using var activity = UpdateInstallationIngressLease.TryEnter(Environment.ProcessPath);
        Check(activity is not null, "The inert parent was refused admission.");
        var identity = CurrentIdentity();
        var nonce = Nonce();
        var binding = Binding(identity, nonce);
        using var server = UpdateWorkerHandshake.CreateParentPipe(out var name);
        var physical = activity.InstallationIdentity;
        Print(string.Join('|', "RESERVED_PARENT", name, identity.ProcessId, identity.CreationTimeUtcTicks,
            nonce, physical.VolumeSerialNumber, physical.Low, physical.High));
        var workerLine = await Console.In.ReadLineAsync();
        if (workerLine is null) return; // Cooperative cleanup before a worker is declared.
        var fields = workerLine.Split('|');
        Check(fields.Length == 2, "The fixture worker declaration is invalid.");
        var declared = new DeclaredUpdateParent(int.Parse(fields[0], CultureInfo.InvariantCulture),
            long.Parse(fields[1], CultureInfo.InvariantCulture), Environment.ProcessPath);
        using var worker = UpdateProcessIdentityLease.Acquire(declared, Environment.ProcessPath);
        await new UpdateWorkerHandshake(binding).ExchangeParentAsync(server, worker, ExchangeLimit, CancellationToken.None);
        Print("RESERVED_PARENT_ARMED");
        await Console.In.ReadLineAsync();
        activity.Dispose();
        Print("PARENT_ACTIVITY_RELEASED_OS_ALIVE");
        await Console.In.ReadLineAsync();
        Print("RESERVED_PARENT_EXITING");
    }

    private static async Task RunReservedWorkerAsync(string[] args)
    {
        // Configuration comes from the owning test runner/fixture, NOT from pipe
        // frames. Neither an app path nor real installer is accepted or launched.
        var parent = new DeclaredUpdateParent(int.Parse(args[2], CultureInfo.InvariantCulture),
            long.Parse(args[3], CultureInfo.InvariantCulture), Environment.ProcessPath);
        var physical = new UpdateInstallationIngressLease.InstallationFileIdentity(
            ulong.Parse(args[5], CultureInfo.InvariantCulture), ulong.Parse(args[6], CultureInfo.InvariantCulture),
            ulong.Parse(args[7], CultureInfo.InvariantCulture));
        var additional = int.Parse(args[8], CultureInfo.InvariantCulture) == 0 ? Array.Empty<DeclaredUpdateParent>()
            : [new DeclaredUpdateParent(int.Parse(args[8], CultureInfo.InvariantCulture),
                long.Parse(args[9], CultureInfo.InvariantCulture), Environment.ProcessPath)];
        var variation = args[10];
        Check(variation is "success" or "callback-cancel" or "callback-fault" or "callback-null"
            or "short-exit" or "short-handshake" or "identity" or "nonce" or "stale-original" or "busy" or "cancel-exit",
            "Unknown inert reserved variation.");
        if (variation == "identity") physical = physical with { Low = physical.Low ^ 1 };
        if (variation == "stale-original") additional[0] = additional[0] with
            { CreationTimeUtcTicks = additional[0].CreationTimeUtcTicks + 1 };
        var nonce = variation == "nonce" ? new string(args[4][0] == 'A' ? 'B' : 'A', 64) : args[4];
        using var client = Client(args[1]);
        using var cancellation = new CancellationTokenSource();
        var attempt = new ReservedUpdateWorkerAttempt(Binding(parent, nonce), physical);
        var calls = 0;
        Func<Task> boundary = variation == "callback-null" ? () =>
        {
            calls++;
            Print("INERT_BOUNDARY_NULL");
            return null;
        } : async () =>
        {
            calls++;
            if (variation == "callback-cancel") cancellation.Cancel();
            Print("INERT_BOUNDARY_STARTED");
            // EOF is cooperative release, never force-kill or detached work.
            await Console.In.ReadLineAsync();
            if (variation == "callback-fault") throw new InvalidDataException("INERT boundary fault.");
            Print("INERT_BOUNDARY_COMPLETED");
        };
        Task<bool> original = null;
        try
        {
            original = attempt.RunAsync(client, additional,
                variation == "short-handshake" ? TimeSpan.FromMilliseconds(200) : ExchangeLimit,
                variation == "short-exit" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(20),
                boundary, cancellation.Token);
            Print("RESERVED_ATTEMPT_STARTED");
            if (variation is "success" or "callback-cancel" or "callback-fault" or "callback-null")
            {
                Check(!original.IsCompleted, "The original attempt was not held before parent exchange.");
                await MustRejectAsync(attempt.RunAsync(client, [], ExchangeLimit, PeerLimit,
                    boundary, CancellationToken.None), typeof(InvalidOperationException));
                Print("CONCURRENT_ATTEMPT_REFUSED");
            }
            if (variation == "cancel-exit")
            {
                // Owning test sends cancellation only AFTER authenticated ARM
                // and at the chosen positive-original-exit stage. EOF cleans up.
                await Console.In.ReadLineAsync();
                cancellation.Cancel();
            }
            Print("RESERVED_ATTEMPT_RESULT|" + await original);
        }
        catch (Exception error) when (error is InvalidDataException or OperationCanceledException
            or EndOfStreamException or Win32Exception or IOException or InvalidOperationException)
        {
            // These are observed refusal/fault cases, NOT fake install success.
            Print("RESERVED_ATTEMPT_REJECTED|" + error.GetType().Name);
        }
        finally
        {
            // Even diagnostic stdout failure must not detach the original run.
            if (original is not null) await ObserveAsync(original);
        }
        await MustRejectAsync(attempt.RunAsync(client, [], ExchangeLimit, PeerLimit,
            boundary, CancellationToken.None), typeof(InvalidOperationException));
        Print("INERT_BOUNDARY_CALLS|" + calls);
    }

    private static UpdateWorkerHandshakeBinding Binding(DeclaredUpdateParent parent, string nonce)
    {
        // Syntactically valid fixture metadata only. No file is created at the
        // installer path and no manifest, package or installer is executed.
        var update = new VerifiedUpdate(new Version(99, 0, 1), new Version(0, 0, 1), "stable",
            new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
            new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/inert/fixture.bin"),
            new string('a', 64), 1, "INERT TEST DATA ONLY", "inert-fixture-key");
        var prepared = new PreparedUpdate(update,
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath), "99.0.1", "keyboard.wtf-setup.exe"));
        return UpdateWorkerHandshakeBinding.Create(
            UpdateInstallerInvocation.Create(prepared, parent, Environment.ProcessPath), nonce);
    }

    private static DeclaredUpdateParent CurrentIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath);
    }

    private static NamedPipeClientStream Client(string name) => new(".", name, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static string Nonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static byte[] Packet(byte[] frame)
    {
        var packet = new byte[frame.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)frame.Length);
        frame.CopyTo(packet, 2);
        return packet;
    }

    private static async Task SendAsync(Stream pipe, byte[] frame, CancellationToken token)
    {
        await pipe.WriteAsync(Packet(frame), token);
        await pipe.FlushAsync(token);
    }

    private static async Task ReceiveAsync(Stream pipe, UpdateWorkerHandshakeBinding binding, byte phase,
        CancellationToken token)
    {
        var prefix = new byte[2];
        await pipe.ReadExactlyAsync(prefix, token);
        Check(BinaryPrimitives.ReadUInt16BigEndian(prefix) == UpdateWorkerHandshakeBinding.FrameLength,
            "Fixture received an invalid frame length.");
        var frame = new byte[UpdateWorkerHandshakeBinding.FrameLength];
        await pipe.ReadExactlyAsync(frame, token);
        binding.ValidateFrame(frame, phase);
    }

    private static async Task MustRejectAsync(Task original, Type refusal)
    {
        Exception observed = null;
        try { await original; }
        catch (Exception error) { observed = error; }
        // Immediate remote close can reach OS peer authentication before frame
        // reading. Accept only the narrow real disconnect variants, not any
        // arbitrary error which could conceal a fixture/protocol defect.
        var expected = observed is not null && (refusal.IsInstanceOfType(observed)
            || (refusal == typeof(EndOfStreamException) && observed is IOException or Win32Exception));
        Check(expected,
            "Expected " + refusal.Name + ", observed " + (observed?.GetType().Name ?? "successful exchange") + ".");
    }

    private static async Task ObserveAsync(Task original)
    {
        try { await original; }
        catch { /* Failure already asserted; positively observe original drainage. */ }
    }

    private static void Print(string value)
    {
        Console.WriteLine(value);
        Console.Out.Flush();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireFixtureDirectory()
    {
        var root = Path.GetDirectoryName(Environment.ProcessPath);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        const string prefix = "kwt-update-peer-";
        var name = Path.GetFileName(root);
        Check(string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[prefix.Length..], "N", out _),
            "The inert fixture may run only in its own GUID-local temp directory.");
    }
}
