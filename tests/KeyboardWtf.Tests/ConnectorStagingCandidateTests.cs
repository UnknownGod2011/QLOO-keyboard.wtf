namespace KeyboardWtf.Tests;

using System.Reflection;
using System.Xml.Linq;
using KeyboardWtf.Services;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;
using Xunit;

[Collection("Legacy secret persistence")]
public sealed class ConnectorStagingCandidateTests
{
    private const string Symbol = "KEYBOARD_WTF_CONNECTOR_STAGING_CANDIDATE";
    private const string Profile = "keyboard.wtf-connectors-staging-candidate";
    private const string Backend = "https://gs-q261013---keyboard-wtf-connectors-staging-aac6362p6a-el.a.run.app/";
    private const string Web = "https://keyboard-wtf-connectors-staging.vercel.app/";

    [Fact]
    public void Connector_flavor_declares_only_its_symbol_and_rejects_combined_production_flavors()
    {
        var project = XDocument.Load(SourcePath("src", "KeyboardWtf.csproj"));
        var group = Assert.Single(project.Root!.Elements("PropertyGroup"), element =>
            (string)element.Attribute("Condition") == "'$(KeyboardWtfConnectorStagingCandidate)' == 'true'");
        Assert.Equal("$(DefineConstants);" + Symbol, group.Element("DefineConstants")?.Value);
        var target = Assert.Single(project.Root.Elements("Target"), element =>
            (string)element.Attribute("Name") == "RejectIncompatibleConnectorBuildFlavor");
        Assert.Equal("PrepareForBuild", (string)target.Attribute("BeforeTargets"));
        Assert.Equal("'$(KeyboardWtfConnectorStagingCandidate)' == 'true'", (string)target.Attribute("Condition"));
        Assert.Equal("'$(KeyboardWtfPaidProduction)' == 'true' Or '$(KeyboardWtfPaidProductionCandidate)' == 'true' Or '$(KeyboardWtfPersonalApiCandidate)' == 'true'",
            (string)Assert.Single(target.Elements("Error")).Attribute("Condition"));
    }

    [Fact]
    public void Connector_candidate_keeps_existing_legal_notices_beside_its_bundled_assets()
    {
        var project = XDocument.Load(SourcePath("src", "KeyboardWtf.csproj"));
        var group = Assert.Single(project.Root!.Elements("ItemGroup"), element =>
            (string)element.Attribute("Condition") == "'$(KeyboardWtfConnectorStagingCandidate)' == 'true'");
        var contents = group.Elements("Content").ToArray();
        Assert.Equal(2, contents.Length);
        foreach (var filename in new[] { "LICENSE", "NOTICES.md" })
        {
            Assert.True(File.Exists(SourcePath(filename)));
            var content = Assert.Single(contents, element => (string)element.Attribute("Include") == "..\\" + filename);
            Assert.Equal(filename, content.Element("Link")?.Value);
            Assert.Equal("PreserveNewest", content.Element("CopyToOutputDirectory")?.Value);
            Assert.Equal("PreserveNewest", content.Element("CopyToPublishDirectory")?.Value);
            Assert.Equal("true", content.Element("ExcludeFromSingleFile")?.Value);
        }
        // This candidate group must not change public diagnostic exclusions or
        // duplicate notices under a mixed flavor: the build guard rejects that.
        Assert.Empty(group.Elements("Compile"));
    }

    [Fact]
    public void Staging_backend_and_profile_are_compile_pinned_without_runtime_environment_setup()
    {
        var configuration = ConnectorBranch(Read("src", "Services", "PaidStaging", "PaidStagingConfiguration.cs"));
        Assert.Contains(Backend, configuration);
        Assert.Contains(Web, configuration);
        Assert.Contains("DeploymentEnvironment: \"staging\"", configuration);
        Assert.Contains("PreserveExistingRegistrations: true", configuration);
        Assert.Contains("EmergencyByokEnabled: false", configuration);
        Assert.DoesNotContain("GetEnvironmentVariable", configuration);
        var profile = ConnectorBranch(Read("src", "Services", "SettingsService.cs"));
        Assert.Contains("Environment.SpecialFolder.LocalApplicationData", profile);
        Assert.Contains(Profile, profile);
        Assert.DoesNotContain("GetEnvironmentVariable", profile);
    }

