namespace KeyboardWtf.Destinations;

using System.Diagnostics;
using System.Runtime.Versioning;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;

[SupportedOSPlatform("windows")]
public class NotionDestination : IDestination
{
    public string Name => "Notion";
    public string Description => "Send to Notion (copies formatted text + opens app)";
    public DestinationCategory Category => DestinationCategory.Ai;
    public bool IsAvailable => false;
    public bool IsFallbackAvailable => true;

    public string AiPrompt =>
        "Format this speech transcript as a clean Notion page block. " +
        "Use clear headings and bullet points where appropriate. " +
        "Output only the formatted content, no preamble.";

    public Task<bool> SendAsync(string text)
    {
        ClipboardHelper.SetText(text);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "notion://", UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo { FileName = "https://notion.so", UseShellExecute = true });
        }
        KeyboardWtfState.LastSendResult = "Paste in Notion";
        AppLog.Info("Notion: text copied, app opened");
        return Task.FromResult(true);
    }
}
