namespace KeyboardWtf.Services;

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

/// <summary>
/// Downloads and installs the Piper TTS binary and voice models.
///
/// Mirrors the pattern of <see cref="ModelManager"/> (Whisper/Vosk):
/// canonical paths under %LOCALAPPDATA%\keyboard.wtf\tts, idempotent "Ensure" methods, one HttpClient,
/// progress callbacks. Runs in the background during app startup; if any step fails, TTS silently
/// falls back to SAPI at call time.
///
/// Installed layout:
///   %LOCALAPPDATA%\keyboard.wtf\tts\piper\piper.exe     (+ espeak-ng-data, DLLs, etc.)
///   %LOCALAPPDATA%\keyboard.wtf\tts\voices\en_US-amy-medium.onnx (+ .onnx.json)
///   %LOCALAPPDATA%\keyboard.wtf\tts\voices\de_DE-thorsten-medium.onnx (+ .onnx.json)
/// </summary>
public static partial class PiperInstaller
{
    // v1.2.0 dropped the Windows asset (Linux/macOS only). 2023.11.14-2 is the last release
    // that shipped piper_windows_amd64.zip. See https://github.com/rhasspy/piper/releases
    private const string PiperZipUrl =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    // Independently verified against all 361 accepted local runtime files.
    // This is our byte pin, not an upstream signature or licensing clearance.
    private const long PiperArchiveBytes = 22_477_236;
    private const string PiperArchiveSha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea";

    // Same official medium voices; only NEW downloads use immutable origin bytes.
    private const string VoiceRevision = "c10ece1aade47bb51c153c893d14e5bf8e5b7117";
    private const string EnOnnxUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/" + VoiceRevision + "/en/en_US/amy/medium/en_US-amy-medium.onnx";
    private const string EnJsonUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/" + VoiceRevision + "/en/en_US/amy/medium/en_US-amy-medium.onnx.json";
    private const string DeOnnxUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/" + VoiceRevision + "/de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx";
    private const string DeJsonUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/" + VoiceRevision + "/de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx.json";

    private const long EnOnnxBytes = 63_201_294;
    private const long DeOnnxBytes = 63_201_294;
    private const long EnJsonBytes = 4_882;
    private const long DeJsonBytes = 4_819;
    private const string EnOnnxSha256 = "b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18";
    private const string DeOnnxSha256 = "7e64762d8e5118bb578f2eea6207e1a35a8e0c30595010b666f983fc87bb7819";
    private const string EnJsonSha256 = "95a23eb4d42909d38df73bb9ac7f45f597dbfcde2d1bf9526fdeaf5466977d77";
    private const string DeJsonSha256 = "974adee790533adb273a1ac88f49027d2a1b8f0f2cf4905954a4791e79264e85";

    private const string EnVoiceLabel = "English (Amy, medium)";
    private const string DeVoiceLabel = "Deutsch (Thorsten, medium)";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    // ---------- Downloaded location (fallback if the executable is shipped without bundle) ----------
    public static string TtsRoot { get; } = Path.Combine(
        SettingsService.AppDataDir, "tts");
    public static string PiperDir { get; } = Path.Combine(TtsRoot, "piper");
    public static string VoicesDir { get; } = Path.Combine(TtsRoot, "voices");
    public static string PiperExe { get; } = Path.Combine(PiperDir, "piper.exe");
    public static string EnVoicePath { get; } = Path.Combine(VoicesDir, "en_US-amy-medium.onnx");
    public static string DeVoicePath { get; } = Path.Combine(VoicesDir, "de_DE-thorsten-medium.onnx");

    // ---------- Bundled location (shipped next to the executable) ----------
    // Resolved from AppContext.BaseDirectory at first access; null if no bundled TTS folder exists.
    private static readonly Lazy<string> _bundledRoot = new(ResolveBundledRoot);
    public static string BundledTtsRoot => _bundledRoot.Value;
    public static string BundledPiperDir => BundledTtsRoot == null ? null : Path.Combine(BundledTtsRoot, "piper");
    public static string BundledVoicesDir => BundledTtsRoot == null ? null : Path.Combine(BundledTtsRoot, "voices");
    public static string BundledPiperExe => BundledPiperDir == null ? null : Path.Combine(BundledPiperDir, "piper.exe");
    public static string BundledEnVoicePath => BundledVoicesDir == null ? null : Path.Combine(BundledVoicesDir, "en_US-amy-medium.onnx");
    public static string BundledDeVoicePath => BundledVoicesDir == null ? null : Path.Combine(BundledVoicesDir, "de_DE-thorsten-medium.onnx");

