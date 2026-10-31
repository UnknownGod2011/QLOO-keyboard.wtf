namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class FinalPolishRegressionTests : IDisposable
{
    [Theory]
    [InlineData(VoiceUiPhase.Executing, "Searching files", 2, false)]
    [InlineData(VoiceUiPhase.Executing, "Searching files", 5, true)]
    [InlineData(VoiceUiPhase.Thinking, "Preparing response", 5, true)]
    [InlineData(VoiceUiPhase.Listening, "Listening", 5, false)]
    [InlineData(VoiceUiPhase.Speaking, "Speaking", 5, false)]
    public void Slow_task_feedback_is_visual_only_and_does_not_change_quick_or_inactive_phases(
        VoiceUiPhase phase, string detail, int seconds, bool working)
    {
        var display = VoiceOverlayForm.BuildDetail(phase, detail, TimeSpan.FromSeconds(seconds));

        Assert.Equal(working, display.Contains("working on it", StringComparison.Ordinal));
        Assert.Contains(detail, display, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_usable_now_excludes_reserved_credits_and_discloses_the_hold()
    {
        var html = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Resources", "web", "settings.html"));
        var usage = html[html.IndexOf("document.getElementById('paidUsage').textContent", StringComparison.Ordinal)..];
        usage = usage[..usage.IndexOf("const signInMessage", StringComparison.Ordinal)];
        Assert.Contains("formatPaidCredits(available)", usage);
        Assert.DoesNotContain("formatPaidCredits(totalUsable)", usage);
        Assert.Contains("reserved pending settlement", usage);
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-final-polish-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("browser-automation", "browser-automation")]
    [InlineData("#how-to-use-jarvis", "how-to-use")]
    [InlineData("keyboard.wtf text to speech settings", "tts")]
    [InlineData("what's next", "roadmap")]
    [InlineData("account", "account-billing")]
    public void Settings_routes_use_one_canonical_section_registry(
        string requested,
        string expected) =>
        Assert.Equal(expected, SettingsSectionRegistry.Resolve(requested));

    [Fact]
    public void Every_canonical_settings_route_exists_in_the_packaged_page()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));
        foreach (var id in SettingsSectionRegistry.CanonicalIds)
            Assert.Contains($"<section id=\"{id}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_sidebar_uses_the_same_canonical_order_as_rendered_content()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));
        var nav = System.Text.RegularExpressions.Regex.Match(
            html,
            "(?s)<nav id=\"sidebar-nav\">(.*?)</nav>").Groups[1].Value;
        var navIds = System.Text.RegularExpressions.Regex.Matches(nav, "href=\"#([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        var sectionIds = System.Text.RegularExpressions.Regex.Matches(html, "<section id=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(sectionIds, navIds);
        Assert.True(Array.IndexOf(navIds, "voice-capture") < Array.IndexOf(navIds, "account-billing"));
        Assert.Contains("const sectionOrder = new Map(Array.from(sectionById.keys())", html, StringComparison.Ordinal);
        Assert.Contains("navLinks.forEach(link => sidebarNav.appendChild(link));", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_settings_deep_link_remains_authoritative_while_account_layout_loads()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));

        Assert.Contains("let initialRoute = resolveRoute(location.hash);", html, StringComparison.Ordinal);
        Assert.Contains("if (initialRoute)", html, StringComparison.Ordinal);
        Assert.Contains("window.finishInitialSettingsRoute", html, StringComparison.Ordinal);
        Assert.Contains("sectionById.forEach(section => layoutObserver.observe(section));", html,
            StringComparison.Ordinal);
        Assert.Contains("loadSettings().finally(() => window.finishInitialSettingsRoute?.());", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_refresh_does_not_reapply_the_route_and_yank_a_manual_scroll()
    {
        var html = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Resources", "web", "settings.html"));
        var paidRenderer = html[html.IndexOf("function renderPaidAccount(account)", StringComparison.Ordinal)..];
        paidRenderer = paidRenderer[..paidRenderer.IndexOf("function ", 1, StringComparison.Ordinal)];

        Assert.DoesNotContain("syncSettingsRoute", paidRenderer, StringComparison.Ordinal);
        Assert.Contains("window.syncSettingsRoute();", html, StringComparison.Ordinal);
        Assert.Contains("window.finishInitialSettingsRoute", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_folder_contract_distinguishes_requested_targets_from_ambiguous_candidates()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Services", "GeminiLiveConversationService.cs"));

        Assert.Contains("resolve and open each requested folder separately in their order", source,
            StringComparison.Ordinal);
        Assert.Contains("ask the user to choose only when that individual folder remains ambiguous", source,
            StringComparison.Ordinal);
        Assert.Contains("Open one known, learned, or approximately named folder per call", source,
            StringComparison.Ordinal);
        Assert.Contains("search each separately rather than combining their names", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_account_preserves_the_actual_settings_scroll_container()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));

        Assert.Contains("id=\"main-scroll\"", html, StringComparison.Ordinal);
        Assert.Contains("document.getElementById('main-scroll')", html, StringComparison.Ordinal);
        Assert.Contains("settingsScroller.scrollTop = refreshScrollTop;", html, StringComparison.Ordinal);
        Assert.Contains("window.refreshSettingsScrollSpy?.();", html, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelector('main')", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settings", true, false)]
    [InlineData("Windows Settings", true, false)]
    [InlineData("keyboard.wtf settings", false, true)]
    [InlineData("Jarvis settings", false, true)]
    [InlineData("your settings", false, true)]
    public void Settings_intent_keeps_windows_and_keyboard_wtf_targets_distinct(
        string request,
        bool windows,
        bool keyboardWtf)
    {
        Assert.Equal(windows, JarvisAutomationService.IsWindowsSettingsTarget(request));
        Assert.Equal(keyboardWtf, JarvisAutomationService.IsKeyboardWtfSettingsTarget(request));
    }

    [Theory]
    [InlineData("Open Account & Billing in keyboard.wtf Settings.", "account-billing")]
    [InlineData("Show me your hotkey settings.", "hotkeys")]
    [InlineData("Open Jarvis assistant settings.", "assistant")]
    [InlineData("Open keyboard.wtf settings.", "")]
    [InlineData("Open keyboard.wtf Settings, Account and Billing. Do not use Browser Automation.", "account-billing")]
    [InlineData("Show me your hotkey settings; leave text to speech unchanged.", "hotkeys")]
    [InlineData("Open keyboard.wtf voice notes settings.", "voice-notes")]
    public void Explicit_keyboard_wtf_settings_requests_resolve_to_one_local_route(
        string request,
        string expectedSection)
    {
        Assert.True(SettingsSectionRegistry.TryResolveKeyboardWtfNavigationRequest(
            request,
            out var section));
        Assert.Equal(expectedSection, section);
    }

    [Theory]
    [InlineData("Open settings.")]
    [InlineData("Open Windows Settings.")]
    [InlineData("What do you think about the keyboard.wtf settings design?")]
    public void Non_local_or_conversational_settings_mentions_do_not_claim_the_privileged_route(
        string request) =>
        Assert.False(SettingsSectionRegistry.TryResolveKeyboardWtfNavigationRequest(
            request,
            out _));

    [Theory]
    [InlineData("native", true)]
    [InlineData("app", true)]
    [InlineData("application", true)]
    [InlineData("auto", false)]
    [InlineData("website", false)]
    public void Explicit_app_intent_can_require_a_real_native_application(
        string targetKind,
        bool expected) =>
        Assert.Equal(expected, JarvisAutomationService.IsExplicitNativeTarget(targetKind));

    [Fact]
    public void Failed_native_resolution_can_offer_only_a_verified_web_alternative_without_opening_it()
    {
        Assert.True(JarvisAutomationService.TryGetVerifiedWebAlternative(
            "devpost", nativeOnly: true, out var verifiedUrl));
        Assert.Equal("https://devpost.com/", verifiedUrl);
        Assert.False(JarvisAutomationService.TryGetVerifiedWebAlternative(
            "word", nativeOnly: true, out _));
        Assert.False(JarvisAutomationService.TryGetVerifiedWebAlternative(
            "devpost", nativeOnly: false, out _));
    }

    [Theory]
    [InlineData("Open Devpost", "open_app", "Devpost", "https://devpost.com/")]
    [InlineData("Open Gmail, Instagram and Devpost", "open_app", "native Devpost", "https://devpost.com/")]
    [InlineData("Open GitHub and YouTube", "open_app", "desktop GitHub application", "https://github.com/")]
    [InlineData("Open Gmail, Devpost and Word, then take a picture", "open_app", "Devpost", "https://devpost.com/")]
    [InlineData("Open Devpost", "open_url", "dev.to", "https://devpost.com/")]
    [InlineData("Open Google Drive", "open_url", "drive.to", "https://drive.google.com/")]
    public void Unqualified_named_web_open_uses_existing_catalog_despite_model_tool_choice(
        string userTurn, string tool, string target, string expectedUrl)
    {
        using var automation = CreateWebOpenAutomation();
        Assert.True(automation.TryResolveRequestedKnownWebOpen(
            userTurn, tool, target, out var url));
        Assert.Equal(expectedUrl, url);
    }

    [Theory]
    [InlineData("Open the Devpost app", "open_app", "Devpost")]
    [InlineData("Open native Devpost", "open_app", "Devpost")]
    [InlineData("Open the desktop application GitHub", "open_app", "GitHub")]
    [InlineData("Launch Devpost", "open_app", "Devpost")]
    [InlineData("Open Devpost as an application", "open_app", "Devpost")]
    [InlineData("Open the Devpost app", "open_url", "dev.to")]
    [InlineData("Open Devpost at dev.to", "open_app", "Devpost")]
    [InlineData("Open Devpost and GitHub", "open_url", "dev.to")]
    [InlineData("Open Devpost at https://dev.to/", "open_url", "dev.to")]
    [InlineData("Open Devpost", "open_url", "https://devpost.com/")]
    [InlineData("Tell me about Devpost", "open_app", "Devpost")]
    [InlineData("Don't open Devpost", "open_app", "Devpost")]
    public void Explicit_or_ambiguous_named_web_requests_are_not_substituted(
        string userTurn, string tool, string target)
    {
        using var automation = CreateWebOpenAutomation();
        Assert.False(automation.TryResolveRequestedKnownWebOpen(
            userTurn, tool, target, out _));
    }

    [Fact]
    public void A_durable_local_choice_overrides_the_builtin_web_catalog_in_compound_turns()
    {
        using var automation = CreateWebOpenAutomation("Devpost", "https://example.org/project");
        Assert.False(automation.TryResolveRequestedKnownWebOpen(
            "Open Gmail, Devpost and Word", "open_app", "Devpost", out _));
        Assert.False(automation.TryResolveRequestedKnownWebOpen(
            "Open Devpost", "open_url", "dev.to", out _));
    }

    [Theory]
    [InlineData("Open Gmail, Instagram and Devpost", "https://instagram.com/", "https://www.instagram.com/")]
    [InlineData("Open GitHub and YouTube", "https://www.github.com/", "https://github.com/")]
    [InlineData("Open GitHub and YouTube", "https://youtube.com/", "https://www.youtube.com/")]
    [InlineData("Open Gmail, Instagram and Devpost", "https://gmail.com/", "https://mail.google.com/")]
    [InlineData("Open Gmail and WhatsApp", "https://whatsapp.com/", "https://web.whatsapp.com/")]
    [InlineData("Open Google Drive and GitHub", "https://googledrive.com/", "https://drive.google.com/")]
    public void Requested_known_homepage_canonicalizes_an_exact_name_or_www_variant_before_the_destination_guard(
        string turn, string proposal, string expected)
    {
        using var automation = CreateWebOpenAutomation();
        Assert.True(automation.TryResolveRequestedKnownWebOpen(turn, "open_url", proposal, out var canonical));
        Assert.Equal(expected, canonical);
        Assert.False(automation.IsUnrequestedDestinationInExactWebOpen(turn, "open_url", canonical));
    }

    [Theory]
    [InlineData("Open Gmail and Instagram", "https://instagram.com/specific/page")]
    [InlineData("Open Gmail and Instagram", "https://instagram.com/?q=other")]
    [InlineData("Open Gmail and Instagram", "https://instagram.com/#other")]
    [InlineData("Open Gmail and Instagram", "https://instagram.com:8443/")]
    [InlineData("Open Gmail and Instagram", "https://instagram.com.unrelated.invalid/")]
    [InlineData("Open Gmail and Instagram", "https://unrelated.instagram.com/")]
    [InlineData("Open Gmail and Instagram", "https://www.reddit.com/")]
    [InlineData("Open Gmail and Instagram", "https://gmail.com.unrelated.invalid/")]
    [InlineData("Open Gmail and Instagram", "https://gmail.com/inbox")]
    [InlineData("Open Gmail and Instagram", "https://gmail.com:8443/")]
    [InlineData("Open Gmail and Instagram", "https://email.com/")]
    [InlineData("Open Gmail and Instagram", "https://mail.com/")]
    [InlineData("Open Gmail and Instagram", "https://insta.com/")]
    [InlineData("Open Gmail and the Instagram app", "https://instagram.com/")]
    [InlineData("Open Gmail and instagram.com", "https://instagram.com/")]
    public void Known_homepage_correction_preserves_qualified_or_unrelated_destinations(string turn, string proposal)
    {
        using var automation = CreateWebOpenAutomation();
        Assert.False(automation.TryResolveRequestedKnownWebOpen(turn, "open_url", proposal, out _));
    }

    [Fact]
    public void A_saved_preference_remains_authoritative_over_a_named_homepage_proposal()
    {
        using var automation = CreateWebOpenAutomation("Gmail", "https://example.org/inbox");
        Assert.False(automation.TryResolveRequestedKnownWebOpen(
            "Open Gmail and Instagram", "open_url", "https://gmail.com/", out _));
        Assert.True(automation.TryResolveRequestedKnownWebOpen(
            "Open Gmail and Instagram", "open_url", "https://instagram.com/", out var instagram));
        Assert.Equal("https://www.instagram.com/", instagram);
    }

    [Theory]
    [InlineData("Open Gmail, Instagram and Devpost", "open_url", "https://dev.to/", true)]
    [InlineData("Open Gmail, Instagram and Devpost", "open_url", "https://www.reddit.com/r/example/", true)]
    [InlineData("Open Gmail, Instagram and Devpost", "open_app", "death post", true)]
    [InlineData("Open Gmail, Instagram and Devpost", "open_service_page", "Reddit", true)]
    [InlineData("Open Gmail, Instagram and Devpost", "open_url", "https://devpost.com/", false)]
    [InlineData("Open Gmail, Instagram and Devpost", "open_url", "https://mail.google.com/mail/u/0/", false)]
    [InlineData("Open Gmail, Instagram and Devpost", "open_app", "Devpost", false)]
    [InlineData("Open GitHub and YouTube", "open_url", "https://www.reddit.com/", true)]
    [InlineData("Open GitHub and YouTube", "open_url", "https://github.com/example", false)]
    [InlineData("Open Gmail and an unknown site", "open_url", "https://example.org/", false)]
    [InlineData("Open the Devpost app", "open_app", "Discord", false)]
    [InlineData("Open Devpost at https://dev.to/", "open_url", "https://dev.to/", false)]
    public void Exact_named_web_list_rejects_only_unrequested_model_destinations(
        string userTurn, string tool, string target, bool reject)
    {
        using var automation = CreateWebOpenAutomation();
        Assert.Equal(reject, automation.IsUnrequestedDestinationInExactWebOpen(
            userTurn, tool, target));
    }

    [Fact]
    public void Exact_named_web_list_includes_the_saved_link_without_guessing_other_targets()
    {
        using var automation = CreateWebOpenAutomation("Discord", "https://example.org/private-discord");
        const string turn = "Open Gmail, Instagram, Discord and Devpost.";
        Assert.True(automation.IsUnrequestedDestinationInExactWebOpen(
            turn, "open_url", "https://dev.to/"));
        Assert.True(automation.IsUnrequestedDestinationInExactWebOpen(
            turn, "open_app", "Excel"));
        Assert.False(automation.IsUnrequestedDestinationInExactWebOpen(
            turn, "open_url", "Discord"));
        Assert.False(automation.IsUnrequestedDestinationInExactWebOpen(
            turn, "open_url", "https://devpost.com/"));
        Assert.False(automation.IsUnrequestedDestinationInExactWebOpen(
            turn, "open_url", "https://example.org/private-discord"));
    }

    private JarvisAutomationService CreateWebOpenAutomation(
        string savedAlias = null, string savedUrl = null)
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        if (savedAlias != null)
            mappings.Remember(LearnedMappingKind.Link, savedAlias, savedUrl, savedAlias);
        return new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });
    }

    [Theory]
    [InlineData("ChatGPT app", "ChatGPT")]
    [InlineData("launch Visual Studio Code application", "Visual Studio Code")]
    [InlineData("start Discord native app", "Discord")]
    [InlineData("Calculator", "Calculator")]
    public void Explicit_native_app_wording_is_removed_before_installed_app_resolution(
        string request,
        string expected) =>
        Assert.Equal(expected, JarvisAutomationService.NormalizeNativeAppQuery(request));

    [Theory]
    [InlineData("close tab", false)]
    [InlineData("close Chrome", false)]
    [InlineData("thanks", false)]
    [InlineData("thank you", false)]
    [InlineData("goodbye", true)]
    [InlineData("bye", true)]
    [InlineData("good bye", true)]
    [InlineData("Good-bye\u2026", true)]
    [InlineData("go away", true)]
    [InlineData("stop Jarvis", true)]
    [InlineData("Stop Jarvis, please", true)]
    [InlineData("End Jarvis, thanks", true)]
    [InlineData("end chat", true)]
    [InlineData("End the conversation now.", true)]
    [InlineData("Could you end the conversation now?", true)]
    [InlineData("Could you please end the conversation now?", true)]
    [InlineData("Please end this chat now, Jarvis.", true)]
    [InlineData("End conversation now, please.", true)]
    [InlineData("fuck off", true)]
    [InlineData("Don't say goodbye yet", false)]
    [InlineData("After filling the form, say goodbye", false)]
    [InlineData("Can you explain what goodbye means?", false)]
    [InlineData("Tell the browser task to stop listening for changes", false)]
    [InlineData("Don't end the conversation now.", false)]
    [InlineData("Could you not end the conversation now?", false)]
    [InlineData("After reading the email, end the conversation now.", false)]
    [InlineData("Translate 'end the conversation now' into Hindi.", false)]
    [InlineData("What does 'end the conversation now' mean?", false)]
    [InlineData("End the conversation now and open Gmail.", false)]
    [InlineData("Find Daisy's email. End the conversation now.", false)]
    public void Only_explicit_conversation_dismissal_ends_Jarvis(
        string transcript,
        bool expected) =>
        Assert.Equal(expected, GeminiLiveConversationService.ShouldAutoEnd(transcript));

    [Fact]
    public void Fresh_goodbye_fragment_is_detected_even_if_the_accumulated_turn_is_saturated()
    {
        var saturatedTurn = new string('x', 4096);

        Assert.True(GeminiLiveConversationService.ShouldAutoEnd("goodbye", saturatedTurn));
        Assert.True(GeminiLiveConversationService.ShouldAutoEnd("go away", saturatedTurn));
        Assert.False(GeminiLiveConversationService.ShouldAutoEnd("close tab", saturatedTurn));
    }

    [Theory]
    [InlineData("interimInputTranscription")]
    [InlineData("inputTranscription")]
    public void Farewell_is_recognized_from_the_earliest_available_input_transcript(
        string transcriptionProperty)
    {
        using var document = JsonDocument.Parse(
            "{\"" + transcriptionProperty + "\":{\"text\":\"Okay, goodbye.\"}}");

        Assert.True(GeminiLiveConversationService.TryGetFarewellTranscription(
            document.RootElement,
            out var kind));
        Assert.Equal(
            transcriptionProperty == "interimInputTranscription" ? "interim" : "final",
            kind);
    }

    [Fact]
    public void Normal_browser_close_is_not_a_farewell_in_an_interim_transcript()
    {
        using var document = JsonDocument.Parse(
            """{"interimInputTranscription":{"text":"close the Chrome tab"}}""");

        Assert.False(GeminiLiveConversationService.TryGetFarewellTranscription(
            document.RootElement,
            out _));
    }

    [Fact]
    public void Managed_farewell_path_is_local_and_has_no_deferred_provider_turn_delay()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "GeminiLiveConversationService.cs"));

        var managedFarewell = System.Text.RegularExpressions.Regex.Match(source,
            @"if \(!diagnostics\.UsesPersonalToolProtocol\s+&& ShouldAutoEnd\(text, accumulatedUserTurn\)\)");
        var farewell = managedFarewell.Success ? managedFarewell.Index : -1;
        var observer = source.IndexOf(
            "GetToolDispatcher(diagnostics, token).Observe(_userTranscriptObserver, accumulatedUserTurn,",
            StringComparison.Ordinal);
        Assert.True(farewell >= 0);
        Assert.True(observer > farewell);
        Assert.Contains("StopMicrophone();", source, StringComparison.Ordinal);
        Assert.Contains("StopPlayback();", source, StringComparison.Ordinal);
        Assert.Contains("await StopAsync(showDone: false);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await Task.Delay(850);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_prompt_routes_natural_current_screen_questions_to_fresh_vision()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "GeminiLiveConversationService.cs"));

        Assert.Contains("what the user is looking at", source, StringComparison.Ordinal);
        Assert.Contains("must call inspect_screen", source, StringComparison.Ordinal);
        Assert.Contains("Every new explicit current-screen request captures a fresh image", source,
            StringComparison.Ordinal);
        Assert.Contains("the foreground app name alone is not screen analysis", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Live_prompt_keeps_transient_actions_separate_from_explicit_persistent_memory()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "GeminiLiveConversationService.cs"));

        Assert.Contains("Ordinary one-time commands", source, StringComparison.Ordinal);
        Assert.Contains("Never call remember_routine", source, StringComparison.Ordinal);
        Assert.Contains("explicitly asks to remember or save a reusable trigger", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Deleted_workflow_tombstone_survives_restart_and_is_cleared_by_explicit_resave()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var settings = new SettingsService(path);
        settings.Load();
        settings.SaveWorkflow("initialize my setup", "Notepad", "", "");

        Assert.True(settings.DeleteWorkflow("initialize my setup"));
        Assert.True(settings.IsWorkflowDeleted("initialize my setup"));

        var reloaded = new SettingsService(path);
        reloaded.Load();
        Assert.True(reloaded.IsWorkflowDeleted("initialize my setup"));
        Assert.Empty(reloaded.Current.JarvisWorkflows);

        reloaded.SaveWorkflow("initialize my setup", "Calculator", "", "");
        Assert.False(reloaded.IsWorkflowDeleted("initialize my setup"));
        Assert.Equal("Calculator", Assert.Single(reloaded.Current.JarvisWorkflows).Apps);
    }

    [Fact]
    public void Deleting_a_workflow_removes_aliases_and_targets_from_learned_mappings()
    {
        Directory.CreateDirectory(_root);
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(
            LearnedMappingKind.Workflow,
            "start my day",
            "initialize my setup",
            "Initialize my setup");

        Assert.Equal(1, mappings.ForgetWorkflowReferences("initialize my setup"));
        Assert.Empty(mappings.Snapshot());
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(91, false)]
    public void Cached_selection_is_only_safe_in_the_same_recent_window(int ageSeconds, bool expected)
    {
        var context = new SelectionContext((IntPtr)10, "notepad", "Draft - Notepad");
        var snapshot = new SelectionSnapshot(
            "current selection",
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ageSeconds),
            context.Handle,
            context.ProcessName,
            context.WindowTitle);

        Assert.Equal(expected, JarvisAutomationService.CanUseCachedSelection(
            snapshot,
            context,
            DateTimeOffset.UtcNow));
        Assert.False(JarvisAutomationService.CanUseCachedSelection(
            snapshot,
            new SelectionContext((IntPtr)11, "chrome", "Unrelated tab"),
            DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch { }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
