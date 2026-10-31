namespace KeyboardWtf.Destinations;

using KeyboardWtf.Models;

// Raw destinations (Clipboard, Type Out) always receive the literal transcript — no AI reformatting.
// Ai destinations (Email, Slack, Discord, Teams, Calendar, etc.) get the AI-formatted version.
// The split is by category so Send folder and dials can show the right subset per context.
public static class DestinationRegistry
{
    private static readonly List<IDestination> _all = new();
    private static readonly List<IDestination> _raw = new();
    private static readonly List<IDestination> _ai = new();

    public static IReadOnlyList<IDestination> All => _all;
    public static IReadOnlyList<IDestination> Raw => _raw;
    public static IReadOnlyList<IDestination> Ai => _ai;

    public static int Count => _all.Count;
    public static int RawCount => _raw.Count;
    public static int AiCount => _ai.Count;

    public static void Register(IDestination destination)
    {
        _all.Add(destination);
        if (destination.Category == DestinationCategory.Raw)
            _raw.Add(destination);
        else
            _ai.Add(destination);

        AppLog.Info($"Registered destination: {destination.Name} ({destination.Category})");
    }

    public static IDestination GetRawByIndex(int index)
    {
        if (_raw.Count == 0) return null;
        // Double-modulo keeps index positive when the dial scrolls backwards past zero.
        // C# % can return negative for negative operands; adding count before the second % fixes it.
        var safe = ((index % _raw.Count) + _raw.Count) % _raw.Count;
        return _raw[safe];
    }

    public static IDestination GetAiByIndex(int index)
    {
        if (_ai.Count == 0) return null;
        var safe = ((index % _ai.Count) + _ai.Count) % _ai.Count;
        return _ai[safe];
    }

    /// <summary>
    /// The currently active destination: depends on which dial the user last scrolled.
    /// SendAction / RegenerateAction / PushToTalkAction all route through here.
    /// </summary>
    public static IDestination Current =>
        KeyboardWtfState.ActiveCategory == DestinationCategory.Raw
            ? GetRawByIndex(KeyboardWtfState.SelectedRawIndex)
            : GetAiByIndex(KeyboardWtfState.SelectedAiIndex);
}
