using KeyboardWtf.Services;
using Xunit;
using Window = KeyboardWtf.Services.NativeWindowCloser.Window;

namespace KeyboardWtf.Tests;

public sealed class NativeInstalledAppCloseTests
{
    [Theory]
    [InlineData("Paint", "Paint", "mspaint", "Microsoft.Paint_package!App")]
    [InlineData("Teams", "Microsoft Teams", "ms-teams", "MSTeams_package!App")]
    [InlineData("Microsoft Teams", "Microsoft Teams", "ms-teams", "MSTeams_package!App")]
    [InlineData("Sketchpad", "Contoso Sketchpad", "opaque-host", "Contoso.Unknown_package!DifferentApp")]
    public void Registered_identity_resolves_display_names_without_process_aliases(
        string request, string label, string process, string identity)
    {
        var window = Make(1, process, "Document - " + label);
        Assert.Empty(NativeWindowCloser.Select([window], request, false, null, window.Handle));
        Assert.Equal(window, Assert.Single(NativeWindowCloser.SelectInstalledApp(
            [window], request, [App(label, identity)], null, _ => identity)));
    }

    [Fact]
    public void Exact_identity_and_window_id_exclude_unrelated_windows_and_browsers()
    {
        Window[] windows = [Make(1, "opaque-host", "Editor"), Make(2, "chrome", "Editor"),
            Make(3, "other", "Editor"), Make(4, "opaque-host", "Other document")];
        var app = App("Editor", "Vendor.Editor!App");
        string Identity(uint process) => process is 1 or 2 or 4 ? "Vendor.Editor!App" : "Other.Editor!App";
        Assert.Equal(new[] { windows[0], windows[3] },
            NativeWindowCloser.SelectInstalledApp(windows, "Editor", [app], null, Identity));
        Assert.Equal(windows[3], Assert.Single(
            NativeWindowCloser.SelectInstalledApp(windows, "Editor", [app], "4", Identity)));
        Assert.Empty(NativeWindowCloser.SelectInstalledApp(windows, "Editor", [app], "2", Identity));
    }

    [Theory]
    [InlineData("Editr", "Editor", "StartApps", true)]
    [InlineData("spotfy", "Spotify", "StartApps", true)]
    [InlineData("Editor", "Editor Pro", "StartApps", true)]
    [InlineData("Editor", "Editor", "Learned", true)]
    [InlineData("Editor", "Editor", "StartApps", false)]
    [InlineData("", "Editor", "StartApps", true)]
    [InlineData("C:\\Editor", "C Editor", "StartApps", true)]
    public void Unverified_or_different_catalog_names_do_not_authorize_a_close(
        string request, string label, string source, bool trusted)
    {
        var reads = 0;
        Assert.Empty(NativeWindowCloser.SelectInstalledApp([Make(1, "host", label)], request,
            [new AppCandidate { Name = label, Source = source, Trusted = trusted, LaunchTarget = "appx:Vendor.Editor!App" }],
            null, _ => { reads++; return "Vendor.Editor!App"; }));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Vendor.Other!App")]
    public void Missing_or_changed_process_identity_never_matches(string actual) =>
        Assert.Empty(NativeWindowCloser.SelectInstalledApp([Make(1, "host", "Editor")],
            "Editor", [App("Editor", "Vendor.Editor!App")], null, _ => actual));

    [Fact]
    public async Task Catalog_selection_preserves_existing_snapshot_identity_validation()
    {
        var selected = NativeWindowCloser.SelectInstalledApp([Make(1, "host", "Editor")],
            "Editor", [App("Editor", "Vendor.Editor!App")], null, _ => "Vendor.Editor!App");
        var posted = 0;
        var outcome = await NativeWindowCloser.CloseAsync(selected, CancellationToken.None,
            _ => Make(1, "host", "Replacement") with { ProcessId = 999 },
            _ => { posted++; return true; }, TimeSpan.Zero);
        Assert.Equal(0, posted);
        Assert.Empty(outcome.Remaining);
    }

    [Fact]
    public void Ambiguous_catalog_names_do_not_pick_a_ranked_winner()
    {
        var reads = 0;
        Assert.Empty(NativeWindowCloser.SelectInstalledApp([Make(1, "host", "Editor")],
            "Editor", [App("Contoso Editor", "Contoso.Editor!App"), App("Fabrikam Editor", "Fabrikam.Editor!App")],
            null, _ => { reads++; return "Contoso.Editor!App"; }));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Exact_catalog_label_outranks_suffix_but_duplicate_identities_are_not_ambiguous()
    {
        var window = Make(1, "host", "Editor");
        Assert.Equal(window, Assert.Single(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [App("Editor", "Vendor.Editor!App"), App("Editor", "Vendor.Editor!App"),
                App("Contoso Editor", "Other.Editor!App")], null, _ => "Vendor.Editor!App")));
        Assert.Empty(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [App("Editor", "Vendor.Editor!App"), App("Editor", "Other.Editor!App")],
            null, _ => "Vendor.Editor!App"));
    }

    [Theory]
    [InlineData("Paint", "Paint", "mspaint")]
    [InlineData("Teams", "Microsoft Teams", "ms-teams")]
    [InlineData("Sketchpad", "Contoso Sketchpad", "opaque-host")]
    public void Registered_executable_resolves_an_AppPaths_only_catalog(
        string request, string label, string process)
    {
        var image = SystemImage("notepad.exe");
        var window = Make(1, process, "Untitled - " + label);
        Assert.Equal(window, Assert.Single(NativeWindowCloser.SelectInstalledApp([window], request,
            [PathApp(label, image)], null, _ => "", _ => image)));
    }

