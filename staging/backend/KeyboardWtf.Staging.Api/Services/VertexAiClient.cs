using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using Microsoft.Extensions.Options;
using KeyboardWtf.Staging.Api.Contracts;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class VertexAiClient : IVertexAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly GoogleCredential _credential;
    private readonly PaidSystemOptions _options;

    public VertexAiClient(HttpClient httpClient, IOptions<PaidSystemOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _credential = GoogleCredential.GetApplicationDefault()
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
    }

    public async Task<VertexGenerateResult> GenerateAsync(
        string model,
        string location,
        string prompt,
        string? systemInstruction,
        int maxOutputTokens,
        JsonElement? responseJsonSchema,
        IReadOnlyList<InlineImageRequest>? images,
        InlineAudioRequest? audio,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildGenerateContentEndpoint(
            _options.ProjectId,
            location,
            model);
        var body = BuildRequestBody(prompt, systemInstruction, maxOutputTokens, responseJsonSchema, images, audio);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var accessToken = await _credential.UnderlyingCredential
            .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new VertexRequestRejectedException((int)response.StatusCode);

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var text = ExtractText(root);
        var usage = ExtractUsage(root, response.Headers);
        return new VertexGenerateResult(text, usage, ExtractCompleteText(root));
    }

    public async Task<int> CountTokensAsync(
        string model,
        string location,
        string prompt,
        string? systemInstruction,
        int maxOutputTokens,
        JsonElement? responseJsonSchema,
        IReadOnlyList<InlineImageRequest>? images,
        InlineAudioRequest? audio,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildCountTokensEndpoint(_options.ProjectId, location, model);
        var body = BuildRequestBody(prompt, systemInstruction, maxOutputTokens, responseJsonSchema, images, audio);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var accessToken = await _credential.UnderlyingCredential
            .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new VertexRequestRejectedException((int)response.StatusCode);
        using var document = JsonDocument.Parse(responseBody);
        return ParseCountTokensResponse(document.RootElement);
    }

    internal static Dictionary<string, object?> BuildRequestBody(
        string prompt,
        string? systemInstruction,
        int maxOutputTokens,
        JsonElement? responseJsonSchema,
        IReadOnlyList<InlineImageRequest>? images,
        InlineAudioRequest? audio)
    {
        var parts = new List<object> { new { text = prompt } };
        if (images is not null)
        {
            parts.AddRange(images.Select(image => (object)new
            {
                inlineData = new { mimeType = image.MimeType, data = image.Data },
            }));
        }
        if (audio is not null)
        {
            parts.Add(new
            {
                inlineData = new { mimeType = audio.MimeType, data = audio.Data },
            });
        }
        var generationConfig = BuildGenerationConfig(maxOutputTokens, responseJsonSchema);
        var body = new Dictionary<string, object?>
        {
            ["contents"] = new[]
            {
                new { role = "user", parts },
            },
            ["generationConfig"] = generationConfig,
        };
        if (!string.IsNullOrWhiteSpace(systemInstruction))
            body["systemInstruction"] = new { parts = new[] { new { text = systemInstruction } } };
        return body;
    }

    internal static int ParseCountTokensResponse(JsonElement root)
    {
        static InvalidOperationException InvalidCount() => new("The provider returned invalid token-count evidence.");
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("totalTokens", out var total)
            || total.ValueKind != JsonValueKind.Number
            || !total.TryGetInt32(out var tokens) || tokens <= 0)
            throw InvalidCount();
        if (root.TryGetProperty("totalBillableCharacters", out var characters)
            && (characters.ValueKind != JsonValueKind.Number || !characters.TryGetInt32(out var characterCount) || characterCount < 0))
            throw InvalidCount();
        if (root.TryGetProperty("promptTokensDetails", out var details))
        {
            // This rescue only counts text. Supplied details must prove a
            // coherent text total; no image/audio estimate becomes a bound.
            if (details.ValueKind != JsonValueKind.Array || details.GetArrayLength() == 0)
                throw InvalidCount();
            long detailTotal = 0;
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object
                    || !detail.TryGetProperty("modality", out var modality)
                    || modality.ValueKind != JsonValueKind.String || modality.GetString() != "TEXT"
                    || !detail.TryGetProperty("tokenCount", out var count)
                    || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var detailCount) || detailCount < 0)
                    throw InvalidCount();
                detailTotal = checked(detailTotal + detailCount);
            }
            if (detailTotal != tokens)
                throw InvalidCount();
        }
        return tokens;
    }

    internal static Dictionary<string, object> BuildGenerationConfig(
        int maxOutputTokens,
        JsonElement? responseJsonSchema)
    {
        var generationConfig = new Dictionary<string, object>
        {
            ["maxOutputTokens"] = Math.Clamp(maxOutputTokens, 1, 65_536),
            ["temperature"] = 0.3,
        };
        if (responseJsonSchema is JsonElement schema)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseJsonSchema"] = schema;
        }
        return generationConfig;
    }

    internal static string BuildGenerateContentEndpoint(
        string projectId,
        string location,
        string model) => BuildModelEndpoint(projectId, location, model, "generateContent");

    internal static string BuildCountTokensEndpoint(
        string projectId,
        string location,
        string model) => BuildModelEndpoint(projectId, location, model, "countTokens");

    private static string BuildModelEndpoint(string projectId, string location, string model, string operation)
    {
        // Vertex's global location is represented in the resource path while
        // the service host remains aiplatform.googleapis.com. Regional calls
        // continue to use the documented regional service host.
        var host = string.Equals(location, "global", StringComparison.OrdinalIgnoreCase)
            ? "aiplatform.googleapis.com"
            : $"{Uri.EscapeDataString(location)}-aiplatform.googleapis.com";
        return
            $"https://{host}/v1/projects/{Uri.EscapeDataString(projectId)}/locations/{Uri.EscapeDataString(location)}/publishers/google/models/{Uri.EscapeDataString(model)}:{operation}";
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
            return "";
        var output = new StringBuilder();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts))
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    output.Append(text.GetString());
            }
        }
        return output.ToString().Trim();
    }

    // Match the Personal complete-document acceptance rules without changing
    // ordinary Vertex response publication. Accounting still consumes actual
    // usage even when this returns null; partial content is never copied.
    internal static string? ExtractCompleteText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object
            && feedback.TryGetProperty("blockReason", out var blocked) && blocked.ValueKind == JsonValueKind.String
            && blocked.GetString() is not (null or "" or "BLOCK_REASON_UNSPECIFIED")) return null;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() != 1) return null;
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object
            || !candidate.TryGetProperty("finishReason", out var reason) || reason.ValueKind != JsonValueKind.String
            || reason.GetString() != "STOP"
            || !candidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return null;
        var output = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || part.TryGetProperty("functionCall", out _)) return null;
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) output.Append(text.GetString());
        }
        var result = output.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private static ProviderUsage ExtractUsage(JsonElement root, HttpResponseHeaders headers)
    {
        ProviderUsageMetadata.TryParse(root.TryGetProperty("usageMetadata", out var metadata) ? metadata : default, out var usage);
        // A nonempty generated result cannot truthfully have zero candidate
        // output usage, including omitted counts paired with a partial total.
        if (usage.OutputTokens == 0 && HasNonThoughtText(root))
            usage = usage with { Known = false };
        var requestId = headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;
        requestId ??= root.TryGetProperty("responseId", out var responseId) && responseId.ValueKind == JsonValueKind.String
            ? responseId.GetString()
            : null;
        return usage with
        {
            ProviderRequestId = requestId,
            ProviderModelVersion = root.TryGetProperty("modelVersion", out var modelVersion) && modelVersion.ValueKind == JsonValueKind.String
                ? modelVersion.GetString()
                : null,
        };
    }

    private static bool HasNonThoughtText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var candidate in candidates.EnumerateArray())
            if (candidate.ValueKind == JsonValueKind.Object
                && candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                foreach (var part in parts.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.Object
                        && !(part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                        && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(text.GetString()))
                        return true;
        return false;
    }
}
