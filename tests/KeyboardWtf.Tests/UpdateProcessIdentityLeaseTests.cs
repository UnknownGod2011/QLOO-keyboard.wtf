using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Real Windows handles and GUID-local pipes; never launches keyboard.wtf, a
// provider, an installer or native-host/GUI code. Missing binding fails BEFORE
// any child process or pipe is created. These are primitive, not update tests.
public sealed class UpdateProcessIdentityLeaseTests
{
    [Fact]
    public void Current_process_identity_is_verified_and_read_only_across_forty_cycles()
    {
        var binding = Binding.Require();
        var identity = CurrentIdentity();
        for (var index = 0; index < 40; index++)
        {
            using var lease = binding.Acquire(identity);
            Assert.Equal(identity.ProcessId, lease.Read<int>("ProcessId"));
            Assert.Equal(identity.CreationTimeUtcTicks, lease.Read<long>("CreationTimeUtcTicks"));
            Assert.Equal(identity.ExecutablePath, lease.Read<string>("ExecutablePath"), StringComparer.OrdinalIgnoreCase);
            Assert.False(lease.Read<bool>("HasExited"));
        }
    }

    [Fact]
    public void Trusted_path_spelling_is_case_insensitive_but_not_a_different_executable()
    {
        var binding = Binding.Require();
        var identity = CurrentIdentity();
        using var lease = binding.Acquire(identity with { ExecutablePath = identity.ExecutablePath.ToUpperInvariant() });
        Assert.False(lease.Read<bool>("HasExited"));
        Assert.Throws<InvalidDataException>(() => binding.Acquire(identity with
        {
            ExecutablePath = Path.Combine(Path.GetDirectoryName(identity.ExecutablePath), "not-this-process.exe"),
        }));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1L)]
    public void Same_pid_and_path_with_different_creation_time_is_not_the_parent(long offset)
    {
        var binding = Binding.Require();
        var identity = CurrentIdentity();
        Assert.Throws<InvalidDataException>(() => binding.Acquire(identity with
        {
            CreationTimeUtcTicks = identity.CreationTimeUtcTicks + offset,
        }));
    }

    [Theory]
    [InlineData(0, 1, "C:\\synthetic.exe")]
    [InlineData(-1, 1, "C:\\synthetic.exe")]
    [InlineData(1, 0, "C:\\synthetic.exe")]
    [InlineData(1, -1, "C:\\synthetic.exe")]
    [InlineData(1, long.MaxValue, "C:\\synthetic.exe")]
    [InlineData(1, 1, "relative.exe")]
    [InlineData(1, 1, "C:\\..\\synthetic.exe")]
    public void Invalid_declarations_fail_before_any_process_query(int pid, long ticks, string path)
    {
        var binding = Binding.Require();
        Assert.Throws<InvalidDataException>(() => binding.Acquire(new(pid, ticks, path)));
    }

    [Fact]
    public void Null_declaration_is_not_identity_proof()
    {
        var binding = Binding.Require();
        Assert.Throws<ArgumentNullException>(() => binding.Acquire(null));
    }

    [Fact]
    public void Declared_path_cannot_replace_the_independent_trusted_process_path()
    {
        var binding = Binding.Require();
        var identity = CurrentIdentity();
        var different = identity with { ExecutablePath = "C:\\Synthetic\\other.exe" };
        Assert.Throws<InvalidDataException>(() => binding.Acquire(different, identity.ExecutablePath));
        Assert.Throws<ArgumentNullException>(() => binding.Acquire(identity, null));
    }

    [Fact]
    public async Task Timeout_or_cancellation_is_never_positive_parent_exit()
    {
        var binding = Binding.Require();
        using var lease = binding.Acquire(CurrentIdentity());
        Assert.False(await lease.Wait(TimeSpan.FromMilliseconds(15), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.Wait(TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.False(lease.Read<bool>("HasExited"));
        Assert.False(await lease.Wait(TimeSpan.FromMilliseconds(15), CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(300001)]
    public async Task Exit_wait_requires_a_finite_positive_updater_bound(int milliseconds)
    {
        var binding = Binding.Require();
        using var lease = binding.Acquire(CurrentIdentity());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lease.Wait(TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None));
    }

    [Fact]
    public async Task Held_original_handle_signals_only_after_the_owned_child_really_exits()
    {
        var binding = Binding.Require();
        using var child = Child.Start();
        using var lease = binding.Acquire(child.Identity);
        Assert.False(await lease.Wait(TimeSpan.FromMilliseconds(20), CancellationToken.None));
        var originalWait = lease.Wait(TimeSpan.FromSeconds(8), CancellationToken.None);
        Assert.False(originalWait.IsCompleted);
        child.Release();
        Assert.True(await originalWait);
        Assert.True(lease.Read<bool>("HasExited"));
    }

    [Fact]
    public async Task Disposing_public_lease_cannot_close_the_handle_beneath_an_original_wait()
    {
        var binding = Binding.Require();
        using var child = Child.Start();
        using var lease = binding.Acquire(child.Identity);
        var originalWait = lease.Wait(TimeSpan.FromSeconds(8), CancellationToken.None);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Read<bool>("HasExited"));
        child.Release();
        Assert.True(await originalWait);
    }

    [Fact]
    public async Task Cancellation_after_an_original_wait_starts_does_not_manufacture_exit_or_poison_the_handle()
    {
        var binding = Binding.Require();
        using var child = Child.Start();
        using var lease = binding.Acquire(child.Identity);
        using var cancellation = new CancellationTokenSource();
        var original = lease.Wait(TimeSpan.FromSeconds(8), cancellation.Token);
        Assert.False(original.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
        Assert.False(lease.Read<bool>("HasExited"));
        child.Release();
        Assert.True(await lease.Wait(TimeSpan.FromSeconds(8), CancellationToken.None));
    }

    [Fact]
    public void Already_exited_declared_process_cannot_become_ready()
    {
        var binding = Binding.Require();
        using var child = Child.Start();
        var identity = child.Identity;
        child.Release();
        Assert.True(child.Process.WaitForExit(8000));
        var error = Record.Exception(() => binding.Acquire(identity));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Both_pipe_directions_query_the_connected_OS_peer_not_a_claimed_pid()
    {
        var binding = Binding.Require();
        using var expected = binding.Acquire(CurrentIdentity());
        using var pipes = await Pipes.Connect();
        expected.Authenticate("AuthenticateClient", pipes.Server);
        expected.Authenticate("AuthenticateServer", pipes.Client);
        using var child = Child.Start();
        using var wrong = binding.Acquire(child.Identity);
        Assert.Throws<InvalidDataException>(() => wrong.Authenticate("AuthenticateClient", pipes.Server));
        Assert.Throws<InvalidDataException>(() => wrong.Authenticate("AuthenticateServer", pipes.Client));
    }

    [Fact]
    public void Unconnected_or_disposed_pipe_is_not_peer_readiness()
    {
        var binding = Binding.Require();
        using var lease = binding.Acquire(CurrentIdentity());
        using var server = new NamedPipeServerStream("kwt-update-identity-" + Guid.NewGuid().ToString("N"),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        Assert.Throws<InvalidOperationException>(() => lease.Authenticate("AuthenticateClient", server));
        server.Dispose();
        Assert.NotNull(Record.Exception(() => lease.Authenticate("AuthenticateClient", server)));
    }

    [Fact]
    public async Task Real_cross_process_pipe_client_matches_its_held_process_identity()
    {
        var binding = Binding.Require();
        var name = "kwt-update-identity-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        using var child = Child.Start("$p=[IO.Pipes.NamedPipeClientStream]::new('.', '" + name
            + "', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous);"
            + "$p.Connect(5000);[Console]::In.ReadLine()|Out-Null;$p.Dispose()");
        using var lease = binding.Acquire(child.Identity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await server.WaitForConnectionAsync(timeout.Token);
        lease.Authenticate("AuthenticateClient", server);
        using var wrong = binding.Acquire(CurrentIdentity());
        Assert.Throws<InvalidDataException>(() => wrong.Authenticate("AuthenticateClient", server));
    }

    [Fact]
    public async Task Real_cross_process_pipe_server_is_not_mistaken_for_the_client_process()
    {
        var binding = Binding.Require();
        var name = "kwt-update-identity-" + Guid.NewGuid().ToString("N");
        using var child = Child.Start("$opts=[IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly;"
            + "$p=[IO.Pipes.NamedPipeServerStream]::new('" + name
            + "',[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,$opts);"
            + "if(-not $p.WaitForConnectionAsync().Wait(5000)){$p.Dispose();exit 1};"
            + "[Console]::In.ReadLine()|Out-Null;$p.Dispose()");
        using var expected = binding.Acquire(child.Identity);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(timeout.Token);
        expected.Authenticate("AuthenticateServer", client);
        using var wrong = binding.Acquire(CurrentIdentity());
        Assert.Throws<InvalidDataException>(() => wrong.Authenticate("AuthenticateServer", client));
    }

    [Fact]
    public void Handle_primitives_cannot_activate_installation()
    {
        var ready = typeof(WebSettingsService).GetProperty("SafeUpdateInstallationReady", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ready);
        Assert.False((bool)ready.GetValue(null));
    }

    private static DeclaredUpdateParent CurrentIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath);
    }

    private sealed class Child : IDisposable
    {
        internal Process Process { get; }
        internal DeclaredUpdateParent Identity { get; }
        private bool _released;
        private Child(Process process, string path)
        {
            Process = process;
            Identity = new(process.Id, process.StartTime.ToUniversalTime().Ticks, path);
        }
        internal static Child Start(string script = "[Console]::In.ReadLine()|Out-Null")
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
                start.ArgumentList.Add(argument);
            return new(Process.Start(start), path);
        }
        internal void Release()
        {
            if (_released) return;
            _released = true;
            if (Process.HasExited) return;
            Process.StandardInput.WriteLine();
            Process.StandardInput.Flush();
            Process.StandardInput.Close();
        }
        public void Dispose()
        {
            Release();
            if (!Process.WaitForExit(10000))
                throw new TimeoutException("The owned inert child did not exit; no user process may be killed.");
            Process.Dispose();
        }
    }

    private sealed class Pipes : IDisposable
    {
        internal NamedPipeServerStream Server;
        internal NamedPipeClientStream Client;
        internal static async Task<Pipes> Connect()
        {
            var name = "kwt-update-identity-" + Guid.NewGuid().ToString("N");
            var pair = new Pipes
            {
                Server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance),
                Client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
            };
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Task.WhenAll(pair.Server.WaitForConnectionAsync(timeout.Token), pair.Client.ConnectAsync(timeout.Token));
                return pair;
            }
            catch { pair.Dispose(); throw; }
        }
        public void Dispose() { Client?.Dispose(); Server?.Dispose(); }
    }

    private sealed class Binding
    {
        private readonly Type _type;
        private readonly MethodInfo _acquire;
        private Binding()
        {
            _type = typeof(UpdateWorkAdmissionGate).Assembly.GetType("KeyboardWtf.Services.Updates.UpdateProcessIdentityLease");
            Assert.True(_type is not null, "The real Windows parent-handle identity primitive is missing.");
            _acquire = _type.GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(_acquire);
        }
        internal static Binding Require() => new();
        internal Lease Acquire(DeclaredUpdateParent identity) => Acquire(identity, identity?.ExecutablePath);
        internal Lease Acquire(DeclaredUpdateParent identity, string trustedPath) => new(Invoke(() => _acquire.Invoke(null, [identity, trustedPath])), _type);
    }

    private sealed class Lease(object value, Type type) : IDisposable
    {
        internal T Read<T>(string name) => (T)Invoke(() => type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value));
        internal Task<bool> Wait(TimeSpan timeout, CancellationToken token) => (Task<bool>)Invoke(() =>
            type.GetMethod("WaitForExitAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, [timeout, token]));
        internal void Authenticate(string method, object pipe) => Invoke(() =>
            type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, [pipe]));
        public void Dispose() => ((IDisposable)value).Dispose();
    }

    private static object Invoke(Func<object> action)
    {
        try { return action(); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
