using KeyboardWtf.Destinations;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class CalendarDestinationTempTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-calendar-tests", Guid.NewGuid().ToString("N"));

    public CalendarDestinationTempTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Failed_shell_launch_removes_only_the_export_created_by_this_request()
    {
        await EstablishOwnedDirectoryAsync();
        var unrelated = Path.Combine(_root, "keep.ics");
        File.WriteAllText(unrelated, "user calendar");
        string export = null;
        var destination = new CalendarDestination(_root, path =>
        {
            export = path;
            Assert.True(File.Exists(path));
            throw new System.ComponentModel.Win32Exception("Fixture: calendar association unavailable");
        });

        Assert.False(await destination.SendAsync("{\"title\":\"Fixture event\"}"));

        Assert.NotNull(export);
        Assert.False(File.Exists(export));
        Assert.Equal("user calendar", File.ReadAllText(unrelated));
        Assert.Equal(unrelated, Assert.Single(Directory.GetFiles(_root, "*.ics")));
    }

    [Fact]
    public async Task Successful_shell_launch_preserves_complete_export_for_asynchronous_calendar_import()
    {
        string export = null;
        var destination = new CalendarDestination(_root, path =>
        {
            export = path;
            // The writer must be closed before the external calendar is started.
            using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var text = new StreamReader(read);
            var content = text.ReadToEnd();
            Assert.Contains("BEGIN:VCALENDAR", content);
            Assert.Contains("SUMMARY:Fixture event", content);
            Assert.Contains("END:VCALENDAR", content);
        });

        Assert.True(await destination.SendAsync("{\"title\":\"Fixture event\"}"));

        Assert.NotNull(export);
        Assert.True(File.Exists(export));
        Assert.Equal(export, Assert.Single(Directory.GetFiles(_root, "*.ics")));
    }

    [Fact]
    public async Task Unavailable_export_directory_does_not_touch_unrelated_files_or_invoke_shell()
    {
        var unavailable = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(unavailable, "keep");
        var launches = 0;
        var destination = new CalendarDestination(unavailable, _ => launches++);

        Assert.False(await destination.SendAsync("{\"title\":\"Fixture event\"}"));

        Assert.Equal(0, launches);
        Assert.Equal("keep", File.ReadAllText(unavailable));
    }

    [Fact]
    public async Task Expired_owned_export_is_removed_on_later_use_but_recent_busy_and_unknown_files_remain()
    {
        var destination = new CalendarDestination(_root, _ => { });
        Assert.True(await destination.SendAsync("{\"title\":\"Establish owned directory\"}"));
        var expired = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N") + ".ics");
        var busy = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N") + ".ics");
        var recent = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N") + ".ics");
        var unknown = Path.Combine(_root, "keyboard-wtf-old-legacy.ics");
        foreach (var path in new[] { expired, busy, recent, unknown })
        {
            File.WriteAllText(path, "fixture");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-20));
        }
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow);
        using (var held = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
            CalendarDestination.CleanupExpiredExports(_root, DateTime.UtcNow);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(busy));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Unmarked_export_directory_is_never_swept()
    {
        var path = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N") + ".ics");
        File.WriteAllText(path, "keep");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-20));
        CalendarDestination.CleanupExpiredExports(_root, DateTime.UtcNow);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Existing_nonempty_unmarked_directory_is_never_adopted_or_opened()
    {
        var path = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N") + ".ics");
        File.WriteAllText(path, "unowned calendar file");
        var launches = 0;
        var destination = new CalendarDestination(_root, _ => launches++);
        Assert.False(await destination.SendAsync("{\"title\":\"Fixture event\"}"));
        Assert.Equal(0, launches);
        Assert.Equal("unowned calendar file", File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(_root, ".keyboard.wtf-calendar-exports-v1")));
    }

    private async Task EstablishOwnedDirectoryAsync()
    {
        string export = null;
        var destination = new CalendarDestination(_root, path => export = path);
        Assert.True(await destination.SendAsync("{\"title\":\"Fixture establishes owned directory\"}"));
        Assert.NotNull(export);
        File.Delete(export); // Exact disposable fixture leaf; the owner marker remains.
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
