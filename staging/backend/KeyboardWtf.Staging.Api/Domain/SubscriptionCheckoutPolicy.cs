namespace KeyboardWtf.Staging.Api.Domain;

internal static class SubscriptionCheckoutPolicy
{
    internal static bool CanStart(string? status) =>
        (status ?? "none").Trim().ToLowerInvariant() is
            "" or "none" or "cancelled" or "completed" or "expired" or "failed";

    // Halted/paused subscriptions can resume; local checkout cancellation or
    // request failure says nothing about whether a remote subscription exists.
    internal static bool IsVerifiedTerminalEvent(string eventType, string status) =>
        status is "cancelled" or "completed" or "expired"
        && string.Equals(eventType, "subscription." + status, StringComparison.Ordinal);
}
