using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Staging.Api.Domain;

namespace KeyboardWtf.Staging.Api.Connectors;

// Diagnostic metadata only. None of these fields admits work, selects an
// account, changes prices, or claims a verified link to an AI usage record.
internal static class ConnectorTelemetry
{
    internal static string? LogicalLiveId(string? value) => value is { Length: 32 }
        && Regex.IsMatch(value, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) ? value : null;
    internal static string? HashValue(string? value) => value is { Length: 64 }
        && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant) ? value : null;
    internal static string UserHash(string uid) => ConnectorIds.Hash("connector-uid:" + uid);
    internal static string? AuthHash(string? session) => string.IsNullOrEmpty(session) ? null
        : ConnectorIds.Hash("connector-auth-session:" + session);
    internal static string? Error(string? code) => code switch
    {
        "rate_limited" or "reconnect_required" or "scope_required" or "outcome_unknown"
            or "unavailable" or "resource_not_found" or "cancelled" or "local_failure" or "verification_unknown" => code,
        null or "" => null,
        _ => "unavailable"
    };
    internal static void Write(ILogger logger, CallerIdentity caller, StoredConnectorTask task,
        ConnectorDefinition definition, string phase, string state, string? operation = null,
        string? capability = null, int requests = 0, long elapsed = 0, string? error = null,
        bool correlationMismatch = false, long? connectionLookupMs = null, long? capabilityResolutionMs = null) => logger.LogInformation(
            "Connector telemetry phase={Phase} uidHash={UidHash} authSessionHash={AuthSessionHash} "
            + "callerAuthSessionHash={CallerAuthSessionHash} logicalLiveClientCorrelation={LogicalLiveClientCorrelation} "
            + "task={Task} operation={Operation} toolkit={Toolkit} capability={Capability} state={State} "
            + "providerActionRequestAttempts={ProviderActionRequestAttempts} actionElapsedMs={ActionElapsedMs} "
            + "errorCode={ErrorCode} correlationMismatch={CorrelationMismatch} "
            + "connectionLookupMs={ConnectionLookupMs} capabilityResolutionMs={CapabilityResolutionMs}",
            phase, UserHash(caller.UserId), HashValue(task.AuthSessionHash), AuthHash(caller.SessionId),
            LogicalLiveId(task.LogicalLiveSessionId), task.Id, operation, definition.Toolkit, capability,
            state, requests, elapsed, Error(error), correlationMismatch, connectionLookupMs, capabilityResolutionMs);
}

// Per-operation counter, not shared state and not a second provider pipeline.
// Count actual adapter invocations, including verification/reconciliation, with
// the existing cancellation tokens and no additional retries or payload logs.
internal sealed class ConnectorProviderRequestCounter(IConnectorProvider inner) : IConnectorProvider
{
    private int _requests;
    internal int Requests => Volatile.Read(ref _requests);
    public Task<JsonElement> RequestAsync(string connectionId, string endpoint, string method,
        object? body, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        return inner.RequestAsync(connectionId, endpoint, method, body, ct);
    }
    public Task<ProviderConnectReply> ConnectAsync(string userId, string authConfigId, string callbackUrl,
        string alias, CancellationToken ct) => inner.ConnectAsync(userId, authConfigId, callbackUrl, alias, ct);
    public Task<IReadOnlyList<ProviderConnection>> ListAsync(string userId, CancellationToken ct) => inner.ListAsync(userId, ct);
    public Task<ProviderConnection?> GetAsync(string connectionId, CancellationToken ct) => inner.GetAsync(connectionId, ct);
    public Task DisconnectAsync(string connectionId, CancellationToken ct) => inner.DisconnectAsync(connectionId, ct);
}
