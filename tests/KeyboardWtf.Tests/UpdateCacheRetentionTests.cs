using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class UpdateCacheRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-update-retention-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    public UpdateCacheRetentionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Old_owned_installers_are_trimmed_but_requested_running_and_two_newest_are_preserved()
    {
        var running = Owned("1.0.0");
        var old = Owned("1.1.0");
        var newerOld = Owned("1.2.0");
        var previous = Owned("1.3.0");
        var newest = Owned("1.4.0");
        var requested = Owned("1.5.0");

        UpdateCacheRetention.Trim(_root, new Version(1, 5, 0), _now, new Version(1, 0, 0, 0));

        Assert.True(File.Exists(running));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(newerOld));
        Assert.True(File.Exists(previous));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(requested));
        Assert.True(Directory.Exists(Path.GetDirectoryName(old))); // Never recursively delete a cache version directory.
    }

    [Fact]
    public void Busy_or_changed_owned_bytes_are_retained()
    {
        var busy = Owned("1.0.0");
        var changed = Owned("1.1.0");
        Owned("1.2.0"); Owned("1.3.0"); Owned("1.4.0");
        File.WriteAllText(changed, "changed fixture bytes");
        File.SetLastWriteTimeUtc(changed, _now.AddDays(-60).UtcDateTime);

        using (var held = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
            UpdateCacheRetention.Trim(_root, new Version(1, 4, 0), _now, new Version(0, 1));

        Assert.True(File.Exists(busy));
        Assert.Equal("changed fixture bytes", File.ReadAllText(changed));
    }

    [Fact]
    public void Unmarked_corrupt_and_nonversion_entries_are_not_cleanup_authority()
    {
        var unmarked = Path.Combine(_root, "0.8.0", "keyboard.wtf-setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(unmarked));
        File.WriteAllText(unmarked, "legacy verified installer retained");
        var corrupt = Owned("0.9.0");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(corrupt), "keyboard.wtf-owned-download-v1.json"), "{}");
        var unrelated = Path.Combine(_root, "user-copy");
        Directory.CreateDirectory(unrelated);
        var sentinel = Path.Combine(unrelated, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        Owned("1.2.0"); Owned("1.3.0"); Owned("1.4.0");

        UpdateCacheRetention.Trim(_root, new Version(1, 4, 0), _now, new Version(0, 1));

        Assert.Equal("legacy verified installer retained", File.ReadAllText(unmarked));
        Assert.True(File.Exists(corrupt));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Recent_owned_cache_is_not_evicted_even_when_older_than_two_newer_versions()
    {
        var recent = Owned("1.0.0", _now.AddDays(-3));
        Owned("1.1.0"); Owned("1.2.0"); Owned("1.3.0");
        UpdateCacheRetention.Trim(_root, new Version(1, 3, 0), _now, new Version(0, 1));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void One_trim_attempt_is_limited_to_two_old_installers_even_with_many_eligible_versions()
    {
        var paths = Enumerable.Range(0, 8).Select(index => Owned($"1.{index}.0")).ToArray();
        UpdateCacheRetention.Trim(_root, new Version(1, 7, 0), _now, new Version(0, 1));
        Assert.Equal(2, paths.Count(path => !File.Exists(path)));
        Assert.False(File.Exists(paths[0]));
        Assert.False(File.Exists(paths[1]));
        Assert.All(paths.Skip(2), path => Assert.True(File.Exists(path)));
    }

    private string Owned(string version, DateTimeOffset? recorded = null)
    {
        var when = recorded ?? _now.AddDays(-60);
        var bytes = Encoding.UTF8.GetBytes("verified update fixture " + version);
        var parsed = Version.Parse(version);
        var directory = Path.Combine(_root, version);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "keyboard.wtf-setup.exe");
        File.WriteAllBytes(path, bytes);
        var update = new VerifiedUpdate(parsed, new Version(0, 1), "stable", when,
            new Uri("https://github.com/example/releases/download/fixture/setup.exe"),
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "fixture", "fixture-key");
        UpdateCacheRetention.RecordAndTrim(_root, update, when, new Version(0, 1));
        File.SetLastWriteTimeUtc(path, when.UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(directory, "keyboard.wtf-owned-download-v1.json"), when.UtcDateTime);
        return path;
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
