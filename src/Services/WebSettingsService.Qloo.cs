namespace KeyboardWtf.Services;

using System.Net;
using System.Text.Json;
using System.Windows.Forms;

/// <summary>Settings routes for Qloo cultural intelligence and the taste profile.
/// The Qloo key is entered in a native dialog and never sent to or from the page.</summary>
public sealed partial class WebSettingsService
{
    internal Taste.TasteJarvisTools TasteTools { get; set; }

    private async Task HandleQlooSettings(HttpListenerRequest req, HttpListenerResponse resp, string path)
    {
        var taste = TasteTools;
        if (taste is null)
        {
            resp.StatusCode = 503;
            await WriteJson(resp, new { ok = false, error = "Qloo cultural intelligence is not initialized." });
            return;
        }
        if (path == "/api/qloo/state")
        {
            await WriteJson(resp, new { ok = true, state = QlooState(taste) });
            return;
        }
        if (path != "/api/qloo/action")
        {
            resp.StatusCode = 404;
            await WriteJson(resp, new { ok = false, error = "Unknown Qloo settings route." });
            return;
        }

        JsonDocument document;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
        {
            try { document = JsonDocument.Parse(await reader.ReadToEndAsync()); }
            catch (JsonException)
            {
                resp.StatusCode = 400;
                await WriteJson(resp, new { ok = false, error = "Invalid request." });
                return;
            }
        }
        using var parsed = document;
        var root = parsed.RootElement;
        var action = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : "";
        string message;
        try
        {
            switch (action)
            {
                case "enabled":
                {
                    var enabled = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                    _settings.SaveQlooEnabled(enabled);
                    message = enabled
                        ? "Qloo cultural intelligence is on. It applies from the next Jarvis conversation."
                        : "Qloo cultural intelligence is off. Jarvis behaves exactly like the original from the next conversation.";
                    break;
                }
                case "add-key":
                {
                    var key = await QlooKeyEntryDialog.ShowAsync();
                    if (key is null) { message = "No key was entered."; break; }
                    var credentials = KeyStore(taste);
                    credentials.Save(key);
                    message = "Qloo key saved with Windows encryption. It is used only for https://hackathon.api.qloo.com.";
                    break;
                }
                case "remove-key":
                    message = KeyStore(taste).Remove() ? "Qloo key removed from this PC." : "No stored Qloo key was found.";
                    break;
                case "forget":
                    message = taste.Profile.Forget(QlooJsonString(root, "id")) ? "Preference forgotten." : "Preference not found.";
                    break;
                case "correct":
                {
                    var polarity = QlooJsonString(root, "polarity");
                    var updated = taste.Profile.Correct(QlooJsonString(root, "id"), QlooJsonString(root, "value"),
                        string.IsNullOrWhiteSpace(polarity) ? null : polarity == "dislike" ? -1 : 1,
                        root.TryGetProperty("context", out _) ? QlooJsonString(root, "context") ?? "" : null);
                    message = updated is null ? "Preference not found or not allowed." : "Preference corrected.";
                    break;
                }
                case "clear-profile":
                    taste.Profile.Clear();
                    message = "Taste memory cleared.";
                    break;
                case "clear-cache":
                    taste.Qloo?.ClearCache();
                    message = "Private Qloo cache cleared.";
                    break;
                default:
                    resp.StatusCode = 400;
                    await WriteJson(resp, new { ok = false, error = "Unsupported Qloo action." });
                    return;
            }
        }
        catch (ArgumentException ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyboardWtf.Helpers.SecretPersistenceException)
        {
            resp.StatusCode = 500;
            await WriteJson(resp, new { ok = false, code = "settings_storage_failed", error = "The change could not be saved on this PC." });
            return;
        }
        await WriteJson(resp, new { ok = true, message, state = QlooState(taste) });
    }

    private object QlooState(Taste.TasteJarvisTools taste)
    {
        var credentials = KeyStore(taste);
        var usage = taste.Qloo?.Usage();
        return new
        {
            enabled = _settings?.Current.QlooEnabled ?? true,
            configured = credentials?.IsConfigured ?? false,
            keySource = credentials?.Source ?? "none",
            mask = credentials?.Mask() ?? "",
            lockedOut = taste.Qloo?.IsLockedOut ?? false,
            baseUrl = "https://hackathon.api.qloo.com",
            usage = usage is null ? null : new
            {
                day = usage.Day,
                requests = usage.Requests,
                cacheHits = usage.CacheHits,
                failures = usage.Failures,
                dailyBudget = usage.DailyBudget,
                lastError = usage.LastError,
            },
            cachedResponses = taste.Qloo?.CachedEntryCount ?? 0,
            preferences = taste.Profile.Snapshot().Take(120).Select(p => new
            {
                id = p.Id,
                domain = p.Domain,
                attribute = p.Attribute,
                value = p.Value,
                context = p.Context,
                kind = p.Kind,
                polarity = p.Polarity,
                strength = p.Strength,
                confidence = p.Confidence,
                evidence = p.Evidence,
                recentlyChanged = p.RecentlyChanged,
                lastQuote = p.LastQuote,
                expiresUtc = p.ExpiresUtc,
            }).ToArray(),
        };
    }

    private static Qloo.QlooCredentialStore KeyStore(Taste.TasteJarvisTools taste) =>
        taste.Qloo?.Credentials;

    private static string QlooJsonString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Native, password-masked Qloo key entry on its own STA thread.</summary>
internal static class QlooKeyEntryDialog
{
    internal static Task<string> ShowAsync()
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form
                {
                    Text = "QLOO - Qloo API key",
                    Width = 500,
                    Height = 250,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterScreen,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    TopMost = true,
                };
                var caption = new Label { Text = "Qloo hackathon API key", Left = 20, Top = 20, AutoSize = true };
                var key = new TextBox { Left = 20, Top = 46, Width = 440, MaxLength = 200, UseSystemPasswordChar = true };
                var note = new Label
                {
                    Left = 20, Top = 82, Width = 440, Height = 60,
                    Text = "Saved only in Windows-encrypted local storage for this user. Sent only in the X-Api-Key header to https://hackathon.api.qloo.com. Never shown in Settings or logs.",
                };
                var ok = new Button { Text = "Save", Left = 280, Top = 150, Width = 85, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 375, Top = 150, Width = 85, DialogResult = DialogResult.Cancel };
                form.Controls.AddRange(new Control[] { caption, key, note, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                var accepted = form.ShowDialog() == DialogResult.OK ? key.Text.Trim() : null;
                result.TrySetResult(string.IsNullOrWhiteSpace(accepted) ? null : accepted);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Qloo key dialog failed");
                result.TrySetResult(null);
            }
        })
        { IsBackground = true, Name = "Qloo key entry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
