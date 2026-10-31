using System.Text.RegularExpressions;

namespace KeyboardWtf.Services.Updates;

// Syntactic updater path validation only. This does not authenticate a process
// image or pin a filesystem namespace (including mapped drives/8.3 aliases).
internal static class UpdatePathIdentity
{
    internal static void RequireCanonicalLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length is < 4 or > 32767
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.Contains('/') || path.AsSpan(2).Contains(':')
            || path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 || path.Any(char.IsControl))
            throw new InvalidDataException("The prepared update installer path is invalid.");
        foreach (var segment in path[3..].Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".."
                || segment.EndsWith(' ') || segment.EndsWith('.')
                || Regex.IsMatch(segment, @"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:[ ]*\.|\z)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("The prepared update installer path is invalid.");
        }
        if (!string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared update installer path is invalid.");
    }
}
