namespace KeyboardWtf.Services;

using System.Text.RegularExpressions;

// Ephemeral original sources, not durable memory. The dispatcher owns locking
// and clears this bounded history when the logical conversation ends.
internal sealed class SessionSelectionHistory
{
    internal const int Capacity = 8;
    internal sealed record Snapshot(string Text, string ContextId, string CaptureId,
        string Title, string App, string Origin, DateTimeOffset CreatedUtc);
    private readonly List<Snapshot> _sources = new();

    internal void Clear() => _sources.Clear();
    internal int Count => _sources.Count;

    internal Snapshot Accept(Dictionary<string, object> capture)
    {
        string Read(string key) => capture.GetValueOrDefault(key)?.ToString() ?? "";
        var text = Read("text");
        var context = Read("source_context_id");
        // Re-reading the same unchanged selection does not consume history
        // slots. Different text, even in the same document, remains distinct.
        var existing = _sources.FindIndex(item => context.Length > 0
            && item.ContextId == context && item.Text == text);
        var snapshot = new Snapshot(text, context, Read("capture_id"),
            Read("source_title"), Read("source_app"), Read("source_origin"),
            existing >= 0 ? _sources[existing].CreatedUtc : DateTimeOffset.UtcNow);
        if (existing >= 0) _sources.RemoveAt(existing);
        _sources.Add(snapshot);
        if (_sources.Count > Capacity) _sources.RemoveAt(0);
        return snapshot;
    }

    internal Snapshot Previous => _sources.Count >= 2 ? _sources[^2] : null;

    internal bool TryNamedReference(string request, out Snapshot snapshot)
    {
        snapshot = null;
        if (!IsHistoricalNamedReference(request) || !TryReferenceLabel(request, out var label)) return false;
        var matches = _sources.Where(item => ContainsLabel(item.Title, label)
            || ContainsLabel(item.App, label) || ContainsLabel(item.Origin, label)).ToArray();
        if (matches.Length == 1) snapshot = matches[0];
        return true; // A named but missing/ambiguous reference must not pick another source.
    }

    internal object[] Index() => _sources.Select(item => (object)new
    {
        capture_id = item.CaptureId,
        source_title = item.Title,
        source_app = item.App,
        source_origin = item.Origin,
        characters = item.Text.Length,
        captured_utc = item.CreatedUtc,
    }).ToArray();

    internal static bool TryReferenceLabel(string request, out string label)
        => TryReferenceLabel(request, out label, out _);

    internal static bool IsHistoricalNamedReference(string request)
    {
        if (!TryReferenceLabel(request, out _, out var rawLabel)) return false;
        return !Regex.IsMatch(request ?? "", @"\bthis\s+"
            + @"(?:(?:earlier|previous|prior|original)\s+)?" + Regex.Escape(rawLabel)
            + @"\s+(?:selected\s+text|selection)\b", RegexOptions.IgnoreCase);
    }

    internal static bool TryReferenceLabel(string request, out string label, out string rawLabel)
    {
        label = "";
        rawLabel = "";
        // Overlapping matches let "the deadline in that X selection" identify
        // X without treating the question itself as a document name.
        var matches = Regex.Matches(request ?? "",
            @"(?=\b(?:that|this|the|my|earlier|previous|prior|original)\s+"
            + @"(?:(?:earlier|previous|prior|original)\s+)?(?<label>[\p{L}\p{N}][\p{L}\p{N} ._'’-]{0,90}?)\s+"
            + @"(?:selected\s+text|selection)\b)", RegexOptions.IgnoreCase);
        foreach (Match match in matches.Reverse())
        {
            var candidate = Normalize(match.Groups["label"].Value);
            if (candidate.Length == 0 || Regex.IsMatch(candidate,
                @"\b(?:in|about|from|that|this|the|my|earlier|previous|prior|original|entire|whole|full|complete|fresh|new|newly|different|current|currently|selected|highlighted)\b"))
                continue;
            label = candidate;
            rawLabel = match.Groups["label"].Value;
            return true;
        }
        return false;
    }

    private static bool ContainsLabel(string provenance, string label) =>
        (" " + Normalize(provenance) + " ").Contains(" " + label + " ", StringComparison.Ordinal);

    private static string Normalize(string value) =>
        Regex.Replace((value ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}
