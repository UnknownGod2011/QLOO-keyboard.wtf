using System.Security.Cryptography;

namespace KeyboardWtf.Services;

public static partial class PiperInstaller
{
    private static readonly SemaphoreSlim VoiceDownloadGate = new(1, 1);
    private const string VoiceStagePrefix = ".piper-voice-download-";
    private const long VoiceModelMaximumBytes = 64L * 1024 * 1024;
    private const long VoiceConfigMaximumBytes = 64L * 1024;

    // This boundary affects missing-voice installation only. Existing bundled /
    // downloaded pair readiness and all TTS selection/playback remain unchanged.
    internal static async Task DownloadVoiceAsync(
        string onnxPath, HttpClient http, string onnxUrl, string jsonUrl,
        long onnxBytes, string onnxSha256, long jsonBytes, string jsonSha256,
        CancellationToken cancellationToken, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(http);
        var modelUri = ValidateVoiceArtifact(onnxUrl, onnxBytes, onnxSha256, VoiceModelMaximumBytes);
        var configUri = ValidateVoiceArtifact(jsonUrl, jsonBytes, jsonSha256, VoiceConfigMaximumBytes);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(30))
            throw new InvalidDataException("Invalid Piper voice download deadline.");
        var destination = Path.GetFullPath(onnxPath);
        if (!destination.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid Piper voice destination.");
        var directory = Path.GetDirectoryName(destination);
        var stage = Path.Combine(directory, VoiceStagePrefix + Guid.NewGuid().ToString("N"));
        var ownsGate = false;
        var ownsStage = false;
        var published = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            await VoiceDownloadGate.WaitAsync(token);
            ownsGate = true;
            RequireSafeVoicePath(destination);
            RequireSafeVoicePath(destination + ".json");
            if (File.Exists(destination) && File.Exists(destination + ".json")) return;
            Directory.CreateDirectory(directory);
            RequireSafeVoicePath(directory);
            if (Directory.Exists(stage) || File.Exists(stage))
                throw new InvalidDataException("Piper voice staging already exists.");
            Directory.CreateDirectory(stage);
            ownsStage = true;
            RequireSafeVoicePath(stage);

            await DownloadVoiceArtifactAsync(http, modelUri, Path.Combine(stage, "voice-model.partial"),
                onnxBytes, onnxSha256, token);
            await DownloadVoiceArtifactAsync(http, configUri, Path.Combine(stage, "voice-config.partial"),
                jsonBytes, jsonSha256, token);
            token.ThrowIfCancellationRequested();
            PublishVoicePair(stage, destination, token);
            published = true;
        }
        finally
        {
            // An ambiguous restoration must retain prior bytes, never erase them.
            if (ownsStage && !published && (File.Exists(Path.Combine(stage, "previous-model"))
                || File.Exists(Path.Combine(stage, "previous-config"))))
                AppLog.Warning($"Piper voice recovery staging retained: operation={Path.GetFileName(stage)}");
            else if (ownsStage)
            {
                try
                {
                    ValidateOwnedVoiceStage(stage, destination);
                    RequireSafeVoicePath(stage, recursive: true);
                    if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
                }
                catch (Exception exception) { AppLog.Warning(exception, "Piper owned voice staging cleanup deferred"); }
            }
            if (ownsGate) VoiceDownloadGate.Release();
        }
    }

    private static Uri ValidateVoiceArtifact(string url, long bytes, string sha256, long maximumBytes)
    {
        if (bytes <= 0 || bytes > maximumBytes || sha256 is null || sha256.Length != 64
            || sha256.Any(character => !Uri.IsHexDigit(character))
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid Piper voice download descriptor.");
        return uri;
    }

    private static async Task DownloadVoiceArtifactAsync(
        HttpClient http, Uri uri, string partial, long bytes, string sha256, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declared && declared != bytes)
            throw new InvalidDataException("Piper voice size does not match its pinned descriptor.");
        RequireSafeVoicePath(partial);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, buffer.Length, useAsync: true))
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), token);
                if (read == 0) break;
                total = checked(total + read);
                if (total > bytes)
                    throw new InvalidDataException("Piper voice exceeded its pinned size.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), token);
            }
            await target.FlushAsync(token);
        }
        if (total != bytes || !CryptographicOperations.FixedTimeEquals(
            hash.GetHashAndReset(), Convert.FromHexString(sha256)))
            throw new InvalidDataException("Piper voice is incomplete or failed integrity verification.");
    }

    // Two files cannot be renamed atomically. Remove incomplete legacy bytes
    // from readiness first; publish the verified config before the verified model.
    // Exact owned backups survive ambiguous failure for manual recovery.
    internal static void PublishVoicePair(string stage, string onnxPath, CancellationToken token,
        Action<string, string> moveFile = null)
    {
        var destination = Path.GetFullPath(onnxPath);
        var config = destination + ".json";
        stage = Path.GetFullPath(stage);
        ValidateOwnedVoiceStage(stage, destination);
        RequireSafeVoicePath(destination);
        RequireSafeVoicePath(config);
        RequireSafeVoicePath(stage, recursive: true);
        token.ThrowIfCancellationRequested();
        if (File.Exists(destination) && File.Exists(config)) return;
        var modelSource = Path.Combine(stage, "voice-model.partial");
        var configSource = Path.Combine(stage, "voice-config.partial");
        var modelBackup = Path.Combine(stage, "previous-model");
        var configBackup = Path.Combine(stage, "previous-config");
        if (!File.Exists(modelSource) || !File.Exists(configSource)
            || File.Exists(modelBackup) || Directory.Exists(modelBackup)
            || File.Exists(configBackup) || Directory.Exists(configBackup)
            || Directory.Exists(destination) || Directory.Exists(config))
            throw new InvalidDataException("Invalid Piper voice publication files.");
        var modelBytes = new FileInfo(modelSource).Length;
        var configBytes = new FileInfo(configSource).Length;
        if (modelBytes <= 0 || modelBytes > VoiceModelMaximumBytes
            || configBytes <= 0 || configBytes > VoiceConfigMaximumBytes)
            throw new InvalidDataException("Piper voice publication exceeds its file bounds.");
        var modelHash = HashVoiceFile(modelSource, modelBytes, token);
        var configHash = HashVoiceFile(configSource, configBytes, token);
        moveFile ??= File.Move;
        try
        {
            if (File.Exists(destination)) moveFile(destination, modelBackup);
            if (File.Exists(config)) moveFile(config, configBackup);
            token.ThrowIfCancellationRequested();
            moveFile(configSource, config);
            token.ThrowIfCancellationRequested();
            moveFile(modelSource, destination);
        }
        catch
        {
            try
            {
                // Infer committed moves from their source absence and exact bytes,
                // including an injected post-move failure. Never delete a new owner.
                RestoreStagedVoiceFile(modelSource, destination, modelBytes, modelHash, moveFile);
                RestoreStagedVoiceFile(configSource, config, configBytes, configHash, moveFile);
                if (!File.Exists(destination) && !Directory.Exists(destination)
                    && !File.Exists(config) && !Directory.Exists(config))
                {
                    if (File.Exists(modelBackup)) moveFile(modelBackup, destination);
                    if (File.Exists(configBackup)) moveFile(configBackup, config);
                }
            }
            catch (Exception exception) { AppLog.Warning(exception, "Piper voice restoration deferred"); }
            throw;
        }
    }

    private static void RestoreStagedVoiceFile(string source, string destination, long expectedBytes, byte[] expectedHash,
        Action<string, string> moveFile)
    {
        RequireSafeVoicePath(destination);
        if (!File.Exists(source) && File.Exists(destination) && new FileInfo(destination).Length == expectedBytes
            && CryptographicOperations.FixedTimeEquals(HashVoiceFile(destination, expectedBytes, CancellationToken.None), expectedHash))
            moveFile(destination, source);
    }

    private static byte[] HashVoiceFile(string path, long expectedBytes, CancellationToken token)
    {
        using var input = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, expectedBytes - total + 1));
            total += read;
            if (total > expectedBytes)
                throw new InvalidDataException("Piper voice changed beyond its verified size.");
            if (read == 0)
            {
                if (total != expectedBytes)
                    throw new InvalidDataException("Piper voice no longer matches its verified size.");
                return hash.GetHashAndReset();
            }
            hash.AppendData(buffer, 0, read);
        }
    }

    private static void ValidateOwnedVoiceStage(string stage, string destination)
    {
        var name = Path.GetFileName(stage);
        if (!string.Equals(Path.GetDirectoryName(stage), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(VoiceStagePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[VoiceStagePrefix.Length..], "N", out _))
            throw new InvalidDataException("Piper voice stage is not an owned child directory.");
    }

    private static void RequireSafeVoicePath(string path, bool recursive = false)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Piper voice paths cannot contain reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (recursive && Directory.Exists(path))
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                RequireSafeVoicePath(child, recursive: true);
    }
}
