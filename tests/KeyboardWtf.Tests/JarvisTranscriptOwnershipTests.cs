using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class JarvisTranscriptOwnershipTests : IDisposable
{
    [Theory]
    [InlineData("Thank you. Also, do me a favor. Can you please translate the selected text to French and copy to my clipboard?")]
    [InlineData("Translate the whole selected text to French.")]
    public async Task Wrong_browser_tool_for_explicit_selected_content_returns_the_correct_local_route(string request)
    {
        using var coordinator = new BrowserAutomationCoordinator(null, null, new BrowserHelperBridge(), new BrowserAutomationPlanner());
        var commands = new CommandRegistry(null, null, null, null, null, browserAutomation: coordinator);
        commands.ObserveJarvisTranscript(request, CancellationToken.None);

        var result = await commands.ExecuteJarvisToolAsync("browser_automation",
            JsonSerializer.SerializeToElement(new { task = "Translate this webpage using Browser Automation." }),
            CancellationToken.None);

        Assert.Equal(false, result["ok"]);
        Assert.Equal(false, result["started"]);
        Assert.Equal(false, result["needs_clarification"]);
        Assert.Equal("selected_text", result["required_capability"]);
        Assert.Contains("get_selected_text", result["message"].ToString());
        Assert.Contains("full", result["message"].ToString());
        Assert.False(coordinator.Snapshot().CanCancel);
        Assert.Empty(coordinator.RecentTerminalOutcomes());
    }

    [Fact]
    public async Task Ambiguous_content_question_does_not_invent_a_selection_to_skip_clarification()
    {
        using var coordinator = new BrowserAutomationCoordinator(null, null, new BrowserHelperBridge(), new BrowserAutomationPlanner());
        var commands = new CommandRegistry(null, null, null, null, null, browserAutomation: coordinator);
        commands.ObserveJarvisTranscript("Translate it into French.", CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("browser_automation",
            JsonSerializer.SerializeToElement(new { task = "Translate the selected text." }), CancellationToken.None);
        Assert.Equal(false, result["started"]);
        Assert.Equal(true, result["needs_clarification"]);
        Assert.False(result.ContainsKey("required_capability"));
        Assert.False(coordinator.Snapshot().CanCancel);
    }

    [Theory]
    [InlineData("Navigate to the How to Use Jarvis section.")]
    [InlineData("Please open the hotkeys section.")]
    public async Task Semantic_internal_settings_tool_retains_local_ownership(string text)
    {
        using var bridge = new BrowserHelperBridge();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(BrowserHelperBridge).GetMethod("ProcessMessage", flags)!.Invoke(bridge,
        [JsonSerializer.Serialize(new { type = "hello", helper = new {
            extensionId = "gojdebdikepkpapmnggeljneamjdigbh",
            serviceWorkerVersion = "2.1.21", contentScriptVersion = "2.1.21",
            activeTabFresh = true, commandChannelReady = true, commandAuthoritySupported = true,
            activeUrl = "https://example.org/", sessionId = "test-session",
        } })]);
        var settings = new SettingsService(Path.Combine(_directory, "local-only.json"));
        settings.Current.BrowserAutomationEnabled = true;
        settings.Current.BrowserAutomationConsentVersion = SettingsService.CurrentBrowserAutomationConsentVersion;
        using var planner = new BrowserAutomationPlanner();
        using var coordinator = new BrowserAutomationCoordinator(settings, new NotificationService(null), bridge, planner);
        var commands = new CommandRegistry(null, null, null, settings, new NotificationService(null), browserAutomation: coordinator);
        commands.ObserveJarvisTranscript(text, CancellationToken.None);
        // Deliberately invalid section prevents a real Settings window opening
        // in the unit test. Even a local validation failure cannot authorize BA.
        var result = await commands.ExecuteJarvisToolAsync("open_keyboard_wtf_settings",
            JsonSerializer.SerializeToElement(new { section = "nonexistent-test-section" }), CancellationToken.None);
        Assert.False(coordinator.Snapshot().CanCancel);
        Assert.Contains("section does not exist", result["message"].ToString());
        var alternate = await commands.ExecuteJarvisToolAsync("browser_automation",
            JsonSerializer.SerializeToElement(new { request = text }), CancellationToken.None);
        Assert.Equal(true, alternate["duplicate_suppressed"]);
        Assert.False(coordinator.Snapshot().CanCancel);

        // A tool-only Live turnComplete can precede the model's final answer.
        // It must not free a later model-only website open to compete with the
        // local Settings receipt when no new user request was heard.
        commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        var lateOpen = await commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/" }), CancellationToken.None);
        Assert.Equal(true, lateOpen["duplicate_suppressed"]);
        Assert.Contains("section does not exist", lateOpen["message"].ToString());
        Assert.False(coordinator.Snapshot().CanCancel);

        commands.CompleteJarvisUserTurn(assistantAnswerComplete: true);
        Assert.Null(typeof(CommandRegistry).GetField("_browserPreRouteReceipt", flags)!.GetValue(commands));

        commands.ObserveJarvisTranscript("Now tell me the time.", CancellationToken.None);
        Assert.Null(typeof(CommandRegistry).GetField("_browserPreRouteReceipt", flags)!.GetValue(commands));
    }

    [Theory]
    [InlineData("open_url", "Separately, open https://example.org in a new tab. Leave the form task paused.")]
    [InlineData("open_url", "Could you also open https://example.org? Keep the form task on hold.")]
    [InlineData("browser_automation_status", "The project reference is https://example.org; what is the form status?")]
    public async Task A_selected_non_guidance_tool_cannot_inject_the_turn_into_a_paused_browser_task(string tool, string text)
    {
        using var bridge = new BrowserHelperBridge();
        var settings = new SettingsService(Path.Combine(_directory, "unused-settings.json"));
        using var planner = new BrowserAutomationPlanner();
        using var coordinator = new BrowserAutomationCoordinator(settings, new NotificationService(null), bridge, planner);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var runtimeType = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", flags)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(runtime, BrowserTaskState.NeedsUserAction);
        runtimeType.GetProperty("Intervention")!.SetValue(runtime, new BrowserUserIntervention
        {
            Missing = "Provide the project reference for the notes field.", CanSkip = true,
        });
        runtimeType.GetProperty("Page")!.SetValue(runtime, new BrowserPageSnapshot
        {
            TabId = 12, Url = "https://example.test/form", Controls =
            [
                new BrowserControl { Role = "link", AccessibleName = "HTML Form Instructions", Visible = true, Enabled = true },
                new BrowserControl { Id = "notes", FieldId = "notes", FieldLabel = "Project reference", Role = "textbox",
                    Visible = true, Enabled = true, FieldRequired = false },
            ],
        });
        typeof(BrowserAutomationCoordinator).GetField("_active", flags)!.SetValue(coordinator, runtime);
        var commands = new CommandRegistry(null, null, null, settings, new NotificationService(null), browserAutomation: coordinator);
        commands.ObserveJarvisTranscript(text, CancellationToken.None);
        await commands.ExecuteJarvisToolAsync(tool, JsonSerializer.SerializeToElement(new { url = "https://example.org" }), CancellationToken.None);
        Assert.Equal(BrowserTaskState.NeedsUserAction, coordinator.Snapshot().State);
        Assert.Equal(0, runtimeType.GetField("GuidanceRevision")!.GetValue(runtime));
        Assert.NotNull(coordinator.Snapshot().Intervention);
        // No automation service is supplied: the direct tool cannot cause any
        // native effect in this ownership test. Real Helper execution is replayed separately.
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly LearnedMappingService _mappings;
    private readonly JarvisActionHistoryService _history;
    private readonly JarvisAutomationService _automation;
    private readonly CommandRegistry _commands;
    private const string FixtureSourceContextId = "owned-fixture-document";
    private const string Request = "Always open https://example.org/workspace when I say open my workspace";

    [Fact]
    public void Unresolved_single_selected_url_proposal_belongs_to_link_capture_not_workflow_storage()
    {
        var proposed = JsonSerializer.SerializeToElement(new
        {
            trigger = "open my project",
            actions = new[] { new { kind = "url", target = "use_selected" } },
        });
        Assert.True(CommandRegistry.TryGetSelectedLinkAliasFromRoutineProposal(
            proposed, "Whenever I say open my project, open this selected link.", out var alias));
        Assert.Equal("open my project", alias);
        Assert.False(CommandRegistry.TryGetSelectedLinkAliasFromRoutineProposal(
            proposed, "Whenever I say open my project, open a website.", out _));

        var valid = JsonSerializer.SerializeToElement(new
        {
            trigger = "open my project",
            actions = new[] { new { kind = "url", target = "example.org/work?q=1&mode=full" } },
        });
        Assert.False(CommandRegistry.TryGetSelectedLinkAliasFromRoutineProposal(
            valid, "Whenever I say open my project, open this link.", out _));
        var compound = JsonSerializer.SerializeToElement(new
        {
            trigger = "open my project",
            actions = new[]
            {
                new { kind = "url", target = "use_selected" },
                new { kind = "app", target = "Notepad" },
            },
        });
        Assert.False(CommandRegistry.TryGetSelectedLinkAliasFromRoutineProposal(
            compound, "Whenever I say open my project, open this link and Notepad.", out _));
    }

    [Fact]
    public void Selected_web_destination_keeps_path_and_query_without_requiring_a_scheme()
    {
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["text"] = "example.org/software/project?ref_content=portfolio&ref_feature=in_progress",
        };
        Assert.Equal(
            "https://example.org/software/project?ref_content=portfolio&ref_feature=in_progress",
            CommandRegistry.TryGetSelectedHttpUrl(result));
        result["text"] = "use_selected";
        Assert.Equal("", CommandRegistry.TryGetSelectedHttpUrl(result));
    }

    public JarvisTranscriptOwnershipTests()
    {
        _mappings = new LearnedMappingService(Path.Combine(_directory, "mappings.json"));
        _history = new JarvisActionHistoryService(Path.Combine(_directory, "history.json"));
        var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        _automation = new JarvisAutomationService(null, settings, _history, _mappings, () => { });
        _commands = new CommandRegistry(null, null, null, settings, null, automation: _automation, history: _history,
            currentSelectionContextId: () => FixtureSourceContextId,
            captureSelectedText: _ => Task.FromResult(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["message"] = "No fixture selection supplied; physical input is not part of this test.",
            }));
    }

    private static Dictionary<string, object> FixtureSelectedText(string source)
    {
        var result = JarvisAutomationService.SelectionResult(source, fresh: true);
        result["source_context_id"] = FixtureSourceContextId;
        return result;
    }

    [Fact]
    public void A_new_user_turn_invalidates_the_previous_same_window_selection_fallback()
    {
        var field = typeof(JarvisAutomationService).GetField(
            "_lastSelection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(_automation, new SelectionSnapshot(
            "older selected text",
            DateTimeOffset.UtcNow,
            (IntPtr)42,
            "fixture",
            "Selection fixture"));

        _commands.ObserveJarvisTranscript(
            "Rewrite my newly selected text and copy the result.",
            CancellationToken.None);

        Assert.Null(field.GetValue(_automation));
    }

    [Fact]
    public async Task Session_end_releases_large_selection_from_existing_cache()
    {
        var field = typeof(JarvisAutomationService).GetField("_lastSelection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(_automation, new SelectionSnapshot(new string('x', 50000), DateTimeOffset.UtcNow,
            (IntPtr)42, "fixture", "Selection fixture"));
        await _commands.HandleJarvisSessionEndingAsync();
        Assert.Null(field.GetValue(_automation));
    }

    [Fact]
    public void Observing_a_durable_request_does_not_save_or_act_before_a_model_tool_call()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        Assert.Empty(_mappings.Snapshot());
        Assert.False(File.Exists(_mappings.MappingPath));
        Assert.False(File.Exists(Path.Combine(_directory, "history.json")));
    }

    [Fact]
    public async Task The_authorized_tool_returns_the_single_verified_local_mapping_result()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        var first = await _commands.ExecuteJarvisToolAsync("remember_link_alias",
            JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), CancellationToken.None);
        Assert.Equal(true, first["persistence_verified"]);
        Assert.Single(_mappings.Snapshot());
        Assert.Equal("https://example.org/workspace", _mappings.Snapshot()[0].Target);
        var repeated = await _commands.ExecuteJarvisToolAsync("remember_link_alias",
            JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), CancellationToken.None);
        Assert.Equal(true, repeated["duplicate_suppressed"]);
        Assert.Equal(first["message"], repeated["message"]);
        Assert.Equal(first["persistence_verified"], repeated["persistence_verified"]);
    }

    [Fact]
    public async Task Cancelled_tool_cannot_execute_the_observed_durable_request()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _commands.ExecuteJarvisToolAsync(
            "remember_link_alias", JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), new CancellationToken(true)));
        Assert.Empty(_mappings.Snapshot());
    }

    [Fact]
    public async Task A_new_conversational_turn_cannot_execute_an_older_observed_request()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        _commands.ObserveJarvisTranscript("What mappings have I saved?", CancellationToken.None);
        await _commands.ExecuteJarvisToolAsync("list_learned_mappings", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Empty(_mappings.Snapshot());
    }

    [Fact]
    public async Task A_completed_non_action_response_cannot_be_executed_by_a_later_tool_without_input()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        _commands.CompleteJarvisUserTurn();
        await _commands.ExecuteJarvisToolAsync("list_learned_mappings", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Empty(_mappings.Snapshot());
    }

    [Theory]
    [InlineData("browser_action")]
    [InlineData("remember_link_alias")]
    public void Tool_only_settings_hold_does_not_extend_other_ordered_action_receipts(string tool)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var receiptField = typeof(CommandRegistry).GetField("_browserPreRouteReceipt", flags)!;
        receiptField.SetValue(_commands, new BrowserPreRouteReceipt
        {
            UserTurn = "Do the requested actions in order.",
            ToolName = tool,
            Ok = true,
            Authoritative = true,
            Result = new Dictionary<string, object> { ["ok"] = true, ["message"] = "Done." },
        });

        _commands.CompleteJarvisUserTurn();

        Assert.Null(receiptField.GetValue(_commands));
    }

    [Fact]
    public async Task Older_response_completion_cannot_erase_a_new_immediate_browser_cancel_receipt()
    {
        var coordinator = new BrowserAutomationCoordinator(null, null, null, null);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var runtimeType = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", flags)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Executing);
        typeof(BrowserAutomationCoordinator).GetField("_active", flags)!.SetValue(coordinator, runtime);
        var commands = new CommandRegistry(null, null, null, null, null, browserAutomation: coordinator);
        commands.ObserveSubmittedJarvisText("Cancel BA.", CancellationToken.None);
        Assert.Equal(BrowserTaskState.Cancelled, coordinator.Snapshot().State);

        var receiptField = typeof(CommandRegistry).GetField("_browserPreRouteReceipt", flags)!;
        var receipt = Assert.IsType<BrowserPreRouteReceipt>(receiptField.GetValue(commands));
        Assert.False(receipt.IsAcknowledged);
        commands.CompleteJarvisUserTurn(); // Old response's turnComplete, after the new cancel.
        Assert.Same(receipt, receiptField.GetValue(commands));

        var result = await commands.ExecuteJarvisToolAsync(
            "browser_automation_control",
            JsonSerializer.SerializeToElement(new { action = "cancel" }),
            CancellationToken.None);
        Assert.Equal(true, result["ok"]);
        Assert.Contains("cancelled", result["message"].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(receipt.IsAcknowledged);
        commands.CompleteJarvisUserTurn();
        Assert.Null(receiptField.GetValue(commands));

        ((CancellationTokenSource)runtimeType.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Fact]
    public async Task Rapid_followup_after_immediate_cancel_reports_the_terminal_task_without_authorizing_a_new_cancel()
    {
        var coordinator = new BrowserAutomationCoordinator(null, null, null, null);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var runtimeType = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", flags)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Executing);
        runtimeType.GetProperty("TaskId")!.SetValue(runtime, "test-cancel-task");
        typeof(BrowserAutomationCoordinator).GetField("_active", flags)!.SetValue(coordinator, runtime);
        var commands = new CommandRegistry(null, null, null, null, null, browserAutomation: coordinator);
        commands.ObserveSubmittedJarvisText("Cancel BA.", CancellationToken.None);
        Assert.Equal(BrowserTaskState.Cancelled, coordinator.Snapshot().State);

        // A new utterance can arrive before the provider publishes its older
        // cancel tool call. It must not grant authority over a future task,
        // but the result must agree with the already-terminal coordinator.
        commands.ObserveSubmittedJarvisText("What time is it?", CancellationToken.None);
        commands.CompleteJarvisUserTurn();
        var result = await commands.ExecuteJarvisToolAsync(
            "browser_automation_control",
            JsonSerializer.SerializeToElement(new { action = "cancel" }),
            CancellationToken.None);
        Assert.True(result["ok"] is true, JsonSerializer.Serialize(result));
        Assert.Equal(true, result["already_cancelled"]);
        Assert.Equal(BrowserTaskState.Cancelled, coordinator.Snapshot().State);

        // Once another task owns the coordinator, a delayed cancel from the
        // earlier response may neither act on nor claim the newer task.
        var nextRuntime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(nextRuntime, BrowserTaskState.Executing);
        runtimeType.GetProperty("TaskId")!.SetValue(nextRuntime, "next-task");
        typeof(BrowserAutomationCoordinator).GetField("_active", flags)!.SetValue(coordinator, nextRuntime);
        var staleResult = await commands.ExecuteJarvisToolAsync(
            "browser_automation_control",
            JsonSerializer.SerializeToElement(new { action = "cancel" }),
            CancellationToken.None);
        Assert.False(staleResult["ok"] is true, JsonSerializer.Serialize(staleResult));
        Assert.Equal(BrowserTaskState.Executing, coordinator.Snapshot().State);

        var now = DateTime.UtcNow;
        Assert.False(CommandRegistry.CanReportAlreadyCancelled("cancel",
            new BrowserAutomationSnapshot
            {
                TaskId = "new-active-task", State = BrowserTaskState.Executing,
                CanCancel = true, UpdatedUtc = now,
            }, now));
        Assert.False(CommandRegistry.CanReportAlreadyCancelled("pause",
            new BrowserAutomationSnapshot
            {
                TaskId = "old-task", State = BrowserTaskState.Cancelled,
                CanCancel = false, UpdatedUtc = now,
            }, now));
        Assert.False(CommandRegistry.CanReportAlreadyCancelled("cancel",
            new BrowserAutomationSnapshot
            {
                TaskId = "old-task", State = BrowserTaskState.Cancelled,
                CanCancel = false, UpdatedUtc = now.AddSeconds(-13),
            }, now));

        ((CancellationTokenSource)runtimeType.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
        ((CancellationTokenSource)runtimeType.GetProperty("Cancellation")!.GetValue(nextRuntime)!).Dispose();
    }

    [Fact]
    public async Task Browser_status_does_not_present_a_prior_sessions_cancelled_task_as_current()
    {
        using var bridge = new BrowserHelperBridge();
        var coordinator = new BrowserAutomationCoordinator(null, null, bridge, null);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var runtimeType = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", flags)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Executing);
        runtimeType.GetProperty("TaskId")!.SetValue(runtime, "prior-session-task");
        typeof(BrowserAutomationCoordinator).GetField("_active", flags)!.SetValue(coordinator, runtime);
        Assert.True(coordinator.RequestCancel(announceOutcome: false));
        var commands = new CommandRegistry(null, null, null, null, null, browserAutomation: coordinator);
        var currentTerminal = await commands.ExecuteJarvisToolAsync(
            "browser_automation_status", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Equal("cancelled", currentTerminal["state"]);

        coordinator.ClearRecentTerminalOutcomes(); // The previous Jarvis session ended.
        var nextSession = await commands.ExecuteJarvisToolAsync(
            "browser_automation_status", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Equal("idle", nextSession["state"]);
        Assert.Equal("", nextSession["task_id"]);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<object>>(nextSession["recent_outcomes"]));

        var nextRuntime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(nextRuntime, BrowserTaskState.Executing);
        runtimeType.GetProperty("TaskId")!.SetValue(nextRuntime, "current-session-task");
        typeof(BrowserAutomationCoordinator).GetField("_active", flags)!.SetValue(coordinator, nextRuntime);
        var active = await commands.ExecuteJarvisToolAsync(
            "browser_automation_status", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Equal("running", active["state"]);
        Assert.Equal("current-session-task", active["task_id"]);

        ((CancellationTokenSource)runtimeType.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
        ((CancellationTokenSource)runtimeType.GetProperty("Cancellation")!.GetValue(nextRuntime)!).Dispose();
    }

    [Fact]
    public void A_later_transcription_fragment_cannot_erase_a_completed_direct_open_in_the_same_turn()
    {
        var field = typeof(CommandRegistry).GetField(
            "_completedDirectOpenForCurrentTurn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _commands.ObserveJarvisTranscript("Open the calendar", CancellationToken.None);
        field.SetValue(_commands, true); // A successful direct-tool receipt.

        _commands.ObserveJarvisTranscript(
            "Open the calendar then click the Reports tab", CancellationToken.None);
        Assert.True((bool)field.GetValue(_commands)!);

        _commands.CompleteJarvisUserTurn();
        Assert.False((bool)field.GetValue(_commands)!);
    }

    [Fact]
    public async Task Browser_tool_cannot_replay_a_whole_turn_after_an_ordered_direct_open()
    {
        var coordinator = new BrowserAutomationCoordinator(null, null, null, null);
        var commands = new CommandRegistry(null, null, null, null, null,
            browserAutomation: coordinator);
        const string request = "Open the saved project portal, then click the Reports tab.";
        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        typeof(CommandRegistry).GetField(
            "_completedDirectOpenForCurrentTurn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(commands, true); // Simulated successful direct-open receipt.

        var rejected = await commands.ExecuteJarvisToolAsync("browser_automation",
            JsonSerializer.SerializeToElement(new { task = request }), CancellationToken.None);

        Assert.False((bool)rejected["ok"]);
        Assert.Contains("earlier direct open already completed", rejected["message"].ToString());
        Assert.False(coordinator.Snapshot().CanCancel);
    }

    [Theory]
    [InlineData("copy_text")]
    [InlineData("replace_selected_text")]
    public async Task Current_selected_context_rejects_an_instruction_echo_before_output_execution(string toolName)
    {
        const string request =
            "I've selected some text. I need you to polish the English and copy the revised version to my clipboard, keeping the original meaning intact.";
        const string selection =
            "Yesterday the engineering team review the incident and they decides that the cache policy need clearer ownership. The change reduce duplicate work while keeping every customer-visible behavior the same.";
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);

        typeof(CommandRegistry).GetMethod(
                "RecordCapturedBrowserContext",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands,
            [
                "get_selected_text",
                new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["text"] = selection,
                    ["fresh"] = true,
                },
            ]);

        var result = await _commands.ExecuteJarvisToolAsync(
            toolName,
            JsonSerializer.SerializeToElement(new { text = request }),
            CancellationToken.None);

        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["clipboard_unchanged"]);
        Assert.Equal(true, result["selection_unchanged"]);
        Assert.Equal(true, result["selected_context_result_required"]);
        Assert.Contains("selected content", result["message"].ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Earlier_selection_is_available_only_for_an_explicit_same_session_follow_up()
    {
        Assert.False(CommandRegistry.ReferencesEarlierSelection("Analyze the text I selected now"));
        Assert.False(CommandRegistry.ReferencesEarlierSelection("Select text before copying it"));
        Assert.True(CommandRegistry.ReferencesEarlierSelection("Remember the text I selected earlier?"));
        Assert.True(CommandRegistry.ReferencesEarlierSelection("What about the previous selection?"));
        _commands.ObserveJarvisTranscript("Analyze this selected paragraph", CancellationToken.None);
        typeof(CommandRegistry).GetMethod(
                "RecordCapturedBrowserContext",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands,
            [
                "get_selected_text",
                new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["text"] = "The blue lamp is beside the desk.",
                    ["fresh"] = true,
                },
            ]);

        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Tell me more about that text I selected earlier", CancellationToken.None);
        var recalled = await _commands.ExecuteJarvisToolAsync(
            "get_selected_text", JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal(true, recalled["ok"]);
        Assert.Equal("The blue lamp is beside the desk.", recalled["text"]);
        Assert.Equal("recent_selection", recalled["source"]);

        _commands.ObserveJarvisTranscript("Analyze the selected text now", CancellationToken.None);
        var currentCaptures = 0;
        var currentRequest = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            currentCaptures++;
            return Task.FromResult(new Dictionary<string, object> { ["ok"] = false });
        }, CancellationToken.None);
        Assert.Equal(1, currentCaptures);
        Assert.Equal(false, currentRequest["ok"]);

        _commands.ObserveJarvisTranscript("Tell me more about what I selected earlier", CancellationToken.None);
        typeof(CommandRegistry).GetField(
                "_capturedBrowserContextUtc",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_commands, DateTime.UtcNow - TimeSpan.FromMinutes(6));
        var retained = await _commands.ExecuteJarvisToolAsync(
            "get_selected_text", JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal(true, retained["ok"]); // BA's excerpt expiry is not selection-source expiry.
        Assert.Equal("The blue lamp is beside the desk.", retained["text"]);

        await _commands.HandleJarvisSessionEndingAsync();
        Assert.Equal("", typeof(CommandRegistry).GetField(
                "_capturedBrowserContextText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_commands));
        Assert.Equal("", typeof(CommandRegistry).GetField("_originalSelectedText",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_commands));
    }

    [Theory]
    [InlineData(6001)]
    [InlineData(32808)]
    [InlineData(50000)]
    [InlineData(50001)]
    [InlineData(250000)]
    public async Task Original_selection_survives_analysis_and_clipboard_context_without_a_partial_prefix(int length)
    {
        var source = "BEGIN\r\n" + new string('x', length - 21) + "\r\nMIDDLE\r\nEND!";
        Assert.Equal(length, source.Length);
        var record = typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _commands.ObserveJarvisTranscript("Analyze this selected text", CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", FixtureSelectedText(source)]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Tell me about the judges", CancellationToken.None);
        record.Invoke(_commands, ["get_clipboard_text", new Dictionary<string, object>
        {
            ["ok"] = true, ["text"] = "An unrelated copied summary", ["source"] = "clipboard_text",
        }]);
        _commands.CompleteJarvisUserTurn();
        // This fixture has no Live/translation owner. Exercise source recall;
        // owned translate-and-copy is covered by SelectedTranslationDispatchBoundaryTests.
        _commands.ObserveJarvisTranscript("Analyze the entire original selection", CancellationToken.None);
        var recalled = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal(true, recalled["ok"]);
        Assert.Equal(source, recalled["text"]);
        Assert.Equal(false, recalled["truncated"]);
        await _commands.HandleJarvisSessionEndingAsync();
        _commands.ObserveJarvisTranscript("Translate the earlier selection", CancellationToken.None);
        var cleared = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal(false, cleared["ok"]);
    }

    [Theory]
    [InlineData("Could you please translate it into French?")]
    [InlineData("Now translate that into French.")]
    public async Task Polite_original_selection_follow_up_reads_the_same_full_snapshot(string request)
    {
        _commands.ObserveJarvisTranscript("Analyze this selection", CancellationToken.None);
        var source = new string('x', 21000) + "END";
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands, ["get_selected_text", FixtureSelectedText(source)]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var result = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal(source, result["text"]);
    }

    [Theory]
    [InlineData("Translate the selection to French and copy it to clipboard.", true)]
    [InlineData("Translate the selection to French and copy it to clipboard.", false)]
    [InlineData("Could you please translate the entire selected text into French?", true)]
    [InlineData("Now rewrite my highlighted text clearly.", false)]
    [InlineData("Translate the selection to French and copy it to clipboard now.", true)]
    public async Task Plain_selection_transformation_captures_the_current_full_source_even_with_use_recent(string request, bool useRecent)
    {
        // A literal selection request must capture current source B. The fake
        // keeps this unit test independent of Windows focus and clipboard.
        var captures = 0;
        const int length = 32808;
        var source = "BEGIN\r\n" + new string('x', length - 21) + "\r\nMIDDLE\r\nEND!";
        var commands = new CommandRegistry(null, null, null, new SettingsService(Path.Combine(_directory, "settings.json")), null,
            automation: _automation,
            history: _history,
            currentSelectionContextId: () => FixtureSourceContextId,
            captureSelectedText: _ =>
            {
                captures++;
                return Task.FromResult(FixtureSelectedText(source));
            });
        var record = typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        commands.ObserveJarvisTranscript("Analyze this selected text", CancellationToken.None);
        record.Invoke(commands, ["get_selected_text", FixtureSelectedText("A different previous source")]);
        commands.CompleteJarvisUserTurn();
        commands.ObserveJarvisTranscript("Who are the judges?", CancellationToken.None);
        record.Invoke(commands, ["get_clipboard_text", new Dictionary<string, object>
        {
            ["ok"] = true, ["text"] = "Unrelated clipboard content and a generated summary",
        }]);
        commands.CompleteJarvisUserTurn();

        commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var result = await commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = useRecent }), CancellationToken.None);

        if (SelectedTextTranslationOperation.TryParseRequest(request, true, out _)
            || SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(request, out _, out _))
        {
            // No bound Live session exists here. It must not fall through to
            // capture/copy; the real owned fixture verifies complete sources.
            Assert.Equal(false, result["ok"]);
            Assert.Equal(true, result["clipboard_unchanged"]);
            Assert.Equal(0, captures);
            return;
        }
        Assert.True(result["ok"] is true, result.GetValueOrDefault("message")?.ToString());
        Assert.Equal(1, captures);
        Assert.Equal(source, result["text"]);
        Assert.Equal("current_selection", result["source"]);
        Assert.Equal(false, result["truncated"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_incomplete_selection_recapture_does_not_destroy_the_original(bool incomplete)
    {
        const string source = "Original source before the failed recapture.";
        var record = typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _commands.ObserveJarvisTranscript("Analyze this selection", CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", FixtureSelectedText(source)]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Read my current selection now", CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", new Dictionary<string, object>
        {
            ["ok"] = false, ["text"] = "", ["truncated"] = incomplete,
        }]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate the original selection to French", CancellationToken.None);

        var result = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);

        Assert.Equal(true, result["ok"]);
        Assert.Equal(source, result["text"]);
    }

    [Theory]
    [InlineData("Translate the fresh selection to French.")]
    [InlineData("Rewrite my newly selected text clearly.")]
    [InlineData("Translate a different selection to French.")]
    [InlineData("Translate the current selection to French.")]
    [InlineData("Analyze the selected text now.")]
    public async Task Explicit_new_selection_captures_the_current_source_instead_of_the_previous_source(string request)
    {
        _commands.ObserveJarvisTranscript("Analyze this selection", CancellationToken.None);
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands, ["get_selected_text", FixtureSelectedText("Previous source")]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);

        // Supersedes rejection-only coverage: explicit fresh intent must
        // capture the requested source even if the model hints use_recent.
        // Safety means never returning the previous source, not refusing B.
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult("Current foreground source", true));
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal("Current foreground source", result["text"]);
        Assert.Equal("current_selection", result["source"]);
    }

    [Fact]
    public async Task Failed_new_selection_does_not_authorize_an_implicit_follow_up_to_use_the_old_source()
    {
        var record = typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _commands.ObserveJarvisTranscript("Analyze this selection", CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", FixtureSelectedText("Previous source")]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Analyze the new selection", CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", new Dictionary<string, object> { ["ok"] = false }]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate it to French", CancellationToken.None);

        var result = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);

        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));
    }

    [Fact]
    public async Task Complete_new_selection_replaces_the_source_and_restores_follow_up_authority()
    {
        var record = typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _commands.ObserveJarvisTranscript("Analyze this selection", CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", FixtureSelectedText("Previous source")]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Analyze my new selection", CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", FixtureSelectedText("Current complete source")]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate it to French", CancellationToken.None);

        var result = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);

        Assert.Equal(true, result["ok"]);
        Assert.Equal("Current complete source", result["text"]);
    }

    [Theory]
    [InlineData("Analyze my new selection")]
    [InlineData("Analyze my new selection please")]
    public async Task Same_open_turn_fresh_selection_echo_preserves_the_successful_capture(string echo)
    {
        _commands.ObserveJarvisTranscript("Analyze my new selection", CancellationToken.None);
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands, ["get_selected_text", FixtureSelectedText("Fresh complete source")]);

        _commands.ObserveJarvisTranscript(echo, CancellationToken.None);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate it to French", CancellationToken.None);
        var result = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);

        Assert.True(result["ok"] is true, result.GetValueOrDefault("message")?.ToString());
        Assert.Equal("Fresh complete source", result["text"]);
    }

    [Fact]
    public async Task A_later_fresh_selection_turn_revokes_implicit_authority_even_when_the_words_repeat()
    {
        var record = typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        const string request = "Analyze my new selection";
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", FixtureSelectedText("Previous complete source")]);
        _commands.CompleteJarvisUserTurn();

        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        record.Invoke(_commands, ["get_selected_text", new Dictionary<string, object> { ["ok"] = false }]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate it to French", CancellationToken.None);
        var result = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);

        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Read the original selection", CancellationToken.None);
        var original = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal("Previous complete source", original["text"]);
    }

    [Theory]
    [InlineData("Translate the selected text to French.")]
    [InlineData("Translate my current selection to French.")]
    public async Task Missing_original_for_a_current_selection_transformation_recaptures_once(string request)
    {
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult("Current foreground selection", true));
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal("Current foreground selection", result["text"]);
        Assert.Equal("current_selection", result["source"]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate the original selection to French", CancellationToken.None);
        var retained = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal("Current foreground selection", retained["text"]);
    }

    [Theory]
    [InlineData("Translate the original selection to French.")]
    [InlineData("Translate the text I selected earlier to French.")]
    public async Task Missing_retrospective_source_never_substitutes_a_current_selection_or_clipboard(string request)
    {
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult("Unrelated current text", true));
        }, CancellationToken.None);

        Assert.Equal(0, captures);
        Assert.Equal(false, result["ok"]);
        Assert.False(result.ContainsKey("text"));
    }

    [Theory]
    [InlineData("Analyze this selection.")]
    [InlineData("Can you please analyze this selected text?")]
    [InlineData("Analyse this highlighted text.")]
    public async Task Deictic_analysis_captures_the_new_foreground_source_instead_of_the_retained_source(string request)
    {
        _commands.ObserveJarvisTranscript("Analyze this selected text", CancellationToken.None);
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands, ["get_selected_text", FixtureSelectedText("Source A")]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult("Source B selected now", true));
        }, CancellationToken.None);

        Assert.Equal(1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal("Source B selected now", result["text"]);
        Assert.Equal("current_selection", result["source"]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Translate the original selection to French", CancellationToken.None);
        var followUp = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal("Source B selected now", followUp["text"]);
    }

    [Fact]
    public async Task Failed_deictic_analysis_capture_never_returns_A_but_keeps_explicit_original_recall()
    {
        _commands.ObserveJarvisTranscript("Analyze this selected text", CancellationToken.None);
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands, ["get_selected_text", FixtureSelectedText("Source A")]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Analyze this selection", CancellationToken.None);
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
        _commands.ObserveJarvisTranscript("Read the original selection", CancellationToken.None);
        var original = await _commands.ExecuteJarvisToolAsync("get_selected_text",
            JsonSerializer.SerializeToElement(new { use_recent = true }), CancellationToken.None);
        Assert.Equal("Source A", original["text"]);
    }

    [Theory]
    [InlineData("Analyze it.", false)]
    [InlineData("Analyze the original selection.", true)]
    [InlineData("Analyse the earlier selection.", true)]
    [InlineData("Translate it to French.", false)]
    public async Task Follow_ups_prefer_current_selection_except_explicit_original_recall(string request, bool earlier)
    {
        _commands.ObserveJarvisTranscript("Analyze this selected text", CancellationToken.None);
        typeof(CommandRegistry).GetMethod("RecordCapturedBrowserContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands, ["get_selected_text", FixtureSelectedText("Source A")]);
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var captures = 0;
        var result = await _commands.GetRecentSelectedTextOrRecaptureAsync(_ =>
        {
            captures++;
            return Task.FromResult(JarvisAutomationService.SelectionResult("Unrelated source B", true));
        }, CancellationToken.None);

        Assert.Equal(earlier ? 0 : 1, captures);
        Assert.Equal(true, result["ok"]);
        Assert.Equal(earlier ? "Source A" : "Unrelated source B", result["text"]);
        Assert.Equal(earlier ? "recent_selection" : "current_selection", result["source"]);
    }

    [Fact]
    public async Task Translation_start_turn_completion_cannot_authorize_a_second_clipboard_publication()
    {
        const string request = "Translate the selection to French and copy it to clipboard.";
        using var operation = new SelectedTextTranslationOperation(request, "French", CancellationToken.None);
        var copies = 0;
        operation.Start("source", (_, _, _) => Task.FromResult("translated result"),
            _ => { copies++; return new() { ["ok"] = true, ["clipboard_verified"] = true }; },
            _ => Task.CompletedTask);
        await operation.Completion;
        typeof(CommandRegistry).GetField("_selectedTranslation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(_commands, operation);
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        _commands.CompleteJarvisUserTurn();
        var late = await _commands.ExecuteJarvisToolAsync("copy_text",
            JsonSerializer.SerializeToElement(new { text = "competing provider result" }), CancellationToken.None);
        Assert.Equal(true, late["duplicate_suppressed"]);
        Assert.Equal(operation.Id, late["operation_id"]);
        Assert.Equal(true, late["clipboard_verified"]);
        Assert.Equal(1, copies);
    }

    [Fact]
    public async Task Translation_cancel_receipt_survives_an_older_turn_complete_and_transcription_echo()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        typeof(CommandRegistry).GetField("_selectedTranslation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(_commands, operation);
        _commands.ObserveImmediateJarvisControl("Cancel the translation.", CancellationToken.None)!.Invoke();
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript("Cancel the translation.", CancellationToken.None);
        var result = await _commands.ExecuteJarvisToolAsync("cancel_sensitive_action",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Contains("Translation cancelled", result["message"].ToString());
        Assert.True(operation.IsCancelled);
        await _commands.HandleJarvisSessionEndingAsync();
    }

    [Fact]
    public void Explicitly_deferred_selection_reaches_matching_later_browser_task_without_a_model_hint()
    {
        const string deferred = "Use selected information to fill the form when I tell you to.";
        const string source = "Operating system: macOS. Team size: 4.";
        const string later = "All right, can you please fill the form now?";
        _commands.ObserveJarvisTranscript(deferred, CancellationToken.None);
        RecordSelectedBrowserContext(source);
        _commands.CompleteJarvisUserTurn();

        _commands.ObserveJarvisTranscript(later, CancellationToken.None);
        var (attached, task) = AttachCapturedBrowserContext(later);

        Assert.True(attached);
        Assert.Contains(source, task, StringComparison.Ordinal);
        Assert.Contains("AUTHORITATIVE USER-PROVIDED SELECTED TEXT", task, StringComparison.Ordinal);
    }

    [Fact]
    public void Deferred_selection_is_not_attached_to_a_different_later_browser_action()
    {
        const string deferred = "Use selected information to fill the form when I tell you to.";
        const string unrelated = "Please fill a different account form now.";
        _commands.ObserveJarvisTranscript(deferred, CancellationToken.None);
        RecordSelectedBrowserContext("Private selected information.");
        _commands.CompleteJarvisUserTurn();

        _commands.ObserveJarvisTranscript(unrelated, CancellationToken.None);
        var (attached, task) = AttachCapturedBrowserContext(unrelated);

        Assert.False(attached);
        Assert.Equal(unrelated, task);
    }

    [Fact]
    public void A_later_cumulative_transcript_can_authorize_its_open_turn_capture_but_a_new_turn_cannot()
    {
        const string prefix = "Use selected information to fill the form";
        const string complete = "Use selected information to fill the form when I tell you to";
        var authorizedField = typeof(CommandRegistry).GetField(
            "_capturedBrowserContextAuthorizedForLaterAction",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        _commands.ObserveJarvisTranscript(prefix, CancellationToken.None);
        RecordSelectedBrowserContext("Source details.");
        Assert.Equal(false, authorizedField.GetValue(_commands));
        _commands.ObserveJarvisTranscript(complete, CancellationToken.None);
        Assert.Equal(true, authorizedField.GetValue(_commands));

        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(prefix, CancellationToken.None);
        RecordSelectedBrowserContext("Different source.");
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(complete, CancellationToken.None);
        Assert.Equal(false, authorizedField.GetValue(_commands));
    }

    [Fact]
    public async Task A_deferred_selection_proposed_as_a_note_remains_ephemeral_and_is_reused()
    {
        const string deferred = "Use selected information to fill the form when I tell you to.";
        _commands.ObserveJarvisTranscript(deferred, CancellationToken.None);
        RecordSelectedBrowserContext("Nonpersistent source details.");

        var method = typeof(CommandRegistry).GetMethod(
            "RetainDeferredBrowserSourceInsteadOfSavingNoteAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = (Task<Dictionary<string, object>>)method.Invoke(
            _commands, [deferred, CancellationToken.None])!;
        var result = await pending;

        Assert.Equal(true, result["ok"]);
        Assert.Equal(true, result["retained_temporarily"]);
        Assert.Equal(false, result["durable_saved"]);
        Assert.Contains("No local note was saved", result["message"].ToString());
        Assert.Equal("Nonpersistent source details.",
            typeof(CommandRegistry).GetField(
                    "_capturedBrowserContextText",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(_commands));
    }

    private void RecordSelectedBrowserContext(string text) =>
        typeof(CommandRegistry).GetMethod(
                "RecordCapturedBrowserContext",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands,
            [
                "get_selected_text",
                new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["text"] = text,
                    ["fresh"] = true,
                },
            ]);

    [Theory]
    [InlineData("open_url")]
    [InlineData("open_app")]
    [InlineData("open_service_page")]
    public async Task A_selected_website_open_cannot_reuse_the_previous_turns_destination(string tool)
    {
        const string request = "Open this selected website.";
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        RecordSelectedBrowserContext("example.org/old");
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);

        var result = await _commands.ExecuteJarvisToolAsync(tool,
            JsonSerializer.SerializeToElement(new { url = "https://example.org/old", app_name = "example.org/old", service = "example.org/old" }),
            CancellationToken.None);

        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["current_selection_required"]);
        Assert.Empty(_history.Snapshot());
    }

    [Fact]
    public async Task Invalid_current_selected_domain_cannot_authorize_an_older_valid_URL()
    {
        _commands.ObserveJarvisTranscript("Open the highlighted website.", CancellationToken.None);
        RecordSelectedBrowserContext("this is ordinary prose, not a website");
        var result = await _commands.ExecuteJarvisToolAsync("open_url",
            JsonSerializer.SerializeToElement(new { url = "https://example.org/old" }), CancellationToken.None);
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["invalid_selected_destination"]);
        Assert.Empty(_history.Snapshot());
    }

    [Theory]
    [InlineData("Open this selected website.", "keyboardwtf.com/path?q=1#end", "https://keyboardwtf.com/path?q=1#end")]
    [InlineData("Open this.", "www.example.org", "https://www.example.org/")]
    [InlineData("Please open that", "http://example.org/path", "http://example.org/path")]
    public void A_current_selected_domain_is_the_canonical_action_owner(string request, string selection, string expected)
    {
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        RecordSelectedBrowserContext(selection);
        Assert.True(_commands.TryResolveCurrentSelectedWebDestination(request, out var url, out var rejection));
        Assert.Null(rejection);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("Open that.")]
    [InlineData("Open Gmail.")]
    [InlineData("Close that.")]
    [InlineData("Open the previous selected website.")]
    [InlineData("Open Gmail then read the selected text.")]
    public void Selected_web_guard_does_not_replace_ordinary_or_explicit_earlier_referents(string request)
    {
        _commands.ObserveJarvisTranscript("Read my selected text.", CancellationToken.None);
        RecordSelectedBrowserContext("example.org/old");
        _commands.CompleteJarvisUserTurn();
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        Assert.False(_commands.TryResolveCurrentSelectedWebDestination(request, out _, out _));
    }

    private (bool Attached, string Task) AttachCapturedBrowserContext(string request)
    {
        object[] arguments = [request, false, null];
        var attached = (bool)typeof(CommandRegistry).GetMethod(
                "TryAttachCapturedBrowserContext",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands, arguments)!;
        return (attached, (string)arguments[2]!);
    }

    public void Dispose()
    {
        _automation.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Link_follow_up_context_uses_the_verified_destination_not_the_model_alias()
    {
        var result = new Dictionary<string, object> { ["ok"] = true, ["url"] = "https://example.org/actual" };
        JarvisAutomationService.AttachReferentialTarget("open_url",
            JsonSerializer.SerializeToElement(new { url = "my workspace" }), result);
        var target = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("https://example.org/actual", target["target"]);
    }

    [Fact]
    public async Task An_owned_result_cannot_expire_and_authorize_an_alternate_tool_in_the_same_request()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        await _commands.ExecuteJarvisToolAsync("remember_link_alias",
            JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), CancellationToken.None);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var receipt = Assert.IsType<BrowserPreRouteReceipt>(
            typeof(CommandRegistry).GetField("_browserPreRouteReceipt", flags)!.GetValue(_commands));
        receipt.GetType().GetField("<CreatedUtc>k__BackingField", flags)!.SetValue(receipt, DateTime.UtcNow.AddMinutes(-2));
        var late = await _commands.ExecuteJarvisToolAsync("unavailable_tool", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Equal(true, late["duplicate_suppressed"]);
        Assert.Equal(receipt.Message, late["message"]);
        _commands.ObserveJarvisTranscript("What mappings have I saved?", CancellationToken.None);
        var fresh = await _commands.ExecuteJarvisToolAsync("list_learned_mappings", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.False(fresh.ContainsKey("duplicate_suppressed"));
    }
}
