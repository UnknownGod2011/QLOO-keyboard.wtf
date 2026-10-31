using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorGithubCapabilityTests
{
    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    [Theory]
    [InlineData("github_repo", "", "/repos/owner/repo")]
    [InlineData("github_pulls", "", "/pulls?per_page=15&state=closed&page=1")]
    [InlineData("github_releases", "", "/releases/latest")]
    [InlineData("github_activity", "branches", "/branches?per_page=15&page=1")]
    [InlineData("github_activity", "commits", "/commits?per_page=15&page=1")]
    [InlineData("github_activity", "contributors", "/contributors?per_page=15&page=1")]
    [InlineData("github_activity", "languages", "/languages")]
    [InlineData("github_activity", "events", "/events?per_page=15&page=1")]
    [InlineData("github_activity", "workflows", "/actions/workflows?per_page=15&page=1")]
    [InlineData("github_activity", "runs", "/actions/runs?per_page=15&page=1")]
    public void New_capabilities_are_allowlisted_native_gets(string capability, string kind, string suffix)
    {
        var plan = ConnectorOperations.Plan(capability, Args(new { repository = "owner/repo", state = "closed", kind }), "");
        Assert.EndsWith(suffix, plan.Endpoint); Assert.StartsWith("https://api.github.com/repos/owner/repo", plan.Endpoint);
        Assert.Equal("GET", plan.Method); Assert.Null(plan.Body); Assert.Null(plan.ReconcileEndpoint);
    }
    [Theory]
    [InlineData("../delete")][InlineData("https://evil.test")][InlineData("runs/12/rerun")]
    public void Activity_cannot_invoke_arbitrary_endpoints_or_actions(string kind)
        => Assert.Throws<ArgumentException>(() => ConnectorOperations.Plan("github_activity", Args(new { repository = "owner/repo", kind }), ""));
    [Fact]
    public void Name_matching_ignores_punctuation_but_preserves_owner_ambiguity_and_paging()
    {
        var args = Args(new { query = "MX Cursivis" });
        Assert.Contains("per_page=100", ConnectorOperations.Plan("github_repos", args, "").Endpoint);
        var repos = new[] { new { name = "MX--Cursivis", full_name = "a/MX--Cursivis", html_url = "https://github.com/a/MX--Cursivis" },
            new { name = "mx-cursivis", full_name = "b/mx-cursivis", html_url = "https://github.com/b/mx-cursivis" },
            new { name = "other", full_name = "a/other", html_url = "https://github.com/a/other" } };
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(repos), "github_repos", args);
        Assert.Equal(2, result.GetProperty("data").GetArrayLength());
        Assert.Equal(2, result.GetProperty("coverage").GetProperty("pageItemCount").GetInt32());
        Assert.True(result.GetProperty("coverage").GetProperty("complete").GetBoolean());
        var full = Enumerable.Range(0, 100).Select(i => new { name = "other" + i, full_name = "a/other" + i });
        var emptyPage = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(full), "github_repos", args);
        Assert.Empty(emptyPage.GetProperty("data").EnumerateArray());
        Assert.False(emptyPage.GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.Equal(2, emptyPage.GetProperty("coverage").GetProperty("next").GetProperty("page").GetInt32());
    }
    [Fact]
    public void Exact_name_outranks_punctuation_recovery_without_discarding_equal_owners()
    {
        var repos = JsonSerializer.SerializeToElement(new[] {
            new { name = "project--desktop", full_name = "owner/project--desktop" },
            new { name = "project-desktop", full_name = "owner/project-desktop" },
            new { name = "PROJECT--DESKTOP", full_name = "other/PROJECT--DESKTOP" } });
        var exact = ConnectorGithubReads.Match(repos, "project--desktop");
        Assert.Equal(2, exact.GetArrayLength());
        Assert.DoesNotContain(exact.EnumerateArray(), item => item.GetProperty("full_name").GetString() == "owner/project-desktop");
        Assert.Single(ConnectorGithubReads.Match(repos, "owner/project--desktop").EnumerateArray());
        Assert.Equal(3, ConnectorGithubReads.Match(repos, "project desktop").GetArrayLength());
    }
    [Fact]
    public void Asset_totals_are_verified_nonnegative_metrics_not_unique_users()
    {
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { id = 3, tag_name = "v1", assets = new[] {
            new { id = 1, name = "a.zip", download_count = 17 }, new { id = 2, name = "b.exe", download_count = 23 } } }), "github_releases");
        var data = result.GetProperty("data");
        Assert.Equal(40, data.GetProperty("totalAssetDownloads").GetInt32());
        Assert.Equal(2, data.GetProperty("assets").GetArrayLength());
        Assert.Contains("not unique", data.GetProperty("downloadMetric").GetString());
        var incomplete = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { assets = new[] { new { id = 1, download_count = -1 } } }), "github_releases");
        Assert.False(incomplete.GetProperty("data").GetProperty("assetCountsComplete").GetBoolean());
        Assert.False(incomplete.GetProperty("data").TryGetProperty("totalAssetDownloads", out _));
    }
    [Fact]
    public void Release_asset_pagination_cannot_claim_a_full_page_is_complete()
    {
        var args = Args(new { repository = "owner/repo", releaseId = 12, page = 1 });
        Assert.EndsWith("/releases/12/assets?per_page=100&page=1", ConnectorOperations.Plan("github_release_assets", args, "").Endpoint);
        var result = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(Enumerable.Range(1, 100).Select(id => new { id, name = "asset", download_count = id })), "github_release_assets", args);
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, result.GetProperty("coverage").GetProperty("next").GetProperty("page").GetInt32());
    }
}
