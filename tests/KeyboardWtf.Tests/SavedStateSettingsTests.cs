namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class SavedStateSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-saved-state-tests", Guid.NewGuid().ToString("N"));
    private SettingsService Settings() => new(Path.Combine(_root, "settings.json"));
    private LearnedMappingService Mappings() => new(Path.Combine(_root, "mappings.json"));
    private IntentMemoryService Memory() => new(Path.Combine(_root, "memory.json"));

    [Fact]
    public async Task Invalidation_follows_successful_persistence_and_coalesces_without_saved_content()
    {
        var settings = Settings();
        var mappings = Mappings();
        var memory = Memory();
        using var notifications = new SavedStateNotifications(settings, mappings, memory);
        var channel = notifications.Subscribe();
        Assert.Equal(0, await channel.Reader.ReadAsync());
        var persistedAtNotification = false;
        mappings.Changed += () => persistedAtNotification = File.ReadAllText(mappings.MappingPath).Contains("edited", StringComparison.Ordinal);
        var original = mappings.Remember(LearnedMappingKind.Link, "my test link", "https://example.org/original");
        var updated = mappings.RememberExclusive(LearnedMappingKind.Link, "MY test LINK", "https://example.org/edited", "edited");
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.CreatedAt, updated.CreatedAt);
        Assert.True(persistedAtNotification);
        Assert.Equal(2, await channel.Reader.ReadAsync());
        Assert.False(mappings.Forget("missing"));
        Assert.Throws<InvalidOperationException>(() => mappings.Remember(LearnedMappingKind.Link, "invalid", "invalid"));
        Assert.Throws<InvalidOperationException>(() => settings.SaveWorkflow("invalid", [], []));
        Assert.Equal(2, notifications.Revision);

        settings.SaveWorkflow("start test", ["start test"], [new() { Kind = "url", Target = "https://example.org/" }]);
        settings.UpdateWorkflow("start test", "start edited test", [], [new() { Kind = "url", Target = "https://example.org/edited" }]);
        settings.DeleteWorkflow("start edited test");
        Assert.Equal(5, notifications.Revision);
        memory.Remember("test", "explicit test memory");
        memory.Forget("test");
        Assert.Equal(7, notifications.Revision);
        Assert.Equal(7, await channel.Reader.ReadAsync());
        notifications.Unsubscribe(channel);
        Assert.Equal(0, notifications.SubscriberCount);
        Assert.True(channel.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void Failed_persistence_does_not_send_a_success_invalidation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "blocked.json"));
        var mappings = new LearnedMappingService(Path.Combine(_root, "blocked.json"));
        using var notifications = new SavedStateNotifications(null, mappings, null);
        var failure = Assert.ThrowsAny<Exception>(() => mappings.Remember(LearnedMappingKind.Link, "test", "https://example.org/"));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(0, notifications.Revision);
    }

    [Fact]
    public async Task Disposal_detaches_store_events_and_completes_subscribers()
    {
        var mappings = Mappings();
        var notifications = new SavedStateNotifications(null, mappings, null);
        var channel = notifications.Subscribe();
        await channel.Reader.ReadAsync();
        notifications.Dispose();
        mappings.Remember(LearnedMappingKind.Link, "after close", "https://example.org/");
        Assert.Equal(0, notifications.Revision);
        Assert.Equal(0, notifications.SubscriberCount);
        await channel.Reader.Completion;
    }

    [Fact]
    public void Workflow_reference_projects_its_authoritative_actions_without_rewriting_legacy_records()
    {
        var mappings = Mappings();
        var settings = Settings();
        var workflow = settings.SaveWorkflow("please open my harbor link", [], [new() { Kind = "url", Target = "https://example.org/harbor" }]);
        var reference = mappings.Remember(LearnedMappingKind.Workflow, workflow.Name, workflow.Name, workflow.Name);
        var view = Assert.Single(WebSettingsService.ProjectSavedMappings(mappings.Snapshot(), settings.Current.JarvisWorkflows));
        Assert.Equal("Workflow", view.ReadableKind);
        Assert.Equal("active", view.State);
        Assert.Equal(workflow.Name, view.WorkflowName);
        Assert.Equal("https://example.org/harbor", Assert.Single(view.ResolvedTargets));
        Assert.Equal(workflow.Name, mappings.FindExact(LearnedMappingKind.Workflow, workflow.Name).Target);
        Assert.Equal(reference.Id, view.Id);
        var reloaded = new LearnedMappingService(mappings.MappingPath);
        reloaded.Load();
        Assert.Equal(reference.Target, Assert.Single(reloaded.Snapshot()).Target);
        Assert.Equal("missing", Assert.Single(WebSettingsService.ProjectSavedMappings(reloaded.Snapshot(), [])).State);
        workflow.Actions = [new() { Kind = "auto", Target = "the selected link" }];
        Assert.Equal("needs-repair", Assert.Single(WebSettingsService.ProjectSavedMappings(reloaded.Snapshot(), [workflow])).State);
    }

    [Fact]
    public void Deleting_a_workflow_row_removes_authority_and_all_references_but_keeps_unrelated_Link()
    {
        var settings = Settings();
        var mappings = Mappings();
        settings.SaveWorkflow("start test", ["test alias"], [new() { Kind = "url", Target = "https://example.org/" }]);
        var reference = mappings.Remember(LearnedMappingKind.Workflow, "test reference", "start test");
        mappings.Remember(LearnedMappingKind.Workflow, "test alias", "start test");
        var unrelated = mappings.Remember(LearnedMappingKind.Link, "unrelated", "https://example.org/unrelated");
        Assert.True(WebSettingsService.DeleteSavedMapping(settings, mappings, reference.Id));
        Assert.Empty(settings.Current.JarvisWorkflows);
        Assert.True(settings.IsWorkflowDeleted("start test"));
        Assert.Equal(unrelated.Id, Assert.Single(mappings.Snapshot()).Id);
        var persisted = new LearnedMappingService(mappings.MappingPath);
        persisted.Load();
        Assert.Equal(unrelated.Id, Assert.Single(persisted.Snapshot()).Id);
    }

    [Fact]
    public void Bulk_clear_is_explicitly_limited_to_non_workflow_mappings()
    {
        var mappings = Mappings();
        var workflow = mappings.Remember(LearnedMappingKind.Workflow, "start test", "start test");
        mappings.Remember(LearnedMappingKind.Link, "test link", "https://example.org/");
        Assert.Equal(1, WebSettingsService.ClearOrdinarySavedMappings(mappings));
        Assert.Equal(workflow.Id, Assert.Single(mappings.Snapshot()).Id);
    }

    [Fact]
    public void Saved_snapshot_and_stream_reuse_the_existing_Settings_session_guard_without_URL_tokens()
    {
        var guard = WebSettingsService.BuildSettingsRequestGuardScript("test-only-session-value");
        Assert.Contains("target.pathname.startsWith('/api/saved-state')", guard);
        Assert.Contains("headers.set('X-Keyboard-Wtf-Settings-Action', actionToken)", guard);
        Assert.Contains("isSettingsSessionRequest && response.status === 403", guard);
        Assert.DoesNotContain("searchParams", guard);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
