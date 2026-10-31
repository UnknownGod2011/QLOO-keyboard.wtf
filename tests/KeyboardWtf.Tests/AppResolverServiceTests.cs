namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class AppResolverServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-app-tests", Guid.NewGuid().ToString("N"));
    private readonly LearnedMappingService _mappings;

    public AppResolverServiceTests()
    {
        Directory.CreateDirectory(_root);
        _mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        _mappings.Load();
    }

    [Fact]
    public void LearnedAppMappingWinsDeterministically()
    {
        _mappings.Remember(LearnedMappingKind.App, "my assistant", "appx:OpenAI.Codex_2p2nqsd0c76g0!App", "Codex");
        var resolver = new AppResolverService(_mappings);

        var result = resolver.Resolve("my assistant");

        Assert.Equal(AppResolutionStatus.Found, result.Status);
        Assert.Equal("Codex", result.Best.Name);
        Assert.Equal("Learned", result.Best.Source);
    }

    [Fact]
    public void CoreWindowsAppCanBeResolvedFromTrustedSources()
    {
        var resolver = new AppResolverService(_mappings);
        var result = resolver.Resolve("notepad");

        Assert.Equal(AppResolutionStatus.Found, result.Status);
        Assert.True(result.Best.Trusted);
    }

    [Fact]
    public void CommandScriptsAreNeverTrustedLaunchTargets()
    {
        Assert.False(AppResolverService.IsTrustedLaunchPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "example.cmd")));
    }

    [Theory]
    [InlineData("spotfy", "Spotify")]
    [InlineData("aple music", "Apple Music")]
    [InlineData("visual code", "Visual Studio Code")]
    [InlineData("xbox game bar", "Game Bar")]
    [InlineData("windows camera", "Camera")]
    public void NaturalNameTyposCanonicalize(string query, string expected)
    {
        Assert.Equal(expected, AppResolverService.CanonicalizeNaturalName(query));
    }

    [Fact]
    public void ShortGenericNameDoesNotBecomeCodex()
    {
        Assert.Equal("code", AppResolverService.CanonicalizeNaturalName("code"));
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
