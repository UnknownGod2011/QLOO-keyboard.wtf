namespace KeyboardWtf.Services.Ai.Backends;

using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record AiInlinePart(string MimeType, string Data);
internal sealed record AiGenerationRequest(string Prompt, string SystemInstruction, int MaxOutputTokens,
    object ResponseJsonSchema = null, IReadOnlyList<AiInlinePart> Media = null, bool CompleteSelectedText = false);
internal sealed record PersonalGenerationResult(string Text, string Model, string FinishReason, JsonElement Usage);
internal enum PersonalAiFailure { Credential, Capability, Quota, Provider, AmbiguousTransport, InvalidResponse, Blocked, Truncated, PreDispatchConnection }

internal sealed class PersonalAiException(PersonalAiFailure failure) : Exception(failure switch
{
    PersonalAiFailure.Credential => "Google rejected the Personal credential. It was disabled; no managed fallback occurred.",
    PersonalAiFailure.Capability => "The requested Personal Gemini model or capability is unavailable. No managed fallback occurred.",
    PersonalAiFailure.Quota => "Personal Gemini quota/rate limit reached. The key pool is cooling down; no key rotation or managed fallback occurred.",
    PersonalAiFailure.Provider => "Personal Gemini rejected or could not complete the request. No managed fallback occurred.",
    PersonalAiFailure.PreDispatchConnection => "Personal Gemini could not connect before inference started. No managed fallback occurred.",
    PersonalAiFailure.AmbiguousTransport => "The Personal Gemini response could not be confirmed. The request was not replayed or moved to Managed.",
    PersonalAiFailure.Truncated => "Personal Gemini reached its output limit. The incomplete result was not published.",
    PersonalAiFailure.Blocked => "Personal Gemini blocked this result. No partial result was published.",
    _ => "Personal Gemini returned an incomplete or unsupported response. No result was published.",
})
{
    internal PersonalAiFailure Failure { get; } = failure;
    // Numeric protocol metadata only; never retain provider error bodies,
    // request text, credentials, or response headers in diagnostics.
    internal int? HttpStatusCode { get; set; }
}

/// <summary>One owned direct-provider attempt; never calls managed accounting or rotates keys on ambiguous completion.</summary>
internal sealed class PersonalGeminiHttpTransport : IDisposable
{
    private readonly PersonalApiVault _vault;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly Action<string> _diagnostics;
    internal PersonalGeminiHttpTransport(PersonalApiVault vault, HttpMessageHandler handler = null, TimeSpan? timeout = null,
        Action<string> diagnostics = null)
    {
        _vault = vault;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout ?? TimeSpan.FromSeconds(75);
        _diagnostics = diagnostics ?? AppLog.Info;
    }

