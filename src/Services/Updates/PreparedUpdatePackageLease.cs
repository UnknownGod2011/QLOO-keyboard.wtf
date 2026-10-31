using System.Security.Cryptography;

namespace KeyboardWtf.Services.Updates;

// Initially UNWIRED. Pins verified leaf bytes against write/delete sharing, NOT
// parent-directory namespace changes or path-to-process launch identity. Those
// separate boundaries must be proven before this can authorize real execution.
internal sealed class PreparedUpdatePackageLease : IDisposable
{
    private FileStream _stream;
    internal VerifiedUpdate Update { get; }
    internal string InstallerPath { get; }

    private PreparedUpdatePackageLease(VerifiedUpdate update, string path, FileStream stream)
    {
        Update = update;
        InstallerPath = path;
        _stream = stream;
    }

    internal static async Task<PreparedUpdatePackageLease> AcquireAsync(
        VerifiedUpdate update, string downloadRoot, string installerPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        if (update.SizeBytes is <= 0 or > UpdateManifestVerifier.MaximumPackageBytes)
            throw new InvalidDataException("The prepared update signed size is invalid.");
        if (update.Sha256 is null || update.Sha256.Length != 64 || !update.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The prepared update signed hash is invalid.");

        UpdatePathIdentity.RequireCanonicalLocalPath(downloadRoot);
        UpdatePathIdentity.RequireCanonicalLocalPath(installerPath);
        if (update.Version is null)
            throw new InvalidDataException("The prepared update version is invalid.");
        var expected = Path.Combine(downloadRoot, update.Version.ToString(), "keyboard.wtf-setup.exe");
        if (!string.Equals(expected, installerPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared update installer path is invalid.");

        // Reject known reparse/unknown metadata BEFORE opening package content.
        // These observations are not a race-free namespace pin.
        RequireOrdinaryAncestors(expected);
        FileStream stream = null;
        try
        {
            stream = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != update.SizeBytes)
                throw new InvalidDataException("The prepared update package size does not match its signed manifest.");
            var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("The prepared update package hash is invalid.");
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new PreparedUpdatePackageLease(update, expected, stream);
            stream = null; // Transfer ownership only after all validation succeeds.
            return lease;
        }
        finally { stream?.Dispose(); }
    }

    private static void RequireOrdinaryAncestors(string filePath)
    {
        var path = filePath;
        var leaf = true;
        while (path is not null)
        {
            var attributes = File.GetAttributes(path); // Unknown/missing metadata fails closed.
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || ((attributes & FileAttributes.Directory) != 0) == leaf)
                throw new InvalidDataException("The prepared update path metadata is invalid.");
            leaf = false;
            path = Path.GetDirectoryName(path);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
