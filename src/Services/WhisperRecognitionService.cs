namespace KeyboardWtf.Services;

using System.Runtime.InteropServices;
using Whisper.net;

public sealed class WhisperRecognitionService : IDisposable
{
    private readonly WhisperModelLifetime<WhisperFactory> _models = new();

    public bool IsModelLoaded => _models.IsLoaded;

    public void LoadModel(string modelPath)
    {
        // Skip if same model already loaded
        if (_models.IsDisposed || _models.Matches(modelPath))
            return;

        // Stop new borrowers of the old model; any active processor retains it
        // until it has finished. Never synchronously wait for inference here.
        _models.Clear();

        if (!File.Exists(modelPath))
        {
            AppLog.Error($"Whisper model not found at: {modelPath}");
            return;
        }

        try
        {
            // RuntimeOptions.LibraryPath must be set by the app shell before
            // this call. WhisperFactory initializes the native whisper.dll on first use; if the
            // path is wrong it throws a DllNotFoundException that is not caught here.
            if (_models.TryReplace(WhisperFactory.FromPath(modelPath), modelPath))
                AppLog.Info($"Whisper model loaded from: {modelPath}");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Whisper model load failed");
        }
    }

    public async Task<string> RecognizeFromWavAsync(byte[] wavData, CancellationToken cancellationToken = default)
    {
        if (wavData == null || wavData.Length == 0)
            return string.Empty;

        using var model = _models.Borrow();
        if (model is null)
        {
            AppLog.Warning("Whisper model not loaded - skipping recognition");
            return string.Empty;
        }

        try
        {
            // Pass the WAV data directly - Whisper.net ProcessAsync accepts WAV streams
            using var wavStream = new MemoryStream(wavData);

            var language = Models.KeyboardWtfState.SelectedLanguage;
            var builder = model.Value.CreateBuilder()
                .WithThreads(Environment.ProcessorCount)
                // WithPrompt biases Whisper toward literal transcription and reduces hallucinated
                // punctuation or filler phrases ("Thank you for watching.") on short recordings.
                // Future: make this user-configurable for domain-specific vocabulary (names, jargon).
                .WithPrompt("keyboard.wtf voice transcription. Transcribe exactly what was said.");

            // "auto" language detection is accurate but noticeably slower; specifying en/de skips
            // the detection step. "auto" is kept as the fallback so other languages still work.
            if (language == "en")
                builder.WithLanguage("en");
            else if (language == "de")
                builder.WithLanguage("de");
            else
                builder.WithLanguage("auto");

            // ProcessAsync can observe cancellation before its native worker
            // finishes. Async disposal waits for that worker before the model
            // lease can free the borrowed context (Whisper.net 1.9.1 contract).
            await using var processor = builder.Build();

            var segments = new List<string>();
            string detectedLang = null;
            await foreach (var segment in processor.ProcessAsync(wavStream, cancellationToken))
            {
                // Language tag comes from the first segment; subsequent segments inherit it.
                // Storing it in KeyboardWtfState lets the UI show which language was detected.
                if (detectedLang == null && !string.IsNullOrEmpty(segment.Language))
                    detectedLang = segment.Language;
                segments.Add(segment.Text);
            }

            Models.KeyboardWtfState.DetectedLanguage = detectedLang;
            return string.Join(" ", segments).Trim();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Whisper recognition cancelled");
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Whisper recognition failed");
            return string.Empty;
        }
    }

    public void Dispose()
    {
        _models.Dispose();
    }
}
