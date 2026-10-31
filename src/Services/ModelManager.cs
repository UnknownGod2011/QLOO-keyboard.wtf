namespace KeyboardWtf.Services;

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KeyboardWtf.Models;

public static class ModelManager
{
    private const string VoskModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    private const string VoskModelFolder = "vosk-model";
    private const string VoskModelMarker = "README";
    private const long VoskMinimumArchiveBytes = 30L * 1024 * 1024;
    private const long VoskMaximumArchiveBytes = 100L * 1024 * 1024;
    private const long VoskMaximumExtractedBytes = 512L * 1024 * 1024;
    private const int VoskMaximumEntries = 10_000;

    private const string WhisperModelFolder = "whisper-model";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly object _whisperValidationLock = new();
    private static readonly SemaphoreSlim _whisperDownloadGate = new(1, 1);
    private static readonly SemaphoreSlim _voskDownloadGate = new(1, 1);
    private static WhisperValidationCache _whisperValidationCache;

    private sealed record WhisperValidationCache(
        string Path,
        long Length,
        DateTime LastWriteUtc,
        WhisperModelSize Size,
        bool IsValid);

    public static string ModelsBaseDir { get; } = Path.Combine(
        SettingsService.AppDataDir, "models");

    public static string VoskModelPath => Path.Combine(ModelsBaseDir, VoskModelFolder);

    public static string WhisperModelPath =>
        Path.Combine(ModelsBaseDir, WhisperModelFolder, GetWhisperFileName(KeyboardWtfState.SelectedWhisperModel));

    public static bool IsVoskPresent => Directory.Exists(VoskModelPath)
        && File.Exists(Path.Combine(VoskModelPath, VoskModelMarker))
        && Directory.Exists(Path.Combine(VoskModelPath, "am"))
        && Directory.Exists(Path.Combine(VoskModelPath, "conf"))
        && Directory.Exists(Path.Combine(VoskModelPath, "graph"))
        && Directory.Exists(Path.Combine(VoskModelPath, "ivector"));

    public static bool IsWhisperPresent => IsWhisperModelValid(WhisperModelPath, KeyboardWtfState.SelectedWhisperModel);

    public static string GetWhisperFileName(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => "ggml-base.bin",
        WhisperModelSize.Small => "ggml-small.bin",
        _ => "ggml-small.bin"
    };

    private static string GetWhisperDownloadUrl(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin",
        WhisperModelSize.Small => "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.bin",
        _ => "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.bin"
    };

    private static string GetWhisperSizeLabel(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => "~140 MB",
        WhisperModelSize.Small => "~460 MB",
        _ => "~460 MB"
    };

    private static long GetWhisperMinimumBytes(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => 100L * 1024 * 1024,
        WhisperModelSize.Small => 300L * 1024 * 1024,
        _ => 300L * 1024 * 1024
    };

    private static string GetWhisperSha1(WhisperModelSize size) => size switch
    {
        // Published by the upstream whisper.cpp model inventory.
        WhisperModelSize.Base => "465707469ff3a37a2b9b8d8f89f2f99de7299dac",
        WhisperModelSize.Small => "55356645c2b361a969dfd0ef2c5a50d530afd8d5",
        _ => "55356645c2b361a969dfd0ef2c5a50d530afd8d5",
    };

    // Immutable upstream LFS metadata for the SAME existing model files.
    // Existing installed-file validation retains its published SHA-1/cache.
    private static string GetWhisperDownloadSha256(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        _ => "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
    };

    private static long GetWhisperMaximumDownloadBytes(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => 147_951_465,
        _ => 487_601_967,
    };

    private static bool IsWhisperModelValid(string path, WhisperModelSize size)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            var file = new FileInfo(path);
            if (file.Length < GetWhisperMinimumBytes(size))
                return false;

