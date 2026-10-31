namespace KeyboardWtf.Services.Ai;

using KeyboardWtf.Destinations;
using KeyboardWtf.Models;

/// <summary>
/// Unified "can this destination actually send right now?" check. Combines:
/// <list type="bullet">
///   <item>Destination-specific config (webhook URL present, etc.) via <see cref="IDestination.IsAvailable"/>.</item>
///   <item>AI layer readiness for AI-category destinations (selected provider implemented + key present).</item>
/// </list>
/// Called by <c>SendAction</c>, <c>QuickSendActionBase</c>, and <c>AiDestinationDial</c> so
/// "disabled when key missing" behavior is consistent across every touchpoint.
/// </summary>
internal static class DestinationReadiness
{
    public readonly struct Result
    {
        public bool Ready { get; }
        public string Reason { get; }
        public string ShortLabel { get; }

        public Result(bool ready, string reason, string shortLabel)
        {
            Ready = ready;
            Reason = reason;
            ShortLabel = shortLabel;
        }
    }

    public static Result Check(IDestination dest)
    {
        if (dest == null)
            return new Result(false, "No destination", "n/a");

        if (!dest.IsAvailable)
        {
            // Local app fallback: let the send proceed even without webhook/API config.
            // SendAsync handles the actual fallback (copy to clipboard + open app).
            if (dest.IsFallbackAvailable)
                return new Result(true, string.Empty, string.Empty);

            return new Result(false, $"{dest.Name} not configured", NotConfiguredLabel(dest));
        }

        if (dest.Category == DestinationCategory.Ai)
        {
            var provider = AiProviderRegistry.Current;
            if (!provider.IsImplemented)
                return new Result(false, $"{provider.DisplayName} coming soon", "Coming soon");
            if (!provider.IsAvailable)
                return new Result(false, $"{provider.DisplayName} API key missing", "No AI key");
        }

        return new Result(true, string.Empty, string.Empty);
    }

    private static string NotConfiguredLabel(IDestination dest)
    {
        return dest.Name switch
        {
            "Slack" or "Discord" or "Teams" => "No webhook",
            "Email" or "Calendar" => "Not set up",
            _ => "Not set up"
        };
    }

    // Label shown in the Send folder before the user taps (pre-flight).
    // When the fallback is available (app installed, no webhook), show a hint
    // that Slack will open as a fallback rather than blocking.
    public static string PreflightLabel(IDestination dest)
    {
        if (dest.IsAvailable) return string.Empty;
        if (dest.IsFallbackAvailable) return "Open app";
        return NotConfiguredLabel(dest);
    }

    public static Result CheckAiProviderOnly()
    {
        var provider = AiProviderRegistry.Current;
        if (!provider.IsImplemented)
            return new Result(false, $"{provider.DisplayName} coming soon", "Coming soon");
        if (!provider.IsAvailable)
            return new Result(false, $"{provider.DisplayName} API key missing", "No AI key");
        return new Result(true, string.Empty, string.Empty);
    }
}