    private static bool FileExistsSafe(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    public static bool IsBundledPiperPresent => FileExistsSafe(BundledPiperExe);
    public static bool IsBundledEnVoicePresent => FileExistsSafe(BundledEnVoicePath) && FileExistsSafe(BundledEnVoicePath + ".json");
    public static bool IsBundledDeVoicePresent => FileExistsSafe(BundledDeVoicePath) && FileExistsSafe(BundledDeVoicePath + ".json");

    public static bool IsDownloadedPiperPresent => File.Exists(PiperExe);
    public static bool IsDownloadedEnVoicePresent => File.Exists(EnVoicePath) && File.Exists(EnVoicePath + ".json");
    public static bool IsDownloadedDeVoicePresent => File.Exists(DeVoicePath) && File.Exists(DeVoicePath + ".json");

    // ---------- Aggregate view: true if either source has it ----------
    public static bool IsBinaryPresent => IsBundledPiperPresent || IsDownloadedPiperPresent;
    public static bool IsEnVoicePresent => IsBundledEnVoicePresent || IsDownloadedEnVoicePresent;
    public static bool IsDeVoicePresent => IsBundledDeVoicePresent || IsDownloadedDeVoicePresent;
    public static bool IsFullyInstalled => IsBinaryPresent && (IsEnVoicePresent || IsDeVoicePresent);

    // ---------- "Active" paths — prefer bundled so the demo works offline out of the box ----------
    public static string ActivePiperExe =>
        IsBundledPiperPresent ? BundledPiperExe :
        IsDownloadedPiperPresent ? PiperExe : null;

    public static string ActivePiperDir =>
        ActivePiperExe == null ? null : Path.GetDirectoryName(ActivePiperExe);

    public static string ActiveVoicesDir =>
        (IsBundledEnVoicePresent || IsBundledDeVoicePresent) ? BundledVoicesDir :
        (IsDownloadedEnVoicePresent || IsDownloadedDeVoicePresent) ? VoicesDir : null;

    public static string PiperBinarySource =>
        IsBundledPiperPresent ? "Bundled" :
        IsDownloadedPiperPresent ? "Downloaded" : "None";
    public static string EnVoiceSource =>
        IsBundledEnVoicePresent ? "Bundled" :
        IsDownloadedEnVoicePresent ? "Downloaded" : "None";
    public static string DeVoiceSource =>
        IsBundledDeVoicePresent ? "Bundled" :
        IsDownloadedDeVoicePresent ? "Downloaded" : "None";

    private static string ResolveBundledRoot()
    {
        try
        {
            var ttsDir = Path.Combine(AppContext.BaseDirectory, "tts");
            return Directory.Exists(ttsDir) ? ttsDir : null;
        }
        catch
        {
            return null;
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(PiperDir);
        Directory.CreateDirectory(VoicesDir);
    }

    public static async Task EnsureInstalledAsync(Action<string> onProgress = null)
    {
        try
        {
            EnsureDirectories();

            // Only download pieces that aren't already bundled or previously downloaded.
            if (!IsBinaryPresent)
                await DownloadPiperBinaryAsync(onProgress);

            if (!IsEnVoicePresent)
                await DownloadVoiceAsync(EnVoiceLabel, EnOnnxUrl, EnJsonUrl, EnVoicePath,
                    EnOnnxBytes, EnOnnxSha256, EnJsonBytes, EnJsonSha256, onProgress);

            if (!IsDeVoicePresent)
                await DownloadVoiceAsync(DeVoiceLabel, DeOnnxUrl, DeJsonUrl, DeVoicePath,
                    DeOnnxBytes, DeOnnxSha256, DeJsonBytes, DeJsonSha256, onProgress);

            if (IsFullyInstalled)
            {
                AppLog.Info($"Piper ready (bin={PiperBinarySource}, en={EnVoiceSource}, de={DeVoiceSource})");
                onProgress?.Invoke("Piper TTS ready");
            }
            else
            {
                onProgress?.Invoke("Piper install incomplete");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Piper install failed — will fall back to SAPI at call time");
            onProgress?.Invoke($"Install failed: {ex.Message}");
        }
    }

    private static async Task DownloadPiperBinaryAsync(Action<string> onProgress)
    {
        onProgress?.Invoke("Downloading Piper binary (~25 MB)...");
        AppLog.Info($"Downloading Piper binary from {PiperZipUrl}");

        await DownloadPiperRuntimeAsync(TtsRoot, _http, PiperZipUrl, PiperArchiveBytes, PiperArchiveSha256,
            System.Threading.CancellationToken.None, TimeSpan.FromMinutes(30),
            () => onProgress?.Invoke("Extracting Piper binary..."));

        AppLog.Info($"Piper binary installed at {PiperExe}");
    }

    private static async Task DownloadVoiceAsync(string label, string onnxUrl, string jsonUrl, string onnxPath,
        long onnxBytes, string onnxSha256, long jsonBytes, string jsonSha256, Action<string> onProgress)
    {
        onProgress?.Invoke($"Downloading voice: {label} (~63 MB)...");
        AppLog.Info($"Downloading Piper voice {label} from {onnxUrl}");

        await DownloadVoiceAsync(onnxPath, _http, onnxUrl, jsonUrl,
            onnxBytes, onnxSha256, jsonBytes, jsonSha256,
            System.Threading.CancellationToken.None, TimeSpan.FromMinutes(30));

        AppLog.Info($"Piper voice installed: {onnxPath}");
    }
}
