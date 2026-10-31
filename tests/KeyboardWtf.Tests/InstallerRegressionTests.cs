namespace KeyboardWtf.Tests;

using Xunit;

public sealed class InstallerRegressionTests
{
    [Fact]
    public void Runtime_update_markers_are_excluded_without_changing_normal_payload_rules()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "keyboard-wtf.iss"));
        var section = System.Text.RegularExpressions.Regex.Match(source,
            @"(?ms)^\[Files\]\r?\n(?<body>.*?)(?=^\[|\z)");
        Assert.True(section.Success);
        var entry = Assert.Single(section.Groups["body"].Value.Split('\n')
            .Select(line => line.Trim()), line => line.StartsWith("Source:", StringComparison.Ordinal));
        var excludes = System.Text.RegularExpressions.Regex.Match(entry, "Excludes: \"(?<patterns>[^\"]+)\"");
        Assert.True(excludes.Success);
        Assert.Equal(new[] { "*.pdb", ".keyboard.wtf-update-reservation", ".keyboard.wtf-update-activity" },
            excludes.Groups["patterns"].Value.Split(',').Select(value => value.Trim()).ToArray());
        Assert.Equal("Source: \"{#MySourceDir}\\*\"; DestDir: \"{app}\"; "
            + "Excludes: \"*.pdb\"; Flags: ignoreversion recursesubdirs createallsubdirs",
            entry.Replace(excludes.Value, "Excludes: \"*.pdb\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Personal_candidate_has_separate_install_profile_startup_and_pipe_identity()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, "scripts", "build-windows-release.ps1"));
        var arguments = build.IndexOf("$isccArguments = @()", StringComparison.Ordinal);
        var start = build.IndexOf("if ($PersonalApiCandidate)", arguments, StringComparison.Ordinal);
        var end = build.IndexOf("if ($SideBySideStaging)", start, StringComparison.Ordinal);
        var block = build[start..end];
        Assert.Contains("84684681-8F17-4B43-9220-919485814D2F", block);
        Assert.DoesNotContain("C7905F92-81A1-4824-9E31-26A5E532B50F", block);
        Assert.Contains("Programs\\keyboard.wtf Personal API Candidate", block);
        Assert.Contains("MyUserDataDir=keyboard.wtf-personal-api-candidate", block);
        Assert.Contains("MySkipStartup", block);
        Assert.Contains("MyPreserveNativeHostRegistration", block);
        foreach (var (file, expected) in new[]
        {
            ("SettingsService.cs", "keyboard.wtf-personal-api-candidate"),
            ("WindowsStartupService.cs", "keyboard.wtf Personal API Candidate"),
            (Path.Combine("BrowserAutomationV2", "BrowserAutomationModels.cs"), "keyboard.wtf.browser.v2.personal-api-candidate"),
        })
        {
            var source = File.ReadAllText(Path.Combine(root, "src", "Services", file));
            var personal = source.IndexOf("#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE", StringComparison.Ordinal);
            var baseline = source.IndexOf("#elif KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE", personal, StringComparison.Ordinal);
            Assert.Contains(expected, source[personal..baseline]);
        }
    }

    [Fact]
    public void Upgrade_pauses_and_restores_the_existing_native_host_registration()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));

        Assert.Contains("function PrepareToInstall", installer, StringComparison.Ordinal);
        Assert.Contains("RegDeleteValue(HKCU, NativeHostRegistryPath, '')", installer, StringComparison.Ordinal);
        Assert.Contains("procedure CurStepChanged", installer, StringComparison.Ordinal);
        Assert.Contains("procedure DeinitializeSetup", installer, StringComparison.Ordinal);
        Assert.Contains("RestoreNativeHostRegistration", installer, StringComparison.Ordinal);
        Assert.Contains("RegWriteStringValue", installer, StringComparison.Ordinal);
        Assert.Contains("StopOwnedProcessesForInstallOrUninstall();", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_removes_only_the_native_host_owned_by_that_installation()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));

        Assert.Contains("procedure UnregisterOwnedNativeHost", installer, StringComparison.Ordinal);
        Assert.Contains("CompareText(RegisteredManifest, OwnedManifest) <> 0", installer, StringComparison.Ordinal);
        Assert.Contains("RegDeleteKeyIncludingSubkeys(HKCU, NativeHostRegistryPath)", installer, StringComparison.Ordinal);
        Assert.Contains("if CurUninstallStep = usUninstall then", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_stops_only_processes_running_from_that_installation()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));

        Assert.Contains("procedure StopOwnedProcessesForInstallOrUninstall", installer, StringComparison.Ordinal);
        Assert.Contains("TargetExecutable := ExpandConstant('{app}\\{#MyAppExeName}')", installer, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::GetFullPath($_.Path) -eq $target", installer, StringComparison.Ordinal);
        Assert.Contains("StopOwnedProcessesForInstallOrUninstall();", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paid_production_candidate_does_not_register_for_startup()
    {
        var root = FindRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "scripts", "build-windows-release.ps1"));
        var compilerArgumentsStart = buildScript.IndexOf("$isccArguments = @()", StringComparison.Ordinal);
        var candidateBlockStart = buildScript.IndexOf(
            "if ($PaidProductionCandidate)",
            compilerArgumentsStart,
            StringComparison.Ordinal);
        var compilerStart = buildScript.IndexOf("& $iscc", candidateBlockStart, StringComparison.Ordinal);

        Assert.True(candidateBlockStart >= 0 && compilerStart > candidateBlockStart);
        Assert.Contains("'/DMySkipStartup'", buildScript[candidateBlockStart..compilerStart], StringComparison.Ordinal);
        Assert.Contains("'/DMyPreserveNativeHostRegistration'", buildScript[candidateBlockStart..compilerStart], StringComparison.Ordinal);
    }

    [Fact]
    public void Side_by_side_installers_preserve_the_shared_native_host_registration()
    {
        var root = FindRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "scripts", "build-windows-release.ps1"));
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));
        var compilerArgumentsStart = buildScript.IndexOf("$isccArguments = @()", StringComparison.Ordinal);
        var stagingBlockStart = buildScript.IndexOf("if ($SideBySideStaging)", compilerArgumentsStart, StringComparison.Ordinal);
        var candidateBlockStart = buildScript.IndexOf("if ($PaidProductionCandidate)", stagingBlockStart, StringComparison.Ordinal);
        var compilerStart = buildScript.IndexOf("& $iscc", candidateBlockStart, StringComparison.Ordinal);
        var guardStart = installer.IndexOf("#ifndef MyPreserveNativeHostRegistration", StringComparison.Ordinal);
        var pauseStart = installer.IndexOf("NativeHostRegistrationPaused := RegQueryStringValue(", StringComparison.Ordinal);
        var guardEnd = installer.IndexOf("#endif", pauseStart, StringComparison.Ordinal);
        var cleanupStart = installer.IndexOf("StopOwnedProcessesForInstallOrUninstall();", pauseStart, StringComparison.Ordinal);

        Assert.True(stagingBlockStart >= 0 && candidateBlockStart > stagingBlockStart && compilerStart > candidateBlockStart);
        Assert.Contains("'/DMyPreserveNativeHostRegistration'", buildScript[stagingBlockStart..candidateBlockStart], StringComparison.Ordinal);
        Assert.Contains("'/DMyPreserveNativeHostRegistration'", buildScript[candidateBlockStart..compilerStart], StringComparison.Ordinal);
        Assert.True(guardStart >= 0 && pauseStart > guardStart && guardEnd > pauseStart && cleanupStart > guardEnd);
        Assert.True(installer.IndexOf("#else", pauseStart, StringComparison.Ordinal) > pauseStart);
    }

    [Fact]
    public void Uninstall_removes_known_empty_runtime_directories_without_recursive_app_deletion()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));

        Assert.Contains("[UninstallDelete]", installer, StringComparison.Ordinal);
        Assert.Contains("Type: dirifempty; Name: \"{app}\\runtimes\\win-x64\"", installer, StringComparison.Ordinal);
        Assert.Contains("Type: dirifempty; Name: \"{app}\\runtimes\"", installer, StringComparison.Ordinal);
        Assert.Contains("Type: dirifempty; Name: \"{app}\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Type: filesandordirs; Name: \"{app}\"", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Updater_mode_requires_no_force_no_reboot_arguments_before_installation_effects()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "keyboard-wtf.iss"));
        var start = source.IndexOf("function InitializeSetup(): Boolean;", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("function RestoreNativeHostRegistration", start, StringComparison.Ordinal);
        var initialize = source[start..end];
        Assert.Contains("if not IsUpdaterMode() then", initialize);
        foreach (var flag in new[] { "/SILENT", "/NOCLOSEAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS",
                     "/NORESTARTAPPLICATIONS", "/NORESTART", "/RESTARTEXITCODE=3010" })
            Assert.Contains("HasCommandLineParameter('" + flag + "')", initialize);
        foreach (var forbidden in new[] { "/CLOSEAPPLICATIONS", "/FORCECLOSEAPPLICATIONS", "/RESTARTAPPLICATIONS" })
            Assert.Contains("not HasCommandLineParameter('" + forbidden + "')", initialize);
        Assert.DoesNotContain("RegDelete", initialize);
        Assert.DoesNotContain("Exec(", initialize);
    }

    [Fact]
    public void Updater_skips_custom_force_stop_but_ordinary_install_and_uninstall_keep_existing_cleanup()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "keyboard-wtf.iss"));
        var start = source.IndexOf("function PrepareToInstall", StringComparison.Ordinal);
        var end = source.IndexOf("procedure CurStepChanged", start, StringComparison.Ordinal);
        Assert.Contains("if not IsUpdaterMode() then\n    StopOwnedProcessesForInstallOrUninstall();",
            source[start..end].Replace("\r\n", "\n"));
        var uninstall = source[source.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal)..];
        Assert.Contains("StopOwnedProcessesForInstallOrUninstall();", uninstall);
        Assert.DoesNotContain("IsUpdaterMode", uninstall);
        Assert.Contains("CloseApplications=force", source); // Preserve ordinary installer defaults.
    }

    [Fact]
    public void Updater_has_no_Inno_relaunch_owner_even_if_run_entry_would_otherwise_execute()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "installer", "keyboard-wtf.iss"));
        var start = source.IndexOf("[Run]", StringComparison.Ordinal);
        var end = source.IndexOf("[UninstallDelete]", start, StringComparison.Ordinal);
        Assert.Contains("Check: ShouldLaunchAfterInstall", source[start..end]);
        Assert.Contains("function ShouldLaunchAfterInstall(): Boolean;", source);
        Assert.Contains("Result := not IsUpdaterMode();", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeyboardWtf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the keyboard.wtf repository root.");
    }
}
