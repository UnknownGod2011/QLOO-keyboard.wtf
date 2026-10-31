namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class BrowserLauncherServiceTests
{
    [Theory]
    [InlineData("Google Chrome", "chrome")]
    [InlineData("Microsoft Edge", "edge")]
    [InlineData("Brave browser", "brave")]
    [InlineData("default browser", "")]
    public void BrowserNamesNormalize(string input, string expected)
    {
        Assert.Equal(expected, BrowserLauncherService.NormalizeBrowser(input));
    }

    [Fact]
    public void SearchUrlsAreServiceSpecific()
    {
        Assert.Contains("youtube.com", BrowserLauncherService.BuildSearchUrl("MrBeast", "youtube"));
        Assert.Contains("google.com", BrowserLauncherService.BuildSearchUrl("keyboard wtf", "google"));
    }

    [Fact]
    public void UnsafeSchemesAreRejected()
    {
        Assert.False(BrowserLauncherService.TryNormalizeUrl("file:///C:/Windows", out _));
    }

    [Theory]
    [InlineData("https://example.com/portal", "https://example.com/portal")]
    [InlineData("http://localhost:8080/test", "http://localhost:8080/test")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("keyboardwtf.com", "https://keyboardwtf.com/")]
    [InlineData("www.keyboardwtf.com", "https://www.keyboardwtf.com/")]
    [InlineData("https://keyboardwtf.com", "https://keyboardwtf.com/")]
    [InlineData("http://keyboardwtf.com", "http://keyboardwtf.com/")]
    [InlineData("keyboardwtf.com/path", "https://keyboardwtf.com/path")]
    [InlineData("sub.example.com/path?q=hello%20world#part-2", "https://sub.example.com/path?q=hello%20world#part-2")]
    [InlineData("example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("example.com/project?ref_content=portfolio&ref_feature=in_progress", "https://example.com/project?ref_content=portfolio&ref_feature=in_progress")]
    public void WebDestinationsUseOneSafeCanonicalForm(string input, string expected)
    {
        Assert.True(BrowserLauncherService.TryNormalizeUrl(input, out var uri));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("not a URL")]
    [InlineData("localhost")]
    [InlineData("user@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("C:\\Windows\\file.example")]
    [InlineData("https://use_selected/")]
    [InlineData("https://not_a_host.example/path")]
    [InlineData("open keyboardwtf.com please")]
    [InlineData("C:/Users/Admin/example.com")]
    [InlineData("//server/share")]
    [InlineData("me@keyboardwtf.com")]
    [InlineData("example..com")]
    [InlineData("-example.com")]
    public void AmbiguousOrUnsafeBareDestinationsAreRejected(string input)
    {
        Assert.False(BrowserLauncherService.TryNormalizeUrl(input, out _));
    }
}