            lock (_whisperValidationLock)
            {
                if (_whisperValidationCache is not null
                    && string.Equals(_whisperValidationCache.Path, path, StringComparison.OrdinalIgnoreCase)
                    && _whisperValidationCache.Length == file.Length
                    && _whisperValidationCache.LastWriteUtc == file.LastWriteTimeUtc
                    && _whisperValidationCache.Size == size)
                    return _whisperValidationCache.IsValid;
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA1.HashData(stream));
            var isValid = string.Equals(
                actual,
                GetWhisperSha1(size),
                StringComparison.OrdinalIgnoreCase);
            lock (_whisperValidationLock)
            {
                _whisperValidationCache = new WhisperValidationCache(
                    path,
                    file.Length,
                    file.LastWriteTimeUtc,
                    size,
                    isValid);
            }
            return isValid;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Path.Combine(ModelsBaseDir, VoskModelFolder));
        Directory.CreateDirectory(Path.Combine(ModelsBaseDir, WhisperModelFolder));
    }

    public static async Task<bool> DownloadVoskModelAsync(Action<string> onProgress = null)
    {
        if (IsVoskPresent)
        {
            AppLog.Info("Vosk model already present");
            return true;
        }

        return await DownloadVoskModelAsync(ModelsBaseDir, _http, VoskModelUrl,
            VoskMinimumArchiveBytes, VoskMaximumArchiveBytes, VoskMaximumExtractedBytes,
            VoskMaximumEntries, onProgress, CancellationToken.None, TimeSpan.FromMinutes(30));
    }

    internal static async Task<bool> DownloadVoskModelAsync(
        string modelsDirectory, HttpClient http, string url,
        long minimumArchiveBytes, long maximumArchiveBytes, long maximumExtractedBytes,
        int maximumEntries, Action<string> onProgress, CancellationToken cancellationToken, TimeSpan timeout)
    {
        var directory = Path.GetFullPath(modelsDirectory);
        var destination = Path.Combine(directory, VoskModelFolder);
        var stage = Path.Combine(directory, ".vosk-download-" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(stage, "model.zip");
        var extraction = Path.Combine(stage, "extracted");
        var backup = Path.Combine(stage, "previous-model");
        var ownsGate = false;
        var ownsStage = false;
        var published = false;
        try
        {
            if (minimumArchiveBytes <= 0 || maximumArchiveBytes < minimumArchiveBytes
                || maximumArchiveBytes > VoskMaximumArchiveBytes || maximumExtractedBytes <= 0
                || maximumExtractedBytes > VoskMaximumExtractedBytes || maximumEntries <= 0
                || maximumEntries > VoskMaximumEntries || timeout <= TimeSpan.Zero
                || timeout > TimeSpan.FromMinutes(30) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("Invalid Vosk download descriptor.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var token = deadline.Token;
            await _voskDownloadGate.WaitAsync(token);
            ownsGate = true;
            if (HasVoskModelStructure(destination)) return true;
            Directory.CreateDirectory(directory);
            EnsureNoReparsePoints(directory, recursive: false);
            if (Directory.Exists(stage) || File.Exists(stage))
                throw new InvalidDataException("Vosk staging already exists.");
            Directory.CreateDirectory(stage);
            ownsStage = true;
            onProgress?.Invoke("Downloading Vosk model (~40 MB)...");
            AppLog.Info("Downloading Vosk model");

            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength
                    && (contentLength < minimumArchiveBytes || contentLength > maximumArchiveBytes))
                    throw new InvalidDataException("Vosk download size is outside the model bounds.");
                await DownloadResponseToFileAsync(response, zipPath, "Vosk model", onProgress,
                    token, maximumArchiveBytes, requireDeclaredLength: true, createNew: true);
            }

            token.ThrowIfCancellationRequested();
            if (new FileInfo(zipPath).Length < minimumArchiveBytes)
                throw new InvalidDataException("Vosk download did not produce a complete model archive");
            onProgress?.Invoke("Extracting Vosk model...");
            token.ThrowIfCancellationRequested();
            AppLog.Info("Extracting Vosk model");
            await ExtractVoskArchiveAsync(zipPath, extraction, maximumExtractedBytes, maximumEntries, token);
            var roots = Directory.GetDirectories(extraction);
            if (roots.Length != 1 || Directory.GetFiles(extraction).Length != 0
                || !HasVoskModelStructure(roots[0]))
                throw new InvalidDataException("Vosk download did not contain the required model structure");
            EnsureNoReparsePoints(stage, recursive: true);
            PublishVoskModel(roots[0], destination, backup, token);
            published = true;
            AppLog.Info("Vosk model installed");
            onProgress?.Invoke("Vosk model ready");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Vosk model download failed");
            onProgress?.Invoke($"Download failed: {ex.Message}");
            return false;
        }
        finally
        {
            // Never clean away a prior model on ambiguous publication/restore.
            if (!published && Directory.Exists(backup))
                AppLog.Warning($"Vosk recovery staging retained: operation={Path.GetFileName(stage)}");
            else if (ownsStage)
            {
                try
                {
                    ValidateOwnedVoskStage(directory, stage);
                    if (Directory.Exists(stage))
                    {
                        EnsureNoReparsePoints(stage, recursive: true);
                        Directory.Delete(stage, recursive: true);
                    }
                }
                catch (Exception ex) { AppLog.Warning(ex, "Vosk owned staging cleanup deferred"); }
            }
            if (ownsGate) _voskDownloadGate.Release();
        }
    }

    private static bool HasVoskModelStructure(string path) => Directory.Exists(path)
        && File.Exists(Path.Combine(path, VoskModelMarker))
        && Directory.Exists(Path.Combine(path, "am")) && Directory.Exists(Path.Combine(path, "conf"))
        && Directory.Exists(Path.Combine(path, "graph")) && Directory.Exists(Path.Combine(path, "ivector"));

    internal static void PublishVoskModel(string source, string destination, string backup,
        CancellationToken token, Action<string, string> moveDirectory = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        var stage = Path.GetDirectoryName(Path.GetFullPath(backup));
        ValidateOwnedVoskStage(directory, stage);
        if (Path.GetFileName(destination) != VoskModelFolder || Path.GetFileName(backup) != "previous-model"
            || !Path.GetFullPath(source).StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(backup) || File.Exists(backup) || !HasVoskModelStructure(source))
            throw new InvalidDataException("Invalid Vosk publication paths or structure.");
        EnsureNoReparsePoints(stage, recursive: true);
        token.ThrowIfCancellationRequested();
        if (HasVoskModelStructure(destination)) return;
        moveDirectory ??= Directory.Move;
        try
        {
            if (Directory.Exists(destination))
            {
                EnsureNoReparsePoints(destination, recursive: true);
                moveDirectory(destination, backup);
            }
            token.ThrowIfCancellationRequested();
            moveDirectory(source, destination);
        }
        catch
        {
            if (!Directory.Exists(destination) && !File.Exists(destination) && Directory.Exists(backup))
                moveDirectory(backup, destination);
            // An unexpectedly appearing canonical directory is never removed.
            throw;
        }
    }

    // Shared only at the archive-validation boundary; recognizer/model readiness is unchanged.
    internal static Task ExtractBoundedArchiveAsync(string zipPath, string extraction,
        long maximumBytes, int maximumEntries, CancellationToken token) =>
        ExtractVoskArchiveAsync(zipPath, extraction, maximumBytes, maximumEntries, token);

    private static async Task ExtractVoskArchiveAsync(string zipPath, string extraction,
        long maximumBytes, int maximumEntries, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
            throw new InvalidDataException("Vosk archive entry count exceeds its bound.");
        var root = Path.GetFullPath(extraction) + Path.DirectorySeparatorChar;
        var paths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long advertised = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var components = relative.TrimEnd('/').Split('/');
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (relative.Length == 0 || relative.StartsWith('/') || relative.Contains(':')
                || System.Linq.Enumerable.Any(components, part => part.Length == 0 || part is "." or ".."
                    || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || IsReservedWindowsModelName(part))
                || unixType is not (0 or 0x8000 or 0x4000)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Vosk archive contains an unsafe path or entry type.");
            var path = Path.GetFullPath(Path.Combine(extraction, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(path.TrimEnd(Path.DirectorySeparatorChar)))
                throw new InvalidDataException("Vosk archive contains an outside or duplicate destination.");
            advertised = checked(advertised + entry.Length);
            if (advertised > maximumBytes)
                throw new InvalidDataException("Vosk archive exceeds its extracted size bound.");
        }
        Directory.CreateDirectory(extraction);
        var buffer = new byte[128 * 1024];
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(extraction, entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, useAsync: true);
            long entryBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), token);
                if (read == 0) break;
                entryBytes = checked(entryBytes + read);
                total = checked(total + read);
                if (entryBytes > entry.Length || total > maximumBytes)
                    throw new InvalidDataException("Vosk extraction exceeded its advertised or total size bound.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (entryBytes != entry.Length)
                throw new InvalidDataException("Vosk entry did not match its advertised length.");
        }
    }

    private static bool IsReservedWindowsModelName(string component)
    {
        var stem = component.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    private static void ValidateOwnedVoskStage(string directory, string stage)
    {
        var path = Path.GetFullPath(stage);
        var name = Path.GetFileName(path);
        const string prefix = ".vosk-download-";
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidDataException("Vosk cleanup target is not an owned staging directory.");
    }

    private static void EnsureNoReparsePoints(string path, bool recursive)
    {
        var pending = new System.Collections.Generic.Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Vosk model staging cannot contain reparse points.");
            if (recursive && (attributes & FileAttributes.Directory) != 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
        }
    }

    public static async Task<bool> DownloadWhisperModelAsync(Action<string> onProgress = null)
    {
        // Freeze the requested model before any await. Changing Settings must
        // not redirect an in-flight download or its cleanup to another model.
        var size = KeyboardWtfState.SelectedWhisperModel;
        var directory = Path.Combine(ModelsBaseDir, WhisperModelFolder);
        var destination = Path.Combine(directory, GetWhisperFileName(size));
        if (IsWhisperModelValid(destination, size))
        {
            AppLog.Info($"Whisper model already present: {destination}");
            return true;
        }

        return await DownloadWhisperModelAsync(() => size, directory, _http,
            GetWhisperDownloadUrl, GetWhisperMinimumBytes, GetWhisperDownloadSha256,
            GetWhisperMaximumDownloadBytes, onProgress, CancellationToken.None, TimeSpan.FromMinutes(30));
    }

    internal static async Task<bool> DownloadWhisperModelAsync(
        Func<WhisperModelSize> selectedModel,
        string whisperDirectory,
        HttpClient http,
        Func<WhisperModelSize, string> downloadUrl,
        Func<WhisperModelSize, long> minimumBytes,
        Func<WhisperModelSize, string> expectedHash,
        Func<WhisperModelSize, long> maximumBytes,
        Action<string> onProgress,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var size = selectedModel();
        var directory = Path.GetFullPath(whisperDirectory);
        var destination = Path.Combine(directory, GetWhisperFileName(size));
        var url = downloadUrl(size);
        var minimum = minimumBytes(size);
        var maximum = maximumBytes(size);
        var checksum = expectedHash(size);
        var tempPath = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        var ownsGate = false;
        try
        {
            if (minimum <= 0 || maximum < minimum || checksum is null
                || checksum.Length is not (40 or 64) || !System.Linq.Enumerable.All(checksum, Uri.IsHexDigit)
                || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(30))
                throw new InvalidDataException("Invalid Whisper download descriptor.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var token = deadline.Token;
            await _whisperDownloadGate.WaitAsync(token);
            ownsGate = true;
            // The preceding owner may already have installed the exact file.
            if (HasExpectedWhisperDownload(destination, minimum, maximum, checksum))
                return true;
            Directory.CreateDirectory(directory);
            onProgress?.Invoke($"Downloading Whisper {size} model ({GetWhisperSizeLabel(size)})...");
            AppLog.Info($"Downloading Whisper {size} model");
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength
                    && (contentLength < minimum || contentLength > maximum))
                    throw new InvalidDataException("Whisper download size is outside the verified model bounds.");
                await DownloadResponseToFileAsync(response, tempPath, $"Whisper {size}", onProgress,
                    token, maximum, requireDeclaredLength: true, createNew: true);
            }
            token.ThrowIfCancellationRequested();
            if (!HasExpectedWhisperDownload(tempPath, minimum, maximum, checksum))
                throw new InvalidDataException($"Whisper download failed the upstream {size} model checksum");
            token.ThrowIfCancellationRequested();
            File.Move(tempPath, destination, true);
            AppLog.Info($"Whisper model installed at: {destination}");
            onProgress?.Invoke("Whisper model ready");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Whisper model download failed");
            onProgress?.Invoke($"Download failed: {ex.Message}");
            return false;
        }
        finally
        {
            // Exact unique owned file only; never remove another operation's
            // shared partial or either installed model during failed work.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            if (ownsGate) _whisperDownloadGate.Release();
        }
    }

    private static bool HasExpectedWhisperDownload(string path, long minimum, long maximum, string checksum)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        if (length < minimum || length > maximum) return false;
        using var stream = File.OpenRead(path);
        var hash = checksum.Length == 64 ? SHA256.HashData(stream) : SHA1.HashData(stream);
        return string.Equals(Convert.ToHexString(hash), checksum, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task EnsureModelsAsync(Action<string> onProgress = null)
    {
        EnsureDirectories();

        if (!IsVoskPresent && !await DownloadVoskModelAsync(onProgress))
            throw new InvalidOperationException(
                "The Vosk speech model could not be downloaded. Check the connection and retry.");

        if (!IsWhisperPresent && !await DownloadWhisperModelAsync(onProgress))
            throw new InvalidOperationException(
                "The Whisper speech model could not be downloaded. Check the connection and retry.");
    }

    private static async Task DownloadResponseToFileAsync(
        HttpResponseMessage response,
        string path,
        string label,
        Action<string> onProgress,
        CancellationToken cancellationToken = default,
        long maximumBytes = long.MaxValue,
        bool requireDeclaredLength = false,
        bool createNew = false)
    {
        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            path,
            createNew ? FileMode.CreateNew : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var lastReportedPercent = -5;
        long lastReportedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            received = checked(received + read);
            if (received > maximumBytes || (requireDeclaredLength && totalBytes is long declared && received > declared))
                throw new InvalidDataException("Download exceeded its verified size bound.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            if (totalBytes is > 0)
            {
                var percent = (int)Math.Min(100, received * 100 / totalBytes.Value);
                if (percent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = percent;
                    onProgress?.Invoke($"Downloading {label}... {percent}%");
                }
            }
            else if (received - lastReportedBytes >= 5L * 1024 * 1024)
            {
                lastReportedBytes = received;
                onProgress?.Invoke($"Downloading {label}... {received / (1024 * 1024)} MB");
            }
        }
        if (requireDeclaredLength && totalBytes is long expected && received != expected)
            throw new InvalidDataException("Download did not match its declared length.");
        await destination.FlushAsync(cancellationToken);
    }
}
