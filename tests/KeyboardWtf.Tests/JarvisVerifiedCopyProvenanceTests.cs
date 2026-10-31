using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class JarvisVerifiedCopyProvenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-copy-tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly JarvisAutomationService _automation;

    public JarvisVerifiedCopyProvenanceTests()
    {
        _settings = new(Path.Combine(_directory, "settings.json"));
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        _automation = new(null, _settings, new(Path.Combine(_directory, "history.json")),
            new(Path.Combine(_directory, "mappings.json")), () => { });
    }

    [Fact]
    public void Exact_verified_copy_retains_complete_output_and_origin_without_changing_receipt()
    {
        const string request = "Copy the judges' names to my clipboard.";
        const string text = "Ada Smith\nRobin Jones";
        _automation.ObserveUserTranscript(request);
        var before = DateTimeOffset.UtcNow;
        var clipboard = "previous clipboard";
        var writes = 0;
        var receipt = _automation.CopyText(text, value => { writes++; clipboard = value; return true; }, () => clipboard);

        Assert.Equal(1, writes);
        Assert.Equal(true, receipt["ok"]);
        Assert.Equal(true, receipt["clipboard_verified"]);
        var snapshot = Assert.IsType<JarvisAutomationService.VerifiedClipboardCopy>(_automation.LatestVerifiedClipboardCopy);
        Assert.Equal(text, snapshot.Text);
        Assert.Equal(request, snapshot.OriginatingUserTurn);
        Assert.InRange(snapshot.CreatedUtc, before, DateTimeOffset.UtcNow);
        Assert.False(receipt.ContainsKey("text"));
        Assert.False(receipt.ContainsKey("originating_user_turn"));
        Assert.False(receipt.ContainsKey("copied_result"));
    }

    [Theory]
    [InlineData(false, "result")]
    [InlineData(true, "old clipboard")]
    public void Failed_write_or_readback_cannot_create_an_output_referent(bool wrote, string readback)
    {
        var reads = 0;
        var receipt = _automation.CopyText("result", _ => wrote, () => { reads++; return readback; });
        Assert.Equal(false, receipt["ok"]);
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.Equal(wrote ? 1 : 0, reads);
        Assert.False(_automation.AttributeVerifiedCopy("result", "request", receipt));
    }

    [Fact]
    public void Failed_new_copy_preserves_only_the_previous_verified_snapshot()
    {
        var old = Copy("old verified output", "Copy the old result.");
        var snapshot = _automation.LatestVerifiedClipboardCopy;
        var failure = _automation.CopyText("unverified result", _ => true, () => "different text");
        Assert.Equal(false, failure["ok"]);
        Assert.Same(snapshot, _automation.LatestVerifiedClipboardCopy);
        Assert.False(_automation.AttributeVerifiedCopy("unverified result", "request", failure));
        Assert.Equal(true, old["clipboard_verified"]);
    }

    [Fact]
    public void Copy_origin_is_captured_before_the_write_not_from_later_speech()
    {
        const string original = "Copy the judges' names.";
        _automation.ObserveUserTranscript(original);
        var clipboard = "";
        _automation.CopyText("Ada Smith", value =>
        {
            _automation.ObserveUserTranscript("What is two plus two?");
            clipboard = value;
            return true;
        }, () => clipboard);
        Assert.Equal(original, _automation.LatestVerifiedClipboardCopy.OriginatingUserTurn);
    }

    [Fact]
    public void Owned_operation_may_reassign_only_its_exact_current_verified_receipt()
    {
        var receipt = Copy("translated output", "An unrelated question.");
        var oldSnapshot = _automation.LatestVerifiedClipboardCopy;
        const string origin = "Translate the earlier selected text to French and copy it to clipboard.";
        Assert.True(_automation.AttributeVerifiedCopy("translated output", origin, receipt));
        Assert.Equal(origin, _automation.LatestVerifiedClipboardCopy.OriginatingUserTurn);
        Assert.Equal(oldSnapshot.CreatedUtc, _automation.LatestVerifiedClipboardCopy.CreatedUtc);
        Assert.Equal("An unrelated question.", oldSnapshot.OriginatingUserTurn); // Snapshot remains immutable.
        Assert.False(_automation.AttributeVerifiedCopy("different output", "wrong", receipt));
        Assert.False(_automation.AttributeVerifiedCopy("translated output", "wrong", new(receipt)));

        var newer = Copy("translated output", "A later copy with the same contents.");
        Assert.False(_automation.AttributeVerifiedCopy("translated output", "stale", receipt));
        Assert.True(_automation.AttributeVerifiedCopy("translated output", "current", newer));
    }

    [Fact]
    public async Task Unconfirmed_copy_does_not_create_a_verified_output_referent()
    {
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        var receipt = await _automation.ExecuteAsync("copy_text",
            JsonSerializer.SerializeToElement(new { text = "not yet copied" }), CancellationToken.None);
        Assert.Equal(true, receipt["confirmation_required"]);
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.False(_automation.AttributeVerifiedCopy("not yet copied", "request", receipt));
        await _automation.ExecuteAsync("cancel_sensitive_action", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
    }

    [Fact]
    public void Reset_clears_verified_copy_and_rejects_late_origin_assignment()
    {
        var receipt = Copy("verified output", "Copy the result.");
        _automation.ResetConversationTransientContext();
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.False(_automation.AttributeVerifiedCopy("verified output", "old session", receipt));
    }

    [Fact]
    public void Copy_in_progress_at_session_reset_cannot_restore_the_old_referent()
    {
        var receipt = _automation.CopyText("verified after reset", _ =>
        {
            _automation.ResetConversationTransientContext();
            return true;
        }, () => "verified after reset");
        Assert.Equal(true, receipt["clipboard_verified"]);
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
    }

    [Fact]
    public void Newer_verified_copy_cannot_be_overwritten_by_an_older_callback()
    {
        _automation.ObserveUserTranscript("Copy A.");
        _automation.CopyText("A", _ => true, () =>
        {
            Copy("B", "Copy B.");
            return "A"; // The earlier read completes after the later verified copy.
        });
        Assert.Equal("B", _automation.LatestVerifiedClipboardCopy.Text);
        Assert.Equal("Copy B.", _automation.LatestVerifiedClipboardCopy.OriginatingUserTurn);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(50_001)]
    [InlineData(250_000)]
    public void Complete_verified_output_is_retained_without_a_character_ceiling(int length)
    {
        Copy("earlier verified output", "Copy earlier.");
        var output = new string('x', length);
        var receipt = Copy(output, "Copy this output.");
        Assert.Equal(true, receipt["clipboard_verified"]);
        Assert.Equal(length, receipt["character_count"]);
        Assert.Equal(output, _automation.LatestVerifiedClipboardCopy.Text);
    }

    private Dictionary<string, object> Copy(string text, string origin)
    {
        _automation.ObserveUserTranscript(origin);
        var clipboard = "";
        return _automation.CopyText(text, value => { clipboard = value; return true; }, () => clipboard);
    }

    public void Dispose()
    {
        _automation.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
