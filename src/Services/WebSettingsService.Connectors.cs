namespace KeyboardWtf.Services;

using System.Net;
using System.Text.Json;

public sealed partial class WebSettingsService
{
    internal static bool TryResolveConnectorResource(string path, out string resource, out string type)
    {
        resource = null; type = null;
        var relative = path?.StartsWith("/connectors/", StringComparison.Ordinal) == true ? path[12..] : "";
        if (relative is "connectors-panel.js" or "connectors-settings.js" or "connectors-panel.css")
        {
            resource = "web/connectors/" + relative;
            type = relative.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
            return true;
        }
        if (relative.StartsWith("icons/", StringComparison.Ordinal) && relative.EndsWith(".svg", StringComparison.Ordinal))
        {
            var name = relative[6..^4];
            // Resolve only a bundled resource, never a filesystem or remote URL.
            if (name.Length is < 1 or > 64 || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_-]+$")) return false;
            resource = "web/connectors/icons/" + name + ".svg"; type = "image/svg+xml"; return true;
        }
        return false;
    }
    private static async Task ServeConnectorResource(HttpListenerResponse response, string path)
    {
        if (!TryResolveConnectorResource(path, out var resource, out var type)) { response.StatusCode = 404; response.Close(); return; }
        await using var stream = typeof(WebSettingsService).Assembly.GetManifestResourceStream(resource);
        if (stream is null) { response.StatusCode = 404; response.Close(); return; }
        response.ContentType = type; response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers[HttpResponseHeader.CacheControl] = "no-store"; response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(response.OutputStream); response.Close();
    }

    private async Task HandleConnectorSettings(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        if (!HasSettingsActionHeader(request) || !IsSameSettingsReferer(request.Headers["Referer"], Port)
            || !IsSameOriginFetch(request.Headers["Sec-Fetch-Site"]))
        {
            response.StatusCode = 403;
            await WriteJson(response, new { ok = false, code = "settings_session_expired", error = "Open connectors from the current Settings page." });
            return;
        }
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        try
        {
            if (_paidAccount is null) throw new InvalidOperationException("Account services are unavailable.");
            JsonElement result;
            var method = request.HttpMethod;
            if (method == "GET" && path == "/api/connectors")
                result = await _paidAccount.ConnectorRequestAsync(HttpMethod.Get, "v1/connectors/");
            else if (method == "GET" && System.Text.RegularExpressions.Regex.IsMatch(path, "^/api/connectors/attempts/[a-f0-9]{32}$"))
                result = await _paidAccount.ConnectorRequestAsync(HttpMethod.Get, "v1/connectors/attempts/" + path.Split('/')[4]);
            else if (method == "POST" && path == "/api/connectors/connect")
            {
                var body = await ReadConnectorBody(request);
                var connectorId = GetJsonString(body, "connectorId"); var requestId = GetJsonString(body, "requestId");
                result = await _paidAccount.ConnectorRequestAsync(HttpMethod.Post, "v1/connectors/connect", new { connectorId, requestId });
                if (result.TryGetProperty("redirectUrl", out var redirect) && redirect.ValueKind == JsonValueKind.String)
                    _paidAccount.OpenConnectorAttempt(GetJsonString(result, "attemptId"));
                // A vendor redirect is not returned to localhost code. Opening
                // the branded account-bound flow is a desktop-owned operation.
                result = JsonSerializer.SerializeToElement(new { attemptId = GetJsonString(result, "attemptId"),
                    state = GetJsonString(result, "state"), expiresAt = result.GetProperty("expiresAt"), redirectUrl = "desktop-opened" });
            }
            else if ((method == "DELETE" || method == "POST") && System.Text.RegularExpressions.Regex.IsMatch(path,
                "^/api/connectors/accounts/[a-f0-9]{32}(?:/default)?$"))
            {
                var suffix = path["/api/connectors/".Length..];
                if ((method == "DELETE" && suffix.EndsWith("/default", StringComparison.Ordinal))
                    || (method == "POST" && !suffix.EndsWith("/default", StringComparison.Ordinal))) throw new ArgumentException("Unsupported account action.");
                result = await _paidAccount.ConnectorRequestAsync(new HttpMethod(method), "v1/connectors/" + suffix);
            }
            else { response.StatusCode = 404; await WriteJson(response, new { error = "Connector route not found." }); return; }
            await WriteJson(response, result.GetRawText());
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or HttpRequestException or JsonException or PaidStaging.PaidApiException)
        {
            response.StatusCode = 400;
            await WriteJson(response, new { error = exception is HttpRequestException or PaidStaging.PaidApiException
                ? "The connector service could not complete this request. Refresh or sign in again when ready." : exception.Message });
        }
    }
    private static async Task<JsonElement> ReadConnectorBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 < 0 || request.ContentLength64 > 8192) throw new ArgumentException("Connector request is too large.");
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var text = await reader.ReadToEndAsync();
        if (text.Length > 8192) throw new ArgumentException("Connector request is too large.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        return document.RootElement.Clone();
    }
}
