namespace KeyboardWtf.Tests;

using System.Reflection;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

// These tests exercise only the dispatcher/source boundary. No automation
// service, provider, clipboard, foreground input, or persisted settings is used.
[Collection("Live event integration")]
public sealed class FreshSelectedSourceAuthorityTests
{
    private readonly CommandRegistry _commands;
    private string _currentContextId = "fixture-document-A";
    private const string EarlierSource = "The complete original source from page A.";
    private const string CurrentSource = "A different complete selection from page B.";

    public FreshSelectedSourceAuthorityTests()
    {
        _commands = new CommandRegistry(null, null, null, null, null,
            currentSelectionContextId: () => _currentContextId,
            captureSelectedText: _ => Task.FromResult(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["message"] = "No fixture selection supplied; native input must not be used.",
            }));
    }

    [Theory]
    [InlineData("Translate the selected text to French.")]
    [InlineData("Translate selected text to French and copy it to my clipboard.")]
    [InlineData("Translate the current selected text to French and copy it to my clipboard.")]
    [InlineData("Translate my newly selected text to French and copy it to clipboard.")]
    [InlineData("Translate the fresh selection to French and copy it to clipboard.")]
    [InlineData("Translate the selection to French.")]
    [InlineData("Please translate my entire selection into French and copy the result to clipboard.")]
    [InlineData("Could you please translate this highlighted text into French?")]
    [InlineData("Hi Jarvis, can you please translate the selected text to French and copy it to my clipboard.")]
    [InlineData("Jarvis, please translate my selection into French.")]
    [InlineData("Hello Jarvis! Could you translate this highlighted text into French?")]
    public async Task Explicit_selection_request_captures_B_instead_of_reusing_A(string request)
    {
        RetainPageA();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var captures = 0;

        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true));
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal(CurrentSource, result["text"]);
        Assert.Equal("current_selection", result["source"]);
    }

    [Theory]
    [InlineData("Translate the selected text to French.")]
    [InlineData("Translate the current selection to French and copy it to clipboard.")]
    [InlineData("Translate the current selected text to French and copy it to my clipboard.")]
    [InlineData("Translate my newly selected text to French and copy it to clipboard.")]
    [InlineData("Translate the fresh selection to French and copy it to clipboard.")]
    [InlineData("Please translate this entire highlighted text into French.")]
    [InlineData("Hi Jarvis, can you please translate the selected text to French and copy it to clipboard.")]
    public async Task Failed_explicit_selection_capture_never_returns_page_A(string request)
    {
        RetainPageA();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var captures = 0;

        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["message"] = "The current foreground has no readable selection.",
            });
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));
    }

    [Fact]
    public async Task Failed_current_capture_preserves_A_only_for_an_explicit_original_request()
    {
        RetainPageA();
        _commands.ObserveJarvisTranscript("Translate the current selection to French.", CancellationToken.None);
        var freshCaptures = 0;
        var failed = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            freshCaptures++;
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false });
        }, CancellationToken.None);
        Assert.Equal(1, freshCaptures);
        Assert.Equal(false, failed["ok"]);
        Assert.False(failed.ContainsKey("text"));

        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate the original selection to French.", CancellationToken.None);
        var unrelatedCaptures = 0;
        var original = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            unrelatedCaptures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true));
        }, CancellationToken.None);

        Assert.Equal(0, unrelatedCaptures);
        Assert.Equal(true, original["ok"]);
        Assert.Equal(EarlierSource, original["text"]);
        Assert.Equal("recent_selection", original["source"]);
    }

    [Fact]
    public async Task Successful_fresh_capture_becomes_the_next_complete_original_source()
    {
        RetainPageA();
        _commands.ObserveJarvisTranscript("Translate the selected text to French.", CancellationToken.None);
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true)),
            CancellationToken.None);
        Assert.Equal(CurrentSource, result["text"]);

        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate the entire original selection to French.", CancellationToken.None);
        var captures = 0;
        var original = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult("Unrelated page C", fresh: true));
        }, CancellationToken.None);

        Assert.Equal(0, captures);
        Assert.Equal(CurrentSource, original["text"]);
        Assert.Equal(false, original["truncated"]);
    }

    [Theory]
    [InlineData("Analyze it.")]
    [InlineData("Translate that to French.")]
    [InlineData("Analyze the whole thing.")]
    [InlineData("Thanks. Do me a favor: translate it to French and copy it.")]
    public async Task Same_document_pronoun_retains_all_50k_only_when_current_capture_is_unavailable(string request)
    {
        var source = "BEGIN|" + new string('x', 49_982) + "|MIDDLE|END!";
        Assert.Equal(50_000, source.Length);
        RetainPageA(source);
        _commands.ObserveJarvisTranscript("Who are the judges?", CancellationToken.None);
        RecordCapture("get_clipboard_text", new Dictionary<string, object>
        {
            ["ok"] = true,
            ["text"] = "An unrelated copied answer or analysis.",
            ["source"] = "clipboard_text",
        });
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false });
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal(source, result["text"]);
        Assert.Equal(false, result["truncated"]);
    }

    [Theory]
    [InlineData("fixture-document-B")]
    [InlineData("")]
    public async Task Changed_or_unproven_context_with_failed_capture_cannot_recall_old_source(string currentContext)
    {
        RetainPageA();
        _currentContextId = currentContext;
        _commands.ObserveJarvisTranscript("Translate it to French.", CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false });
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));

        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate the original selection to French.", CancellationToken.None);
        var original = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true));
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(EarlierSource, original["text"]);
    }

    [Theory]
    [InlineData("fixture-document-A")]
    [InlineData("fixture-document-B")]
    [InlineData("")]
    public async Task A_pronoun_uses_available_current_selection_even_if_old_context_matches_or_changes(string currentContext)
    {
        RetainPageA();
        _currentContextId = currentContext;
        _commands.ObserveJarvisTranscript("Translate it to French.", CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            var fresh = JarvisAutomationService.SelectionResult(CurrentSource, fresh: true);
            fresh["source_context_id"] = currentContext;
            return Task.FromResult(fresh);
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal(CurrentSource, result["text"]);
        Assert.Equal("current_selection", result["source"]);
    }

    [Fact]
    public async Task A_pronoun_without_an_earlier_snapshot_can_use_a_real_current_selection()
    {
        _commands.ObserveJarvisTranscript("Translate it to French.", CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true));
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(CurrentSource, result["text"]);
    }

    [Theory]
    [InlineData("fixture-document-A")]
    [InlineData("fixture-document-B")]
    public async Task Wrapped_validated_pronoun_preserves_current_source_authority(string context)
    {
        RetainPageA();
        _currentContextId = context;
        _commands.ObserveJarvisTranscript("Thanks. Do me a favor: translate it to French and copy it.", CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true));
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(CurrentSource, result["text"]);
        Assert.Equal("current_selection", result["source"]);
    }

    [Fact]
    public async Task Wrapped_pronoun_cannot_recall_a_different_source_after_failed_fresh_capture()
    {
        RetainPageA();
        _currentContextId = "fixture-document-B";
        _commands.ObserveJarvisTranscript("Thanks. Translate it to French and copy it.", CancellationToken.None);
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
            Task.FromResult(new Dictionary<string, object> { ["ok"] = false }), CancellationToken.None);
        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("incomplete")]
    public async Task A_pronoun_cannot_fall_back_to_old_source_when_current_capture_is_incomplete(string failure)
    {
        RetainPageA();
        _commands.ObserveJarvisTranscript("Translate it to French.", CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(failure == "oversized"
                ? JarvisAutomationService.SelectionResult(new string('x', 50_001), fresh: true)
                : JarvisAutomationService.SelectionResult("A partial current selection", fresh: true, truncated: true));
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["truncated"]);
        Assert.Equal("", result["text"]);
    }

    [Fact]
    public async Task Incomplete_new_selection_revokes_implicit_recall_but_preserves_explicit_original()
    {
        RetainPageA();
        _commands.ObserveJarvisTranscript("Translate it to French.", CancellationToken.None);
        await _commands.GetRecentSelectedTextOrRecaptureAsync(_ => Task.FromResult(
            JarvisAutomationService.SelectionResult("Incomplete new source", fresh: true, truncated: true)),
            CancellationToken.None);
        _commands.CompleteJarvisUserTurn();

        _commands.ObserveJarvisTranscript("Translate it to French.", CancellationToken.None);
        var unavailable = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ => Task.FromResult(
            new Dictionary<string, object> { ["ok"] = false }), CancellationToken.None);
        Assert.Equal(false, unavailable["ok"]);
        Assert.False(unavailable.ContainsKey("text"));
        _commands.CompleteJarvisUserTurn();

        _commands.ObserveJarvisTranscript("Translate the original selection to French.", CancellationToken.None);
        var original = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ => throw new InvalidOperationException(
            "Explicit original recall must not capture unrelated current text."), CancellationToken.None);
        Assert.Equal(EarlierSource, original["text"]);
    }

    [Theory]
    [InlineData("winword", "Fixture page - Google Chrome")]
    [InlineData("excel", "Fixture page - Google Chrome")]
    [InlineData("msedge", "Fixture page - Google Chrome")]
    [InlineData("chrome", "Other page - Google Chrome")]
    public void Background_Chrome_selection_cannot_be_a_foreground_fallback(string process, string title)
    {
        var context = new SelectionContext((IntPtr)9, process, title);
        var helper = HelperIdentity();
        Assert.False(JarvisAutomationService.CanTryBrowserSelectionFallback(context, helper));
        var receipt = new BrowserSelectionReceipt
        {
            Ok = true, Text = "Background selection", TotalLength = 20,
            SessionId = helper.SessionId, WindowId = helper.WindowId, TabId = helper.TabId,
        };
        Assert.False(JarvisAutomationService.BrowserSelectionReceiptIsOwned(receipt, helper, context, context));
    }

    [Fact]
    public async Task Session_end_clears_original_source_and_its_implicit_authority()
    {
        RetainPageA();
        await _commands.HandleJarvisSessionEndingAsync();
        _commands.ObserveJarvisTranscript("Translate the original selection to French.", CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true));
        }, CancellationToken.None);

        Assert.Equal(0, captures);
        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));
        Assert.Equal("", typeof(CommandRegistry).GetField("_originalSelectionContextId",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_commands));
    }

    [Fact]
    public async Task Still_open_translation_turn_reuses_its_accepted_capture_when_final_transcript_adds_copy_clause()
    {
        const string partial = "Translate the selected text to French";
        const string complete = partial + " and copy it to my clipboard";
        var source = new string('x', 21_000) + "ORIGINAL_END";
        var accepted = JarvisAutomationService.SelectionResult(source, fresh: true);
        accepted["source_context_id"] = _currentContextId;
        accepted["capture_id"] = "accepted-capture-in-this-open-turn";
        _commands.ObserveJarvisTranscript(partial, CancellationToken.None);
        var captures = 0;
        var first = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(accepted);
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(source, first["text"]);

        // No turnComplete occurred: this is cumulative transcription, not a
        // second user request. Re-reading could fail or capture unrelated text.
        _commands.ObserveJarvisTranscript(complete, CancellationToken.None);
        var reused = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false });
        }, CancellationToken.None);
        Assert.Equal(1, captures);
        Assert.Equal(true, reused["ok"]);
        Assert.Equal(source, reused["text"]);
        Assert.Equal(accepted["capture_id"], reused["capture_id"]);
        Assert.Equal(accepted["source_context_id"], reused["source_context_id"]);

        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(complete, CancellationToken.None);
        var later = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult(CurrentSource, fresh: true));
        }, CancellationToken.None);
        Assert.Equal(2, captures);
        Assert.Equal(CurrentSource, later["text"]);
    }

    [Theory]
    [InlineData("native-window")]
    [InlineData("native-title")]
    [InlineData("browser-window")]
    [InlineData("tab")]
    [InlineData("session")]
    [InlineData("url")]
    [InlineData("document-epoch")]
    public void Browser_source_fingerprint_changes_when_its_document_identity_changes(string changed)
    {
        var context = new SelectionContext((IntPtr)9, "chrome", "Fixture page - Google Chrome");
        var original = JarvisAutomationService.SelectionSourceContextId(context, HelperIdentity());
        var current = changed switch
        {
            "native-window" => JarvisAutomationService.SelectionSourceContextId(context with { Handle = (IntPtr)10 }, HelperIdentity()),
            "native-title" => JarvisAutomationService.SelectionSourceContextId(context with { WindowTitle = "Other page - Google Chrome" }, HelperIdentity(title: "Other page")),
            "browser-window" => JarvisAutomationService.SelectionSourceContextId(context, HelperIdentity(window: 11)),
            "tab" => JarvisAutomationService.SelectionSourceContextId(context, HelperIdentity(tab: 22)),
            "session" => JarvisAutomationService.SelectionSourceContextId(context, HelperIdentity(session: "new-session")),
            "url" => JarvisAutomationService.SelectionSourceContextId(context, HelperIdentity(url: "https://example.org/other")),
            "document-epoch" => JarvisAutomationService.SelectionSourceContextId(context, HelperIdentity(epoch: "new-document")),
            _ => throw new InvalidOperationException("Unknown identity fixture."),
        };

        Assert.False(string.IsNullOrWhiteSpace(original));
        Assert.False(string.IsNullOrWhiteSpace(current));
        Assert.False(CommandRegistry.SelectionSourceContextMatches(original, current));
        Assert.DoesNotContain(context.WindowTitle, original);
        Assert.DoesNotContain("example.org", original);
    }

    [Theory]
    [InlineData("no-helper")]
    [InlineData("not-ready")]
    [InlineData("not-fresh")]
    [InlineData("no-window")]
    [InlineData("no-tab")]
    [InlineData("no-session")]
    [InlineData("no-url")]
    [InlineData("no-title")]
    [InlineData("mismatched-title")]
    public void Incomplete_browser_identity_cannot_authorize_implicit_recall(string missing)
    {
        var context = new SelectionContext((IntPtr)9, "chrome", "Fixture page - Google Chrome");
        var helper = missing switch
        {
            "no-helper" => null,
            "not-ready" => HelperIdentity(ready: false),
            "not-fresh" => HelperIdentity(fresh: false),
            "no-window" => HelperIdentity(window: null),
            "no-tab" => HelperIdentity(tab: null),
            "no-session" => HelperIdentity(session: ""),
            "no-url" => HelperIdentity(url: ""),
            "no-title" => HelperIdentity(title: ""),
            "mismatched-title" => HelperIdentity(title: "Unrelated page"),
            _ => throw new InvalidOperationException("Unknown identity fixture."),
        };

        var identity = JarvisAutomationService.SelectionSourceContextId(context, helper);
        Assert.Equal("", identity);
        Assert.False(CommandRegistry.SelectionSourceContextMatches(identity, identity));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("winword")]
    [InlineData("excel")]
    [InlineData("msedge")]
    public void Other_foreground_apps_do_not_inherit_background_Chrome_Helper_identity(string process)
    {
        var context = new SelectionContext((IntPtr)9, process, "Fixture document");
        var identity = JarvisAutomationService.SelectionSourceContextId(context, null);
        Assert.False(string.IsNullOrWhiteSpace(identity));
        Assert.True(CommandRegistry.SelectionSourceContextMatches(identity,
            JarvisAutomationService.SelectionSourceContextId(context, null)));
        Assert.False(CommandRegistry.SelectionSourceContextMatches(identity,
            JarvisAutomationService.SelectionSourceContextId(context with { Handle = (IntPtr)10 }, null)));
        Assert.Equal(identity, JarvisAutomationService.SelectionSourceContextId(context, HelperIdentity()));
        Assert.Equal(identity, JarvisAutomationService.SelectionSourceContextId(context,
            HelperIdentity(tab: 21, url: "https://example.org/other", epoch: "changed-background-document")));
    }

    private static BrowserHelperStatus HelperIdentity(bool ready = true, bool fresh = true,
        int? window = 10, int? tab = 20, string session = "fixture-session",
        string url = "https://example.org/fixture", string epoch = "fixture-document",
        string title = "Fixture page") => new()
    {
        CommandChannelReady = ready,
        SelectionReadSupported = true,
        ActiveTabFresh = fresh,
        WindowId = window,
        TabId = tab,
        SessionId = session,
        ActiveUrl = url,
        ActiveTitle = title,
        DomEpoch = epoch,
    };

    private void RetainPageA(string source = EarlierSource)
    {
        _commands.ObserveJarvisTranscript("Analyze this selected text.", CancellationToken.None);
        var capture = JarvisAutomationService.SelectionResult(source, fresh: true);
        capture["source_context_id"] = "fixture-document-A";
        RecordCapture("get_selected_text", capture);
        _commands.CompleteJarvisUserTurn();
    }

    private void RecordCapture(string tool, Dictionary<string, object> capture) =>
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_commands, [tool, capture]);
}
