namespace KeyboardWtf.Destinations;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;

public class CalendarDestination : IDestination
{
    private readonly string _temporaryDirectory;
    private readonly Action<string> _launch;
    private const string OwnerMarker = ".keyboard.wtf-calendar-exports-v1";
    private const string OwnerValue = "keyboard.wtf calendar exports v1";

    public CalendarDestination() : this(Path.Combine(SettingsService.AppDataDir, "temp", "calendar-exports-v1"), path =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })) { }

    internal CalendarDestination(string temporaryDirectory, Action<string> launch)
    {
        _temporaryDirectory = temporaryDirectory;
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    public string Name => "Calendar";
    public string Description => "Create a calendar event via the default calendar app";
    public bool IsAvailable => true;
    public DestinationCategory Category => DestinationCategory.Ai;
    public string AiPrompt =>
        "Convert this speech into a calendar event. " +
        "Output ONLY a JSON object with these fields: " +
        "title (string), start (ISO 8601 local datetime, e.g. 2026-04-22T14:00:00), " +
        "end (ISO 8601 local datetime), location (string, empty if not mentioned), " +
        "description (one-sentence summary). No markdown, no explanation, JSON only.";

    public async Task<bool> SendAsync(string text)
    {
        string tempFile = null;
        var ownsTemporaryFile = false;
        var launched = false;
        try
        {
            var ics = BuildIcs(text);
            EnsureExportDirectory(_temporaryDirectory);
            CleanupExpiredExports(_temporaryDirectory, DateTime.UtcNow);
            tempFile = Path.Combine(_temporaryDirectory, $"export-{Guid.NewGuid():N}.ics");
            await using (var stream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                ownsTemporaryFile = true;
                await using var writer = new StreamWriter(stream, Encoding.UTF8);
                await writer.WriteAsync(ics);
            }

            _launch(tempFile);
            launched = true;
            AppLog.Info($"Calendar: opened ICS; sourceCharacters={text.Length}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Calendar: failed");
            return false;
        }
        finally
        {
            // A failed write/launch has no external consumer. A successful
            // shell launch may import asynchronously, so retain that export.
            // Never scan legacy temp files or delete the user's saved calendar.
            if (ownsTemporaryFile && !launched)
                OwnedTemporaryFileCleanup.TryDelete(tempFile, _temporaryDirectory, DateTime.UtcNow.AddMinutes(1));
        }
    }

    private static void EnsureExportDirectory(string directory)
    {
        var ancestor = Path.GetFullPath(directory);
        while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor)
            ?? throw new IOException("Calendar temporary directory is unavailable.");
        if (!OwnedTemporaryFileCleanup.IsOrdinaryDirectory(ancestor))
            throw new IOException("Calendar temporary directory is not an ordinary app directory.");
        Directory.CreateDirectory(directory);
        if (!OwnedTemporaryFileCleanup.IsOrdinaryDirectory(directory))
            throw new IOException("Calendar temporary directory changed.");
        var marker = Path.Combine(directory, OwnerMarker);
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any() && !File.Exists(marker))
                throw new IOException("Calendar temporary directory contains unowned files.");
            try
            {
                using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                writer.Write(OwnerValue);
            }
            catch (IOException) when (File.Exists(marker))
            {
                // Another export established the marker concurrently. Never
                // overwrite it; cleanup independently validates its contents.
            }
        }
    }

    internal static void CleanupExpiredExports(string directory, DateTime nowUtc)
    {
        try
        {
            if (!OwnedTemporaryFileCleanup.IsOrdinaryDirectory(directory)) return;
            var marker = new FileInfo(Path.Combine(directory, OwnerMarker));
            if (!marker.Exists || marker.Length > 128 || (marker.Attributes & FileAttributes.ReparsePoint) != 0) return;
            using (var file = new FileStream(marker.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length is <= 0 or > 128) return;
                var bytes = new byte[(int)file.Length];
                file.ReadExactly(bytes);
                if (file.ReadByte() != -1 || Encoding.UTF8.GetString(bytes) != OwnerValue) return;
            }
            // Opportunistic bounded cleanup, never a global temp sweep. Seven
            // days allows the shell consumer to import asynchronously; busy files
            // are retained by the exclusive-handle check. No directory is deleted.
            foreach (var path in Directory.EnumerateFiles(directory).Take(128))
            {
                var name = Path.GetFileName(path);
                if (name.Length == 43 && name.StartsWith("export-", StringComparison.Ordinal)
                    && name.EndsWith(".ics", StringComparison.Ordinal)
                    && Guid.TryParseExact(name.AsSpan(7, 32), "N", out _))
                    OwnedTemporaryFileCleanup.TryDelete(path, directory, nowUtc.AddDays(-7));
            }
        }
        catch { } // Retention failure must not fail a valid calendar export.
    }

    private static string BuildIcs(string text)
    {
        string title = "New Event";
        DateTime start = DateTime.Now.AddHours(1);
        DateTime end   = start.AddHours(1);
        string location = "";
        string description = "";

        // Try to extract JSON from the AI response
        try
        {
            var jStart = text.IndexOf('{');
            var jEnd   = text.LastIndexOf('}');
            if (jStart >= 0 && jEnd > jStart)
            {
                using var doc = JsonDocument.Parse(text[jStart..(jEnd + 1)]);
                var r = doc.RootElement;

                if (r.TryGetProperty("title", out var t) && t.GetString() is { } ts && ts.Length > 0)
                    title = ts;
                if (r.TryGetProperty("start", out var s) && DateTime.TryParse(s.GetString(), out var sd))
                    start = sd;
                if (r.TryGetProperty("end", out var e) && DateTime.TryParse(e.GetString(), out var ed))
                    end = ed;
                if (r.TryGetProperty("location", out var l))
                    location = l.GetString() ?? "";
                if (r.TryGetProperty("description", out var d))
                    description = d.GetString() ?? "";
            }
        }
        catch
        {
            // JSON parse failed — use raw text as description, keep defaults
            description = text;
        }

        if (end <= start) end = start.AddHours(1);

        var uid  = Guid.NewGuid().ToString("N");
        var now  = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var sb   = new StringBuilder();

        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//keyboard.wtf//keyboard.wtf//EN");
        sb.AppendLine("BEGIN:VEVENT");
        sb.AppendLine($"UID:{uid}@keyboard.wtf");
        sb.AppendLine($"DTSTAMP:{now}");
        sb.AppendLine($"DTSTART:{start:yyyyMMddTHHmmss}");
        sb.AppendLine($"DTEND:{end:yyyyMMddTHHmmss}");
        sb.AppendLine($"SUMMARY:{Esc(title)}");
        if (!string.IsNullOrWhiteSpace(location))
            sb.AppendLine($"LOCATION:{Esc(location)}");
        if (!string.IsNullOrWhiteSpace(description))
            sb.AppendLine($"DESCRIPTION:{Esc(description)}");
        sb.AppendLine("END:VEVENT");
        sb.AppendLine("END:VCALENDAR");

        return sb.ToString();
    }

    private static string Esc(string v) =>
        v.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
         .Replace("\r", "").Replace("\n", "\\n");
}
