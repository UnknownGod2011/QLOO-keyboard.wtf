namespace KeyboardWtf.Services;

using System.Text.RegularExpressions;

/// <summary>
/// A single, transient follow-up to a genuinely ambiguous folder result. The
/// search receipt alone never authorizes opening every candidate; only the
/// user's next explicit plural instruction can consume this exact path set.
/// </summary>
internal sealed class FolderChoiceContinuation
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private static readonly string[] PluralCommands =
    [
        "open both", "open all", "open both folders", "open all folders",
        "open them both", "open all of them", "please open both",
        "please open all", "please open both folders", "please open all folders",
    ];

    private readonly object _sync = new();
    private string[] _paths = [];
    private DateTimeOffset _capturedAt;
    private bool _awaitingNextTurn;

    internal int Remember(FileSearchResult search)
    {
        var paths = StrongDistinctPaths(search);
        lock (_sync)
        {
            _paths = paths;
            _capturedAt = DateTimeOffset.UtcNow;
            _awaitingNextTurn = false;
        }
        return paths.Length;
    }

    internal void CompleteUserTurn()
    {
        lock (_sync)
            if (_paths.Length >= 2)
                _awaitingNextTurn = true;
    }

    internal void ObserveUserTurn(string text)
    {
        var normalized = NormalizeTurn(text);
        if (normalized.Length == 0)
            return;
        lock (_sync)
        {
            if (!_awaitingNextTurn || _paths.Length == 0)
                return; // Late fragments of the originating turn are not a new choice.
            if (DateTimeOffset.UtcNow - _capturedAt > Lifetime
                || !PluralCommands.Any(command => command.StartsWith(normalized, StringComparison.Ordinal)))
                ClearLocked();
        }
    }

    internal bool TryConsume(string text, out string[] paths)
    {
        paths = [];
        var normalized = NormalizeTurn(text);
        if (!PluralCommands.Contains(normalized, StringComparer.Ordinal))
            return false;
        lock (_sync)
        {
            if (!_awaitingNextTurn || _paths.Length < 2
                || DateTimeOffset.UtcNow - _capturedAt > Lifetime)
            {
                ClearLocked();
                return false;
            }
            paths = _paths;
            ClearLocked(); // Consume before any OS effect, including cancellation.
            return true;
        }
    }

    internal void Clear()
    {
        lock (_sync)
            ClearLocked();
    }

    internal static string[] StrongDistinctPaths(FileSearchResult search)
    {
        if (search?.Status != FileResolutionStatus.Ambiguous
            || search.Best is not { IsDirectory: true, NameMatchRank: >= 3 } best)
            return [];

        return search.Candidates
            .Where(candidate => candidate.IsDirectory
                && candidate.NameMatchRank == best.NameMatchRank
                && candidate.Depth == best.Depth
                && Math.Abs(candidate.Score - best.Score) < 0.08
                && Directory.Exists(candidate.Path))
            .Select(candidate => NormalizeExistingPath(candidate.Path))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeExistingPath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static string NormalizeTurn(string text) => Regex.Replace(
        (text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private void ClearLocked()
    {
        _paths = [];
        _capturedAt = default;
        _awaitingNextTurn = false;
    }
}
