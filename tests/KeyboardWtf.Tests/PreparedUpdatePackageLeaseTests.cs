using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Tiny inert files only: never an actual installer, process, profile or network.
// These checks establish a held leaf-file byte boundary, NOT confinement of the
// parent directory namespace, Authenticode, installer execution or installation.
public sealed class PreparedUpdatePackageLeaseTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Runtime_lease_seam_is_internal_immutable_and_bound_to_existing_service()
    {
        var binding = Binding.Require();
        Assert.True(binding.LeaseType.IsSealed);
        Assert.False(binding.LeaseType.IsVisible);
        Assert.True(typeof(IDisposable).IsAssignableFrom(binding.LeaseType));
        Assert.Equal(typeof(VerifiedUpdate), binding.UpdateProperty.PropertyType);
        Assert.Equal(typeof(string), binding.PathProperty.PropertyType);
        Assert.False(binding.UpdateProperty.CanWrite);
        Assert.False(binding.PathProperty.CanWrite);
        Assert.True(binding.AcquireMethod.GetParameters()[2].IsOptional);
        Assert.False(binding.AcquireMethod.IsPublic);
    }

    [Fact]
    public async Task Correct_tiny_package_binds_the_original_trusted_tuple_and_canonical_path_without_HTTP()
    {
        await using var fixture = Fixture.Create();
        using var lease = await fixture.AcquireAsync();
        Assert.Same(fixture.Update, lease.Update);
        Assert.Equal(fixture.InstallerPath, lease.InstallerPath);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(lease.InstallerPath));
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Equal(new[] { fixture.InstallerPath }, fixture.Files());
    }

    [Fact]
    public async Task Ordinary_Windows_case_differences_are_accepted_but_return_the_derived_canonical_path()
    {
        await using var fixture = Fixture.Create();
        using var lease = await fixture.AcquireAsync(path: fixture.InstallerPath.ToUpperInvariant());
        Assert.Equal(fixture.InstallerPath, lease.InstallerPath);
        Assert.Same(fixture.Update, lease.Update);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Same_size_post_preparation_tampering_is_rejected_and_failed_acquisition_releases_the_file()
    {
        await using var fixture = Fixture.Create();
        var tampered = fixture.Bytes.ToArray();
        tampered[0] ^= 0x7f;
        await File.WriteAllBytesAsync(fixture.InstallerPath, tampered);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.AcquireAndDisposeAsync());
        Assert.Equal(tampered, await File.ReadAllBytesAsync(fixture.InstallerPath));
        fixture.AssertExclusiveOpen();
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(UpdateManifestVerifier.MaximumPackageBytes + 1)]
    public async Task Invalid_signed_size_is_rejected_without_creating_a_large_file(long size)
    {
        await using var fixture = Fixture.Create();
        File.Delete(fixture.InstallerPath);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.AcquireAndDisposeAsync(fixture.Update with { SizeBytes = size }));
        Assert.Equal("The prepared update signed size is invalid.", error.Message);
        Assert.False(File.Exists(fixture.InstallerPath));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(64)]
    public async Task Malformed_signed_hash_is_rejected_before_missing_leaf_access(int length)
    {
        await using var fixture = Fixture.Create();
        File.Delete(fixture.InstallerPath);
        var hash = length < 0 ? null : new string(length == 64 ? 'g' : 'a', length);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.AcquireAndDisposeAsync(fixture.Update with { Sha256 = hash }));
        Assert.Equal("The prepared update signed hash is invalid.", error.Message);
        Assert.False(File.Exists(fixture.InstallerPath));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Existing_file_length_must_match_the_trusted_signed_size_exactly(int delta)
    {
        await using var fixture = Fixture.Create();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.AcquireAndDisposeAsync(fixture.Update with { SizeBytes = fixture.Bytes.Length + delta }));
        fixture.AssertExclusiveOpen();
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Missing_prepared_file_is_a_truthful_failure_without_recreation_or_HTTP()
    {
        await using var fixture = Fixture.Create();
        File.Delete(fixture.InstallerPath);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.AcquireAndDisposeAsync());
        Assert.False(File.Exists(fixture.InstallerPath));
        Assert.Empty(fixture.Files());
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Directory_at_the_leaf_is_rejected_instead_of_being_treated_as_package_bytes()
    {
        await using var fixture = Fixture.Create();
        File.Delete(fixture.InstallerPath);
        Directory.CreateDirectory(fixture.InstallerPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.AcquireAndDisposeAsync());
        Assert.True(Directory.Exists(fixture.InstallerPath));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("dot")]
    [InlineData("dotdot")]
    [InlineData("duplicate-separator")]
    [InlineData("alternate-separator")]
    [InlineData("relative")]
    [InlineData("trailing-dot")]
    [InlineData("trailing-space")]
    [InlineData("ads")]
    [InlineData("wildcard")]
    [InlineData("device-prefix")]
    [InlineData("extended-prefix")]
    [InlineData("unc")]
    [InlineData("unrelated")]
    [InlineData("alternate-filename")]
    [InlineData("empty")]
    [InlineData("null")]
    public async Task Noncanonical_or_unrelated_paths_are_rejected_as_path_validation_not_missing_file(string variant)
    {
        await using var fixture = Fixture.Create();
        var path = fixture.PathVariant(variant);
        // A correct canonical file remains present. In particular dot/relative/
        // separator forms cannot pass merely because normalization finds it.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.AcquireAndDisposeAsync(path: path, explicitlyUseNullPath: variant == "null"));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.InstallerPath));
        fixture.AssertExclusiveOpen();
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Null_trusted_update_is_rejected_before_attempting_to_open_the_leaf()
    {
        await using var fixture = Fixture.Create();
        using var writer = fixture.OpenWriter();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.AcquireAndDisposeAsync(update: null, explicitlyUseNullUpdate: true));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Existing_public_update_eligibility_refusal_precedes_leaf_access()
    {
        await using var fixture = Fixture.Create(eligible: false);
        using var writer = fixture.OpenWriter();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AcquireAndDisposeAsync());
        Assert.Contains("public updates", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Existing_writable_handle_prevents_a_leaf_read_lock_and_is_not_bypassed()
    {
        await using var fixture = Fixture.Create();
        using (var writer = fixture.OpenWriter())
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.AcquireAndDisposeAsync());
        using var lease = await fixture.AcquireAsync();
        Assert.Same(fixture.Update, lease.Update);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Leaf_write_delete_and_rename_are_blocked_until_the_lease_is_disposed()
    {
        await using var fixture = Fixture.Create();
        var lease = await fixture.AcquireAsync();
        var renamed = Path.Combine(fixture.VersionDirectory, "renamed-inert-fixture.bin");
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(fixture.InstallerPath, fixture.Bytes));
        Assert.ThrowsAny<IOException>(() => File.Delete(fixture.InstallerPath));
        Assert.ThrowsAny<IOException>(() => File.Move(fixture.InstallerPath, renamed));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.InstallerPath));
        lease.Dispose();
        lease.Dispose(); // Terminal cleanup is idempotent.
        await File.WriteAllBytesAsync(fixture.InstallerPath, fixture.Bytes);
        File.Move(fixture.InstallerPath, renamed);
        File.Move(renamed, fixture.InstallerPath);
        File.Delete(fixture.InstallerPath);
        Assert.False(File.Exists(fixture.InstallerPath));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_package_namespace_prevents_parent_rename_until_disposal(bool downloadRoot)
    {
        await using var fixture = Fixture.Create();
        var lease = await fixture.AcquireAsync();
        var original = downloadRoot ? fixture.DownloadRoot : fixture.VersionDirectory;
        var moved = original + "-renamed";
        try
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(original, moved));
            Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.InstallerPath));
            // Holding directories must not block unrelated ordinary file creation.
            var sibling = Path.Combine(fixture.VersionDirectory, "ordinary-sibling.txt");
            await File.WriteAllTextAsync(sibling, "inert sibling");
            File.Delete(sibling);
        }
        finally
        {
            lease.Dispose();
            // The RED implementation allows the move. Restore only the exact
            // GUID fixture before the existing guarded cleanup runs.
            if (Directory.Exists(moved)) Directory.Move(moved, original);
        }
        Directory.Move(original, moved);
        Directory.Move(moved, original);
        fixture.AssertExclusiveOpen();
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("hash")]
    public async Task Rejected_package_releases_all_parent_handles_not_only_leaf(string reason)
    {
        await using var fixture = Fixture.Create();
        var rejected = reason == "length"
            ? fixture.Update with { SizeBytes = fixture.Bytes.Length + 1 }
            : fixture.Update with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.AcquireAndDisposeAsync(rejected));
        var moved = fixture.DownloadRoot + "-renamed";
        try { Directory.Move(fixture.DownloadRoot, moved); }
        finally { if (Directory.Exists(moved)) Directory.Move(moved, fixture.DownloadRoot); }
        fixture.AssertExclusiveOpen();
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Precancelled_acquisition_does_not_leave_a_read_handle_or_dispatch_HTTP()
    {
        await using var fixture = Fixture.Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.AcquireAndDisposeAsync(token: cancelled.Token));
        fixture.AssertExclusiveOpen();
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.InstallerPath));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task A_later_record_reference_cannot_change_the_original_lease_tuple()
    {
        await using var fixture = Fixture.Create();
        var original = fixture.Update;
        using var lease = await fixture.AcquireAsync(original);
        var subsequent = original with
        {
            Version = new Version(9, 9, 9), Channel = "beta", Sha256 = new string('0', 64),
            PackageUri = new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.9.9/other.exe"),
        };
        Assert.NotEqual(original, subsequent);
        Assert.Same(original, lease.Update);
        Assert.Equal(original.Channel, lease.Update.Channel);
        Assert.Equal(original.Sha256, lease.Update.Sha256);
        Assert.Equal(fixture.InstallerPath, lease.InstallerPath);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Fifty_tiny_local_acquisitions_keep_each_leaf_locked_until_disposal_and_release_every_handle()
    {
        await using var fixture = Fixture.Create();
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var bytes = Encoding.UTF8.GetBytes($"inert tuple-cycle bytes {iteration:D2}");
            await File.WriteAllBytesAsync(fixture.InstallerPath, bytes);
            var update = fixture.Update with
            {
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            };
            var lease = await fixture.AcquireAsync(update);
            Assert.Same(update, lease.Update);
            Assert.ThrowsAny<IOException>(() => File.Delete(fixture.InstallerPath));
            lease.Dispose();
            fixture.AssertExclusiveOpen();
            Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.InstallerPath));
        }
        Assert.Equal(0, fixture.Handler.Requests);
    }

    private sealed class Binding(Type leaseType, MethodInfo acquireMethod, PropertyInfo updateProperty, PropertyInfo pathProperty)
    {
        internal Type LeaseType { get; } = leaseType;
        internal MethodInfo AcquireMethod { get; } = acquireMethod;
        internal PropertyInfo UpdateProperty { get; } = updateProperty;
        internal PropertyInfo PathProperty { get; } = pathProperty;

        internal static Binding Require()
        {
            var leaseType = typeof(SecureUpdateService).Assembly.GetType(
                "KeyboardWtf.Services.Updates.PreparedUpdatePackageLease", throwOnError: false);
            Assert.NotNull(leaseType);
            var method = typeof(SecureUpdateService).GetMethod("AcquirePreparedPackageLeaseAsync",
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                types: [typeof(VerifiedUpdate), typeof(string), typeof(CancellationToken)], modifiers: null);
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<>).MakeGenericType(leaseType), method.ReturnType);
            var properties = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var updateProperty = leaseType.GetProperty("Update", properties);
            var pathProperty = leaseType.GetProperty("InstallerPath", properties);
            Assert.NotNull(updateProperty);
            Assert.NotNull(pathProperty);
            return new Binding(leaseType, method, updateProperty, pathProperty);
        }

        internal Task Invoke(SecureUpdateService service, VerifiedUpdate update, string path, CancellationToken token)
        {
            try { return (Task)AcquireMethod.Invoke(service, [update, path, token])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        internal object Result(Task task) => task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private sealed class Lease(Binding binding, object instance) : IDisposable
    {
        private int _disposed;
        internal VerifiedUpdate Update => (VerifiedUpdate)binding.UpdateProperty.GetValue(instance)!;
        internal string InstallerPath => (string)binding.PathProperty.GetValue(instance)!;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ((IDisposable)instance).Dispose();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Binding _binding;
        private readonly string _root;
        private readonly List<Task> _originals = [];
        private readonly List<Lease> _leases = [];
        internal byte[] Bytes { get; } = Encoding.UTF8.GetBytes("tiny inert prepared package bytes");
        internal string VersionDirectory { get; }
        internal string DownloadRoot => _root;
        internal string InstallerPath { get; }
        internal VerifiedUpdate Update { get; }
        internal NoNetworkHandler Handler { get; } = new();
        internal SecureUpdateService Service { get; }

        private Fixture(Binding binding, bool eligible)
        {
            _binding = binding;
            Assert.True(OperatingSystem.IsWindows(), "Leaf read-sharing checks require Windows.");
            _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"kwt-prepared-leaf-{Guid.NewGuid():N}"));
            Update = new VerifiedUpdate(new Version(9, 0, 1), new Version(1, 4, 19), "stable",
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
                new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.1/keyboard.wtf-setup.exe"),
                Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(), Bytes.Length,
                "Inert local test tuple", UpdateManifestVerifier.CurrentKeyId);
            VersionDirectory = Path.Combine(_root, Update.Version.ToString());
            InstallerPath = Path.Combine(VersionDirectory, "keyboard.wtf-setup.exe");
            Service = new SecureUpdateService(Handler, publicUpdateEligibility: () => eligible, downloadRoot: _root);
            Directory.CreateDirectory(VersionDirectory);
            File.WriteAllBytes(InstallerPath, Bytes);
        }

        internal static Fixture Create(bool eligible = true)
        {
            // Missing-seam RED fails before any synthetic directory/file or work.
            var binding = Binding.Require();
            return new Fixture(binding, eligible);
        }

        internal async Task<Lease> AcquireAsync(VerifiedUpdate update = null, string path = null,
            CancellationToken token = default, bool explicitlyUseNullUpdate = false, bool explicitlyUseNullPath = false)
        {
            var original = _binding.Invoke(Service, explicitlyUseNullUpdate ? null : update ?? Update,
                explicitlyUseNullPath ? null : path ?? InstallerPath, token);
            _originals.Add(original);
            await original.WaitAsync(TestTimeout);
            var lease = new Lease(_binding, _binding.Result(original));
            _leases.Add(lease);
            return lease;
        }

        internal async Task AcquireAndDisposeAsync(VerifiedUpdate update = null, string path = null,
            CancellationToken token = default, bool explicitlyUseNullUpdate = false, bool explicitlyUseNullPath = false)
        {
            using var lease = await AcquireAsync(update, path, token, explicitlyUseNullUpdate,
                explicitlyUseNullPath);
        }

        internal string PathVariant(string variant)
        {
            return variant switch
            {
                "dot" => Path.Combine(VersionDirectory, ".", "keyboard.wtf-setup.exe"),
                "dotdot" => Path.Combine(VersionDirectory, "..", Update.Version.ToString(), "keyboard.wtf-setup.exe"),
                "duplicate-separator" => _root + "\\\\" + Update.Version + "\\keyboard.wtf-setup.exe",
                "alternate-separator" => InstallerPath.Replace('\\', '/'),
                "relative" => Path.GetRelativePath(Environment.CurrentDirectory, InstallerPath),
                "trailing-dot" => InstallerPath + ".",
                "trailing-space" => InstallerPath + " ",
                "ads" => InstallerPath + ":payload",
                "wildcard" => Path.Combine(VersionDirectory, "*.exe"),
                "device-prefix" => @"\\.\" + InstallerPath,
                "extended-prefix" => @"\\?\" + InstallerPath,
                "unc" => @"\\example.test\not-opened\keyboard.wtf-setup.exe",
                "unrelated" => Path.Combine(_root, "unrelated-inert.bin"),
                "alternate-filename" => Path.Combine(VersionDirectory, "keyboard-wtf-setup.exe"),
                "empty" => "",
                "null" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
        }

        internal FileStream OpenWriter() => new(InstallerPath, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        internal void AssertExclusiveOpen()
        {
            using var exclusive = new FileStream(InstallerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(new FileInfo(InstallerPath).Length, exclusive.Length);
        }

        internal string[] Files() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

        public async ValueTask DisposeAsync()
        {
            // Join the original operations, not only cancelled/timed-out waiters.
            // If an original remains unfinished, timeout escapes and retains its
            // GUID fixture; it must not be silently deleted underneath that work.
            var originals = Task.WhenAll(_originals);
            try { await originals.WaitAsync(TestTimeout); }
            catch when (originals.IsCompleted) { /* Observe completed synthetic rejection/cancellation only. */ }
            foreach (var lease in _leases) lease.Dispose();
            // A test waiter could time out just before its original succeeds.
            // That returned handle must also be disposed before fixture cleanup.
            foreach (var original in _originals.Where(task => task.IsCompletedSuccessfully))
                ((IDisposable)_binding.Result(original)).Dispose();
            Service.Dispose();
            Assert.Equal(0, Handler.Requests);
            if (!Directory.Exists(_root)) return;
            var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(temporaryParent, Path.GetDirectoryName(_root), StringComparer.OrdinalIgnoreCase);
            var name = Path.GetFileName(_root);
            Assert.StartsWith("kwt-prepared-leaf-", name, StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(name["kwt-prepared-leaf-".Length..], "N", out _));
            Assert.False((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories),
                path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Prepared leaf verification must not dispatch HTTP.");
        }
    }
}