    [Fact]
    public void Package_has_distinct_identity_and_never_changes_public_startup_native_host_or_site_hash()
    {
        var build = Read("scripts", "build-windows-release.ps1");
        Assert.Contains("[switch]$ConnectorStagingCandidate", build);
        Assert.Contains("@($SideBySideStaging, $PaidProductionCandidate, $PaidProductionRelease, $PersonalApiCandidate, $ConnectorStagingCandidate).Where({ $_ }).Count -gt 1", build);
        Assert.Contains("$isSideBySide = $SideBySideStaging -or $PaidProductionCandidate -or $PersonalApiCandidate -or $ConnectorStagingCandidate", build);
        Assert.Contains("-p:KeyboardWtfConnectorStagingCandidate=true", build);
        Assert.Contains("if (-not $SkipSiteHashUpdate -and -not $isSideBySide -and -not $PaidProductionRelease)", build);
        var arguments = build.IndexOf("$isccArguments = @()", StringComparison.Ordinal);
        var start = build.IndexOf("if ($ConnectorStagingCandidate)", arguments, StringComparison.Ordinal);
        var end = build.IndexOf("& $iscc", start, StringComparison.Ordinal);
        Assert.True(arguments >= 0 && start > arguments && end > start);
        var block = build[start..end];
        foreach (var expected in new[]
        {
            "/DMyAppName=keyboard.wtf Connectors Staging Candidate",
            "/DMyAppId={{0F5D5DB4-9C8B-458F-8928-4F2275099417}",
            "/DMyDefaultDirName={localappdata}\\Programs\\keyboard.wtf Connectors Staging Candidate",
            "/DMyOutputBaseFilename=keyboard-wtf-connectors-staging-candidate-setup",
            "/DMySourceDir=..\\dist\\release\\keyboard-wtf-connectors-staging-candidate-win-x64",
            "/DMyRunValueName=keyboard.wtf Connectors Staging Candidate",
            "/DMyDefaultGroupName=keyboard.wtf Connectors Staging Candidate",
            "/DMyShortcutName=keyboard.wtf Connectors Staging Candidate",
            "/DMyUserDataDir=" + Profile,
            "/DMySkipStartup",
            "/DMyPreserveNativeHostRegistration",
        })
            Assert.Contains(expected, block);
        Assert.DoesNotContain("/DMyAppId={{A2290D45-0672-4FC5-8CFB-AEB92B1EF945}", block);
        Assert.Contains("\"keyboard-wtf-connectors-staging-candidate-setup.exe\"", build);
    }

    [Fact]
    public void Settings_mutation_guards_preserve_public_registration_owners_only_in_new_flavor()
    {
        var app = Read("src", "Services", "KeyboardWtfApp.cs");
        var guardStart = app.IndexOf("#if " + Symbol + "\n", StringComparison.Ordinal);
        Assert.True(guardStart >= 0);
        var branch = ConnectorBranch(app[guardStart..]);
        Assert.Contains("public void SyncStartupRegistration()", branch);
        Assert.Contains("does not manage Windows startup", branch);
        Assert.DoesNotContain("WindowsStartupService.Sync", branch);
        Assert.Contains("public void SyncStartupRegistration() =>\n        WindowsStartupService.Sync(_settings.Current.StartWithWindows, StartupExecutablePath);", app);
        var settings = Read("src", "Services", "WebSettingsService.cs");
        foreach (var method in new[] { "ConnectBrowserHelper", "ReconnectBrowserHelper" })
        {
            var start = settings.IndexOf("private async Task " + method + "(HttpListenerResponse resp)", StringComparison.Ordinal);
            Assert.True(start >= 0);
            var helper = ConnectorBranch(settings[start..]);
            Assert.Contains("ok = false", helper);
            Assert.Contains("native-host registration is unchanged", helper);
            Assert.DoesNotContain("_browserNativeHostInstaller.Register", helper);
            Assert.DoesNotContain("_browserNativeHostInstaller.InstallExtension", helper);
            var ordinaryBranchStart = settings.IndexOf("#else", start, StringComparison.Ordinal);
            var ordinaryBranchEnd = settings.IndexOf("#endif", ordinaryBranchStart, StringComparison.Ordinal);
            Assert.Contains("_browserNativeHostInstaller.Register(executable);", settings[ordinaryBranchStart..ordinaryBranchEnd]);
        }
    }

    [Fact]
    public void Existing_candidate_and_public_identity_branches_are_preserved()
    {
        var settings = Read("src", "Services", "SettingsService.cs");
        Assert.Contains("#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE", settings);
        Assert.Contains("#elif KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE", settings);
        Assert.Contains("\"keyboard.wtf-personal-api-candidate\"", settings);
        Assert.Contains("\"keyboard.wtf-paid-production-candidate\"", settings);
        var startup = Read("src", "Services", "WindowsStartupService.cs");
        Assert.DoesNotContain(Symbol, startup);
        var helper = Read("src", "Services", "BrowserAutomationV2", "BrowserAutomationModels.cs");
        Assert.DoesNotContain(Symbol, helper);
#if KEYBOARD_WTF_TEST_PUBLIC_PRODUCTION
        var configuration = PaidStagingConfiguration.FromEnvironment();
        Assert.True(configuration.IsProduction);
        Assert.False(configuration.PreserveExistingRegistrations);
        Assert.Equal("https://keyboard-wtf-paid-api-mhrc6c7goa-el.a.run.app/", configuration.BackendBaseUri.AbsoluteUri);
        Assert.Equal("https://keyboardwtf.com/", configuration.WebBaseUri.AbsoluteUri);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "keyboard.wtf"), SettingsService.AppDataDir);
