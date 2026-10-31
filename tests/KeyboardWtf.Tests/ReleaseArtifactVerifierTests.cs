using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

[CollectionDefinition("Release artifact file boundaries", DisableParallelization = true)]
public sealed class ReleaseArtifactFileBoundaryCollection { }

[Collection("Release artifact file boundaries")]
public sealed class ReleaseArtifactVerifierTests
{
    [Fact]
    public async Task Complete_synthetic_fixture_matches_declared_metadata_without_writes()
    {
        using var fixture = new ArtifactFixture();
        var before = fixture.FileHashes();
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await ReleaseArtifactVerifier.RunAsync(fixture.Args(), output, error,
            fixture.Verifier, fixture.ReadSyntheticVersion);
        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        Assert.Equal(before, fixture.FileHashes());
        using var receipt = JsonDocument.Parse(output.ToString());
        Assert.True(receipt.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("declaration-matched; not source attestation",
            receipt.RootElement.GetProperty("sourceProvenance").GetString());
        Assert.False(receipt.RootElement.GetProperty("publicFlavorProven").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("authenticodeVerified").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("applicationIncludedInInstallerProven").GetBoolean());
        Assert.DoesNotContain(fixture.Notes, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--private-key")]
    [InlineData("--public-key")]
    [InlineData("--key-id")]
    [InlineData("--output")]
    [InlineData("--url")]
    [InlineData("--install")]
    [InlineData("--network")]
    public async Task CLI_refuses_unknown_or_effectful_options(string option)
    {
        using var fixture = new ArtifactFixture();
        await fixture.AssertRejected(fixture.Args().Concat([option, "sensitive-fixture-value"]).ToArray(), "arguments_invalid");
        var exactCount = fixture.Args();
        exactCount[0] = option;
        await fixture.AssertRejected(exactCount, "arguments_invalid");
    }

    [Fact]
    public async Task CLI_refuses_duplicate_missing_odd_and_positional_arguments()
    {
        using var fixture = new ArtifactFixture();
        await fixture.AssertRejected(fixture.Args().Concat(["--version", "1.4.20"]).ToArray(), "arguments_invalid");
        await fixture.AssertRejected(fixture.Args()[2..], "arguments_invalid");
        await fixture.AssertRejected(fixture.Args().Concat(["--version"]).ToArray(), "arguments_invalid");
        var positional = fixture.Args();
        positional[0] = "manifest";
        await fixture.AssertRejected(positional, "arguments_invalid");
        var sameCountDuplicate = fixture.Args();
        sameCountDuplicate[0] = "--version";
        await fixture.AssertRejected(sameCountDuplicate, "arguments_invalid");
    }

    [Theory]
    [InlineData("source-commit", "\n")]
    [InlineData("source-commit", "\r\n")]
    [InlineData("source-commit", " ")]
    [InlineData("version", "\n")]
    [InlineData("version", "\r\n")]
    [InlineData("version", " ")]
    public async Task Canonical_CLI_identifiers_reject_trailing_whitespace(string option, string suffix)
    {
        using var fixture = new ArtifactFixture();
        var args = fixture.Args();
        var index = Array.IndexOf(args, "--" + option) + 1;
        args[index] += suffix;
        if (option == "source-commit")
        {
            fixture.Provenance["sourceCommit"] = args[index];
            fixture.WriteProvenance();
        }
        await fixture.AssertRejected(args, "arguments_invalid");
    }

    [Fact]
    public async Task Uncompressed_application_can_exceed_compressed_installer_envelope()
    {
        using var fixture = new ArtifactFixture();
        var length = UpdateManifestVerifier.MaximumPackageBytes + 64L * 1024 * 1024;
        using (var application = File.OpenWrite(fixture.ApplicationPath))
            application.SetLength(length);
        string expectedHash;
        await using (var source = File.OpenRead(fixture.ApplicationPath))
            expectedHash = Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await ReleaseArtifactVerifier.RunAsync(fixture.Args(), output, error,
            fixture.Verifier, fixture.ReadSyntheticVersion);
        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        Assert.Equal(length, new FileInfo(fixture.ApplicationPath).Length);
        using var receipt = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedHash, receipt.RootElement.GetProperty("applicationSha256").GetString());
        Assert.False(receipt.RootElement.GetProperty("applicationIncludedInInstallerProven").GetBoolean());
        // Generated trust/synthetic PE metadata exercise only the size/hash boundary.
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(ReleaseArtifactVerifier.MaximumApplicationBytes + 1)]
    public async Task Application_size_is_bounded_before_metadata_or_hashing(long length)
    {
        using var fixture = new ArtifactFixture();
        using (var application = File.OpenWrite(fixture.ApplicationPath))
            application.SetLength(length);
        fixture.OnReadVersion = _ => throw new Xunit.Sdk.XunitException("Oversized input must be refused before metadata.");
        await fixture.AssertRejected(fixture.Args(), "application_size_invalid");
    }

    [Theory]
    [InlineData("2026-10-04T12:00:00")]
    [InlineData("2026-10-04T12:00:00+05:30")]
    public async Task Provenance_requires_an_explicit_UTC_timestamp(string timestamp)
    {
        using var fixture = new ArtifactFixture();
        fixture.Provenance["buildDateUtc"] = timestamp;
        fixture.WriteProvenance();
        await fixture.AssertRejected(fixture.Args(), "provenance_invalid");
    }

    [Fact]
    public async Task Actual_PE_reader_rejects_synthetic_non_PE_binaries_without_loading_them()
    {
        using var fixture = new ArtifactFixture();
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, await ReleaseArtifactVerifier.RunAsync(fixture.Args(), output, error, fixture.Verifier));
        Assert.Empty(output.ToString());
        Assert.Contains("pe_metadata_invalid", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_embedded_trust_rejects_an_otherwise_complete_generated_key_fixture()
    {
        using var fixture = new ArtifactFixture();
        var output = new StringWriter();
        var error = new StringWriter();
        // Null trust uses the SAME default embedded verifier as the public CLI.
        var code = await ReleaseArtifactVerifier.RunAsync(fixture.Args(), output, error,
            verifier: null, readFileVersion: fixture.ReadSyntheticVersion);
        Assert.Equal(1, code);
        Assert.Empty(output.ToString());
        Assert.Contains("manifest_invalid", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Notes, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Archived_manifest_alone_is_accepted_by_real_default_trust()
    {
        var root = FindRepositoryRoot();
        var json = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "releases", "stable-1.4.17.json"));
        var verified = new UpdateManifestVerifier().Verify(json, "stable", new Version(1, 2, 0), DateTimeOffset.UtcNow);
        Assert.Equal(new Version(1, 4, 17), verified.Version);
        Assert.Equal("812aaa434a4d6b618764b044a9a5f96b5dc9b85830ce1f205c5b6ad582ded6cd", verified.Sha256);
        // This is intentionally NOT a complete artifact acceptance claim.
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("upgrade")]
    [InlineData("url")]
    [InlineData("notes")]
    [InlineData("package-bytes")]
    [InlineData("package-size")]
    [InlineData("package-envelope")]
    [InlineData("installer-version")]
    [InlineData("application-version")]
    [InlineData("metadata-reader")]
    public async Task Material_artifact_mismatches_are_rejected(string mutation)
    {
        using var fixture = new ArtifactFixture();
        var args = fixture.Args();
        var manifest = fixture.Manifest;
        switch (mutation)
        {
            case "signature": fixture.WriteManifest(manifest with { Signature = "AA==" }, resign: false); break;
            case "channel": fixture.WriteManifest(manifest with { Channel = "beta" }); break;
            case "version": fixture.WriteManifest(manifest with { Version = "1.4.21" }); break;
            case "upgrade": fixture.WriteManifest(manifest with { MinimumVersion = "1.4.19" }); break;
            case "url": fixture.WriteManifest(manifest with { PackageUrl = manifest.PackageUrl.Replace("/v1.4.20/", "/v1.4.21/", StringComparison.Ordinal) }); break;
            case "notes": await File.WriteAllTextAsync(fixture.NotesPath, "different notes"); break;
            case "package-bytes": await File.WriteAllBytesAsync(fixture.PackagePath, Encoding.UTF8.GetBytes("tampered-package!")); break;
            case "package-size": fixture.WriteManifest(manifest with { SizeBytes = manifest.SizeBytes + 1 }); break;
            case "package-envelope": fixture.WriteManifest(manifest with { SizeBytes = UpdateManifestVerifier.MaximumPackageBytes + 1 }); break;
            case "installer-version": fixture.InstallerVersion = new Version(1, 4, 19, 0); break;
            case "application-version": fixture.ApplicationVersion = new Version(1, 4, 19, 0); break;
            case "metadata-reader": fixture.MetadataFailure = true; break;
        }
        await fixture.AssertRejected(args, mutation == "package-envelope" ? "manifest_invalid" : null);
    }

    [Theory]
    [InlineData("sourceCommit")]
    [InlineData("version")]
    [InlineData("installer")]
    [InlineData("installerSha256")]
    [InlineData("signingKeyId")]
    [InlineData("buildDateUtc")]
    public async Task Every_existing_provenance_field_is_validated(string field)
    {
        using var fixture = new ArtifactFixture();
        fixture.Provenance[field] = field == "buildDateUtc" ? "not-a-timestamp" : "mismatch";
        fixture.WriteProvenance();
        await fixture.AssertRejected(fixture.Args(), "provenance_invalid");
    }

    [Theory]
    [InlineData("manifest-oversized")]
    [InlineData("manifest-duplicate")]
    [InlineData("manifest-unknown")]
    [InlineData("manifest-invalid-utf8")]
    [InlineData("provenance-oversized")]
    [InlineData("provenance-duplicate")]
    [InlineData("provenance-unknown")]
    [InlineData("provenance-number")]
    [InlineData("provenance-missing")]
    [InlineData("notes-oversized")]
    [InlineData("notes-invalid-utf8")]
    public async Task Malformed_bounded_inputs_fail_without_content_leak(string mutation)
    {
        using var fixture = new ArtifactFixture();
        switch (mutation)
        {
            case "manifest-oversized": await File.WriteAllBytesAsync(fixture.ManifestPath, new byte[128 * 1024 + 1]); break;
            case "manifest-duplicate": await File.WriteAllTextAsync(fixture.ManifestPath, File.ReadAllText(fixture.ManifestPath).Replace("{", "{\"VERSION\":\"1.4.20\",", StringComparison.Ordinal)); break;
            case "manifest-unknown": await File.WriteAllTextAsync(fixture.ManifestPath, File.ReadAllText(fixture.ManifestPath).Replace("{", "{\"extra\":true,", StringComparison.Ordinal)); break;
            case "manifest-invalid-utf8": await File.WriteAllBytesAsync(fixture.ManifestPath, [0xff, 0xfe, 0xff]); break;
            case "provenance-oversized": await File.WriteAllBytesAsync(fixture.ProvenancePath, new byte[16 * 1024 + 1]); break;
            case "provenance-duplicate": await File.WriteAllTextAsync(fixture.ProvenancePath, File.ReadAllText(fixture.ProvenancePath).Replace("{", "{\"VERSION\":\"1.4.20\",", StringComparison.Ordinal)); break;
            case "provenance-unknown": fixture.Provenance["extra"] = "sensitive-fixture-value"; fixture.WriteProvenance(); break;
            case "provenance-number": await File.WriteAllTextAsync(fixture.ProvenancePath, File.ReadAllText(fixture.ProvenancePath).Replace("\"1.4.20\"", "123", StringComparison.Ordinal)); break;
            case "provenance-missing": fixture.Provenance.Remove("sourceCommit"); fixture.WriteProvenance(); break;
            case "notes-oversized": await File.WriteAllTextAsync(fixture.NotesPath, new string('x', 20_001)); break;
            case "notes-invalid-utf8": await File.WriteAllBytesAsync(fixture.NotesPath, [0xff, 0xfe, 0xff]); break;
        }
        await fixture.AssertRejected(fixture.Args());
    }

    [Fact]
    public async Task Package_is_held_without_write_or_delete_sharing_during_metadata_checks()
    {
        using var fixture = new ArtifactFixture();
        fixture.OnReadVersion = path =>
        {
            if (path == fixture.PackagePath)
                Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        };
        var code = await ReleaseArtifactVerifier.RunAsync(fixture.Args(), new StringWriter(), new StringWriter(), fixture.Verifier, fixture.ReadSyntheticVersion);
        Assert.Equal(0, code);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "KeyboardWtf.sln"))) return current.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class ArtifactFixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "kwt-artifact-verifier-" + Guid.NewGuid().ToString("N"));
        public string Notes => "Bounded synthetic notes, not a real release.";
        public string PackagePath => Path.Combine(Root, "keyboard-wtf-setup.exe");
        public string ApplicationPath => Path.Combine(Root, "keyboard.wtf.exe");
        public string ManifestPath => Path.Combine(Root, "stable.json");
        public string NotesPath => Path.Combine(Root, "notes.md");
        public string ProvenancePath => Path.Combine(Root, "release-provenance.json");
        public SignedUpdateManifest Manifest { get; private set; }
        public Dictionary<string, string> Provenance { get; } = new();
        public Version InstallerVersion { get; set; } = new(1, 4, 20, 0);
        public Version ApplicationVersion { get; set; } = new(1, 4, 20, 0);
        public bool MetadataFailure { get; set; }
        public Action<string> OnReadVersion { get; set; }
        public UpdateManifestVerifier Verifier => new(new Dictionary<string, string> { [UpdateManifestVerifier.CurrentKeyId] = _key.ExportSubjectPublicKeyInfoPem() });

