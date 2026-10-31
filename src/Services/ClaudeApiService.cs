namespace KeyboardWtf.Services;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Models;

public sealed class ClaudeApiService : IDisposable
{
    private readonly HttpClient _http;
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const string DefaultModel = "claude-sonnet-4-20250514";
    private const int MaxTokens = 1024;

    public ClaudeApiService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool IsAvailable => !string.IsNullOrEmpty(KeyboardWtfState.ClaudeApiKey);

    /// <summary>
    /// Reformat transcribed text for a specific destination using Claude.
    /// </summary>
    public async Task<string> ReformatAsync(string rawText, string destinationPrompt, string language)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Claude API key not configured");

        var systemPrompt = BuildSystemPrompt(destinationPrompt, language);

        var requestBody = new
        {
            model = DefaultModel,
            max_tokens = MaxTokens,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = rawText }
            }
        };

        return await SendRequestAsync(requestBody);
    }

    /// <summary>
    /// Chat with Claude (for Voice Assistant mode). Accepts conversation history.
    /// </summary>
    public async Task<string> ChatAsync(string userMessage, List<ChatMessage> history)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Claude API key not configured");

        var messages = new List<object>();
        foreach (var msg in history)
            messages.Add(new { role = msg.Role, content = msg.Content });
        messages.Add(new { role = "user", content = userMessage });

        var requestBody = new
        {
            model = DefaultModel,
            max_tokens = MaxTokens,
            system = "You are a helpful voice assistant. Keep responses concise and natural - they will be read aloud via text-to-speech. Respond in the same language the user speaks.",
            messages
        };

        return await SendRequestAsync(requestBody);
    }

    /// <summary>
    /// Test the API key with a minimal request.
    /// </summary>
    public async Task<(bool success, string message)> TestApiKeyAsync()
    {
        try
        {
            var result = await ReformatAsync("Hello", "Respond with just: OK", "en");
            return (true, $"API key works. Response: {result}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<string> SendRequestAsync(object requestBody)
    {
        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Add("x-api-key", KeyboardWtfState.ClaudeApiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Error($"Claude API error: httpStatus={(int)response.StatusCode} responseCharacters={responseBody.Length}");
            throw new HttpRequestException($"Claude API returned {(int)response.StatusCode}: {ExtractErrorMessage(responseBody)}");
        }

        return ExtractContent(responseBody);
    }

    private static string BuildSystemPrompt(string destinationPrompt, string language)
    {
        var langInstruction = language switch
        {
            "en" => "Output in English.",
            "de" => "Output in German (Deutsch).",
            _ => "Output in the same language as the input."
        };

        return $"{destinationPrompt}\n\n{langInstruction}\n\nThe input is a speech-to-text transcript and may contain recognition errors. Fix obvious errors while preserving the intended meaning.";
    }

    private static string ExtractContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var content = doc.RootElement.GetProperty("content");
        if (content.GetArrayLength() > 0)
        {
            var firstBlock = content[0];
            if (firstBlock.TryGetProperty("text", out var text))
                return text.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string ExtractErrorMessage(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return "Unknown error";
    }

    public void Dispose() => _http.Dispose();
}
