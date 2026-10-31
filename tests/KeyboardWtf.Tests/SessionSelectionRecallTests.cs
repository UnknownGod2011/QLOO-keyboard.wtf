namespace KeyboardWtf.Tests;

using System.Reflection;
using KeyboardWtf.Services;
using Xunit;

[Collection("Live event integration")]
public sealed class SessionSelectionRecallTests
{
    private readonly CommandRegistry _commands = new(null, null, null, null, null,
        currentSelectionContextId: () => "current-document-B");

    [Fact]
    public async Task Named_earlier_selection_survives_a_different_accepted_selection()
    {
        Accept("Devpost Shipaton", "selection-A", "The deadline is October 30. FULL_A_END");
        Accept("Logitech feedback", "selection-B", "An unrelated feedback form. FULL_B_END");
        _commands.ObserveJarvisTranscript("What was the deadline in that Devpost selection?", CancellationToken.None);
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            throw new InvalidOperationException("A named historic source must not be replaced by foreground B."), CancellationToken.None);
        Assert.Equal(true, result["ok"]);
        Assert.Equal("The deadline is October 30. FULL_A_END", result["text"]);
        Assert.Equal("selection-A", result["capture_id"]);
    }

    [Fact]
    public async Task Earlier_named_selection_does_not_mean_latest_original()
    {
        Accept("Devpost Shipaton", "selection-A", "FULL_A");
        Accept("Logitech feedback", "selection-B", "FULL_B");
        _commands.ObserveJarvisTranscript("Translate the earlier Devpost selection to French.", CancellationToken.None);
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            throw new InvalidOperationException("Historic request must not capture B."), CancellationToken.None);
        Assert.Equal(true, result["ok"]);
        Assert.Equal("FULL_A", result["text"]);
    }

    [Fact]
    public async Task Named_unavailable_source_does_not_substitute_current_or_latest_source()
    {
        Accept("Document A", "selection-A", "FULL_A");
        Accept("Document B", "selection-B", "FULL_B");
        _commands.ObserveJarvisTranscript("What did that missing document selection say?", CancellationToken.None);
        var result = await ReadWithoutCapture();
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["needs_clarification"]);
        Assert.False(result.ContainsKey("text"));
    }

    [Fact]
    public async Task Two_same_named_sources_require_clarification_not_latest_match()
    {
        Accept("Devpost first document", "selection-A", "FULL_A");
        Accept("Devpost second document", "selection-B", "FULL_B");
        _commands.ObserveJarvisTranscript("Translate that Devpost selection to French.", CancellationToken.None);
        var result = await ReadWithoutCapture();
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["needs_clarification"]);
    }

    [Fact]
    public async Task Generic_selection_with_no_foreground_and_multiple_sources_asks_which()
    {
        Accept("Document A", "selection-A", "FULL_A");
        Accept("Document B", "selection-B", "FULL_B");
        _commands.ObserveJarvisTranscript("Translate the selection to French.", CancellationToken.None);
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            Task.FromResult(new Dictionary<string, object> { ["ok"] = false }), CancellationToken.None);
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["needs_clarification"]);
        Assert.False(result.ContainsKey("text"));
    }

    [Fact]
    public async Task Clear_same_document_followup_attempts_fresh_first_then_retains_one_original()
    {
        Accept("Document B", "selection-B", "FULL_B");
        _commands.ObserveJarvisTranscript("Who are the judges?", CancellationToken.None);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate the selection to French.", CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false });
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal("FULL_B", result["text"]);
        Assert.Equal("recent_selection", result["source"]);
    }

    [Fact]
    public async Task Unrelated_turns_and_clipboard_answers_never_replace_retained_source()
    {
        Accept("Devpost Shipaton", "selection-A", "BEGIN_A|FULL_A|END_A");
        for (var iteration = 0; iteration < 40; iteration++)
        {
            _commands.ObserveJarvisTranscript("Tell me a fun fact.", CancellationToken.None);
            Record("get_clipboard_text", new Dictionary<string, object>
            { ["ok"] = true, ["text"] = "A generated answer, not the original." });
            _commands.CompleteJarvisUserTurn();
            _commands.ObserveJarvisTranscript("What did that Devpost selection say?", CancellationToken.None);
            var result = await ReadWithoutCapture();
            Assert.Equal("BEGIN_A|FULL_A|END_A", result["text"]);
            _commands.CompleteJarvisUserTurn();
        }
    }

    [Fact]
    public async Task Session_shutdown_clears_all_sources_not_only_active_one()
    {
        Accept("Devpost Shipaton", "selection-A", "FULL_A");
        Accept("Document B", "selection-B", "FULL_B");
        await _commands.HandleJarvisSessionEndingAsync();
        _commands.ObserveJarvisTranscript("What did that Devpost selection say?", CancellationToken.None);
        var result = await ReadWithoutCapture();
        Assert.Equal(false, result["ok"]);
        Assert.Empty((object[])result["selection_history"]);
    }

    [Fact]
    public void History_is_bounded_and_deduplicates_unchanged_recaptures()
    {
        var history = new SessionSelectionHistory();
        for (var iteration = 0; iteration < 40; iteration++)
            history.Accept(new() { ["text"] = "Complete unchanged source", ["source_context_id"] = "document", ["capture_id"] = iteration.ToString() });
        Assert.Equal(1, history.Count);
        for (var iteration = 0; iteration < 20; iteration++)
            history.Accept(new() { ["text"] = new string('x', 50_000), ["source_context_id"] = iteration.ToString() });
        Assert.Equal(SessionSelectionHistory.Capacity, history.Count);
        history.Clear();
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Reaccepted_unchanged_source_updates_recency_without_duplicate_history()
    {
        var history = new SessionSelectionHistory();
        var first = new Dictionary<string, object> { ["text"] = "A", ["source_context_id"] = "A" };
        history.Accept(first);
        history.Accept(new() { ["text"] = "B", ["source_context_id"] = "B" });
        history.Accept(first);
        Assert.Equal(2, history.Count);
        Assert.Equal("B", history.Previous.Text);
    }

    [Theory]
    [InlineData("Analyze this Devpost selection.")]
    [InlineData("Translate this Devpost selection to French.")]
    public async Task This_named_selection_attempts_current_capture_not_historic_source(string request)
    {
        Accept("Devpost Shipaton", "selection-A", "OLD_A");
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var calls = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            calls++;
            return Task.FromResult(JarvisAutomationService.SelectionResult("NEW_CURRENT", fresh: true));
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("NEW_CURRENT", result["text"]);
    }

    [Theory]
    [InlineData("that Devpost selection", "devpost")]
    [InlineData("the earlier Devpost selection", "devpost")]
    [InlineData("What was the deadline in that Devpost selection?", "devpost")]
    [InlineData("the Contract Review selection", "contract review")]
    public void Named_reference_uses_provenance_words_not_fuzzy_content(string request, string label)
    {
        Assert.True(SessionSelectionHistory.TryReferenceLabel(request, out var actual));
        Assert.Equal(label, actual);
    }

    [Theory]
    [InlineData("Translate the selection to French.")]
    [InlineData("Translate the whole selection to French.")]
    [InlineData("Analyze this selected text.")]
    [InlineData("Translate the fresh selection to French.")]
    [InlineData("Rewrite my newly selected text clearly.")]
    [InlineData("Translate the different selection to French.")]
    [InlineData("Translate the currently selected text to French.")]
    [InlineData("Translate the current selected text to French and copy it to clipboard.")]
    public void Current_or_generic_selection_is_not_a_named_history_reference(string request) =>
        Assert.False(SessionSelectionHistory.TryReferenceLabel(request, out _));

    private Task<Dictionary<string, object>> ReadWithoutCapture() =>
        _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            throw new InvalidOperationException("Explicit historic reference must not capture unrelated foreground text."), CancellationToken.None);

    private void Record(string tool, Dictionary<string, object> result) =>
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_commands, new object[] { tool, result });

    private void Accept(string title, string id, string text)
    {
        _commands.ObserveJarvisTranscript("Analyze this selected text.", CancellationToken.None);
        var result = JarvisAutomationService.SelectionResult(text, fresh: true);
        result["source_title"] = title;
        result["source_app"] = "chrome";
        result["source_context_id"] = id == "selection-A" ? "previous-document-A" : "current-document-B";
        result["capture_id"] = id;
        Record("get_selected_text", result);
        _commands.CompleteJarvisUserTurn();
    }
}
