namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorRetainedOpenDispatchTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly string _task = Guid.NewGuid().ToString("N"), _account = Guid.NewGuid().ToString("N");
    private readonly SettingsService _settings;
    private readonly JarvisAutomationService _automation;
    private readonly CommandRegistry _commands;
    private readonly JarvisConnectorController _controller;
    private readonly CancellationTokenSource _session = new();
    private readonly List<string> _urls = [], _requests = [];
    private string _owner = "signed-in-user";
    private int _accountLookups;
    private bool _cancelAfterFirst;

    public ConnectorRetainedOpenDispatchTests()
    {
        _settings = new(Path.Combine(_directory, "settings.json"));
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(Path.Combine(_directory, "mappings.json"));
        _automation = new(null, _settings, new(Path.Combine(_directory, "history.json")), mappings, () => { }, null, null, null,
            simpleUrlLauncher: (url, browser) =>
            {
                _urls.Add(url);
                if (_cancelAfterFirst && _urls.Count == 1) _session.Cancel();
                return (true, "Opened.", "fixture");
            });
        _commands = new(null, null, null, _settings, new NotificationService(null), automation: _automation);
        _controller = new(Request, () => { _accountLookups++; return _owner; }, () => _session.Token,
            () => _commands.ConnectorUserRequest, (_, _) => Task.FromResult(true),
            userRequestIdentity: () => _commands.ConnectorUserRequestIdentity,
            userRequestSnapshot: () => _commands.ConnectorUserRequestSnapshot);
        _automation.Connectors = _controller;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await _controller.ResetAsync();
        _session.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
    {
        _requests.Add(path);
        if (path.EndsWith("/resolve")) return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            taskId = _task, state = "ready", connectorId = "gmail", accountId = _account,
            choices = new[] { new { accountId = _account, label = "reader@example.test" } }, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching Gmail…",
                arguments = new Dictionary<string, string> { ["query"] = "Search terms", ["matchMode"] = "exact or topic" } } }
        }));
        if (path.EndsWith("/execute")) return Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            state = "completed", verified = true, result = new { truncated = false, data = new
            {
                messages = new[] { new { id = "11111111", threadId = "aaaaaaaa", subject = "First conversation" },
                    new { id = "22222222", threadId = "bbbbbbbb", subject = "Second conversation" } },
                hasMore = false, pageItemCount = 2
            } }
        }));
        return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true }));
    }

    private Task<Dictionary<string, object>> Tool(string name, object arguments) =>
        _commands.ExecuteJarvisToolAsync(name, JsonSerializer.SerializeToElement(arguments), default);
    private void Input(string text)
    {
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveSubmittedJarvisText(text, default);
    }
    private async Task RetainPair()
    {
        Input("Find recent invoices.");
        Assert.Equal(true, (await Tool("connector_resolve", new { connector_id = "gmail" }))["ok"]);
        Assert.Equal(true, (await Tool("connector_execute", new { task_id = _task, capability_id = "mail_search",
            arguments_json = "{\"query\":\"invoice\",\"matchMode\":\"exact\"}" }))["ok"]);
        Assert.Equal(true, (await Tool("connector_control", new { task_id = _task, action = "complete" }))["ok"]);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Two_conflicting_model_open_proposals_dispatch_the_two_retained_threads_once()
    {
        await RetainPair();
        var requests = _requests.Count;
        Input("Open both separately in new tabs.");
        var first = await Tool("open_url", new { url = "https://mail.google.com/mail/u/0/#inbox/CtWLlTTscGXjJqpTGnBlql" });
        var second = await Tool("open_url", new { url = "https://mail.google.com/mail/u/0/#inbox" });
        Assert.Equal(true, first["ok"]);
        Assert.Equal(true, second["ok"]);
        Assert.Equal(new[] { "https://mail.google.com/mail/?authuser=reader%40example.test#all/aaaaaaaa",
            "https://mail.google.com/mail/?authuser=reader%40example.test#all/bbbbbbbb" }, _urls);
        Assert.Equal(requests, _requests.Count);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);

        Input("Open both separately in new tabs.");
        Assert.Equal(true, (await Tool("open_url", new { url = "https://mail.google.com/" }))["ok"]);
        Assert.Equal(4, _urls.Count);
        Assert.Equal(_urls.Take(2), _urls.Skip(2));
        Assert.Equal(requests, _requests.Count);
    }

    [Fact]
    public async Task Ordinary_Gmail_open_ignores_retained_connector_context_and_account_owner()
    {
        await RetainPair();
        var requests = _requests.Count;
        Input("Open Gmail.");
        var lookups = _accountLookups;
        Assert.Equal(true, (await Tool("open_url", new { url = "https://mail.google.com/" }))["ok"]);
        Assert.Equal("https://mail.google.com/", Assert.Single(_urls));
        Assert.Equal(lookups, _accountLookups);
        Assert.Equal(requests, _requests.Count);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    [Fact]
    public async Task Changed_signed_in_account_blocks_retained_opening_without_homepage_fallback()
    {
        await RetainPair();
        Input("Open both in separate tabs.");
        _owner = "different-signed-in-user";
        var result = await Tool("open_url", new { url = "https://mail.google.com/" });
        Assert.Equal(false, result["ok"]);
        Assert.Empty(_urls);
    }

    [Fact]
    public async Task Session_cancelled_after_first_dispatch_prevents_second_and_repeated_proposal()
    {
        await RetainPair();
        Input("Open both in separate tabs.");
        _cancelAfterFirst = true;
        Assert.Equal(false, (await Tool("open_url", new { url = "https://mail.google.com/" }))["ok"]);
        Assert.Single(_urls);
        Assert.Equal(false, (await Tool("open_url", new { url = "https://mail.google.com/" }))["ok"]);
        Assert.Single(_urls);
    }

    [Fact]
    public async Task Unknown_first_dispatch_is_not_retried_from_a_later_model_proposal()
    {
        await RetainPair();
        Input("Open both in separate tabs.");
        var calls = 0;
        Task<Dictionary<string, object>> Dispatch(string url, CancellationToken token)
        {
            calls++;
            throw new HttpRequestException("Fixture unknown browser receipt.");
        }
        var first = await _controller.TryOpenRetainedResourcesAsync(_commands.ConnectorUserRequestSnapshot, Dispatch, default);
        var second = await _controller.TryOpenRetainedResourcesAsync(_commands.ConnectorUserRequestSnapshot, Dispatch, default);
        Assert.Equal("unknown", first["state"]);
        Assert.Equal("unknown", second["state"]);
        Assert.Equal(1, calls);
        Assert.False((bool)first["navigation_verified"]);
    }
}
