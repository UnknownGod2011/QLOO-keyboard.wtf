namespace KeyboardWtf.Services;

using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;

public sealed partial class WebSettingsService
{
    private readonly object _lumaServiceLock = new();
    private LumaLocalTokenService _lumaTokenService;
    // These names are deliberately explicit. Do not map request paths to files,
    // arbitrary assembly resources, the app profile, or developer directories.
    private static readonly IReadOnlyDictionary<string, string> LumaResourceTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["luma.js"] = "text/javascript; charset=utf-8",
            ["luma-site.js"] = "text/javascript; charset=utf-8",
            ["panel-placement.js"] = "text/javascript; charset=utf-8",
            ["luma-live.js"] = "text/javascript; charset=utf-8",
            ["luma-live-worklet.js"] = "text/javascript; charset=utf-8",
            ["product-guide.js"] = "text/javascript; charset=utf-8",
            ["crystal-growth.js"] = "text/javascript; charset=utf-8",
            ["crystal-dance.js"] = "text/javascript; charset=utf-8",
            ["crystal-trail.js"] = "text/javascript; charset=utf-8",
            ["wind-snow.js"] = "text/javascript; charset=utf-8",
            ["winter-hat.js"] = "text/javascript; charset=utf-8",
            ["winter-audio.js"] = "text/javascript; charset=utf-8",
            ["theme-bootstrap.js"] = "text/javascript; charset=utf-8",
            ["settings-adapter.js"] = "text/javascript; charset=utf-8",
            ["settings-theme.css"] = "text/css; charset=utf-8",
            ["luma-ui.css"] = "text/css; charset=utf-8",
            ["assets/spritesheet.webp"] = "image/webp",
            ["assets/santa-hat-realistic.png"] = "image/png",
            ["assets/HAT-GENERATION.md"] = "text/plain; charset=utf-8",
            ["licenses/react-bits.md"] = "text/plain; charset=utf-8",
        };

    internal static bool TryResolveLumaResource(string path, out string resourceName, out string contentType)
    {
        resourceName = null;
        contentType = null;
        const string prefix = "/luma/";
        if (path is null || path.Length > 160 || !path.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var relative = path[prefix.Length..];
        if (!LumaResourceTypes.TryGetValue(relative, out contentType))
            return false;
        resourceName = "web/luma/" + relative;
        return true;
    }

    private static async Task ServeLumaResource(HttpListenerResponse response, string path)
    {
        if (!TryResolveLumaResource(path, out var resourceName, out var contentType))
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }
        await using var stream = typeof(WebSettingsService).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }
        response.ContentType = contentType;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(response.OutputStream);
        response.Close();
    }

    private async Task ServeLumaContext(HttpListenerRequest request, HttpListenerResponse response)
    {
        // HandleRequest has already enforced the exact loopback listener. The
        // process-specific header also prevents ambient cross-origin reads.
        if (!HasSettingsActionHeader(request)
            || !IsSameSettingsOrigin(request.Headers["Origin"], Port)
            || !IsSameSettingsReferer(request.Headers["Referer"], Port)
            || !IsSameOriginFetch(request.Headers["Sec-Fetch-Site"]))
        {
            response.StatusCode = 403;
            await WriteJson(response, new { ok = false, code = "settings_session_expired", error = "Open Luma from the current Settings page." });
            return;
        }
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        await WriteJson(response, BuildLumaSettingsContext(
            request.QueryString["section"], _hotkeys?.ActiveRegistrations, _app?.LumaNativeVoiceBusy != false));
    }

    private async Task ServeLumaLiveToken(HttpListenerRequest request, HttpListenerResponse response)
    {
        // The existing POST authorization + finite update admission already ran.
        // This smaller endpoint accepts one section enum, never model/system
        // instructions, state blobs, key IDs or a browser-supplied credential.
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        if (!request.HasEntityBody || request.ContentLength64 is <= 0 or > 1024)
        {
            response.StatusCode = 400;
            await WriteJson(response, new { available = false, message = "Choose voice from the current Luma Settings controls." });
            return;
        }
        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var document = JsonDocument.Parse(body);
            if (!TryReadLumaTokenSection(document.RootElement, out var section))
            {
                response.StatusCode = 400;
                await WriteJson(response, new { available = false, message = "Luma accepts only the current Settings section." });
                return;
            }
            LumaLocalTokenService service;
            lock (_lumaServiceLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                service = _lumaTokenService ??= new LumaLocalTokenService(
                    new PersonalApiVault(Path.Combine(SettingsService.AppDataDir, "luma-personal-api-v1.dat")),
                    () => _app?.LumaNativeVoiceBusy != false);
            }
            var grant = await service.IssueAsync(LumaHelpTopicForSettingsSection(section),
                _cts?.Token ?? CancellationToken.None, ProjectLumaActiveHotkeys(_hotkeys?.ActiveRegistrations));
            await WriteJson(response, new
            {
                available = true,
                token = grant.Token,
                model = grant.Model,
                expiresAt = grant.ExpiresAt,
                newSessionExpiresAt = grant.NewSessionExpiresAt,
                sessionSeconds = grant.SessionSeconds,
                websocketUrl = grant.WebSocketUrl,
            });
        }
        catch (JsonException)
        {
            response.StatusCode = 400;
            await WriteJson(response, new { available = false, message = "Luma accepts only the current Settings section." });
        }
        catch (LumaLiveUnavailableException exception)
        {
            response.StatusCode = 503;
            await WriteJson(response, new { available = false, message = exception.Message });
        }
        catch (Exception)
        {
            // Do not log a credential, token or provider response via an exception.
            response.StatusCode = 503;
            await WriteJson(response, new { available = false, message = "Luma voice is temporarily unavailable. The tour and text help still work." });
        }
    }

    internal static bool TryReadLumaTokenSection(JsonElement root, out string section)
    {
        section = null;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty("section", out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        var candidate = value.GetString();
        if (candidate is null || candidate.Length > 40
            || (candidate != "settings" && NormalizeLumaSettingsSection(candidate) != candidate))
            return false;
        section = candidate;
        return true;
    }

    internal static string LumaHelpTopicForSettingsSection(string section) => section switch
    {
        "getting-started" => "installation",
        "voice-capture" or "speech-models" or "voice-notes" => "dictation",
        "account-billing" => "account",
        "hotkeys" => "hotkeys",
        "browser-automation" or "destinations" => "automation",
        "support-maintenance" => "support",
        "set-api-key" => "personal",
        "ai" => "writing",
        "assistant" or "how-to-use" or "roadmap" => "overview",
        _ => "settings",
    };

    private void DisposeLumaService()
    {
        lock (_lumaServiceLock)
        {
            _lumaTokenService?.Dispose();
            _lumaTokenService = null;
        }
    }

    internal static string NormalizeLumaSettingsSection(string section) => section switch
    {
        "getting-started" or "voice-capture" or "account-billing" or "hotkeys"
            or "assistant" or "browser-automation" or "how-to-use" or "support-maintenance"
            or "set-api-key" or "speech-models" or "tts" or "voice-notes"
            or "ai" or "destinations" or "roadmap" => section,
        _ => "settings",
    };

    internal static object BuildLumaSettingsContext(string section, IReadOnlyDictionary<string, string> activeHotkeys,
        bool nativeVoiceBusy = true)
    {
        // Never serialize Current, account, credentials, saved items, transcripts
        // or arbitrary DOM text here. Luma can explain controls, not operate them.
        return new
        {
            ok = true,
            surface = "settings",
            section = NormalizeLumaSettingsSection(section),
            featureLabels = new[] { "Dictation", "Smart Writing", "Jarvis", "Screen understanding", "Selected text", "Chrome Helper", "Personal API", "Managed usage", "Support" },
            activeHotkeys = ProjectLumaActiveHotkeys(activeHotkeys),
            capabilities = new[] { "Explain Settings controls", "Show a skippable product tour" },
            nativeVoiceBusy,
        };
    }

    internal static IReadOnlyDictionary<string, string> ProjectLumaActiveHotkeys(IReadOnlyDictionary<string, string> activeHotkeys)
    {
        var safeHotkeys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (activeHotkeys is not null)
        {
            foreach (var name in new[] { "SmartWriting", "Dictation", "Jarvis", "Cancel", "OpenSettings" })
            {
                activeHotkeys.TryGetValue(name, out var value);
                // PushToTalk is the configured record name; the current service
                // registers SmartWriting. Accept either without inventing defaults.
                if (value is null && name == "SmartWriting") activeHotkeys.TryGetValue("PushToTalk", out value);
                if (value is { Length: > 0 and <= 48 }
                    && HotkeyService.TryParse(value, out _, out _))
                    safeHotkeys[name] = value;
            }
        }
        return safeHotkeys;
    }
}
