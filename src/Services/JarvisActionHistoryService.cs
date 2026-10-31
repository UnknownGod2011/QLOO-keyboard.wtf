namespace KeyboardWtf.Services;

using System.Text.Json;

public sealed class JarvisActionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Success { get; set; }
    public bool ConfirmationRequired { get; set; }
}

public sealed class JarvisActionHistoryService
{
    public const int MaxEntries = 50;
    private readonly object _lock = new();
    private readonly List<JarvisActionEntry> _entries = new();
    private readonly string _path;

    public JarvisActionHistoryService(string path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(SettingsService.AppDataDir, "jarvis-action-history.json")
            : path;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var entries = JsonSerializer.Deserialize<List<JarvisActionEntry>>(File.ReadAllText(_path)) ?? new();
            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(entries.OrderByDescending(e => e.Timestamp).Take(MaxEntries));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Jarvis action history load failed");
        }
    }

    public void Add(string action, string detail, bool success, bool confirmationRequired = false)
    {
        lock (_lock)
        {
            _entries.Insert(0, new JarvisActionEntry
            {
                Action = action ?? "",
                Detail = Shorten(detail, 500),
                Success = success,
                ConfirmationRequired = confirmationRequired,
            });
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            SaveLocked();
        }
    }

    public IReadOnlyList<JarvisActionEntry> Snapshot()
    {
        lock (_lock)
            return _entries.Select(Clone).ToArray();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Jarvis action history save failed");
        }
    }

    private static JarvisActionEntry Clone(JarvisActionEntry entry) => new()
    {
        Id = entry.Id,
        Timestamp = entry.Timestamp,
        Action = entry.Action,
        Detail = entry.Detail,
        Success = entry.Success,
        ConfirmationRequired = entry.ConfirmationRequired,
    };

    private static string Shorten(string value, int maxLength)
    {
        var text = (value ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
