using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Services.Updates;

/// <summary>
/// Read-only checks for an already-built, already-signed release. A matched
/// source declaration is not reproducible-build attestation, and PE versions
/// do not establish public flavor or Authenticode publisher identity.
/// </summary>
public static class ReleaseArtifactVerifier
{
    // The unpacked self-contained PE is larger than its compressed installer.
    // This local verification resource bound is not a download/runtime limit.
    internal const long MaximumApplicationBytes = 512L * 1024 * 1024;
    private static readonly string[] ArgumentNames = ["manifest", "package", "application", "release-notes", "provenance", "channel", "version", "oldest-client-version", "source-commit"];
    private static readonly string[] ManifestFields = ["schemaVersion", "channel", "version", "minimumVersion", "publishedAt", "packageUrl", "sha256", "sizeBytes", "releaseNotes", "keyId", "signature"];
    private static readonly string[] ProvenanceFields = ["sourceCommit", "version", "buildDateUtc", "installer", "installerSha256", "signingKeyId"];

    // CLI callers have no trust/metadata override, signing, output or network options.
    public static Task<int> RunAsync(string[] args) => RunAsync(args, Console.Out, Console.Error);

    // In-memory fixtures may use generated public trust and synthetic metadata.
    // These seams are inaccessible to the command line and default to real checks.
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        UpdateManifestVerifier? verifier = null, Func<string, Version>? readFileVersion = null)
    {
        try
        {
            var options = ParseArguments(args);
            if (options["channel"] is not ("stable" or "beta") ||
                !Regex.IsMatch(options["version"], @"\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\z") ||
                !Version.TryParse(options["version"], out var version) ||
                !Version.TryParse(options["oldest-client-version"], out var oldestClient) ||
                !Regex.IsMatch(options["source-commit"], @"\A[0-9a-fA-F]{40}\z"))
                throw Failure("arguments_invalid");

            var packagePath = Path.GetFullPath(options["package"]);
            var applicationPath = Path.GetFullPath(options["application"]);
            if (Path.GetFileName(packagePath) != "keyboard-wtf-setup.exe" ||
                Path.GetFileName(applicationPath) != "keyboard.wtf.exe")
                throw Failure("canonical_filename_invalid");

            var manifestJson = await ReadBoundedUtf8Async(options["manifest"], 128 * 1024);
            ValidateJsonFields(manifestJson, ManifestFields);
            VerifiedUpdate update;
            try { update = (verifier ?? new UpdateManifestVerifier()).Verify(manifestJson, options["channel"], oldestClient, DateTimeOffset.UtcNow); }
            catch { throw Failure("manifest_invalid"); }
            if (update.Version != version)
                throw Failure("version_mismatch");
            var canonicalUrl = $"https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v{options["version"]}/keyboard-wtf-setup.exe";
            if (!string.Equals(update.PackageUri.OriginalString, canonicalUrl, StringComparison.Ordinal))
                throw Failure("canonical_url_invalid");

            var notes = await ReadBoundedUtf8Async(options["release-notes"], 80_003);
            if (notes.Length > 20_000 || !string.Equals(notes, update.ReleaseNotes, StringComparison.Ordinal))
                throw Failure("release_notes_mismatch");
            var provenance = ReadProvenance(await ReadBoundedUtf8Async(options["provenance"], 16 * 1024));

            // Keep both binary files open without write/delete sharing through
            // hash and metadata checks; neither binary is loaded or executed.
            await using var package = OpenRead(packagePath);
            await using var application = OpenRead(applicationPath);
            if (application.Length is <= 0 or > MaximumApplicationBytes)
                throw Failure("application_size_invalid");
            if (package.Length != update.SizeBytes || package.Length > UpdateManifestVerifier.MaximumPackageBytes)
                throw Failure("package_size_mismatch");
            var packageHash = Convert.ToHexString(await SHA256.HashDataAsync(package)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(packageHash), Convert.FromHexString(update.Sha256)))
                throw Failure("package_hash_mismatch");

            var binaryVersion = new Version(version.Major, version.Minor, version.Build, 0);
            var readVersion = readFileVersion ?? ReadPeFileVersion;
            if (readVersion(packagePath) != binaryVersion || readVersion(applicationPath) != binaryVersion)
                throw Failure("pe_version_mismatch");
            ValidateProvenance(provenance, options, update, packageHash);
            var applicationHash = Convert.ToHexString(await SHA256.HashDataAsync(application)).ToLowerInvariant();
            var receipt = new
            {
                passed = true,
                version = options["version"],
                channel = update.Channel,
                minimumVersion = update.MinimumVersion.ToString(),
                installerSha256 = packageHash,
                installerSizeBytes = package.Length,
                applicationSha256 = applicationHash,
                fileVersion = binaryVersion.ToString(),
                manifestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant(),
                releaseNotesSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notes))).ToLowerInvariant(),
                sourceCommit = options["source-commit"].ToLowerInvariant(),
                signingKeyId = update.KeyId,
                sourceProvenance = "declaration-matched; not source attestation",
                publicFlavorProven = false,
                authenticodeVerified = false,
                applicationIncludedInInstallerProven = false,
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(receipt));
            return 0;
        }
        catch (VerificationFailure exception)
        {
            await error.WriteLineAsync($"Artifact verification failed: {exception.Code}.");
            return 1;
        }
        catch
        {
            // Never echo paths, file contents or upstream exception messages.
            await error.WriteLineAsync("Artifact verification failed: input_or_metadata_invalid.");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length != ArgumentNames.Length * 2) throw Failure("arguments_invalid");
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw Failure("arguments_invalid");
            var name = args[index][2..];
            if (!ArgumentNames.Contains(name, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(args[index + 1]) || !result.TryAdd(name, args[index + 1]))
                throw Failure("arguments_invalid");
        }
        return result;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> ReadBoundedUtf8Async(string path, int maximumBytes)
    {
        await using var stream = OpenRead(Path.GetFullPath(path));
        if (stream.Length > maximumBytes) throw Failure("input_too_large");
        using var data = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            if (data.Length + read > maximumBytes) throw Failure("input_too_large");
            data.Write(buffer, 0, read);
        }
        var text = new UTF8Encoding(false, true).GetString(data.ToArray());
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static void ValidateJsonFields(string json, string[] expectedFields)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Failure("json_invalid");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!expectedFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase) || !seen.Add(property.Name))
                throw Failure("json_invalid");
        if (seen.Count != expectedFields.Length) throw Failure("json_invalid");
    }

    private static Dictionary<string, string> ReadProvenance(string json)
    {
        ValidateJsonFields(json, ProvenanceFields);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw Failure("provenance_invalid");
            fields.Add(property.Name, property.Value.GetString()!);
        }
        return fields;
    }

    private static void ValidateProvenance(Dictionary<string, string> fields, Dictionary<string, string> options, VerifiedUpdate update, string packageHash)
    {
        if (!Regex.IsMatch(fields["sourceCommit"], @"\A[0-9a-fA-F]{40}\z") ||
            !string.Equals(fields["sourceCommit"], options["source-commit"], StringComparison.OrdinalIgnoreCase) ||
            fields["version"] != options["version"] || fields["installer"] != "keyboard-wtf-setup.exe" ||
            !string.Equals(fields["installerSha256"], packageHash, StringComparison.OrdinalIgnoreCase) ||
            fields["signingKeyId"] != update.KeyId ||
            !Regex.IsMatch(fields["buildDateUtc"], @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)\z") ||
            !DateTimeOffset.TryParse(fields["buildDateUtc"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var buildDate) ||
            buildDate.Offset != TimeSpan.Zero || buildDate > DateTimeOffset.UtcNow.AddHours(24))
            throw Failure("provenance_invalid");
    }

    private static Version ReadPeFileVersion(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw Failure("pe_metadata_invalid");
        stream.Position = 0x3c;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 4) throw Failure("pe_metadata_invalid");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw Failure("pe_metadata_invalid");
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static VerificationFailure Failure(string code) => new(code);
    private sealed class VerificationFailure(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
