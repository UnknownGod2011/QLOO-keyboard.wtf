namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

public sealed class JarvisShortcutTests
{
    [Fact]
    public void Native_focus_resolution_uses_app_identity_and_honors_exact_window_ids()
    {
        var windows = new[]
        {
            new NativeWindowCloser.Window((IntPtr)11, 1, 1, "Chrome_WidgetWin_1", "chrome", "Microsoft Edge help"),
            new NativeWindowCloser.Window((IntPtr)12, 2, 2, "Chrome_WidgetWin_1", "msedge", "Dashboard"),
            new NativeWindowCloser.Window((IntPtr)13, 2, 2, "Chrome_WidgetWin_1", "msedge", "Another dashboard"),
            new NativeWindowCloser.Window((IntPtr)14, 3, 3, "Notepad", "notepad", "Microsoft Edge"),
        };

        Assert.Equal((IntPtr)12, JarvisAutomationService.ResolveNativeWindowTarget(windows, "Microsoft Edge", null));
        Assert.Equal((IntPtr)11, JarvisAutomationService.ResolveNativeWindowTarget(windows, "Google Chrome", null));
        Assert.Equal((IntPtr)13, JarvisAutomationService.ResolveNativeWindowTarget(windows, "Microsoft Edge", windows[2].Id));
        Assert.Equal(IntPtr.Zero, JarvisAutomationService.ResolveNativeWindowTarget(windows, "Microsoft Edge", windows[0].Id));
        Assert.Equal(IntPtr.Zero, JarvisAutomationService.ResolveNativeWindowTarget(windows, "Microsoft Edge", windows[3].Id));
        Assert.Equal(IntPtr.Zero, JarvisAutomationService.ResolveNativeWindowTarget([windows[3]], "Microsoft Edge", null));
        Assert.Equal((IntPtr)11, JarvisAutomationService.ResolveNativeWindowTarget(windows, windows[0].Title, windows[0].Id));
        Assert.Equal(IntPtr.Zero, JarvisAutomationService.ResolveNativeWindowTarget(windows, "Another dashboard", windows[0].Id));
        Assert.Equal(IntPtr.Zero, JarvisAutomationService.ResolveNativeWindowTarget(windows, "Missing app", null));
        Assert.True(NativeWindowCloser.IsKnownAppIdentity("Microsoft Edge"));
        Assert.True(NativeWindowCloser.IsKnownAppIdentity("Chrome"));
        Assert.False(NativeWindowCloser.IsKnownAppIdentity("Microsoft Edge help"));
    }

    [Fact]
    public void Focus_receipt_waits_briefly_for_exact_activation_but_never_accepts_another_window()
    {
        var requested = new IntPtr(17);
        var other = new IntPtr(23);
        var reads = 0;
        Assert.True(JarvisAutomationService.WaitForForegroundWindow(
            requested,
            () => ++reads < 3 ? other : requested,
            TimeSpan.FromMilliseconds(250)));
        Assert.True(reads >= 3);
        Assert.False(JarvisAutomationService.WaitForForegroundWindow(
            requested, () => other, TimeSpan.FromMilliseconds(10)));
        Assert.False(JarvisAutomationService.IsEquivalentForegroundProcessRequest(
            "chrome", "chrome", "chrome", requested.ToInt64().ToString("X")));
    }

    [Theory]
    [InlineData("chrome", "chrome", "chrome", true)]
    [InlineData("Google Chrome", "chrome", "chrome", true)]
    [InlineData("Microsoft Edge", "msedge", "msedge", true)]
    [InlineData("chrome", "chrome", "msedge", false)]
    [InlineData("Specific tab - Chrome", "chrome", "chrome", false)]
    [InlineData("word", "WINWORD", "WINWORD", true)]
    public void Generic_process_focus_accepts_another_window_of_only_that_requested_process(
        string requestedName, string targetProcess, string foregroundProcess, bool expected) =>
        Assert.Equal(expected, JarvisAutomationService.IsEquivalentForegroundProcessRequest(
            requestedName, targetProcess, foregroundProcess));

    [Fact]
    public void Successful_open_receipt_exposes_one_exact_referential_target()
    {
        var path = Path.Combine(Path.GetTempPath(), "Example Folder");
        var args = JsonSerializer.SerializeToElement(new { folder = "Example Folder" });
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["path"] = path,
            ["message"] = "Opened Example Folder.",
        };

