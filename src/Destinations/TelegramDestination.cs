namespace KeyboardWtf.Destinations;

using System.Diagnostics;
using System.Runtime.Versioning;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;

[SupportedOSPlatform("windows")]
public class TelegramDestination : IDestination
{
    public string Name => "Telegram";
    public string Description => "Send to Telegram (copies text + opens app)";
    public DestinationCategory Category => DestinationCategory.Ai;
    public bool IsAvailable => false;
    public bool IsFallbackAvailable => true;

    public string AiPrompt =>
        "Format this speech transcript as a natural Telegram message. " +
        "Keep it concise and conversational. No markdown. Output only the message text.";

    public Task<bool> SendAsync(string text)
    {
        ClipboardHelper.SetText(text);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "tg://", UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo { FileName = "https://web.telegram.org", UseShellExecute = true });
        }
        KeyboardWtfState.LastSendResult = "Paste in Telegram";
        AppLog.Info("Telegram: text copied, app opened");
        return Task.FromResult(true);
    }
}
