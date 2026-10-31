using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KeyboardWtf.Services.Updates;

// Cooperative Windows admission primitive. It cannot authenticate executable
// bytes or certify native-idle, installer completion or relaunch. The independently trusted
// installation path is NOT accepted from a peer/profile/update command line.
internal sealed class UpdateInstallationIngressLease : IDisposable
{
    private const string ReservationMarker = ".keyboard.wtf-update-reservation";
    private const string ActivityMarker = ".keyboard.wtf-update-activity";
    private readonly object _sync = new();
    private NamespaceLease _namespace;
    private SafeFileHandle _reservation;
    private SafeFileHandle _activity;
    private readonly bool _reserved;

    internal InstallationFileIdentity InstallationIdentity { get; }

    private UpdateInstallationIngressLease(NamespaceLease names, SafeFileHandle reservation,
        SafeFileHandle activity, bool reserved)
    {
        _namespace = names;
        _reservation = reservation;
        _activity = activity;
        _reserved = reserved;
        InstallationIdentity = names.Identity;
    }

    internal static UpdateInstallationIngressLease TryEnter(string trustedInstallationExecutablePath) =>
        Acquire(trustedInstallationExecutablePath, reserved: false);

    internal static UpdateInstallationIngressLease TryReserve(string trustedInstallationExecutablePath) =>
        Acquire(trustedInstallationExecutablePath, reserved: true);

    private static UpdateInstallationIngressLease Acquire(string path, bool reserved)
    {
        var names = NamespaceLease.Acquire(path);
        SafeFileHandle reservation = null;
        SafeFileHandle activity = null;
        try
        {
            // Serialize the ENTIRE ingress transaction: an entrant cannot read
            // a stale free flag and acquire activity after the worker reserves.
            reservation = TryOpenMarker(names, ReservationMarker, exclusive: true);
            if (reservation is null) return null;
            if (!reserved)
            {
                activity = TryOpenMarker(names, ActivityMarker, exclusive: false);
                if (activity is null) return null;
                reservation.Dispose();
                reservation = null;
            }
            var lease = new UpdateInstallationIngressLease(names, reservation, activity, reserved);
            names = null;
            reservation = null;
            activity = null;
            return lease;
        }
        finally
        {
            activity?.Dispose();
            reservation?.Dispose();
            names?.Dispose();
        }
    }

    internal bool TryAcquireExclusive()
    {
        lock (_sync)
        {
            if (_namespace is null) throw new ObjectDisposedException(nameof(UpdateInstallationIngressLease));
            if (!_reserved) throw new InvalidOperationException("Ingress is not an update reservation.");
            // A busy activity probe does NOT release reservation. New GUI/host
            // ingress remains excluded while the original parent/hosts finish.
            _activity ??= TryOpenMarker(_namespace, ActivityMarker, exclusive: true);
            return _activity is not null;
        }
    }

