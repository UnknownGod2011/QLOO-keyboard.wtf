using KeyboardWtf.Services;
using System.Text.Json;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class FolderChoiceContinuationTests
{
    [Fact]
    public void Distinct_strong_folders_are_consumed_once_by_explicit_next_turn()
    {
        using var fixture = new FolderFixture();
        var choice = new FolderChoiceContinuation();
        Assert.Equal(2, choice.Remember(fixture.Ambiguous()));
        choice.CompleteUserTurn();
        choice.ObserveUserTurn("Open both");
        Assert.True(choice.TryConsume("Open both", out var paths));
        Assert.Equal(2, paths.Length);
        Assert.Contains(fixture.First, paths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(fixture.Second, paths, StringComparer.OrdinalIgnoreCase);
        Assert.False(choice.TryConsume("Open both", out _));
    }

    [Fact]
    public void Duplicate_absolute_path_is_one_logical_choice()
    {
        using var fixture = new FolderFixture();
        var choice = new FolderChoiceContinuation();
        Assert.Equal(1, choice.Remember(fixture.Ambiguous(secondPath: fixture.First)));
        choice.CompleteUserTurn();
        Assert.False(choice.TryConsume("Open all", out _));
    }

    [Theory]
    [InlineData("open the other folder")]
    [InlineData("explain both folders")]
    [InlineData("open the screenshot")]
    public void Unrelated_next_turn_clears_the_pending_authority(string nextTurn)
    {
        using var fixture = new FolderFixture();
        var choice = new FolderChoiceContinuation();
        choice.Remember(fixture.Ambiguous());
        choice.CompleteUserTurn();
        choice.ObserveUserTurn(nextTurn);
        Assert.False(choice.TryConsume("Open both", out _));
    }

    [Fact]
    public void Originating_turn_fragments_cannot_consume_its_own_choice()
    {
        using var fixture = new FolderFixture();
        var choice = new FolderChoiceContinuation();
        choice.Remember(fixture.Ambiguous());
        choice.ObserveUserTurn("open both");
        Assert.False(choice.TryConsume("open both", out _));
    }

    [Fact]
    public void Session_reset_discards_pending_folder_paths()
    {
        using var fixture = new FolderFixture();
        var choice = new FolderChoiceContinuation();
        choice.Remember(fixture.Ambiguous());
        choice.CompleteUserTurn();
        choice.Clear();
        Assert.False(choice.TryConsume("open all", out _));
    }

    [Fact]
    public async Task Dispatcher_opens_each_distinct_retained_folder_once_even_when_model_proposes_one_folder()
    {
        using var folders = new FolderFixture();
        var opened = new List<string>();
        using var dispatcher = new DispatcherFixture(path =>
        {
            opened.Add(path);
            return (true, "");
        });
        Assert.Equal(2, dispatcher.Automation.RememberFolderAmbiguity(folders.Ambiguous()));
        dispatcher.Commands.CompleteJarvisUserTurn();
        dispatcher.Commands.ObserveSubmittedJarvisText("Open both", CancellationToken.None);

        // The provider is allowed to propose only one folder. The next user's
        // explicit plural instruction, not that argument, owns the path set.
        var first = await dispatcher.Commands.ExecuteJarvisToolAsync(
            "open_folder",
            JsonSerializer.SerializeToElement(new { folder = folders.First }),
            CancellationToken.None);
        var duplicateProposal = await dispatcher.Commands.ExecuteJarvisToolAsync(
            "open_app",
            JsonSerializer.SerializeToElement(new { app_name = "Explorer" }),
            CancellationToken.None);

        Assert.Equal(true, first["ok"]);
        Assert.Equal(2, first["requested_count"]);
        Assert.Equal(2, first["opened_count"]);
        Assert.Equal(true, duplicateProposal["duplicate_suppressed"]);
        Assert.Equal(2, opened.Count);
        Assert.Contains(folders.First, opened, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(folders.Second, opened, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, dispatcher.History.Snapshot().Count(entry => entry.Action == "open_folder"));
    }

    [Fact]
    public async Task Dispatcher_does_not_reuse_folder_choice_after_an_unrelated_intervening_turn()
    {
        using var folders = new FolderFixture();
        var opened = new List<string>();
        using var dispatcher = new DispatcherFixture(path =>
        {
            opened.Add(path);
            return (true, "");
        });
        dispatcher.Automation.RememberFolderAmbiguity(folders.Ambiguous());
        dispatcher.Commands.CompleteJarvisUserTurn();
        dispatcher.Commands.ObserveSubmittedJarvisText("What time is it?", CancellationToken.None);
        dispatcher.Commands.CompleteJarvisUserTurn();
        dispatcher.Commands.ObserveSubmittedJarvisText("Open both", CancellationToken.None);

        var result = await dispatcher.Commands.ExecuteJarvisToolAsync(
            "not_a_tool", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.Equal(false, result["ok"]);
        Assert.Empty(opened);
    }

    [Fact]
    public void Removed_folder_aborts_entire_plural_open_before_any_side_effect()
    {
        using var folders = new FolderFixture();
        var opened = new List<string>();
        using var dispatcher = new DispatcherFixture(path =>
        {
            opened.Add(path);
            return (true, "");
        });
        dispatcher.Automation.RememberFolderAmbiguity(folders.Ambiguous());
        dispatcher.Automation.CompleteUserTurnForFolderChoices();
        Directory.Delete(folders.Second);

        Assert.True(dispatcher.Automation.TryOpenPendingFolderChoices(
            "open all", CancellationToken.None, out var result));
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["needs_clarification"]);
        Assert.Empty(opened);
        Assert.False(dispatcher.Automation.TryOpenPendingFolderChoices(
            "open all", CancellationToken.None, out _));
    }

    [Fact]
    public void Partial_os_failure_is_reported_as_partial_not_all_opened()
    {
        using var folders = new FolderFixture();
        var opened = new List<string>();
        using var dispatcher = new DispatcherFixture(path =>
        {
            opened.Add(path);
            return (path == folders.First, path == folders.First ? "" : "fixture failure");
        });
        dispatcher.Automation.RememberFolderAmbiguity(folders.Ambiguous());
        dispatcher.Automation.CompleteUserTurnForFolderChoices();

        Assert.True(dispatcher.Automation.TryOpenPendingFolderChoices(
            "open both", CancellationToken.None, out var result));
        Assert.Equal(false, result["ok"]);
        Assert.Equal(2, result["requested_count"]);
        Assert.Equal(1, result["opened_count"]);
        Assert.Equal(1, result["failed_count"]);
        Assert.DoesNotContain("all 2", result["message"].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, opened.Count);
    }

    private sealed class DispatcherFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-dispatch-" + Guid.NewGuid().ToString("N"));
        public JarvisAutomationService Automation { get; }
        public CommandRegistry Commands { get; }
        public JarvisActionHistoryService History { get; }

        public DispatcherFixture(Func<string, (bool Opened, string Error)> opener)
        {
            Directory.CreateDirectory(_root);
            var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
            History = new JarvisActionHistoryService(Path.Combine(_root, "history.json"));
            var settings = new SettingsService();
            Automation = new JarvisAutomationService(
                null, settings, History, mappings, () => { },
                timerService: null, folderChoiceOpener: opener);
            Commands = new CommandRegistry(
                null, null, null, settings, null, automation: Automation, history: History);
        }

        public void Dispose()
        {
            Automation.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FolderFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-choice-" + Guid.NewGuid().ToString("N"));
        public string First { get; }
        public string Second { get; }

        public FolderFixture()
        {
            First = Path.Combine(_root, "Project Copy");
            Second = Path.Combine(_root, "Project--Copy");
            Directory.CreateDirectory(First);
            Directory.CreateDirectory(Second);
        }

        public FileSearchResult Ambiguous(string secondPath = null)
        {
            var first = Candidate(First);
            var second = Candidate(secondPath ?? Second);
            return new FileSearchResult
            {
                Status = FileResolutionStatus.Ambiguous,
                Best = first,
                Candidates = [first, second],
            };
        }

        private static FileSearchCandidate Candidate(string path) => new()
        {
            Path = path,
            Name = Path.GetFileName(path),
            IsDirectory = true,
            NameMatchRank = 4,
            Depth = 0,
            Score = 1,
        };

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
