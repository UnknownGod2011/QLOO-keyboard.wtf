using System.Text.Json;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Connectors;

public sealed class ConnectorConnectionService(IConnectorStore store, IConnectorProvider provider,
    ConnectorRegistry registry, IClock clock, IOptions<ConnectorOptions> options)
{
    private readonly ConnectorOptions _options = options.Value;
    public string ProviderUser(string uid) => ConnectorIds.ProviderUser(_options.Environment, uid);
    public bool Enabled => _options.Enabled;
    private void RequireEnabled()
    {
        if (!Enabled) throw new InvalidOperationException("Connectors are not available in this environment yet.");
    }

    public async Task<ConnectorSnapshot> SnapshotAsync(CallerIdentity caller, CancellationToken ct)
    {
        if (!Enabled) return new(false, registry.Catalog(), []);
        await store.EnsureActiveAsync(caller.UserId, ct);
        var accounts = await store.AccountsAsync(caller.UserId, ct);
        // This endpoint is called on management open/focus/mutations, not on
        // every ordinary Jarvis turn. No stale index alone means Connected.
        var refreshed = new List<ConnectorAccount>();
        string? notice = null;
        foreach (var account in accounts)
        {
            if (account.State is "disconnecting" or "disconnected")
            { refreshed.Add(ConnectorIds.Public(account)); continue; }
            try
            {
                var live = await provider.GetAsync(account.ProviderId, ct);
                if (live is not null) ValidateBinding(caller.UserId, account.Family, live);
                var updated = account with { State = State(live), VerifiedAt = clock.UtcNow };
                await store.SaveAccountAsync(caller.UserId, updated, ct);
                refreshed.Add(ConnectorIds.Public(updated));
            }
            catch (ConnectorProviderException)
            {
                // Provider outage is unknown, never a verified green check.
                refreshed.Add(ConnectorIds.Public(account with { State = "unavailable", VerifiedAt = null }));
                notice = "Some account statuses could not be verified. Try refreshing shortly.";
            }
            catch (KeyNotFoundException)
            {
                // A cleaned tombstone disappeared while provider I/O waited.
                // Do not recreate it or publish its old Connected check.
            }
        }
        // An overlapping disconnect may have won while provider I/O waited.
        // Never return the stale candidate as a verified Connected check.
        var current = (await store.AccountsAsync(caller.UserId, ct)).ToDictionary(value => value.Id);
        return new(true, registry.Catalog(), refreshed.Where(value => current.ContainsKey(value.AccountId)).Select(value =>
        {
            var stored = current[value.AccountId];
            return value.State == "unavailable" && stored.State is not ("disconnecting" or "disconnected")
                ? value with { IsDefault = stored.IsDefault } : ConnectorIds.Public(stored);
        }).ToArray(), notice);
    }

    public async Task<ConnectReply> ConnectAsync(CallerIdentity caller, ConnectRequest request, CancellationToken ct)
    {
        RequireEnabled();
        var definition = registry.Get(request.ConnectorId);
        if (!registry.Available(definition)) throw new InvalidOperationException("This connector is coming soon.");
        ConnectorIds.RequireId(request.RequestId);
        var state = ConnectorIds.New() + ConnectorIds.New();
        var candidate = new ConnectorAttempt(ConnectorIds.New(), definition.Family, ConnectorIds.Hash(state),
            request.RequestId, "creating", clock.UtcNow.AddMinutes(10));
        var claim = await store.ClaimAttemptAsync(caller.UserId, candidate, clock.UtcNow, ct);
        if (!claim.Created) return await AttemptAsync(caller, claim.Item.Id, null, ct);
        var callback = _options.WebBaseUrl.TrimEnd('/') + "/connectors?attempt=" + candidate.Id + "&state=" + state;
        try
        {
            var response = await provider.ConnectAsync(ProviderUser(caller.UserId), registry.AuthConfig(definition.Family),
                callback, "kwt_" + candidate.Id, ct);
            candidate = candidate with { State = "connecting", ProviderId = response.Id, RedirectUrl = response.RedirectUrl };
            // Persist the acknowledgement independently of an aborted HTTP
            // caller. Never lose the account ref needed by deletion/reconcile.
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await store.SaveAttemptAsync(caller.UserId, candidate, save.Token);
        }
        catch (ConnectorProviderException exception)
        {
            candidate = candidate with { State = exception.OutcomeUnknown ? "outcome_unknown" : "failed" };
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await store.SaveAttemptAsync(caller.UserId, candidate, save.Token);
        }
        return Reply(candidate);
    }

    public async Task<ConnectReply> AttemptAsync(CallerIdentity caller, string attemptId, string? state, CancellationToken ct)
    {
        RequireEnabled();
        var attempt = await store.GetAttemptAsync(caller.UserId, ConnectorIds.RequireId(attemptId), ct);
        if (state is not null && !ConnectorIds.VerifyState(state, attempt.StateHash))
            throw new UnauthorizedAccessException("This connection return does not match your sign-in attempt.");
        if (attempt.State == "connected")
        {
            var owned = (await store.AccountsAsync(caller.UserId, ct)).SingleOrDefault(value => value.Id == attempt.AccountId);
            var verified = owned is null || owned.State is "disconnecting" or "disconnected" ? null : await provider.GetAsync(owned.ProviderId, ct);
            if (verified is not null) ValidateBinding(caller.UserId, attempt.Family, verified);
            return State(verified) == "connected" ? Reply(attempt) : Reply(attempt with { State = "failed", RedirectUrl = null });
        }
        if (attempt.State is "failed" or "cancelled") return Reply(attempt);
        if (attempt.ExpiresAt <= clock.UtcNow)
        {
            await store.SaveAttemptAsync(caller.UserId, attempt with { State = "expired", RedirectUrl = null }, ct);
            return Reply(attempt with { State = "expired", RedirectUrl = null });
        }
        if (attempt.ProviderId is null)
        {
            // Lost create response: reconcile the bound alias, never repeat POST.
            var matches = (await provider.ListAsync(ProviderUser(caller.UserId), ct))
                .Where(value => value.Alias == "kwt_" + attempt.Id).ToArray();
            if (matches.Length > 1) throw new ConnectorProviderException("outcome_unknown", true);
            if (matches.Length == 0) return Reply(attempt);
            ValidateBinding(caller.UserId, attempt.Family, matches[0]);
            attempt = attempt with { ProviderId = matches[0].Id };
            await store.SaveAttemptAsync(caller.UserId, attempt, ct);
        }
        var live = await provider.GetAsync(attempt.ProviderId!, ct);
        if (live is null)
        {
            attempt = attempt with { State = "failed", RedirectUrl = null };
            await store.SaveAttemptAsync(caller.UserId, attempt, ct);
            return Reply(attempt);
        }
        ValidateBinding(caller.UserId, attempt.Family, live);
        var status = State(live);
        if (status == "connected")
        {
            var id = ConnectorIds.Hash(attempt.Family + ":" + live.Id)[..32];
            var existing = (await store.AccountsAsync(caller.UserId, ct)).SingleOrDefault(value => value.Id == id);
            var label = existing?.Label ?? await ReadLabelAsync(live, attempt.Family, ct);
            var account = new StoredConnectorAccount(id, attempt.Family, live.Id, label, "connected", existing?.IsDefault ?? false, clock.UtcNow);
            if (!await store.CompleteAttemptAsync(caller.UserId, attempt, account, clock.UtcNow, ct))
                return Reply(attempt with { State = "failed", RedirectUrl = null });
            attempt = attempt with { State = "connected", AccountId = id, RedirectUrl = null, CleanupRequired = false };
        }
        else if (status is "revoked" or "expired" or "disconnected" or "reconnect_required")
        {
            attempt = attempt with { State = "failed", RedirectUrl = null };
            await store.SaveAttemptAsync(caller.UserId, attempt, ct);
        }
        return Reply(attempt);
    }

    public async Task CancelAttemptAsync(CallerIdentity caller, string id, CancellationToken ct)
    {
        RequireEnabled();
        var attempt = await store.GetAttemptAsync(caller.UserId, id, ct);
        if (attempt.State == "connected") throw new InvalidOperationException("Disconnect the connected account instead.");
        if (attempt.ProviderId is null && attempt.State is "creating" or "outcome_unknown")
            throw new InvalidOperationException("This sign-in is still being reconciled. Wait before starting another one.");
        await store.SaveAttemptAsync(caller.UserId, attempt with { State = "cancelled", RedirectUrl = null }, ct);
        attempt = await store.GetAttemptAsync(caller.UserId, id, ct);
        if (attempt.State != "cancelled") throw new InvalidOperationException("Disconnect the connected account instead.");
        if (attempt.ProviderId is not null)
        {
            var live = await provider.GetAsync(attempt.ProviderId, ct);
            if (live is not null)
            {
                ValidateBinding(caller.UserId, attempt.Family, live);
                await provider.DisconnectAsync(live.Id, ct);
            }
        }
        await store.SaveAttemptAsync(caller.UserId, attempt with { State = "cancelled", RedirectUrl = null, CleanupRequired = false }, ct);
    }

    internal async Task CleanupExpiredAttemptAsync(string uid, string id, CancellationToken ct)
    {
        RequireEnabled();
        var attempt = await store.GetAttemptAsync(uid, id, ct);
        if (!attempt.CleanupRequired || attempt.ExpiresAt > clock.UtcNow || attempt.State == "connected") return;
        // Fence account adoption before provider I/O. Atomic completion cannot
        // turn this expired/abandoned credential into a new connected account.
        await store.SaveAttemptAsync(uid, attempt with { State = "expired", RedirectUrl = null }, ct);
        attempt = await store.GetAttemptAsync(uid, id, ct);
        if (attempt.State is not ("expired" or "cancelled")) return;
        var candidates = attempt.ProviderId is not null
            ? new[] { await provider.GetAsync(attempt.ProviderId, ct) }.Where(value => value is not null).Cast<ProviderConnection>().ToArray()
            : (await provider.ListAsync(ProviderUser(uid), ct)).Where(value => value.Alias == "kwt_" + attempt.Id).ToArray();
        foreach (var live in candidates)
        {
            ValidateBinding(uid, attempt.Family, live);
            await provider.DisconnectAsync(live.Id, ct);
        }
        await store.SaveAttemptAsync(uid, attempt with { CleanupRequired = false }, ct);
    }

    public async Task<StoredConnectorAccount> VerifiedAccountAsync(string uid, string family, string id, CancellationToken ct)
    {
        RequireEnabled();
        await store.EnsureActiveAsync(uid, ct);
        var account = (await store.AccountsAsync(uid, ct)).SingleOrDefault(value => value.Id == ConnectorIds.RequireId(id)
            && value.Family == family) ?? throw new KeyNotFoundException("That account is not connected to your profile.");
        if (account.State is "disconnecting" or "disconnected") throw new ConnectorProviderException("reconnect_required");
        var live = await provider.GetAsync(account.ProviderId, ct);
        if (live is not null) ValidateBinding(uid, family, live);
        if (State(live) != "connected")
        {
            await store.SaveAccountAsync(uid, account with { State = State(live), VerifiedAt = clock.UtcNow }, ct);
            throw new ConnectorProviderException("reconnect_required");
        }
        return account;
    }

    public async Task DisconnectAsync(CallerIdentity caller, string id, CancellationToken ct)
    {
        RequireEnabled();
        await store.EnsureActiveAsync(caller.UserId, ct);
        var account = (await store.AccountsAsync(caller.UserId, ct)).SingleOrDefault(value => value.Id == ConnectorIds.RequireId(id))
            ?? throw new KeyNotFoundException("That account was not found.");
        var live = await provider.GetAsync(account.ProviderId, ct);
        if (live is not null) ValidateBinding(caller.UserId, account.Family, live);
        await store.SaveAccountAsync(caller.UserId, account with { State = "disconnecting", IsDefault = false }, ct);
        if (live is not null) await provider.DisconnectAsync(account.ProviderId, ct);
        await store.RemoveAccountAsync(caller.UserId, id, ct);
    }

    public async Task CleanupDeletedUserAsync(string uid, CancellationToken ct)
    {
        if (!Enabled)
        {
            if ((await store.AccountsAsync(uid, ct)).Count > 0 || (await store.AttemptsAsync(uid, ct)).Count > 0)
                throw new InvalidOperationException("Account deletion is pending while connector cleanup is unavailable.");
            return;
        }
        // The existing account-deletion fence already prevents new claims.
        // Detect pre-fence in-flight creates before declaring cleanup complete.
        var attempts = await store.AttemptsAsync(uid, ct);
        var providerAccounts = await provider.ListAsync(ProviderUser(uid), ct);
        foreach (var attempt in attempts.Where(value => value.ProviderId is null
            && value.State is "creating" or "outcome_unknown" && value.ExpiresAt > clock.UtcNow))
            if (!providerAccounts.Any(value => value.Alias == "kwt_" + attempt.Id))
                throw new InvalidOperationException("Account deletion is pending while a connection is reconciled.");
        foreach (var live in providerAccounts)
        {
            if (live.UserId != ProviderUser(uid)) throw new UnauthorizedAccessException("Connector ownership could not be confirmed.");
            await provider.DisconnectAsync(live.Id, ct);
        }
        if ((await provider.ListAsync(ProviderUser(uid), ct)).Count != 0)
            throw new InvalidOperationException("Account deletion is pending while connected accounts are removed.");
        await store.PurgeAsync(uid, ct);
    }

    internal void ValidateBinding(string uid, string family, ProviderConnection live)
    {
        var expectedToolkit = ConnectorRegistry.Definitions.First(value => value.Family == family).Toolkit;
        if (live.UserId != ProviderUser(uid) || live.AuthConfigId != registry.AuthConfig(family)
            || live.Toolkit != expectedToolkit)
            throw new UnauthorizedAccessException("This service account does not belong to your connector profile.");
    }
    private async Task<string> ReadLabelAsync(ProviderConnection live, string family, CancellationToken ct)
    {
        var endpoint = family switch
        {
            "google" or "googleforms" or "youtube" => "https://www.googleapis.com/oauth2/v3/userinfo",
            "github" => "https://api.github.com/user", "slack" => "https://slack.com/api/auth.test",
            "discord" => "https://discord.com/api/v10/users/@me", _ => ""
        };
        try
        {
            var info = await provider.RequestAsync(live.Id, endpoint, family == "slack" ? "POST" : "GET", null, ct);
            var label = new[] { "email", "login", "username", "user", "name", "user_id" }
                .Select(key => ComposioConnectorProvider.Text(info, key)).FirstOrDefault(value => value.Length is > 0 and <= 160);
            if (label is not null && !label.Any(char.IsControl))
            {
                var team = ComposioConnectorProvider.Text(info, "team");
                return family == "slack" && team.Length is > 0 and <= 160 && !team.Any(char.IsControl) ? label + " · " + team : label;
            }
        }
        catch (ConnectorProviderException) { }
        return "Connected account · " + ConnectorIds.Hash(live.Id)[..6];
    }
    internal static string State(ProviderConnection? account) => account is null ? "disconnected"
        : account.Disabled ? "revoked" : account.Status switch
        {
            "ACTIVE" => "connected", "EXPIRED" => "expired", "REVOKED" or "INACTIVE" => "revoked",
            "FAILED" => "reconnect_required", "INITIALIZING" or "INITIATED" => "connecting", _ => "unavailable"
        };
    private static ConnectReply Reply(ConnectorAttempt value) => new(value.Id, value.State, value.RedirectUrl, value.ExpiresAt, value.AccountId);
}