#endif
    }

    [Fact]
    public void Only_connector_candidate_has_distinguishable_tray_and_settings_identity()
    {
        var program = Read("src", "Program.cs");
        var tray = ConnectorBranch(program);
        Assert.Contains("Text = \"keyboard.wtf Connector Staging\"", tray);
        Assert.DoesNotContain("Stop typing. Say it.", tray);
        Assert.Contains("Text = \"keyboard.wtf - Stop typing. Say it.\"", program);
        var settings = Read("src", "Services", "WebSettingsService.cs");
        var start = settings.IndexOf("private string LoadSettingsHtml()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Contains("DecorateConnectorStagingSettingsHtml(AppResources.ReadTextFile(\"settings.html\"))", ConnectorBranch(settings[start..]));
        Assert.Contains("connector-staging-banner", settings);
        Assert.Contains("Separate test account and profile. Public app unchanged.", settings);
#if !KEYBOARD_WTF_TEST_CONNECTOR_STAGING_CANDIDATE
        Assert.Null(typeof(WebSettingsService).GetMethod("DecorateConnectorStagingSettingsHtml", BindingFlags.Static | BindingFlags.NonPublic));
#endif
    }

#if KEYBOARD_WTF_TEST_CONNECTOR_STAGING_CANDIDATE
    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("0", null, null, "1")]
    [InlineData("1", "http://example.test/", "http://web.example.test/", "1")]
    [InlineData("1", "https://alternate-api.example.test/", "https://alternate-web.example.test/", "true")]
    [InlineData("1", "http://127.0.0.1:65500/", "http://localhost:65501/", "1")]
    [InlineData("1", "not a URL", "not a URL", "1")]
    public void Installed_connector_candidate_ignores_environment_redirects_and_emergency_byok(
        string enabled, string backend, string web, string emergency)
    {
        using var scope = new EnvironmentScope(
            ("KEYBOARD_WTF_PAID_STAGING", enabled),
            ("KEYBOARD_WTF_PAID_BACKEND_URL", backend),
            ("KEYBOARD_WTF_PAID_WEB_URL", web),
            ("KEYBOARD_WTF_BYOK_EMERGENCY", emergency));
        var configuration = PaidStagingConfiguration.FromEnvironment();
        Assert.True(configuration.Enabled);
        Assert.False(configuration.IsProduction);
        Assert.Equal("staging", configuration.DeploymentEnvironment);
        Assert.False(configuration.EmergencyByokEnabled);
        Assert.True(configuration.PreserveExistingRegistrations);
        Assert.Equal(Backend, configuration.BackendBaseUri.AbsoluteUri);
        Assert.Equal(Web, configuration.WebBaseUri.AbsoluteUri);
        Assert.False(PublicUpdateEligibility.IsAllowed());
    }

    [Fact]
    public void Installed_connector_candidate_keeps_credentials_and_instance_lock_in_its_own_profile()
    {
        using var scope = new EnvironmentScope(("KEYBOARD_WTF_APPDATA_DIR",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "keyboard.wtf")));
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Profile);
        var configured = typeof(SettingsService).GetMethod("ConfiguredAppDataOverride", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (string)configured.Invoke(null, null));
        Assert.Equal(expected, SettingsService.AppDataDir);
        Assert.Equal(Path.Combine(expected, "settings.json"), new SettingsService().SettingsPath);
        var session = new PaidSessionStore();
        var sessionPath = typeof(PaidSessionStore).GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(Path.Combine(expected, "paid-staging-session.dat"), (string)sessionPath.GetValue(session));
        Assert.Contains("Path.Combine(SettingsService.AppDataDir, \"keyboard.wtf.lock\")", Read("src", "Program.cs"));
        Assert.Contains("new PersonalApiVault(Path.Combine(SettingsService.AppDataDir, \"personal-api-v1.dat\"))", Read("src", "Services", "KeyboardWtfApp.cs"));
    }

    [Fact]
    public void Installed_connector_candidate_displays_test_identity_without_replacing_settings_content()
    {
        var html = Read("src", "Resources", "web", "settings.html");
        var decorated = WebSettingsService.DecorateConnectorStagingSettingsHtml(html);
        Assert.Contains("<title>keyboard.wtf Connector Staging</title>", decorated);
        Assert.Contains("id=\"connector-staging-banner\"", decorated);
        Assert.Contains("Separate test account and profile. Public app unchanged.", decorated);
        Assert.Contains("id=\"startWithWindows\" disabled", decorated);
        Assert.Contains("id=\"sidebar-nav\"", decorated);
        Assert.Contains("id=\"connectors\"", decorated);
        Assert.Contains("id=\"account-billing\"", decorated);
        Assert.Equal(decorated, WebSettingsService.DecorateConnectorStagingSettingsHtml(decorated));
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string> _previous = new(StringComparer.Ordinal);
        public EnvironmentScope(params (string Name, string Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }
        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
#endif

    private static string ConnectorBranch(string source)
    {
        var start = source.IndexOf("#if " + Symbol + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("#else", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string Read(params string[] parts) => File.ReadAllText(SourcePath(parts)).Replace("\r\n", "\n");
    private static string SourcePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeyboardWtf.sln")))
                return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the keyboard.wtf repository root.");
    }
}
