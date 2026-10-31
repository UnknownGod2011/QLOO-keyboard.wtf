namespace KeyboardWtf.Services;

using System.Net;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;

public sealed partial class WebSettingsService
{
    // Inject only when the backend gateway owns ALL inference paths. Until then
    // there is no UI that could imply Personal-only while callers still use Managed.
    private PersonalApiSettingsController _personalApiSettings;

    private async Task HandlePersonalApiSettings(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        if (!HasSettingsActionHeader(request))
        {
            response.StatusCode = 403;
            await WriteJson(response, new { ok = false, message = "Settings action authorization required." });
            return;
        }
        if (_personalApiSettings is null || path is not ("/api/personal-ai/state" or "/api/personal-ai/action"))
        {
            response.StatusCode = 404;
            await WriteJson(response, new { ok = false, message = "Personal API configuration is not enabled in this build." });
            return;
        }
        PersonalSettingsResult result;
        try
        {
            if (path == "/api/personal-ai/state") result = _personalApiSettings.GetState();
            else
            {
                if (request.ContentLength64 is < 1 or > 2048)
                {
                    response.StatusCode = 400;
                    await WriteJson(response, new { ok = false, message = "Invalid Personal API Settings request." });
                    return;
                }
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync(_cts?.Token ?? CancellationToken.None);
                result = await _personalApiSettings.ExecuteAsync(json, _cts?.Token ?? CancellationToken.None);
            }
        }
        catch
        {
            // This boundary never logs exception/request/provider text or secrets.
            result = new(false, "Personal API Settings is unavailable. No backend fallback was performed.");
        }
        await WriteJson(response, JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
