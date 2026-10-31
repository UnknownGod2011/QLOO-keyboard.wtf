namespace KeyboardWtf.Services.Ai.Backends;

using System.Net;
using System.Net.Http;
using System.Text.Json;

internal sealed record PersonalKeyValidationResult(PersonalKeyValidation Status, string Message);

/// <summary>Metadata only. Never resolves AiProviderRegistry or performs generation/Live validation.</summary>
internal sealed class PersonalGeminiKeyValidator : IDisposable
{
    private static readonly Uri MetadataEndpoint = new(
        "https://generativelanguage.googleapis.com/v1beta/models/" + PersonalGeminiModels.Text);
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    internal PersonalGeminiKeyValidator(HttpMessageHandler handler = null, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    internal async Task<PersonalKeyValidationResult> ValidateAsync(string key, CancellationToken cancellationToken = default)
    {
        PersonalApiVault.ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MetadataEndpoint);
            request.Headers.Add("x-goog-api-key", key);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            // Redirects are never followed; error bodies/messages must not enter logs/UI.
            if ((int)response.StatusCode is >= 300 and < 400)
                return Result(PersonalKeyValidation.Unknown);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Result(PersonalKeyValidation.InvalidCredential);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                || (int)response.StatusCode >= 500)
                return Result(PersonalKeyValidation.Unavailable);
            var bytes = await ReadBoundedAsync(response.Content, deadline.Token);
            using var document = JsonDocument.Parse(bytes);
            if (!response.IsSuccessStatusCode)
            {
                if (HasInvalidKeyReason(document.RootElement)) return Result(PersonalKeyValidation.InvalidCredential);
                return Result(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    ? PersonalKeyValidation.CapabilityUnavailable : PersonalKeyValidation.Unknown);
            }
            var root = document.RootElement;
            if (!root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() != "models/" + PersonalGeminiModels.Text
                || !root.TryGetProperty("supportedGenerationMethods", out var methods)
                || methods.ValueKind != JsonValueKind.Array)
                return Result(PersonalKeyValidation.Unknown);
            var supportsText = methods.EnumerateArray().Any(method =>
                method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent");
            deadline.Token.ThrowIfCancellationRequested();
            return Result(supportsText ? PersonalKeyValidation.AuthenticationValidated : PersonalKeyValidation.CapabilityUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result(PersonalKeyValidation.Unavailable); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        { return Result(PersonalKeyValidation.Unknown); }
    }

    private static bool HasInvalidKeyReason(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array) return false;
        return details.EnumerateArray().Any(detail => detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            && reason.GetString() == "API_KEY_INVALID");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        const int maximum = 1_048_576;
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Metadata response is too large.");
        using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + length > maximum) throw new InvalidDataException("Metadata response is too large.");
            output.Write(buffer, 0, length);
        }
        return output.ToArray();
    }

    private static PersonalKeyValidationResult Result(PersonalKeyValidation status) => new(status, status switch
    {
        PersonalKeyValidation.AuthenticationValidated => "Authentication and text-model metadata verified. Generation quota and Live capability have not been tested.",
        PersonalKeyValidation.InvalidCredential => "The provider rejected this credential. Check the key; no generation was attempted.",
        PersonalKeyValidation.CapabilityUnavailable => "The configured model metadata is unavailable or access is restricted. This does not prove the key is invalid.",
        PersonalKeyValidation.Unavailable => "The provider is temporarily unavailable or rate-limited. No generation was attempted.",
        _ => "Credential validation could not be confirmed. No generation was attempted.",
    });

    public void Dispose() => _http.Dispose();
}
