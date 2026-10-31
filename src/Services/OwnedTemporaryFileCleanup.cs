using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KeyboardWtf.Services;

// Deletes one already-owned leaf through its verified exclusive native handle.
// No directories, recursive scans, path-based post-validation deletes or retries.
internal static class OwnedTemporaryFileCleanup
{
    internal static bool TryDelete(string path, string expectedDirectory, DateTime cutoffUtc,
        long? expectedBytes = null, string expectedSha256 = null)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory));
            if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(expectedDirectory)
                || !string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)) return false;
            using var handle = CreateFile(full, 0x80010080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                || (info.Attributes & (0x10u | 0x400u | 1u)) != 0 || info.Links != 1) return false;
            var final = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity) return false;
            var actual = final.ToString();
            if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (!actual.Equals(full, StringComparison.OrdinalIgnoreCase)) return false;
            var modified = DateTime.FromFileTimeUtc(((long)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime);
            if (modified > cutoffUtc.ToUniversalTime()) return false;
            var bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
            if (expectedBytes is long required && bytes != required) return false;
            if (expectedSha256 is not null)
            {
                if (expectedBytes is null or < 0 or > 250L * 1024 * 1024
                    || expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c))) return false;
                using var stream = new FileStream(handle, FileAccess.Read);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(expectedSha256)))
                    return false;
                var disposition = new Disposition { Delete = true };
                return SetFileInformationByHandle(handle, 4, ref disposition, Marshal.SizeOf<Disposition>());
            }
            var remove = new Disposition { Delete = true };
            return SetFileInformationByHandle(handle, 4, ref remove, Marshal.SizeOf<Disposition>());
        }
        catch
        {
            return false; // Busy/unknown/changed files are retained; cleanup never breaks the requested operation.
        }
    }

    internal static bool IsOrdinaryDirectory(string directory)
    {
        try
        {
            for (var path = Path.GetFullPath(directory); path is not null; path = Path.GetDirectoryName(path))
            {
                if (!Directory.Exists(path)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return Directory.Exists(directory);
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition { [MarshalAs(UnmanagedType.U1)] internal bool Delete; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int information,
        ref Disposition value, int size);
}
