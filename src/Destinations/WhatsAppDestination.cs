namespace KeyboardWtf.Destinations;

using System.Diagnostics;
using System.Runtime.Versioning;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;

[SupportedOSPlatform("windows")]
public class WhatsAppDestination : IDestination
{
    public string Name => "WhatsApp";
    public string Description => "Send to WhatsApp (copies text + opens app)";
    public DestinationCategory Category => DestinationCategory.Ai;
    public bool IsAvailable => false;
    public bool IsFallbackAvailable => true; // always: copy + open

    public string AiPrompt =>
        "Format this speech transcript as a natural WhatsApp message. " +
        "Keep it casual and conversational. No markdown. Output only the message text.";

    public Task<bool> SendAsync(string text)
    {
        ClipboardHelper.SetText(text);
        try
        {
            // WhatsApp Desktop registers the whatsapp:// protocol on install
            Process.Start(new ProcessStartInfo { FileName = "whatsapp://", UseShellExecute = true });
        }
        catch
        {
            // If WhatsApp Desktop isn't installed, open web.whatsapp.com
            Process.Start(new ProcessStartInfo { FileName = "https://web.whatsapp.com", UseShellExecute = true });
        }
        KeyboardWtfState.LastSendResult = "Paste in WhatsApp";
        AppLog.Info("WhatsApp: text copied, app opened");
        return Task.FromResult(true);
    }
}
