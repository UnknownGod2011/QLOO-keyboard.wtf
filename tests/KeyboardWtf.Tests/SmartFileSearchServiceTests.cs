namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class SmartFileSearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-file-tests", Guid.NewGuid().ToString("N"));
    private readonly LearnedMappingService _mappings;

    public SmartFileSearchServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested", "project screenshots"));
        File.WriteAllText(Path.Combine(_root, "nested", "project screenshots", "keyboard-thumbnail-final.png"), "test");
        File.WriteAllText(Path.Combine(_root, "dangerous-installer.exe"), "test");
        _mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        _mappings.Load();
    }

    [Fact]
    public async Task FindsNestedApproximateImage()
    {
        var service = new SmartFileSearchService(
            _mappings,
            [(_root, "Test", 10)]);
        var result = await service.SearchAsync(
            "keyboard thumbnail image",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.EndsWith("keyboard-thumbnail-final.png", result.Best.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Natural_desktop_file_request_uses_the_name_instead_of_location_words()
    {
        var target = Path.Combine(_root, "Tanush_oppo.txt");
        File.WriteAllText(target, "test");
        var service = new SmartFileSearchService(_mappings, [(_root, "Desktop", 10)]);

        var result = await service.SearchAsync(
            "Find the file on my Desktop called tanush-oppo",
            "",
            includeFiles: true,
            includeFolders: true,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
        Assert.Equal("tanush oppo", SmartFileSearchService.SearchNeedle(
            "Find the file on my Desktop called tanush-oppo"));
    }

    [Fact]
    public async Task Natural_desktop_folder_request_handles_spaces_underscores_and_case()
    {
        var target = Path.Combine(_root, "Keyboard_WTF Project");
        Directory.CreateDirectory(target);
        var service = new SmartFileSearchService(_mappings, [(_root, "Desktop", 10)]);

        var result = await service.SearchAsync(
            "Find the keyboard-wtf project folder on my desktop",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
    }

    [Fact]
    public async Task Spoken_split_compound_matches_one_filesystem_token_without_guessing_a_different_folder()
    {
        var target = Path.Combine(_root, "sample-dataops");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(_root, "sample-copy"));
        var service = new SmartFileSearchService(_mappings, [(_root, "Desktop", 10)]);

        var result = await service.SearchAsync("data ops", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(target, result.Best.Path);
    }

    [Fact]
    public async Task Spoken_split_compound_with_two_matching_folders_requires_clarification()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sample-dataops"));
        Directory.CreateDirectory(Path.Combine(_root, "archive-dataops"));
        var service = new SmartFileSearchService(_mappings, [(_root, "Desktop", 10)]);

        var result = await service.SearchAsync("data ops", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count(candidate => candidate.Name.EndsWith("dataops", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Overlapping_roots_do_not_make_one_compact_name_match_ambiguous()
    {
        var working = Path.Combine(_root, "working");
        var target = Path.Combine(working, "nested", "ProjectRun");
        Directory.CreateDirectory(target);
        var service = new SmartFileSearchService(
            _mappings,
            [(working, "Primary", 10), (_root, "Parent", 7)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(target, candidate.Path);
        Assert.Equal("Primary", candidate.RootLabel);
    }

    [Fact]
    public async Task Equivalent_root_path_spellings_retain_the_strongest_candidate_metadata()
    {
        var target = Path.Combine(_root, "nested", "ProjectRun");
        Directory.CreateDirectory(target);
        var equivalentRoot = Path.Combine(_root, "nested", "..");
        var service = new SmartFileSearchService(
            _mappings,
            [(equivalentRoot, "Secondary", 7), (_root, "Primary", 10)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(target, candidate.Path);
        Assert.Equal("Primary", candidate.RootLabel);
        Assert.Equal(1, candidate.Depth);
    }

    [Fact]
    public async Task Strong_compact_name_match_does_not_pad_choices_with_weak_fuzzy_names()
    {
        var target = Path.Combine(_root, "nested", "ProjectRun");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(_root, "nested", "Project room"));
        var service = new SmartFileSearchService(_mappings, [(_root, "Primary", 10)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(target, Assert.Single(result.Candidates).Path);
    }

    [Fact]
    public async Task Distinct_strong_compact_matches_stay_ambiguous_without_weak_padding()
    {
        var first = Path.Combine(_root, "first", "ProjectRun");
        var second = Path.Combine(_root, "second", "ProjectRun");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(Path.Combine(_root, "second", "Project room"));
        var service = new SmartFileSearchService(_mappings, [(_root, "Primary", 10)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { first, second }.Order(), result.Candidates.Select(candidate => candidate.Path).Order());
    }

    [Fact]
    public async Task Distinct_equally_primary_direct_exact_matches_require_clarification()
    {
        var firstRoot = Path.Combine(_root, "first");
        var secondRoot = Path.Combine(_root, "second");
        var first = Path.Combine(firstRoot, "project run");
        var second = Path.Combine(secondRoot, "project run");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var service = new SmartFileSearchService(
            _mappings,
            [(firstRoot, "First", 10), (secondRoot, "Second", 10)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { first, second }.Order(), result.Candidates.Select(candidate => candidate.Path).Order());
    }

    [Fact]
    public async Task Literal_direct_folder_does_not_hide_a_distinct_normalized_exact_peer()
    {
        var firstRoot = Path.Combine(_root, "first-literal");
        var secondRoot = Path.Combine(_root, "second-normalized");
        var literal = Path.Combine(firstRoot, "Project Copy");
        var normalizedPeer = Path.Combine(secondRoot, "Project--Copy");
        Directory.CreateDirectory(literal);
        Directory.CreateDirectory(normalizedPeer);
        var service = new SmartFileSearchService(
            _mappings,
            [(firstRoot, "First", 10), (secondRoot, "Second", 10)]);

        var result = await service.SearchAsync("project copy folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(
            new[] { literal, normalizedPeer }.Order(),
            result.Candidates.Select(candidate => candidate.Path).Order());
    }

    [Fact]
    public async Task Literal_direct_folder_does_not_hide_a_normalized_exact_peer_in_the_same_root()
    {
        var root = Path.Combine(_root, "literal-and-normalized");
        var literal = Path.Combine(root, "Project Copy");
        var normalizedPeer = Path.Combine(root, "Project! - Copy");
        Directory.CreateDirectory(literal);
        Directory.CreateDirectory(normalizedPeer);
        var service = new SmartFileSearchService(_mappings, [(root, "Primary", 10)]);

        var result = await service.SearchAsync("project copy folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(
            new[] { literal, normalizedPeer }.Order(),
            result.Candidates.Select(candidate => candidate.Path).Order());
    }

    [Fact]
    public async Task Duplicate_paths_do_not_consume_the_candidate_limit_before_a_distinct_peer()
    {
        var firstRoot = Path.Combine(_root, "first");
        var secondRoot = Path.Combine(_root, "second");
        var first = Path.Combine(firstRoot, "nested", "ProjectRun");
        var second = Path.Combine(secondRoot, "nested", "ProjectRun");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var service = new SmartFileSearchService(
            _mappings,
            [(firstRoot, "First", 10), (firstRoot, "Repeated", 10), (secondRoot, "Second", 10)]);

        var result = await service.SearchAsync(
            "project run folder", "", false, true, CancellationToken.None, maxResults: 2);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { first, second }.Order(), result.Candidates.Select(candidate => candidate.Path).Order());
    }

    [Fact]
    public async Task Equivalent_direct_folder_paths_remain_one_choice()
    {
        var target = Path.Combine(_root, "project run");
        Directory.CreateDirectory(target);
        var equivalentRoot = Path.Combine(_root, "nested", "..");
        var service = new SmartFileSearchService(
            _mappings,
            [(equivalentRoot, "Secondary", 7), (_root, "Primary", 10)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(target, Assert.Single(result.Candidates).Path);
        Assert.Equal("Primary", result.Best.RootLabel);
    }

    [Fact]
    public async Task Fuzzy_only_folder_results_remain_available()
    {
        var target = Path.Combine(_root, "nested", "Project room");
        Directory.CreateDirectory(target);
        var service = new SmartFileSearchService(_mappings, [(_root, "Secondary", 7)]);

        var result = await service.SearchAsync("project rum folder", "", false, true, CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Contains(result.Candidates, candidate => candidate.Path == target);
        Assert.All(result.Candidates, candidate => Assert.True(candidate.NameMatchRank < 3));
    }

    [Fact]
    public async Task Normalized_exact_root_folder_avoids_irrelevant_files_and_deeper_scan()
    {
        var root = Path.Combine(_root, "punctuated");
        var target = Path.Combine(root, "Project! - Copy");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(root, "archive", "Project Copy"));
        for (var i = 0; i < 100; i++)
            File.WriteAllText(Path.Combine(root, $"noise-{i}.txt"), "fixture");
        var service = new SmartFileSearchService(_mappings, [(root, "Primary", 10)]);

        var result = await service.SearchAsync("project copy folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(target, Assert.Single(result.Candidates).Path);
        Assert.Equal(4, result.Best.NameMatchRank);
        Assert.Equal(0, result.Best.Depth);
        Assert.Equal(2, result.ScannedEntries);
        Assert.False(result.TimedOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normalized_exact_folder_shortcut_checks_all_root_level_peers(bool reverseRoots)
    {
        var firstRoot = Path.Combine(_root, "first-punctuated");
        var secondRoot = Path.Combine(_root, "second-punctuated");
        var first = Path.Combine(firstRoot, "Project! - Copy");
        var second = Path.Combine(secondRoot, "Project--Copy");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(Path.Combine(firstRoot, "archive", "Project Copy"));
        var roots = new[] { (firstRoot, "First", 10), (secondRoot, "Second", 10) };
        var service = new SmartFileSearchService(_mappings, reverseRoots ? roots.Reverse().ToArray() : roots);

        var result = await service.SearchAsync("project copy folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { first, second }.Order(), result.Candidates.Select(candidate => candidate.Path).Order());
        Assert.All(result.Candidates, candidate => Assert.Equal(0, candidate.Depth));
        Assert.Equal(3, result.ScannedEntries);
    }

    [Fact]
    public async Task Deeper_exact_folder_in_higher_priority_root_cannot_outrank_root_level_exact()
    {
        var highRoot = Path.Combine(_root, "higher");
        var lowRoot = Path.Combine(_root, "lower");
        var deeper = Path.Combine(highRoot, "archive", "Project Copy");
        var direct = Path.Combine(lowRoot, "Project! - Copy");
        Directory.CreateDirectory(deeper);
        Directory.CreateDirectory(direct);
        var service = new SmartFileSearchService(_mappings, [(highRoot, "Higher", 10), (lowRoot, "Lower", 6)]);

        var result = await service.SearchAsync("project copy folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(direct, Assert.Single(result.Candidates).Path);
        Assert.Equal("Lower", result.Best.RootLabel);
        Assert.Equal(2, result.ScannedEntries);
    }

    [Fact]
    public async Task Folder_search_without_root_level_exact_match_still_descends()
    {
        var root = Path.Combine(_root, "recursive");
        var target = Path.Combine(root, "archive", "Project! - Copy");
        Directory.CreateDirectory(target);
        var service = new SmartFileSearchService(_mappings, [(root, "Primary", 10)]);

        var result = await service.SearchAsync("project copy folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(target, Assert.Single(result.Candidates).Path);
        Assert.Equal(1, result.Best.Depth);
        Assert.Equal(2, result.ScannedEntries);
    }

    [Fact]
    public async Task Mixed_search_still_enumerates_files_and_deeper_exact_matches()
    {
        var root = Path.Combine(_root, "mixed");
        var direct = Path.Combine(root, "Project! - Copy");
        var deeper = Path.Combine(root, "archive", "Project Copy");
        var file = Path.Combine(root, "Project! - Copy.txt");
        Directory.CreateDirectory(direct);
        Directory.CreateDirectory(deeper);
        File.WriteAllText(file, "fixture");
        var service = new SmartFileSearchService(_mappings, [(root, "Primary", 10)]);

        var result = await service.SearchAsync("project copy folder", "", true, true, CancellationToken.None);

        Assert.Contains(result.Candidates, candidate => candidate.Path == direct);
        Assert.Contains(result.Candidates, candidate => candidate.Path == deeper);
        Assert.Contains(result.Candidates, candidate => candidate.Path == file);
        Assert.Equal(4, result.ScannedEntries);
    }

    [Fact]
    public async Task Cancelled_folder_search_does_not_enter_the_recursive_scan()
    {
        var root = Path.Combine(_root, "cancelled");
        Directory.CreateDirectory(Path.Combine(root, "Project! - Copy"));
        var service = new SmartFileSearchService(_mappings, [(root, "Primary", 10)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.SearchAsync("project copy folder", "", false, true, cancellation.Token);

        Assert.Equal(FileResolutionStatus.Cancelled, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.ScannedEntries);
    }

    [Fact]
    public async Task Cancelled_direct_folder_search_does_not_return_a_stale_hit()
    {
        var root = Path.Combine(_root, "cancelled-direct");
        Directory.CreateDirectory(Path.Combine(root, "Project Copy"));
        var service = new SmartFileSearchService(_mappings, [(root, "Primary", 10)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.SearchAsync("project copy folder", "", false, true, cancellation.Token);

        Assert.Equal(FileResolutionStatus.Cancelled, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Exact_compact_folder_basename_uses_the_bounded_direct_lookup()
    {
        var target = Path.Combine(_root, "ProjectRun");
        Directory.CreateDirectory(target);
        var service = new SmartFileSearchService(_mappings, [(_root, "Primary", 10)]);

        var result = await service.SearchAsync(
            "project run folder", "", false, true, CancellationToken.None, timeout: TimeSpan.FromTicks(1));

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(target, Assert.Single(result.Candidates).Path);
        Assert.Equal(0, result.ScannedEntries);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Exact_normalized_name_outranks_a_distinct_compact_basename()
    {
        var compact = Path.Combine(_root, "ProjectRun");
        var exact = Path.Combine(_root, "Project Run");
        Directory.CreateDirectory(compact);
        Directory.CreateDirectory(exact);
        var service = new SmartFileSearchService(_mappings, [(_root, "Primary", 10)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(exact, Assert.Single(result.Candidates).Path);
    }

    [Fact]
    public async Task Distinct_direct_compact_basename_matches_remain_ambiguous()
    {
        var firstRoot = Path.Combine(_root, "first");
        var secondRoot = Path.Combine(_root, "second");
        var first = Path.Combine(firstRoot, "ProjectRun");
        var second = Path.Combine(secondRoot, "ProjectRun");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var service = new SmartFileSearchService(
            _mappings,
            [(firstRoot, "First", 10), (secondRoot, "Second", 10)]);

        var result = await service.SearchAsync("project run folder", "", false, true, CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { first, second }.Order(), result.Candidates.Select(candidate => candidate.Path).Order());
    }

    [Fact]
    public async Task Short_split_name_does_not_gain_a_new_compact_shortcut()
    {
        var target = Path.Combine(_root, "abcd");
        Directory.CreateDirectory(target);
        var service = new SmartFileSearchService(_mappings, [(_root, "Primary", 10)]);

        var result = await service.SearchAsync("ab cd folder", "", false, true, CancellationToken.None);

        Assert.True(result.ScannedEntries > 0);
    }

    [Fact]
    public async Task Explicit_location_scopes_before_global_duplicate_ranking()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var downloads = Path.Combine(_root, "Downloads");
        var desktopTarget = Path.Combine(desktop, "keyboard.wtf");
        var downloadsTarget = Path.Combine(downloads, "some", "nested", "keyboard.wtf");
        Directory.CreateDirectory(desktopTarget);
        Directory.CreateDirectory(downloadsTarget);
        var service = new SmartFileSearchService(
            _mappings,
            [(desktop, "Desktop", 10), (downloads, "Downloads", 9)]);

        var result = await service.SearchAsync(
            "open the folder called keyboard.wtf on my Desktop",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None,
            timeout: TimeSpan.FromTicks(1));

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(desktopTarget, result.Best.Path);
        Assert.All(result.Candidates, candidate =>
            Assert.StartsWith(desktop, candidate.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Exact_direct_child_beats_a_nested_duplicate_within_requested_location()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var direct = Path.Combine(desktop, "Important_Doc.txt");
        var nested = Path.Combine(desktop, "archive", "Important_Doc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(direct, "current");
        File.WriteAllText(nested, "archived");
        var service = new SmartFileSearchService(_mappings, [(desktop, "Desktop", 10)]);

        var result = await service.SearchAsync(
            "find the important doc on my desktop",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(direct, result.Best.Path);
    }

    [Theory]
    [InlineData("tanush oppo")]
    [InlineData("TANUSH-OPPO")]
    [InlineData("tanush_op")]
    public async Task Explicit_location_normalizes_case_spaces_underscores_hyphens_and_partial_names(
        string queryName)
    {
        var desktop = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(desktop);
        var target = Path.Combine(desktop, "Tanush_oppo.txt");
        File.WriteAllText(target, "test");
        var service = new SmartFileSearchService(_mappings, [(desktop, "Desktop", 10)]);

        var result = await service.SearchAsync(
            $"find {queryName} on my Desktop",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
    }

    [Fact]
    public async Task Explicit_path_scope_ignores_a_learned_duplicate_outside_that_scope()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var downloads = Path.Combine(_root, "Downloads");
        var desktopTarget = Path.Combine(desktop, "keyboard.wtf");
        var downloadsTarget = Path.Combine(downloads, "keyboard.wtf");
        Directory.CreateDirectory(desktopTarget);
        Directory.CreateDirectory(downloadsTarget);
        _mappings.Remember(
            LearnedMappingKind.Folder,
            "keyboard wtf",
            downloadsTarget,
            "Downloads copy");
        var service = new SmartFileSearchService(
            _mappings,
            [(desktop, "Desktop", 10), (downloads, "Downloads", 9)]);

        var result = await service.SearchAsync(
            "keyboard wtf",
            desktop,
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(desktopTarget, result.Best.Path);
    }

    [Theory]
    [InlineData("open it on my Desktop", "desktop")]
    [InlineData("find it in downloads", "downloads")]
    [InlineData("locate it inside my Documents", "documents")]
    [InlineData("open it from OneDrive", "onedrive")]
    public void Extracts_explicit_known_location_from_the_full_user_request(
        string query,
        string expected) =>
        Assert.Equal(expected, SmartFileSearchService.ResolveRequestedLocation(query, ""));

    [Fact]
    public void BlocksDangerousFileTypes()
    {
        Assert.True(SmartFileSearchService.IsRiskyFile(Path.Combine(_root, "dangerous-installer.exe")));
        Assert.False(SmartFileSearchService.TryOpenSafe(
            Path.Combine(_root, "dangerous-installer.exe"),
            out var error));
        Assert.Contains("blocked", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LatestScreenshotPrefersMostRecentMatch()
    {
        var older = Path.Combine(_root, "screenshot-old.png");
        var newer = Path.Combine(_root, "screenshot-new.png");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        var service = new SmartFileSearchService(_mappings, [(_root, "Test", 10)]);

        var result = await service.SearchAsync(
            "latest screenshot",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(newer, result.Best.Path);
    }

    [Fact]
    public async Task UsesLearnedFolderAlias()
    {
        var folder = Path.Combine(_root, "nested", "project screenshots");
        _mappings.Remember(LearnedMappingKind.Folder, "demo assets", folder, "Project screenshots");
        var service = new SmartFileSearchService(_mappings, [(_root, "Test", 10)]);

        var result = await service.SearchAsync(
            "demo assets",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(folder, result.Best.Path);
    }

    [Fact]
    public async Task UsesFuzzyLearnedFolderAlias()
    {
        var folder = Path.Combine(_root, "nested", "project screenshots");
        _mappings.Remember(LearnedMappingKind.Folder, "hackathon assets", folder, "Project screenshots");
        var service = new SmartFileSearchService(_mappings, [(_root, "Test", 10)]);

        var result = await service.SearchAsync(
            "hackaton asset",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(folder, result.Best.Path);
    }

    [Fact]
    public async Task BreadthFirstSearchFindsMatchOutsideLargeFirstFolder()
    {
        var noisy = Path.Combine(_root, "a-noisy");
        var targetRoot = Path.Combine(_root, "z-target");
        Directory.CreateDirectory(noisy);
        Directory.CreateDirectory(targetRoot);
        for (var i = 0; i < 1200; i++)
            File.WriteAllText(Path.Combine(noisy, $"noise-{i:D4}.txt"), "test");
        var target = Path.Combine(targetRoot, "keyboard-demo-video.mp4");
        File.WriteAllText(target, "test");
        var service = new SmartFileSearchService(
            _mappings,
            [(noisy, "Noisy", 10), (targetRoot, "Target", 9)]);

        var result = await service.SearchAsync(
            "keyboard demo video",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None,
            maxDepth: 2,
            maxResults: 5,
            // Exercise the real production search budget. A two-second budget
            // made this filesystem-I/O test depend on shared-runner load even
            // though the breadth-first result was correct.
            timeout: SmartFileSearchService.DefaultTimeout);

        Assert.False(result.TimedOut);
        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
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