    private static SafeFileHandle TryOpenMarker(NamespaceLease names, string marker, bool exclusive)
    {
        RequireAttributes(names.DirectoryHandle, directory: true);
        if (ReadIdentity(names.DirectoryHandle) != names.Identity)
            throw new InvalidDataException("The installation directory identity changed.");
        SafeFileHandle handle = null;
        try
        {
            // Handle-relative child opening cannot be redirected by an ancestor
            // name/reparse change between validation and a Win32 path open.
            var status = OpenName(names.DirectoryHandle, marker,
                (exclusive ? 0xc0000000u : 0x80000000u) | 0x00100000u,
                exclusive ? 0u : 1u, 3, 0x00200160, out handle);
            // OPEN_IF, NON_DIRECTORY, sync, open reparse leaf, COMPLETE_IF_OPLOCKED.
            // Never await an external oplock ACK while holding admission/_sync.
            if (status < 0)
            {
                var error = unchecked((int)Native.StatusToError(status));
                handle?.Dispose();
                handle = null;
                if (error == 32) return null;
                throw new Win32Exception(error);
            }
            if (status != 0 || handle is null || handle.IsInvalid)
                throw new InvalidDataException("The update ingress handle is unknown.");
        }
        catch { handle?.Dispose(); throw; }
        try
        {
            RequireAttributes(handle, directory: false);
            if (!Native.GetStandardInformation(handle, 1, out var standard, 24))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (standard.EndOfFile != 0 || standard.NumberOfLinks != 1
                || standard.DeletePending != 0 || standard.Directory != 0
                || !string.Equals(FinalPath(handle), FinalPath(names.DirectoryHandle) + "\\" + marker,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update ingress marker identity is invalid.");
            RequireAttributes(names.DirectoryHandle, directory: true);
            if (ReadIdentity(handle).VolumeSerialNumber != names.Identity.VolumeSerialNumber)
                throw new InvalidDataException("The update ingress marker volume is invalid.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static int OpenName(SafeFileHandle directory, string path, uint access,
        uint sharing, uint disposition, uint options, out SafeFileHandle handle)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(path);
        var unicodeBuffer = IntPtr.Zero;
        handle = null;
        try
        {
            var name = new Native.UnicodeName
            {
                Length = checked((ushort)(path.Length * 2)),
                MaximumLength = checked((ushort)((path.Length + 1) * 2)),
                Buffer = nameBuffer,
            };
            unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.UnicodeName>());
            Marshal.StructureToPtr(name, unicodeBuffer, false);
            var attributes = new Native.ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<Native.ObjectAttributes>(),
                RootDirectory = directory?.DangerousGetHandle() ?? IntPtr.Zero,
                ObjectName = unicodeBuffer,
                Attributes = 0x40, // OBJ_CASE_INSENSITIVE; callers own/pin the original root.
            };
            return Native.OpenFile(out handle, access, ref attributes, out _, IntPtr.Zero,
                0x80, sharing, disposition, options, IntPtr.Zero, 0);
        }
        catch { handle?.Dispose(); handle = null; throw; }
        finally
        {
            GC.KeepAlive(directory);
            if (unicodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            // Activity ends before admission resumes. Original leases, not UI
            // completion/cancellation, must control this call in future wiring.
            _activity?.Dispose();
            _activity = null;
            _reservation?.Dispose();
            _reservation = null;
            _namespace?.Dispose();
            _namespace = null;
        }
    }

    private sealed class NamespaceLease : IDisposable
    {
        private readonly List<SafeFileHandle> _pins = [];
        internal string FinalDirectory { get; private set; }
        internal InstallationFileIdentity Identity { get; private set; }
        internal SafeFileHandle DirectoryHandle { get; private set; }

        internal static NamespaceLease Acquire(string executable)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Update ingress requires Windows.");
            UpdatePathIdentity.RequireCanonicalLocalPath(executable);
            if (!string.Equals(Path.GetFileName(executable), "keyboard.wtf.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The trusted installation executable is invalid.");
            var result = new NamespaceLease();
            try
            {
                var directory = Path.GetDirectoryName(executable);
                var original = result.PinChain("\\\\?\\" + directory, "\\\\?\\" + Path.GetPathRoot(directory));
                result.Identity = ReadIdentity(original);
                result.FinalDirectory = FinalPath(original);
                const string volumePrefix = "\\\\?\\Volume{";
                var close = result.FinalDirectory.IndexOf('}');
                if (!result.FinalDirectory.StartsWith(volumePrefix, StringComparison.OrdinalIgnoreCase)
                    || close < volumePrefix.Length || close + 1 >= result.FinalDirectory.Length
                    || result.FinalDirectory[close + 1] != '\\'
                    || !Guid.TryParseExact(result.FinalDirectory[volumePrefix.Length..close], "D", out _))
                    throw new InvalidDataException("The installation volume identity is unsupported.");
                // Pin OS-resolved ancestry too, so mapped-drive/8.3 spelling is
                // not a second gate. No path hash, profile or claimed ID is used.
                var physical = result.PinChain(result.FinalDirectory, result.FinalDirectory[..(close + 2)]);
                if (ReadIdentity(physical) != result.Identity
                    || !string.Equals(FinalPath(physical), result.FinalDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installation namespace identity changed.");
                result.DirectoryHandle = physical;
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private SafeFileHandle PinChain(string path, string root)
        {
            var current = root;
            var leaf = Pin(current);
            foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or ".." || segment.Contains(':'))
                    throw new InvalidDataException("The installation ancestor is invalid.");
                current = current.TrimEnd('\\') + "\\" + segment;
                leaf = Pin(current);
            }
            return leaf;
        }

        private SafeFileHandle Pin(string path)
        {
            // Deny deletion/rename of these exact ancestors, but permit normal
            // directory writes: denying those blocked unrelated sibling renames.
            // Reparse mutation is not prevented by this sharing mode. Marker
            // opens are relative to the retained original directory handle.
            // CreateFileW has no COMPLETE_IF_OPLOCKED equivalent. Match the old
            // directory read/sharing/backup/reparse behavior using native OPEN,
            // but return rather than indefinitely waiting for an external ACK.
            if (!path.StartsWith("\\\\?\\", StringComparison.Ordinal))
                throw new InvalidDataException("The installation directory name is invalid.");
            var status = OpenName(null, "\\??\\" + path[4..], 0x80100000, 3,
                1, 0x00204121, out var handle); // DIRECTORY, sync, backup, reparse, COMPLETE_IF_OPLOCKED.
            if (status != 0 || handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                if (status < 0) throw new Win32Exception(unchecked((int)Native.StatusToError(status)));
                throw new InvalidDataException("The installation directory handle is unknown.");
            }
            try
            {
                RequireAttributes(handle, directory: true);
                _ = ReadIdentity(handle);
                _pins.Add(handle);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (var index = _pins.Count - 1; index >= 0; index--) _pins[index].Dispose();
            _pins.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct InstallationFileIdentity(ulong VolumeSerialNumber, ulong Low, ulong High);

    private static InstallationFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!Native.GetIdentityInformation(handle, 18, out var identity, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (identity.Low == 0 && identity.High == 0)
            throw new InvalidDataException("The installation file identity is unknown.");
        return identity;
    }

    private static void RequireAttributes(SafeFileHandle handle, bool directory)
    {
        if (!Native.GetAttributeInformation(handle, 9, out var info, 8))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            throw new InvalidDataException("The update ingress path is not an ordinary filesystem object.");
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(32768);
        var length = Native.GetFinalPath(handle, path, (uint)path.Capacity, 1);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= path.Capacity) throw new InvalidDataException("The installation path is too large.");
        return path.ToString().TrimEnd('\\');
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct UnicodeName { internal ushort Length; internal ushort MaximumLength; internal IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectAttributes
        {
            internal uint Length;
            internal IntPtr RootDirectory;
            internal IntPtr ObjectName;
            internal uint Attributes;
            internal IntPtr SecurityDescriptor;
            internal IntPtr SecurityQualityOfService;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct AttributesInfo { internal uint Attributes; internal uint ReparseTag; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StandardInfo
        {
            internal long AllocationSize;
            internal long EndOfFile;
            internal uint NumberOfLinks;
            internal byte DeletePending;
            internal byte Directory;
        }
        [DllImport("ntdll.dll", EntryPoint = "NtCreateFile")]
        internal static extern int OpenFile(out SafeFileHandle handle, uint access,
            ref ObjectAttributes attributes, out IoStatus status, IntPtr allocation, uint fileAttributes,
            uint share, uint disposition, uint options, IntPtr extendedAttributes, uint extendedSize);
        [DllImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
        internal static extern uint StatusToError(int status);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIdentityInformation(SafeFileHandle handle, int kind,
            out InstallationFileIdentity identity, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetAttributeInformation(SafeFileHandle handle, int kind,
            out AttributesInfo info, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetStandardInformation(SafeFileHandle handle, int kind,
            out StandardInfo info, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    }
}
