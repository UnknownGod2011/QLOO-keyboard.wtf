using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using KeyboardWtf.Services.Updates;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class UpdateIngressNamespaceTests
{
    [Fact]
    public void Nonempty_ancestor_reparse_mutation_is_refused_without_losing_original_marker_ownership()
    {
        var root = NewRoot();
        var ancestor = Path.Combine(root, "original");
        var installed = Path.Combine(ancestor, "installed");
        var redirected = Path.Combine(root, "redirected");
        var otherInstalled = Path.Combine(redirected, "installed");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(otherInstalled);
        using var entry = UpdateInstallationIngressLease.TryEnter(Path.Combine(installed, "keyboard.wtf.exe"));
        using var reservation = UpdateInstallationIngressLease.TryReserve(Path.Combine(installed, "keyboard.wtf.exe"));
        Assert.NotNull(entry);
        Assert.NotNull(reservation);
        Assert.Equal(entry.InstallationIdentity, reservation.InstallationIdentity);
        var buffer = JunctionBuffer(redirected);
        var refused = Assert.Throws<Win32Exception>(() => MakeJunction(ancestor, buffer));
        Assert.Equal(145, refused.NativeErrorCode); // Observed NTFS nonempty-directory rule, not sharing.
        Assert.False(File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(reservation.TryAcquireExclusive());
        Assert.Empty(Directory.GetFileSystemEntries(otherInstalled));
        entry.Dispose();
        Assert.True(reservation.TryAcquireExclusive());
        Assert.Empty(Directory.GetFileSystemEntries(otherInstalled));
        var control = NewRoot();
        MakeJunction(control, buffer);
        Assert.True(File.GetAttributes(control).HasFlag(FileAttributes.ReparsePoint));
        // The valid buffer succeeds on an empty owned control. Mutation of the
        // populated ancestor was not exercised successfully; do not claim it was.
    }

    [Fact]
    public void Installation_lifetime_does_not_block_unrelated_sibling_directory_rename()
    {
        var root = NewRoot();
        var installed = Path.Combine(root, "installed");
        var sibling = Path.Combine(root, "unrelated");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(sibling);
        using var lease = UpdateInstallationIngressLease.TryEnter(Path.Combine(installed, "keyboard.wtf.exe"));
        Assert.NotNull(lease);
        Directory.Move(sibling, sibling + "-renamed");
        Directory.Move(sibling + "-renamed", sibling);
    }

    [Fact]
    public void Normal_directory_writes_remain_available_but_zero_access_cannot_modify_reparse_data()
    {
        var root = NewRoot();
        var target = Path.Combine(root, "installed");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(destination);
        var executable = Path.Combine(target, "keyboard.wtf.exe");
        using var lease = UpdateInstallationIngressLease.TryEnter(executable);
        Assert.NotNull(lease);
        using (var write = Native.CreateFile(target, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert.False(write.IsInvalid);
        }
        var buffer = JunctionBuffer(destination);
        using (var metadataOnly = Native.CreateFile(target, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert.False(metadataOnly.IsInvalid);
            Assert.False(Native.SetReparse(metadataOnly, 0x900a4, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero));
            Assert.Equal(5, Marshal.GetLastWin32Error());
        }
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
        lease.Dispose();
        // Same buffer, original owned directory, actual positive control. Delete
        // only the two inert markers so control is independent of nonempty-dir rules.
        File.Delete(Path.Combine(target, ".keyboard.wtf-update-reservation"));
        File.Delete(Path.Combine(target, ".keyboard.wtf-update-activity"));
        MakeJunction(target, buffer);
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
        Assert.Throws<InvalidDataException>(() => UpdateInstallationIngressLease.TryEnter(executable));
        // Retain this isolated junction/receipt fixture; do not recursively delete it.
    }

    [Theory]
    [InlineData(".keyboard.wtf-update-reservation")]
    [InlineData(".keyboard.wtf-update-activity")]
    public void Hardlinked_marker_is_not_a_unique_installation_protocol_object(string marker)
    {
        var root = NewRoot();
        var executable = Path.Combine(root, "keyboard.wtf.exe");
        var outside = Path.Combine(root, "inert-original");
        File.WriteAllBytes(outside, []);
        Assert.True(Native.CreateHardLink(Path.Combine(root, marker), outside, IntPtr.Zero),
            "Owned hardlink fixture failed: " + Marshal.GetLastWin32Error());
        Assert.Throws<InvalidDataException>(() =>
        {
            using var reservation = UpdateInstallationIngressLease.TryReserve(executable);
            Assert.NotNull(reservation);
            reservation.TryAcquireExclusive();
        });
    }

    [Fact]
    public void Case_spelling_does_not_create_an_independent_gate_or_installation_identity()
    {
        var root = NewRoot();
        var executable = Path.Combine(root, "keyboard.wtf.exe");
        using var entry = UpdateInstallationIngressLease.TryEnter(executable);
        using var reserve = UpdateInstallationIngressLease.TryReserve(executable.ToUpperInvariant());
        Assert.NotNull(entry);
        Assert.NotNull(reserve);
        Assert.Equal(entry.InstallationIdentity, reserve.InstallationIdentity);
        Assert.Null(UpdateInstallationIngressLease.TryEnter(executable));
        Assert.False(reserve.TryAcquireExclusive());
        entry.Dispose();
        Assert.True(reserve.TryAcquireExclusive());
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "kwt-ingress-namespace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] JunctionBuffer(string destination)
    {
        var substitute = "\\??\\" + destination;
        var print = destination;
        var path = Encoding.Unicode.GetBytes(substitute + "\0" + print + "\0");
        var buffer = new byte[16 + path.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)(substitute.Length * 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)((substitute.Length + 1) * 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)(print.Length * 2)));
        path.CopyTo(buffer, 16);
        return buffer;
    }

    private static void MakeJunction(string path, byte[] buffer)
    {
        using var handle = Native.CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Native.SetReparse(handle, 0x900a4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetReparse(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
            IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(string link, string target, IntPtr security);
    }
}
