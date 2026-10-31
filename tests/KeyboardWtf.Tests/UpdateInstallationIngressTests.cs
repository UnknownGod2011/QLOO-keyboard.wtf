using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Owned inert filesystem only. No app, native host, installer, profile or provider.
public sealed class UpdateInstallationIngressTests
{
    [Fact]
    public void Reservation_excludes_new_ingress_before_existing_lifetimes_finish()
    {
        var api = new Api();
        using var fixture = new Fixture();
        using var gui = api.Enter(fixture.Executable);
        using var host = api.Enter(fixture.Executable);
        Assert.NotNull(gui);
        Assert.NotNull(host);
        using var reservation = api.Reserve(fixture.Executable);
        Assert.NotNull(reservation);
        Assert.Null(api.Enter(fixture.Executable));
        Assert.Null(api.Reserve(fixture.Executable));
        Assert.False(api.Exclusive(reservation));
        gui.Dispose();
        Assert.False(api.Exclusive(reservation));
        host.Dispose();
        Assert.True(api.Exclusive(reservation));
        Assert.True(api.Exclusive(reservation));
        Assert.Null(api.Enter(fixture.Executable));
        reservation.Dispose();
        using var next = api.Enter(fixture.Executable);
        Assert.NotNull(next);
        Assert.Throws<ObjectDisposedException>(() => api.Exclusive(reservation));
    }

    [Fact]
    public void Separate_physical_installations_do_not_share_a_profile_or_path_name_gate()
    {
        var api = new Api();
        using var first = new Fixture();
        using var second = new Fixture();
        using var reservation = api.Reserve(first.Executable);
        using var other = api.Enter(second.Executable);
        Assert.NotNull(other);
        Assert.True(api.Exclusive(reservation));
        Assert.NotEqual(api.Identity(reservation), api.Identity(other));
        Assert.Null(api.Enter(first.Executable));
    }

    [Fact]
    public void Forty_paired_lifetimes_do_not_release_a_newer_reservation()
    {
        var api = new Api();
        using var fixture = new Fixture();
        for (var cycle = 0; cycle < 40; cycle++)
        {
            using var entry = api.Enter(fixture.Executable);
            using var reserve = api.Reserve(fixture.Executable);
            Assert.NotNull(entry);
            Assert.NotNull(reserve);
            Assert.False(api.Exclusive(reserve));
            Assert.Null(api.Enter(fixture.Executable));
            entry.Dispose();
            Assert.True(api.Exclusive(reserve));
            reserve.Dispose();
            using var newer = api.Reserve(fixture.Executable);
            entry.Dispose();
            reserve.Dispose();
            Assert.NotNull(newer);
            Assert.True(api.Exclusive(newer));
            Assert.Null(api.Enter(fixture.Executable));
        }
    }

    [Theory]
    [InlineData(".keyboard.wtf-update-reservation")]
    [InlineData(".keyboard.wtf-update-activity")]
    public void Marker_with_content_or_directory_cannot_certify_an_installation(string marker)
    {
        var api = new Api();
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Installation, marker);
        File.WriteAllText(path, "NOT A PROTOCOL MARKER");
        Assert.Throws<InvalidDataException>(() => api.ReserveAndExclusive(fixture.Executable));
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.NotNull(Record.Exception(() => api.ReserveAndExclusive(fixture.Executable)));
    }

    [Fact]
    public void Unknown_access_failure_is_not_idle_or_exclusive_authority()
    {
        var api = new Api();
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Installation, ".keyboard.wtf-update-reservation");
        File.WriteAllBytes(path, []);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try { Assert.NotNull(Record.Exception(() => api.ReserveAndExclusive(fixture.Executable))); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        using var next = api.Reserve(fixture.Executable);
        Assert.True(api.Exclusive(next));
    }

    [Fact]
    public void External_sharing_lock_refuses_but_cannot_become_a_successful_reservation()
    {
        var api = new Api();
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Installation, ".keyboard.wtf-update-reservation");
        using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(api.Reserve(fixture.Executable));
        using var later = api.Reserve(fixture.Executable);
        Assert.True(api.Exclusive(later));
    }

    [Fact]
    public void Original_directory_and_ancestor_cannot_be_renamed_while_a_lifetime_is_held()
    {
        var api = new Api();
        using var fixture = new Fixture();
        using var entry = api.Enter(fixture.Executable);
        Assert.NotNull(entry);
        Assert.Throws<IOException>(() => Directory.Move(fixture.Installation, fixture.Installation + "-moved"));
        Assert.Throws<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-moved"));
        entry.Dispose();
        Directory.Move(fixture.Installation, fixture.Installation + "-moved");
        Directory.Move(fixture.Installation + "-moved", fixture.Installation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\keyboard.wtf.exe")]
    [InlineData("C:\\Synthetic\\other.exe")]
    [InlineData("C:\\Synthetic\\..\\keyboard.wtf.exe")]
    [InlineData("\\\\server\\share\\keyboard.wtf.exe")]
    public void Invalid_or_unsupported_installation_declaration_never_creates_authority(string path)
    {
        var api = new Api();
        Assert.NotNull(Record.Exception(() => api.Enter(path)));
        Assert.NotNull(Record.Exception(() => api.Reserve(path)));
    }

    [Fact]
    public void Primitive_does_not_activate_the_runtime_updater()
    {
        var ready = typeof(WebSettingsService).GetProperty("SafeUpdateInstallationReady", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(ready);
        Assert.False((bool)ready.GetValue(null));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "kwt-ingress-test-" + Guid.NewGuid().ToString("N"));
        internal string Installation => Path.Combine(Root, "installed");
        internal string Executable => Path.Combine(Installation, "keyboard.wtf.exe");
        internal Fixture()
        {
            Directory.CreateDirectory(Installation);
            File.WriteAllText(Executable, "INERT DATA - NEVER EXECUTED");
        }
        public void Dispose()
        {
            // Retain GUID-local evidence. No recursive delete of computed paths.
        }
    }

    private sealed class Api
    {
        private readonly Type _type = typeof(UpdateWorkAdmissionGate).Assembly.GetType(
            "KeyboardWtf.Services.Updates.UpdateInstallationIngressLease");
        internal Api() => Assert.NotNull(_type);
        internal IDisposable Enter(string path) => (IDisposable)Call("TryEnter", null, path);
        internal IDisposable Reserve(string path) => (IDisposable)Call("TryReserve", null, path);
        internal bool Exclusive(IDisposable lease) => (bool)Call("TryAcquireExclusive", lease);
        internal object Identity(IDisposable lease) => _type.GetProperty("InstallationIdentity", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lease);
        internal void ReserveAndExclusive(string path)
        {
            using var lease = Reserve(path);
            Assert.NotNull(lease);
            Assert.True(Exclusive(lease));
        }
        private object Call(string name, object target, params object[] args)
        {
            var method = _type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            Assert.NotNull(method);
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
