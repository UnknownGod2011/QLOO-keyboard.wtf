using System.Security.Cryptography;
using System.Text.Json;

namespace KeyboardWtf.Services.Updates;

// Future verified downloads only. Legacy/unmarked cache and rollback archives
// are deliberately outside this policy. Cleanup never authorizes installation.
internal static class UpdateCacheRetention
{
    private const string Marker = "keyboard.wtf-owned-download-v1.json";
    private sealed record Receipt(string Owner, string Version, long Bytes, string Sha256, DateTimeOffset RecordedUtc);

    internal static void RecordAndTrim(string root, VerifiedUpdate update, DateTimeOffset now, Version runningVersion)
    {
        string temporary = null;
        try
        {
            var directory = Path.Combine(root, update.Version.ToString());
            if (!OwnedTemporaryFileCleanup.IsOrdinaryDirectory(directory)) return;
            var marker = Path.Combine(directory, Marker);
            temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var receipt = new Receipt("keyboard.wtf.update-cache.v1", update.Version.ToString(), update.SizeBytes,
                update.Sha256, now);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, receipt);
            File.Move(temporary, marker, overwrite: true);
            Trim(root, update.Version, now, runningVersion);
        }
        catch { } // A cache-maintenance failure cannot invalidate a verified download.
        finally
        {
            if (temporary is not null)
                OwnedTemporaryFileCleanup.TryDelete(temporary, Path.GetDirectoryName(temporary), now.UtcDateTime.AddMinutes(1));
        }
    }

    internal static void Trim(string root, Version requestedVersion, DateTimeOffset now, Version runningVersion)
    {
        try
        {
            if (!OwnedTemporaryFileCleanup.IsOrdinaryDirectory(root)) return;
            var candidates = new List<(string Directory, Version Version, Receipt Receipt, byte[] Marker)>();
            foreach (var directory in Directory.EnumerateDirectories(root).Take(128))
            {
                try
                {
                    if (!Version.TryParse(Path.GetFileName(directory), out var version)
                        || version.ToString() != Path.GetFileName(directory)
                        || !OwnedTemporaryFileCleanup.IsOrdinaryDirectory(directory)) continue;
                    var marker = new FileInfo(Path.Combine(directory, Marker));
                    if (!marker.Exists || marker.Length > 2048 || (marker.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    byte[] bytes;
                    using (var file = new FileStream(marker.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (file.Length is <= 0 or > 2048) continue;
                        bytes = new byte[(int)file.Length];
                        file.ReadExactly(bytes);
                        if (file.ReadByte() != -1) continue;
                    }
                    var receipt = JsonSerializer.Deserialize<Receipt>(bytes);
                    if (receipt is null || receipt.Owner != "keyboard.wtf.update-cache.v1"
                        || receipt.Version != version.ToString() || receipt.Bytes is <= 0 or > UpdateManifestVerifier.MaximumPackageBytes
                        || receipt.Sha256 is null || receipt.Sha256.Length != 64 || receipt.Sha256.Any(c => !Uri.IsHexDigit(c))
                        || receipt.RecordedUtc == default || receipt.RecordedUtc > now) continue;
                    candidates.Add((directory, version, receipt, bytes));
                }
                catch { } // Unknown or unreadable cache entries are not cleanup authority.
            }
            // Keep the requested/running version plus two newest other versions.
            var preserved = candidates.Where(item => !SameVersion(item.Version, requestedVersion))
                .OrderByDescending(item => item.Version).Take(2).Select(item => item.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cutoff = now.AddDays(-30);
            var attempts = 0;
            foreach (var item in candidates.OrderBy(item => item.Receipt.RecordedUtc).ThenBy(item => item.Version))
            {
                if (SameVersion(item.Version, requestedVersion) || SameVersion(item.Version, runningVersion)
                    || preserved.Contains(item.Directory) || item.Receipt.RecordedUtc > cutoff) continue;
                if (attempts++ >= 2) break; // At most two package hash attempts, never a multi-GB sweep.
                var installer = Path.Combine(item.Directory, "keyboard.wtf-setup.exe");
                if (!OwnedTemporaryFileCleanup.TryDelete(installer, item.Directory, cutoff.UtcDateTime,
                    item.Receipt.Bytes, item.Receipt.Sha256)) continue;
                // Remove only the unchanged marker read above, through another
                // exclusive verified handle; no directory or arbitrary leaf is removed.
                OwnedTemporaryFileCleanup.TryDelete(Path.Combine(item.Directory, Marker), item.Directory,
                    now.UtcDateTime, item.Marker.Length, Convert.ToHexString(SHA256.HashData(item.Marker)));
            }
        }
        catch { } // Retain bytes on busy, path, permissions or metadata failure.
    }

    private static bool SameVersion(Version first, Version second) => first is not null && second is not null
        && first.Major == second.Major && first.Minor == second.Minor
        && Math.Max(0, first.Build) == Math.Max(0, second.Build)
        && Math.Max(0, first.Revision) == Math.Max(0, second.Revision);
}
