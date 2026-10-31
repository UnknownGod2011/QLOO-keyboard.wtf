using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class SavedSetupCapacityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-saved-capacity-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(20_000, false)]
    [InlineData(25_000, true)]
    [InlineData(40_000, true)]
    [InlineData(120_000, true)]
    public void Notice_threshold_is_informational_and_never_changes_saved_records(int characters, bool expected)
    {
        var target = "https://example.org/" + new string('a', characters - "https://example.org/".Length);
        var workflow = new JarvisWorkflowSettings
        {
            Actions = [new() { Kind = "url", Target = target }],
            Urls = target, // Compatibility projection must not count it twice.
        };
        var measured = WebSettingsService.SavedSetupCharacters([workflow], []);
        Assert.Equal(characters + 3, measured);
        Assert.Equal(expected, measured >= WebSettingsService.LargeSavedSetupWarningCharacters);
        Assert.Equal(target, Assert.Single(workflow.Actions).Target);
        Assert.Equal(target, workflow.Urls);
    }

    [Fact]
    public void Measurement_includes_mapping_identities_and_legacy_workflow_targets_without_metadata()
    {
        var mapping = new LearnedMappingEntry { Alias = "alias", Target = "target", DisplayName = "label" };
        var workflow = new JarvisWorkflowSettings { Name = "name", Aliases = ["alternate"], Apps = "app", Urls = "url", Folder = "folder" };
        Assert.Equal(16 + 25, WebSettingsService.SavedSetupCharacters([workflow], [mapping]));
        Assert.Equal(0, WebSettingsService.SavedSetupCharacters(null, null));
    }

    [Fact]
    public void Dismissal_survives_reload_and_does_not_rewrite_workflow_identity_or_action()
    {
        var path = Path.Combine(_root, "settings.json");
        var settings = new SettingsService(path);
        var original = settings.SaveWorkflow("launch project", ["my project"],
            [new() { Kind = "url", Target = "https://example.org/project" }]);
        settings.DismissLargeSavedSetupWarning();
        var reloaded = new SettingsService(path);
        reloaded.Load();
        Assert.True(reloaded.Current.LargeSavedSetupWarningDismissed);
        var workflow = Assert.Single(reloaded.Current.JarvisWorkflows);
        Assert.Equal(original.Name, workflow.Name);
        Assert.Equal(original.Aliases, workflow.Aliases);
        Assert.Equal("https://example.org/project", Assert.Single(workflow.Actions).Target);
        reloaded.SaveWorkflow("another project", [], [new() { Kind = "url", Target = "https://example.org/another" }]);
        Assert.True(reloaded.Current.LargeSavedSetupWarningDismissed);
    }

    [Fact]
    public void Failed_dismissal_persistence_does_not_claim_acknowledgement()
    {
        var path = Path.Combine(_root, "blocked.json");
        Directory.CreateDirectory(path);
        var settings = new SettingsService(path);
        var failure = Assert.ThrowsAny<Exception>(() => settings.DismissLargeSavedSetupWarning());
        Assert.IsType<KeyboardWtf.Helpers.SecretPersistenceException>(failure);
        Assert.Equal(KeyboardWtf.Helpers.SecretPersistenceException.SafeMessage, failure.Message);
        Assert.False(settings.Current.LargeSavedSetupWarningDismissed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
