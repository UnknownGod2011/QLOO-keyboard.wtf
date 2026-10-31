using KeyboardWtf.Staging.Api.Domain;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class AccountDeletionService
{
    internal static readonly TimeSpan RecentAuthenticationWindow = TimeSpan.FromMinutes(5);
    private readonly IAccountStore _store;
    private readonly IIdentityService _identities;
    private readonly IClock _clock;
    private readonly IPaymentProvider _payments;
    private readonly Connectors.ConnectorDeletionCleanup? _connectors;

    public AccountDeletionService(
        IAccountStore store,
        IIdentityService identities,
        IClock clock,
        IPaymentProvider payments,
        Connectors.ConnectorDeletionCleanup? connectors = null)
    {
        _store = store;
        _identities = identities;
        _clock = clock;
        _payments = payments;
        _connectors = connectors;
    }

    public async Task DeleteAsync(
        CallerIdentity caller,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
            throw new ArgumentException("Permanent account deletion must be explicitly confirmed.");
        if (!string.Equals(caller.AuthenticationKind, "firebase", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Account deletion requires a fresh Google sign-in.");
        if (!IsRecentAuthentication(caller.AuthenticatedAt, _clock.UtcNow))
            throw new UnauthorizedAccessException(
                "For your security, sign in with Google again before deleting this account.");

        // Fence new purchases before inspecting recurring mandates. A pre-fence
        // provider creation may still attach; final cleanup rechecks the store.
        var subscriptions = await _store.BeginConfirmedAccountDeletionAsync(caller, cancellationToken);
        await ContinueDeletionAsync(caller.UserId, subscriptions, cancellationToken);
    }

    // Maintenance-only; no HTTP endpoint or authentication fallback. The stored
    // intent certifies the original confirmed fresh Google request, not new consent.
    internal async Task ResumePendingDeletionAsync(string userId, CancellationToken cancellationToken)
    {
        var progress = await _store.GetConfirmedAccountDeletionProgressAsync(userId, cancellationToken);
        if (progress == AccountDeletionProgress.Complete) return;
        if (progress != AccountDeletionProgress.Pending)
            throw new InvalidOperationException("No verified pending account deletion exists.");
        var subscriptions = await _store.BeginAccountDeletionAsync(userId, cancellationToken);
        await ContinueDeletionAsync(userId, subscriptions, cancellationToken);
    }

    private async Task ContinueDeletionAsync(string userId, AccountDeletionSubscriptions subscriptions,
        CancellationToken cancellationToken)
    {
        var unresolved = subscriptions.HasUnresolvedSubscription;
        foreach (var subscription in subscriptions.Pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_payments.IsConfigured || _payments.Name != "razorpay")
                    throw new InvalidOperationException("Subscription cancellation is unavailable.");
                var state = await _payments.InspectSubscriptionCancellationStateAsync(
                    subscription.ProviderSubscriptionId, cancellationToken);
                ValidateSubscriptionBinding(subscription, state);
                if (!IsStopped(state.Status))
                {
                    try
                    {
                        state = await _payments.CancelSubscriptionImmediatelyAsync(
                            subscription.ProviderSubscriptionId, cancellationToken);
                        ValidateSubscriptionBinding(subscription, state);
                        if (!IsStopped(state.Status))
                            throw new InvalidOperationException("The provider did not stop renewal.");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // An ambiguous POST is not retried blindly. Only an
                        // independently bound terminal GET can prove success.
                        state = await _payments.InspectSubscriptionCancellationStateAsync(
                            subscription.ProviderSubscriptionId, cancellationToken);
                        ValidateSubscriptionBinding(subscription, state);
                    }
                }
                if (!IsStopped(state.Status))
                    throw new InvalidOperationException("The provider did not stop renewal.");
                await _store.RecordAccountDeletionSubscriptionStoppedAsync(
                    userId, subscription, state.Status, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Preserve confirmed progress and attempt other known mandates.
                // Do not publish provider payloads or falsely complete deletion.
                unresolved = true;
            }
        }
        if (unresolved)
            throw new InvalidOperationException(
                "Account deletion is pending because subscription cancellation could not be confirmed. " +
                "Retry shortly; if it persists, contact support. No deletion success has been recorded.");

        // Operational data is pseudonymized only after nonrenewal is proven.
        // The caller still has a verifiable identity. Financial ledger, purchases and usage metadata are
        // retained under the pseudonymous UID for accounting/audit obligations.
        if (_connectors is not null) await _connectors.CleanupAsync(userId, cancellationToken);
        await _store.DeleteAccountOperationalDataAsync(userId, cancellationToken);
        await _identities.DeleteFirebaseUserAsync(userId, cancellationToken);
        // Final flags/audit may be committed ONLY after operational cleanup and
        // exact-UID identity deletion/absence have both been acknowledged.
        await _store.CompleteAccountDeletionAsync(userId, cancellationToken);
    }

    internal static bool IsStopped(string status) => status is "cancelled" or "completed" or "expired";

    private static void ValidateSubscriptionBinding(
        AccountDeletionSubscription expected, ProviderSubscriptionCancellation actual)
    {
        if (!string.Equals(actual.Id, expected.ProviderSubscriptionId, StringComparison.Ordinal)
            || !string.Equals(actual.PurchaseId, expected.PurchaseId, StringComparison.Ordinal)
            || !string.Equals(actual.ProductSku, expected.ProductSku, StringComparison.Ordinal)
            || !string.Equals(actual.Environment, expected.Environment, StringComparison.Ordinal))
            throw new InvalidOperationException("The subscription ownership could not be confirmed.");
    }

    internal static bool IsRecentAuthentication(
        DateTimeOffset? authenticatedAt,
        DateTimeOffset now) =>
        authenticatedAt is DateTimeOffset value
        && value <= now.AddMinutes(1)
        && now - value <= RecentAuthenticationWindow;
}
