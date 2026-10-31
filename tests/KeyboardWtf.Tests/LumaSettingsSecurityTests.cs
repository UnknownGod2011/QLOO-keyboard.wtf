using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class LumaSettingsSecurityTests
{
    [Theory]
    [InlineData("/luma/luma.js", "web/luma/luma.js", "text/javascript; charset=utf-8")]
    [InlineData("/luma/panel-placement.js", "web/luma/panel-placement.js", "text/javascript; charset=utf-8")]
    [InlineData("/luma/settings-adapter.js", "web/luma/settings-adapter.js", "text/javascript; charset=utf-8")]
    [InlineData("/luma/settings-theme.css", "web/luma/settings-theme.css", "text/css; charset=utf-8")]
    [InlineData("/luma/assets/spritesheet.webp", "web/luma/assets/spritesheet.webp", "image/webp")]
    [InlineData("/luma/assets/santa-hat-realistic.png", "web/luma/assets/santa-hat-realistic.png", "image/png")]
    [InlineData("/luma/licenses/react-bits.md", "web/luma/licenses/react-bits.md", "text/plain; charset=utf-8")]
    public void Static_resources_use_only_exact_canonical_names(string path, string resource, string mediaType)
    {
        Assert.True(WebSettingsService.TryResolveLumaResource(path, out var resolved, out var type));
        Assert.Equal(resource, resolved);
        Assert.Equal(mediaType, type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/luma/")]
    [InlineData("/luma/../settings.html")]
    [InlineData("/luma/%2e%2e/settings.html")]
    [InlineData("/luma/%252e%252e/settings.html")]
    [InlineData("/luma/assets/../../settings.json")]
    [InlineData("/luma/assets\\spritesheet.webp")]
    [InlineData("/luma//luma.js")]
    [InlineData("/luma/Luma.js")]
    [InlineData("/LUMA/luma.js")]
    [InlineData("/luma/luma.js?path=settings.json")]
    [InlineData("/luma/luma.js#fragment")]
    [InlineData("/luma/app.js")]
    [InlineData("/luma/server.cjs")]
    [InlineData("C:/Users/Admin/settings.json")]
    public void Unknown_encoded_machine_and_traversal_paths_do_not_resolve(string path)
    {
        Assert.False(WebSettingsService.TryResolveLumaResource(path, out var resource, out var mediaType));
        Assert.Null(resource);
        Assert.Null(mediaType);
    }

    [Fact]
    public void Context_has_an_explicit_small_schema_and_discards_unapproved_state()
    {
        var state = new Dictionary<string, string>
        {
            ["Jarvis"] = "Ctrl+Alt+J",
            ["Cancel"] = "Ctrl+Alt+Escape",
            ["SmartWriting"] = "Ctrl+Alt+K",
            ["Dictation"] = "not-a-real-key",
            ["OpenSettings"] = new string('X', 200),
            ["apiKey"] = "synthetic-secret",
            ["email"] = "private@example.invalid",
            ["savedLink"] = "https://private.invalid",
            ["balance"] = "999",
        };
        var encoded = JsonSerializer.Serialize(WebSettingsService.BuildLumaSettingsContext("hotkeys", state));
        using var document = JsonDocument.Parse(encoded);
        var root = document.RootElement;
        Assert.Equal(new[] { "ok", "surface", "section", "featureLabels", "activeHotkeys", "capabilities", "nativeVoiceBusy" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("settings", root.GetProperty("surface").GetString());
        Assert.Equal("hotkeys", root.GetProperty("section").GetString());
        Assert.Equal(3, root.GetProperty("activeHotkeys").EnumerateObject().Count());
        Assert.Equal("Ctrl+Alt+J", root.GetProperty("activeHotkeys").GetProperty("Jarvis").GetString());
        Assert.DoesNotContain("synthetic-secret", encoded);
        Assert.DoesNotContain("private@example", encoded);
        Assert.DoesNotContain("private.invalid", encoded);
        Assert.DoesNotContain("balance", encoded);
        Assert.DoesNotContain("not-a-real-key", encoded);
        Assert.True(root.GetProperty("nativeVoiceBusy").GetBoolean());
    }

    [Theory]
    [InlineData("hotkeys", "hotkeys")]
    [InlineData("account-billing", "account-billing")]
    [InlineData("assistant", "assistant")]
    [InlineData("set-api-key", "set-api-key")]
    [InlineData(null, "settings")]
    [InlineData("", "settings")]
    [InlineData("https://private.invalid?q=secret", "settings")]
    [InlineData("<script>secret</script>", "settings")]
    [InlineData("../profile", "settings")]
    public void Section_cannot_carry_arbitrary_context(string section, string expected) =>
        Assert.Equal(expected, WebSettingsService.NormalizeLumaSettingsSection(section));

    [Fact]
    public void Context_request_uses_the_existing_settings_session_boundary()
    {
        var script = WebSettingsService.BuildSettingsRequestGuardScript("synthetic-process-action-token");
        Assert.Contains("target.pathname === '/api/luma/context'", script);
        Assert.Contains("X-Keyboard-Wtf-Settings-Action", script);
        Assert.Contains("target.origin === window.location.origin", script);
        Assert.DoesNotContain("luma/context?key", script);
    }

    [Theory]
    [InlineData("{\"section\":\"hotkeys\"}", true)]
    [InlineData("{\"section\":\"settings\"}", true)]
    [InlineData("{\"section\":\"assistant\",\"systemInstruction\":\"override\"}", false)]
    [InlineData("{\"section\":\"assistant\",\"model\":\"arbitrary\"}", false)]
    [InlineData("{\"section\":\"assistant\",\"keyId\":\"arbitrary\"}", false)]
    [InlineData("{\"section\":\"https://private.invalid\"}", false)]
    [InlineData("{\"section\":null}", false)]
    [InlineData("{\"section\":42}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("{\"section\":\"hotkeys\",\"section\":\"assistant\"}", false)]
    public void Live_grant_ingress_accepts_only_one_section_enum(string body, bool accepted)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Equal(accepted, WebSettingsService.TryReadLumaTokenSection(json.RootElement, out var section));
        if (!accepted) Assert.Null(section);
    }

    [Fact]
    public void Finite_token_issuance_obeys_existing_update_admission()
    {
        Assert.True(WebSettingsService.RequiresFiniteSettingsUpdateWork("POST", "/api/luma/live-token"));
        Assert.False(WebSettingsService.RequiresFiniteSettingsUpdateWork("GET", "/api/luma/context"));
    }

    [Fact]
    public void Settings_routes_map_to_bounded_live_help_topics_not_user_prompts()
    {
        foreach (var route in new[] { "getting-started", "voice-capture", "account-billing", "hotkeys", "assistant", "browser-automation", "how-to-use", "support-maintenance", "set-api-key", "speech-models", "tts", "voice-notes", "ai", "destinations", "roadmap", "settings", "untrusted-private-page" })
            Assert.Contains(WebSettingsService.LumaHelpTopicForSettingsSection(route), LumaLiveProtocol.Sections);
        Assert.Equal("account", WebSettingsService.LumaHelpTopicForSettingsSection("account-billing"));
        Assert.Equal("personal", WebSettingsService.LumaHelpTopicForSettingsSection("set-api-key"));
    }

    [Fact]
    public void Active_shortcut_projection_accepts_configured_legacy_label_but_never_invents_defaults()
    {
        var projected = WebSettingsService.ProjectLumaActiveHotkeys(new Dictionary<string, string>
        {
            ["PushToTalk"] = "Ctrl+Alt+K",
            ["Jarvis"] = "Ctrl+Alt+J",
            ["selectedText"] = "private-user-text",
        });
        Assert.Equal("Ctrl+Alt+K", projected["SmartWriting"]);
        Assert.Equal("Ctrl+Alt+J", projected["Jarvis"]);
        Assert.False(projected.ContainsKey("Cancel"));
        Assert.False(projected.ContainsKey("selectedText"));
        Assert.Empty(WebSettingsService.ProjectLumaActiveHotkeys(null));
    }

    [Fact]
    public void Canonical_pet_atlas_and_local_ui_are_embedded_not_read_from_a_developer_folder()
    {
        var assembly = typeof(WebSettingsService).Assembly;
        using var atlas = assembly.GetManifestResourceStream("web/luma/assets/spritesheet.webp");
        Assert.NotNull(atlas);
        Assert.True(atlas.Length > 1000);
        foreach (var name in new[] { "luma.js", "luma-site.js", "panel-placement.js", "settings-adapter.js", "settings-theme.css", "theme-bootstrap.js", "luma-ui.css" })
            Assert.Contains("web/luma/" + name, assembly.GetManifestResourceNames());
        using var settings = assembly.GetManifestResourceStream("web/settings.html");
        using var reader = new StreamReader(settings!);
        var html = reader.ReadToEnd();
        Assert.Contains("id=\"luma-settings-root\"", html);
        Assert.Contains("src=\"/luma/settings-adapter.js\"", html);
        Assert.DoesNotContain("create-a-pet-based-on-what", html);
    }
}
