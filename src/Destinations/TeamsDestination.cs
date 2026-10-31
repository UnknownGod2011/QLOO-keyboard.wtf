namespace KeyboardWtf.Destinations;

using KeyboardWtf.Models;

public class TeamsDestination : IDestination
{
    public string Name => "Teams";
    public string Description => "Post to Microsoft Teams via Incoming Webhook";
    public bool IsAvailable => !string.IsNullOrEmpty(KeyboardWtfState.TeamsWebhookUrl);
    public DestinationCategory Category => DestinationCategory.Ai;
    public string AiPrompt => "Format this speech as a Microsoft Teams message. Use clear paragraphs. Keep it professional but conversational. Output only the message text.";

    public Task<bool> SendAsync(string text)
    {
        // TODO: HTTP POST Adaptive Card JSON to KeyboardWtfState.TeamsWebhookUrl
        AppLog.Info($"Teams: stub - would post to webhook: {text.Length} chars");
        return Task.FromResult(false);
    }
}
