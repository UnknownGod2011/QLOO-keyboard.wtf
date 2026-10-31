using System.Reflection;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

// Source-role tests only: no provider, native clipboard, foreground input or UI.
[Collection("Live event integration")]
public sealed class SelectedTranslationCopiedResultTests : IDisposable
{
    private const string OriginalSource = "Atlas hackathon. Judges: Ada Smith and Robin Jones. Deadline: 30 October. Footer end marker.";
    private const string CopiedJudges = "Ada Smith\nRobin Jones";
    private const string CurrentSource = "A different current document, with its complete ending.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-copied-source-tests", Guid.NewGuid().ToString("N"));
    private readonly JarvisAutomationService _automation;
    private readonly CommandRegistry _commands;
    private string _clipboard = "unrelated old clipboard";
    private string _currentContext = "fixture-document-A";
    private bool _clipboardReadThrows;

    public SelectedTranslationCopiedResultTests()
    {
        var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        _automation = new(null, settings, new(Path.Combine(_directory, "history.json")),
            new(Path.Combine(_directory, "mappings.json")), () => { });
        _commands = new(null, null, null, settings, null, automation: _automation,
            currentSelectionContextId: () => _currentContext,
            captureSelectedText: _ => throw new InvalidOperationException("Tests must explicitly provide a fake selection capture."),
            readClipboardText: () => _clipboardReadThrows
                ? throw new InvalidOperationException("Fixture clipboard read unavailable.") : _clipboard);
    }

    [Theory]
    [InlineData("Translate it to French.")]
    [InlineData("Translate that to French and copy it to my clipboard.")]
    [InlineData("Hi Jarvis, could you translate it to French?")]
    [InlineData("Thanks. Do me a favor: translate it to French and copy it.")]
    public async Task Immediate_pronoun_follow_up_uses_the_verified_copied_result_not_the_full_selection(string request)
    {
        RetainOriginal();
        CopyAndComplete(CopiedJudges);
        Observe(request);
        Assert.True(_commands.UsesRecentVerifiedCopiedResult(request));
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            throw new InvalidOperationException("The copied-result follow-up must not recapture the original page.");
        }, CancellationToken.None);

        Assert.Equal(0, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal(CopiedJudges, result["text"]);
        Assert.Equal("verified_clipboard_result", result["source"]);
        Assert.Equal("user_copied_result_content", result["content_role"]);
        Assert.Equal(false, result["truncated"]);
        Assert.Equal(OriginalSource, OriginalText());
    }

    [Theory]
    [InlineData("Translate the selection to French and copy it to my clipboard.")]
    [InlineData("Translate the current selected text to French and copy it to my clipboard.")]
    public async Task Explicit_selection_is_fresh_foreground_authority_even_after_copying_a_derived_answer(string request)
    {
        RetainOriginal();
        CopyAndComplete(CopiedJudges);
        _currentContext = "fixture-document-B";
        Observe(request);
        Assert.False(_commands.UsesRecentVerifiedCopiedResult(request));
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(Capture(CurrentSource, _currentContext, "Current document", "capture-B"));
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal(CurrentSource, result["text"]);
        Assert.Equal("current_selection", result["source"]);
        Assert.NotEqual(CopiedJudges, result["text"]);
        Assert.Equal(CopiedJudges, _automation.LatestVerifiedClipboardCopy.Text);
    }

    [Fact]
    public async Task Explicit_original_selection_remains_distinct_after_a_copied_result_follow_up()
    {
        RetainOriginal();
        CopyAndComplete(CopiedJudges);
        Observe("Translate it to French.");
        var judges = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            throw new InvalidOperationException("No foreground recapture for the copied result."), CancellationToken.None);
        Assert.Equal(CopiedJudges, judges["text"]);
        _commands.CompleteJarvisUserTurn();

        const string request = "Translate the original selection to French and copy it to clipboard.";
        Observe(request);
        Assert.False(_commands.UsesRecentVerifiedCopiedResult(request));
        var original = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            throw new InvalidOperationException("An explicit original reference uses the retained original source."), CancellationToken.None);
        Assert.Equal(true, original["ok"]);
        Assert.Equal(OriginalSource, original["text"]);
        Assert.Equal("recent_selection", original["source"]);
        Assert.Equal("capture-A", original["capture_id"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_or_unavailable_clipboard_fails_truthfully_without_selection_fallback(bool readThrows)
    {
        RetainOriginal();
        CopyAndComplete(CopiedJudges);
        _clipboard = "unrelated new clipboard";
        _clipboardReadThrows = readThrows;
        const string request = "Translate it to French and copy it to clipboard.";
        Observe(request);
        Assert.True(_commands.UsesRecentVerifiedCopiedResult(request));
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(Capture(CurrentSource, "fixture-document-B", "Other document", "capture-B"));
        }, CancellationToken.None);

        Assert.Equal(0, captures);
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["needs_clarification"]);
        Assert.Equal(true, result["clipboard_unchanged"]);
        Assert.False(result.ContainsKey("text"));
        Assert.Equal(OriginalSource, OriginalText());
        Assert.Equal("unrelated new clipboard", _clipboard);
    }

    [Fact]
    public async Task Late_owned_copy_from_A_cannot_steal_the_pronoun_after_turn_B_completed()
    {
        RetainOriginal();
        const string originA = "Translate the original selection to French and copy it to clipboard.";
        Observe(originA);
        _commands.CompleteJarvisUserTurn();
        Observe("Analyze this selected text.");
        _currentContext = "fixture-document-B";
        Record(Capture(CurrentSource, _currentContext, "Other document", "capture-B"));
        _commands.CompleteJarvisUserTurn();

        var late = VerifiedCopy("Late translated output from A");
        Assert.True(_automation.AttributeVerifiedCopy("Late translated output from A", originA, late));
        const string request = "Translate it to French.";
        Observe(request);
        Assert.False(_commands.UsesRecentVerifiedCopiedResult(request));
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(Capture(CurrentSource, _currentContext, "Other document", "capture-B-current"));
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(CurrentSource, result["text"]);
        Assert.NotEqual(_automation.LatestVerifiedClipboardCopy.Text, result["text"]);
    }

    [Fact]
    public async Task Failed_copy_does_not_authorize_a_copied_result_follow_up()
    {
        RetainOriginal();
        Observe("Copy the judges' names to my clipboard.");
        var failed = _automation.CopyText(CopiedJudges, _ => true, () => "old unrelated clipboard");
        Assert.Equal(false, failed["ok"]);
        _commands.CompleteJarvisUserTurn();
        Observe("Translate it to French.");
        Assert.False(_commands.UsesRecentVerifiedCopiedResult("Translate it to French."));
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            Task.FromResult(Capture(CurrentSource, "fixture-document-B", "Other document", "capture-B")), CancellationToken.None);
        Assert.Equal(CurrentSource, result["text"]);
        Assert.NotEqual(CopiedJudges, result["text"]);
    }

    [Fact]
    public async Task Session_shutdown_clears_copied_result_authority_and_original_source_history()
    {
        RetainOriginal();
        CopyAndComplete(CopiedJudges);
        Assert.NotNull(_automation.LatestVerifiedClipboardCopy);
        await _commands.HandleJarvisSessionEndingAsync();
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.Equal("", OriginalText());

        Observe("Translate it to French.");
        Assert.False(_commands.UsesRecentVerifiedCopiedResult("Translate it to French."));
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false, ["message"] = "No current selection in this fixture." });
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));
    }

    [Fact]
    public void An_unrelated_completed_question_ends_immediate_copied_result_pronoun_authority()
    {
        RetainOriginal();
        CopyAndComplete(CopiedJudges);
        Observe("What is two plus two?");
        _commands.CompleteJarvisUserTurn();
        Observe("Translate it to French.");
        Assert.False(_commands.UsesRecentVerifiedCopiedResult("Translate it to French."));
        Assert.Equal(OriginalSource, OriginalText());
        Assert.Equal(CopiedJudges, _automation.LatestVerifiedClipboardCopy.Text);
    }

    private void RetainOriginal()
    {
        Observe("Analyze this selected text.");
        Record(Capture(OriginalSource, "fixture-document-A", "Atlas hackathon", "capture-A"));
        _commands.CompleteJarvisUserTurn();
    }

    private void CopyAndComplete(string text)
    {
        Observe("Copy the judges' names to my clipboard.");
        var receipt = VerifiedCopy(text);
        Assert.Equal(true, receipt["clipboard_verified"]);
        _commands.CompleteJarvisUserTurn();
    }

    private Dictionary<string, object> VerifiedCopy(string text) => _automation.CopyText(text,
        value => { _clipboard = value; return true; }, () => _clipboard);

    private void Observe(string request) => _commands.ObserveJarvisTranscript(request, CancellationToken.None);

    private void Record(Dictionary<string, object> result) => typeof(CommandRegistry)
        .GetMethod("RecordCapturedBrowserContext", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_commands, ["get_selected_text", result]);

    private string OriginalText() => (string)typeof(CommandRegistry)
        .GetField("_originalSelectedText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_commands)!;

    private static Dictionary<string, object> Capture(string text, string context, string title, string id)
    {
        var result = JarvisAutomationService.SelectionResult(text, fresh: true);
        result["source_context_id"] = context;
        result["source_title"] = title;
        result["source_app"] = "fixture";
        result["capture_id"] = id;
        return result;
    }

    public void Dispose()
    {
        _automation.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
