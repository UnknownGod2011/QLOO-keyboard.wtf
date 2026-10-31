namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

public sealed class BrowserSelectionFallbackTests
{
    [Fact]
    public void Old_helper_remains_usable_but_cannot_claim_a_selection_read()
    {
        using var bridge = new BrowserHelperBridge();
        UpdateStatus(bridge, selectionReadSupported: false);
        var status = bridge.Status;
        Assert.True(status.CommandChannelReady);
        Assert.False(status.SelectionReadSupported);
        Assert.False(JarvisAutomationService.CanTryBrowserSelectionFallback(
            new SelectionContext(new IntPtr(3), "chrome", "Example - Google Chrome"), status));
    }

    [Fact]
    public void Browser_read_requires_an_exact_live_Chrome_window_and_receipt()
    {
        using var bridge = new BrowserHelperBridge();
        UpdateStatus(bridge, selectionReadSupported: true);
        var status = bridge.Status;
        var original = new SelectionContext(new IntPtr(3), "chrome", "Example - Google Chrome");
        Assert.True(JarvisAutomationService.CanTryBrowserSelectionFallback(original, status));
        Assert.False(JarvisAutomationService.CanTryBrowserSelectionFallback(
            original with { ProcessName = "msedge" }, status));

        var receipt = new BrowserSelectionReceipt
        {
            Ok = true, Text = "genuine selected content", TotalLength = 23,
            SessionId = status.SessionId, WindowId = status.WindowId, TabId = status.TabId,
        };
        Assert.True(JarvisAutomationService.BrowserSelectionReceiptIsOwned(
            receipt, status, original, original));
        Assert.False(JarvisAutomationService.BrowserSelectionReceiptIsOwned(
            WithSession("old"), status, original, original));
        Assert.False(JarvisAutomationService.BrowserSelectionReceiptIsOwned(
            receipt, status, original, original with { Handle = new IntPtr(4) }));
        Assert.False(JarvisAutomationService.BrowserSelectionReceiptIsOwned(
            receipt, status, original, original with { WindowTitle = "Other - Google Chrome" }));

        BrowserSelectionReceipt WithSession(string session) => new()
        {
            Ok = true, Text = receipt.Text, TotalLength = receipt.TotalLength,
            SessionId = session, WindowId = receipt.WindowId, TabId = receipt.TabId,
        };
    }

    [Theory]
    [InlineData(32000)]
    [InlineData(50000)]
    [InlineData(50001)]
    [InlineData(250000)]
    public void Larger_receipts_preserve_identity_checks_without_a_character_ceiling(int length)
    {
        using var bridge = new BrowserHelperBridge();
        UpdateStatus(bridge, true);
        var status = bridge.Status;
        var context = new SelectionContext((IntPtr)3, "chrome", "Example - Google Chrome");
        var receipt = new BrowserSelectionReceipt
        {
            Ok = true, Text = new string('x', length), TotalLength = length,
            SessionId = status.SessionId, WindowId = status.WindowId, TabId = status.TabId,
        };
        Assert.True(JarvisAutomationService.BrowserSelectionReceiptIsOwned(receipt, status, context, context));
        var tooLarge = new BrowserSelectionReceipt
        {
            Ok = true, Text = "", TotalLength = 50001, Truncated = true,
            SessionId = status.SessionId, WindowId = status.WindowId, TabId = status.TabId,
        };
        Assert.True(JarvisAutomationService.BrowserSelectionReceiptIsOwned(tooLarge, status, context, context));
        Assert.False(JarvisAutomationService.BrowserSelectionReceiptIsOwned(tooLarge, status, context, context with { Handle = (IntPtr)4 }));
    }

    private static void UpdateStatus(BrowserHelperBridge bridge, bool selectionReadSupported)
    {
        var method = typeof(BrowserHelperBridge).GetMethod("ProcessMessage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(bridge, [JsonSerializer.Serialize(new
        {
            type = "hello",
            helper = new
            {
                extensionId = BrowserHelperProtocol.StoreExtensionId,
                serviceWorkerVersion = BrowserHelperProtocol.Version,
                contentScriptVersion = BrowserHelperProtocol.Version,
                activeTabFresh = true,
                commandChannelReady = true,
                commandAuthoritySupported = true,
                selectionReadSupported,
                activeUrl = "https://example.org/",
                activeTitle = "Example",
                sessionId = "current-session",
                windowId = 9,
                tabId = 31,
            },
        })]);
    }
}
