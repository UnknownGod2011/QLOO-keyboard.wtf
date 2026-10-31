using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using KeyboardWtf.Services.Updates;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Tests;

// Inert filesystem probe only. No executable, app, host, installer or profile runs.
// Retain every uniquely owned fixture, including positive-control evidence.
public sealed class UpdateIngressPosixReplacementTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(".keyboard.wtf-update-reservation", true)]
    [InlineData(".keyboard.wtf-update-activity", true)]
    [InlineData(".keyboard.wtf-update-activity", false)]
    public void Posix_hardlink_replacement_cannot_split_held_marker_ownership(string marker, bool exclusiveActivity)
    {
        Assert.True(OperatingSystem.IsWindows());
        var tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var fixtureName = "kwt-ingress-posix-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(tempParent, fixtureName);
        Assert.Equal(tempParent, Path.GetDirectoryName(root), StringComparer.OrdinalIgnoreCase);
        Assert.True(Guid.TryParseExact(fixtureName["kwt-ingress-posix-".Length..], "N", out _));
        Directory.CreateDirectory(root);
        output.WriteLine("OWNED_FIXTURE|" + root);

        var target = Path.Combine(root, marker);
        var source = Path.Combine(root, "inert-replacement-source");
        File.WriteAllBytes(target, []);
        File.WriteAllBytes(source, []);
        var originalTarget = Identity(target);
        var sourceIdentity = Identity(source);
        var buffer = LinkBuffer(target);
        using var sourceHandle = Native.CreateFile(source, 0xc0010000, 7, IntPtr.Zero,
            3, 0x00200000, IntPtr.Zero); // READ/WRITE/DELETE, share all, open reparse leaf.
        Assert.False(sourceHandle.IsInvalid, "Source open failed: " + Marshal.GetLastWin32Error());

        int heldStatus;
        FileIdentity heldTarget;
        using var entry = exclusiveActivity ? null
            : UpdateInstallationIngressLease.TryEnter(Path.Combine(root, "keyboard.wtf.exe"));
        if (!exclusiveActivity) Assert.NotNull(entry);
        using (var reservation = UpdateInstallationIngressLease.TryReserve(Path.Combine(root, "keyboard.wtf.exe")))
        {
            Assert.NotNull(reservation);
            Assert.Equal(exclusiveActivity, reservation.TryAcquireExclusive());
            heldStatus = Native.SetInformation(sourceHandle, out _, buffer, (uint)buffer.Length, 72);
            heldTarget = Identity(target);
            Record(exclusiveActivity ? "HELD_EXCLUSIVE" : "HELD_SHARED", marker, heldStatus);
            output.WriteLine($"HELD_TARGET|{heldTarget}|ORIGINAL_TARGET|{originalTarget}|SOURCE|{sourceIdentity}");
        }
        entry?.Dispose();

        // If the held attempt unexpectedly succeeded, restore a distinct target
        // AFTER ownership ends. This ensures the control performs real replacement,
        // rather than returning success for a destination already linked to source.
        if (heldStatus >= 0)
        {
            File.Delete(target);
            File.WriteAllBytes(target, []);
        }
        var controlTarget = Identity(target);
        var controlStatus = Native.SetInformation(sourceHandle, out _, buffer, (uint)buffer.Length, 72);
        Record("POSITIVE_CONTROL", marker, controlStatus);
        Assert.True(controlStatus == 0,
            "POSIX replacement control failed; this run cannot establish compatibility or refusal. "
            + Result(heldStatus) + "; control " + Result(controlStatus));
        Assert.Equal(sourceIdentity, Identity(target));
        Assert.NotEqual(controlTarget, Identity(target));

        // Only the named inert source link is removed. The target is now the sole
        // link, matching the core's single-link marker rule, and remains preserved.
        File.Delete(source);
        using (var targetMetadata = OpenMetadata(target))
        {
            Assert.True(Native.GetStandardInformation(targetMetadata, 1, out var standard, 24));
            Assert.Equal(1u, standard.NumberOfLinks);
            Assert.Equal(0L, standard.EndOfFile);
            output.WriteLine($"CONTROL_TARGET|{Identity(target)}|LINKS|{standard.NumberOfLinks}|EOF|{standard.EndOfFile}");
        }

        Assert.True(heldStatus < 0,
            "A POSIX hard-link replacement changed a held protocol marker: " + Result(heldStatus));
        Assert.Equal(originalTarget, heldTarget);
    }

    private void Record(string phase, string marker, int status) =>
        output.WriteLine(phase + "|" + marker + "|" + Result(status));

    private static string Result(int status) =>
        $"NTSTATUS|0x{unchecked((uint)status):X8}|WIN32|{Native.StatusToError(status)}";

    private static byte[] LinkBuffer(string target)
    {
        // FILE_LINK_INFORMATION_EX: ULONG flags, pointer-sized aligned root,
        // ULONG name byte length, then an absolute native Unicode destination.
        var name = Encoding.Unicode.GetBytes("\\??\\" + target);
        var lengthOffset = IntPtr.Size == 8 ? 16 : 8;
        var nameOffset = lengthOffset + 4;
        var buffer = new byte[nameOffset + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 3); // REPLACE_IF_EXISTS | POSIX_SEMANTICS.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lengthOffset), checked((uint)name.Length));
        name.CopyTo(buffer, nameOffset);
        return buffer;
    }

    private static SafeFileHandle OpenMetadata(string path)
    {
        var handle = Native.CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static FileIdentity Identity(string path)
    {
        using var handle = OpenMetadata(path);
        if (!Native.GetIdentityInformation(handle, 18, out var identity, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return identity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong Low, ulong High);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StandardInfo
        {
            internal long AllocationSize;
            internal long EndOfFile;
            internal uint NumberOfLinks;
            internal byte DeletePending;
            internal byte Directory;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
        internal static extern int SetInformation(SafeFileHandle handle, out IoStatus status,
            byte[] information, uint size, int kind);
        [DllImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
        internal static extern uint StatusToError(int status);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIdentityInformation(SafeFileHandle handle, int kind,
            out FileIdentity identity, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetStandardInformation(SafeFileHandle handle, int kind,
            out StandardInfo info, uint size);
    }
}
