using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Connectors;

// No SDK agent, NLP execution, default-account selection or automatic write
// retry. The replaceable adapter is the only runtime owner of vendor protocol.
public sealed class ComposioConnectorProvider(HttpClient http, IOptions<ConnectorOptions> options)
    : IConnectorProvider
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly ConnectorOptions _options = options.Value;

    public async Task<ProviderConnectReply> ConnectAsync(string userId, string authConfigId,
        string callbackUrl, string alias, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Post, "connected_accounts", new
        {
            auth_config = new { id = authConfigId },
            connection = new { user_id = userId, callback_url = callbackUrl, alias }
        }, true, cancellationToken);
        var id = Text(result, "id");
        var redirect = Text(result, "redirect_url");
        if (redirect.Length == 0) redirect = Text(result, "redirectUrl");
        if (!ValidProviderId(id) || !Uri.TryCreate(redirect, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo.Length != 0
            || !IsAuthorizationHost(uri.Host))
            throw new ConnectorProviderException("outcome_unknown", true);
        return new(id, redirect);
    }

    public async Task<IReadOnlyList<ProviderConnection>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        var result = new List<ProviderConnection>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var path = "connected_accounts?limit=100&user_ids=" + Uri.EscapeDataString(userId)
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var payload = await SendAsync(HttpMethod.Get, path, null, false, cancellationToken);
            if (!payload.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new ConnectorProviderException("unavailable");
            foreach (var item in items.EnumerateArray())
            {
                var account = ParseConnection(item);
                // Filtering is verified independently: never trust vendor query
                // filtering alone as a cross-user isolation guarantee.
                if (!string.Equals(account.UserId, userId, StringComparison.Ordinal))
                    throw new ConnectorProviderException("unavailable");
                result.Add(account);
            }
            cursor = Text(payload, "next_cursor");
            if (string.IsNullOrEmpty(cursor)) return result;
        }
        throw new ConnectorProviderException("unavailable"); // Never silently truncate ownership cleanup.
    }

    public async Task<ProviderConnection?> GetAsync(string connectionId, CancellationToken cancellationToken)
    {
        ValidateProviderId(connectionId);
        try
        {
            var account = ParseConnection(await SendAsync(HttpMethod.Get, "connected_accounts/" + connectionId,
                null, false, cancellationToken, allowNotFound: true));
            if (account.Id != connectionId) throw new ConnectorProviderException("unavailable");
            return account;
        }
        catch (ProviderNotFoundException) { return null; }
    }

    public async Task DisconnectAsync(string connectionId, CancellationToken cancellationToken)
    {
        ValidateProviderId(connectionId);
        // Revocation is a distinct current API operation. Deleting the index
        // alone must not be presented as revoking the user's grant.
        try
        {
            await SendAsync(HttpMethod.Post, "connected_accounts/" + connectionId + "/revoke",
                new { }, true, cancellationToken, allowNotFound: true, allowUnsupportedRevocation: true);
        }
        catch (ProviderNotFoundException)
        {
            // A missing revoke route is not proof that the connection is absent.
            // DELETE and an independent GET still own credential-removal proof.
        }
        catch (ProviderRevocationUnsupportedException)
        {
            // Some user-OAuth toolkits cannot revoke their upstream grant.
            // Remove our credential owner anyway; do not claim the grant was
            // revoked. User-facing disconnect means no keyboard.wtf access.
        }
        try
        {
            await SendAsync(HttpMethod.Delete, "connected_accounts/" + connectionId,
                null, true, cancellationToken, allowNotFound: true);
        }
        catch (ProviderNotFoundException) { }
        if (await GetAsync(connectionId, cancellationToken) is not null)
            throw new ConnectorProviderException("outcome_unknown", true);
    }

    public async Task<JsonElement> RequestAsync(string connectionId, string endpoint, string method,
        object? body, CancellationToken cancellationToken)
    {
        ValidateProviderId(connectionId);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || !AllowedApiHost(uri.Host)
            || method is not ("GET" or "POST"))
            throw new ArgumentException("Unsupported connector action destination.");
        var payload = await SendAsync(HttpMethod.Post, "tools/execute/proxy", new
        {
            connected_account_id = connectionId, endpoint, method, body
        }, method != "GET", cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code))
            throw new ConnectorProviderException(method == "GET" ? "unavailable" : "outcome_unknown", method != "GET");
        if (code is < 200 or >= 300) throw Failure(code, method != "GET", ProxyRetryAfter(payload));
        if (!payload.TryGetProperty("data", out var data))
            throw new ConnectorProviderException(method == "GET" ? "unavailable" : "outcome_unknown", method != "GET");
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("ok", out var ok)
            && ok.ValueKind == JsonValueKind.False)
        {
            var error = Text(data, "error");
            throw new ConnectorProviderException(error is "missing_scope" or "no_permission"
                ? "scope_required" : error is "invalid_auth" or "token_revoked" ? "reconnect_required" : "unavailable");
        }
        return data.Clone(); // Proxy response headers and credentials never leave this method.
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body,
        bool mayHaveSideEffects, CancellationToken cancellationToken, bool allowNotFound = false,
        bool allowUnsupportedRevocation = false)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ConnectorProviderException("unavailable");
        // ResponseHeadersRead stops HttpClient's timeout at the headers. Keep
        // the same bounded deadline through body reading and JSON parsing too.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var maximum = TimeSpan.FromSeconds(35);
        deadline.CancelAfter(http.Timeout != Timeout.InfiniteTimeSpan && http.Timeout < maximum ? http.Timeout : maximum);
        var boundedCancellation = deadline.Token;
        using var request = new HttpRequestMessage(method, "https://backend.composio.dev/api/v3.1/" + path);
        request.Headers.Add("x-api-key", _options.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, boundedCancellation);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) throw new ProviderNotFoundException();
            if (allowUnsupportedRevocation && (int)response.StatusCode is 400 or 409)
                throw new ProviderRevocationUnsupportedException();
            if (!response.IsSuccessStatusCode)
                throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? new ConnectorProviderException("unavailable") // Project credentials, not end-user OAuth.
                    : Failure((int)response.StatusCode, mayHaveSideEffects, ResponseRetryAfter(response));
            await using var stream = await response.Content.ReadAsStreamAsync(boundedCancellation);
            using var bounded = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), boundedCancellation)) > 0)
            {
                if (bounded.Length + read > MaximumResponseBytes)
                    throw new ConnectorProviderException(mayHaveSideEffects ? "outcome_unknown" : "unavailable", mayHaveSideEffects);
                await bounded.WriteAsync(buffer.AsMemory(0, read), boundedCancellation);
            }
            if (bounded.Length == 0) return JsonSerializer.SerializeToElement(new { });
            bounded.Position = 0;
            using var json = await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 40 }, boundedCancellation);
            return json.RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (!mayHaveSideEffects && cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            // No original exception, body or URI-with-state is attached/logged.
            // An ambiguous write is not automatically replayable, even on cancel.
            throw new ConnectorProviderException(mayHaveSideEffects ? "outcome_unknown" : "unavailable", mayHaveSideEffects,
                retryableRead: !mayHaveSideEffects && exception is not JsonException);
        }
    }

    private static ConnectorProviderException Failure(int status, bool write, TimeSpan? retryAfter = null) => new(status switch
    {
        401 => "reconnect_required", 403 => "scope_required", 404 when !write => "resource_not_found", 429 => "rate_limited",
        >= 500 when write => "outcome_unknown", _ => "unavailable"
    }, write && status >= 500, retryableRead: !write && status is 408 or 429 or 500 or 502 or 503 or 504,
        retryAfter: retryAfter);

    private static TimeSpan? ProxyRetryAfter(JsonElement payload)
    {
        if (!payload.TryGetProperty("headers", out var headers)) return null;
        if (headers.ValueKind != JsonValueKind.Object) return TimeSpan.MaxValue;
        TimeSpan? wait = null;
        var seen = false;
        foreach (var header in headers.EnumerateObject())
        {
            if (!header.Name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen) return TimeSpan.MaxValue;
            seen = true;
            var value = header.Value;
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1) value = value[0];
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()
                : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
            wait = RetryDelay(text);
        }
        return wait;
    }
    private static TimeSpan? ResponseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        var entries = values.Take(2).ToArray();
        return entries.Length == 1 ? RetryDelay(entries[0]) : TimeSpan.MaxValue;
    }
    private static TimeSpan RetryDelay(string? text)
    {
        // Invalid, duplicate or oversized declared waits must not cause an
        // early replay. Only a typed duration is retained, never raw headers.
        if (text is null || text.Length > 128
            || !System.Net.Http.Headers.RetryConditionHeaderValue.TryParse(text, out var retry))
            return TimeSpan.MaxValue;
        return retry.Delta ?? (retry.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.MaxValue);
    }
    private static ProviderConnection ParseConnection(JsonElement value)
    {
        var id = Text(value, "id");
        ValidateProviderId(id);
        return new(id, Text(value, "user_id"), value.TryGetProperty("auth_config", out var auth) ? Text(auth, "id") : "",
            value.TryGetProperty("toolkit", out var toolkit) ? Text(toolkit, "slug") : "",
            Text(value, "status"), value.TryGetProperty("is_disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True,
            Text(value, "alias"));
    }
    private static bool AllowedApiHost(string host) => host is "www.googleapis.com" or "gmail.googleapis.com"
        or "docs.googleapis.com" or "forms.googleapis.com" or "api.github.com" or "slack.com" or "discord.com";
    private static bool IsAuthorizationHost(string host) => host is "backend.composio.dev" or "connect.composio.dev"
        or "accounts.google.com" or "github.com" or "slack.com" or "discord.com";
    private static bool ValidProviderId(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]{4,128}$");
    private static void ValidateProviderId(string value)
    {
        if (!ValidProviderId(value)) throw new ArgumentException("Invalid service account reference.");
    }
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private sealed class ProviderNotFoundException : Exception;
    private sealed class ProviderRevocationUnsupportedException : Exception;
}