    internal async Task<PersonalGenerationResult> GenerateAsync(IPersonalCredentialLease credential, string model,
        AiGenerationRequest input, CancellationToken token)
    {
        if (model is not (PersonalGeminiModels.Text or PersonalGeminiModels.Reasoning))
            throw new PersonalAiException(PersonalAiFailure.Capability);
        token.ThrowIfCancellationRequested();
        var timingRequest = input?.Media?.Any(part => part?.MimeType is "image/jpeg" or "image/png" or "image/webp") == true
            ? Guid.NewGuid() : Guid.Empty;
        var started = Stopwatch.GetTimestamp();
        var payload = BuildPayload(input); // Freeze caller-owned media/schema before any await.
        LogImageTiming(timingRequest, "payload", started);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        int? receivedStatus = null;
        string pendingStage = "request-setup";
        var stageStarted = Stopwatch.GetTimestamp();
        try
        {
            _vault.EnsureDispatchAllowed(credential);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent");
            request.Headers.Add("x-goog-api-key", _vault.ReadSecretForTransport(credential));
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new("application/json");
            LogImageTiming(timingRequest, pendingStage, stageStarted);
            pendingStage = "response-headers";
            stageStarted = Stopwatch.GetTimestamp();
            LogImageTiming(timingRequest, "response-headers-start", stageStarted);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            receivedStatus = (int)response.StatusCode;
            LogImageTiming(timingRequest, pendingStage, stageStarted);
            pendingStage = null;
            deadline.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(1);
                _vault.CoolDownUnknownScopePool(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 86_400)));
                throw new PersonalAiException(PersonalAiFailure.Quota);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _vault.RecordCredentialRejection(credential);
                throw new PersonalAiException(PersonalAiFailure.Credential);
            }
            if ((int)response.StatusCode is >= 300 and < 400 || (int)response.StatusCode >= 500)
                throw new PersonalAiException(PersonalAiFailure.Provider);
            pendingStage = "response-body";
            stageStarted = Stopwatch.GetTimestamp();
            var body = await ReadBoundedAsync(response.Content, deadline.Token);
            LogImageTiming(timingRequest, pendingStage, stageStarted);
            pendingStage = "response-parse";
            stageStarted = Stopwatch.GetTimestamp();
            using var document = JsonDocument.Parse(body);
            deadline.Token.ThrowIfCancellationRequested();
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode)
            {
                if (IsInvalidKey(root))
                {
                    _vault.RecordCredentialRejection(credential);
                    throw new PersonalAiException(PersonalAiFailure.Credential);
                }
                throw new PersonalAiException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    ? PersonalAiFailure.Capability : PersonalAiFailure.Provider);
            }
            var result = ParseCompleteResult(root, model);
            LogImageTiming(timingRequest, pendingStage, stageStarted);
            pendingStage = null;
            return result;
        }
        catch (PersonalAiException ex)
        {
            ex.HttpStatusCode = receivedStatus;
            throw;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new PersonalAiException(PersonalAiFailure.AmbiguousTransport); }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        { throw new PersonalAiException(PersonalAiFailure.PreDispatchConnection); }
        catch (HttpRequestException) { throw new PersonalAiException(PersonalAiFailure.AmbiguousTransport); }
        catch (IOException) { throw new PersonalAiException(PersonalAiFailure.AmbiguousTransport); }
        catch (JsonException) { throw new PersonalAiException(PersonalAiFailure.InvalidResponse) { HttpStatusCode = receivedStatus }; }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (pendingStage != null) LogImageTiming(timingRequest, pendingStage + "-incomplete", stageStarted);
            LogImageTiming(timingRequest, "total", started);
        }
    }

    private void LogImageTiming(Guid request, string stage, long started)
    {
        if (request == Guid.Empty) return;
        try
        {
            _diagnostics($"Personal image request timing: request={request:N} stage={stage} " +
                $"elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        }
        catch { /* Diagnostics must not change transport results or exception classification. */ }
    }

    internal static byte[] BuildPayload(AiGenerationRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var outputLimit = input.CompleteSelectedText ? 65_536 : 8192;
        if (string.IsNullOrWhiteSpace(input.Prompt) || !input.CompleteSelectedText && input.Prompt.Length > 200_000
            || input.SystemInstruction?.Length > 20_000 || input.MaxOutputTokens < 1 || input.MaxOutputTokens > outputLimit
            || (input.CompleteSelectedText && (input.Media?.Count > 0 || input.ResponseJsonSchema != null)))
            throw new ArgumentException("Unsupported Personal generation request size.");
        var generation = new Dictionary<string, object>
            { ["maxOutputTokens"] = input.MaxOutputTokens, ["temperature"] = 0.3 };
        if (input.ResponseJsonSchema is not null)
        {
            var schema = JsonSerializer.SerializeToElement(input.ResponseJsonSchema);
            if (Encoding.UTF8.GetByteCount(schema.GetRawText()) > 50_000)
                throw new ArgumentException("The Personal response schema is too large.");
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = schema;
        }
        var parts = new List<object> { new { text = input.Prompt } };
        if (input.Media is not null)
        {
            if (input.Media.Count > 5) throw new ArgumentException("Too many Personal media inputs.");
            foreach (var media in input.Media)
            {
                if (media is null || media.MimeType is not ("image/jpeg" or "image/png" or "image/webp" or "audio/wav")
                    || string.IsNullOrWhiteSpace(media.Data) || media.Data.Length > 14_000_000)
                    throw new ArgumentException("Unsupported Personal media input.");
                parts.Add(new { inlineData = new { mimeType = media.MimeType, data = media.Data } });
            }
        }
        var body = new Dictionary<string, object>
        {
            ["contents"] = new[] { new { role = "user", parts } },
            ["generationConfig"] = generation,
        };
        if (!string.IsNullOrWhiteSpace(input.SystemInstruction))
            body["systemInstruction"] = new { parts = new[] { new { text = input.SystemInstruction } } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > 20_000_000)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The Personal generation request is too large.");
        }
        return bytes;
    }

    private static bool IsInvalidKey(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
        && details.EnumerateArray().Any(detail => detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            && reason.GetString() == "API_KEY_INVALID");

    internal static PersonalGenerationResult ParseCompleteResult(JsonElement root, string model)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object
            && feedback.TryGetProperty("blockReason", out var blocked) && blocked.ValueKind == JsonValueKind.String
            && blocked.GetString() is not (null or "" or "BLOCK_REASON_UNSPECIFIED"))
            throw new PersonalAiException(PersonalAiFailure.Blocked);
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() != 1)
            throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty("finishReason", out var finish)
            || finish.ValueKind != JsonValueKind.String) throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
        var reason = finish.GetString();
        if (reason == "MAX_TOKENS") throw new PersonalAiException(PersonalAiFailure.Truncated);
        if (reason is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII")
            throw new PersonalAiException(PersonalAiFailure.Blocked);
        if (reason != "STOP" || !candidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
        var output = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || part.TryGetProperty("functionCall", out _))
                throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) output.Append(text.GetString());
        }
        if (string.IsNullOrWhiteSpace(output.ToString())) throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
        return new(output.ToString().Trim(), model, reason,
            root.TryGetProperty("usageMetadata", out var usage) ? usage.Clone() : default);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        const int maximum = 2_097_152;
        if (content.Headers.ContentLength > maximum) throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
        using var source = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new PersonalAiException(PersonalAiFailure.InvalidResponse);
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public void Dispose() => _http.Dispose();
}
