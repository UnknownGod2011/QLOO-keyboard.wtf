using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class OwnedTemporaryFileCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-owned-cleanup-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;
    public OwnedTemporaryFileCleanupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Expired_leaf_is_deleted_by_verified_handle_without_deleting_directory_or_neighbors()
    {
        var expired = Create("expired.tmp");
        var neighbor = Create("keep.txt");
        Assert.True(OwnedTemporaryFileCleanup.TryDelete(expired, _root, _now.AddDays(-7)));
        Assert.False(File.Exists(expired));
        Assert.True(Directory.Exists(_root));
        Assert.Equal("fixture", File.ReadAllText(neighbor));
    }

    [Fact]
    public void Recent_future_and_wrong_root_leaves_are_retained()
    {
        var path = Create("recent.tmp");
        File.SetLastWriteTimeUtc(path, _now);
        Assert.False(OwnedTemporaryFileCleanup.TryDelete(path, _root, _now.AddDays(-7)));
        File.SetLastWriteTimeUtc(path, _now.AddDays(1));
        Assert.False(OwnedTemporaryFileCleanup.TryDelete(path, _root, _now));
        Assert.False(OwnedTemporaryFileCleanup.TryDelete(path, Path.Combine(_root, "other"), DateTime.MaxValue));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Held_leaf_and_directory_are_not_deleted()
    {
        var path = Create("busy.tmp");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(OwnedTemporaryFileCleanup.TryDelete(path, _root, _now));
        var directory = Path.Combine(_root, "keep-directory");
        Directory.CreateDirectory(directory);
        Assert.False(OwnedTemporaryFileCleanup.TryDelete(directory, _root, _now));
        Assert.True(File.Exists(path));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void Expected_hash_and_size_bind_cleanup_to_the_recorded_bytes()
    {
        var path = Create("verified.tmp");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture")));
        Assert.False(OwnedTemporaryFileCleanup.TryDelete(path, _root, _now, 1, hash));
        Assert.False(OwnedTemporaryFileCleanup.TryDelete(path, _root, _now, 7, new string('0', 64)));
        Assert.True(File.Exists(path));
        Assert.True(OwnedTemporaryFileCleanup.TryDelete(path, _root, _now, 7, hash));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Hard_linked_leaf_is_not_cleanup_authority()
    {
        var original = Create("keep.txt");
        var alias = Path.Combine(_root, "alias.tmp");
        Assert.True(CreateHardLink(alias, original, IntPtr.Zero), "NTFS fixture hard link must be created.");
        Assert.False(OwnedTemporaryFileCleanup.TryDelete(alias, _root, _now));
        Assert.Equal("fixture", File.ReadAllText(original));
        Assert.True(File.Exists(alias));
    }

    private string Create(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "fixture");
        File.SetLastWriteTimeUtc(path, _now.AddDays(-40));
        return path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
