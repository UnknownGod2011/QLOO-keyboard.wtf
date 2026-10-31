namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using KeyboardWtf.Models;
using KeyboardWtf.Services.PaidStaging;

public sealed class GeminiScreenGuidanceService : IDisposable
{
    // Thinking and visible output share this limit. 900 left only 35 answer
    // tokens in the human failure; stay within the existing server route cap.
    internal const int OutputTokenLimit = 2048;
    // Do not discard a healthy provider response because analysis exceeds a
    // local UI deadline. Caller cancellation and transport bounds still apply.
    internal static readonly TimeSpan AnalysisTimeout = Timeout.InfiniteTimeSpan;
    private static readonly string[] ModelCandidates =
    [
        "gemini-2.5-flash",
        "gemini-2.5-flash-lite",
        "gemini-2.0-flash",
    ];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };
    private readonly PaidAccountService _paidAccount;
#if QLOO_JARVIS
    private bool UsesInferenceGateway => _paidAccount?.HasAiBackend == true;
#else
    private bool UsesInferenceGateway => _paidAccount?.Enabled == true;
#endif
    private readonly Func<Guid, byte[]> _capture;
    private readonly Func<string, byte[], CancellationToken, Guid, Task<string>> _analyze;
    private readonly object _sessionLock = new();
    private ScreenSnapshot _sessionSnapshot;
    private long _sessionGeneration;
    private long _captureAttempt;
    private long _storedCaptureAttempt;
    private bool _disposed;

    private sealed record ScreenSnapshot(string Id, DateTimeOffset CapturedUtc, byte[] Image);
    internal sealed record ScreenAnalysis(string Guidance, string ContextId, DateTimeOffset CapturedUtc, bool Reused);

    public GeminiScreenGuidanceService() : this(null) { }

    internal GeminiScreenGuidanceService(PaidAccountService paidAccount)
        : this(paidAccount, null, null) { }

    internal GeminiScreenGuidanceService(
        PaidAccountService paidAccount,
        Func<Guid, byte[]> capture,
        Func<string, byte[], CancellationToken, Guid, Task<string>> analyze)
    {
        _paidAccount = paidAccount;
        _capture = capture ?? CaptureScreenJpeg;
        _analyze = analyze;
    }

    // Standalone Settings checks do not seed or replace Jarvis session context.
    public async Task<string> InspectAsync(string request, CancellationToken token) =>
        (await InspectCoreAsync(request, null, retainForSession: false, token)).Guidance;

    internal Task<ScreenAnalysis> InspectSessionAsync(
        string request, string screenContextId, CancellationToken token) =>
        InspectCoreAsync(request, screenContextId, retainForSession: true, token);

    internal void ClearSessionContext()
    {
        lock (_sessionLock)
        {
            _sessionSnapshot = null;
            _sessionGeneration++;
            _captureAttempt++;
        }
    }

    private async Task<ScreenAnalysis> InspectCoreAsync(
        string request, string screenContextId, bool retainForSession, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_analyze == null && !UsesInferenceGateway && string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey))
            throw new InvalidOperationException("Screen analysis is not configured.");

        var operation = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var reuse = screenContextId != null;
            ScreenSnapshot snapshot;
            long generation;
            long attempt;
            lock (_sessionLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                generation = _sessionGeneration;
                attempt = retainForSession && !reuse ? ++_captureAttempt : _captureAttempt;
                snapshot = reuse ? _sessionSnapshot : null;
                if (reuse && (!retainForSession || snapshot == null
                    || !string.Equals(snapshot.Id, screenContextId, StringComparison.Ordinal)))
                    throw new InvalidOperationException("That screen snapshot is no longer available in this Jarvis session. Ask to analyze the current screen instead.");
            }

            if (reuse)
                LogTiming(operation, "session-image-reused", started);
            else
            {
                var uiStarted = Stopwatch.GetTimestamp();
                KeyboardWtfState.SetUi(VoiceUiPhase.Executing, "Capturing screen", retainForSession
                    ? "Approved capture. Kept only in this Jarvis session; not saved to disk."
                    : "One-time capture approved. Nothing is saved.");
                LogTiming(operation, "capture-ui", uiStarted);
                var image = _capture(operation);
                snapshot = new ScreenSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, image);
            }
            token.ThrowIfCancellationRequested();
            var guidance = await CompleteAnalysisAsync(
                async analysisToken =>
                {
                    var text = _analyze != null
                        ? await _analyze(request, snapshot.Image, analysisToken, operation)
                        : await AnalyzeCapturedImageAsync(request, snapshot.Image, analysisToken, operation);
                    analysisToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(text))
                        throw new InvalidDataException("Screen analysis returned no guidance.");
                    lock (_sessionLock)
                        if (retainForSession && generation != _sessionGeneration)
                            throw new OperationCanceledException("Jarvis screen context ended.");
                    return text;
                }, token,
                timingOperation: operation,
                detail: reuse ? "Analyzing the previously approved screen..." : "Analyzing your screen...");
            token.ThrowIfCancellationRequested();
            if (retainForSession)
            {
                lock (_sessionLock)
                {
                    // Session shutdown or a newer successful capture must not be
                    // undone by an older provider completion, even if it ignored cancellation.
                    if (generation != _sessionGeneration)
                        throw new OperationCanceledException("Jarvis screen context ended.");
                    if (!reuse && attempt > _storedCaptureAttempt)
                    {
                        _sessionSnapshot = snapshot;
                        _storedCaptureAttempt = attempt;
                    }
                }
            }
            return new ScreenAnalysis(guidance, snapshot.Id, snapshot.CapturedUtc, reuse);
        }
        finally { LogTiming(operation, "total", started); }
    }

    internal static async Task<string> CompleteAnalysisAsync(
        Func<CancellationToken, Task<string>> analyze,
        CancellationToken token,
        TimeSpan? timeout = null,
        Guid timingOperation = default,
        string detail = "Analyzing your screen...")
    {
        token.ThrowIfCancellationRequested();
        var uiStarted = Stopwatch.GetTimestamp();
        var uiRevision = KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Analyzing screen", detail);
        LogTiming(timingOperation, "analysis-ui", uiStarted);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? AnalysisTimeout);
        try
        {
            // Only this waiter publishes UI; a late result cannot overwrite a
            // newer conversation after caller cancellation or an explicit deadline.
            var text = await analyze(deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            uiStarted = Stopwatch.GetTimestamp();
            KeyboardWtfState.TrySetUiIfRevision(uiRevision, VoiceUiPhase.Done, "Guidance ready", Limit(text, 120));
            LogTiming(timingOperation, "completion-ui", uiStarted);
            return text;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            const string message = "Screen analysis took too long. Jarvis is still available; no screen action was performed.";
            uiStarted = Stopwatch.GetTimestamp();
            KeyboardWtfState.TrySetUiIfRevision(uiRevision, VoiceUiPhase.Error, "Screen analysis timed out", message);
            LogTiming(timingOperation, "timeout-ui", uiStarted);
            throw new TimeoutException(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            uiStarted = Stopwatch.GetTimestamp();
            KeyboardWtfState.TrySetUiIfRevision(uiRevision, VoiceUiPhase.Error, "Screen analysis failed", "The approved screen capture could not be analyzed.");
            LogTiming(timingOperation, "failure-ui", uiStarted);
            throw;
        }
    }

    private async Task<string> AnalyzeCapturedImageAsync(string request, byte[] image, CancellationToken token, Guid operation)
    {
        if (UsesInferenceGateway)
        {
            var setupStarted = Stopwatch.GetTimestamp();
            var prompt = BuildPrompt(request);
            var inlineImage = new PaidInlineImage("image/jpeg", Convert.ToBase64String(image));
            LogTiming(operation, "base64-setup", setupStarted);
            var requestStarted = Stopwatch.GetTimestamp();
            LogTiming(operation, "request-parse-start", requestStarted);
            try
            {
                var response = await RetryNameResolutionOnceAsync(
                    attemptToken => _paidAccount.GenerateAsync(
                        prompt, null, OutputTokenLimit, null,
                        new[] { inlineImage }, "screen-guidance", attemptToken),
                    token);
                return response.Text;
            }
            finally { LogTiming(operation, "request-parse", requestStarted); }
        }

        Exception lastError = null;
        foreach (var model in ModelCandidates)
        {
            try
            {
                return await AnalyzeAsync(model, image, request, token, operation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                AppLog.Warning(ex, $"Gemini screen analysis failed: {model}");
            }
        }

        throw lastError ?? new InvalidOperationException("Screen analysis failed.");
    }

    // A DNS failure occurs before the paid request reaches the backend. One
    // bounded retry of the same approved image can recover a transient local
    // resolver outage without retrying ambiguous HTTP failures or recapturing.
    internal static async Task<T> RetryNameResolutionOnceAsync<T>(
        Func<CancellationToken, Task<T>> request,
        CancellationToken token,
        TimeSpan? retryDelay = null)
    {
        try
        {
            return await request(token);
        }
        catch (HttpRequestException ex) when (IsNameResolutionFailure(ex) && !token.IsCancellationRequested)
        {
            AppLog.Warning("Screen analysis backend name resolution failed; retrying once.");
            await Task.Delay(retryDelay ?? TimeSpan.FromMilliseconds(500), token);
            return await request(token);
        }
    }

    private static bool IsNameResolutionFailure(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.NameResolutionError)
            return true;
        for (Exception cause = exception.InnerException; cause != null; cause = cause.InnerException)
            if (cause is SocketException socket
                && socket.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain)
                return true;
        return false;
    }

    public static byte[] CaptureScreenJpeg() => CaptureScreenJpeg(Guid.Empty);

    private static byte[] CaptureScreenJpeg(Guid operation)
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The screen could not be captured.");

        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        var copyStarted = Stopwatch.GetTimestamp();
        using (var graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        LogTiming(operation, "capture-copy", copyStarted);

        var preprocessingStarted = Stopwatch.GetTimestamp();
        const int maxWidth = 1600;
        var scale = Math.Min(1, maxWidth / (double)source.Width);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
        resized.Save(stream, encoder, parameters);
        var image = stream.ToArray();
        LogTiming(operation, "resize-jpeg", preprocessingStarted);
        return image;
    }

    private async Task<string> AnalyzeAsync(
        string model,
        byte[] jpeg,
        string request,
        CancellationToken token,
        Guid operation)
    {
        var setupStarted = Stopwatch.GetTimestamp();
        var prompt = BuildPrompt(request);
        var body = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inlineData = new
                            {
                                mimeType = "image/jpeg",
                                data = Convert.ToBase64String(jpeg),
                            },
                        },
                    },
                },
            },
            generationConfig = new
            {
                temperature = 0.15,
                maxOutputTokens = OutputTokenLimit,
            },
        };

        using var requestMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        requestMessage.Headers.Add("x-goog-api-key", KeyboardWtfState.GeminiApiKey);
        requestMessage.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        LogTiming(operation, "base64-setup", setupStarted);
        var requestStarted = Stopwatch.GetTimestamp();
        LogTiming(operation, "request-parse-start", requestStarted);
        try
        {
            using var response = await _http.SendAsync(requestMessage, token);
            var responseBody = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Screen analysis returned {(int)response.StatusCode}: {ExtractError(responseBody)}");

            var text = ExtractText(responseBody);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("Screen analysis returned no guidance.");
            return text;
        }
        finally { LogTiming(operation, "request-parse", requestStarted); }
    }

    // Only fixed stage labels, random correlation and numeric duration; never screen or prompt content.
    private static void LogTiming(Guid operation, string stage, long started)
    {
        if (operation == Guid.Empty) return;
        try
        {
            AppLog.Info($"Screen analysis timing: operation={operation:N} stage={stage} " +
                $"elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        }
        catch { /* Diagnostics must not change the analysis result or cancellation. */ }
    }

    private static string BuildPrompt(string request)
    {
        var userRequest = string.IsNullOrWhiteSpace(request)
            ? "Explain what is visible and give the next safe step."
            : request.Trim();
        return
            """
            You are the screen-guidance component of keyboard.wtf on Windows.
            Analyze only this one user-approved screenshot.
            Give concise, step-by-step guidance about what is visibly present.
            Identify likely buttons or controls by their visible labels and relative position.
            Never claim you clicked, typed, submitted, or changed anything.
            If the requested control is not visible or uncertain, say so and explain what to look for.
            Avoid repeating private values, API keys, passwords, tokens, email bodies, financial details,
            or personal messages visible on screen unless the user explicitly asks to read that exact content.
            Do not infer hidden page state or browser DOM.

            User request:
            """ + userRequest;
    }

    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.GetArrayLength() == 0
            || !candidates[0].TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts))
            return "";
        return string.Join(
            Environment.NewLine,
            parts.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string ExtractError(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
                return message.GetString() ?? "Unknown error";
        }
        catch { }
        return "Unknown error";
    }

    private static string Limit(string value, int maxLength)
    {
        var text = (value ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            _disposed = true;
            ClearSessionContext();
        }
        _http.Dispose();
    }
}
