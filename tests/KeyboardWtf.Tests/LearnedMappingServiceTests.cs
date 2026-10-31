namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class LearnedMappingServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public LearnedMappingServiceTests()
    {
        _path = Path.Combine(_directory, "learned-mappings.json");
    }

    [Fact]
    public void MappingsPersistAndCanBeForgotten()
    {
        var service = new LearnedMappingService(_path);
        service.Load();
        service.Remember(LearnedMappingKind.App, "my codex", "appx:OpenAI.Codex_2p2nqsd0c76g0!App", "Codex");

        var reloaded = new LearnedMappingService(_path);
        reloaded.Load();

        var entry = reloaded.FindExact(LearnedMappingKind.App, "my codex");
        Assert.NotNull(entry);
        Assert.Equal("Codex", entry.DisplayName);
        Assert.True(reloaded.Forget(entry.Id));
        Assert.Empty(reloaded.Snapshot());
    }

    [Fact]
    public void Failed_publish_cleans_staging_without_replacing_existing_mappings()
    {
        var service = new LearnedMappingService(_path);
        service.Load();
        service.Remember(LearnedMappingKind.Link, "docs", "https://example.com/original");
        var original = File.ReadAllBytes(_path);
        using (var pinned = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            // Windows reports a blocked overwrite as sharing violation or
            // access denied; neither may acknowledge a successful save.
            Assert.True(Record.Exception(() => service.Remember(
                LearnedMappingKind.Link, "docs", "https://example.com/changed"))
                is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Failed_publish_preserves_unrelated_files_and_directory_destination()
    {
        Directory.CreateDirectory(_path);
        var sentinel = Path.Combine(_path, "keep.txt");
        File.WriteAllText(sentinel, "untouched");
        var service = new LearnedMappingService(_path);
        Assert.True(Record.Exception(() => service.Remember(
            LearnedMappingKind.Link, "docs", "https://example.com")) is IOException or UnauthorizedAccessException);
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void FileMappingsRequireAbsolutePaths()
    {
        var service = new LearnedMappingService(_path);
        Assert.Throws<InvalidOperationException>(() =>
            service.Remember(LearnedMappingKind.File, "resume", "resume.pdf"));
    }

    [Fact]
    public void MappingStoreStaysBounded()
    {
        var service = new LearnedMappingService(_path);
        for (var index = 0; index < LearnedMappingService.MaxEntries + 5; index++)
            service.Remember(LearnedMappingKind.Link, $"link {index}", $"https://example.com/{index}");

        Assert.Equal(LearnedMappingService.MaxEntries, service.Snapshot().Count);
    }

    [Fact]
    public void ExclusivePreferenceReplacesSupersededKindInOneDurableRecord()
    {
        var service = new LearnedMappingService(_path);
        service.Load();
        service.Remember(
            LearnedMappingKind.App,
            "My service",
            "appx:Example.App_123!App",
            "Example App");

        service.RememberExclusive(
            LearnedMappingKind.Link,
            "my-service",
            "https://example.com/app",
            "Example web",
            LearnedMappingKind.App,
            LearnedMappingKind.Link);

        var reloaded = new LearnedMappingService(_path);
        reloaded.Load();
        Assert.Null(reloaded.FindExact(LearnedMappingKind.App, "my service"));
        Assert.Equal(
            "https://example.com/app",
            reloaded.FindExact(LearnedMappingKind.Link, "MY_service")?.Target);
        Assert.Equal(
            1,
            reloaded.ForgetAcrossKinds(
                "my service",
                LearnedMappingKind.App,
                LearnedMappingKind.Link));
        Assert.Empty(reloaded.Snapshot());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
        catch { }
    }

    [Fact]
    public void Live_link_capabilities_are_bounded_and_do_not_expose_stored_targets()
    {
        var service = new LearnedMappingService(_path);
        Assert.Equal("", service.BuildLinkAliasPromptDigest());
        for (var i = 0; i < 80; i++)
            service.Remember(LearnedMappingKind.Link, new string('x', 65) + i, $"https://example.org/private?token=secret-{i}");
        service.Remember(LearnedMappingKind.Workflow, "not-a-link", "separate workflow");
        var digest = service.BuildLinkAliasPromptDigest();
        Assert.True(digest.Length < 1100);
        Assert.Contains("open_url", digest);
        Assert.DoesNotContain("secret-", digest);
        Assert.DoesNotContain("not-a-link", digest);
        Assert.DoesNotContain("example.org", digest);
    }
}
