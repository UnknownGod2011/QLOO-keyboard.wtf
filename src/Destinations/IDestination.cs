namespace KeyboardWtf.Destinations;

using KeyboardWtf.Models;

public interface IDestination
{
    string Name { get; }
    string Description { get; }
    bool IsAvailable { get; }
    DestinationCategory Category { get; }
    Task<bool> SendAsync(string text);

    string AiPrompt => null;

    // IsFallbackAvailable: messaging apps (WhatsApp, Telegram, Notion) appear in the Send folder
    // even without API config because they can always open the app via URI handler and paste from
    // clipboard. Most users are fine with the clipboard bridge; full API integration is a future step.
    bool IsFallbackAvailable => false;

    string IconName => $"mode-{Name.ToLowerInvariant()}.svg";
}
