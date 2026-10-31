namespace KeyboardWtf.Services;

using System.Text.RegularExpressions;

public enum ParsedCommandKind
{
    Dictation,
    Copy,
    TypeOut,
    SendTo,
    MakeProfessional,
    MakeCasual,
    MakeShorter,
    Summarize,
    BulletList,
    FixGrammar,
    Email,
    TranslateGerman,
    SaveNote,
    ReadBack,
    ReplayLast,
    Cancel,
    OpenSettings,
    OpenNotepad,
    OpenUrl,
    GmailDraft,
    SwitchWhisper,
    SwitchVosk,
    EnableAi,
    DisableAi
}

public sealed record ParsedVoiceCommand(ParsedCommandKind Kind, string Argument = null, string Content = null);

public static class VoiceCommandParser
{
    public static ParsedVoiceCommand Parse(string transcript)
    {
        var text = (transcript ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(lower))
            return new ParsedVoiceCommand(ParsedCommandKind.Dictation);
        if (ContainsAny(lower, "cancel", "stop operation", "never mind"))
            return new ParsedVoiceCommand(ParsedCommandKind.Cancel);
        if (ContainsAny(lower, "open settings", "show settings"))
            return new ParsedVoiceCommand(ParsedCommandKind.OpenSettings);
        if (ContainsAny(lower, "open notepad", "launch notepad"))
            return new ParsedVoiceCommand(ParsedCommandKind.OpenNotepad);
        if (TryParseGmailDraft(text, lower, out var gmailDraft))
            return gmailDraft;
        if (TryParseOpenUrl(text, lower, out var openUrl))
            return openUrl;
        if (ContainsAny(lower, "read back", "read that back"))
            return new ParsedVoiceCommand(ParsedCommandKind.ReadBack);
        if (ContainsAny(lower, "replay last", "repeat last"))
            return new ParsedVoiceCommand(ParsedCommandKind.ReplayLast);
        if (lower.StartsWith("copy ", StringComparison.Ordinal) && !ContainsAny(lower, "copy this", "copy that", "copy it"))
            return new ParsedVoiceCommand(ParsedCommandKind.Copy, Content: text[5..].Trim());
        if (ContainsAny(lower, "copy this", "copy that", "copy it"))
            return new ParsedVoiceCommand(ParsedCommandKind.Copy, Content: ExtractTrailingContent(text));
        if (lower.StartsWith("type ", StringComparison.Ordinal) && !ContainsAny(lower, "type this", "type that", "type it", "type this out"))
            return new ParsedVoiceCommand(ParsedCommandKind.TypeOut, Content: text[5..].Trim());
        if (ContainsAny(lower, "type this", "type that", "type it", "type this out"))
            return new ParsedVoiceCommand(ParsedCommandKind.TypeOut, Content: ExtractTrailingContent(text));
        if (ContainsAny(lower, "save note", "save this note", "voice note"))
            return new ParsedVoiceCommand(ParsedCommandKind.SaveNote);
        if (ContainsAny(lower, "make this professional", "make this sound professional", "make it professional")
            || lower.StartsWith("write a professional message", StringComparison.Ordinal))
            return new ParsedVoiceCommand(
                ParsedCommandKind.MakeProfessional,
                Content: ExtractAfterAny(text, lower, " saying ", " that says ") ?? ExtractTrailingContent(text));
        if (ContainsAny(lower, "make this casual", "make it casual"))
            return new ParsedVoiceCommand(ParsedCommandKind.MakeCasual);
        if (ContainsAny(lower, "make this shorter", "make it shorter", "shorten this"))
            return new ParsedVoiceCommand(ParsedCommandKind.MakeShorter);
        if (ContainsAny(lower, "summarize this", "summarize that", "summarize it"))
            return new ParsedVoiceCommand(ParsedCommandKind.Summarize);
        if (ContainsAny(lower, "make this bullet points", "turn this into bullet points", "bullet point this"))
            return new ParsedVoiceCommand(ParsedCommandKind.BulletList);
        if (ContainsAny(lower, "fix grammar", "clean this up", "clean up this"))
            return new ParsedVoiceCommand(ParsedCommandKind.FixGrammar);
        if (ContainsAny(lower, "write this as an email", "turn this into an email"))
            return new ParsedVoiceCommand(ParsedCommandKind.Email);
        if (ContainsAny(lower, "translate this to german", "translate to german", "translate that to german"))
            return new ParsedVoiceCommand(ParsedCommandKind.TranslateGerman);
        if (ContainsAny(lower, "switch to whisper", "use whisper"))
            return new ParsedVoiceCommand(ParsedCommandKind.SwitchWhisper);
        if (ContainsAny(lower, "switch to vosk", "use vosk"))
            return new ParsedVoiceCommand(ParsedCommandKind.SwitchVosk);
        if (ContainsAny(lower, "enable ai formatting", "turn on ai formatting", "enable ai"))
            return new ParsedVoiceCommand(ParsedCommandKind.EnableAi);
        if (ContainsAny(lower, "disable ai formatting", "turn off ai formatting", "disable ai"))
            return new ParsedVoiceCommand(ParsedCommandKind.DisableAi);

        foreach (var destination in new[] { "Slack", "Discord", "Teams", "Calendar", "WhatsApp", "Notion", "Telegram", "Email", "Clipboard" })
        {
            var destLower = destination.ToLowerInvariant();
            var patterns = new[]
            {
                $"send to {destLower}",
                $"send this to {destLower}",
                $"send that to {destLower}",
                $"send it to {destLower}",
            };

            foreach (var pattern in patterns)
            {
                var index = lower.IndexOf(pattern, StringComparison.Ordinal);
                if (index < 0)
                    continue;

                var contentStart = index + pattern.Length;
                var content = contentStart < text.Length ? text[contentStart..].Trim(' ', ':', '-', '.') : null;
                return new ParsedVoiceCommand(ParsedCommandKind.SendTo, destination, content);
            }
        }

        return new ParsedVoiceCommand(ParsedCommandKind.Dictation);
    }

    private static bool TryParseGmailDraft(string text, string lower, out ParsedVoiceCommand command)
    {
        command = null;
        if (!ContainsAny(lower, "open gmail", "gmail compose")
            && !(lower.Contains("gmail", StringComparison.Ordinal) && ContainsAny(lower, "create an email", "compose an email", "draft an email")))
            return false;

        var recipient = "";
        var email = Regex.Match(text, @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase);
        if (email.Success)
            recipient = email.Value;

        var body = ExtractAfterAny(text, lower, " saying ", " that says ", " with body ", " message ");
        command = new ParsedVoiceCommand(ParsedCommandKind.GmailDraft, recipient, body);
        return true;
    }

    private static bool TryParseOpenUrl(string text, string lower, out ParsedVoiceCommand command)
    {
        command = null;
        if (!lower.StartsWith("open ", StringComparison.Ordinal))
            return false;

        var target = text[5..].Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        command = new ParsedVoiceCommand(ParsedCommandKind.OpenUrl, uri.ToString());
        return true;
    }

    private static string ExtractAfterAny(string original, string lower, params string[] markers)
    {
        foreach (var marker in markers)
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            return original[(idx + marker.Length)..].Trim(' ', '.', '"', '\'');
        }

        return null;
    }

    private static string ExtractTrailingContent(string text)
    {
        var punctuation = text.IndexOfAny(['.', ':', '-']);
        if (punctuation < 0 || punctuation + 1 >= text.Length)
            return null;

        var content = text[(punctuation + 1)..].Trim(' ', '.', ':', '-', '"', '\'');
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));
}
