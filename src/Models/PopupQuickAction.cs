namespace KeyboardWtf.Models;

internal enum PopupQuickActionKind { Hidden, Mute, Unmute, Done }

// The displayed action belongs to one exact session/capture. A mouse press
// retains this immutable value; it never looks up a newer owner on release.
internal sealed record PopupQuickAction(PopupQuickActionKind Kind, string Owner, long Revision)
{
    internal static PopupQuickAction Hidden { get; } = new(PopupQuickActionKind.Hidden, "", 0);
}

internal sealed class PopupQuickActionPresentation
{
    internal PopupQuickAction Select(PopupQuickAction action, VoiceUiPhase phase,
        bool connectorActivity, double inputLevelDb, long nowMilliseconds)
    {
        // The existing service supplies an exact active session/capture owner.
        // Its control stays reachable through speech, silence and task phases;
        // input-meter fluctuations must not make the control disappear.
        return action ?? PopupQuickAction.Hidden;
    }
}
