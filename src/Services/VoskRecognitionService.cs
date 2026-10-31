namespace KeyboardWtf.Services;

using System.Text.Json;
using NAudio.Wave;
using Vosk;

public sealed class VoskRecognitionService : IDisposable
{
    private Model _model;
    private bool _disposed;

    public bool IsModelLoaded => _model != null;

    public void LoadModel(string modelPath)
    {
        if (_model != null)
            return;

        if (!Directory.Exists(modelPath))
        {
            AppLog.Error($"Vosk model not found at: {modelPath}");
            return;
        }

        // SetLogLevel(-1) suppresses Vosk's extremely verbose native stdout output.
        // Suppress native model-load diagnostics so the local app log stays useful.
        global::Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(modelPath);
        AppLog.Info($"Vosk model loaded from: {modelPath}");
    }

    public string RecognizeFromWav(byte[] wavData)
    {
        if (wavData == null || wavData.Length == 0)
            return string.Empty;

        if (_model == null)
        {
            AppLog.Warning("Vosk model not loaded - skipping recognition");
            return string.Empty;
        }

        try
        {
            using var stream = new MemoryStream(wavData);
            using var reader = new WaveFileReader(stream);
            using var rec = new VoskRecognizer(_model, reader.WaveFormat.SampleRate);

            var buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                // AcceptWaveform returns true when Vosk has a hypothesis ready; we discard interim
                // results and call FinalResult() once after the full audio is fed.
                // Future: use PartialResult() inside this loop for live transcription display.
                rec.AcceptWaveform(buffer, bytesRead);
            }

            var resultJson = rec.FinalResult();
            return ExtractText(resultJson);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Vosk recognition failed");
            return string.Empty;
        }
    }

    public VoskStreamingSession CreateStreamingSession(int sampleRate) =>
        _model == null ? null : new VoskStreamingSession(_model, sampleRate);

    private static string ExtractText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var text)
                ? text.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _model?.Dispose();
        _model = null;
    }
}

public sealed class VoskStreamingSession : IDisposable
{
    private readonly object _lock = new();
    private readonly VoskRecognizer _recognizer;
    private readonly List<string> _committed = new();
    private bool _disposed;

    internal VoskStreamingSession(Model model, int sampleRate)
    {
        _recognizer = new VoskRecognizer(model, sampleRate);
    }

    public string AcceptPcm(byte[] buffer, int count)
    {
        if (_disposed || buffer == null || count <= 0)
            return "";

        lock (_lock)
        {
            if (_disposed)
                return "";

            var hasResult = _recognizer.AcceptWaveform(buffer, count);
            var json = hasResult ? _recognizer.Result() : _recognizer.PartialResult();
            var text = Extract(json, hasResult ? "text" : "partial");
            if (hasResult && !string.IsNullOrWhiteSpace(text))
                _committed.Add(text);

            return string.Join(
                " ",
                _committed.Append(hasResult ? "" : text)
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    private static string Extract(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value)
                ? value.GetString()?.Trim() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _recognizer.Dispose();
        }
    }
}