    [Fact]
    public void Registered_image_requires_exact_path_not_just_a_filename_or_title()
    {
        var image = SystemImage("notepad.exe");
        var window = Make(1, "opaque-host", "Editor");
        foreach (var actual in new[] { "", "notepad.exe", Path.Combine(Path.GetTempPath(), "notepad.exe") })
            Assert.Empty(NativeWindowCloser.SelectInstalledApp([window], "Editor",
                [PathApp("Editor", image)], null, _ => "", _ => actual));
        Assert.Equal(window, Assert.Single(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [PathApp("Editor", image)], null, _ => "", _ => image.ToUpperInvariant())));
    }

    [Fact]
    public void Registered_paths_preserve_exact_id_browser_and_explorer_exclusions()
    {
        var image = SystemImage("notepad.exe");
        Window[] windows = [Make(1, "opaque-host", "Editor"), Make(2, "chrome", "Editor"),
            Make(3, "explorer", "Editor") with { ClassName = "CabinetWClass" }, Make(4, "opaque-host", "Editor")];
        Assert.Equal(windows[3], Assert.Single(NativeWindowCloser.SelectInstalledApp(windows, "Editor",
            [PathApp("Editor", image)], "4", _ => "", _ => image)));
        Assert.Empty(NativeWindowCloser.SelectInstalledApp(windows, "Editor",
            [PathApp("Editor", image)], "2", _ => "", _ => image));
        Assert.Empty(NativeWindowCloser.SelectInstalledApp(windows, "Editor",
            [PathApp("Editor", image)], "3", _ => "", _ => image));
        Assert.Empty(NativeWindowCloser.SelectInstalledApp(windows, "Editor",
            [PathApp("Editor", image)], "999", _ => "", _ => image));
    }

    [Fact]
    public void Invalid_untrusted_and_missing_registered_paths_never_read_a_process()
    {
        var valid = PathApp("Editor", SystemImage("notepad.exe"));
        AppCandidate[] invalid = [
            PathApp("Editor", SystemImage("notepad.exe"), trusted: false),
            PathApp("Editor", SystemImage("notepad.exe"), source: "Learned"),
            PathApp("Editor", Path.Combine(Path.GetTempPath(), "untrusted.exe")),
            PathApp("Editor", Path.Combine(Environment.SystemDirectory, "missing-native-close-test.exe")),
            PathApp("Editor", "notepad.exe"), PathApp("Editor", Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts"))];
        var reads = 0;
        foreach (var app in invalid)
            Assert.Empty(NativeWindowCloser.SelectInstalledApp([Make(1, "host", "Editor")], "Editor",
                [app], null, _ => "", _ => { reads++; return valid.LaunchTarget[5..]; }));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Exact_label_priority_is_shared_by_both_catalog_sources()
    {
        var image = SystemImage("notepad.exe");
        var window = Make(1, "host", "Editor");
        Assert.Equal(window, Assert.Single(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [App("Contoso Editor", "Other!App"), PathApp("Editor", image)], null, _ => "", _ => image)));
        Assert.Equal(window, Assert.Single(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [App("Editor", "Vendor!App"), PathApp("Contoso Editor", image)], null, _ => "Vendor!App",
            _ => throw new InvalidOperationException("Existing app-ID selection must not read image paths"))));
    }

    [Fact]
    public void Selected_StartApps_authority_never_falls_back_after_ambiguity_or_identity_failure()
    {
        var image = SystemImage("notepad.exe");
        var window = Make(1, "host", "Editor");
        var registered = PathApp("Editor", image);
        var reads = 0;
        Assert.Empty(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [App("Editor", "Vendor!App"), registered], null, _ => "", _ => { reads++; return image; }));
        Assert.Empty(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [App("Editor", "Vendor!App"), App("Editor", "Other!App"), registered],
            null, _ => "Vendor!App", _ => { reads++; return image; }));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Duplicate_registered_paths_are_equivalent_but_distinct_paths_remain_ambiguous()
    {
        var image = SystemImage("notepad.exe");
        var app = PathApp("Editor", image);
        var window = Make(1, "host", "Editor");
        Assert.Equal(window, Assert.Single(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [app, PathApp("Editor", image.ToUpperInvariant())], null, _ => "", _ => image)));
        Assert.Empty(NativeWindowCloser.SelectInstalledApp([window], "Editor",
            [app, PathApp("Editor", SystemImage("cmd.exe"))], null, _ => "", _ => image));
    }

    private static AppCandidate PathApp(string name, string image, string source = "AppPaths", bool trusted = true) => new()
    { Name = name, LaunchTarget = "path:" + image, Source = source, Trusted = trusted };

    private static string SystemImage(string filename)
    {
        var path = Path.Combine(Environment.SystemDirectory, filename);
        Assert.True(File.Exists(path), "Required Windows fixture image must exist: " + path);
        return path;
    }

    private static AppCandidate App(string name, string identity) => new()
    { Name = name, LaunchTarget = "appx:" + identity, Source = "StartApps", Trusted = true };

    private static Window Make(int id, string process, string title) =>
        new((IntPtr)id, (uint)id, (uint)id, "AppWindow", process, title);
}