        JarvisAutomationService.AttachReferentialTarget("open_folder", args, result);

        var reference = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("folder", reference["kind"]);
        Assert.Equal(path, reference["target"]);
        Assert.DoesNotContain("native_window_title", reference.Keys);
        Assert.Equal("one", reference["cardinality"]);
    }

    [Fact]
    public void Opened_file_receipt_uses_the_file_name_as_its_native_title()
    {
        var path = Path.Combine(Path.GetTempPath(), "Release Notes.txt");
        var args = JsonSerializer.SerializeToElement(new { path });
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["path"] = path,
        };

        JarvisAutomationService.AttachReferentialTarget("open_path", args, result);

        var reference = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("path", reference["kind"]);
        Assert.Equal(path, reference["target"]);
        Assert.Equal("Release Notes.txt", reference["native_window_title"]);
    }

    [Fact]
    public void Failed_or_non_open_receipt_cannot_become_a_referential_target()
    {
        var args = JsonSerializer.SerializeToElement(new { folder = "Missing" });
        var failed = new Dictionary<string, object> { ["ok"] = false };
        var unrelated = new Dictionary<string, object> { ["ok"] = true };

        JarvisAutomationService.AttachReferentialTarget("open_folder", args, failed);
        JarvisAutomationService.AttachReferentialTarget("system_control", args, unrelated);

        Assert.DoesNotContain("referential_target", failed.Keys);
        Assert.DoesNotContain("referential_target", unrelated.Keys);
    }

    [Theory]
    [InlineData("take_photo")]
    [InlineData("take_screenshot")]
    [InlineData("save_note")]
    public void A_successfully_created_file_exposes_its_exact_containing_folder_generically(string tool)
    {
        var path = Path.GetTempFileName();
        try
        {
            var result = new Dictionary<string, object> { ["ok"] = true, ["path"] = path };
            JarvisAutomationService.AttachReferentialTarget(tool,
                JsonSerializer.SerializeToElement(new { }), result);
            var reference = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
            Assert.Equal("path", reference["kind"]);
            Assert.Equal(path, reference["target"]);
            Assert.Equal(Path.GetDirectoryName(path), reference["containing_folder"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_failed_or_unverified_file_save_cannot_advertise_a_containing_folder()
    {
        foreach (var ok in new[] { false, true })
        {
            var result = new Dictionary<string, object> { ["ok"] = ok,
                ["path"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") };
            JarvisAutomationService.AttachReferentialTarget("take_photo",
                JsonSerializer.SerializeToElement(new { }), result);
            Assert.DoesNotContain("referential_target", result.Keys);
        }
    }

    [Fact]
    public void Open_path_folder_receipt_keeps_resolved_path_and_does_not_advertise_a_shared_title()
    {
        var path = Path.Combine(Path.GetTempPath(), "A", "notes");
        var result = new Dictionary<string, object> { ["ok"] = true, ["path"] = path, ["target_kind"] = "folder" };
        JarvisAutomationService.AttachReferentialTarget("open_path", JsonSerializer.SerializeToElement(new { path = "notes" }), result);
        var target = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("folder", target["kind"]);
        Assert.Equal(path, target["target"]);
        Assert.DoesNotContain("native_window_title", target.Keys);
    }

    [Theory]
    [InlineData("ChatGPT", "ChatGPT", "https://chatgpt.com/", true)]
    [InlineData("Gmail", "Inbox - Gmail", "https://mail.google.com/", true)]
    [InlineData("ChatGPT", "Inbox - Gmail", "https://mail.google.com/", false)]
    [InlineData("https://example.com/portal", "Portal", "https://example.com/portal/home", true)]
    public void Named_browser_close_requires_a_verified_matching_tab(
        string target,
        string title,
        string url,
        bool expected) =>
        Assert.Equal(expected, JarvisAutomationService.BrowserTargetMatches(target, title, url));

    [Theory]
    [InlineData("Chrome", true)]
    [InlineData("the browser", true)]
    [InlineData("ChatGPT", false)]
    [InlineData("Discord", false)]
    public void Only_explicit_browser_names_authorize_closing_the_browser_window(
        string target,
        bool expected) =>
        Assert.Equal(expected, JarvisAutomationService.IsExplicitBrowserApplication(target));

    [Theory]
    [InlineData("new tab", "^t")]
    [InlineData("close this website", "^w")]
    [InlineData("reopen closed tab", "^+t")]
    [InlineData("refresh", "^r")]
    [InlineData("back", "%{LEFT}")]
    [InlineData("forward", "%{RIGHT}")]
    [InlineData("downloads", "^j")]
    [InlineData("history", "^h")]
    [InlineData("find on page", "^f")]
    public void BrowserActionsMapOnlyToAllowlistedShortcuts(string action, string expected)
    {
        Assert.Equal(expected, JarvisAutomationService.BrowserShortcutForAction(action));
    }

    [Theory]
    [InlineData("switch to Chrome's fourth tab using Ctrl+4", "index:4")]
    [InlineData("go to the 7th tab", "index:7")]
    [InlineData("navigate to the third tab", "index:3")]
    [InlineData("navigate to the first tab", "index:1")]
    [InlineData("navigate to the last tab", "from_end:1")]
    [InlineData("switch to last tab", "from_end:1")]
    [InlineData("Switch to the last tab.", "from_end:1")]
    [InlineData("Go to the last Chrome tab.", "from_end:1")]
    [InlineData("go to the second last tab", "from_end:2")]
    [InlineData("go to the second last browser tab", "from_end:2")]
    [InlineData("switch to second-to-last tab", "from_end:2")]
    [InlineData("go to the next tab", "next")]
    [InlineData("go to the previous tab", "previous")]
    public void Tab_selection_uses_exact_helper_tab_ids_instead_of_OS_keys(
        string request,
        string expectedSelection)
    {
        Assert.True(
            JarvisAutomationService.TryGetBrowserTabSelection(
                request,
                out var selection,
                out _));
        Assert.Equal(expectedSelection, selection);
        Assert.Null(JarvisAutomationService.BrowserShortcutForAction(request));
    }

    [Fact]
    public void Exact_tab_result_keeps_helper_identity_without_private_page_data()
    {
        var receipt = new KeyboardWtf.Services.BrowserAutomationV2.BrowserActionReceipt
        {
            Ok = true,
            WindowId = 7,
            TabId = 43,
            TabCount = 4,
            NoVisibleChange = false,
            AfterUrl = "https://private.example/record",
        };

        var result = CommandRegistry.ExactTabSelectionToolResult(receipt, "index:3", "tab 3");

        Assert.True((bool)result["ok"]);
        Assert.Equal("index:3", result["tab_selection"]);
        Assert.Equal(7, result["window_id"]);
        Assert.Equal(43, result["tab_id"]);
        Assert.Equal(4, result["tab_count"]);
        Assert.False((bool)result["already_active"]);
        Assert.DoesNotContain("private.example", JsonSerializer.Serialize(result));

        receipt.NoVisibleChange = true;
        var alreadyActive = CommandRegistry.ExactTabSelectionToolResult(receipt, "index:3", "tab 3");
        Assert.True((bool)alreadyActive["already_active"]);
        Assert.Contains("already active", alreadyActive["message"].ToString());
    }

    [Theory]
    [InlineData("{\"action\":\"select tab\",\"tab_index\":3}", "index:3")]
    [InlineData("{\"action\":\"select tab\",\"tab_from_end\":1}", "from_end:1")]
    public void Structured_tab_selection_does_not_depend_on_request_phrase(
        string json,
        string expected)
    {
        Assert.True(CommandRegistry.TryGetStructuredTabSelection(
            JsonDocument.Parse(json).RootElement, out var selection, out _));
        Assert.Equal(expected, selection);
    }

    [Fact]
    public void Structured_tab_selection_rejects_conflicting_positions_and_unverified_receipts()
    {
        using var document = JsonDocument.Parse(
            """{"action":"select tab","tab_index":3,"tab_from_end":1}""");
        Assert.False(CommandRegistry.TryGetStructuredTabSelection(
            document.RootElement, out _, out _));

        var incomplete = CommandRegistry.ExactTabSelectionToolResult(
            new KeyboardWtf.Services.BrowserAutomationV2.BrowserActionReceipt { Ok = true },
            "index:3", "tab 3");
        Assert.False((bool)incomplete["ok"]);
        Assert.False((bool)incomplete["already_active"]);
    }

    [Theory]
    [InlineData("close the last tab", "from_end:1")]
    [InlineData("Please close the second-last tab.", "from_end:2")]
    [InlineData("close the second-to-last browser tab", "from_end:2")]
    [InlineData("close the first tab", "index:1")]
    [InlineData("close the second Chrome tab", "index:2")]
    [InlineData("close the fourth tab", "index:4")]
    [InlineData("close the 4th tab", "index:4")]
    [InlineData("close this tab", "current")]
    [InlineData("close the current tab", "current")]
    [InlineData("close the previous tab", "previous")]
    public void A_close_operation_reuses_the_positional_tab_reference_without_becoming_a_selection(
        string request, string expected)
    {
        Assert.True(JarvisAutomationService.TryGetBrowserTabCloseReference(request, out var reference, out _));
        Assert.Equal(expected, reference);
        Assert.False(JarvisAutomationService.TryGetBrowserTabSelection(request, out _, out _));
        Assert.Equal(expected == "current" ? "^w" : null, JarvisAutomationService.BrowserShortcutForAction(request));
    }

    [Theory]
    [InlineData("don't close the last tab")]
    [InlineData("write this and close the last tab")]
    [InlineData("close the last tab and open another")]
    [InlineData("close the last three tabs")]
    [InlineData("close some tabs")]
    [InlineData("tell me how to close the last tab")]
    public void Composite_negated_and_plural_closes_do_not_become_one_relative_close(string request) =>
        Assert.False(JarvisAutomationService.TryGetBrowserTabCloseReference(request, out _, out _));

    [Theory]
    [InlineData("{\"action\":\"close tab\",\"tab_index\":2}", "index:2")]
    [InlineData("{\"action\":\"close tab\",\"tab_from_end\":1}", "from_end:1")]
    [InlineData("{\"action\":\"close tab\",\"tab_reference\":\"previous\"}", "previous")]
    [InlineData("{\"action\":\"close tab\",\"tab_reference\":\"current\"}", "current")]
    public void Structured_close_reference_is_separate_from_select_operation(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(JarvisAutomationService.TryGetStructuredBrowserTabClose(document.RootElement, out var reference, out _));
        Assert.Equal(expected, reference);
        Assert.False(CommandRegistry.TryGetStructuredTabSelection(document.RootElement, out _, out _));
    }

    [Theory]
    [InlineData("{\"action\":\"close tab\",\"tab_index\":2,\"tab_from_end\":1}")]
    [InlineData("{\"action\":\"close tab\",\"tab_index\":0}")]
    [InlineData("{\"action\":\"close tab\",\"tab_index\":2,\"target_ids\":[\"old\"]}")]
    [InlineData("{\"action\":\"close tabs\",\"tab_index\":2}")]
    [InlineData("{\"action\":\"select tab\",\"tab_index\":2}")]
    [InlineData("{\"action\":\"close tab\",\"tab_reference\":\"invented\"}")]
    public void Structured_close_rejects_conflicting_or_non_close_reference(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(JarvisAutomationService.TryGetStructuredBrowserTabClose(document.RootElement, out _, out _));
    }

    [Theory]
    [InlineData("close the last 3 Chrome tabs", 3)]
    [InlineData("Please close my last three browser tabs.", 3)]
    [InlineData("close last nine tabs", 9)]
    public void Rightmost_multi_tab_close_is_a_bounded_whole_command(
        string request,
        int expectedCount)
    {
        Assert.True(JarvisAutomationService.TryGetRightmostBrowserTabCloseCount(
            request,
            out var count));
        Assert.Equal(expectedCount, count);
    }

    [Theory]
    [InlineData("Close the first two tabs.", false, 2)]
    [InlineData("Can you please close the first 2 tabs?", false, 2)]
    [InlineData("Could you pls close the first three tabs?", false, 3)]
    [InlineData("Can you please close the last 2 tabs?", true, 2)]
    [InlineData("Please close my first three Chrome tabs.", false, 3)]
    [InlineData("close first 9 browser tabs", false, 9)]
    [InlineData("close the last two tabs", true, 2)]
    [InlineData("Please close my last three browser tabs.", true, 3)]
    [InlineData("Can you please close the first two tabs for me now?", false, 2)]
    [InlineData("Please close the first two tabs in Chrome.", false, 2)]
    [InlineData("I want you to close the first two tabs.", false, 2)]
    [InlineData("Can you please close the last two tabs for me now?", true, 2)]
    [InlineData("Please close the last two tabs in Chrome.", true, 2)]
    [InlineData("I want you to close the last two tabs.", true, 2)]
    [InlineData("Can you please close the first two tabs, please?", false, 2)]
    [InlineData("Jarvis, can you please close the first two tabs?", false, 2)]
    [InlineData("Can you just close the first two tabs in Chrome?", false, 2)]
    [InlineData("Close the first two tabs which are already open.", false, 2)]
    [InlineData("Please close the first two tabs that are open.", false, 2)]
    [InlineData("Jarvis, please just close the last three open tabs, please.", true, 3)]
    public void Positional_multi_tab_close_preserves_the_explicit_requested_edge(
        string request, bool expectedFromEnd, int expectedCount)
    {
        Assert.True(JarvisAutomationService.TryGetPositionalBrowserTabCloseCount(
            request, out var fromEnd, out var count));
        Assert.Equal(expectedFromEnd, fromEnd);
        Assert.Equal(expectedCount, count);
    }

    [Theory]
    [InlineData("close the first tab")]
    [InlineData("close the first 10 tabs")]
    [InlineData("don't close the first two tabs")]
    [InlineData("close the first two tabs and open Gmail")]
    [InlineData("write this and close the first two tabs")]
    [InlineData("tell me how to close the first two tabs")]
    [InlineData("I don't want you to close the first two tabs.")]
    [InlineData("I want you to tell me how to close the first two tabs.")]
    [InlineData("Can you please not close the first two tabs for me now?")]
    [InlineData("Please close the first two tabs in Chrome and open Gmail.")]
    [InlineData("I want you to close the first two tabs and delete my email.")]
    [InlineData("Jarvis, please don't close the first two tabs.")]
    [InlineData("Can you just tell me how to close the first two tabs, please?")]
    [InlineData("Close the first two tabs which are already open and open Gmail.")]
    [InlineData("Say: close the first two tabs.")]
    public void Positional_set_close_rejects_single_negated_composite_or_unbounded_requests(
        string request) =>
        Assert.False(JarvisAutomationService.TryGetPositionalBrowserTabCloseCount(
            request, out _, out _));

    [Theory]
    [InlineData("close the last tab")]
    [InlineData("close some tabs")]
    [InlineData("write this and close the last 3 tabs")]
    [InlineData("close the last 10 tabs")]
    public void Ambiguous_or_composite_tab_close_does_not_enter_the_fixed_set_owner(
        string request) =>
        Assert.False(JarvisAutomationService.TryGetRightmostBrowserTabCloseCount(
            request,
            out _));

    [Theory]
    [InlineData("Switch to the second-to-last tab.", true)]
    [InlineData("Please refresh.", true)]
    [InlineData("Write this text and then switch to the second tab.", false)]
    [InlineData("Open YouTube and use the third tab.", false)]
    public void Only_a_whole_direct_browser_command_uses_the_shortcut_pre_route(
        string request,
        bool expected)
    {
        Assert.Equal(
            expected,
            JarvisAutomationService.IsDirectBrowserShortcutRequest(request));
    }

    [Theory]
    [InlineData("create a new desktop", 0x44)]
    [InlineData("switch to next desktop", 0x27)]
    [InlineData("switch to previous desktop", 0x25)]
    [InlineData("close current desktop", 0x73)]
    [InlineData("move window to desktop", 0)]
    public void VirtualDesktopActionsMapOnlyToWindowsShortcuts(string action, int expected)
    {
        Assert.Equal((byte)expected, JarvisAutomationService.VirtualDesktopKeyForAction(action));
    }

    [Theory]
    [InlineData("start recording", 3, 0x12)]
    [InlineData("stop recording", 3, 0x12)]
    [InlineData("open game bar", 2, 0x47)]
    [InlineData("record screen region", 3, 0x10)]
    public void RecordingActionsMapOnlyToAllowlistedWindowsShortcuts(
        string action,
        int expectedLength,
        int expectedModifier)
    {
        var keys = JarvisAutomationService.WindowsRecordingShortcutForAction(action);

        Assert.NotNull(keys);
        Assert.Equal(expectedLength, keys.Length);
        Assert.Equal(0x5B, keys[0]);
        Assert.Equal(expectedModifier, keys[1]);
    }

    [Fact]
    public void UnknownRecordingActionHasNoShortcut()
    {
        Assert.Null(JarvisAutomationService.WindowsRecordingShortcutForAction("upload recording"));
    }
}
