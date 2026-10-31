namespace KeyboardWtf.Destinations;

using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;

[SupportedOSPlatform("windows")]
public class SlackWebhookDestination : IDestination
{
    private static readonly HttpClient Http = new();

    public string Name => "Slack";
    public string Description => "Send to Slack via webhook";
    public DestinationCategory Category => DestinationCategory.Ai;
    public string AiPrompt => "Format this speech transcript as a concise Slack message. Use Slack markdown: *bold*, _italic_, `code`. Keep it casual but professional. Output only the message text, nothing else.";

    public bool IsAvailable => !string.IsNullOrEmpty(KeyboardWtfState.SlackWebhookUrl);
    public bool IsFallbackAvailable => IsSlackInstalled();

    public async Task<bool> SendAsync(string text)
    {
        if (!string.IsNullOrEmpty(KeyboardWtfState.SlackWebhookUrl))
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { text });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await Http.PostAsync(KeyboardWtfState.SlackWebhookUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    AppLog.Info("Sent to Slack successfully");
                    return true;
                }

                AppLog.Error($"Slack webhook returned {(int)response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Slack send failed");
                return false;
            }
        }

        // Fallback: Slack app is installed but no webhook configured.
        // Copy text to clipboard and open Slack so the user can paste.
        if (!IsSlackInstalled())
        {
            AppLog.Error("Slack: no webhook configured and app not installed");
            return false;
        }

        ClipboardHelper.SetText(text);
        Process.Start(new ProcessStartInfo { FileName = "slack://", UseShellExecute = true });
        AppLog.Info("Slack: opened app via slack:// (text copied to clipboard)");
        KeyboardWtfState.LastSendResult = "Pasted in Slack";
        return true;
    }

    private static bool IsSlackInstalled()
    {
        // Check if slack:// protocol handler is registered - most reliable regardless of install path
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("slack");
            if (key != null) return true;
        }
        catch { }

        // Fallback: check common install paths
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] paths =
        [
            Path.Combine(local, "slack", "slack.exe"),
            Path.Combine(local, "Programs", "slack", "Slack.exe"),
            @"C:\Program Files\Slack\Slack.exe",
            @"C:\Program Files (x86)\Slack\Slack.exe",
        ];
        return paths.Any(File.Exists);
    }
}
