namespace KeyboardWtf.Helpers;

using System.Text.RegularExpressions;

public static class FillerWordFilter
{
    // Deliberately conservative — only sounds that are never real words in EN or DE.
    // "er" is borderline (German pronoun) but kept because it's far more common as a filler.
    // RegexOptions.Compiled: the regex is built once at class init and reused for every transcript.
    // Future: also strip Whisper hallucinations like "Thank you for watching." or "[BLANK_AUDIO]"
    // that appear on very short or silent recordings.
    private static readonly Regex Pattern = new(
        @"\b(uh+|um+|hmm+|hm+|er|erm|äh+|ähm+)\b,?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@" {2,}", RegexOptions.Compiled);

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = Pattern.Replace(text, " ");
        cleaned = MultiSpace.Replace(cleaned, " ").Trim();

        // Restore sentence-initial capital if the original had one
        if (text.Length > 0 && char.IsUpper(text[0]) && cleaned.Length > 0)
            cleaned = char.ToUpper(cleaned[0]) + cleaned[1..];

        return cleaned;
    }
}
