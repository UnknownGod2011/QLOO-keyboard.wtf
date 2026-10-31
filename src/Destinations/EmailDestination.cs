namespace KeyboardWtf.Destinations;

using System.Diagnostics;
using System.Text;
using KeyboardWtf.Models;

public class EmailDestination : IDestination
{
    public string Name => "Email";
    public string Description => "Open default email client with formatted text";
    public bool IsAvailable => true;
    public DestinationCategory Category => DestinationCategory.Ai;
    public string AiPrompt =>
        "Format this speech transcript as a professional email with headers.\n" +
        "First line: 'To: <recipient email>' (leave empty after colon if no address is given; put the bare name if only a name was spoken).\n" +
        "Second line: 'Subject: <inferred subject>'.\n" +
        "Optional third line: 'Cc: <email>' only if the user explicitly mentions a CC.\n" +
        "Then a blank line, then the email body.\n" +
        "Keep the body professional and concise. Output only the email with these headers, nothing else.";

    public async Task<bool> SendAsync(string text)
    {
        try
        {
            var (to, cc, subject, body) = ParseEmailFields(text);
            var mailto = BuildMailtoUrl(to, cc, subject, body);

            Process.Start(new ProcessStartInfo
            {
                FileName = mailto,
                UseShellExecute = true,
            });

            AppLog.Info($"Opened email client: recipientPresent={!string.IsNullOrEmpty(to)} ccPresent={!string.IsNullOrEmpty(cc)} subjectCharacters={subject.Length} bodyCharacters={body.Length}");
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Email destination failed");
            return false;
        }
    }

    /// <summary>
    /// Parses AI-formatted email output with optional To:/Cc:/Subject: headers followed by a blank line and body.
    /// If no headers are found, the whole input is treated as body.
    /// </summary>
    private static (string to, string cc, string subject, string body) ParseEmailFields(string text)
    {
        string to = "", cc = "", subject = "";
        var lines = text.Split('\n');

        int headerLines = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                headerLines = i + 1; // include the blank separator
                break;
            }
            if (line.StartsWith("To:", StringComparison.OrdinalIgnoreCase))
            {
                to = line.Substring(3).Trim();
                headerLines = i + 1;
            }
            else if (line.StartsWith("Cc:", StringComparison.OrdinalIgnoreCase))
            {
                cc = line.Substring(3).Trim();
                headerLines = i + 1;
            }
            else if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            {
                subject = line.Substring(8).Trim();
                headerLines = i + 1;
            }
            else
            {
                // Non-header encountered before a blank line — treat the rest as body,
                // and whatever headers we parsed already stand.
                break;
            }
        }

        var body = headerLines >= lines.Length
            ? ""
            : string.Join("\n", lines, headerLines, lines.Length - headerLines);
        return (to, cc, subject, body);
    }

    private static string BuildMailtoUrl(string to, string cc, string subject, string body)
    {
        var sb = new StringBuilder("mailto:");
        if (!string.IsNullOrEmpty(to))
            sb.Append(Uri.EscapeDataString(to));

        var query = new List<string>();
        if (!string.IsNullOrEmpty(cc)) query.Add($"cc={Uri.EscapeDataString(cc)}");
        if (!string.IsNullOrEmpty(subject)) query.Add($"subject={Uri.EscapeDataString(subject)}");
        if (!string.IsNullOrEmpty(body)) query.Add($"body={Uri.EscapeDataString(body)}");

        if (query.Count > 0)
        {
            sb.Append('?');
            sb.Append(string.Join('&', query));
        }
        return sb.ToString();
    }
}
