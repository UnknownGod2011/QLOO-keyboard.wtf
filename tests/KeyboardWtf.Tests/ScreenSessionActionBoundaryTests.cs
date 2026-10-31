using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class ScreenSessionActionBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-screen-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly JarvisAutomationService _automation;
    private readonly GeminiScreenGuidanceService _screen;
    private readonly List<byte[]> _analyzed = [];
    private int _captures;

    public ScreenSessionActionBoundaryTests()
    {
        _settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        _automation = new(null, _settings, new(Path.Combine(_directory, "history.json")),
            new(Path.Combine(_directory, "mappings.json")), () => { });
        _screen = new(null, _ => [(byte)++_captures], (_, image, _, _) =>
        {
            _analyzed.Add(image);
            return Task.FromResult("FULL_GUIDANCE_" + image[0] + "_END");
        });
        var field = typeof(JarvisAutomationService).GetField("_screenGuidance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((GeminiScreenGuidanceService)field.GetValue(_automation)!).Dispose();
        field.SetValue(_automation, _screen);
    }

    [Fact]
    public async Task Ordinary_fresh_and_exact_prior_id_reach_the_existing_executor_once()
    {
        var fresh = await Execute(new { request = "Read the labels." });
        Assert.Equal(true, fresh["ok"]);
        Assert.Equal(false, fresh["screen_reused"]);
        var id = Assert.IsType<string>(fresh["screen_context_id"]);
        Assert.NotEmpty(id);
        var prior = await Execute(new { request = "What color was the text?", screen_context_id = id });
        Assert.Equal(true, prior["ok"]);
        Assert.Equal(true, prior["screen_reused"]);
        Assert.Equal(id, prior["screen_context_id"]);
        Assert.Equal(fresh["screen_captured_utc"], prior["screen_captured_utc"]);
        Assert.Equal("FULL_GUIDANCE_1_END", prior["guidance"]);
        Assert.Equal(1, _captures);
        Assert.Equal(2, _analyzed.Count);
        Assert.Same(_analyzed[0], _analyzed[1]);
        var receipt = JsonSerializer.Serialize(prior);
        Assert.DoesNotContain("base64", receipt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(false, prior["capture_persisted"]);
        Assert.Equal(false, prior["automation_performed"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid-or-ended-id")]
    public async Task Present_invalid_id_is_not_treated_as_a_fresh_capture(string id)
    {
        var result = await Execute(new { request = "Recall that image.", screen_context_id = id });
        Assert.Equal(false, result["ok"]);
        Assert.Contains("no longer available", result["message"].ToString());
        Assert.Equal(0, _captures);
        Assert.Empty(_analyzed);
    }

    [Fact]
    public async Task Settings_check_does_not_replace_the_approved_Jarvis_image()
    {
        var fresh = await Execute(new { request = "Screen A." });
        var settings = await _automation.InspectScreenFromSettingsAsync("Settings check.", CancellationToken.None);
        Assert.Equal(true, settings["ok"]);
        Assert.Equal("", settings["screen_context_id"]);
        var prior = await Execute(new { request = "Old detail.", screen_context_id = fresh["screen_context_id"] });
        Assert.Equal(true, prior["ok"]);
        Assert.Equal("FULL_GUIDANCE_1_END", prior["guidance"]);
        Assert.Equal(2, _captures);
        Assert.Same(_analyzed[0], _analyzed[2]);
    }

    [Fact]
    public async Task Real_registry_session_end_clears_the_image_but_normal_turn_completion_does_not()
    {
        var fresh = await Execute(new { request = "Screen A." });
        var commands = new CommandRegistry(null, null, null, _settings, null, automation: _automation);
        commands.CompleteJarvisUserTurn();
        var prior = await Execute(new { request = "Old detail.", screen_context_id = fresh["screen_context_id"] });
        Assert.Equal(true, prior["ok"]);
        await commands.HandleJarvisSessionEndingAsync();
        var ended = await Execute(new { request = "Old detail.", screen_context_id = fresh["screen_context_id"] });
        Assert.Equal(false, ended["ok"]);
        Assert.Equal(1, _captures);
        Assert.Equal(2, _analyzed.Count);
    }

    [Fact]
    public async Task Permission_confirmation_is_bound_to_the_exact_prior_image_not_a_newer_capture()
    {
        var fresh = await Execute(new { request = "Screen A." });
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        var pending = await Execute(new { request = "Old detail.", screen_context_id = fresh["screen_context_id"] });
        Assert.Equal(true, pending["confirmation_required"]);
        Assert.Contains("previously approved", pending["description"].ToString());
        Assert.Equal(1, _captures);
        await _screen.InspectSessionAsync("Screen B.", null, CancellationToken.None);
        _automation.ObserveUserTranscript("Confirm");
        var confirmed = await _automation.ExecuteAsync("confirm_sensitive_action",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Equal(false, confirmed["ok"]);
        Assert.Contains("no longer available", confirmed["message"].ToString());
        Assert.Equal(2, _captures);
        Assert.Equal(2, _analyzed.Count);
    }

    private Task<Dictionary<string, object>> Execute(object args) =>
        _automation.ExecuteAsync("inspect_screen", JsonSerializer.SerializeToElement(args), CancellationToken.None);

    public void Dispose()
    {
        _automation.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
