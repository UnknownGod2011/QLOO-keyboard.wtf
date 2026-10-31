using System.Diagnostics;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class ConnectorNavigationRegressionTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly LearnedMappingService _mappings;
    private readonly JarvisAutomationService _automation;
    private readonly CommandRegistry _commands;
    private readonly JarvisConnectorController _controller;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _urls = [];
    private readonly List<string> _requests = [];
    private int _owners;
    private int _reasoningCalls;
    private readonly string _taskId = Guid.NewGuid().ToString("N");

    public ConnectorNavigationRegressionTests(ITestOutputHelper output)
    {
        _output = output;
        _settings = new(Path.Combine(_directory, "settings.json"));
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        _mappings = new(Path.Combine(_directory, "mappings.json"));
        _automation = new(null, _settings, new(Path.Combine(_directory, "history.json")), _mappings, () => { }, null, null, null,
            simpleUrlLauncher: (url, browser) => { _urls.Add(url); return (true, "Opened.", "fixture"); });
        _commands = new(null, null, null, _settings, new NotificationService(null), automation: _automation);
        _controller = new((method, path, body, token) =>
        {
            _requests.Add(path);
            return Task.FromResult(JsonSerializer.SerializeToElement(path.EndsWith("/resolve")
                ? (object)new { taskId = _taskId, state = "ready", accountId = Guid.NewGuid().ToString("N"),
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), capabilities = Array.Empty<object>() }
                : new { state = "completed", verified = true }));
        }, () => { ++_owners; return "fixture-owner"; }, () => CancellationToken.None,
            () => _commands.ConnectorUserRequest, (_, _) => Task.FromResult(true),
            userRequestIdentity: () => _commands.ConnectorUserRequestIdentity,
            reasonRead: (_, _, _, _, _, _, _) => { ++_reasoningCalls; return Task.FromResult("{}"); });
        _automation.Connectors = _controller;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await _controller.ResetAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private Task<Dictionary<string, object>> Tool(string name, object args) =>
        _commands.ExecuteJarvisToolAsync(name, JsonSerializer.SerializeToElement(args), default);
    private void Input(string text) => _commands.ObserveSubmittedJarvisText(text, default);
    private void SaveWorkflow(string name, string target, string kind = "url")
    {
        _settings.Current.JarvisWorkflows = [new() { Version = 2, Name = name, Aliases = [name],
            Actions = [new() { Kind = kind, Target = target }] }];
        _mappings.Remember(LearnedMappingKind.Workflow, name, name);
    }

    [Theory]
    [InlineData("Open Gmail.")]
    [InlineData("Open Discord.")]
    [InlineData("Open GitHub.")]
    [InlineData("Open Google Drive.")]
    [InlineData("Open Instagram.")]
    [InlineData("Open Devpost.")]
    [InlineData("Open Gmail and Instagram.")]
    [InlineData("Open Gmail, Instagram, Discord and Devpost.")]
    [InlineData("Bring Chrome to the foreground and open Gmail.")]
    [InlineData("Could you please bring Chrome to the foreground and open Gmail?")]
    [InlineData("Please focus Chrome and then open Gmail.")]
    [InlineData("Switch to Chrome, then open Gmail and Instagram.")]
    [InlineData("Activate the Chrome window and open Gmail.")]
    [InlineData("Bring my Edge browser to the front and visit Gmail.")]
    [InlineData("Open Gmail and bring Chrome to the foreground.")]
    [InlineData("Please open Gmail, then focus Chrome.")]
    public async Task Navigation_cannot_admit_a_connector_even_when_the_model_proposes_it(string request)
    {
        Input(request);
        var started = Stopwatch.GetTimestamp();
        var result = await Tool("connector_resolve", new { connector_id = "gmail" });
        _output.WriteLine($"proposal-to-rejection-ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
        Assert.Equal("ordinary_navigation_required", result["state"]);
        Assert.Empty(_requests);
        Assert.Equal(0, _owners);
        Assert.Equal(0, _reasoningCalls);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        // The controller is independently guarded, even without CommandRegistry.
        await _controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
        Assert.Empty(_requests);
        Assert.Equal(0, _owners);
        Assert.Equal(0, _reasoningCalls);
    }

    [Theory]
    [InlineData("Check my Gmail.", "gmail")]
    [InlineData("Find the BioCyclic winning email.", "gmail")]
    [InlineData("Find an email from Daisy.", "gmail")]
    [InlineData("Reply to that email.", "gmail")]
    [InlineData("Find a Drive document.", "googledrive")]
    [InlineData("Search GitHub for my issue.", "github")]
    [InlineData("What meetings do I have tomorrow?", "googlecalendar")]
    [InlineData("Did a professor reply?", "gmail")]
    [InlineData("Open Gmail and check today's emails.", "gmail")]
    [InlineData("Bring Chrome to the foreground and open Gmail and read Daisy's email.", "gmail")]
    [InlineData("Please focus Chrome and check my Gmail.", "gmail")]
    [InlineData("Open Gmail and bring Chrome to the foreground and read the bodies.", "gmail")]
    [InlineData("Activate Chrome and open Gmail and send Daisy an email.", "gmail")]
    [InlineData("Switch to Chrome and open Gmail and reply to that email.", "gmail")]
    [InlineData("Bring Chrome to the foreground and open that conversation thread.", "gmail")]
    public async Task Connected_data_requests_still_resolve(string request, string provider)
    {
        Input(request);
        var result = await Tool("connector_resolve", new { connector_id = provider });
        Assert.Equal(true, result["ok"]);
        Assert.Single(_requests);
        Assert.EndsWith("/resolve", _requests[0]);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Open Gmail.", "https://mail.google.com/")]
    [InlineData("Open Devpost.", "https://devpost.com/")]
    [InlineData("Bring Chrome to the foreground and open Gmail.", "https://mail.google.com/")]
    [InlineData("Open Gmail and bring Chrome to the foreground.", "https://mail.google.com/")]
    public async Task Ordinary_open_dispatch_has_zero_connector_work(string request, string url)
    {
        Input(request);
        await Tool("connector_resolve", new { connector_id = "gmail" });
        var started = Stopwatch.GetTimestamp();
        Assert.Equal(true, (await Tool("open_url", new { url }))["ok"]);
        _output.WriteLine($"proposal-to-completion-ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}; backend=0; connector-model=0");
        Assert.Equal(url, Assert.Single(_urls));
        Assert.Empty(_requests);
        Assert.Equal(0, _owners);
        Assert.Equal(0, _reasoningCalls);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Two_site_open_keeps_ordinary_actions_after_rejected_broker()
    {
        Input("Open Gmail and Instagram.");
        await Tool("connector_resolve", new { connector_id = "gmail" });
        foreach (var url in new[] { "https://mail.google.com/", "https://www.instagram.com/" })
            Assert.Equal(true, (await Tool("open_url", new { url }))["ok"]);
        Assert.Equal(2, _urls.Count);
        Assert.Empty(_requests);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Theory]
    [InlineData("Open Discord.")]
    [InlineData("Open Gmail, Instagram, Discord and Devpost.")]
    public async Task Actual_staging_workflow_shape_wins_before_broker_and_conflicting_service_tool(string request)
    {
        SaveWorkflow("open discord", "https://discord.com/app");
        Input(request);
        Assert.Equal(true, (await Tool("connector_resolve", new { connector_id = "gmail" }))["ok"]);
        await Tool("open_service_page", new { service = "discord", page = "home" });
        Assert.Equal(1, _urls.Count(url => url == "https://discord.com/app"));
        Assert.Equal(request.Contains("Gmail") ? 4 : 1, _urls.Count);
        Assert.Empty(_requests);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal("https://discord.com/app", Assert.Single(Assert.Single(_settings.Current.JarvisWorkflows).Actions).Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_authority_corrects_late_compound_service_guess_for_links_and_workflows(bool workflow)
    {
        const string destination = "https://example.org/preferred";
        if (workflow) SaveWorkflow("open example service", destination);
        else _mappings.Remember(LearnedMappingKind.Link, "open example service", destination);
        Input("Please also open example service and then tell me the time.");
        Assert.Equal(true, (await Tool("open_service_page", new { service = "example service", page = "home" }))["ok"]);
        Assert.Equal(destination, Assert.Single(_urls));
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("Open Discord web version.", "https://discord.com/app")]
    [InlineData("Open Reddit in the browser.", "https://www.reddit.com/")]
    [InlineData("Open YouTube website.", "https://www.youtube.com/")]
    [InlineData("Open GitHub web version.", "https://github.com/")]
    public async Task Explicit_web_navigation_overrides_a_native_service_guess(string request, string url)
    {
        Input(request);
        Assert.Equal(true, (await Tool("open_service_page", new { service = "discord", page = "home" }))["ok"]);
        Assert.Equal(url, Assert.Single(_urls));
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("app", "open discord web version", "https://discord.com/app")]
    [InlineData("app", "discord on the web", "https://discord.com/app")]
    [InlineData("auto", "open Reddit in the browser", "https://www.reddit.com/")]
    [InlineData("auto", "open YouTube website", "https://www.youtube.com/")]
    [InlineData("auto", "open GitHub web version", "https://github.com/")]
    public async Task Existing_semantic_workflow_actions_execute_web_intent_without_rewriting_storage(string kind, string target, string url)
    {
        SaveWorkflow("my saved destination", target, kind);
        Input("my saved destination");
        Assert.Equal(true, (await Tool("run_workflow", new { name = "my saved destination" }))["ok"]);
        Assert.Equal(url, Assert.Single(_urls));
        Assert.Equal(target, Assert.Single(Assert.Single(_settings.Current.JarvisWorkflows).Actions).Target);
    }

    [Fact]
    public async Task Resolve_only_answer_clears_activity_but_tool_boundary_and_unrelated_answer_do_not()
    {
        Input("Check my Gmail.");
        await Tool("connector_resolve", new { connector_id = "gmail" });
        var identity = _commands.ConnectorUserRequestIdentity;
        _commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        _controller.CompleteSettledUserTurn("unrelated");
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        _controller.CompleteSettledUserTurn(identity, _taskId);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        _controller.CompleteSettledUserTurn(identity);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, _requests.Count(path => path.EndsWith("/complete")));
        _controller.CompleteSettledUserTurn(identity);
        Assert.Equal(1, _requests.Count(path => path.EndsWith("/complete")));
    }
}
