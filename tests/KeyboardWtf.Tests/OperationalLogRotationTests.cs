using System.Diagnostics;
using System.Reflection;
using System.Text;
using Xunit;

namespace KeyboardWtf.Tests;

// Synthetic path-only tests. Never initialize AppLog or load an installed
// settings/profile path; the production formatter and exception privacy are
// independently covered by OperationalLogPrivacyTests.
public sealed class OperationalLogRotationTests
{
    private const long ProductionFileLimit = 5L * 1024 * 1024;
    private const int EntryLimit = 32 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Fact]
    public void Ordinary_short_line_is_unchanged_and_appended_once_without_creating_archives()
    {
        using var fixture = new Fixture();
        const string line = "2026-10-04T12:00:00+00:00 [INFO] synthetic operational event";

        Assert.True(Write(fixture.Current, line, ProductionFileLimit));
        Assert.Equal(StrictUtf8.GetBytes(line + Environment.NewLine), File.ReadAllBytes(fixture.Current));
        Assert.Equal(["keyboard.wtf.log"], fixture.Names());
        Assert.True(Write(fixture.Current, "second event", ProductionFileLimit));
        Assert.Equal(line + Environment.NewLine + "second event" + Environment.NewLine, File.ReadAllText(fixture.Current, StrictUtf8));
        Assert.Equal(["keyboard.wtf.log"], fixture.Names());
    }

    [Fact]
    public void A_line_that_exactly_fits_the_file_bound_does_not_rotate_or_add_delay()
    {
        using var fixture = new Fixture();
        const long maximum = 128;
        var line = new string('x', (int)maximum - StrictUtf8.GetByteCount(Environment.NewLine));

        Assert.True(Write(fixture.Current, line, maximum));
        Assert.Equal(maximum, new FileInfo(fixture.Current).Length);
        Assert.False(File.Exists(fixture.Archive(1)));
        Assert.False(File.Exists(fixture.Archive(2)));
    }

    [Fact]
    public void Rotation_preserves_the_current_and_previous_generation_and_prunes_only_the_oldest_exact_archive()
    {
        using var fixture = new Fixture();
        const long maximum = 128;
        var current = StrictUtf8.GetBytes(new string('c', (int)maximum));
        var previous = "previous archive generation"u8.ToArray();
        var oldest = "oldest archive generation"u8.ToArray();
        File.WriteAllBytes(fixture.Current, current);
        File.WriteAllBytes(fixture.Archive(1), previous);
        File.WriteAllBytes(fixture.Archive(2), oldest);
        var unrelated = fixture.SeedUnrelatedFiles();

        Assert.True(Write(fixture.Current, "new generation", maximum));
        Assert.Equal("new generation" + Environment.NewLine, File.ReadAllText(fixture.Current, StrictUtf8));
        Assert.Equal(current, File.ReadAllBytes(fixture.Archive(1)));
        Assert.Equal(previous, File.ReadAllBytes(fixture.Archive(2)));
        fixture.AssertUnrelatedFiles(unrelated);
        fixture.AssertOwnedFileBounds(maximum);
    }

    [Fact]
    public void Repeated_rotations_keep_only_three_bounded_owned_generations_without_touching_similar_names()
    {
        using var fixture = new Fixture();
        const long maximum = 128;
        var unrelated = fixture.SeedUnrelatedFiles();

        for (var index = 0; index < 100; index++)
        {
            Assert.True(Write(fixture.Current, $"event-{index:D3}-" + new string('x', 75), maximum));
            fixture.AssertOwnedFileBounds(maximum);
        }
        Assert.Equal(3, fixture.OwnedNames().Length);
        Assert.Contains("event-099-", File.ReadAllText(fixture.Current, StrictUtf8));
        Assert.Contains("event-098-", File.ReadAllText(fixture.Archive(1), StrictUtf8));
        Assert.Contains("event-097-", File.ReadAllText(fixture.Archive(2), StrictUtf8));
        fixture.AssertUnrelatedFiles(unrelated);
        Assert.Equal(unrelated.Keys.Append("keyboard.wtf.log").Append("keyboard.wtf.log.1").Append("keyboard.wtf.log.2")
            .Order(StringComparer.Ordinal), fixture.Names());
    }

    [Theory]
    [InlineData("current")]
    [InlineData("archives")]
    [InlineData("all")]
    public void Successful_write_bounds_preexisting_oversized_owned_logs_without_deleting_unrelated_files(string oversized)
    {
        using var fixture = new Fixture();
        const long maximum = 256;
        File.WriteAllBytes(fixture.Current, StrictUtf8.GetBytes(new string('c', oversized is "current" or "all" ? 4096 : 32)));
        File.WriteAllBytes(fixture.Archive(1), StrictUtf8.GetBytes(new string('p', oversized is "archives" or "all" ? 4096 : 32)));
        File.WriteAllBytes(fixture.Archive(2), StrictUtf8.GetBytes(new string('o', oversized is "archives" or "all" ? 4096 : 32)));
        var unrelated = fixture.SeedUnrelatedFiles();

        Assert.True(Write(fixture.Current, "after legacy normalization", maximum));
        fixture.AssertOwnedFileBounds(maximum);
        Assert.Contains("after legacy normalization", File.ReadAllText(fixture.Current, StrictUtf8));
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("two_byte")]
    [InlineData("three_byte")]
    [InlineData("surrogate_pair")]
    public void Oversized_entry_is_bounded_in_UTF8_bytes_without_splitting_a_Unicode_scalar(string kind)
    {
        using var fixture = new Fixture();
        var unit = kind switch
        {
            "ascii" => "a",
            "two_byte" => "é",
            "three_byte" => "界",
            "surrogate_pair" => "😀",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var line = "[INFO] bounded unicode prefix: " + string.Concat(Enumerable.Repeat(unit, EntryLimit));

        Assert.True(Write(fixture.Current, line, ProductionFileLimit));
        var bytes = File.ReadAllBytes(fixture.Current);
        Assert.InRange(bytes.Length, 1, EntryLimit);
        var decoded = StrictUtf8.GetString(bytes);
        Assert.StartsWith("[INFO] bounded unicode prefix: ", decoded);
        Assert.Contains("[truncated]", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Environment.NewLine, decoded);
        Assert.DoesNotContain("\uFFFD", decoded);
        Assert.Equal(["keyboard.wtf.log"], fixture.Names());
    }

    [Fact]
    public async Task Concurrent_in_process_calls_each_append_once_without_interleaving_or_duplicates()
    {
        using var fixture = new Fixture();
        const int count = 128;
        var writes = Enumerable.Range(0, count)
            .Select(index => Task.Run(() => Write(fixture.Current, $"concurrent-entry-{index:D3}", ProductionFileLimit)))
            .ToArray();

        Assert.All(await Task.WhenAll(writes), success => Assert.True(success));
        var lines = File.ReadAllLines(fixture.Current, StrictUtf8);
        Assert.Equal(count, lines.Length);
        Assert.Equal(count, lines.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"concurrent-entry-{index:D3}").Order(StringComparer.Ordinal),
            lines.Order(StringComparer.Ordinal));
        fixture.AssertOwnedFileBounds(ProductionFileLimit);
        Assert.Equal(["keyboard.wtf.log"], fixture.Names());
    }

    [Fact]
    public void Sharing_failure_is_fail_open_and_does_not_change_current_or_unrelated_files()
    {
        using var fixture = new Fixture();
        var original = "locked log stays unchanged"u8.ToArray();
        File.WriteAllBytes(fixture.Current, original);
        var unrelated = fixture.SeedUnrelatedFiles();
        using (var held = new FileStream(fixture.Current, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Write(fixture.Current, "cannot acquire file", ProductionFileLimit));

        Assert.Equal(original, File.ReadAllBytes(fixture.Current));
        fixture.AssertUnrelatedFiles(unrelated);
        Assert.False(File.Exists(fixture.Archive(1)));
        Assert.False(File.Exists(fixture.Archive(2)));
    }

    [Fact]
    public void Rotation_rename_failure_does_not_retry_or_append_past_the_cap_and_keeps_the_current_bytes()
    {
        using var fixture = new Fixture();
        const long maximum = 128;
        var original = StrictUtf8.GetBytes(new string('c', (int)maximum));
        File.WriteAllBytes(fixture.Current, original);
        File.WriteAllText(fixture.Archive(1), "locked prior generation");
        File.WriteAllText(fixture.Archive(2), "oldest exact owned generation");
        var unrelated = fixture.SeedUnrelatedFiles();
        using (var held = new FileStream(fixture.Archive(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Write(fixture.Current, "cannot rotate now", maximum));

        Assert.Equal(original, File.ReadAllBytes(fixture.Current));
        Assert.Equal("locked prior generation", File.ReadAllText(fixture.Archive(1)));
        fixture.AssertOwnedFileBounds(maximum);
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Fact]
    public void Directory_at_current_log_path_causes_truthful_failure_without_recursive_cleanup()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Current);
        var child = Path.Combine(fixture.Current, "owned-sentinel.txt");
        File.WriteAllText(child, "must not recursively delete this directory");
        var unrelated = fixture.SeedUnrelatedFiles();

        Assert.False(Write(fixture.Current, "cannot append to directory", ProductionFileLimit));
        Assert.Equal("must not recursively delete this directory", File.ReadAllText(child));
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Invalid_file_bound_has_no_filesystem_effect(long maximum)
    {
        using var fixture = new Fixture();
        var unrelated = fixture.SeedUnrelatedFiles();

        Assert.False(Write(fixture.Current, "invalid size descriptor", maximum));
        Assert.Empty(fixture.OwnedNames());
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Theory]
    [InlineData(15L)]
    [InlineData(5L * 1024 * 1024 + 1)]
    [InlineData(long.MaxValue)]
    public void File_bound_outside_the_fixed_policy_is_rejected_without_writing(long maximum)
    {
        using var fixture = new Fixture();
        var unrelated = fixture.SeedUnrelatedFiles();

        Assert.False(Write(fixture.Current, "invalid bounded policy", maximum));
        Assert.Empty(fixture.OwnedNames());
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("relative")]
    [InlineData("wrong_basename")]
    [InlineData("archive_as_current")]
    [InlineData("trailing_dot")]
    [InlineData("missing_parent")]
    public void Invalid_or_unavailable_exact_log_path_is_fail_open_without_touching_similar_names(string invalid)
    {
        using var fixture = new Fixture();
        var unrelated = fixture.SeedUnrelatedFiles();
        var before = fixture.Names();
        var path = invalid switch
        {
            "null" => null,
            "empty" => "",
            "relative" => "keyboard.wtf.log",
            "wrong_basename" => Path.Combine(fixture.Root, "another.log"),
            "archive_as_current" => fixture.Archive(1),
            "trailing_dot" => fixture.Current + ".",
            "missing_parent" => Path.Combine(fixture.Root, "not-created", "keyboard.wtf.log"),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };

        Assert.False(Write(path, "must not escape the exact file boundary", ProductionFileLimit));
        Assert.Equal(before, fixture.Names());
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Theory]
    [InlineData(16L)]
    [InlineData(17L)]
    [InlineData(19L)]
    [InlineData(32L)]
    [InlineData(63L)]
    public void Small_file_limit_truncates_Unicode_to_a_complete_scalar_or_marker_only(long maximum)
    {
        using var fixture = new Fixture();
        var line = string.Concat(Enumerable.Repeat("😀", 128));

        Assert.True(Write(fixture.Current, line, maximum));
        var bytes = File.ReadAllBytes(fixture.Current);
        Assert.InRange(bytes.Length, 1, (int)maximum);
        var decoded = StrictUtf8.GetString(bytes);
        Assert.EndsWith("[truncated]" + Environment.NewLine, decoded);
        Assert.DoesNotContain("\uFFFD", decoded);
        fixture.AssertOwnedFileBounds(maximum);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Directory_at_an_archive_path_fails_before_any_owned_or_unrelated_file_is_changed(int archive)
    {
        using var fixture = new Fixture();
        var current = "current log must remain"u8.ToArray();
        File.WriteAllBytes(fixture.Current, current);
        Directory.CreateDirectory(fixture.Archive(archive));
        var child = Path.Combine(fixture.Archive(archive), "owned-sentinel.txt");
        File.WriteAllText(child, "archive directory must not be recursively deleted");
        var unrelated = fixture.SeedUnrelatedFiles();

        Assert.False(Write(fixture.Current, "must fail before writing", ProductionFileLimit));
        Assert.Equal(current, File.ReadAllBytes(fixture.Current));
        Assert.Equal("archive directory must not be recursively deleted", File.ReadAllText(child));
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Fact]
    public void Oversized_locked_archive_failure_never_changes_the_current_log_or_unrelated_files()
    {
        using var fixture = new Fixture();
        const long maximum = 128;
        var current = "current log must remain"u8.ToArray();
        var archive = Encoding.UTF8.GetBytes(new string('a', 512));
        File.WriteAllBytes(fixture.Current, current);
        File.WriteAllBytes(fixture.Archive(1), archive);
        var unrelated = fixture.SeedUnrelatedFiles();
        using (var held = new FileStream(fixture.Archive(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Write(fixture.Current, "must fail without retry", maximum));

        Assert.Equal(current, File.ReadAllBytes(fixture.Current));
        Assert.Equal(archive, File.ReadAllBytes(fixture.Archive(1)));
        fixture.AssertUnrelatedFiles(unrelated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reparse_parent_or_ancestor_is_rejected_without_writing_through_to_the_other_fixture_directory(bool ancestor)
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "other-owned-directory");
        Directory.CreateDirectory(target);
        var targetLogs = ancestor ? Path.Combine(target, "logs") : target;
        Directory.CreateDirectory(targetLogs);
        var targetCurrent = Path.Combine(targetLogs, "keyboard.wtf.log");
        var original = "reparse target must not be written or rotated"u8.ToArray();
        File.WriteAllBytes(targetCurrent, original);
        File.WriteAllBytes(targetCurrent + ".1", "untouched target archive"u8.ToArray());
        var link = Path.Combine(fixture.Root, "owned-junction");
        CreateOwnedJunction(fixture, link, target);
        var path = ancestor ? Path.Combine(link, "logs", "keyboard.wtf.log") : Path.Combine(link, "keyboard.wtf.log");
        try
        {
            Assert.False(Write(path, "must not write through a reparse ancestor", ProductionFileLimit));
            Assert.Equal(original, File.ReadAllBytes(targetCurrent));
            Assert.Equal("untouched target archive"u8.ToArray(), File.ReadAllBytes(targetCurrent + ".1"));
            Assert.False(File.Exists(targetCurrent + ".2"));
        }
        finally
        {
            fixture.AssertOwnedDirectChild(link);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(link, recursive: false);
        }
    }

    private static void CreateOwnedJunction(Fixture fixture, string link, string target)
    {
        fixture.AssertOwnedDirectChild(link);
        fixture.AssertOwnedDirectChild(target);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
        };
        using var process = Process.Start(start);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(5000), "Owned test junction creation did not finish.");
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private static bool Write(string exactPath, string line, long maximumFileBytes)
    {
        var method = typeof(AppLog).GetMethod("TryWriteLineToFile", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, [typeof(string), typeof(string), typeof(long)], modifiers: null);
        Assert.NotNull(method); // Red until disk writes share a bounded path-only core.
        Assert.Equal(typeof(bool), method.ReturnType);
        return Assert.IsType<bool>(method.Invoke(null, [exactPath, line, maximumFileBytes]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private readonly string _name = $"kwt-log-rotation-{Guid.NewGuid():N}";
        internal string Root { get; }
        internal string Current => Path.Combine(Root, "keyboard.wtf.log");
        internal Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(_parent, _name));
            Directory.CreateDirectory(Root);
        }
        internal string Archive(int index) => Current + "." + index;
        internal string[] Names() => Directory.GetFileSystemEntries(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        internal string[] OwnedNames() => Names().Where(name => name is "keyboard.wtf.log" or "keyboard.wtf.log.1" or "keyboard.wtf.log.2").ToArray();
        internal Dictionary<string, byte[]> SeedUnrelatedFiles()
        {
            var names = new[] { "another.log", "keyboard.wtf.log.3", "keyboard.wtf.log.1.backup", "keyboard.wtf.log.old", "keyboard.wtf.log.2.json" };
            var result = names.ToDictionary(name => name, name => StrictUtf8.GetBytes("unrelated exact-name sentinel: " + name));
            foreach (var file in result) File.WriteAllBytes(Path.Combine(Root, file.Key), file.Value);
            return result;
        }
        internal void AssertOwnedDirectChild(string path)
        {
            Assert.Equal(Root, Path.GetDirectoryName(Path.GetFullPath(path)), ignoreCase: true);
            Assert.NotEqual(Root, Path.GetFullPath(path));
        }
        internal void AssertUnrelatedFiles(Dictionary<string, byte[]> expected)
        {
            foreach (var file in expected) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(Root, file.Key)));
        }
        internal void AssertOwnedFileBounds(long maximum)
        {
            foreach (var name in OwnedNames())
                Assert.InRange(new FileInfo(Path.Combine(Root, name)).Length, 0L, maximum);
        }
        public void Dispose()
        {
            Assert.Equal(_parent, Path.GetDirectoryName(Root), ignoreCase: true);
            Assert.Equal(_name, Path.GetFileName(Root));
            Assert.True(_name.StartsWith("kwt-log-rotation-", StringComparison.Ordinal)
                && Guid.TryParseExact(_name["kwt-log-rotation-".Length..], "N", out _));
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
