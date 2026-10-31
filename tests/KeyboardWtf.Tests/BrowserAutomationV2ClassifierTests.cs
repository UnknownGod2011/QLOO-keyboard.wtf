using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationV2ClassifierTests
{
    private readonly BrowserAutomationTaskClassifier _classifier = new();

    [Theory]
    [InlineData("Thank you. Also, do me a favor. Can you please translate the selected text to French and copy to my clipboard?")]
    [InlineData("Translate the selected text to French and copy the result to my clipboard.")]
    [InlineData("Translate this selection to French and copy it.")]
    [InlineData("Can you translate all of the selected text into French and put the translation on the clipboard?")]
    [InlineData("Summarize the highlighted text and copy the summary to clipboard.")]
    [InlineData("Explain the selected code and copy your answer to the clipboard.")]
    [InlineData("Translate the selected text to French and copy it to clipboard please.")]
    [InlineData("Translate the selected text to French and copy it to clipboard for me.")]
    [InlineData("Translate the selected text to French and copy the entire translation to clipboard.")]
    public void Selected_content_and_local_clipboard_output_do_not_authorize_page_automation(string request)
    {
        var result = _classifier.Classify(request);
        Assert.NotEqual(BrowserAutomationDecision.BrowserAutomation, result.Decision);
        Assert.True(result.PrefersSelectedContent);
    }

    [Theory]
    [InlineData("Translate the selected text and paste it into the comment field on this page.")]
    [InlineData("Translate the selection, copy it, then click Submit on this page.")]
    [InlineData("Use Browser Automation to translate the selection and fill this form.")]
    [InlineData("Translate the selected text into French and copy the result to clipboard; then open the oldest video on YouTube.")]
    [InlineData("Translate the selected text and copy it to clipboard, then open the Reports tab on this page.")]
    [InlineData("Translate the selected text and copy it to clipboard, then search this page for platypus.")]
    public void Selected_transformation_with_an_actual_page_action_keeps_browser_authority(string request) =>
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, _classifier.Classify(request).Decision);

    [Theory]
    [InlineData("open_url", false)]
    [InlineData("open_app", false)]
    [InlineData("open_service_page", false)]
    [InlineData("take_photo", false)]
    [InlineData("browser_automation_status", false)]
    [InlineData("browser_automation", true)]
    public void A_browser_pre_route_cannot_steal_a_selected_direct_tool(
        string selectedTool,
        bool expected)
    {
        Assert.Equal(expected, CommandRegistry.ShouldPreRouteBrowserTaskForTool(selectedTool));
    }

    [Theory]
    [InlineData("Open Amazon")]
    [InlineData("Search YouTube for MrBeast")]
    [InlineData("Search Amazon for MX Master 4")]
    [InlineData("Open https://example.com")]
    [InlineData("Open YouTube")]
    [InlineData("Switch to Chrome's fourth tab using Ctrl+4")]
    [InlineData("Go to the third browser tab")]
    [InlineData("Switch to the last tab")]
    [InlineData("Go to the last Chrome tab")]
    [InlineData("Go to the second last tab")]
    [InlineData("Go to the second last browser tab")]
    [InlineData("Switch to the second-to-last tab")]
    [InlineData("Set a ten second timer called cancellation check.")]
    [InlineData("Cancel the active timer.")]
    [InlineData("Show my active timers.")]
    [InlineData("Open Gmail, Discord, Devpost, Instagram, and Excel.")]
    [InlineData("Whenever I say potato patato, open Gmail, Discord, Devpost, Instagram, and Excel. Remember this.")]
    [InlineData("Open Instagram and Discord.")]
    [InlineData("Open Atlas Mail, then open Harbor Chat, and take a picture.")]
    [InlineData("Open the saved project portal, open the calendar, then launch Calculator.")]
    [InlineData("Open my saved portal and take a picture.")]
    [InlineData("Open Gmail.")]
    [InlineData("Open Devpost.")]
    [InlineData("Always open Discord on the web instead.")]
    [InlineData("Add Instagram to my duck duck setup.")]
    [InlineData("Remove Devpost from that workflow.")]
    [InlineData("Change Gmail to Google Sheets in that workflow.")]
    public void Simple_open_and_search_commands_stay_on_fast_tools(string request)
    {
        var result = _classifier.Classify(request);
        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Open https://example.com normally in Chrome. Do not use Browser Automation.")]
    [InlineData("Open example.com without browser automation.")]
    [InlineData("Open https://example.com normally in Chrome, without Browser Automation. Do not enter or submit anything.")]
    [InlineData("Open https://example.org. Do not use browser automation or click any controls.")]
    [InlineData("Fill this page without browser automation.")]
    [InlineData("Pause browser automation before entering the next value.")]
    [InlineData("Cancel browser automation.")]
    public void Negated_or_controlled_automation_mentions_do_not_start_a_browser_task(
        string request)
    {
        var result = _classifier.Classify(request);

        Assert.False(BrowserAutomationTaskClassifier.HasExplicitAutomationRequest(request));
        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Use Browser Automation to fill this form.")]
    [InlineData("Automate the browser and click the application button.")]
    public void Affirmative_automation_mentions_remain_authoritative(string request)
    {
        Assert.True(BrowserAutomationTaskClassifier.HasExplicitAutomationRequest(request));
        Assert.Equal(
            BrowserAutomationDecision.BrowserAutomation,
            _classifier.Classify(request).Decision);
    }

    [Theory]
    [InlineData("What happened to the Browser Automation task that failed just before the successful one?")]
    [InlineData("What went wrong with browser automation?")]
    [InlineData("Why did that browser task fail?")]
    [InlineData("Could you explain what happened to the automation task?")]
    public void Outcome_diagnostics_do_not_authorize_a_new_browser_task(string request)
    {
        Assert.True(BrowserAutomationTaskClassifier.IsOutcomeDiagnosticQuestion(request));
        Assert.False(BrowserAutomationTaskClassifier.HasExplicitAutomationRequest(request));
        Assert.Equal(
            BrowserAutomationDecision.SimpleTool,
            _classifier.Classify(request).Decision);
    }

    [Fact]
    public void Explicit_investigation_task_remains_an_affirmative_automation_request()
    {
        const string request =
            "Use Browser Automation to investigate why this page failed to load.";

        Assert.False(BrowserAutomationTaskClassifier.IsOutcomeDiagnosticQuestion(request));
        Assert.True(BrowserAutomationTaskClassifier.HasExplicitAutomationRequest(request));
        Assert.Equal(
            BrowserAutomationDecision.BrowserAutomation,
            _classifier.Classify(request).Decision);
    }

    [Theory]
    [InlineData("What happened to the Browser Automation task, and retry it now.")]
    [InlineData("Why did it fail? Use Browser Automation to try again.")]
    public void Diagnostic_followed_by_an_explicit_action_remains_actionable(string request)
    {
        Assert.False(BrowserAutomationTaskClassifier.IsOutcomeDiagnosticQuestion(request));
        Assert.True(BrowserAutomationTaskClassifier.HasExplicitAutomationRequest(request));
        Assert.Equal(
            BrowserAutomationDecision.BrowserAutomation,
            _classifier.Classify(request).Decision);
    }

    [Theory]
    [InlineData("Summarize this page and copy it to clipboard")]
    [InlineData("Fill this Google Form")]
    [InlineData("Click Oldest on this page")]
    [InlineData("Add MX Master 4 to my Amazon cart")]
    [InlineData("Play the oldest NASA video on YouTube")]
    [InlineData("Open Wikipedia, summarize Albert Einstein, and paste it into Google Docs")]
    [InlineData("Fill this quiz")]
    [InlineData("Could you please fill this quiz?")]
    [InlineData("Solve this quiz on the active tab")]
    [InlineData("Solve the quiz but do not submit it")]
    [InlineData("Answer only the first unanswered image-based question on this page")]
    [InlineData("Send Yaksh a message on Instagram")]
    [InlineData("Reload this page, then close the harmless popup")]
    [InlineData("Open GitHub, find browser-use/browser-use, and report visible stars and forks")]
    [InlineData("Find the cheapest round-trip flights from Mumbai to Switzerland in September 2026 for one week")]
    [InlineData("Compare the best three options and open the lowest-priced result")]
    [InlineData("Open YouTube and play latest video by NASA")]
    [InlineData("Open the team portal and click the Reports tab")]
    [InlineData("Can you play NASA's latest video on YouTube?")]
    [InlineData("Play latest YouTube video by MrBeast")]
    public void Live_page_interactions_use_browser_automation(string request)
    {
        var result = _classifier.Classify(request);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("Maybe we could add a community area where users can post recipes.")]
    [InlineData("Let's brainstorm which of these product ideas would be best.")]
    [InlineData("What do you think about a discussion feature for this idea?")]
    [InlineData("The selected text suggests people could publish their own examples.")]
    public void Brainstorming_and_hypothetical_page_topics_stay_with_normal_jarvis(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Analyze this selected context; when I navigate to the form, use it to fill the fields.")]
    [InlineData("Keep this highlighted information. Once I tell you the page is ready, use browser automation to enter it.")]
    [InlineData("Wait for me to open the intended page, then fill it from the selected details.")]
    [InlineData("Analyze the selected feedback and keep it for this conversation. Wait for my separate instruction before filling the survey; I have not moved to the form yet.")]
    [InlineData("Review this highlighted application. Hold for our confirmation before entering anything on the page.")]
    [InlineData("Read my selection; wait for us to confirm the destination before using browser automation.")]
    [InlineData("Remember this selected information until I ask you to fill the form.")]
    [InlineData("Keep the copied details until I tell you to fill the survey.")]
    [InlineData("Retain this highlighted content until we confirm you should fill the form.")]
    public void Future_user_controlled_browser_work_waits_instead_of_starting_now(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
        Assert.True(BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(request));
        Assert.True(result.PrefersSelectedContent);
    }

    [Theory]
    [InlineData("Use the selected details to fill this form.")]
    [InlineData("Fill this page from the information I copied to the clipboard.")]
    [InlineData("Use the selected feedback to fill this survey.")]
    [InlineData("Fill the current form from my selection.")]
    public void Current_selected_context_mutation_waits_for_fresh_capture_before_pre_routing(
        string request)
    {
        Assert.True(BrowserAutomationTaskClassifier.RequiresFreshUserContextCapture(request));
        Assert.True(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            request,
            fromLocalTextSubmission: false));
    }

    [Fact]
    public void Immediate_page_mutation_remains_actionable()
    {
        const string request = "Use the selected details to fill this form now.";

        Assert.False(BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(request));
        Assert.Equal(
            BrowserAutomationDecision.BrowserAutomation,
            _classifier.Classify(request).Decision);
    }

    [Theory]
    [InlineData("Fill the current form using my selection now.")]
    [InlineData("Remember this selected information as a saved note for next month.")]
    public void Immediate_or_durable_selection_requests_are_not_deferred_browser_authority(
        string request) =>
        Assert.False(BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(request));

    [Fact]
    public void Captured_context_is_preserved_verbatim_in_the_one_ephemeral_browser_task_contract()
    {
        const string context =
            "Project: Clear Finch\nTeam size: 3\nWebsite: https://clearfinch.example";

        var task = CommandRegistry.BuildBrowserTaskWithCapturedContext(
            "Fill the current form and skip unknown optional fields.",
            "selected text",
            context);

        Assert.Contains("AUTHORITATIVE USER-PROVIDED SELECTED TEXT", task, StringComparison.Ordinal);
        Assert.Contains(context, task, StringComparison.Ordinal);
        Assert.Contains("genuinely missing required information", task, StringComparison.Ordinal);
        Assert.DoesNotContain("source is partial", task, StringComparison.Ordinal);
    }

    [Fact]
    public void Clipboard_capture_keeps_later_answers_within_the_browser_task_budget()
    {
        var clipboard = new string('a', 7500) + " answer-near-the-tail";
        var capture = JarvisAutomationService.BuildClipboardTextResult(clipboard);

        Assert.Equal(false, capture["truncated"]);
        Assert.Equal(clipboard.Length, capture["original_character_count"]);
        Assert.True(CommandRegistry.TryGetCapturedContextText(capture, out var captured));
        var task = CommandRegistry.BuildBrowserTaskWithCapturedContext(
            "Fill this form from the copied information.", "clipboard text", captured);

        Assert.Contains("answer-near-the-tail", task, StringComparison.Ordinal);
        Assert.DoesNotContain("source is partial", task, StringComparison.Ordinal);
        Assert.True(task.Length <= 10000);
    }

    [Fact]
    public void Clipboard_capture_reports_both_source_and_task_budget_truncation_without_claiming_absence()
    {
        var clipboard = new string('b', 8500) + " omitted-answer";
        var capture = JarvisAutomationService.BuildClipboardTextResult(clipboard);

        Assert.Equal(true, capture["truncated"]);
        Assert.Equal(clipboard.Length, capture["original_character_count"]);
        Assert.Equal(JarvisAutomationService.ClipboardContextCharacterLimit,
            ((string)capture["text"]).Length);
        Assert.True(CommandRegistry.TryGetCapturedContextText(capture, out var captured));
        Assert.Equal(JarvisAutomationService.ClipboardContextCharacterLimit, captured.Length);

        var task = CommandRegistry.BuildBrowserTaskWithCapturedContext(
            "Fill this form from the copied information.", "clipboard text", captured,
            sourceTruncated: true);
        Assert.Contains("source is partial", task, StringComparison.Ordinal);
        Assert.Contains("do not claim it was absent from the original", task, StringComparison.Ordinal);
        Assert.DoesNotContain("omitted-answer", task, StringComparison.Ordinal);
        Assert.True(task.Length <= 10000);

        var longTask = CommandRegistry.BuildBrowserTaskWithCapturedContext(
            new string('c', 3900), "clipboard text", new string('d', 7000));
        Assert.Contains("source is partial", longTask, StringComparison.Ordinal);
        Assert.True(longTask.Length <= 10000);
    }

    [Fact]
    public void Empty_clipboard_does_not_satisfy_capture_and_selected_text_limit_is_unchanged()
    {
        var empty = JarvisAutomationService.BuildClipboardTextResult("");
        Assert.Equal("Clipboard is empty.", empty["message"]);
        Assert.Equal(false, empty["truncated"]);
        Assert.Equal(0, empty["original_character_count"]);
        Assert.False(CommandRegistry.TryGetCapturedContextText(empty, out _));

        Assert.True(CommandRegistry.TryGetCapturedContextText(
            new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "current_selection",
                ["text"] = new string('s', 7000),
            }, out var selected));
        Assert.Equal(6000, selected.Length);

        var partialSelectionTask = CommandRegistry.BuildBrowserTaskWithCapturedContext(
            "Fill the form from my selection.", "selected text", selected,
            sourceTruncated: true);
        Assert.Contains("source is partial", partialSelectionTask, StringComparison.Ordinal);
        Assert.Equal(6000, selected.Length);
    }

    [Fact]
    public void Clipboard_capture_does_not_split_a_unicode_surrogate_at_its_boundary()
    {
        var clipboard = new string('x', JarvisAutomationService.ClipboardContextCharacterLimit - 1)
            + "😀 later information";

        var capture = JarvisAutomationService.BuildClipboardTextResult(clipboard);

        Assert.Equal(true, capture["truncated"]);
        Assert.Equal(new string('x', JarvisAutomationService.ClipboardContextCharacterLimit - 1),
            capture["text"]);
        Assert.Equal(clipboard.Length, capture["original_character_count"]);
    }

    [Theory]
    [InlineData("Fill this form using my selected context.", "get_selected_text")]
    [InlineData("Use the highlighted details to fill this page.", "get_selected_text")]
    [InlineData("Fill the current form from the copied information.", "get_clipboard_text")]
    [InlineData("Fill this form using the context I provided earlier.", "")]
    [InlineData("Explain my selected text.", "")]
    public void Fresh_context_capture_uses_only_the_source_explicitly_owned_by_the_task(
        string request,
        string expectedTool)
    {
        Assert.Equal(
            expectedTool,
            BrowserAutomationTaskClassifier.FreshContextCaptureTool(request));
    }

    [Fact]
    public void Captured_context_result_requires_a_successful_nonempty_text_value()
    {
        Assert.True(CommandRegistry.TryGetCapturedContextText(
            new Dictionary<string, object>
            {
                ["ok"] = true,
                ["text"] = "  current selection  ",
            },
            out var text));
        Assert.Equal("current selection", text);

        Assert.False(CommandRegistry.TryGetCapturedContextText(
            new Dictionary<string, object>
            {
                ["ok"] = false,
                ["text"] = "stale selection",
            },
            out _));
        Assert.False(CommandRegistry.TryGetCapturedContextText(
            new Dictionary<string, object>
            {
                ["ok"] = true,
                ["text"] = "   ",
            },
            out _));
    }

    [Theory]
    [InlineData(true, true, true, false, true, true)]
    [InlineData(true, true, false, false, true, false)]
    [InlineData(true, false, false, true, true, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(false, true, true, true, true, false)]
    public void Captured_context_requires_current_turn_ownership_or_explicit_future_authorization(
        bool fresh,
        bool referencesContext,
        bool sameTurn,
        bool authorizedForLater,
        bool pageMutation,
        bool expected)
    {
        Assert.Equal(
            expected,
            CommandRegistry.CanAttachCapturedBrowserContext(
                fresh,
                referencesContext,
                sameTurn,
                authorizedForLater,
                pageMutation));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void Semantic_context_reference_can_authorize_one_fresh_mutating_browser_task(
        bool fresh,
        bool semanticReference,
        bool pageMutation,
        bool expected)
    {
        Assert.Equal(
            expected,
            CommandRegistry.CanAttachCapturedBrowserContext(
                fresh,
                requestReferencesContext: false,
                capturedForCurrentTurn: false,
                explicitlyAuthorizedForLaterAction: false,
                requestRequiresPageMutation: pageMutation,
                currentRequestUsesCapturedContext: semanticReference));
    }

    [Theory]
    [InlineData("Use selected information to fill the form when I tell you to.", "All right, can you please fill the form now?", true)]
    [InlineData("Use selected information to fill the form when I tell you to.", "Please fill this form now.", true)]
    [InlineData("Use selected information to fill the form when I tell you to.", "Please fill a different form now.", false)]
    [InlineData("Use selected information to fill the form when I tell you to.", "Click the account menu now.", false)]
    [InlineData("Use selected information to fill the form when I tell you to.", "Open a different website.", false)]
    [InlineData("Fill the form from my selection now.", "Fill the form now.", false)]
    public void Deferred_context_can_follow_only_a_compatible_later_page_action(
        string original,
        string current,
        bool expected) =>
        Assert.Equal(expected,
            CommandRegistry.IsCompatibleDeferredBrowserAction(original, current));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Compatible_deferred_action_still_needs_authorization_and_mutation(
        bool authorized,
        bool mutation,
        bool expected) =>
        Assert.Equal(expected,
            CommandRegistry.CanAttachCapturedBrowserContext(
                fresh: true,
                requestReferencesContext: false,
                capturedForCurrentTurn: false,
                explicitlyAuthorizedForLaterAction: authorized,
                requestRequiresPageMutation: mutation,
                requestContinuesDeferredAction: true));

    [Theory]
    [InlineData("save_note", "Use selected information to fill the form when I tell you to.", true)]
    [InlineData("save_note", "Use the clipboard text to fill the form when I tell you to.", true)]
    [InlineData("save_note", "Remember this selected information until I ask you to fill the form.", true)]
    [InlineData("save_note", "Keep the copied details until I tell you to fill the survey.", true)]
    [InlineData("save_note", "Save this selected text as a local note, then use it to fill the form when I tell you to.", false)]
    [InlineData("save_note", "Take a note of this selected information, then use it to fill the form when I tell you to.", false)]
    [InlineData("save_note", "Save this document, and keep my selected information until I tell you to fill the form.", true)]
    [InlineData("save_note", "Save my selected text to a Word document, then use it to fill the form when I tell you to.", true)]
    [InlineData("save_note", "Save a note about parking, then keep my selected information until I tell you to fill the form.", true)]
    [InlineData("save_note", "Save my selected text as a local note, then use it to fill the form when I tell you to.", false)]
    [InlineData("save_note", "Keep the selected information until I ask you to fill the form; don't save a note.", true)]
    [InlineData("save_note", "Analyze this selected information.", false)]
    [InlineData("get_selected_text", "Use selected information to fill the form when I tell you to.", false)]
    public void Deferred_browser_source_does_not_become_a_permanent_note_without_user_authority(
        string proposedTool,
        string userTurn,
        bool expected) =>
        Assert.Equal(expected,
            CommandRegistry.ShouldRetainDeferredBrowserSourceInsteadOfSavingNote(
                proposedTool, userTurn));

    [Theory]
    [InlineData("Fill this form using my selected context.", "selected text", true)]
    [InlineData("Fill this form using my selected context.", "clipboard text", false)]
    [InlineData("Fill this form using the copied clipboard content.", "clipboard text", true)]
    [InlineData("Fill this form using the copied clipboard content.", "selected text", false)]
    [InlineData("Now fill the form using that retained context.", "selected text", true)]
    [InlineData("Now fill the form using that retained context.", "clipboard text", true)]
    public void Explicit_fresh_context_source_cannot_be_satisfied_by_another_source(
        string request,
        string capturedSource,
        bool expected) =>
        Assert.Equal(
            expected,
            CommandRegistry.CapturedContextSourceMatchesRequest(request, capturedSource));

    [Theory]
    [InlineData(true, "Fill the current form.", true)]
    [InlineData(true, "Click the requested control.", true)]
    [InlineData(true, "Open the project website.", false)]
    [InlineData(false, "Fill the current form.", false)]
    public void Available_ephemeral_context_defers_only_mutating_eager_browser_starts(
        bool contextAvailable,
        string request,
        bool expected) =>
        Assert.Equal(
            expected,
            CommandRegistry.ShouldDeferBrowserPreRouteForCapturedContext(
                contextAvailable,
                request));

    [Theory]
    [InlineData("Open the discussion tab.")]
    [InlineData("Go to the community section.")]
    [InlineData("Search this website for the eligibility criteria.")]
    [InlineData("Go to this page and find the application link.")]
    public void Clear_page_navigation_requests_still_use_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("Answer this")]
    [InlineData("Solve these questions")]
    [InlineData("Explain this")]
    [InlineData("I selected this text, answer that question")]
    [InlineData("Please solve the questions I have highlighted")]
    [InlineData("Answer the question from the selected text")]
    [InlineData("Can you explain the text I've selected?")]
    [InlineData("I selected something. Can you answer it?")]
    [InlineData("I selected a word. What does it mean?")]
    [InlineData("I highlighted a paragraph; give me a summary")]
    [InlineData("What is the meaning of the highlighted content?")]
    [InlineData("Read the currently selected text and tell me what it describes")]
    [InlineData("Review this highlighted passage and discuss its main idea")]
    [InlineData("Inspect the selected text and interpret it for me")]
    public void Selection_style_questions_do_not_preempt_normal_jarvis(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.AskUser, result.Decision);
        Assert.True(result.PrefersSelectedContent);
        Assert.Contains("selected content", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Fill this form")]
    [InlineData("Fill this page with the supplied answers")]
    [InlineData("Use browser automation to answer and fill this")]
    [InlineData("I selected these questions; solve and fill the quiz")]
    [InlineData("I selected a paragraph; paste it into this form")]
    public void Explicit_page_mutation_starts_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("Answer the question")]
    [InlineData("Solve this Coursera quiz question")]
    [InlineData("Explain this question on the current page")]
    public void Incremental_answer_fragments_wait_for_possible_selected_context(string request) =>
        Assert.True(BrowserAutomationTaskClassifier.ShouldDeferIncrementalContentRouting(request));

    [Theory]
    [InlineData("Fill this form")]
    [InlineData("I selected these questions; solve and fill the quiz")]
    [InlineData("Use browser automation to answer this question")]
    [InlineData("Click the answer on this page")]
    public void Explicit_page_actions_are_not_deferred_as_selected_content(string request) =>
        Assert.False(BrowserAutomationTaskClassifier.ShouldDeferIncrementalContentRouting(request));

    [Fact]
    public void Only_incremental_voice_pre_route_defers_an_ambiguous_answer_request()
    {
        Assert.True(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            "Answer the question on this page",
            fromLocalTextSubmission: false));
        Assert.False(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            "Answer the question on this page",
            fromLocalTextSubmission: true));
        Assert.False(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            "Fill the answer into this page",
            fromLocalTextSubmission: false));
    }

    [Theory]
    [InlineData("Click Oldest.")]
    [InlineData("Fill the fields.")]
    [InlineData("Continue filling the answerable fields from the live page state.")]
    [InlineData("Resume answering this quiz without submitting it.")]
    [InlineData("Send Yaksh Dharod a message saying hi on Instagram web.")]
    public void Unambiguous_live_interactions_do_not_require_a_site_qualifier(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void Current_page_language_is_preserved()
    {
        var result = _classifier.Classify("What is on this page?");
        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void This_document_is_grounded_to_the_active_tab()
    {
        var result = _classifier.Classify("In this blank Google Doc, paste a draft at the active cursor.");

        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("In this blank Google Doc, place the exact harmless text at the current cursor.")]
    [InlineData("Write this sentence into the active document.")]
    [InlineData("Insert the summary at the current cursor.")]
    [InlineData("Put this draft into the editor.")]
    public void Natural_document_mutation_phrasing_uses_the_active_tab(string request)
    {
        var result = _classifier.Classify(request);

        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void Ordinary_writing_request_does_not_start_browser_automation()
    {
        var result = _classifier.Classify("Write me a short poem about the moon.");

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Draft a Gmail to Yaksh saying I am sick.")]
    [InlineData("Write an email to Pat about tomorrow's report.")]
    [InlineData("Prepare a WhatsApp message for Alex saying I will be late on WhatsApp.")]
    public void Safe_direct_communication_drafts_do_not_start_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
        Assert.Contains("draft", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Delete all promotional emails from Gmail using browser automation.")]
    [InlineData("Read the newest email on this Gmail page.")]
    public void Live_inbox_work_still_uses_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("browser_automation")]
    [InlineData("browser_automation_status")]
    [InlineData("browser_automation_control")]
    public void Pre_routed_turn_allows_only_browser_lifecycle_tools(string toolName)
    {
        Assert.True(CommandRegistry.IsBrowserTaskLifecycleTool(toolName));
    }

    [Theory]
    [InlineData("type_text")]
    [InlineData("replace_selected_text")]
    [InlineData("press_key")]
    [InlineData("open_url")]
    [InlineData("browser_action")]
    public void Pre_routed_turn_rejects_competing_active_window_tools(string toolName)
    {
        Assert.False(CommandRegistry.IsBrowserTaskLifecycleTool(toolName));
    }

    [Theory]
    [InlineData("remember_routine")]
    [InlineData("edit_workflow")]
    [InlineData("remember_app_alias")]
    [InlineData("remember_link_alias")]
    [InlineData("forget_learned_mapping")]
    [InlineData("set_browser_preference")]
    public void Durable_local_mutations_are_idempotent_pre_route_operations(string toolName)
    {
        Assert.True(CommandRegistry.IsDurableMutationTool(toolName));
    }

    [Fact]
    public void Direct_control_command_is_grounded_to_the_active_page()
    {
        var result = _classifier.Classify("Click Oldest.");

        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void Browser_tool_uses_the_complete_raw_user_turn_instead_of_a_lossy_model_summary()
    {
        const string user =
            "Find the cheapest round-trip flights from Mumbai to Switzerland in September 2026 for one week.";

        Assert.Equal(
            user,
            CommandRegistry.ResolveBrowserAutomationTask("Browser automation", user));
    }

    [Theory]
    [InlineData("Open the calendar, then open the saved project portal, then click the Reports tab.", "click the Reports tab", true)]
    [InlineData("Open the calendar, then open the saved project portal, then click the Reports tab.", "Open the saved project portal, then click the Reports tab", false)]
    [InlineData("Open the calendar, then open the saved project portal, then click the Reports tab.", "Open the calendar, then open the saved project portal, then click the Reports tab", false)]
    [InlineData("Open the calendar, then open the saved project portal, then click the Reports tab.", "click the Settings tab", false)]
    public void Mixed_direct_and_browser_turn_requires_a_verbatim_uncompleted_suffix(
        string userTurn,
        string modelTask,
        bool expected)
    {
        Assert.Equal(expected, CommandRegistry.TryResolveScopedBrowserSubtask(
            modelTask, userTurn, out _));
    }

    [Theory]
    [InlineData("Do that.")]
    [InlineData("Yes, please proceed.")]
    [InlineData("Try again.")]
    public void Browser_tool_keeps_model_resolved_context_for_reference_only_follow_ups(
        string userTurn)
    {
        const string resolved =
            "Fill the currently open form with the details already provided, but do not submit it.";

        Assert.Equal(
            resolved,
            CommandRegistry.ResolveBrowserAutomationTask(resolved, userTurn));
    }

    [Theory]
    [InlineData("I'm on the correct page now; continue here.")]
    [InlineData("Use this current form and resume here.")]
    public void Browser_tool_keeps_resolved_deferred_task_for_an_explicit_current_page_handoff(
        string userTurn)
    {
        const string resolved =
            "Fill the current form from the selected context and skip unavailable optional fields, without submitting.";

        Assert.True(BrowserAutomationCoordinator.IsExplicitPageHandoffGuidance(userTurn));
        Assert.Equal(
            resolved,
            CommandRegistry.ResolveBrowserAutomationTask(resolved, userTurn));
    }

    [Fact]
    public void Vague_web_work_asks_once()
    {
        var result = _classifier.Classify("Do something with this website");
        Assert.Equal(BrowserAutomationDecision.AskUser, result.Decision);
    }
}
