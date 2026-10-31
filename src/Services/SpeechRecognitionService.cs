namespace KeyboardWtf.Services;

using KeyboardWtf.Models;

// Facade: both engines are always instantiated but neither loads its model until LoadModel is called,
// so startup is cheap regardless of which engine the user picks. Whisper is preferred when loaded;
// Vosk is the fallback — chosen for speed (40MB, synchronous) when the Whisper model is missing.
// Future: add partial-result streaming to the interface for live transcription display during recording.
public sealed class SpeechRecognitionService : IDisposable
{
    private readonly VoskRecognitionService _vosk = new();
    private readonly WhisperRecognitionService _whisper = new();
    private bool _disposed;

    public bool IsVoskLoaded => _vosk.IsModelLoaded;
    public bool IsWhisperLoaded => _whisper.IsModelLoaded;

    public void LoadVoskModel(string modelPath) => _vosk.LoadModel(modelPath);
    public void LoadWhisperModel(string modelPath) => _whisper.LoadModel(modelPath);
    public VoskStreamingSession CreateStreamingSession(int sampleRate = 16000) =>
        _vosk.CreateStreamingSession(sampleRate);

    public async Task<string> RecognizeFromWavAsync(
        byte[] wavData,
        CancellationToken cancellationToken = default,
        bool preferFast = false)
    {
        var engine = KeyboardWtfState.SelectedEngine;

        if (preferFast && _vosk.IsModelLoaded)
        {
            AppLog.Info("Using Vosk fast path");
            return await Task.Run(() => _vosk.RecognizeFromWav(wavData), cancellationToken);
        }

        if (engine == SpeechEngine.Whisper && _whisper.IsModelLoaded)
        {
            AppLog.Info("Using Whisper engine");
            return await _whisper.RecognizeFromWavAsync(wavData, cancellationToken);
        }

        if (_vosk.IsModelLoaded)
        {
            if (engine == SpeechEngine.Whisper)
                AppLog.Warning("Whisper model not loaded - falling back to Vosk");

            AppLog.Info("Using Vosk engine");
            return await Task.Run(() => _vosk.RecognizeFromWav(wavData), cancellationToken);
        }

        AppLog.Warning("No speech engine loaded - skipping recognition");
        return string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _vosk.Dispose();
        _whisper.Dispose();
    }
}
