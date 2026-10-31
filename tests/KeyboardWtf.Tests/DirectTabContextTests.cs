using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class DirectTabContextTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly BrowserHelperBridge _bridge = new();
    private readonly SettingsService _settings = new();
    private readonly LearnedMappingService _mappings;
    private readonly JarvisAutomationService _automation;
    private readonly List<(string Url, string Browser)> _simpleLaunches = [];
    private SelectionContext _browserTabForeground =
        new(new IntPtr(17), "chrome", "Example - Google Chrome");
    private bool _contentScriptAvailable = true;

    public DirectTabContextTests()
    {
        _settings.Current.PreferredBrowser = "chrome";
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        _mappings = new(Path.Combine(_directory, "mappings.json"));
        _automation = new(null, _settings, new(Path.Combine(_directory, "history.json")),
            _mappings, () => { }, null, null, _bridge,
            browserTabForeground: () => _browserTabForeground,
            simpleUrlLauncher: (url, browser) =>
            {
                if (!BrowserLauncherService.TryNormalizeUrl(url, out _))
                    return (false, "Invalid web destination.", "");
                _simpleLaunches.Add((url, browser));
                var chosen = BrowserLauncherService.NormalizeBrowser(browser);
                if (chosen.Length == 0) chosen = BrowserLauncherService.NormalizeBrowser(_settings.Current.PreferredBrowser);
                return (true, "Opened requested website.", chosen.Length == 0 ? "default browser" : chosen);
            });
        Process("hello");
    }

    [Fact]
    public async Task Link_resolved_through_app_or_path_keeps_actual_tab_identity_not_an_app_alias()
    {
        _mappings.Remember(LearnedMappingKind.Link, "my research", "https://example.org/research");
        foreach (var tool in new[] { "open_app", "open_path" })
        {
            var opening = Execute(tool, new { app_name = "my research", path = "my research" });
            var command = await NextCommand(opening);
            Assert.Equal("https://example.org/research", command.GetProperty("url").GetString());
            Reply(command, 11);
            var target = JsonSerializer.SerializeToElement(await opening).GetProperty("referential_target");
            Assert.Equal("browser_tab", target.GetProperty("kind").GetString());
            Assert.Equal("https://example.org/research", target.GetProperty("target").GetString());
        }
    }

    [Theory]
    [InlineData("Open my website.")]
    [InlineData("Okay, open my website.")]
    [InlineData("Can you open my website?")]
    [InlineData("Could you please open my website?")]
    [InlineData("Okay, can you open my website?")]
    public async Task Exact_saved_Link_beats_model_workflow_guess_even_with_a_legacy_trigger_collision(string userTurn)
    {
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my website", Aliases = ["open my website"],
            Actions = [new() { Kind = "auto", Target = "the selected link" }],
        }];
        _mappings.Remember(LearnedMappingKind.Link, "open my website", "https://example.org/saved");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript(userTurn, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("run_workflow",
            JsonSerializer.SerializeToElement(new { name = "open my website" }), CancellationToken.None));
        var command = await NextCommand(operation);
        Assert.Equal("https://example.org/saved", command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal(true, (await operation)["ok"]);
        Assert.Equal("the selected link", Assert.Single(Assert.Single(_settings.Current.JarvisWorkflows).Actions).Target);
        AssertNoCommand();
        var duplicate = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/guessed" }), CancellationToken.None);
        Assert.Equal(true, duplicate["duplicate_suppressed"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("chat service", "Open Gmail and chat service.")]
    [InlineData("team portal", "Open Gmail and team portal.")]
    public async Task Url_tool_bare_named_target_keeps_the_exact_saved_Link_in_a_compound_turn(string name, string turn)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open " + name, "https://example.org/saved");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript(turn, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = name }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://mail.google.com/", 11), ("https://example.org/saved", 12) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open my code project.")]
    [InlineData("Open GitHub and my code project.")]
    [InlineData("Open GitHub issues.")]
    [InlineData("Open the GitHub app.")]
    [InlineData("Open another website.")]
    [InlineData("Open Devpost.")]
    [InlineData("Open https://github.com/example/project.")]
    [InlineData("Tell me about GitHub.")]
    [InlineData("Don't open GitHub.")]
    public void Known_site_correction_does_not_replace_explicit_project_qualified_or_unrelated_intent(string turn)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://github.com/example/project");
        Assert.False(_automation.TryResolveRequestedKnownWebOpen(turn, "open_url", "open my code project", out _));
        AssertNoCommand();
    }

    [Fact]
    public void Known_site_correction_never_overrides_an_exact_saved_site_preference()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open GitHub", "https://github.com/my-preferred-page");
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://github.com/example/project");
        Assert.False(_automation.TryResolveRequestedKnownWebOpen("Open Gmail and GitHub.",
            "open_url", "open my code project", out _));
        AssertNoCommand();
    }

    [Fact]
    public async Task Legacy_workflow_with_an_unresolved_selection_fails_before_executing_any_step()
    {
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "legacy test", Aliases = ["legacy test"], Actions =
            [
                new() { Kind = "url", Target = "https://example.org/" },
                new() { Kind = "auto", Target = "the selected link" },
            ],
        }];
        var result = await Execute("run_workflow", new { name = "legacy test" });
        Assert.Equal(false, result["ok"]);
        Assert.Contains("no action was executed", result["message"].ToString());
        AssertNoCommand();
        Assert.Equal(2, Assert.Single(_settings.Current.JarvisWorkflows).Actions.Count);
    }

    [Theory]
    [InlineData("Okay, open reliability audit website.", "open_url", "open my website")]
    [InlineData("Okay, open reliability audit website.", "run_workflow", "begin reliability audit routine")]
    [InlineData("Okay, open reliability audit website.", "run_workflow", "reliability audit")]
    [InlineData("Open Gmail and reliability audit website.", "open_url", "open my website")]
    [InlineData("Open Gmail and reliability audit website.", "run_workflow", "begin reliability audit routine")]
    [InlineData("Okay, open recovery paradise link.", "open_url", "https://beginnersparadise.com/recovery")]
    [InlineData("Open my research link.", "open_url", "https://example.org/invented")]
    public async Task Deleted_named_Link_cannot_authorize_a_different_saved_identity(
        string userTurn, string tool, string proposedName)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open reliability audit website", "https://example.org/deleted");
        _mappings.Remember(LearnedMappingKind.Link, "open my website", "https://example.org/unrequested");
        Assert.True(_mappings.Forget("open reliability audit website", LearnedMappingKind.Link));
        _mappings.Load();
        Assert.Null(_mappings.FindExact(LearnedMappingKind.Link, "open reliability audit website"));
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "begin reliability audit routine", Aliases = ["begin reliability audit routine"],
            Actions = [new() { Kind = "url", Target = "https://example.org/unrequested-workflow" }],
        }];
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript(userTurn, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync(tool,
            JsonSerializer.SerializeToElement(new { name = proposedName, url = proposedName }), cancellation.Token));

        // Drain only the fake in-process Helper so a failing assertion cannot
        // leave a blocked tool task or open a real browser during this probe.
        var queuedActions = 0;
        while (!operation.IsCompleted && !cancellation.IsCancellationRequested)
        {
            var command = Process("poll").GetProperty("command");
            if (command.ValueKind != JsonValueKind.Null)
                Reply(command, 100 + ++queuedActions);
            await Task.Delay(10);
        }
        var result = await operation;
        Assert.Equal(0, queuedActions);
        Assert.Equal(false, result["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open team portal.", "open_url", "open team portal", false)]
    [InlineData("Okay, open team portal and Gmail.", "open_url", "open team portal", false)]
    [InlineData("Can you please open team portal?", "open_url", "open team portal", false)]
    [InlineData("Open https://example.org/portal.", "open_url", "open team portal", false)]
    [InlineData("Open an unknown portal.", "open_url", "open team portal", true)]
    [InlineData("Open Gmail and an unknown portal.", "open_url", "open team portal", true)]
    [InlineData("Open launch test collection.", "run_workflow", "launch test collection", false)]
    [InlineData("Open saved collection.", "run_workflow", "launch test collection", false)]
    [InlineData("Open quick collection.", "run_workflow", "launch test collection", false)]
    [InlineData("Open research and writing.", "run_workflow", "launch test collection", false)]
    [InlineData("Open missing collection.", "run_workflow", "launch test collection", true)]
    [InlineData("Run launch test collection.", "run_workflow", "launch test collection", false)]
    [InlineData("Open the workflow launch test collection.", "run_workflow", "launch test collection", false)]
    [InlineData("Open that.", "open_url", "open team portal", false)]
    [InlineData("Open the website you just saved.", "open_url", "open team portal", false)]
    [InlineData("Open the selected link.", "open_url", "open team portal", false)]
    [InlineData("Open the portal app.", "open_url", "open team portal", false)]
    [InlineData("What does open team portal mean?", "open_url", "open team portal", false)]
    [InlineData("Don't open team portal.", "open_url", "open team portal", false)]
    [InlineData("Open an unknown portal.", "open_url", "https://example.org/new", false)]
    [InlineData("Open an unknown portal.", "web_search", "open team portal", false)]
    [InlineData("Okay, open missing research link.", "open_url", "https://example.org/invented", true)]
    [InlineData("Could you please open my website?", "open_url", "https://example.org/invented", true)]
    [InlineData("Open team link.", "open_url", "https://example.org/portal", false)]
    [InlineData("Open team link.", "open_url", "https://example.org/guessed", true)]
    [InlineData("Open Gmail link.", "open_url", "https://mail.google.com/mail/u/0/", false)]
    [InlineData("Open the selected link.", "open_url", "https://example.org/new", false)]
    [InlineData("Open the link you just found.", "open_url", "https://example.org/new", false)]
    [InlineData("Open example.org/link.", "open_url", "https://example.org/link", false)]
    [InlineData("Open Google and team link.", "open_url", "https://www.google.com/", false)]
    [InlineData("Open Google, team link.", "open_url", "https://www.google.com/", false)]
    [InlineData("Open Google; team link.", "open_url", "https://www.google.com/", false)]
    [InlineData("Open research and writing link.", "open_url", "https://example.org/research", false)]
    public void Saved_identity_guard_preserves_exact_names_explicit_owners_and_referents(
        string turn, string tool, string target, bool rejected)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open team portal", "https://example.org/portal");
        _mappings.Remember(LearnedMappingKind.Link, "open team link", "https://example.org/portal");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "launch test collection", Aliases = ["saved collection", "research and writing"],
            Actions = [new() { Kind = "url", Target = "https://example.org/collection" }],
        }];
        _mappings.Remember(LearnedMappingKind.Workflow, "quick collection", "launch test collection");
        Assert.Equal(rejected, _automation.IsUnrequestedSavedIdentityInNamedOpen(turn, tool, target));
        AssertNoCommand();
    }

    [Fact]
    public void Named_Link_URL_authority_follows_save_edit_delete_and_recreate_without_a_stale_cache()
    {
        const string name = "open my reference link";
        const string first = "https://example.org/first";
        const string second = "https://example.org/second";
        _mappings.Remember(LearnedMappingKind.Link, name, first);
        _mappings.Load();
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", first));
        _mappings.Remember(LearnedMappingKind.Link, name, second);
        _mappings.Load();
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", second));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", first));
        Assert.True(_mappings.Forget(name, LearnedMappingKind.Link));
        _mappings.Load();
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", second));
        _mappings.Remember(LearnedMappingKind.Link, name, first);
        _mappings.Load();
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", first));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", second));
        AssertNoCommand();
    }

    [Fact]
    public async Task Legacy_Link_edit_and_delete_resolve_the_same_command_identity_as_invocation()
    {
        const string legacy = "can you please open the reference link";
        var original = _mappings.Remember(LearnedMappingKind.Link, legacy, "https://example.org/first");
        _mappings.Load();
        var edited = await Execute("remember_link_alias", new
        {
            alias = "open the reference link", url = "https://example.org/edited",
        });
        Assert.Equal(true, edited["ok"]);
        _mappings.Load();
        var saved = Assert.Single(_mappings.Snapshot(LearnedMappingKind.Link));
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("https://example.org/edited", saved.Target);
        Assert.True(_automation.TryResolveDurableOpenPreference("Okay, open the reference link.", out _, out _));

        var deleted = await Execute("forget_learned_mapping", new { id_or_alias = "the reference link", kind = "Link" });
        Assert.Equal(true, deleted["ok"]);
        _mappings.Load();
        Assert.Empty(_mappings.Snapshot(LearnedMappingKind.Link));
        Assert.False(_automation.TryResolveDurableOpenPreference("Open the reference link.", out _, out _));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(
            "Open the reference link.", "open_url", "https://example.org/edited"));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("run_workflow")]
    [InlineData("open_url")]
    [InlineData("open_app")]
    public async Task Legacy_polite_workflow_natural_invocation_runs_once_even_when_model_chooses_wrong_kind(string proposedTool)
    {
        const string legacy = "can you please open the reference link";
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = legacy, Aliases = [legacy],
            Actions = [new() { Kind = "url", Target = "https://example.org/reference" }],
        }];
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Okay, open the reference link.", CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync(proposedTool,
            JsonSerializer.SerializeToElement(new { name = legacy, url = "https://example.org/guessed", app_name = "reference link" }), CancellationToken.None));
        var command = await NextCommand(operation);
        Assert.Equal("https://example.org/reference", command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open the reference link.")]
    [InlineData("Okay, open the reference link.")]
    [InlineData("Could you please open the reference link?")]
    public void Legacy_polite_saved_identity_uses_the_same_open_grammar_as_the_current_request(string request)
    {
        const string legacy = "can you please open the reference link";
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = legacy, Aliases = [legacy],
            Actions = [new() { Kind = "url", Target = "https://example.org/reference" }],
        }];
        _mappings.Remember(LearnedMappingKind.Workflow, legacy, legacy);
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "run_workflow", legacy));
        Assert.True(_automation.TryResolveSavedWorkflowTrigger(request, out var workflow));
        Assert.Equal(legacy, workflow);
        _mappings.Remember(LearnedMappingKind.Link, legacy, "https://example.org/reference");
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", legacy));
        Assert.True(_automation.TryResolveDurableOpenPreference(request, out _, out _));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open another reference link.", "run_workflow", legacy));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open reference.", "open_url", legacy));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open my beginners paradise link.")]
    [InlineData("Open the beginners paradise link.")]
    [InlineData("Open beginners paradise link.")]
    public void Legacy_saved_reference_accepts_optional_current_determiner_without_losing_the_object(string request)
    {
        const string saved = "can you please open the beginners paradise link";
        const string url = "https://example.org/beginners";
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = saved, Aliases = [saved],
            Actions = [new() { Kind = "url", Target = url }],
        }];
        _mappings.Remember(LearnedMappingKind.Workflow, saved, saved);
        Assert.True(_automation.TryResolveSavedWorkflowTrigger(request, out var name));
        Assert.Equal(saved, name);
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "run_workflow", saved));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", url));
        Assert.False(_automation.TryResolveSavedWorkflowTrigger("Open beginners paradise project.", out _));
        AssertNoCommand();
    }

    [Fact]
    public void Explicit_Link_intent_selects_current_Link_over_a_legacy_different_workflow_destination()
    {
        const string link = "open my harbor light link";
        const string routine = "can you please open the harborlight link";
        _mappings.Remember(LearnedMappingKind.Link, link, "https://example.org/current");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = routine, Aliases = [routine],
            Actions = [new() { Kind = "url", Target = "https://example.org/legacy" }],
        }];
        Assert.True(_automation.TryResolveDurableOpenPreference("Open the harbor light link.", out _, out _));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open the harbor light link.", "open_url", "https://example.org/current"));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open the harbor light link.", "run_workflow", routine));
        Assert.True(_automation.TryResolveSavedWorkflowTrigger("Run my harbor light link workflow.", out var name));
        Assert.Equal(routine, name);
        AssertNoCommand();
    }

    [Fact]
    public void Explicit_workflow_intent_uses_the_saved_workflow_even_when_a_Link_has_the_same_identity()
    {
        const string name = "research setup";
        _mappings.Remember(LearnedMappingKind.Link, name, "https://example.org/link");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = name, Actions = [new() { Kind = "url", Target = "https://example.org/workflow" }],
        }];
        Assert.True(_automation.TryResolveSavedWorkflowTrigger("Run my research setup workflow.", out var actual));
        Assert.Equal(name, actual);
        Assert.False(_automation.TryResolveDurableOpenPreference("Run my research setup workflow.", out _, out _));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open Devpost.", false)]
    [InlineData("Open Gmail and Devpost.", false)]
    [InlineData("Open my Devpost project.", true)]
    [InlineData("Open Gmail and my Devpost project.", true)]
    public void Generic_catalog_name_and_specific_saved_project_keep_distinct_complete_objects(string request, bool project)
    {
        const string saved = "open my Devpost project";
        const string url = "https://devpost.com/software/user-project";
        _mappings.Remember(LearnedMappingKind.Link, saved, url);
        Assert.Equal(!project, _automation.TryResolveRequestedKnownWebOpen(request, "open_app", "Devpost", out var catalog));
        if (!project) Assert.Equal("https://devpost.com/", catalog);
        Assert.Equal(project, _automation.TryResolveNamedSavedLinkInCompoundOpen(request, "my Devpost project", out _));
        Assert.Equal(!project, _automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", saved));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open Devpost.", "https://devpost.com/")]
    [InlineData("Open Gmail and Devpost.", "https://devpost.com/")]
    [InlineData("Open my Devpost project.", "https://devpost.com/software/user-project")]
    [InlineData("Open Gmail and my Devpost project.", "https://devpost.com/software/user-project")]
    public async Task Model_URL_for_a_specific_saved_project_cannot_replace_the_complete_generic_site_object(string request, string expected)
    {
        const string url = "https://devpost.com/software/user-project";
        _mappings.Remember(LearnedMappingKind.Link, "open my Devpost project", url);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url }), CancellationToken.None));
        if (request == "Open Gmail and my Devpost project.")
        {
            var first = await NextCommand(operation);
            Assert.Equal("https://mail.google.com/", first.GetProperty("url").GetString());
            Reply(first, 10);
        }
        var command = await NextCommand(operation);
        Reply(command, 11);
        Assert.Equal(expected, command.GetProperty("url").GetString());
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open Devpost.", "https://devpost.com/")]
    [InlineData("Open Gmail and Devpost.", "https://devpost.com/")]
    [InlineData("Open my Devpost project.", "https://devpost.com/software/user-project")]
    [InlineData("Open Gmail and my Devpost project.", "https://devpost.com/software/user-project")]
    public async Task Model_saved_project_alias_cannot_replace_the_complete_generic_site_object(string request, string expected)
    {
        const string alias = "open my Devpost project";
        _mappings.Remember(LearnedMappingKind.Link, alias, "https://devpost.com/software/user-project");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = alias }), CancellationToken.None));
        if (request == "Open Gmail and my Devpost project.")
        {
            var first = await NextCommand(operation);
            Assert.Equal("https://mail.google.com/", first.GetProperty("url").GetString());
            Reply(first, 10);
        }
        var command = await NextCommand(operation);
        Reply(command, 11);
        Assert.Equal(expected, command.GetProperty("url").GetString());
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_alias_site_correction_preserves_rejection_native_intent_deletion_and_saved_generic_preference()
    {
        const string alias = "open my Devpost project";
        const string url = "https://devpost.com/software/user-project";
        _mappings.Remember(LearnedMappingKind.Link, alias, url);
        _mappings.Remember(LearnedMappingKind.Link, "open unrelated project", "https://example.org/");
        Assert.False(_automation.TryResolveRequestedKnownWebOpen("Open Devpost.", "open_url", "open unrelated project", out _));
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open Devpost.", CancellationToken.None);
        var rejected = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open unrelated project" }), CancellationToken.None);
        Assert.Equal(false, rejected["ok"]);
        AssertNoCommand();
        Assert.False(_automation.TryResolveRequestedKnownWebOpen("Open the Devpost app.", "open_url", alias, out _));
        _mappings.Forget(alias, LearnedMappingKind.Link);
        Assert.False(_automation.TryResolveRequestedKnownWebOpen("Open Devpost.", "open_url", alias, out _));
        AssertNoCommand();
        _mappings.Remember(LearnedMappingKind.Link, "open discord", "https://discord.com/channels/@me");
        commands.ObserveSubmittedJarvisText("Open Discord.", CancellationToken.None);
        var opening = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open discord" }), CancellationToken.None));
        var command = await NextCommand(opening);
        Reply(command, 11);
        Assert.Equal("https://discord.com/channels/@me", command.GetProperty("url").GetString());
        Assert.Equal(true, (await opening)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_valid_Link_is_not_blocked_by_an_empty_or_unresolved_legacy_workflow(bool unresolved)
    {
        const string saved = "open my research portal";
        _mappings.Remember(LearnedMappingKind.Link, saved, "https://example.org/current");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open the researchportal",
            Actions = unresolved ? [new() { Kind = "auto", Target = "the selected link" }] : [],
        }];
        Assert.True(_automation.TryResolveDurableOpenPreference("Open research portal.", out _, out _));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open research portal.", "open_url", saved));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Change my saved link open my reference verification link to https://example.org/resources.")]
    [InlineData("Update the saved link open the referenceverification link to https://example.org/resources.")]
    [InlineData("Replace my saved link reference verification link with https://example.org/resources.")]
    public async Task Explicit_saved_Link_edit_owns_a_model_workflow_guess_and_updates_the_same_record(string request)
    {
        const string alias = "open my reference verification link";
        var original = _mappings.Remember(LearnedMappingKind.Link, alias, "https://example.org/original");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = alias, Actions = [new() { Kind = "url", Target = "https://example.org/workflow" }],
        }];
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("edit_workflow",
            JsonSerializer.SerializeToElement(new { name = alias, operation = "replace_action", target = "model guess", replacement = "https://example.org/guess" }), CancellationToken.None);
        Assert.Equal(true, result["ok"]);
        Assert.Equal(true, result["persistence_verified"]);
        _mappings.Load();
        var updated = Assert.Single(_mappings.Snapshot(LearnedMappingKind.Link));
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(alias, updated.Alias);
        Assert.Equal("https://example.org/resources", updated.Target);
        Assert.Equal("https://example.org/workflow", Assert.Single(Assert.Single(_settings.Current.JarvisWorkflows).Actions).Target);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Change my saved link missing reference link to https://example.org/resources.")]
    [InlineData("Change my saved link reference verification link to not a URL.")]
    [InlineData("Change my saved link reference verification link to https://example.org/ and https://other.example/.")]
    public void Explicit_saved_Link_edit_rejects_missing_or_invalid_destination_without_mutation(string request)
    {
        var original = _mappings.Remember(LearnedMappingKind.Link, "open my reference verification link", "https://example.org/original");
        Assert.True(_automation.TryApplyDurablePreferenceFromTranscript(request, out var tool, out var result));
        Assert.Equal("remember_link_alias", tool);
        Assert.Equal(false, result["ok"]);
        _mappings.Load();
        var unchanged = Assert.Single(_mappings.Snapshot());
        Assert.Equal(original.Id, unchanged.Id);
        Assert.Equal(original.Target, unchanged.Target);
        AssertNoCommand();
    }

    [Fact]
    public void Explicit_saved_Link_edit_rejects_ambiguous_or_deleted_identity()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my reference light link", "https://example.org/first");
        _mappings.Remember(LearnedMappingKind.Link, "open the referencelight link", "https://example.org/second");
        var before = JsonSerializer.Serialize(_mappings.Snapshot());
        Assert.True(_automation.TryApplyDurablePreferenceFromTranscript(
            "Change my saved link reference light link to https://example.org/resources.", out _, out var ambiguous));
        Assert.Equal(false, ambiguous["ok"]);
        Assert.Equal(before, JsonSerializer.Serialize(_mappings.Snapshot()));
        _mappings.Clear();
        Assert.True(_automation.TryApplyDurablePreferenceFromTranscript(
            "Change my saved link reference light link to https://example.org/resources.", out _, out var deleted));
        Assert.Equal(false, deleted["ok"]);
        Assert.Empty(_mappings.Snapshot());
        AssertNoCommand();
    }

    [Fact]
    public async Task Explicit_saved_Link_edit_temporary_instruction_cannot_be_persisted_by_a_model_tool()
    {
        const string alias = "open my reference light link";
        var original = _mappings.Remember(LearnedMappingKind.Link, alias, "https://example.org/original");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(
            "Change my saved link reference light link to https://example.org/resources just this once.", CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("remember_link_alias",
            JsonSerializer.SerializeToElement(new { alias, url = "https://example.org/resources" }), CancellationToken.None);
        Assert.Equal(false, result["ok"]);
        _mappings.Load();
        var unchanged = Assert.Single(_mappings.Snapshot());
        Assert.Equal(original.Id, unchanged.Id);
        Assert.Equal(original.Target, unchanged.Target);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Claude", "https://claude.ai/")]
    [InlineData("Gemini", "https://gemini.google.com/")]
    [InlineData("ChatGPT", "https://chatgpt.com/")]
    public async Task Verified_service_catalog_handles_model_app_guesses_without_overriding_native_or_saved_intent(string name, string url)
    {
        var request = $"Open ChatGPT, Gemini and Claude.";
        Assert.True(_automation.TryResolveRequestedKnownWebOpen(request, "open_app", name + " app", out var resolved));
        Assert.Equal(url, resolved);
        Assert.False(_automation.TryResolveRequestedKnownWebOpen($"Open the {name} app.", "open_app", name, out _));
        Assert.False(_automation.TryResolveRequestedKnownWebOpen($"Do not open {name}.", "open_app", name, out _));
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_app",
            JsonSerializer.SerializeToElement(new { app_name = name + " app", target_kind = "native" }), CancellationToken.None));
        var command = await NextCommand(operation);
        Assert.Equal(url, command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
        _mappings.Remember(LearnedMappingKind.Link, "open " + name, "https://example.org/preferred");
        Assert.False(_automation.TryResolveRequestedKnownWebOpen(request, "open_app", name, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_auto_workflow_uses_verified_services_without_rewriting_stored_actions(bool savedOverride)
    {
        Assert.True(JarvisAutomationService.TryGetVerifiedWebAlternative("claude", true, out _));
        Assert.True(JarvisAutomationService.TryGetVerifiedWebAlternative("gemini", true, out _));
        Assert.False(JarvisAutomationService.TryGetVerifiedWebAlternative("unverified service", true, out _));
        var routine = new JarvisWorkflowSettings { Name = "initialize my research setup", Actions =
        [
            new() { Kind = "auto", Target = "Claude" },
            new() { Kind = "url", Target = "https://chatgpt.com/" },
            new() { Kind = "auto", Target = "Gemini" },
        ] };
        _settings.Current.JarvisWorkflows = [routine];
        var original = JsonSerializer.Serialize(routine);
        if (savedOverride) _mappings.Remember(LearnedMappingKind.Link, "Claude", "https://example.org/preferred");
        var operation = Execute("run_workflow", new { name = routine.Name });
        var expected = new[] { savedOverride ? "https://example.org/preferred" : "https://claude.ai/", "https://chatgpt.com/", "https://gemini.google.com/" };
        for (var index = 0; index < expected.Length; index++)
        {
            var command = await NextCommand(operation);
            Assert.Equal(expected[index], command.GetProperty("url").GetString());
            Reply(command, 11 + index);
        }
        Assert.Equal(true, (await operation)["ok"]);
        Assert.Equal(original, JsonSerializer.Serialize(routine));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open the HarborLight link.")]
    [InlineData("Okay, open my harborlight link.")]
    [InlineData("Could you please open my harbor light link?")]
    public async Task Unambiguous_saved_reference_uses_the_same_identity_at_lookup_and_URL_guard(string request)
    {
        const string saved = "open the harbor light link";
        const string url = "https://example.org/user-saved";
        _mappings.Remember(LearnedMappingKind.Link, saved, url);
        Assert.True(_automation.TryResolveDurableOpenPreference(request, out _, out _));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", saved));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", url));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", "https://example.org/guess"));
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/guess" }), CancellationToken.None));
        var command = await NextCommand(operation);
        Assert.Equal(url, command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("open the harbor light link", "my HarborLight link", true)]
    [InlineData("open the harbor light link", "my HarborLight link", false)]
    [InlineData("can you please open my code project", "the code project", true)]
    [InlineData("can you please open my code project", "the code project", false)]
    public async Task Saved_handoff_direct_URL_resolves_the_same_canonical_reference_at_the_action_boundary(
        string storedAlias, string proposedAlias, bool helperWindowPresent)
    {
        const string destination = "https://example.org/saved/project";
        _mappings.Remember(LearnedMappingKind.Link, storedAlias, destination);
        Process("hello", browserWindowPresent: helperWindowPresent);
        var opening = Execute("open_url", new { url = proposedAlias, browser = "chrome" });
        if (helperWindowPresent)
        {
            var command = await NextCommand(opening);
            Assert.Equal(destination, command.GetProperty("url").GetString());
            Reply(command, 11);
        }
        var result = JsonSerializer.SerializeToElement(await opening);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(destination, result.GetProperty("referential_target").GetProperty("target").GetString());
        if (helperWindowPresent) Assert.Empty(_simpleLaunches);
        else Assert.Equal((destination, "chrome"), Assert.Single(_simpleLaunches));
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_handoff_literal_alias_wins_but_conflicting_canonical_alias_does_not_launch()
    {
        const string firstAlias = "open my harbor light link";
        const string secondAlias = "open the harborlight link";
        _mappings.Remember(LearnedMappingKind.Link, firstAlias, "https://example.org/first");
        _mappings.Remember(LearnedMappingKind.Link, secondAlias, "https://example.org/second");
        Process("hello", browserWindowPresent: false);
        foreach (var (alias, destination) in new[]
                 { (firstAlias, "https://example.org/first"), (secondAlias, "https://example.org/second") })
        {
            var result = await Execute("open_url", new { url = alias });
            Assert.Equal(true, result["ok"]);
            Assert.Equal(destination, result["url"]);
        }
        var ambiguous = await Execute("open_url", new { url = "the harbor light link" });
        Assert.Equal(false, ambiguous["ok"]);
        Assert.Equal(2, _simpleLaunches.Count);
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_handoff_deleted_canonical_alias_cannot_use_a_stale_or_similar_destination()
    {
        const string deleted = "can you please open my code project";
        _mappings.Remember(LearnedMappingKind.Link, deleted, "https://example.org/deleted-project");
        Assert.True(_mappings.Forget(deleted, LearnedMappingKind.Link));
        _mappings.Load();
        _mappings.Remember(LearnedMappingKind.Link, "open my code link", "https://example.org/different-link");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my code project", Actions = [new() { Kind = "url", Target = "https://example.org/workflow" }],
        }];
        Process("hello", browserWindowPresent: false);
        var result = await Execute("open_url", new { url = "the code project" });
        Assert.Equal(false, result["ok"]);
        Assert.Empty(_simpleLaunches);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open HarborLight.")]
    [InlineData("Open my harbor light project.")]
    [InlineData("Open the harbor light link app.")]
    [InlineData("Open our harbor light link.")]
    [InlineData("Open my harbor lights link.")]
    public void Saved_reference_normalization_never_drops_object_qualifiers_or_guesses_spelling(string request)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open the harbor light link", "https://example.org/saved");
        Assert.False(_automation.TryResolveDurableOpenPreference(request, out _, out _));
        AssertNoCommand();
    }

    [Fact]
    public void Conflicting_saved_reference_variants_require_literal_identity_and_guard_the_selected_destination()
    {
        const string firstName = "open my harbor light link";
        const string secondName = "open the harborlight link";
        const string first = "https://example.org/first";
        const string second = "https://example.org/second";
        _mappings.Remember(LearnedMappingKind.Link, firstName, first);
        _mappings.Remember(LearnedMappingKind.Link, secondName, second);
        foreach (var (name, expected, rejected) in new[] { (firstName, first, second), (secondName, second, first) })
        {
            Assert.True(_automation.TryResolveDurableOpenPreference(name, out _, out _));
            Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", expected));
            Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(name, "open_url", rejected));
        }
        const string ambiguous = "Open the harbor light link.";
        Assert.False(_automation.TryResolveDurableOpenPreference(ambiguous, out _, out _));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(ambiguous, "open_url", firstName));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(ambiguous, "open_url", secondName));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(ambiguous, "open_url", first));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(ambiguous, "open_url", second));
    }

    [Fact]
    public async Task Saved_reference_variant_edit_reload_delete_has_no_stale_destination_or_similar_workflow_fallback()
    {
        const string saved = "open the harbor light link";
        var original = _mappings.Remember(LearnedMappingKind.Link, saved, "https://example.org/first");
        var edit = await Execute("remember_link_alias", new { alias = "open my harborlight link", url = "https://example.org/edited" });
        Assert.Equal(true, edit["ok"]);
        _mappings.Load();
        var updated = Assert.Single(_mappings.Snapshot(LearnedMappingKind.Link));
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("https://example.org/edited", updated.Target);
        Assert.True(_automation.TryResolveDurableOpenPreference("Open my HarborLight link.", out _, out _));
        var delete = await Execute("forget_learned_mapping", new { id_or_alias = "my harborlight link", kind = "Link" });
        Assert.Equal(true, delete["ok"]);
        _mappings.Load();
        Assert.Empty(_mappings.Snapshot(LearnedMappingKind.Link));
        _settings.Current.JarvisWorkflows = [new() { Name = "open the harbor light project", Actions = [new() { Kind = "url", Target = "https://example.org/project" }] }];
        Assert.False(_automation.TryResolveDurableOpenPreference("Open my HarborLight link.", out _, out _));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open my HarborLight link.", "open_url", "https://example.org/edited"));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open my HarborLight link.", "run_workflow", "open the harbor light project"));
    }

    [Fact]
    public void Legacy_workflow_reference_variants_keep_complete_object_identity()
    {
        const string stored = "can you please open the harbor light link";
        _settings.Current.JarvisWorkflows = [new() { Name = stored, Actions = [new() { Kind = "url", Target = "https://example.org/legacy" }] }];
        Assert.True(_automation.TryResolveSavedWorkflowTrigger("Open my HarborLight link.", out var name));
        Assert.Equal(stored, name);
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open my HarborLight link.", "run_workflow", stored));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open my HarborLight link.", "open_url", "https://example.org/legacy"));
        Assert.False(_automation.TryResolveSavedWorkflowTrigger("Open HarborLight.", out _));
    }

    [Fact]
    public void Explicit_Link_reference_selects_the_Link_while_untyped_cross_kind_collisions_remain_unresolved()
    {
        const string link = "open my harbor light link";
        const string routine = "open the harborlight link";
        _mappings.Remember(LearnedMappingKind.Link, link, "https://example.org/link");
        _settings.Current.JarvisWorkflows = [new() { Name = routine, Actions = [new() { Kind = "url", Target = "https://example.org/workflow" }] }];
        // Current "link" intent is more specific than an old workflow phrase;
        // an explicit workflow request still reaches its stored destination.
        Assert.True(_automation.TryResolveDurableOpenPreference(link, out _, out _));
        Assert.True(_automation.TryResolveDurableOpenPreference(routine, out _, out _));
        Assert.False(_automation.TryResolveSavedWorkflowTrigger(routine, out _));
        Assert.True(_automation.TryResolveSavedWorkflowTrigger("Run my harbor light link workflow.", out var actual));
        Assert.Equal(routine, actual);
        _mappings.Remember(LearnedMappingKind.Link, "open my reading room", "https://example.org/reading-link");
        _settings.Current.JarvisWorkflows.Add(new()
        {
            Name = "open the readingroom", Actions = [new() { Kind = "url", Target = "https://example.org/reading-workflow" }],
        });
        const string ambiguous = "Open reading room.";
        Assert.False(_automation.TryResolveDurableOpenPreference(ambiguous, out _, out _));
        Assert.False(_automation.TryResolveSavedWorkflowTrigger(ambiguous, out _));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(ambiguous, "open_url", "open my reading room"));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(ambiguous, "run_workflow", "open the readingroom"));
    }

    [Theory]
    [InlineData("open_app")]
    [InlineData("open_url")]
    [InlineData("run_workflow")]
    public async Task Exact_saved_setup_executes_its_ordered_actions_once_despite_a_model_guess(string tool)
    {
        const string name = "initialize my research setup";
        _settings.Current.JarvisWorkflows = [new() { Name = name, Aliases = [name], Actions =
        [
            new() { Kind = "url", Target = "https://example.org/first" },
            new() { Kind = "url", Target = "https://example.org/second" },
        ] }];
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(name, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync(tool,
            JsonSerializer.SerializeToElement(new { name = "model guess", app_name = "model guess", url = "https://example.org/guess" }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://example.org/first", 11), ("https://example.org/second", 12) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open Gmail, Instagram, Discord and my code project.", "my code project", true)]
    [InlineData("Open Gmail, Instagram, Discord and code.", "my code project", false)]
    [InlineData("Open Gmail, Instagram, Discord and my HarborLight link.", "my harborlight link", true)]
    public void Compound_saved_references_keep_the_complete_requested_object(string request, string proposed, bool expected)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://example.org/project");
        _mappings.Remember(LearnedMappingKind.Link, "open the harbor light link", "https://example.org/link");
        Assert.Equal(expected, _automation.TryResolveNamedSavedLinkInCompoundOpen(request, proposed, out _));
        AssertNoCommand();
    }

    [Fact]
    public void Copy_receipt_requires_actual_payload_readback_and_performs_only_one_write()
    {
        const string resultText = "Le texte original complet a été traduit.";
        var writes = 0;
        var clipboard = "old selected source";
        var falseSuccess = _automation.CopyText(resultText, text => { writes++; return true; }, () => clipboard);
        Assert.Equal(false, falseSuccess["ok"]);
        Assert.Equal(1, writes);
        Assert.Equal("old selected source", clipboard);
        writes = 0;
        var success = _automation.CopyText(resultText, text => { writes++; clipboard = text; return true; }, () => clipboard);
        Assert.Equal(true, success["ok"]);
        Assert.Equal(true, success["clipboard_verified"]);
        Assert.Equal(resultText.Length, success["character_count"]);
        Assert.Equal(1, writes);
        Assert.Equal(resultText, clipboard);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("run_workflow")]
    [InlineData("open_url")]
    public async Task Requested_saved_set_completes_three_destinations_from_one_model_proposal(string proposedTool)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open the harbor light link", "https://example.org/harbor");
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://example.org/project");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "can you please open the research garden link",
            Actions = [new() { Kind = "url", Target = "https://example.org/garden" }],
        }];
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open my HarborLight link, my code project and my research garden link.", CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync(proposedTool,
            JsonSerializer.SerializeToElement(new { name = "can you please open the research garden link", url = "can you please open the research garden link" }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://example.org/harbor", 11), ("https://example.org/project", 12), ("https://example.org/garden", 13) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        var result = JsonSerializer.SerializeToElement(await operation);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(3, result.GetProperty("resolved_count").GetInt32());
        Assert.Equal(3, result.GetProperty("referential_targets").GetArrayLength());
        AssertNoCommand();
        var duplicate = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/project" }), CancellationToken.None);
        Assert.Equal(true, duplicate["duplicate_suppressed"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Requested_saved_set_keeps_valid_items_and_asks_only_for_the_unresolved_name()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://example.org/project");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open my research link, my missing link and my code project.", CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open my research link" }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://example.org/research", 11), ("https://example.org/project", 12) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        var result = JsonSerializer.SerializeToElement(await operation);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("needs_clarification").GetBoolean());
        Assert.Equal("my missing link", Assert.Single(result.GetProperty("unresolved_names").EnumerateArray()).GetString());
        Assert.Equal(2, result.GetProperty("successful_count").GetInt32());
        var invented = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/invented" }), CancellationToken.None);
        Assert.Equal(true, invented["duplicate_suppressed"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("open unrelated portal")]
    [InlineData("https://example.org/unrelated")]
    public async Task Qualified_browser_compound_never_opens_an_unrequested_saved_destination_when_set_routing_declines(string proposed)
    {
        const string request = "Open my research link in Chrome and Gmail.";
        const string unrelated = "open unrelated portal";
        _mappings.Remember(LearnedMappingKind.Link, "open the research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, unrelated, "https://example.org/unrelated");
        Assert.False(_automation.TryResolveSavedOpenSet(request, out _));
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = proposed }), cancellation.Token));
        var queuedDestinations = new List<string>();
        // Drain only the fake Helper so a red assertion cannot leave the tool
        // blocked, and an incorrect proposal can never open a real browser.
        while (!operation.IsCompleted && !cancellation.IsCancellationRequested)
        {
            var command = Process("poll").GetProperty("command");
            if (command.ValueKind != JsonValueKind.Null)
            {
                queuedDestinations.Add(command.GetProperty("url").GetString());
                Reply(command, 100 + queuedDestinations.Count);
            }
            await Task.Delay(10);
        }
        var result = await operation;
        Assert.Empty(queuedDestinations);
        Assert.Equal(false, result["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open my research link in Chrome and https://example.org/unrelated.", "open unrelated portal", "https://example.org/unrelated")]
    [InlineData("Open my research link in Chrome and https://example.org/unrelated.", "https://example.org/unrelated", "https://example.org/unrelated")]
    [InlineData("Open my research link in Chrome and Gmail.", "open the research link", "https://example.org/research")]
    [InlineData("Open my research link in Chrome and Gmail.", "https://example.org/research", "https://example.org/research")]
    public async Task Qualified_saved_open_fallback_preserves_explicit_URL_and_requested_saved_destination(string request, string proposed, string expected)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open the research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open unrelated portal", "https://example.org/unrelated");
        Assert.False(_automation.TryResolveSavedOpenSet(request, out _));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", proposed));
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = proposed, browser = "chrome" }), CancellationToken.None));
        var command = await NextCommand(operation);
        Assert.Equal(expected, command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("open unrelated portal")]
    [InlineData("https://example.org/unrelated")]
    public void Browser_qualifier_preserves_single_saved_object_identity_authority(string proposed)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open the research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open unrelated portal", "https://example.org/unrelated");
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen("Open my research link in Chrome.", "open_url", proposed));
        AssertNoCommand();
    }

    [Fact]
    public void Qualified_saved_URL_authority_preserves_normalized_equivalent_targets_of_distinct_aliases()
    {
        const string requested = "open my research link";
        const string unrelated = "open unrelated portal";
        const string saved = "https://EXAMPLE.org:443/shared";
        _mappings.Remember(LearnedMappingKind.Link, requested, saved);
        _mappings.Remember(LearnedMappingKind.Link, unrelated, "https://example.org/shared");
        Assert.Equal(unrelated, _mappings.Snapshot(LearnedMappingKind.Link)[0].Alias);
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(
            "Open my research link in Chrome and Gmail.", "open_url", saved));
        AssertNoCommand();
    }

    [Fact]
    public void Legacy_single_URL_workflow_keeps_its_requested_effect_when_an_unrelated_Link_shares_the_destination()
    {
        const string url = "https://example.org/shared";
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my legacy link", Actions = [new() { Kind = "url", Target = url }],
        }];
        _mappings.Remember(LearnedMappingKind.Link, "open unrelated portal", url);
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(
            "Open my legacy link in Chrome.", "open_url", url));
        AssertNoCommand();
    }

    [Fact]
    public void Saved_URL_authority_rejects_a_different_fragment_of_the_same_page()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/page#one");
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(
            "Open my research link in Chrome.", "open_url", "https://example.org/page#two"));
        _mappings.Remember(LearnedMappingKind.Link, "open unrelated portal", "https://example.org/page#two");
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(
            "Open my research link in Chrome and https://example.org/page#one.", "open_url", "open unrelated portal"));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("my guide through Arc", "")]
    [InlineData("my guide through Arc", " and Gmail")]
    [InlineData("my guide in Chrome", "")]
    [InlineData("my guide in Chrome", " and Gmail")]
    public void Literal_saved_browser_words_keep_the_complete_requested_identity(string name, string continuation)
    {
        const string url = "https://example.org/guide";
        const string unrelated = "open unrelated portal";
        const string unrelatedUrl = "https://example.org/unrelated";
        var saved = "open " + name;
        var request = "Open " + name + continuation + ".";
        _mappings.Remember(LearnedMappingKind.Link, saved, url);
        _mappings.Remember(LearnedMappingKind.Link, unrelated, unrelatedUrl);
        Assert.False(_automation.TryResolveSavedOpenSet(request, out _));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", saved));
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", url));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", unrelated));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", unrelatedUrl));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open this link in Chrome.", "open unrelated portal")]
    [InlineData("Open this link in Chrome.", "https://example.org/unrelated")]
    [InlineData("Open the link you just found in Chrome.", "open unrelated portal")]
    [InlineData("Open the link you just found in Chrome.", "https://example.org/unrelated")]
    public void Qualified_contextual_link_keeps_its_existing_referent_owner(string request, string proposed)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open unrelated portal", "https://example.org/unrelated");
        Assert.False(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", proposed));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open Devpost in Chrome.", false)]
    [InlineData("Open my Devpost project in Chrome.", true)]
    public void Qualified_generic_site_and_saved_project_keep_distinct_requested_identities(string request, bool project)
    {
        const string saved = "open my Devpost project";
        const string url = "https://devpost.com/software/user-project";
        _mappings.Remember(LearnedMappingKind.Link, saved, url);
        Assert.Equal(!project, _automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", saved));
        Assert.Equal(!project, _automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", url));
        AssertNoCommand();
    }

    [Fact]
    public async Task Possessive_saved_compound_keeps_exact_identity_and_never_substitutes_an_unrequested_portal()
    {
        const string request = "Open my research link, my code project and my beginner's paradise link.";
        const string unresolved = "my beginner's paradise link";
        const string unrelated = "open unrelated portal";
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://example.org/project");
        _mappings.Remember(LearnedMappingKind.Link, "open my beginners paradise link", "https://example.org/paradise");
        _mappings.Remember(LearnedMappingKind.Link, unrelated, "https://example.org/unrelated");
        Assert.True(_automation.TryResolveSavedOpenSet(request, out var plan));
        Assert.Equal(3, plan.RequestedCount);
        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal(unresolved, Assert.Single(plan.UnresolvedNames));
        Assert.True(_automation.IsUnrequestedSavedIdentityInNamedOpen(request, "open_url", unrelated));
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = unrelated }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://example.org/research", 11), ("https://example.org/project", 12) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        var result = JsonSerializer.SerializeToElement(await operation);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("needs_clarification").GetBoolean());
        Assert.Equal(unresolved, Assert.Single(result.GetProperty("unresolved_names").EnumerateArray()).GetString());
        Assert.Equal(2, result.GetProperty("successful_count").GetInt32());
        AssertNoCommand();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requested_saved_set_preserves_generic_site_and_specific_project_authority(bool project)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my GitHub project", "https://github.com/user/project");
        _mappings.Remember(LearnedMappingKind.Link, "open Discord", "https://discord.com/channels/@me");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open Gmail, Instagram, Discord and " + (project ? "my GitHub project." : "GitHub."), CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_app",
            JsonSerializer.SerializeToElement(new { app_name = "Discord", target_kind = "native" }), CancellationToken.None));
        var urls = new[] { "https://mail.google.com/", "https://www.instagram.com/", "https://discord.com/channels/@me", project ? "https://github.com/user/project" : "https://github.com/" };
        for (var index = 0; index < urls.Length; index++)
        {
            var command = await NextCommand(operation);
            Assert.Equal(urls[index], command.GetProperty("url").GetString());
            Reply(command, 11 + index);
        }
        Assert.Equal(true, (await operation)["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Requested_saved_set_releases_its_receipt_for_a_fresh_user_turn()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        for (var turn = 0; turn < 2; turn++)
        {
            commands.ObserveSubmittedJarvisText("Open my research link and Gmail.", CancellationToken.None);
            var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
                JsonSerializer.SerializeToElement(new { url = "open my research link" }), CancellationToken.None));
            foreach (var (url, tab) in new[] { ("https://example.org/research", 11), ("https://mail.google.com/", 12) })
            {
                var command = await NextCommand(operation);
                Assert.Equal(url, command.GetProperty("url").GetString());
                Reply(command, tab);
            }
            Assert.Equal(true, (await operation)["ok"]);
            var duplicate = await commands.ExecuteJarvisToolAsync("open_url",
                JsonSerializer.SerializeToElement(new { url = "https://example.org/research" }), CancellationToken.None);
            Assert.Equal(true, duplicate["duplicate_suppressed"]);
            AssertNoCommand();
            commands.CompleteJarvisUserTurn();
        }
    }

    [Theory]
    [InlineData("open_url", "https://github.com/")]
    [InlineData("open_url", "https://example.org/unrequested")]
    [InlineData("run_workflow", "unrequested routine")]
    public async Task Saved_handoff_compound_receipt_survives_tool_only_completion_without_new_transcript(
        string lateTool, string lateTarget)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://github.com/example/project");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "unrequested routine", Actions = [new() { Kind = "url", Target = "https://example.org/unrequested" }],
        }];
        Process("hello", browserWindowPresent: false);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Open my research link and my code project.", CancellationToken.None);
        var first = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "my research link" }), CancellationToken.None);
        Assert.Equal(true, first["ok"]);
        Assert.Equal(2, first["resolved_count"]);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);

        var late = await commands.ExecuteJarvisToolAsync(lateTool,
            JsonSerializer.SerializeToElement(new { url = lateTarget, name = lateTarget }), CancellationToken.None);
        Assert.Equal(new[] { "https://example.org/research", "https://github.com/example/project" },
            _simpleLaunches.Select(launch => launch.Url));
        Assert.Equal(true, late["ok"]);
        Assert.True(late.TryGetValue("duplicate_suppressed", out var suppressed) && suppressed is true,
            "The late model proposal must reuse the saved-set receipt without another effect.");
        var targets = JsonSerializer.SerializeToElement(late).GetProperty("referential_targets");
        Assert.Equal(new[] { "https://example.org/research", "https://github.com/example/project" },
            targets.EnumerateArray().Select(target => target.GetProperty("target").GetString()));
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_handoff_specific_project_survives_tool_only_completion_without_new_transcript()
    {
        const string project = "https://github.com/example/project";
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", project);
        Process("hello", browserWindowPresent: false);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Open my code project.", CancellationToken.None);
        var first = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://github.com/" }), CancellationToken.None);
        Assert.Equal(true, first["ok"]);
        Assert.Equal(project, Assert.Single(_simpleLaunches).Url);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        var late = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://github.com/" }), CancellationToken.None);
        Assert.Equal(project, Assert.Single(_simpleLaunches).Url);
        Assert.Equal(true, late["ok"]);
        Assert.True(late.TryGetValue("duplicate_suppressed", out var suppressed) && suppressed is true,
            "A tool-only completion cannot replace the saved project with its generic homepage.");
        AssertNoCommand();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saved_handoff_answer_completion_or_fresh_request_releases_the_retained_saved_set(bool completedAnswer)
    {
        const string request = "Open my research link and Gmail.";
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        Process("hello", browserWindowPresent: false);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var first = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open my research link" }), CancellationToken.None);
        Assert.Equal(true, first["ok"]);
        Assert.Equal(2, _simpleLaunches.Count);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        if (completedAnswer)
            commands.CompleteJarvisUserTurn(assistantAnswerComplete: true);
        else
            commands.ObserveSubmittedJarvisText("Open Gmail.", CancellationToken.None);

        var next = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://mail.google.com/" }), CancellationToken.None);
        Assert.Equal(true, next["ok"]);
        Assert.False(next.TryGetValue("duplicate_suppressed", out var suppressed) && suppressed is true);
        Assert.Equal(3, _simpleLaunches.Count);
        Assert.Equal("https://mail.google.com/", _simpleLaunches[^1].Url);
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_handoff_fresh_voice_transcript_releases_retained_receipt_and_destination_deduplication()
    {
        const string project = "https://github.com/example/project";
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", project);
        Process("hello", browserWindowPresent: false);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Open my research link and my code project.", CancellationToken.None);
        var first = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "my research link" }), CancellationToken.None);
        Assert.Equal(true, first["ok"]);
        Assert.Equal(2, _simpleLaunches.Count);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        var late = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://github.com/" }), CancellationToken.None);
        Assert.True(late.TryGetValue("duplicate_suppressed", out var suppressed) && suppressed is true);
        Assert.Equal(2, _simpleLaunches.Count);

        // A new voice-observed request, not a local text submission, authorizes
        // reopening this same destination and must release the old set owner.
        commands.ObserveJarvisTranscript("Open my code project.", CancellationToken.None);
        var next = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "my code project" }), CancellationToken.None);
        Assert.Equal(true, next["ok"]);
        Assert.False(next.TryGetValue("duplicate_suppressed", out suppressed) && suppressed is true);
        Assert.Equal(new[] { "https://example.org/research", project, project }, _simpleLaunches.Select(launch => launch.Url));
        AssertNoCommand();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saved_handoff_receipt_expiry_or_session_reset_releases_retained_authority(bool expireReceipt)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        Process("hello", browserWindowPresent: false);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Open my research link and Gmail.", CancellationToken.None);
        var first = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open my research link" }), CancellationToken.None);
        Assert.Equal(true, first["ok"]);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        var receiptField = typeof(CommandRegistry).GetField("_browserPreRouteReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var receipt = Assert.IsType<BrowserPreRouteReceipt>(receiptField.GetValue(commands));
        if (expireReceipt)
        {
            // Age only the in-memory fixture; no wall-clock sleep or runtime session.
            typeof(BrowserPreRouteReceipt).GetProperty(nameof(BrowserPreRouteReceipt.CreatedUtc))!
                .SetValue(receipt, DateTime.UtcNow.AddMinutes(-1));
            commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        }
        else
            await commands.HandleJarvisSessionEndingAsync();
        Assert.Null(receiptField.GetValue(commands));

        var next = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/fresh" }), CancellationToken.None);
        Assert.Equal(true, next["ok"]);
        Assert.False(next.TryGetValue("duplicate_suppressed", out var suppressed) && suppressed is true);
        Assert.Equal(3, _simpleLaunches.Count);
        Assert.Equal("https://example.org/fresh", _simpleLaunches[^1].Url);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("my HarborLight link")]
    [InlineData("the harbor light link")]
    public async Task Saved_handoff_canonical_alias_still_requires_existing_ask_mode_confirmation(string proposedAlias)
    {
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        _mappings.Remember(LearnedMappingKind.Link, "can you please open the harbor light link", "https://example.org/saved");
        Process("hello", browserWindowPresent: false);
        var result = await Execute("open_url", new { url = proposedAlias });
        Assert.Equal(true, result["confirmation_required"]);
        Assert.Empty(_simpleLaunches);
        Assert.Equal("can you please open the harbor light link", Assert.Single(_mappings.Snapshot(LearnedMappingKind.Link)).Alias);
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_handoff_unrelated_generic_URL_keeps_its_existing_tool_only_boundary()
    {
        Process("hello", browserWindowPresent: false);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Open https://example.org/ordinary.", CancellationToken.None);
        var first = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/ordinary" }), CancellationToken.None);
        Assert.Equal(true, first["ok"]);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        var latestTurnField = typeof(CommandRegistry).GetField("_latestJarvisUserTurn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal("", latestTurnField.GetValue(commands));
        var next = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/next" }), CancellationToken.None);
        Assert.Equal(true, next["ok"]);
        Assert.False(next.TryGetValue("duplicate_suppressed", out var suppressed) && suppressed is true);
        Assert.Equal(new[] { "https://example.org/ordinary", "https://example.org/next" },
            _simpleLaunches.Select(launch => launch.Url));
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_handoff_forty_compound_cycles_keep_three_ordered_destinations_once_per_user_request()
    {
        var destinations = new[] { "https://example.org/research", "https://github.com/example/project", "https://example.org/review" };
        _mappings.Remember(LearnedMappingKind.Link, "open the research link", destinations[0]);
        _mappings.Remember(LearnedMappingKind.Link, "can you please open my code project", destinations[1]);
        _mappings.Remember(LearnedMappingKind.Link, "open my review portal", destinations[2]);
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "unrequested routine", Actions = [new() { Kind = "url", Target = "https://example.org/unrequested" }],
        }];
        var storedMappings = JsonSerializer.Serialize(_mappings.Snapshot());
        var storedWorkflows = JsonSerializer.Serialize(_settings.Current.JarvisWorkflows);
        Process("hello", browserWindowPresent: false);
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        var requests = new[]
        {
            "Open my research link, the code project and my review portal.",
            "open the research link; my code project; the review portal!",
            "Could you please open my Research link, the Code project and my Review portal?",
            "Okay, open the research link, my code project and the review portal.",
            "Please open the research link, the code project and my review portal.",
            "Can you open my research link, my code project and the review portal?",
            "Open my research link, open the code project and open my review portal.",
            "OPEN MY RESEARCH LINK, THE CODE PROJECT AND MY REVIEW PORTAL.",
        };
        for (var cycle = 0; cycle < 40; cycle++)
        {
            commands.ObserveSubmittedJarvisText(requests[cycle % requests.Length], CancellationToken.None);
            var firstTool = new[] { "open_url", "run_workflow", "open_app" }[cycle % 3];
            var first = await commands.ExecuteJarvisToolAsync(firstTool,
                JsonSerializer.SerializeToElement(new { url = "https://example.org/guess", name = "unrequested routine", app_name = "unrequested app" }),
                CancellationToken.None);
            Assert.Equal(true, first["ok"]);
            Assert.Equal(3, first["resolved_count"]);
            Assert.Equal(3, first["successful_count"]);
            Assert.Equal((cycle + 1) * 3, _simpleLaunches.Count);
            Assert.Equal(destinations, _simpleLaunches.TakeLast(3).Select(launch => launch.Url));
            commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);

            var lateTool = cycle % 3 == 2 ? "run_workflow" : "open_url";
            var lateTarget = cycle % 3 == 0 ? "https://github.com/" : "https://example.org/late-" + cycle;
            var late = await commands.ExecuteJarvisToolAsync(lateTool,
                JsonSerializer.SerializeToElement(new { url = lateTarget, name = "unrequested routine" }), CancellationToken.None);
            Assert.Equal(true, late["ok"]);
            Assert.True(late.TryGetValue("duplicate_suppressed", out var suppressed) && suppressed is true);
            Assert.Equal(3, late["resolved_count"]);
            Assert.Equal((cycle + 1) * 3, _simpleLaunches.Count);
            if (cycle % 2 == 0)
                commands.CompleteJarvisUserTurn(assistantAnswerComplete: true);
            // Odd cycles deliberately leave the receipt until the next explicit user submission.
        }
        Assert.Equal(Enumerable.Range(0, 40).SelectMany(_ => destinations), _simpleLaunches.Select(launch => launch.Url));
        Assert.Equal(storedMappings, JsonSerializer.Serialize(_mappings.Snapshot()));
        Assert.Equal(storedWorkflows, JsonSerializer.Serialize(_settings.Current.JarvisWorkflows));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("", " and Gmail.")]
    [InlineData("", ", Gmail.")]
    [InlineData("", "; Gmail.")]
    [InlineData(".", ", Gmail.")]
    public async Task Late_same_turn_open_list_extension_retains_completed_destination_and_executes_the_added_item(string ending, string extension)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Open my research link" + ending, CancellationToken.None);
        var first = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open my research link" }), CancellationToken.None));
        Reply(await NextCommand(first), 11);
        Assert.Equal(true, (await first)["ok"]);
        commands.ObserveJarvisTranscript("Open my research link" + extension, CancellationToken.None);
        var continuation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open my research link" }), CancellationToken.None));
        var added = await NextCommand(continuation);
        Assert.Equal("https://mail.google.com/", added.GetProperty("url").GetString());
        Reply(added, 12);
        var result = await continuation;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(2, result["resolved_count"]);
        Assert.Equal(1, result["executed_count"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open my research link and Visual Studio Code.")]
    [InlineData("Open my research link and custom installed product.")]
    [InlineData("Open my research link and Gmail settings.")]
    [InlineData("Open my research link and fill the form.")]
    [InlineData("Open my research link in Chrome and Gmail.")]
    [InlineData("Open my research link and Discord app.")]
    public void Saved_open_set_declines_clauses_with_an_existing_native_page_browser_or_action_owner(string request)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open Discord", "https://discord.com/channels/@me");
        Assert.False(_automation.TryResolveSavedOpenSet(request, out _));
        AssertNoCommand();
    }

    [Fact]
    public async Task Distinct_requested_aliases_of_one_destination_execute_that_destination_once()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my first link", "https://example.org/shared");
        _mappings.Remember(LearnedMappingKind.Link, "open my second link", "https://example.org/shared");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open my first link, my second link and Gmail.", CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/shared" }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://example.org/shared", 11), ("https://mail.google.com/", 12) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        var result = await operation;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(3, result["requested_count"]);
        Assert.Equal(2, result["executed_count"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open Research and AI.")]
    [InlineData("Open Research and AI and Gmail.")]
    [InlineData("Open Research, AI and Gmail.")]
    public void Saved_names_with_separators_keep_their_complete_identity_or_decline_ambiguous_list_splitting(string request)
    {
        _mappings.Remember(LearnedMappingKind.Link, "open Research and AI", "https://example.org/conjunction");
        _mappings.Remember(LearnedMappingKind.Link, "open Research, AI", "https://example.org/comma");
        Assert.False(_automation.TryResolveSavedOpenSet(request, out _));
        AssertNoCommand();
    }

    [Fact]
    public async Task Invalid_legacy_workflow_does_not_poison_the_other_requested_saved_destinations()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open my code project", "https://example.org/project");
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my legacy link", Actions = [new() { Kind = "auto", Target = "the selected link" }],
        }];
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open my research link, my legacy link and my code project.", CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("run_workflow",
            JsonSerializer.SerializeToElement(new { name = "open my legacy link" }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://example.org/research", 11), ("https://example.org/project", 12) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        var result = await operation;
        Assert.Equal(false, result["ok"]);
        Assert.Equal(2, result["successful_count"]);
        Assert.Equal(3, result["resolved_count"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Deleted_saved_item_in_a_set_cannot_execute_a_similar_workflow_or_an_invented_URL()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        _mappings.Remember(LearnedMappingKind.Link, "open my missing link", "https://example.org/deleted");
        Assert.True(_mappings.Forget("open my missing link", LearnedMappingKind.Link));
        _mappings.Load();
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my missing project", Actions = [new() { Kind = "url", Target = "https://example.org/similar" }],
        }];
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open my research link, my missing link and Gmail.", CancellationToken.None);
        var operation = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/deleted" }), CancellationToken.None));
        foreach (var (url, tab) in new[] { ("https://example.org/research", 11), ("https://mail.google.com/", 12) })
        {
            var command = await NextCommand(operation);
            Assert.Equal(url, command.GetProperty("url").GetString());
            Reply(command, tab);
        }
        Assert.Equal(false, (await operation)["ok"]);
        var alternate = await commands.ExecuteJarvisToolAsync("run_workflow",
            JsonSerializer.SerializeToElement(new { name = "open my missing project" }), CancellationToken.None);
        Assert.Equal(true, alternate["duplicate_suppressed"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Saved_open_set_preserves_ask_mode_pending_action_confirmation()
    {
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        _mappings.Remember(LearnedMappingKind.Link, "open my research link", "https://example.org/research");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveSubmittedJarvisText("Open my research link and Gmail.", CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "open my research link" }), CancellationToken.None);
        Assert.Equal(true, result["confirmation_required"]);
        Assert.False(result.ContainsKey("resolved_count"));
        Assert.NotNull(_automation.PendingSnapshot());
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Open my website and Gmail.", "open my website")]
    [InlineData("Okay, open my website and Gmail.", "open my website")]
    [InlineData("Open Gmail and my website.", "my website")]
    public async Task Saved_Link_collision_uses_current_persisted_value_inside_a_compound_turn(string request, string modelName)
    {
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my website", Aliases = ["my website"],
            Actions = [new() { Kind = "auto", Target = "the selected link" }],
        }];
        foreach (var destination in new[] { "https://example.org/original", "https://example.org/edited" })
        {
            var saved = await Execute("remember_link_alias", new { alias = "open my website", url = destination });
            Assert.Equal(true, saved["persistence_verified"]);
            _mappings.Load();
            var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
            commands.ObserveJarvisTranscript(request, CancellationToken.None);
            var opening = Task.Run(() => commands.ExecuteJarvisToolAsync("run_workflow",
                JsonSerializer.SerializeToElement(new { name = modelName }), CancellationToken.None));
            var destinations = request.StartsWith("Open Gmail", StringComparison.Ordinal)
                ? new[] { "https://mail.google.com/", destination }
                : new[] { destination, "https://mail.google.com/" };
            for (var index = 0; index < destinations.Length; index++)
            {
                var command = await NextCommand(opening);
                Assert.Equal(destinations[index], command.GetProperty("url").GetString());
                Reply(command, 11 + index);
            }
            Assert.Equal(true, (await opening)["ok"]);
            AssertNoCommand();
        }
        Assert.True(_mappings.Forget("open my website", LearnedMappingKind.Link));
        _mappings.Load();
        var afterDelete = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        afterDelete.ObserveJarvisTranscript(request, CancellationToken.None);
        var deletion = Task.Run(() => afterDelete.ExecuteJarvisToolAsync("run_workflow",
            JsonSerializer.SerializeToElement(new { name = modelName }), CancellationToken.None));
        var remaining = await NextCommand(deletion);
        Assert.Equal("https://mail.google.com/", remaining.GetProperty("url").GetString());
        Reply(remaining, 15);
        var rejected = await deletion;
        Assert.Equal(false, rejected["ok"]);
        var failed = Assert.Single(JsonSerializer.SerializeToElement(rejected).GetProperty("action_results")
            .EnumerateArray(), item => !item.GetProperty("ok").GetBoolean());
        Assert.Contains("unresolved selection", failed.GetProperty("result").GetProperty("message").GetString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Explicit_workflow_request_preserves_the_workflow_even_when_a_Link_has_the_same_trigger()
    {
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my website", Actions = [new() { Kind = "url", Target = "https://example.org/workflow" }],
        }];
        _mappings.Remember(LearnedMappingKind.Link, "open my website", "https://example.org/link");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Run the workflow open my website.", CancellationToken.None);
        var opening = Task.Run(() => commands.ExecuteJarvisToolAsync("run_workflow",
            JsonSerializer.SerializeToElement(new { name = "open my website" }), CancellationToken.None));
        var command = await NextCommand(opening);
        Assert.Equal("https://example.org/workflow", command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal(true, (await opening)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Don't open my website.")]
    [InlineData("Open my website app and Gmail.")]
    [InlineData("Run the workflow open my website and then open Gmail.")]
    [InlineData("What does open my website mean?")]
    public async Task Workflow_collision_does_not_grant_Link_authority_against_current_user_intent(string request)
    {
        _settings.Current.JarvisWorkflows = [new()
        {
            Name = "open my website", Actions = [new() { Kind = "auto", Target = "the selected link" }],
        }];
        _mappings.Remember(LearnedMappingKind.Link, "open my website", "https://example.org/link");
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("run_workflow",
            JsonSerializer.SerializeToElement(new { name = "open my website" }), CancellationToken.None);
        Assert.Equal(false, result["ok"]);
        Assert.Contains("unresolved selection", result["message"].ToString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Direct_search_returns_the_actual_opened_tab_without_creating_a_Browser_task()
    {
        var opening = Execute("web_search", new { query = "public documentation", engine = "google", browser = "chrome" });
        var command = await NextCommand(opening);
        Assert.Equal("open_direct_tab", command.GetProperty("type").GetString());
        Assert.Contains("public%20documentation", command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal("browser_tab", JsonSerializer.SerializeToElement(await opening).GetProperty("referential_target").GetProperty("kind").GetString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Post_open_focus_failure_remains_a_successful_tab_receipt_without_a_second_open()
    {
        var opening = Execute("open_url", new { url = "https://example.org/", browser = "chrome" });
        var command = await NextCommand(opening);
        Process("command_result", command, new
        {
            ok = true, tabId = 91, windowId = 7, sessionId = "test-session",
            foregroundConfirmed = false,
        });
        var result = JsonSerializer.SerializeToElement(await opening);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("foreground_confirmed").GetBoolean());
        Assert.Contains("did not confirm", result.GetProperty("message").GetString());
        Assert.Equal("browser_tab", result.GetProperty("referential_target").GetProperty("kind").GetString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Multi_target_close_uses_only_receipt_handles_and_accounts_for_manual_closure()
    {
        var first = await Open(11);
        var second = await Open(12);
        var closing = Execute("browser_action", new { action = "close tabs", target_ids = new[] { first, second, first } });
        var command = await NextCommand(closing);
        Assert.Equal(11, command.GetProperty("tabId").GetInt32());
        Assert.Equal(7, command.GetProperty("windowId").GetInt32());
        Assert.Equal("test-session", command.GetProperty("expectedSessionId").GetString());
        Reply(command, 11);
        command = await NextCommand(closing);
        Assert.Equal(12, command.GetProperty("tabId").GetInt32());
        Reply(command, 12, alreadyClosed: true);
        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(1, result["closed_count"]);
        Assert.Equal(1, result["already_closed_count"]);
        Assert.Equal(2, result["target_count"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("{\"action\":\"close tab\",\"tab_index\":2}", "index:2")]
    [InlineData("{\"action\":\"close tab\",\"tab_from_end\":1}", "from_end:1")]
    [InlineData("{\"action\":\"close tab\",\"tab_reference\":\"previous\"}", "previous")]
    public async Task Structured_single_tab_close_resolves_one_id_then_closes_it_once(string json, string reference)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        using var arguments = JsonDocument.Parse(json);
        var closing = commands.ExecuteJarvisToolAsync("browser_action", arguments.RootElement, CancellationToken.None);
        var resolution = await NextCommand(closing);
        Assert.Equal("activate_existing_tab", resolution.GetProperty("type").GetString());
        Assert.Equal(reference, resolution.GetProperty("text").GetString());
        Assert.Equal(7, resolution.GetProperty("windowId").GetInt32());
        ReplySelection(resolution, 42, 4);
        var close = await NextCommand(closing);
        Assert.Equal("close_direct_tab", close.GetProperty("type").GetString());
        Assert.Equal(42, close.GetProperty("tabId").GetInt32());
        Assert.Equal(7, close.GetProperty("windowId").GetInt32());
        Assert.Equal("test-session", close.GetProperty("expectedSessionId").GetString());
        Reply(close, 42);
        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(1, result["closed_count"]);
        Assert.Equal(1, result["target_count"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Close the last tab.", "from_end:1", false)]
    [InlineData("Close the second-last tab.", "from_end:2", false)]
    [InlineData("Close the first tab.", "index:1", false)]
    [InlineData("Close the last tab.", "from_end:1", true)]
    [InlineData("Close the second-last tab.", "from_end:2", true)]
    [InlineData("Close the first tab.", "index:1", true)]
    public async Task Whole_relative_close_beats_a_model_select_or_stale_handle_and_owns_the_action_once(
        string request, string reference, bool useStaleHandle)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var proposal = useStaleHandle
            ? JsonSerializer.SerializeToElement(new { action = "close tabs", target_ids = new[] { "stale-model-handle" } })
            : JsonSerializer.SerializeToElement(new { action = "select tab", tab_index = 2 });
        var closing = Task.Run(() => commands.ExecuteJarvisToolAsync("browser_action",
            proposal, CancellationToken.None));
        var resolution = await NextCommand(closing);
        Assert.Equal("activate_existing_tab", resolution.GetProperty("type").GetString());
        Assert.Equal(reference, resolution.GetProperty("text").GetString());
        ReplySelection(resolution, 42, 4);
        var close = await NextCommand(closing);
        Assert.Equal("close_direct_tab", close.GetProperty("type").GetString());
        Assert.Equal(42, close.GetProperty("tabId").GetInt32());
        Reply(close, 42);
        Assert.Equal(true, (await closing)["ok"]);
        AssertNoCommand();
        var duplicate = await commands.ExecuteJarvisToolAsync("browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tab" }), CancellationToken.None);
        Assert.Equal(true, duplicate["duplicate_suppressed"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Missing_relative_target_never_falls_back_to_closing_the_current_tab()
    {
        var closing = Execute("browser_action", new { action = "close tab", tab_from_end = 8 });
        var resolution = await NextCommand(closing);
        Process("command_result", resolution, new { ok = false, error = "That window has only four tabs." });
        Assert.Equal(false, (await closing)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_native_tab_authority_does_not_require_a_page_content_script(bool ordinalSet)
    {
        _contentScriptAvailable = false;
        Process("hello");
        Assert.False(_bridge.Status.ActiveTabFresh);
        var closing = ordinalSet
            ? Execute("browser_action", new { action = "close the second and third tabs" })
            : Execute("browser_action", new { action = "close tab", tab_index = 2 });
        ReplySelection(await NextCommand(closing), 32, 4);
        if (ordinalSet) ReplySelection(await NextCommand(closing), 33, 4);
        var first = await NextCommand(closing);
        Assert.Equal("close_direct_tab", first.GetProperty("type").GetString());
        Reply(first, ordinalSet ? 33 : 32);
        if (ordinalSet) Reply(await NextCommand(closing), 32);
        Assert.Equal(true, (await closing)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("no-session")]
    [InlineData("wrong-version")]
    [InlineData("untrusted-extension")]
    [InlineData("multiple-helpers")]
    public void Missing_DOM_status_cannot_use_stale_or_untrusted_native_authority(string invalid)
    {
        var status = new BrowserHelperStatus
        {
            Connected = true, CommandAuthoritySupported = true, WindowId = 7, TabId = 10, ActiveTitle = "Example",
            LastHeartbeatUtc = DateTime.UtcNow.AddSeconds(invalid == "stale" ? -6 : invalid == "future" ? 6 : 0),
            SessionId = invalid == "no-session" ? "" : "test-session",
            ServiceWorkerVersion = invalid == "wrong-version" ? "0.0.0" : BrowserHelperProtocol.Version,
            ExtensionId = invalid == "untrusted-extension" ? "unknown" : BrowserHelperProtocol.StoreExtensionId,
            MultipleHelpersDetected = invalid == "multiple-helpers"
        };
        Assert.False(JarvisAutomationService.CanResolveForegroundBrowserTabReference(_browserTabForeground, status));
    }

    [Fact]
    public async Task Wrong_window_resolution_cannot_close_a_tab_from_another_window()
    {
        var closing = Execute("browser_action", new { action = "close tab", tab_index = 1 });
        var resolution = await NextCommand(closing);
        Process("command_result", resolution, new { ok = true, tabId = 42, windowId = 8, tabCount = 4 });
        Assert.Equal(false, (await closing)["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("notepad", "Example - Google Chrome")]
    [InlineData("chrome", "Different page - Google Chrome")]
    public async Task A_background_or_stale_browser_window_cannot_own_a_relative_close(string process, string title)
    {
        _browserTabForeground = new(new IntPtr(17), process, title);
        var result = await Execute("browser_action", new { action = "close tab", tab_from_end = 1 });
        Assert.Equal(false, result["ok"]);
        Assert.Contains("current browser window could not be verified", result["message"].ToString());
        AssertNoCommand();
    }

    [Fact]
    public async Task A_foreground_window_change_after_resolution_prevents_the_close()
    {
        var closing = Execute("browser_action", new { action = "close tab", tab_index = 1 });
        var resolution = await NextCommand(closing);
        _browserTabForeground = new(new IntPtr(18), "chrome", "Example - Google Chrome");
        ReplySelection(resolution, 42, 4);
        Assert.Equal(false, (await closing)["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Relative_single_close_preserves_normal_ask_mode_permission_boundary()
    {
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        var result = await Execute("browser_action", new { action = "close tab", tab_from_end = 1 });
        Assert.Equal(true, result["confirmation_required"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Close the 2nd and 3rd tab.", "index:2,index:3")]
    [InlineData("Close the second and third tabs.", "index:2,index:3")]
    [InlineData("Could you please close the second and the third Chrome tabs for me?", "index:2,index:3")]
    [InlineData("Jarvis, close the first and fourth tabs in Chrome, please.", "index:1,index:4")]
    [InlineData("Close the ninth and second and fourth tabs.", "index:9,index:2,index:4")]
    [InlineData("Close the 2-3 tab.", "index:2,index:3")]
    [InlineData("Close the 2nd–3rd tabs.", "index:2,index:3")]
    public void Ordinal_set_close_owns_only_the_explicit_distinct_positions(string request, string expected)
    {
        Assert.True(JarvisAutomationService.TryGetOrdinalBrowserTabCloseReferences(request, out var references));
        Assert.Equal(expected.Split(','), references);
        Assert.False(JarvisAutomationService.TryGetPositionalBrowserTabCloseCount(request, out _, out _));
        Assert.False(JarvisAutomationService.TryGetBrowserTabCloseReference(request, out _, out _));
    }

    [Theory]
    [InlineData("Close the first two tabs.")]
    [InlineData("Close the last two tabs.")]
    [InlineData("Close the fourth tab.")]
    [InlineData("Close the second and second tabs.")]
    [InlineData("Close the 2nd and 10th tabs.")]
    [InlineData("Close the 2nd and 0th tabs.")]
    [InlineData("Don't close the second and third tabs.")]
    [InlineData("Tell me how to close the second and third tabs.")]
    [InlineData("Say: close the second and third tabs.")]
    [InlineData("Close the second and third tabs and open Gmail.")]
    [InlineData("Open Gmail and close the second and third tabs.")]
    [InlineData("Close the 3-2 tabs.")]
    [InlineData("Close the 0-3 tabs.")]
    [InlineData("Close the 2-10 tabs.")]
    [InlineData("Don't close the 2-3 tabs.")]
    [InlineData("Close the 2-3 tabs and open Gmail.")]
    public void Ordinal_set_close_declines_other_owners_invalid_positions_and_mixed_intent(string request) =>
        Assert.False(JarvisAutomationService.TryGetOrdinalBrowserTabCloseReferences(request, out _));

    [Theory]
    [InlineData("Close the 2nd and 3rd tab.", "{\"action\":\"close tabs\",\"last_tab_count\":2}")]
    [InlineData("Close the second and third tabs.", "{\"action\":\"select tab\",\"tab_index\":1}")]
    [InlineData("Please close the second and third tabs.", "{\"action\":\"close tabs\",\"target_ids\":[\"stale-model-handle\"]}")]
    [InlineData("Close the 2-3 tab.", "{\"action\":\"close tabs\",\"last_tab_count\":2}")]
    public async Task Whole_ordinal_set_resolves_before_mutation_overrides_model_targets_and_releases_for_new_turn(
        string request, string modelProposal)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        using var proposal = JsonDocument.Parse(modelProposal);
        for (var turn = 0; turn < 2; turn++)
        {
            commands.ObserveSubmittedJarvisText(request, CancellationToken.None);
            var closing = Task.Run(() => commands.ExecuteJarvisToolAsync(
                "browser_action", proposal.RootElement, CancellationToken.None));
            var second = 32 + turn * 10;
            var third = 33 + turn * 10;
            foreach (var expected in new[] { (Selection: "index:2", Tab: second), (Selection: "index:3", Tab: third) })
            {
                var command = await NextCommand(closing);
                Assert.Equal("activate_existing_tab", command.GetProperty("type").GetString());
                Assert.Equal(expected.Selection, command.GetProperty("text").GetString());
                ReplySelection(command, expected.Tab, 4);
            }
            foreach (var expectedTab in new[] { third, second })
            {
                var command = await NextCommand(closing);
                Assert.Equal("close_direct_tab", command.GetProperty("type").GetString());
                Assert.Equal(expectedTab, command.GetProperty("tabId").GetInt32());
                Assert.Equal(7, command.GetProperty("windowId").GetInt32());
                Assert.Equal("test-session", command.GetProperty("expectedSessionId").GetString());
                Reply(command, expectedTab);
            }
            var result = await closing;
            Assert.Equal(true, result["ok"]);
            Assert.Equal(2, result["closed_count"]);
            Assert.Equal(true, result["resolved_before_mutation"]);
            AssertNoCommand();
            var duplicate = await commands.ExecuteJarvisToolAsync("browser_action",
                JsonSerializer.SerializeToElement(new { action = "close tab" }), CancellationToken.None);
            Assert.Equal(true, duplicate["duplicate_suppressed"]);
            AssertNoCommand();
            commands.CompleteJarvisUserTurn();
        }
    }

    [Theory]
    [InlineData("Close the second and third tabs from the left.")]
    [InlineData("Could you close the second tab and the third tab?")]
    [InlineData("Close tabs 2 and 3.")]
    [InlineData("Close the 2 and 3 tabs.")]
    public async Task Spoken_ordinal_positions_override_stale_model_handles_across_consecutive_turns(string request)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        using var proposals = JsonDocument.Parse("{\"functionCalls\":[{\"id\":\"one\",\"name\":\"browser_action\",\"args\":{\"action\":\"close tabs\",\"target_ids\":[\"expired-model-reference\"]}},{\"id\":\"two\",\"name\":\"browser_action\",\"args\":{\"action\":\"close tab\",\"tab_index\":3}}]}");
        for (var turn = 0; turn < 2; turn++)
        {
            using var dispatcher = new LiveToolCallDispatcher(default,
                commands.ExecuteJarvisToolAsync, (_, _) => Task.CompletedTask);
            dispatcher.Observe(commands.ObserveJarvisTranscript, turn == 0 ? request : "Now close the first two tabs.");
            dispatcher.Enqueue(proposals.RootElement, new ToolAttemptGuard());
            var closing = dispatcher.Completion;
            var second = 32 + turn * 10; var third = second + 1;
            var command = await NextCommand(closing);
            Assert.Equal(turn == 0 ? "index:2" : "index:1", command.GetProperty("text").GetString());
            ReplySelection(command, second, 4);
            command = await NextCommand(closing);
            Assert.Equal(turn == 0 ? "index:3" : "index:2", command.GetProperty("text").GetString());
            ReplySelection(command, third, 4);
            foreach (var tab in new[] { third, second })
            {
                command = await NextCommand(closing);
                Assert.Equal("close_direct_tab", command.GetProperty("type").GetString());
                Assert.Equal(tab, command.GetProperty("tabId").GetInt32());
                Reply(command, tab);
            }
            await closing;
            AssertNoCommand();
            commands.CompleteJarvisUserTurn();
        }
    }

    [Theory]
    [InlineData("Close the second and third tabs from the right.")]
    [InlineData("Close the second tab and second tab.")]
    [InlineData("Close tabs 2 and 10.")]
    [InlineData("Close 2 and 3.")]
    [InlineData("Don't close tabs 2 and 3.")]
    [InlineData("Close tabs 2 and 3 and open Gmail.")]
    public void Spoken_ordinal_extension_keeps_ambiguity_and_action_boundaries(string request)
        => Assert.False(JarvisAutomationService.TryGetOrdinalBrowserTabCloseReferences(request, out _));

    [Theory]
    [InlineData("notepad", "Example - Google Chrome")]
    [InlineData("chrome", "Different page - Google Chrome")]
    public async Task Spoken_ordinal_extension_keeps_real_foreground_ownership(string process, string title)
    {
        _browserTabForeground = new(new IntPtr(17), process, title);
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.ObserveJarvisTranscript("Close the second and third tabs from the left.", default);
        var result = await commands.ExecuteJarvisToolAsync("browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tabs", target_ids = new[] { "expired-model-reference" } }), default);
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Contains("current browser window could not be verified", result["message"].ToString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Genuine_stale_handles_are_still_rejected_without_an_explicit_spoken_position()
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.ObserveJarvisTranscript("Close those tabs.", default);
        var result = await commands.ExecuteJarvisToolAsync("browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tabs", target_ids = new[] { "expired-model-reference" } }), default);
        Assert.False(result.GetValueOrDefault("ok") is true);
        Assert.Contains("unknown or expired", result["message"].ToString());
        AssertNoCommand();
    }

    [Theory]
    [InlineData("Don't close the second and third tabs.")]
    [InlineData("Tell me how to close the second and third tabs.")]
    [InlineData("Close the second and third tabs and open Gmail.")]
    [InlineData("Close the second and second tabs.")]
    [InlineData("Close the 2nd and 10th tabs.")]
    public async Task Unowned_ordinal_set_cannot_authorize_a_model_selected_edge(string request)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tabs", last_tab_count = 2 }), CancellationToken.None);
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["no_tab_closed"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("{\"action\":\"close the second and third tabs\",\"tab_index\":2}")]
    [InlineData("{\"action\":\"close the second and third tabs\",\"tab_from_end\":1}")]
    [InlineData("{\"action\":\"close the second and third tabs\",\"tab_reference\":\"current\"}")]
    [InlineData("{\"action\":\"close the second and third tabs\",\"last_tab_count\":2}")]
    [InlineData("{\"action\":\"close the second and third tabs\",\"target_ids\":[\"old\"]}")]
    public async Task Direct_ordinal_action_rejects_conflicting_structured_target_forms(string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await _automation.ExecuteAsync("browser_action", args.RootElement, CancellationToken.None);
        Assert.Equal(false, result["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Whole_ordinal_set_preserves_ask_mode_before_any_resolution()
    {
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.ObserveJarvisTranscript("Close the second and third tabs.", CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tabs", last_tab_count = 2 }), CancellationToken.None);
        Assert.Equal(true, result["confirmation_required"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Ordinal_set_foreground_change_after_resolution_prevents_all_closes()
    {
        var closing = Execute("browser_action", new { action = "close the second and third tabs" });
        ReplySelection(await NextCommand(closing), 32, 4);
        var third = await NextCommand(closing);
        _browserTabForeground = new(new IntPtr(18), "chrome", "Example - Google Chrome");
        ReplySelection(third, 33, 4);
        Assert.Equal(false, (await closing)["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Ordinal_set_missing_position_does_not_close_the_resolved_subset()
    {
        var closing = Execute("browser_action", new { action = "close the second and third tabs" });
        ReplySelection(await NextCommand(closing), 32, 2);
        var third = await NextCommand(closing);
        Process("command_result", third, new { ok = false, error = "That window has only two tabs." });
        Assert.Equal(false, (await closing)["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Leftmost_multi_tab_close_resolves_the_complete_set_before_exact_closes()
    {
        var closing = _automation.ClosePositionalBrowserTabsAsync(2, fromEnd: false, CancellationToken.None);
        foreach (var expected in new[] { (Selection: "index:1", Tab: 21), (Selection: "index:2", Tab: 22) })
        {
            var command = await NextCommand(closing);
            Assert.Equal("activate_existing_tab", command.GetProperty("type").GetString());
            Assert.Equal(expected.Selection, command.GetProperty("text").GetString());
            ReplySelection(command, expected.Tab, 4);
        }
        foreach (var expectedTab in new[] { 22, 21 })
        {
            var command = await NextCommand(closing);
            Assert.Equal("close_direct_tab", command.GetProperty("type").GetString());
            Assert.Equal(expectedTab, command.GetProperty("tabId").GetInt32());
            Assert.Equal(7, command.GetProperty("windowId").GetInt32());
            Assert.Equal("test-session", command.GetProperty("expectedSessionId").GetString());
            Reply(command, expectedTab);
        }
        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(2, result["closed_count"]);
        Assert.Equal(2, result["target_count"]);
        Assert.Equal(true, result["resolved_before_mutation"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Close the first two tabs.")]
    [InlineData("{\"action\":\"close tab\",\"tab_from_end\":1}", "Close the first two tabs.")]
    [InlineData("{\"action\":\"close tabs\",\"target_ids\":[\"stale-model-handle\"]}", "Close the first two tabs.")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Can you please close the first 2 tabs?")]
    [InlineData("{\"action\":\"close tab\",\"tab_from_end\":1}", "Can you please close the first 2 tabs?")]
    [InlineData("{\"action\":\"close tabs\",\"target_ids\":[\"stale-model-handle\"]}", "Can you please close the first 2 tabs?")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Can you please close the first two tabs for me now?")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Please close the first two tabs in Chrome.")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "I want you to close the first two tabs.")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Can you please close the first two tabs, please?")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Jarvis, can you please close the first two tabs?")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Can you just close the first two tabs in Chrome?")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Close the first two tabs which are already open.")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Okay, close the first two tabs.")]
    [InlineData("{\"action\":\"close tabs\",\"last_tab_count\":2}", "Then close the first two tabs.")]
    public async Task Whole_first_two_close_overrides_conflicting_model_targets_and_owns_the_action_once(
        string modelProposal, string request)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        using var proposal = JsonDocument.Parse(modelProposal);
        var closing = Task.Run(() => commands.ExecuteJarvisToolAsync(
            "browser_action", proposal.RootElement, CancellationToken.None));
        foreach (var expected in new[] { (Selection: "index:1", Tab: 31), (Selection: "index:2", Tab: 32) })
        {
            var command = await NextCommand(closing);
            Assert.Equal("activate_existing_tab", command.GetProperty("type").GetString());
            Assert.Equal(expected.Selection, command.GetProperty("text").GetString());
            ReplySelection(command, expected.Tab, 4);
        }
        foreach (var expectedTab in new[] { 32, 31 })
        {
            var command = await NextCommand(closing);
            Assert.Equal("close_direct_tab", command.GetProperty("type").GetString());
            Assert.Equal(expectedTab, command.GetProperty("tabId").GetInt32());
            Reply(command, expectedTab);
        }
        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(2, result["closed_count"]);
        Assert.Equal(true, result["resolved_before_mutation"]);
        AssertNoCommand();

        var duplicate = await commands.ExecuteJarvisToolAsync("browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tabs", last_tab_count = 2 }),
            CancellationToken.None);
        Assert.Equal(true, duplicate["duplicate_suppressed"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("On this screen, close the first two tabs.")]
    [InlineData("Don't close the first two tabs.")]
    [InlineData("Tell me how to close the first two tabs.")]
    [InlineData("Close the first two tabs and then open Gmail.")]
    [InlineData("Then don't close the first two tabs.")]
    [InlineData("Close the 2-10 tabs.")]
    public async Task Unowned_explicit_positional_close_cannot_execute_a_conflicting_model_edge(string request)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var closing = Task.Run(() => commands.ExecuteJarvisToolAsync("browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tabs", last_tab_count = 2 }), cancellation.Token));
        try
        {
            var result = await closing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(false, result["ok"]);
            Assert.Equal(true, result["no_tab_closed"]);
            Assert.Equal("request_not_resolved", result["error_code"]);
            Assert.Equal(false, result["connection_attempted"]);
            AssertNoCommand();
        }
        finally
        {
            cancellation.Cancel();
            try { await closing; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Consecutive_edge_closes_keep_the_current_humans_edge_after_a_completed_request(bool firstFromEnd, bool secondFromEnd)
    {
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        foreach (var fromEnd in new[] { firstFromEnd, secondFromEnd })
        {
            commands.ObserveJarvisTranscript($"Then close the {(fromEnd ? "last" : "first")} two tabs.", CancellationToken.None);
            var closing = Task.Run(() => commands.ExecuteJarvisToolAsync("browser_action",
                JsonSerializer.SerializeToElement(new { action = "close tabs", last_tab_count = 2 }), CancellationToken.None));
            foreach (var index in new[] { 1, 2 })
            {
                var resolution = await NextCommand(closing);
                Assert.Equal(fromEnd ? "from_end:" + (3 - index) : "index:" + index, resolution.GetProperty("text").GetString());
                ReplySelection(resolution, 30 + index, 4);
            }
            foreach (var tab in new[] { 32, 31 }) Reply(await NextCommand(closing), tab);
            Assert.Equal(true, (await closing)["ok"]);
            AssertNoCommand();
            commands.CompleteJarvisUserTurn();
        }
    }

    [Fact]
    public async Task Rightmost_multi_tab_close_resolves_the_complete_set_before_exact_closes()
    {
        var closing = _automation.CloseRightmostBrowserTabsAsync(3, CancellationToken.None);
        foreach (var expected in new[]
                 {
                     (Selection: "from_end:3", Tab: 21),
                     (Selection: "from_end:2", Tab: 22),
                     (Selection: "from_end:1", Tab: 23),
                 })
        {
            var command = await NextCommand(closing);
            Assert.Equal("activate_existing_tab", command.GetProperty("type").GetString());
            Assert.Equal(expected.Selection, command.GetProperty("text").GetString());
            ReplySelection(command, expected.Tab, 4);
        }
        foreach (var expectedTab in new[] { 23, 22, 21 })
        {
            var command = await NextCommand(closing);
            Assert.Equal("close_direct_tab", command.GetProperty("type").GetString());
            Assert.Equal(expectedTab, command.GetProperty("tabId").GetInt32());
            Assert.Equal(7, command.GetProperty("windowId").GetInt32());
            Assert.Equal("test-session", command.GetProperty("expectedSessionId").GetString());
            Reply(command, expectedTab);
        }
        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(3, result["closed_count"]);
        Assert.Equal(3, result["target_count"]);
        Assert.Equal(true, result["resolved_before_mutation"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Unconfirmed_exact_tab_close_reports_uncertainty_without_retargeting_more_tabs()
    {
        var closing = _automation.CloseRightmostBrowserTabsAsync(2, CancellationToken.None);
        foreach (var (selection, tab) in new[] { ("from_end:2", 21), ("from_end:1", 22) })
        {
            var command = await NextCommand(closing);
            Assert.Equal(selection, command.GetProperty("text").GetString());
            ReplySelection(command, tab, 3);
        }

        var close = await NextCommand(closing);
        Assert.Equal("close_direct_tab", close.GetProperty("type").GetString());
        Assert.Equal(22, close.GetProperty("tabId").GetInt32());
        Process("command_result", close, new { ok = false, error = "Close acknowledgement was lost." });

        var result = await closing;
        Assert.Equal(false, result["ok"]);
        Assert.Contains("may also have closed", result["message"].ToString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Model_selected_rightmost_count_uses_exact_Helper_set_not_one_close_shortcut()
    {
        var closing = Execute("browser_action", new { action = "close tabs", last_tab_count = 3 });
        foreach (var expected in new[]
                 {
                     (Selection: "from_end:3", Tab: 21),
                     (Selection: "from_end:2", Tab: 22),
                     (Selection: "from_end:1", Tab: 23),
                 })
        {
            var command = await NextCommand(closing);
            Assert.Equal("activate_existing_tab", command.GetProperty("type").GetString());
            Assert.Equal(expected.Selection, command.GetProperty("text").GetString());
            ReplySelection(command, expected.Tab, 4);
        }
        foreach (var expectedTab in new[] { 23, 22, 21 })
        {
            var command = await NextCommand(closing);
            Assert.Equal("close_direct_tab", command.GetProperty("type").GetString());
            Assert.Equal(expectedTab, command.GetProperty("tabId").GetInt32());
            Reply(command, expectedTab);
        }
        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(3, result["closed_count"]);
        Assert.Equal(3, result["target_count"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Invalid_rightmost_count_cannot_fall_back_to_a_foreground_shortcut()
    {
        var result = await Execute("browser_action", new { action = "close tabs", last_tab_count = 1 });
        Assert.Equal(false, result["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Mixed_exact_ids_and_rightmost_count_cannot_close_either_set()
    {
        var result = await Execute("browser_action", new
        {
            action = "close tabs", target_ids = new[] { "some-target" }, last_tab_count = 3,
        });
        Assert.Equal(false, result["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Duplicate_rightmost_tab_resolution_fails_before_any_close()
    {
        var closing = _automation.CloseRightmostBrowserTabsAsync(3, CancellationToken.None);
        var command = await NextCommand(closing);
        ReplySelection(command, 21, 4);
        command = await NextCommand(closing);
        ReplySelection(command, 21, 4);
        var result = await closing;
        Assert.Equal(false, result["ok"]);
        Assert.Contains("No tab was closed", result["message"].ToString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Whole_turn_rightmost_close_owns_the_selected_direct_tool_once()
    {
        var commands = new CommandRegistry(
            null,
            null,
            null,
            _settings,
            null,
            automation: _automation);
        commands.ObserveJarvisTranscript(
            "Close the last 3 Chrome tabs.",
            CancellationToken.None);
        var closing = Task.Run(async () => await commands.ExecuteJarvisToolAsync(
            "browser_action",
            JsonSerializer.SerializeToElement(new { action = "close tab" }),
            CancellationToken.None));
        foreach (var expected in new[]
                 {
                     (Selection: "from_end:3", Tab: 31),
                     (Selection: "from_end:2", Tab: 32),
                     (Selection: "from_end:1", Tab: 33),
                 })
        {
            var command = await NextCommand(closing);
            Assert.Equal(expected.Selection, command.GetProperty("text").GetString());
            ReplySelection(command, expected.Tab, 4);
        }
        foreach (var expectedTab in new[] { 33, 32, 31 })
            Reply(await NextCommand(closing), expectedTab);

        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(3, result["closed_count"]);
        Assert.Equal(3, result["target_count"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Mixed_known_unknown_references_reject_the_entire_set_before_any_mutation()
    {
        var known = await Open(11);
        var result = await Execute("browser_action", new { action = "close tabs", target_ids = new[] { known, "invented" } });
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["needs_clarification"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Wrong_close_receipt_cannot_count_as_success_or_consume_the_target()
    {
        var known = await Open(11);
        var closing = Execute("browser_action", new { action = "close tabs", target_ids = new[] { known } });
        Reply(await NextCommand(closing), 99);
        var result = await closing;
        Assert.Equal(false, result["ok"]);
        Assert.Equal(0, result["closed_count"]);
        var retry = Execute("browser_action", new { action = "close tabs", target_ids = new[] { known } });
        Reply(await NextCommand(retry), 11);
        Assert.Equal(true, (await retry)["ok"]);
    }

    [Fact]
    public async Task Conversation_reset_invalidates_handles_without_targeting_another_tab()
    {
        var known = await Open(11);
        _automation.ResetConversationTransientContext();
        Assert.Equal(false, (await Execute("browser_action", new { action = "close tabs", target_ids = new[] { known } }))["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Reset_during_open_does_not_repopulate_new_conversation_context()
    {
        var opening = Execute("open_url", new { url = "https://example.org/one", browser = "chrome" });
        var command = await NextCommand(opening);
        _automation.ResetConversationTransientContext();
        Reply(command, 11);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        AssertNoCommand();
    }

    [Fact]
    public async Task Cancellation_revokes_close_waiter_and_never_dispatches_the_rest_of_the_set()
    {
        var first = await Open(11);
        var second = await Open(12);
        using var cancel = new CancellationTokenSource();
        var closing = Execute("browser_action", new { action = "close tabs", target_ids = new[] { first, second } }, cancel.Token);
        var command = await NextCommand(closing);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing);
        Assert.False(Process("command_check", command).GetProperty("commandAuthorized").GetBoolean());
        Reply(command, 11);
        AssertNoCommand();
    }

    [Fact]
    public async Task Workflow_keeps_successful_handles_and_partial_failures_in_one_result()
    {
        _settings.Current.JarvisWorkflows = new() { new()
        {
            Name = "test workspace", Actions = new()
            {
                new() { Kind = "url", Target = "https://example.org/one" },
                new() { Kind = "url", Target = "https://example.org/two" },
                new() { Kind = "url", Target = "https://example.org/three" },
            },
        } };
        var workflow = Execute("run_workflow", new { name = "test workspace" });
        Reply(await NextCommand(workflow), 11);
        var failed = await NextCommand(workflow);
        Process("command_result", failed, new { ok = false, error = "No second tab was created." });
        Reply(await NextCommand(workflow), 13);
        var result = JsonSerializer.SerializeToElement(await workflow);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(3, result.GetProperty("action_results").GetArrayLength());
        var targets = result.GetProperty("referential_targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("https://example.org/one", targets[0].GetProperty("target").GetString());
        Assert.Equal("https://example.org/three", targets[1].GetProperty("target").GetString());
        Assert.NotEmpty(targets[0].GetProperty("target_id").GetString());
        AssertNoCommand();
    }

    [Theory]
    [InlineData("close tabs", "[]")]
    [InlineData("close tabs", "[null]")]
    [InlineData("close tabs", "[11]")]
    [InlineData("first tab", "[\"invented\"]")]
    public async Task Invalid_target_command_fails_closed(string action, string ids)
    {
        using var document = JsonDocument.Parse(ids);
        var result = await Execute("browser_action", new { action, target_ids = document.RootElement });
        Assert.Equal(false, result["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("open_app")]
    [InlineData("open_path")]
    [InlineData("open_url")]
    public void Shell_link_result_never_misrepresents_an_alias_as_a_native_app_or_exact_tab(string tool)
    {
        var result = new Dictionary<string, object> { ["ok"] = true, ["url"] = "https://example.org/actual" };
        JarvisAutomationService.AttachReferentialTarget(tool, JsonSerializer.SerializeToElement(new { app_name = "my research", path = "my research", url = "my research" }), result);
        var target = JsonSerializer.SerializeToElement(result).GetProperty("referential_target");
        Assert.Equal("link", target.GetProperty("kind").GetString());
        Assert.Equal("https://example.org/actual", target.GetProperty("target").GetString());
        Assert.False(target.TryGetProperty("target_id", out _));
        Assert.Contains("no exact tab handle", target.GetProperty("instruction").GetString());
    }

    [Theory]
    [InlineData("ChromeHTML", true)]
    [InlineData("ChromeHTML.random", true)]
    [InlineData("MSEdgeHTM", false)]
    [InlineData("FirefoxURL", false)]
    [InlineData(null, false)]
    public void Only_actual_Chrome_association_allows_Helper_substitution(string progId, bool expected) =>
        Assert.Equal(expected, BrowserLauncherService.IsChromeAssociation(progId));

    [Theory]
    [InlineData("msedge", 17)]
    [InlineData("notepad", 17)]
    [InlineData("chrome", 0)]
    [InlineData("", 0)]
    public async Task Simple_URL_uses_launcher_when_connected_Chrome_Helper_has_no_window(string foregroundProcess, int window)
    {
        _settings.Current.PreferredBrowser = "";
        _browserTabForeground = new(new IntPtr(window), foregroundProcess, "Foreground");
        Process("hello", browserWindowPresent: false);
        _mappings.Remember(LearnedMappingKind.Link, "my research", "https://example.org/research");
        using var cancel = new CancellationTokenSource();
        try
        {
            var opening = Execute("open_url", new { url = "my research", browser = "chrome" }, cancel.Token);
            await Task.WhenAny(opening, Task.Delay(100));
            Assert.Equal(("https://example.org/research", "chrome"), Assert.Single(_simpleLaunches));
            var result = JsonSerializer.SerializeToElement(await opening.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal("link", result.GetProperty("referential_target").GetProperty("kind").GetString());
            Assert.False(result.GetProperty("referential_target").TryGetProperty("target_id", out _));
            AssertNoCommand();
        }
        finally { cancel.Cancel(); }
    }

    [Theory]
    [InlineData("msedge", 17)]
    [InlineData("", 0)]
    public async Task URL_only_workflow_uses_existing_default_launcher_once_per_action_when_Chrome_has_no_window(string foregroundProcess, int window)
    {
        _settings.Current.PreferredBrowser = "";
        _browserTabForeground = new(new IntPtr(window), foregroundProcess, "Foreground");
        Process("hello", browserWindowPresent: false);
        var routine = new JarvisWorkflowSettings { Name = "initialize my research setup", Actions =
        [
            new() { Kind = "auto", Target = "Claude" },
            new() { Kind = "url", Target = "https://chatgpt.com/" },
            new() { Kind = "auto", Target = "Gemini" },
        ] };
        _settings.Current.JarvisWorkflows = [routine];
        var stored = JsonSerializer.Serialize(routine);
        using var cancel = new CancellationTokenSource();
        try
        {
            var result = await Execute("run_workflow", new { name = routine.Name }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(true, result["ok"]);
            Assert.Equal(new[] { "https://claude.ai/", "https://chatgpt.com/", "https://gemini.google.com/" }, _simpleLaunches.Select(open => open.Url));
            Assert.All(_simpleLaunches, open => Assert.True(string.IsNullOrEmpty(open.Browser)));
            Assert.Equal(stored, JsonSerializer.Serialize(routine));
            AssertNoCommand();
        }
        finally { cancel.Cancel(); }
    }

    [Theory]
    [InlineData("msedge")]
    [InlineData("notepad")]
    public async Task Background_Chrome_with_a_window_keeps_existing_exact_tab_open_path(string foregroundProcess)
    {
        _browserTabForeground = new(new IntPtr(17), foregroundProcess, "Foreground");
        await Open(11);
        Assert.Empty(_simpleLaunches);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("", "", "default browser")]
    [InlineData("", "edge", "edge")]
    [InlineData("edge", "chrome", "edge")]
    public async Task Windowless_URL_launch_preserves_requested_preferred_and_default_browser_choice(string requested, string preferred, string expected)
    {
        Process("hello", browserWindowPresent: false);
        _settings.Current.PreferredBrowser = preferred;
        var result = await Execute("open_url", new { url = "https://example.org/", browser = requested });
        Assert.Equal(true, result["ok"]);
        Assert.Equal(expected, result["browser"]);
        Assert.Equal(("https://example.org/", requested), Assert.Single(_simpleLaunches));
        AssertNoCommand();
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows")]
    [InlineData("the selected link")]
    public async Task Windowless_URL_launch_rejects_unsafe_or_unresolved_targets(string target)
    {
        Process("hello", browserWindowPresent: false);
        Assert.Equal(false, (await Execute("open_url", new { url = target }))["ok"]);
        Assert.Empty(_simpleLaunches);
        AssertNoCommand();
    }

    [Fact]
    public async Task Helper_rejection_after_URL_dispatch_never_launches_a_second_browser()
    {
        var opening = Execute("open_url", new { url = "https://example.org/", browser = "chrome" });
        var command = await NextCommand(opening);
        Process("command_result", command, new { ok = false, error = "Creation could not be confirmed." });
        Assert.Equal(false, (await opening)["ok"]);
        Assert.Empty(_simpleLaunches);
        AssertNoCommand();
    }

    [Fact]
    public async Task Ordinary_saved_open_uses_the_existing_action_path_with_zero_connector_requests()
    {
        _mappings.Remember(LearnedMappingKind.Link, "open my music", "https://open.spotify.com/");
        var requests = 0;
        _automation.Connectors = new JarvisConnectorController((method, path, body, token) =>
        {
            requests++; throw new InvalidOperationException("An ordinary open must not contact connectors.");
        }, () => "test-account", () => CancellationToken.None, () => "Open my music", (_, _) => Task.FromResult(true));
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Open my music", CancellationToken.None);
        var opening = Task.Run(() => commands.ExecuteJarvisToolAsync("open_url", JsonSerializer.SerializeToElement(new { url = "my music" }), default));
        var command = await NextCommand(opening);
        Assert.Equal("https://open.spotify.com/", command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.True((bool)(await opening)["ok"]); Assert.Equal(0, requests); AssertNoCommand();
        await _automation.Connectors.ResetAsync();
    }

    [Fact]
    public void Connector_authority_identity_changes_only_for_real_new_user_input_not_tool_boundaries_or_fragments()
    {
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        commands.ObserveJarvisTranscript("Find invoices", default);
        var first = commands.ConnectorUserRequestIdentity;
        commands.ObserveJarvisTranscript("Find invoices from last week", default);
        Assert.Equal(first, commands.ConnectorUserRequestIdentity);
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        commands.ObserveJarvisTranscript("Find invoices from last week", default);
        Assert.Equal(first, commands.ConnectorUserRequestIdentity);
        commands.CompleteJarvisUserTurn();
        Assert.Equal(first, commands.ConnectorUserRequestIdentity); // Lifecycle/answer completion alone adds no authority.
        commands.ObserveJarvisTranscript("Find invoices from last week", default);
        var fresh = commands.ConnectorUserRequestIdentity;
        Assert.NotEqual(first, fresh);
        commands.ObserveSubmittedJarvisText("Find invoices from last week", default);
        var submitted = commands.ConnectorUserRequestIdentity;
        Assert.NotEqual(fresh, submitted);
        commands.ObserveJarvisTranscript("Find invoices from last week", default); // Provider echo of the typed submission.
        Assert.Equal(submitted, commands.ConnectorUserRequestIdentity);
    }

    [Fact]
    public async Task Completed_connector_answer_ends_settled_task_authority_without_a_model_complete_call()
    {
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        var taskId = Guid.NewGuid().ToString("N");
        var paths = new List<string>();
        _automation.Connectors = new JarvisConnectorController((method, path, body, token) =>
        {
            paths.Add(path);
            return Task.FromResult(JsonSerializer.SerializeToElement(path.EndsWith("/resolve", StringComparison.Ordinal)
                ? new { taskId, state = "ready", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    accountId = Guid.NewGuid().ToString("N"), capabilities = new[] { new { id = "mail_search", writes = false } } }
                : (object)new { state = "completed", verified = true }));
        }, () => "test-account", () => CancellationToken.None, () => commands.ConnectorUserRequest,
            (_, _) => Task.FromResult(true), userRequestIdentity: () => commands.ConnectorUserRequestIdentity);
        try
        {
            commands.ObserveJarvisTranscript("Find invoices", default);
            await commands.ExecuteJarvisToolAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), default);
            var step = JsonSerializer.SerializeToElement(new { task_id = taskId, capability_id = "mail_search", arguments_json = "{\"query\":\"invoice\"}" });
            Assert.True((bool)(await commands.ExecuteJarvisToolAsync("connector_execute", step, default))["ok"]);
            commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
            Assert.True((bool)(await _automation.Connectors.ExecuteAsync("connector_execute", step, true, default))["ok"]);
            Assert.Equal(2, paths.Count); // Tool-only boundary did not end the multi-step read.
            commands.CompleteJarvisUserTurn();
            Assert.False(_automation.Connectors.TryCancelSingleTask("cancel", out _));
            Assert.Contains("v1/connectors/tasks/" + taskId + "/complete", paths);
            Assert.False((bool)(await _automation.Connectors.ExecuteAsync("connector_execute", step, true, default))["ok"]);
        }
        finally { await _automation.Connectors.ResetAsync(); }
    }

    [Fact]
    public async Task Real_confirmation_turn_keeps_pending_connector_alive_then_releases_its_completed_authority()
    {
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        var taskId = Guid.NewGuid().ToString("N");
        var paths = new List<string>();
        var executions = 0;
        _automation.Connectors = new JarvisConnectorController((method, path, body, token) =>
        {
            paths.Add(path);
            if (path.EndsWith("/execute", StringComparison.Ordinal))
            {
                executions++;
                if (JsonSerializer.SerializeToElement(body).GetProperty("capabilityId").GetString() == "mail_send")
                    Assert.True(JsonSerializer.SerializeToElement(body).GetProperty("confirmed").GetBoolean());
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(path.EndsWith("/resolve", StringComparison.Ordinal)
                ? new { taskId, state = "ready", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    accountId = Guid.NewGuid().ToString("N"), capabilities = new[] {
                        new { id = "mail_search", writes = false }, new { id = "mail_send", writes = true } } }
                : (object)new { state = "completed", verified = true }));
        }, () => "test-account", () => CancellationToken.None, () => commands.ConnectorUserRequest,
            (_, _) => Task.FromResult(true), userRequestIdentity: () => commands.ConnectorUserRequestIdentity);
        try
        {
            commands.ObserveJarvisTranscript("Find the address and send a greeting", default);
            var originalIdentity = commands.ConnectorUserRequestIdentity;
            await commands.ExecuteJarvisToolAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), default);
            await commands.ExecuteJarvisToolAsync("connector_execute", JsonSerializer.SerializeToElement(new {
                task_id = taskId, capability_id = "mail_search", arguments_json = "{\"query\":\"address\"}" }), default);
            var send = JsonSerializer.SerializeToElement(new {
                task_id = taskId, capability_id = "mail_send", arguments_json = "{\"to\":\"self@example.test\"}" });
            var pending = await commands.ExecuteJarvisToolAsync("connector_execute", send, default);
            Assert.True((bool)pending["confirmation_required"]); Assert.Equal(1, executions);
            commands.CompleteJarvisUserTurn();
            Assert.DoesNotContain("v1/connectors/tasks/" + taskId + "/complete", paths);
            commands.ObserveJarvisTranscript("Confirm", default);
            Assert.NotEqual(originalIdentity, commands.ConnectorUserRequestIdentity);
            var confirmed = await commands.ExecuteJarvisToolAsync("confirm_sensitive_action", JsonSerializer.SerializeToElement(new { }), default);
            Assert.True((bool)confirmed["ok"]); Assert.Equal(2, executions); Assert.Null(_automation.PendingSnapshot());
            commands.CompleteJarvisUserTurn();
            Assert.False(_automation.Connectors.TryCancelSingleTask("cancel", out _));
            Assert.Contains("v1/connectors/tasks/" + taskId + "/complete", paths);
            Assert.Equal(1, paths.Count(path => path.EndsWith("/complete", StringComparison.Ordinal)));
        }
        finally { await _automation.Connectors.ResetAsync(); }
    }

    [Fact]
    public async Task Immediate_connector_cancel_receipt_survives_its_transcription_echo_and_is_not_executed_again()
    {
        var commands = new CommandRegistry(null, null, null, _settings, new NotificationService(null), automation: _automation);
        var taskId = Guid.NewGuid().ToString("N");
        var requests = 0;
        _automation.Connectors = new JarvisConnectorController((method, path, body, token) =>
        {
            ++requests;
            return Task.FromResult(JsonSerializer.SerializeToElement(path.EndsWith("/resolve", StringComparison.Ordinal)
                ? new { taskId, state = "ready", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), accountId = Guid.NewGuid().ToString("N"), capabilities = Array.Empty<object>() }
                : (object)new { state = "cancelled" }));
        }, () => "test-account", () => CancellationToken.None, () => commands.ConnectorUserRequest,
            (_, _) => Task.FromResult(true), userRequestIdentity: () => commands.ConnectorUserRequestIdentity);
        commands.ObserveJarvisTranscript("Find invoices", default);
        await commands.ExecuteJarvisToolAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), default);
        commands.ObserveImmediateJarvisControl("Cancel", default)!.Invoke();
        var beforeEcho = requests;
        commands.CompleteJarvisUserTurn(); // A late preceding provider answer cannot discard an unacknowledged control.
        commands.ObserveJarvisTranscript("Cancel", default);
        var result = await commands.ExecuteJarvisToolAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = taskId, action = "cancel" }), default);
        Assert.True((bool)result["ok"]); Assert.Equal("cancelled", result["state"]);
        Assert.Equal(beforeEcho, requests);
        var repeated = await commands.ExecuteJarvisToolAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = taskId, action = "cancel" }), default);
        Assert.True((bool)repeated["ok"]); Assert.Equal(beforeEcho, requests);
        await _automation.Connectors.ResetAsync();
    }

    private Task<Dictionary<string, object>> Execute(string tool, object args, CancellationToken token = default) =>
        _automation.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args), token);

    private async Task<string> Open(int tab)
    {
        var opening = Execute("open_url", new { url = $"https://example.org/{tab}", browser = "chrome" });
        var command = await NextCommand(opening);
        Assert.Equal("open_direct_tab", command.GetProperty("type").GetString());
        Reply(command, tab);
        var result = JsonSerializer.SerializeToElement(await opening);
        Assert.Equal("browser_tab", result.GetProperty("referential_target").GetProperty("kind").GetString());
        return result.GetProperty("referential_target").GetProperty("target_id").GetString()!;
    }

    private async Task<JsonElement> NextCommand(Task operation)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var command = Process("poll").GetProperty("command");
            if (command.ValueKind != JsonValueKind.Null) return command;
            Assert.False(operation.IsCompleted, "Operation ended before the expected Helper command.");
            await Task.Delay(10);
        }
        throw new TimeoutException("Expected Helper command did not arrive.");
    }

    private void Reply(JsonElement command, int tab, bool alreadyClosed = false) =>
        Process("command_result", command, new { ok = true, tabId = tab, windowId = 7, sessionId = "test-session", alreadyClosed });

    private void ReplySelection(JsonElement command, int tab, int tabCount) =>
        Process("command_result", command, new { ok = true, tabId = tab, windowId = 7, tabCount });

    private void AssertNoCommand() => Assert.Equal(JsonValueKind.Null, Process("poll").GetProperty("command").ValueKind);

    private JsonElement Process(string type, JsonElement command = default, object result = null, bool browserWindowPresent = true)
    {
        var json = JsonSerializer.Serialize(new
        {
            type, result, taskId = command.ValueKind == JsonValueKind.Object ? command.GetProperty("taskId").GetString() : "",
            commandId = command.ValueKind == JsonValueKind.Object ? command.GetProperty("commandId").GetString() : "",
            helper = new
            {
                extensionId = "gojdebdikepkpapmnggeljneamjdigbh", serviceWorkerVersion = "2.1.21", contentScriptVersion = _contentScriptAvailable ? "2.1.21" : "",
                activeTabFresh = _contentScriptAvailable, commandChannelReady = _contentScriptAvailable, commandAuthoritySupported = true,
                activeUrl = "https://example.org/", activeTitle = "Example", sessionId = "test-session",
                windowId = browserWindowPresent ? (int?)7 : null, tabId = browserWindowPresent ? (int?)10 : null,
            },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var method = typeof(BrowserHelperBridge).GetMethod("ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return JsonDocument.Parse((string)method.Invoke(_bridge, new object[] { json })!).RootElement.Clone();
    }

    public void Dispose()
    {
        _automation.Dispose();
        _bridge.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