        public ArtifactFixture()
        {
            Directory.CreateDirectory(Root);
            var package = Encoding.UTF8.GetBytes("synthetic-package");
            File.WriteAllBytes(PackagePath, package);
            File.WriteAllBytes(ApplicationPath, "synthetic-application"u8.ToArray());
            File.WriteAllText(NotesPath, Notes);
            Manifest = new(1, "stable", "1.4.20", "1.4.17", DateTimeOffset.UtcNow,
                "https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.20/keyboard-wtf-setup.exe",
                Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), package.Length, Notes, UpdateManifestVerifier.CurrentKeyId, "");
            WriteManifest(Manifest);
            Provenance["sourceCommit"] = new string('a', 40);
            Provenance["version"] = "1.4.20";
            Provenance["buildDateUtc"] = DateTimeOffset.UtcNow.ToString("O");
            Provenance["installer"] = "keyboard-wtf-setup.exe";
            Provenance["installerSha256"] = Manifest.Sha256;
            Provenance["signingKeyId"] = Manifest.KeyId;
            WriteProvenance();
        }

        public string[] Args() => ["--manifest", ManifestPath, "--package", PackagePath, "--application", ApplicationPath,
            "--release-notes", NotesPath, "--provenance", ProvenancePath, "--channel", "stable", "--version", "1.4.20",
            "--oldest-client-version", "1.4.17", "--source-commit", new string('a', 40)];
        public string[] FileHashes() => Directory.GetFiles(Root).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        public Version ReadSyntheticVersion(string path)
        {
            if (MetadataFailure) throw new IOException("sensitive-fixture-value");
            OnReadVersion?.Invoke(path);
            return path == PackagePath ? InstallerVersion : ApplicationVersion;
        }
        public void WriteManifest(SignedUpdateManifest manifest, bool resign = true)
        {
            Manifest = resign ? manifest with { Signature = Convert.ToBase64String(_key.SignData(UpdateManifestVerifier.CanonicalPayload(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) } : manifest;
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        public void WriteProvenance() => File.WriteAllText(ProvenancePath, JsonSerializer.Serialize(Provenance));
        public async Task AssertRejected(string[] args, string code = null)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(1, await ReleaseArtifactVerifier.RunAsync(args, output, error, Verifier, ReadSyntheticVersion));
            Assert.Empty(output.ToString());
            if (code is not null) Assert.Contains(code, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive-fixture-value", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Notes, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Root, error.ToString(), StringComparison.Ordinal);
        }
        public void Dispose()
        {
            _key.Dispose();
            var resolved = Path.GetFullPath(Root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Assert.Matches("^kwt-artifact-verifier-[a-f0-9]{32}$", Path.GetFileName(resolved));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories),
                path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(resolved, recursive: true);
        }
    }
}
