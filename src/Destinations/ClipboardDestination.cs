namespace KeyboardWtf.Destinations;

using System.Runtime.Versioning;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;

[SupportedOSPlatform("windows")]
public class ClipboardDestination : IDestination
{
    public string Name => "Clipboard";
    public string Description => "Copy voice to clipboard";
    public bool IsAvailable => true;
    public DestinationCategory Category => DestinationCategory.Raw;
    public string AiPrompt => null;

    public Task<bool> SendAsync(string text)
    {
        try
        {
            var success = ClipboardHelper.SetText(text);
            if (success)
                AppLog.Info($"Copied {text.Length} chars to clipboard");
            else
                AppLog.Error("Clipboard: SetText returned false");
            return Task.FromResult(success);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Clipboard failed");
            return Task.FromResult(false);
        }
    }
}
