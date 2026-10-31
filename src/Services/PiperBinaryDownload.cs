namespace KeyboardWtf.Services;

public static partial class PiperInstaller
{
    private static readonly SemaphoreSlim BinaryDownloadGate = new(1, 1);
    private const string BinaryStagePrefix = ".piper-binary-download-";
    private const long BinaryArchiveMaximumBytes = 64L * 1024 * 1024;
    private const long BinaryExpandedMaximumBytes = 128L * 1024 * 1024;
    private const int BinaryMaximumEntries = 2_000;
    private static readonly string[] RequiredRuntimeFiles =
    [
        "piper.exe", "piper_phonemize.dll", "espeak-ng.dll", "onnxruntime.dll",
        "onnxruntime_providers_shared.dll", "libtashkeel_model.ort",
        "espeak-ng-data/phondata", "espeak-ng-data/phonindex", "espeak-ng-data/phontab",
        "espeak-ng-data/intonations", "espeak-ng-data/en_dict", "espeak-ng-data/de_dict",
        "espeak-ng-data/lang/gmw/en", "espeak-ng-data/lang/gmw/en-US", "espeak-ng-data/lang/gmw/de",
    ];

    internal static Task DownloadPiperBinaryAsync(string ttsRoot, HttpClient http, string url,
        long expectedArchiveBytes, string expectedArchiveSha256, CancellationToken token, TimeSpan timeout) =>
        DownloadPiperRuntimeAsync(ttsRoot, http, url, expectedArchiveBytes, expectedArchiveSha256, token, timeout);

    private static async Task DownloadPiperRuntimeAsync(string ttsRoot, HttpClient http, string url,
        long expectedArchiveBytes, string expectedArchiveSha256, CancellationToken cancellationToken,
        TimeSpan timeout, Action onExtracting = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        var uri = ValidateVoiceArtifact(url, expectedArchiveBytes, expectedArchiveSha256, BinaryArchiveMaximumBytes);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(30))
            throw new InvalidDataException("Invalid Piper runtime download deadline.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ttsRoot));
        var destination = Path.Combine(root, "piper");
        var stage = Path.Combine(root, BinaryStagePrefix + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(stage, "previous-runtime");
        var ownsGate = false;
        var ownsStage = false;
        var published = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            await BinaryDownloadGate.WaitAsync(token);
            ownsGate = true;
            RequireSafeVoicePath(destination);
            // Preserve existing bundled/downloaded executable readiness, including legacy bytes.
            if (File.Exists(Path.Combine(destination, "piper.exe"))) return;
            Directory.CreateDirectory(root);
            RequireSafeVoicePath(root);
            if (Directory.Exists(stage) || File.Exists(stage))
                throw new InvalidDataException("Piper runtime staging already exists.");
            Directory.CreateDirectory(stage);
            ownsStage = true;
            RequireSafeVoicePath(stage);
            var zipPath = Path.Combine(stage, "runtime.zip");
            var extraction = Path.Combine(stage, "extracted");
            try
            {
                // Reuse exact-size, SHA256, CreateNew and cancellable streaming from the voice boundary.
                await DownloadVoiceArtifactAsync(http, uri, zipPath, expectedArchiveBytes, expectedArchiveSha256, token);
                token.ThrowIfCancellationRequested();
                onExtracting?.Invoke();
                await ModelManager.ExtractBoundedArchiveAsync(zipPath, extraction,
                    BinaryExpandedMaximumBytes, BinaryMaximumEntries, token);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException("Piper runtime archive failed integrity or safety verification.", exception);
            }
            var wrapped = Path.Combine(extraction, "piper");
            var source = Directory.Exists(wrapped) ? wrapped : extraction;
            token.ThrowIfCancellationRequested();
            PublishPiperRuntime(source, destination, backup, token);
            published = true;
        }
        finally
        {
            // Never erase recoverable prior bytes after an ambiguous move/restoration failure.
            if (ownsStage && !published && (Directory.Exists(backup) || File.Exists(backup)))
                AppLog.Warning($"Piper runtime recovery staging retained: operation={Path.GetFileName(stage)}");
            else if (ownsStage)
            {
                try
                {
                    ValidateOwnedBinaryStage(root, stage);
                    RequireSafeVoicePath(stage, recursive: true);
                    if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
                }
                catch (Exception exception) { AppLog.Warning(exception, "Piper owned runtime staging cleanup deferred"); }
            }
            if (ownsGate) BinaryDownloadGate.Release();
        }
    }

    // Publish a fully verified directory, not an executable followed by incomplete DLL/data copies.
    internal static void PublishPiperRuntime(string source, string destination, string backup,
        CancellationToken token, Action<string, string> moveDirectory = null)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        backup = Path.GetFullPath(backup);
        var root = Path.GetDirectoryName(destination);
        var stage = Path.GetDirectoryName(backup);
        ValidateOwnedBinaryStage(root, stage);
        var extraction = Path.Combine(stage, "extracted");
        if (!string.Equals(destination, Path.Combine(root, "piper"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(backup, Path.Combine(stage, "previous-runtime"), StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(source, extraction, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, Path.Combine(extraction, "piper"), StringComparison.OrdinalIgnoreCase))
            || Directory.Exists(backup) || File.Exists(backup) || File.Exists(destination))
            throw new InvalidDataException("Invalid Piper runtime publication paths.");
        RequireSafeVoicePath(destination, recursive: true);
        RequireSafeVoicePath(stage, recursive: true);
        token.ThrowIfCancellationRequested();
        if (File.Exists(Path.Combine(destination, "piper.exe"))) return;
        foreach (var required in RequiredRuntimeFiles)
        {
            var path = Path.Combine(source, required.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException("Piper runtime is missing a required non-empty executable, library or language asset.");
        }
        moveDirectory ??= Directory.Move;
        try
        {
            if (Directory.Exists(destination)) moveDirectory(destination, backup);
            token.ThrowIfCancellationRequested();
            moveDirectory(source, destination);
        }
        catch
        {
            try
            {
                // No deletion of a canonical directory: it may belong to a concurrent external owner.
                if (!Directory.Exists(destination) && !File.Exists(destination) && Directory.Exists(backup))
                {
                    RequireSafeVoicePath(backup, recursive: true);
                    RequireSafeVoicePath(destination);
                    moveDirectory(backup, destination);
                }
            }
            catch (Exception exception) { AppLog.Warning(exception, "Piper runtime restoration deferred"); }
            throw;
        }
    }

    private static void ValidateOwnedBinaryStage(string root, string stage)
    {
        stage = Path.GetFullPath(stage);
        var name = Path.GetFileName(stage);
        if (!string.Equals(Path.GetDirectoryName(stage), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(BinaryStagePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[BinaryStagePrefix.Length..], "N", out _))
            throw new InvalidDataException("Piper runtime stage is not an owned child directory.");
    }
}
