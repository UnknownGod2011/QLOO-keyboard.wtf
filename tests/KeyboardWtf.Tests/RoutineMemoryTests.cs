namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

public sealed class RoutineMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-routine-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Open my website.")]
    [InlineData("Okay, open my website.")]
    [InlineData("OK open my website")]
    [InlineData("Alright, please open my website.")]
    public async Task A_saved_full_trigger_survives_reload_and_a_conversational_acknowledgement(string request)
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var path = Path.Combine(_root, "mappings.json");
        var mappings = new LearnedMappingService(path);
        using (var automation = new JarvisAutomationService(new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")), mappings, () => { }))
        {
            var saved = await automation.ExecuteAsync("remember_link_alias",
                JsonSerializer.SerializeToElement(new { alias = "open my website", url = "example.org/project" }), CancellationToken.None);
            Assert.Equal(true, saved["persistence_verified"]);
        }
        var reloaded = new LearnedMappingService(path);
        reloaded.Load();
        using var next = new JarvisAutomationService(new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")), reloaded, () => { });
        Assert.True(next.TryResolveDurableOpenPreference(request, out var tool, out var arguments));
        Assert.Equal("open_app", tool);
        Assert.Equal("open my website", arguments.GetProperty("app_name").GetString());
        Assert.Equal("https://example.org/project", reloaded.FindExact(LearnedMappingKind.Link, "open my website")?.Target);
        Assert.False(next.TryResolveDurableOpenPreference("Okay, open my website app.", out _, out _));
        Assert.False(next.TryResolveDurableOpenPreference("Don't open my website.", out _, out _));
    }

    [Fact]
    public async Task RememberRoutineSavesWorkflowAndFuzzyTrigger()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        var history = new JarvisActionHistoryService(Path.Combine(_root, "history.json"));
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            history,
            mappings,
            () => { });
        var args = JsonSerializer.SerializeToElement(new
        {
            trigger = "initialize my setup",
            apps = "Notepad",
            urls = "",
            folder = "",
        });

        var result = await automation.ExecuteAsync("remember_routine", args, CancellationToken.None);

        Assert.True((bool)result["ok"]);
        Assert.Contains(
            settings.Current.JarvisWorkflows,
            workflow => workflow.Name.Equals("initialize my setup", StringComparison.OrdinalIgnoreCase)
                && workflow.Apps == "Notepad"
                && workflow.Version == JarvisWorkflowSettings.CurrentVersion
                && workflow.Actions.Count == 1
                && workflow.Actions[0].Kind == "app");
        Assert.Contains(
            mappings.Search("initialize my set", LearnedMappingKind.Workflow, 3),
            mapping => mapping.Target.Equals("initialize my setup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Invalid_url_action_is_not_saved_as_a_routine()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        var result = await automation.ExecuteAsync("remember_routine",
            JsonSerializer.SerializeToElement(new
            {
                trigger = "open my project",
                actions = new[] { new { kind = "url", target = "use_selected" } },
            }), CancellationToken.None);

        Assert.False((bool)result["ok"]);
        Assert.Empty(settings.Current.JarvisWorkflows);
        Assert.Empty(mappings.Snapshot());
    }

    [Fact]
    public void Mixed_valid_and_invalid_workflow_actions_cannot_silently_save_an_incomplete_routine()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();

        Assert.Throws<InvalidOperationException>(() => settings.SaveWorkflow(
            "open my sites", ["open my sites"],
            [
                new JarvisWorkflowActionSettings { Kind = "url", Target = "https://example.org/" },
                new JarvisWorkflowActionSettings { Kind = "url", Target = "https://use_selected/" },
            ]));

        Assert.Empty(settings.Current.JarvisWorkflows);
    }

    [Fact]
    public async Task Explicitly_remembered_routine_survives_complete_service_reload()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        using (var automation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            var args = JsonSerializer.SerializeToElement(new
            {
                trigger = "potato patato",
                apps = "Notepad",
                urls = "https://example.com/",
                folder = "",
            });
            var result = await automation.ExecuteAsync("remember_routine", args, CancellationToken.None);
            Assert.True((bool)result["ok"]);
        }

        var reloadedSettings = new SettingsService(settingsPath);
        reloadedSettings.Load();
        var reloadedMappings = new LearnedMappingService(mappingsPath);
        reloadedMappings.Load();

        var workflow = Assert.Single(reloadedSettings.Current.JarvisWorkflows);
        Assert.Equal("potato patato", workflow.Name, ignoreCase: true);
        Assert.Equal("Notepad", workflow.Apps);
        Assert.Equal("https://example.com/", workflow.Urls);
        Assert.Equal(JarvisWorkflowSettings.CurrentVersion, workflow.Version);
        Assert.Collection(
            workflow.Actions,
            action =>
            {
                Assert.Equal("app", action.Kind);
                Assert.Equal("Notepad", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://example.com/", action.Target);
            });
        Assert.Equal(
            "potato patato",
            reloadedMappings.FindExact(LearnedMappingKind.Workflow, "potato patato")?.Target,
            ignoreCase: true);
    }

    [Fact]
    public async Task Ordered_durable_workflow_resolves_in_a_completely_new_conversation_service()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var firstSettings = new SettingsService(settingsPath);
        firstSettings.Load();
        firstSettings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var firstMappings = new LearnedMappingService(mappingsPath);
        firstMappings.Load();
        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   firstSettings,
                   new JarvisActionHistoryService(historyPath),
                   firstMappings,
                   () => { }))
        {
            var result = await firstConversation.ExecuteAsync(
                "remember_routine",
                JsonSerializer.SerializeToElement(new
                {
                    trigger = "potato patato",
                    actions = new object[]
                    {
                        new { kind = "url", target = "https://mail.google.com/" },
                        new { kind = "app", target = "Discord" },
                        new { kind = "url", target = "https://devpost.com/" },
                        new { kind = "url", target = "https://www.instagram.com/" },
                        new { kind = "app", target = "Excel" },
                    },
                }),
                CancellationToken.None);

            Assert.True((bool)result["ok"]);
            Assert.True((bool)result["persistence_verified"]);
        }

        var secondSettings = new SettingsService(settingsPath);
        secondSettings.Load();
        var secondMappings = new LearnedMappingService(mappingsPath);
        secondMappings.Load();
        using var completelyNewConversation = new JarvisAutomationService(
            new NotificationService(null),
            secondSettings,
            new JarvisActionHistoryService(historyPath),
            secondMappings,
            () => { });

        Assert.True(completelyNewConversation.TryResolveSavedWorkflowTrigger(
            "Potato-patato!",
            out var canonical));
        Assert.Equal("potato patato", canonical, ignoreCase: true);
        var persisted = secondSettings.ReadPersistedWorkflow("potato patato");
        Assert.NotNull(persisted);
        Assert.Equal(5, persisted.Actions.Count);
        Assert.Equal(
            new[] { "url", "app", "url", "url", "app" },
            persisted.Actions.Select(action => action.Kind).ToArray());
        Assert.Contains(
            "[potato patato]",
            JarvisAutomationService.BuildSavedWorkflowPromptDigest(secondSettings.Current.JarvisWorkflows),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_workflow_save_language_is_never_browser_pre_routed()
    {
        Assert.True(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Whenever I say potato patato, open Gmail and Discord. Remember this."));
        Assert.True(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Save this workflow for later."));
        Assert.False(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Open Gmail."));
        Assert.False(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Open the calendar, then open my saved portal."));
        Assert.True(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Create a reusable workflow named morning setup."));
    }

    [Fact]
    public async Task Model_cannot_turn_a_one_time_compound_request_into_a_saved_workflow()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });
        var commands = new CommandRegistry(
            null, null, null, settings, null, automation: automation);
        var proposed = JsonSerializer.SerializeToElement(new
        {
            name = "morning setup",
            actions = new[] { new { kind = "app", target = "Notepad" } },
        });

        commands.ObserveSubmittedJarvisText(
            "Open the calendar, then open my saved portal.",
            CancellationToken.None);
        var rejected = await commands.ExecuteJarvisToolAsync(
            "create_workflow", proposed, CancellationToken.None);

        Assert.False((bool)rejected["ok"]);
        Assert.Empty(settings.Current.JarvisWorkflows);

        commands.ObserveSubmittedJarvisText(
            "Create a reusable workflow named morning setup.",
            CancellationToken.None);
        var saved = await commands.ExecuteJarvisToolAsync(
            "create_workflow", proposed, CancellationToken.None);

        Assert.True((bool)saved["ok"]);
        Assert.True(settings.Current.JarvisWorkflows.Count == 1, JsonSerializer.Serialize(saved));
    }

    [Fact]
    public void Compound_request_does_not_let_a_guessed_url_replace_an_exact_saved_link()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(
            LearnedMappingKind.Link,
            "open the team portal",
            "https://example.com/team",
            "Team portal");
        mappings.Remember(
            LearnedMappingKind.Link,
            "open the reddit portal",
            "https://example.org/internal",
            "Reddit-named internal portal");
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });
        const string request = "Open Google Calendar, then open the team portal.";

        Assert.True(automation.TryGetUnresolvedRequestedLinkAlias(
            request, "https://guessed.example.net/", out var alias));
        Assert.Equal("open the team portal", alias);
        Assert.False(automation.TryGetUnresolvedRequestedLinkAlias(
            request, "open the team portal", out _));
        Assert.False(automation.TryGetUnresolvedRequestedLinkAlias(
            request, "https://example.com/team", out _));
        Assert.False(automation.TryGetUnresolvedRequestedLinkAlias(
            request, "https://calendar.google.com/", out _));
        Assert.False(automation.TryGetUnresolvedRequestedLinkAlias(
            "Open Google Calendar.", "https://calendar.google.com/", out _));
        Assert.False(automation.TryGetUnresolvedRequestedLinkAlias(
            "Open Reddit, then open the team portal.", "https://www.reddit.com/", out _));
        Assert.True(automation.TryGetUnresolvedRequestedLinkAlias(
            "Open the reddit portal.", "https://www.reddit.com/", out _));
    }

    [Fact]
    public void Compound_open_tools_retain_each_exact_saved_link_over_model_tool_kinds()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(LearnedMappingKind.Link, "open the team portal",
            "https://example.com/team", "Team portal");
        mappings.Remember(LearnedMappingKind.Link, "open the status page",
            "https://example.org/status", "Status page");
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        const string request = "Open the team portal, then open the status page.";
        Assert.True(automation.TryResolveRequestedSavedLinkForOpenTool(
            request, "team portal", out var first));
        Assert.Equal("open the team portal", first);
        Assert.True(automation.TryResolveRequestedSavedLinkForOpenTool(
            request, "status page", out var second));
        Assert.Equal("open the status page", second);
        Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool(
            request, "another portal", out _));
        Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool(
            "Open the team portal app.", "team portal", out _));
        Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool(
            "Open the team portal settings.", "team portal", out _));
        Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool(
            "Tell me about the team portal.", "team portal", out _));
        Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool(
            "Don't open the team portal, then open the status page.",
            "team portal", out _));
        Assert.False(File.Exists(Path.Combine(_root, "history.json")));
    }

    [Fact]
    public void Compound_open_url_can_recover_a_shortened_exact_saved_link_alias()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(LearnedMappingKind.Link, "open my project portal",
            "https://example.org/private-project", "Project portal");
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        Assert.True(automation.TryResolveRequestedSavedLinkForOpenTool(
            "Open Calendar, then open my project portal and open Calculator.",
            "my project portal", out var alias));
        Assert.Equal("open my project portal", alias);
        Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool(
            "Open Calendar and open Calculator.", "my project portal", out _));
    }

    [Fact]
    public void Compound_native_hint_cannot_bypass_an_exact_saved_web_link()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(LearnedMappingKind.Link, "chat service",
            "https://example.org/chat", "Chat service");
        mappings.Remember(LearnedMappingKind.Link, "open chat service",
            "https://example.org/inbox", "Chat inbox");
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Calendar, chat service, and Word; then take a picture.",
            "chat service", out var alias));
        Assert.Equal("chat service", alias);
        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Could you open chat service now?", "chat service", out alias));
        Assert.Equal("chat service", alias);
        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open example.com, chat service, and Word.",
            "chat service", out alias));
        Assert.Equal("chat service", alias);
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open the chat service app.", "chat service", out _));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Launch chat service.", "chat service", out _));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open chat service.example.com.", "chat service", out _));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Tell me about chat service.", "chat service", out _));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open a different service.", "chat service", out _));
        Assert.False(File.Exists(Path.Combine(_root, "history.json")));
    }

    [Fact]
    public void Compound_service_home_open_uses_the_named_saved_link_unless_user_requests_a_page_or_app()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(LearnedMappingKind.Link, "open chat service",
            "https://example.org/saved-chat", "Saved chat");
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        const string compound = "Open Mail, Photos, and chat service.";
        Assert.True(CommandRegistry.CanCorrectNamedSavedLinkFromTool("open_service_page"));
        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            compound, "chat service", out var alias, "home"));
        Assert.Equal("open chat service", alias);
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Mail and the chat service app.", "chat service", out _, "app"));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Mail and chat service friends.", "chat service", out _, "friends"));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Mail and the friends page on chat service.", "chat service", out _, "friends"));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Mail and chat service's friends page.", "chat service", out _, "friends"));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Mail and chat service homepage.", "chat service", out _, "home"));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open the home page of chat service and Mail.", "chat service", out _, "home"));
        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Mail's homepage and chat service.", "chat service", out alias, "home"));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Mail and chat service.example.org.", "chat service", out _, "home"));
        Assert.False(CommandRegistry.CanCorrectNamedSavedLinkFromTool("browser_automation"));
        Assert.False(File.Exists(Path.Combine(_root, "history.json")));
    }

    [Fact]
    public void Personal_compound_open_keeps_the_saved_destination_over_a_model_service_guess()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(LearnedMappingKind.Link, "Discord",
            "https://example.org/saved-chat", "Saved chat");
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        const string userTurn = "Open Gmail, Instagram, Discord and Devpost.";
        // The Personal Live adapter preserves the provider's fragment spacing,
        // so the routing owner receives the actual named object in this turn.
        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            userTurn, "Discord", out var alias, "home"));
        Assert.Equal("Discord", alias);
        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            userTurn, "Discord app", out alias));
        Assert.Equal("Discord", alias);
        Assert.True(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open Discord.", "Discord", out alias, "home"));
        Assert.Equal("Discord", alias);
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open the Discord app.", "Discord", out _, "home"));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open the Discord app.", "Discord app", out _));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen(
            "Open a different app.", "Discord", out _, "home"));
    }

    [Theory]
    [InlineData("Open Gmail and chat service", "native chat service", true)]
    [InlineData("Open Gmail and chat service", "desktop chat service application", true)]
    [InlineData("Open Gmail and chat service", "chat service native app", true)]
    [InlineData("Open the native chat service", "chat service", false)]
    [InlineData("Open the desktop application chat service", "chat service", false)]
    [InlineData("Open the chat service app", "native chat service", false)]
    [InlineData("Open a different service", "native chat service", false)]
    public void Model_app_decorations_do_not_override_the_saved_link_but_user_decorations_do(
        string userTurn, string proposal, bool expected)
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Remember(LearnedMappingKind.Link, "open chat service", "https://example.org/saved-chat");
        using var automation = new JarvisAutomationService(new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")), mappings, () => { });
        Assert.Equal(expected, automation.TryResolveNamedSavedLinkInCompoundOpen(userTurn, proposal, out var alias));
        if (expected) Assert.Equal("open chat service", alias);
        Assert.False(File.Exists(Path.Combine(_root, "history.json")));
    }

    [Theory]
    [InlineData("Open chat service", "https://example.org/", true)]
    [InlineData("Open Gmail, chat service and Devpost", "https://example.org/", true)]
    [InlineData("Open unrelated.example and chat service", "https://example.org/", true)]
    [InlineData("Open the chat service app", "https://example.org/", false)]
    [InlineData("Open chat service home page", "https://example.org/", false)]
    [InlineData("Open chat service friends", "https://example.org/", false)]
    [InlineData("Open chat service at https://example.org/", "https://example.org/", false)]
    [InlineData("Open something else", "https://example.org/", false)]
    [InlineData("Do not open chat service", "https://example.org/", false)]
    [InlineData("Open chat service", "https://unrelated.example/", false)]
    [InlineData("Open chat service", "https://example.org/other-page", false)]
    public void Model_homepage_cannot_bypass_an_exact_requested_saved_link(
        string userTurn, string proposedUrl, bool expected)
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Remember(LearnedMappingKind.Link, "open chat service", "https://example.org/saved-chat");
        using var automation = new JarvisAutomationService(new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")), mappings, () => { });
        Assert.Equal(expected, automation.TryResolveRequestedSavedLinkForOpenTool(userTurn, proposedUrl, out var alias));
        if (expected) Assert.Equal("open chat service", alias);
        Assert.False(File.Exists(Path.Combine(_root, "history.json")));
    }

    [Theory]
    [InlineData("chat service", "open chat service", "https://example.org/")]
    [InlineData("research portal", "open research portal", "https://example.org/")]
    [InlineData("team home", "team home", "https://example.org/")]
    [InlineData("chat service", "open chat service", "native chat service")]
    public void Saved_link_correction_survives_the_subsequent_closed_destination_guard(
        string name, string storedAlias, string modelProposal)
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "guard-settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "guard-mappings.json"));
        mappings.Remember(LearnedMappingKind.Link, storedAlias, "https://example.org/specific/path");
        mappings.Remember(LearnedMappingKind.Link, "open unrelated saved alias", "https://example.org/other");
        using var automation = new JarvisAutomationService(new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "guard-history.json")), mappings, () => { });
        var turn = $"Open Gmail, Instagram, {name} and Devpost.";
        var corrected = automation.TryResolveRequestedSavedLinkForOpenTool(turn, modelProposal, out var alias)
            || automation.TryResolveNamedSavedLinkInCompoundOpen(turn, modelProposal, out alias);
        Assert.True(corrected);
        Assert.Equal(storedAlias, alias);
        Assert.False(automation.IsUnrequestedDestinationInExactWebOpen(turn, "open_url", alias));
        Assert.True(automation.IsUnrequestedDestinationInExactWebOpen(turn, "open_url", "open unrelated saved alias"));
        Assert.True(automation.IsUnrequestedDestinationInExactWebOpen(turn, "open_url", "https://unrelated.invalid/"));
        Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool($"Open the {name} app.", modelProposal, out _));
        Assert.False(automation.TryResolveNamedSavedLinkInCompoundOpen($"Open the {name} app.", modelProposal, out _));
    }

    [Fact]
    public async Task Explicit_web_workflow_target_is_not_persisted_as_an_auto_app()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        settings.SaveWorkflow("test routine", ["test routine"],
            [new JarvisWorkflowActionSettings { Kind = "auto", Target = "url:https://example.org/old" }]);
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        Assert.True(JarvisAutomationService.TryGetTypedWorkflowUrl(
            "url:https://example.org/old", out var oldUrl));
        Assert.Equal("https://example.org/old", oldUrl);
        var edited = await automation.ExecuteAsync("edit_workflow",
            JsonSerializer.SerializeToElement(new
            {
                name = "test routine", operation = "replace_action",
                target = "url:https://example.org/old",
                replacement = "url:https://example.net/new",
            }), CancellationToken.None);
        Assert.True((bool)edited["ok"]);
        var action = Assert.Single(settings.ReadPersistedWorkflow("test routine").Actions);
        Assert.Equal("url", action.Kind);
        Assert.Equal("https://example.net/new", action.Target);
    }

    [Fact]
    public async Task Workflow_edit_retains_link_identity_and_reports_an_unchanged_edit_truthfully()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        settings.SaveWorkflow("test routine", ["test routine"],
            [new JarvisWorkflowActionSettings { Kind = "auto", Target = "old chat" }]);
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(LearnedMappingKind.Link, "team chat", "https://example.org/chat", "Team chat");
        using var automation = new JarvisAutomationService(
            new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings, () => { });

        var args = JsonSerializer.SerializeToElement(new
        {
            name = "test routine", operation = "replace_action",
            target = "old chat", replacement = "team chat",
        });
        var edited = await automation.ExecuteAsync("edit_workflow", args, CancellationToken.None);
        Assert.True((bool)edited["ok"]);
        Assert.True((bool)edited["changed"]);
        var action = Assert.Single(settings.ReadPersistedWorkflow("test routine").Actions);
        Assert.Equal("auto", action.Kind);
        Assert.Equal("team chat", action.Target);

        var unchanged = await automation.ExecuteAsync("edit_workflow",
            JsonSerializer.SerializeToElement(new
            {
                name = "test routine", operation = "replace_action",
                target = "team chat", replacement = "team chat",
            }), CancellationToken.None);
        Assert.True((bool)unchanged["ok"]);
        Assert.False((bool)unchanged["changed"]);
        Assert.Contains("nothing changed", unchanged["message"].ToString());
    }

    [Fact]
    public void Complete_explicit_save_turn_is_persisted_without_relying_on_model_tool_choice()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        var handled = automation.TryRememberWorkflowFromTranscript(
            "Whenever I say potato patato, open Gmail, Discord, Devpost, Instagram, and Excel. Remember this.",
            completeTurn: true,
            out var result);

        Assert.True(handled);
        Assert.True((bool)result["ok"]);
        Assert.True((bool)result["persistence_verified"]);
        var persisted = settings.ReadPersistedWorkflow("potato patato");
        Assert.NotNull(persisted);
        Assert.Collection(
            persisted.Actions,
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://mail.google.com/", action.Target);
            },
            action =>
            {
                Assert.Equal("app", action.Kind);
                Assert.Equal("Discord", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://devpost.com/", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://www.instagram.com/", action.Target);
            },
            action =>
            {
                Assert.Contains(action.Kind, new[] { "app", "auto" });
                Assert.Equal("Excel", action.Target);
            });
        Assert.Equal(
            "potato patato",
            mappings.FindExact(LearnedMappingKind.Workflow, "potato patato")?.Target,
            ignoreCase: true);
    }

    [Fact]
    public void Explicit_open_list_with_comma_and_conjunction_saves_each_target_in_order()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        Assert.True(automation.TryRememberWorkflowFromTranscript(
            "Whenever I say start my workspace, open Gmail, Devpost and Instagram. Remember this.",
            completeTurn: true,
            out var result));

        Assert.True((bool)result["ok"]);
        var actions = settings.ReadPersistedWorkflow("start my workspace").Actions;
        Assert.Equal(3, actions.Count);
        Assert.Equal("https://mail.google.com/", actions[0].Target);
        Assert.Equal("https://devpost.com/", actions[1].Target);
        Assert.Equal("https://www.instagram.com/", actions[2].Target);
        Assert.All(actions, action => Assert.Equal("url", action.Kind));
    }

    [Fact]
    public void Incremental_voice_save_waits_for_the_explicit_terminal_marker()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        Assert.False(automation.TryRememberWorkflowFromTranscript(
            "Whenever I say focus mode, open Gmail",
            completeTurn: false,
            out _));
        Assert.Empty(settings.Current.JarvisWorkflows);

        Assert.True(automation.TryRememberWorkflowFromTranscript(
            "Whenever I say focus mode, open Gmail and Notepad. Remember this.",
            completeTurn: false,
            out var result));
        Assert.True((bool)result["persistence_verified"]);
        Assert.Equal(2, settings.ReadPersistedWorkflow("focus mode").Actions.Count);
    }

    [Fact]
    public async Task Deleting_a_workflow_by_alias_removes_and_tombstones_the_canonical_routine()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        settings.SaveWorkflow("initialize my setup", "Notepad", "", "");
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(
            LearnedMappingKind.Workflow,
            "start my day",
            "initialize my setup",
            "Initialize my setup");
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        var result = await automation.ExecuteAsync(
            "delete_workflow",
            JsonSerializer.SerializeToElement(new { name = "start my day" }),
            CancellationToken.None);

        Assert.True((bool)result["ok"]);
        Assert.Empty(settings.Current.JarvisWorkflows);
        Assert.True(settings.IsWorkflowDeleted("initialize my setup"));
        Assert.True(settings.IsWorkflowDeleted("start my day"));
        Assert.Empty(mappings.Snapshot(LearnedMappingKind.Workflow));

        var reloadedSettings = new SettingsService(settings.SettingsPath);
        reloadedSettings.Load();
        var reloadedMappings = new LearnedMappingService(mappings.MappingPath);
        reloadedMappings.Load();
        using var laterConversation = new JarvisAutomationService(
            new NotificationService(null),
            reloadedSettings,
            new JarvisActionHistoryService(Path.Combine(_root, "later-history.json")),
            reloadedMappings,
            () => { });
        Assert.False(laterConversation.TryResolveSavedWorkflowTrigger("start my day", out _));
        Assert.False(laterConversation.TryResolveSavedWorkflowTrigger("initialize my setup", out _));
        Assert.True(laterConversation.IsDeletedWorkflowTrigger("start my day"));
        Assert.True(laterConversation.IsDeletedWorkflowTrigger("Initialize-my setup!"));
        Assert.False(laterConversation.IsDeletedWorkflowTrigger("an unrelated request"));
    }

    [Fact]
    public void ExplicitDurableWebCorrectionPersistsUpdatesAndDeletesAcrossConversations()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            Assert.True(firstConversation.TryApplyDurablePreferenceFromTranscript(
                "Always open Discord on the web instead.",
                out var tool,
                out var saved));
            Assert.Equal("remember_link_alias", tool);
            Assert.True((bool)saved["ok"]);
            Assert.True((bool)saved["persistence_verified"]);
            Assert.False(firstConversation.TryApplyDurablePreferenceFromTranscript(
                "Open Discord on the web this time.",
                out _,
                out _));
        }

        var secondConversationMappings = new LearnedMappingService(mappingsPath);
        secondConversationMappings.Load();
        Assert.Equal(
            "https://discord.com/app",
            secondConversationMappings.FindExact(LearnedMappingKind.Link, "discord")?.Target);
        var secondSettings = new SettingsService(settingsPath);
        secondSettings.Load();
        using (var secondConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   secondSettings,
                   new JarvisActionHistoryService(historyPath),
                   secondConversationMappings,
                   () => { }))
        {
            Assert.True(secondConversation.TryApplyDurablePreferenceFromTranscript(
                "Always open https://discord.com/channels/@me when I say Discord.",
                out var updateTool,
                out var updated));
            Assert.Equal("remember_link_alias", updateTool);
            Assert.True((bool)updated["ok"]);
        }

        var updatedMappings = new LearnedMappingService(mappingsPath);
        updatedMappings.Load();
        Assert.Equal(
            "https://discord.com/channels/@me",
            updatedMappings.FindExact(LearnedMappingKind.Link, "discord")?.Target);
        using (var finalConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   new SettingsService(settingsPath),
                   new JarvisActionHistoryService(historyPath),
                   updatedMappings,
                   () => { }))
        {
            Assert.True(finalConversation.TryApplyDurablePreferenceFromTranscript(
                "Forget my saved preference for Discord.",
                out var deleteTool,
                out var deleted));
            Assert.Equal("forget_learned_mapping", deleteTool);
            Assert.True((bool)deleted["ok"]);
        }
        var afterDelete = new LearnedMappingService(mappingsPath);
        afterDelete.Load();
        Assert.Null(afterDelete.FindExact(LearnedMappingKind.Link, "discord"));
    }

    [Fact]
    public void Saved_link_edits_and_deletion_change_all_model_open_routes_after_reload()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mapping-lifecycle.json");
        var mappings = new LearnedMappingService(path);
        mappings.RememberExclusive(LearnedMappingKind.Link, "open team portal",
            "https://portal.example/inbox", "Portal");
        foreach (var target in new[] { "https://portal.example/inbox", "https://portal.example/updated?view=team" })
        {
            mappings.RememberExclusive(LearnedMappingKind.Link, "open team portal", target, "Portal");
            mappings = new LearnedMappingService(path);
            mappings.Load();
            Assert.Single(mappings.Snapshot());
            Assert.Equal(target, mappings.FindExact(LearnedMappingKind.Link, "open team portal").Target);
            using var automation = new JarvisAutomationService(null, new SettingsService(Path.Combine(_root, "settings.json")),
                new JarvisActionHistoryService(Path.Combine(_root, "history.json")), mappings, () => { });
            foreach (var request in new[] { "Open team portal.", "Open Gmail and team portal." })
            foreach (var proposal in new[] { "team portal app", "native team portal", "https://portal.example/" })
            {
                var resolved = automation.TryResolveRequestedSavedLinkForOpenTool(request, proposal, out var alias)
                    || automation.TryResolveNamedSavedLinkInCompoundOpen(request, proposal, out alias);
                Assert.True(resolved, request + " / " + proposal);
                Assert.Equal(target, mappings.FindExact(LearnedMappingKind.Link, alias).Target);
            }
            Assert.False(automation.TryResolveRequestedSavedLinkForOpenTool(
                "Open the team portal app.", "team portal app", out _));
        }
        Assert.True(mappings.Forget("open team portal", LearnedMappingKind.Link));
        var reloaded = new LearnedMappingService(path);
        reloaded.Load();
        Assert.Empty(reloaded.Snapshot());
        using var removed = new JarvisAutomationService(null, new SettingsService(Path.Combine(_root, "settings.json")),
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")), reloaded, () => { });
        Assert.False(removed.TryResolveRequestedSavedLinkForOpenTool(
            "Open Gmail and team portal.", "https://portal.example/", out _));
        Assert.Equal("", reloaded.BuildLinkAliasPromptDigest());
    }

    [Fact]
    public void TriggerFirstDurableLinkMappingUsesLinkOwnershipAndSurvivesReload()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();

        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            Assert.True(firstConversation.TryApplyDurablePreferenceFromTranscript(
                "Remember this deliberate mapping: whenever I say open team portal, open exactly https://example.com/workspace/inbox?view=all in the browser instead of opening the desktop app.",
                out var tool,
                out var saved));
            Assert.Equal("remember_link_alias", tool);
            Assert.True((bool)saved["ok"]);
            Assert.True((bool)saved["persistence_verified"]);
        }

        var reloadedMappings = new LearnedMappingService(mappingsPath);
        reloadedMappings.Load();
        Assert.Equal(
            "https://example.com/workspace/inbox?view=all",
            reloadedMappings.FindExact(LearnedMappingKind.Link, "open team portal")?.Target);
        Assert.Empty(reloadedMappings.Snapshot(LearnedMappingKind.Workflow));

        using var laterConversation = new JarvisAutomationService(
            new NotificationService(null),
            new SettingsService(settingsPath),
            new JarvisActionHistoryService(historyPath),
            reloadedMappings,
            () => { });
        Assert.True(laterConversation.TryResolveDurableOpenPreference(
            "Open team portal",
            out var resolvedTool,
            out var arguments));
        Assert.Equal("open_app", resolvedTool);
        Assert.Equal("open team portal", arguments.GetProperty("app_name").GetString());
        Assert.Equal("auto", arguments.GetProperty("target_kind").GetString());
    }

    [Fact]
    public async Task BareHostnameCustomTriggerUsesOneDurableLinkAndSurvivesReloadAndUpdate()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();

        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            Assert.True(firstConversation.TryApplyDurablePreferenceFromTranscript(
                "Remember to open example.com whenever I say open my website.",
                out var tool,
                out var saved));
            Assert.Equal("remember_link_alias", tool);
            Assert.True((bool)saved["ok"]);
            Assert.True((bool)saved["persistence_verified"]);

            var updated = await firstConversation.ExecuteAsync(
                "remember_link_alias",
                JsonSerializer.SerializeToElement(new
                {
                    alias = "open my website",
                    url = "example.org/new",
                }),
                CancellationToken.None);
            Assert.True((bool)updated["ok"]);

            var malformed = await firstConversation.ExecuteAsync(
                "remember_link_alias",
                JsonSerializer.SerializeToElement(new
                {
                    alias = "open unsafe target",
                    url = "not a URL",
                }),
                CancellationToken.None);
            Assert.False((bool)malformed["ok"]);
        }

        var reloadedMappings = new LearnedMappingService(mappingsPath);
        reloadedMappings.Load();
        Assert.Single(reloadedMappings.Snapshot(LearnedMappingKind.Link));
        Assert.Empty(reloadedMappings.Snapshot(LearnedMappingKind.Workflow));
        Assert.Equal(
            "https://example.org/new",
            reloadedMappings.FindExact(LearnedMappingKind.Link, "open my website")?.Target);
        Assert.Null(reloadedMappings.FindExact(LearnedMappingKind.Link, "open unsafe target"));

        using var restartedConversation = new JarvisAutomationService(
            new NotificationService(null),
            new SettingsService(settingsPath),
            new JarvisActionHistoryService(Path.Combine(_root, "restarted-history.json")),
            reloadedMappings,
            () => { });
        Assert.True(restartedConversation.TryResolveDurableOpenPreference(
            "Open my website",
            out var resolvedTool,
            out var arguments));
        Assert.Equal("open_app", resolvedTool);
        Assert.Equal("open my website", arguments.GetProperty("app_name").GetString());
        Assert.Equal("auto", arguments.GetProperty("target_kind").GetString());
    }

    [Theory]
    [InlineData("Remember to open this whenever I say open my website.", "open my website")]
    [InlineData("Save the selected link under the phrase launch team portal", "launch team portal")]
    [InlineData("Remember this mapping: when I say company dashboard, visit this URL", "company dashboard")]
    [InlineData("Create an alias whenever I say open reports: go to the selected website", "open reports")]
    [InlineData("Remember this as my website", "my website")]
    [InlineData("Please remember that as our portal.", "our portal")]
    [InlineData("Save this link as team dashboard", "team dashboard")]
    [InlineData("Remember the selected URL as research home", "research home")]
    [InlineData("Whenever I say open my website, open the selected link. Remember this.", "open my website")]
    [InlineData("When I say team portal, open this URL. Save that.", "team portal")]
    [InlineData("Remember whenever I say open my website, open the selected link.", "open my website")]
    [InlineData("Whenever I say open my paradise link, open this.", "open my paradise link")]
    [InlineData("When I say team portal, open this URL.", "team portal")]
    [InlineData("Please whenever I say launch reports: visit the selected website.", "launch reports")]
    [InlineData("Whenever I say our dashboard; please navigate to that.", "our dashboard")]
    [InlineData("When I say team home, go to the selected link.", "team home")]
    public void SelectedLinkPreferenceExtractsOneDurableTrigger(string request, string expectedAlias)
    {
        Assert.True(JarvisAutomationService.TryParseSelectedLinkPreference(request, out var alias));
        Assert.Equal(expectedAlias, alias);
    }

    [Theory]
    [InlineData("Open this when I say open my website")]
    [InlineData("Remember to open this just once when I say open my website")]
    [InlineData("Remember this selected paragraph")]
    [InlineData("Whenever I say morning setup, open this and then launch Notepad")]
    [InlineData("Remember to open https://example.com whenever I say open my website")]
    [InlineData("Save this as budget.docx")]
    [InlineData("Remember this as my website and then open Notepad")]
    [InlineData("Open this")]
    [InlineData("Don't remember whenever I say team portal, open this")]
    [InlineData("Explain whenever I say team portal, open this")]
    [InlineData("Whenever I say team portal, open this just once")]
    [InlineData("Whenever I say team portal, open this for now")]
    [InlineData("Whenever I say team portal, open this and then launch Notepad")]
    [InlineData("Whenever I say team portal, open https://example.org/")]
    [InlineData("Whenever I say team portal, open the selected folder")]
    [InlineData("Whenever I say , open this")]
    public void SelectedLinkPreferenceDoesNotStealConversationWorkflowOrExplicitUrlOwnership(string request)
    {
        Assert.False(JarvisAutomationService.TryParseSelectedLinkPreference(request, out _));
    }

    [Fact]
    public void Selected_link_trigger_keeps_existing_alias_length_and_multi_action_limits()
    {
        Assert.False(JarvisAutomationService.TryParseSelectedLinkPreference(
            $"Whenever I say {new string('x', 161)}, open this.", out _));
        Assert.False(JarvisAutomationService.TryParseSelectedLinkPreference(
            "Whenever I say team portal and then open Notepad, open this.", out _));
    }

    [Theory]
    [InlineData("app", "the selected link")]
    [InlineData("auto", "this website")]
    [InlineData("folder", "the selected folder")]
    [InlineData("auto", "use_selected")]
    public void A_transient_selection_reference_cannot_be_saved_as_a_literal_workflow_target(string kind, string target)
    {
        var settings = new SettingsService(Path.Combine(_root, "unresolved-settings.json"));
        settings.Load();
        Assert.Throws<InvalidOperationException>(() => settings.SaveWorkflow(
            "open audit workspace", ["open audit workspace"],
            [new JarvisWorkflowActionSettings { Kind = kind, Target = target }]));
        Assert.Empty(settings.Current.JarvisWorkflows);
    }

    [Theory]
    [InlineData("Whenever I say open my website, open the selected link. Remember this.")]
    [InlineData("Whenever I say open my website, open the selected link.")]
    public void Workflow_parser_does_not_steal_a_selected_link_save_from_the_Link_capture_owner(string request)
    {
        var settings = new SettingsService(Path.Combine(_root, "selected-owner-settings.json"));
        settings.Load();
        using var automation = new JarvisAutomationService(new NotificationService(null), settings,
            new JarvisActionHistoryService(Path.Combine(_root, "selected-history.json")),
            new LearnedMappingService(Path.Combine(_root, "selected-mappings.json")), () => { });
        Assert.False(automation.TryRememberWorkflowFromTranscript(
            request, true, out _));
        Assert.Empty(settings.Current.JarvisWorkflows);
    }

    [Fact]
    public void TriggerFirstDurableLinkParserLeavesMultiActionRoutineToWorkflowOwnership()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });
        const string request = "Whenever I say begin review, open https://example.com/review and then open https://example.org/notes. Remember this.";

        Assert.False(automation.TryApplyDurablePreferenceFromTranscript(request, out _, out _));
        Assert.True(automation.TryRememberWorkflowFromTranscript(
            request,
            completeTurn: true,
            out var workflow));
        Assert.True((bool)workflow["ok"]);
        Assert.Single(settings.Current.JarvisWorkflows);
    }

    [Fact]
    public void TriggerFirstLinkShapeWithoutPersistenceIntentIsNotRemembered()
    {
        Directory.CreateDirectory(_root);
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            new SettingsService(Path.Combine(_root, "settings.json")),
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        Assert.False(automation.TryApplyDurablePreferenceFromTranscript(
            "When I say open the report, open https://example.com/report.",
            out _,
            out _));
        Assert.Empty(mappings.Snapshot());
    }

    [Fact]
    public void ExplicitCorrectionWithTrailingFromNowOnUsesRecentTarget()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        using var conversation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(historyPath),
            mappings,
            () => { });

        typeof(JarvisAutomationService)
            .GetMethod(
                "RememberTransientOpenTarget",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(conversation, new object[] { "Discord" });
        Assert.True(conversation.TryApplyDurablePreferenceFromTranscript(
            "Always use the web version instead from now on.",
            out var tool,
            out var saved));
        Assert.Equal("remember_link_alias", tool);
        Assert.True((bool)saved["ok"]);
        Assert.Equal(
            "https://discord.com/app",
            mappings.FindExact(LearnedMappingKind.Link, "discord")?.Target);
    }

    [Fact]
    public void ExplicitWholeWorkflowDeletionUsesAuthoritativeStoreAndTombstone()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.SaveWorkflow(
            "release ducks setup",
            new[] { "release ducks setup", "release morning ducks" },
            new[] { new JarvisWorkflowActionSettings { Kind = "url", Target = "https://example.com/" } });
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(
            LearnedMappingKind.Workflow,
            "release morning ducks",
            "release ducks setup",
            "release ducks setup");
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        Assert.True(automation.TryEditWorkflowFromTranscript(
            "Delete my release ducks setup workflow.",
            out var result));
        Assert.True((bool)result["ok"]);
        Assert.Empty(settings.Current.JarvisWorkflows);
        Assert.True(settings.IsWorkflowDeleted("release ducks setup"));
        Assert.True(settings.IsWorkflowDeleted("release morning ducks"));
        Assert.Empty(mappings.Snapshot(LearnedMappingKind.Workflow));
    }

    [Fact]
    public void AmbiguousOpenUsesDurablePreferenceButExplicitCurrentChoiceWins()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        mappings.RememberExclusive(
            LearnedMappingKind.Link,
            "Discord",
            "https://discord.com/app",
            "Discord web",
            new[]
            {
                LearnedMappingKind.App,
                LearnedMappingKind.Link,
                LearnedMappingKind.File,
                LearnedMappingKind.Folder,
                LearnedMappingKind.Browser,
            });

        using var conversation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(historyPath),
            mappings,
            () => { });
        Assert.True(conversation.TryResolveDurableOpenPreference(
            "open Discord",
            out var tool,
            out var arguments));
        Assert.Equal("open_app", tool);
        Assert.Equal("Discord", arguments.GetProperty("app_name").GetString());
        Assert.Equal("auto", arguments.GetProperty("target_kind").GetString());

        Assert.False(conversation.TryResolveDurableOpenPreference(
            "open Discord app",
            out _,
            out _));
        Assert.False(conversation.TryResolveDurableOpenPreference(
            "open Discord on the web this time",
            out _,
            out _));

        mappings.RememberExclusive(
            LearnedMappingKind.App,
            "ChatGPT",
            "appx:OpenAI.ChatGPT_test!App",
            "ChatGPT",
            new[]
            {
                LearnedMappingKind.App,
                LearnedMappingKind.Link,
                LearnedMappingKind.File,
                LearnedMappingKind.Folder,
                LearnedMappingKind.Browser,
            });
        Assert.True(conversation.TryResolveDurableOpenPreference(
            "open ChatGPT",
            out tool,
            out arguments));
        Assert.Equal("open_app", tool);
        Assert.Equal("ChatGPT", arguments.GetProperty("app_name").GetString());
        Assert.Equal("native", arguments.GetProperty("target_kind").GetString());
    }

    [Theory]
    [InlineData("open atlas dashboard", "Open atlas dashboard.")]
    [InlineData("use observatory console", "Please use observatory console!")]
    public void DurableSpokenCommandAliasResolvesAfterReload(string savedAlias, string request)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mappings.json");
        var mappings = new LearnedMappingService(path);
        mappings.Remember(LearnedMappingKind.Link, savedAlias, "https://example.com/workspace");
        var reloaded = new LearnedMappingService(path);
        reloaded.Load();
        using var conversation = new JarvisAutomationService(
            new NotificationService(null),
            new SettingsService(Path.Combine(_root, "settings.json")),
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            reloaded,
            () => { });

        Assert.True(conversation.TryResolveDurableOpenPreference(request, out var tool, out var arguments));
        Assert.Equal("open_app", tool);
        Assert.Equal(savedAlias, arguments.GetProperty("app_name").GetString());
        Assert.Equal("auto", arguments.GetProperty("target_kind").GetString());
        Assert.False(conversation.TryResolveDurableOpenPreference(
            request.TrimEnd('.', '!') + " app", out _, out _));
        Assert.False(conversation.TryResolveDurableOpenPreference(
            "What do you think about " + savedAlias + "?", out _, out _));
    }

    [Fact]
    public async Task WorkflowEditsUpdateOneAuthoritativeRecordAndSurviveFreshConversations()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            Assert.True(firstConversation.TryRememberWorkflowFromTranscript(
                "Whenever I say duck duck setup, open Gmail and Devpost. Remember this.",
                completeTurn: true,
                out var saved));
            Assert.True((bool)saved["ok"]);

            Assert.True(firstConversation.TryEditWorkflowFromTranscript(
                "Add Instagram to my duck duck setup.",
                out var added));
            Assert.True((bool)added["ok"], added["message"]?.ToString());
            Assert.Equal(3, settings.ReadPersistedWorkflow("duck duck setup").Actions.Count);
        }

        var secondSettings = new SettingsService(settingsPath);
        secondSettings.Load();
        var secondMappings = new LearnedMappingService(mappingsPath);
        secondMappings.Load();
        using (var secondConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   secondSettings,
                   new JarvisActionHistoryService(historyPath),
                   secondMappings,
                   () => { }))
        {
            Assert.True(secondConversation.TryResolveSavedWorkflowTrigger("duck duck setup", out _));
            Assert.True(secondConversation.TryEditWorkflowFromTranscript(
                "Remove Devpost from that workflow.",
                out var removed));
            Assert.True((bool)removed["ok"]);
            Assert.True(secondConversation.TryEditWorkflowFromTranscript(
                "Change Gmail to Google Sheets in that workflow.",
                out var replaced));
            Assert.True((bool)replaced["ok"]);
            Assert.True(secondConversation.TryEditWorkflowFromTranscript(
                "Add alias morning ducks to my duck duck setup.",
                out var aliased));
            Assert.True((bool)aliased["ok"]);
        }

        var thirdSettings = new SettingsService(settingsPath);
        thirdSettings.Load();
        thirdSettings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var persisted = thirdSettings.ReadPersistedWorkflow("morning ducks");
        Assert.NotNull(persisted);
        Assert.Collection(
            persisted.Actions,
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://docs.google.com/spreadsheets/", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://www.instagram.com/", action.Target);
            });
        Assert.DoesNotContain("Devpost", persisted.Apps + persisted.Urls, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("morning ducks", persisted.Aliases, StringComparer.OrdinalIgnoreCase);

        var thirdMappings = new LearnedMappingService(mappingsPath);
        thirdMappings.Load();
        using var thirdConversation = new JarvisAutomationService(
            new NotificationService(null),
            thirdSettings,
            new JarvisActionHistoryService(historyPath),
            thirdMappings,
            () => { });
        Assert.True(thirdConversation.TryResolveSavedWorkflowTrigger("morning ducks", out var canonical));
        Assert.Equal("duck duck setup", canonical, ignoreCase: true);
        thirdConversation.ResetConversationTransientContext();
        Assert.False(thirdConversation.TryEditWorkflowFromTranscript(
            "Add GitHub to that workflow.",
            out _));

        var deleted = await thirdConversation.ExecuteAsync(
            "delete_workflow",
            JsonSerializer.SerializeToElement(new { name = "morning ducks" }),
            CancellationToken.None);
        Assert.True((bool)deleted["ok"]);
        var afterDeleteSettings = new SettingsService(settingsPath);
        afterDeleteSettings.Load();
        var afterDeleteMappings = new LearnedMappingService(mappingsPath);
        afterDeleteMappings.Load();
        using var afterDeleteConversation = new JarvisAutomationService(
            new NotificationService(null),
            afterDeleteSettings,
            new JarvisActionHistoryService(historyPath),
            afterDeleteMappings,
            () => { });
        Assert.False(afterDeleteConversation.TryResolveSavedWorkflowTrigger("morning ducks", out _));
        Assert.False(afterDeleteConversation.TryResolveSavedWorkflowTrigger("duck duck setup", out _));
        Assert.True(afterDeleteConversation.IsDeletedWorkflowTrigger("morning ducks"));
    }

    [Fact]
    public void WebPageMutationLanguageIsNotMistakenForAWorkflowEdit()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        Assert.False(automation.TryEditWorkflowFromTranscript(
            "Add the first result to my Amazon cart.",
            out _));
        Assert.False(automation.TryEditWorkflowFromTranscript(
            "Remove the selected file from Downloads.",
            out _));
        Assert.False(automation.TryEditWorkflowFromTranscript(
            "Change the destination to Mumbai in the current page.",
            out _));
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
}
