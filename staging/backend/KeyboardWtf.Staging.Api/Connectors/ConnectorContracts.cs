using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Staging.Api.Configuration;

namespace KeyboardWtf.Staging.Api.Connectors;

public sealed class ConnectorOptions
{
    public const string Section = "Connectors";
    public bool Enabled { get; set; }
    public string Environment { get; set; } = "staging";
    public string ApiKey { get; set; } = "";
    public string WebBaseUrl { get; set; } = "";
    public Dictionary<string, string> AuthConfigIds { get; set; } = new(StringComparer.Ordinal);

    public bool Validate(PaidSystemOptions paid)
    {
        if (!Enabled) return true;
        if (Environment != DeploymentSafety.ProviderEnvironment(paid.DeploymentEnvironment)
            || string.IsNullOrWhiteSpace(ApiKey)
            || !Uri.TryCreate(WebBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || AuthConfigIds.Count == 0
            || AuthConfigIds.Any(pair => !ConnectorIds.IsKey(pair.Key)
                || !Regex.IsMatch(pair.Value, "^ac_[A-Za-z0-9_-]{4,100}$")))
            throw new InvalidOperationException("Connector deployment configuration is incomplete or inconsistent.");
        if (paid.DeploymentEnvironment == PaidDeploymentEnvironment.Production
            && WebBaseUrl.Contains("staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production connectors cannot use a staging return URL.");
        return true;
    }
}

public sealed record ConnectorCatalogItem(string Id, string Name, string Family, string Icon,
    IReadOnlyList<string> SearchTerms, string Availability, string Description)
{
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
public sealed record ConnectorAccount(string AccountId, string Family, string Label, string State,
    bool IsDefault, DateTimeOffset? VerifiedAt);
public sealed record ConnectorSnapshot(bool Enabled, IReadOnlyList<ConnectorCatalogItem> Catalog,
    IReadOnlyList<ConnectorAccount> Accounts, string? Notice = null);
public sealed record ConnectRequest(string ConnectorId, string RequestId);
public sealed record CompleteConnectRequest(string State);
public sealed record ConnectReply(string AttemptId, string State, string? RedirectUrl,
    DateTimeOffset ExpiresAt, string? AccountId = null);
public sealed record ConnectorResolveRequest(string ConnectorId, string UserRequest, string? AccountId = null,
    string? LogicalLiveSessionId = null);
public sealed record ConnectorAccountChangeRequest(string AccountId);
public sealed record ConnectorCapability(string Id, string Description, bool Writes,
    IReadOnlyDictionary<string, string> Arguments)
{
    public string Activity { get; init; } = "Working…";
    public string Help { get; init; } = "";
    public string EvidenceKind => KeyboardWtf.Connectors.ConnectorEvidenceTraits.For(Id);
}
public sealed record ConnectorTaskReply(string TaskId, string State, string ConnectorId,
    string? AccountId, IReadOnlyList<ConnectorAccount> Choices,
    IReadOnlyList<ConnectorCapability> Capabilities, DateTimeOffset ExpiresAt, string? Message = null)
{
    public IReadOnlyList<ConnectorExecutionReply> Operations { get; init; } = [];
}
public sealed record ConnectorExecuteRequest(string TaskId, string CapabilityId,
    Dictionary<string, JsonElement> Arguments, bool Confirmed = false, string? LogicalLiveSessionId = null);
public sealed record ConnectorExecutionReply(string TaskId, string OperationId, string State,
    JsonElement? Result, bool Verified, string? Message = null);

// Provider records never cross the HTTP boundary. In particular, credentials,
// provider auth config IDs, and raw account payloads are not client DTOs.
public sealed record ProviderConnection(string Id, string UserId, string AuthConfigId,
    string Toolkit, string Status, bool Disabled, string Alias);
public sealed record ProviderConnectReply(string Id, string RedirectUrl);
public sealed record ConnectorAttempt(string Id, string Family, string StateHash, string RequestId,
    string State, DateTimeOffset ExpiresAt, string? ProviderId = null, string? RedirectUrl = null,
    string? AccountId = null, bool CleanupRequired = true);
public sealed record StoredConnectorAccount(string Id, string Family, string ProviderId,
    string Label, string State, bool IsDefault, DateTimeOffset? VerifiedAt);
public sealed record StoredConnectorTask(string Id, string ConnectorId, string? AccountId,
    string State, bool AllowsWrites, DateTimeOffset ExpiresAt)
{
    // Optional client-origin conversation correlation, never identity/authority
    // or a server-verified AI charge reference. Auth hash comes from the caller.
    public string? LogicalLiveSessionId { get; init; }
    public string? AuthSessionHash { get; init; }
}
public sealed record StoredConnectorOperation(string Id, string TaskId, string AccountId,
    string CapabilityId, string Digest, string State, string? ResourceId, DateTimeOffset ExpiresAt)
{
    public string? LogicalLiveSessionId { get; init; }
    public string? AuthSessionHash { get; init; }
    // Action/verification adapter invocations, not OAuth/status reads, AI calls,
    // network-success proof, or a chargeable provider-usage measurement.
    public int ProviderRequestCount { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public string? ErrorCode { get; init; }
}
public sealed record ConnectorClaim<T>(T Item, bool Created);

public interface IConnectorProvider
{
    Task<ProviderConnectReply> ConnectAsync(string userId, string authConfigId, string callbackUrl,
        string alias, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderConnection>> ListAsync(string userId, CancellationToken cancellationToken);
    Task<ProviderConnection?> GetAsync(string connectionId, CancellationToken cancellationToken);
    Task DisconnectAsync(string connectionId, CancellationToken cancellationToken);
    Task<JsonElement> RequestAsync(string connectionId, string endpoint, string method,
        object? body, CancellationToken cancellationToken);
}

public interface IConnectorStore
{
    Task EnsureActiveAsync(string userId, CancellationToken cancellationToken);
    Task<ConnectorClaim<ConnectorAttempt>> ClaimAttemptAsync(string userId, ConnectorAttempt attempt,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<ConnectorAttempt> GetAttemptAsync(string userId, string id, CancellationToken cancellationToken);
    Task SaveAttemptAsync(string userId, ConnectorAttempt attempt, CancellationToken cancellationToken);
    Task<bool> CompleteAttemptAsync(string userId, ConnectorAttempt attempt, StoredConnectorAccount account,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectorAttempt>> AttemptsAsync(string userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredConnectorAccount>> AccountsAsync(string userId, CancellationToken cancellationToken);
    // Update an existing account only. Creation belongs to atomic OAuth adoption.
    Task SaveAccountAsync(string userId, StoredConnectorAccount account, CancellationToken cancellationToken);
    Task RemoveAccountAsync(string userId, string accountId, CancellationToken cancellationToken);
    Task SetDefaultAsync(string userId, string accountId, CancellationToken cancellationToken);
    Task CreateTaskAsync(string userId, StoredConnectorTask task, CancellationToken cancellationToken);
    Task<bool> RebindTaskAsync(string userId, string previousId, StoredConnectorTask replacement,
        IReadOnlyList<string> writeCapabilities, DateTimeOffset now, CancellationToken cancellationToken);
    Task<StoredConnectorTask> GetTaskAsync(string userId, string id, CancellationToken cancellationToken);
    Task CancelTaskAsync(string userId, string id, CancellationToken cancellationToken);
    Task FinishTaskAsync(string userId, string id, CancellationToken cancellationToken);
    Task EndTaskAsync(string userId, string id, string state, CancellationToken cancellationToken);
    Task<ConnectorClaim<StoredConnectorOperation>> ClaimOperationAsync(string userId,
        StoredConnectorOperation operation, DateTimeOffset now, CancellationToken cancellationToken);
    Task SaveOperationAsync(string userId, StoredConnectorOperation operation, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredConnectorOperation>> OperationsAsync(string userId, string taskId, CancellationToken cancellationToken);
    Task PurgeAsync(string userId, CancellationToken cancellationToken);
}

public sealed class ConnectorProviderException : InvalidOperationException
{
    public string Code { get; }
    public bool OutcomeUnknown { get; }
    // Internal recovery hints, never customer authority or provider headers.
    // The normalized error alone is deliberately insufficient for replay.
    internal bool RetryableRead { get; }
    internal TimeSpan? RetryAfter { get; }
    public ConnectorProviderException(string code, bool outcomeUnknown = false,
        bool retryableRead = false, TimeSpan? retryAfter = null)
        : base(code switch
        {
            "rate_limited" => "This service is busy. Wait briefly before trying a new request.",
            "reconnect_required" => "Reconnect this account in Connectors before trying again.",
            "scope_required" => "This account has not granted the permission needed for this action.",
            "resource_not_found" => "This resource was not found or is not visible to this account. A latest release may not exist; this does not mean the connector is disconnected.",
            "outcome_unknown" => "The action's outcome could not be verified. Do not repeat it yet.",
            _ => "The connected service is temporarily unavailable. No success has been confirmed."
        }) { Code = code; OutcomeUnknown = outcomeUnknown; RetryableRead = retryableRead; RetryAfter = retryAfter; }
}

public static class ConnectorIds
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static bool IsKey(string value) => Regex.IsMatch(value ?? "", "^[a-z][a-z0-9_-]{0,63}$");
    public static string RequireId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Regex.IsMatch(value, "^[a-f0-9]{32,64}$"))
            throw new ArgumentException("Invalid connector reference.");
        return value;
    }
    public static string ProviderUser(string environment, string userId) => "kwt_" + environment + "_" + Hash(userId);
    public static bool VerifyState(string state, string hash) => state.Length is >= 32 and <= 128
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(state)), Encoding.ASCII.GetBytes(hash));
    public static ConnectorAccount Public(StoredConnectorAccount account) => new(account.Id, account.Family,
        account.Label, account.State, account.IsDefault, account.VerifiedAt);
}
