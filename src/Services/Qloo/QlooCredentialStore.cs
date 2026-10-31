namespace KeyboardWtf.Services.Qloo;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Stores the user's Qloo API key, independent of the Gemini Personal API vault.
/// The key is protected with Windows DPAPI (current user) in the QLOO profile and is
/// never sent to the settings page, prompts or logs. QLOO_API_KEY is a developer fallback.
/// </summary>
internal sealed class QlooCredentialStore
{
    internal const string FileName = "qloo-api-v1.dat";
    internal const string EnvironmentVariableName = "QLOO_API_KEY";
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("qloo.jarvis/qloo-api-key/v1"));
    private static readonly Regex KeyToken = new(@"[A-Za-z0-9_\-]{20,200}", RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly Func<string> _environment;
    private string _cachedKey;
    private DateTime _cachedStamp;
    private long _revision;

    internal QlooCredentialStore(string path = null, Func<string> environment = null)
    {
        KeyPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(SettingsService.AppDataDir, FileName)
            : path;
        _environment = environment ?? (() => Environment.GetEnvironmentVariable(EnvironmentVariableName));
    }

    internal string KeyPath { get; }

    /// <summary>Changes whenever the stored key changes, so a 401 lockout can be cleared.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    internal string Source
    {
        get
        {
            if (ReadStoredKey() is not null) return "profile";
            return IsUsable(_environment()) ? "environment" : "none";
        }
    }

    internal bool IsConfigured => ReadKey() is not null;

    /// <summary>Returns the usable key or null. Profile key wins over the environment.</summary>
    internal string ReadKey()
    {
        var stored = ReadStoredKey();
        if (stored is not null) return stored;
        var fromEnvironment = _environment()?.Trim();
        return IsUsable(fromEnvironment) ? fromEnvironment : null;
    }

    internal string Mask()
    {
        var key = ReadKey();
        return key is null ? "" : "••••" + key[^4..];
    }

    internal void Save(string key)
    {
        var clean = (key ?? "").Trim();
        if (!IsUsable(clean))
            throw new ArgumentException("Enter a valid Qloo API key (letters, digits, '-' or '_', no spaces).");
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(clean), Entropy, DataProtectionScope.CurrentUser);
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(KeyPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = KeyPath + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, KeyPath, overwrite: true);
            _cachedKey = null;
            _cachedStamp = default;
            Interlocked.Increment(ref _revision);
        }
    }

    internal bool Remove()
    {
        lock (_gate)
        {
            var existed = File.Exists(KeyPath);
            if (existed) File.Delete(KeyPath);
            _cachedKey = null;
            _cachedStamp = default;
            Interlocked.Increment(ref _revision);
            return existed;
        }
    }

    /// <summary>Imports the first key-shaped token from a local text file without echoing it.</summary>
    internal bool ImportFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var key = ExtractKey(File.ReadAllText(path));
        if (key is null) return false;
        Save(key);
        return true;
    }

    internal static string ExtractKey(string text)
    {
        var match = KeyToken.Match(text ?? "");
        return match.Success ? match.Value : null;
    }

    internal static bool IsUsable(string key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length is >= 16 and <= 200
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private string ReadStoredKey()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(KeyPath)) return null;
                var stamp = File.GetLastWriteTimeUtc(KeyPath);
                if (_cachedKey is not null && stamp == _cachedStamp) return _cachedKey;
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(bytes).Trim();
                if (!IsUsable(key)) return null;
                _cachedKey = key;
                _cachedStamp = stamp;
                return key;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                AppLog.Warning("Qloo key file could not be read; Qloo calls are unavailable until it is re-entered.");
                return null;
            }
        }
    }
}
