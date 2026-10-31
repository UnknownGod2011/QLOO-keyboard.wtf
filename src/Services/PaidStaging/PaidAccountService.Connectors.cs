namespace KeyboardWtf.Services.PaidStaging;

using System.Net;
using System.Text.Json;

internal sealed partial class PaidAccountService
{
    // The existing authenticated desktop transport, without AI generation,
    // credit admission or a second credential store. Freeze the desktop session
    // across refresh/retry so a UI account switch cannot retarget an OAuth/write.
    internal event Action<string, string> ConnectorAccountInvalidated;

    internal string ConnectorSessionOwner => _tokens?.SessionId ?? "";
    // UI invalidation identity only: never expose a credential/session identifier.
    internal string ConnectorIdentity => ConnectorIdentityFor(ConnectorSessionOwner);
    internal static string ConnectorIdentityFor(string owner) => string.IsNullOrEmpty(owner) ? "" :
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(owner))).ToLowerInvariant();
    internal async Task<JsonElement> ConnectorRequestAsync(HttpMethod method, string path,
        object body = null, CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn) throw new InvalidOperationException("Sign in with Google in Account & Billing before using connectors.");
        if (path is null || !path.StartsWith("v1/connectors", StringComparison.Ordinal)
            || path.Length > 180 || path.Contains("..", StringComparison.Ordinal) || path.Contains('?')
            || path.Contains('#') || path.Contains('\\')) throw new ArgumentException("Invalid connector route.");
        var owner = ConnectorSessionOwner;
        var token = await EnsureAccessTokenAsync(cancellationToken);
        void RequireOwner()
        {
            if (owner.Length == 0 || owner != ConnectorSessionOwner)
                throw new InvalidOperationException("Your account changed. Start this connector request again.");
        }
        RequireOwner();
        JsonElement result;
        try { result = await SendAsync<JsonElement>(method, path, body, true, cancellationToken, token); }
        catch (PaidApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            RequireOwner();
            token = await RefreshTokensAsync(cancellationToken, forceRefresh: true);
            RequireOwner();
            result = await SendAsync<JsonElement>(method, path, body, true, cancellationToken, token);
        }
        RequireOwner();
        ObserveConnectorAccountState(owner, method, path, result);
        return result;
    }

    // Consume only authenticated account-state receipts already requested by the
    // app. This adds no polling, provider call or model work.
    internal void ObserveConnectorAccountState(string owner, HttpMethod method, string path, JsonElement result)
    {
        if (owner.Length == 0 || owner != ConnectorSessionOwner || result.ValueKind != JsonValueKind.Object) return;
        static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : "";
        void Invalidate(string id)
        {
            if (id.Length == 32 && id.All(char.IsAsciiHexDigit)) ConnectorAccountInvalidated?.Invoke(owner, id);
        }
        const string accounts = "v1/connectors/accounts/";
        if (method == HttpMethod.Delete && path.StartsWith(accounts, StringComparison.Ordinal)
            && Text(result, "state") == "disconnected") Invalidate(path[accounts.Length..]);
        // Resolve choices are stored labels, not fresh authorization observations.
        if (method == HttpMethod.Get && path == "v1/connectors/"
            && result.TryGetProperty("accounts", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
                if (Text(row, "state") is "disconnected" or "disconnecting" or "reconnect_required" or "revoked" or "expired" or "scope_required")
                    Invalidate(Text(row, "accountId"));
    }

    internal void OpenConnectorAttempt(string attemptId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(attemptId ?? "", "^[a-f0-9]{32}$"))
            throw new ArgumentException("Invalid connection attempt.");
        var owner = _account?.UserId;
        if (!IsSignedIn || string.IsNullOrWhiteSpace(owner))
            throw new InvalidOperationException("Wait for your signed-in account to finish loading.");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(owner))).ToLowerInvariant();
        _browserLauncher(new Uri(_configuration.WebBaseUri,
            "connectors?attempt=" + attemptId + "&source=desktop&owner=" + digest).ToString());
    }
}
