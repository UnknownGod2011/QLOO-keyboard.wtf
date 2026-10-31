using KeyboardWtf.Helpers;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class JarvisClipboardPasteTests
{
    private const string Replacement = "new result";
    private const string SelectedSource = "old selected source";

    [Fact]
    public void Selection_capture_requires_a_successful_marker_write_and_exact_readback()
    {
        var reads = 0;
        Assert.False(JarvisAutomationService.ClipboardPayloadIsReady(
            false, "unique marker", () => { reads++; return SelectedSource; }));
        Assert.Equal(0, reads);
        Assert.False(JarvisAutomationService.ClipboardPayloadIsReady(
            true, "unique marker", () => SelectedSource));
        Assert.True(JarvisAutomationService.ClipboardPayloadIsReady(
            true, "unique marker", () => "unique marker"));
    }

    [Fact]
    public async Task Missing_clipboard_snapshot_never_writes_or_pastes()
    {
        var writes = 0;
        var pastes = 0;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => null,
            _ => { writes++; return true; },
            () => SelectedSource,
            () => 1,
            _ => true,
            _ => pastes++,
            (_, _) => Task.CompletedTask);

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.SnapshotUnavailable, outcome);
        Assert.Equal(0, writes);
        Assert.Equal(0, pastes);
    }

    [Fact]
    public async Task Failed_clipboard_write_never_pastes_the_old_selected_source()
    {
        var clipboard = SelectedSource;
        var pastes = 0;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => new ClipboardHelper.Snapshot(),
            _ => false,
            () => clipboard,
            () => 1,
            _ => true,
            _ => pastes++,
            (_, _) => Task.CompletedTask);

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.WriteFailed, outcome);
        Assert.Equal(0, pastes);
        Assert.Equal(SelectedSource, clipboard);
    }

    [Fact]
    public async Task Failed_write_that_cleared_clipboard_restores_snapshot_without_pasting()
    {
        var clipboard = SelectedSource;
        var sequence = 1u;
        var restores = 0;
        var pastes = 0;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => new ClipboardHelper.Snapshot(),
            _ => { clipboard = ""; sequence++; return false; },
            () => clipboard,
            () => sequence,
            _ => { restores++; clipboard = SelectedSource; return true; },
            _ => pastes++,
            (_, _) => Task.CompletedTask,
            () => true);

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.WriteFailed, outcome);
        Assert.Equal(0, pastes);
        Assert.Equal(1, restores);
        Assert.Equal(SelectedSource, clipboard);
    }

    [Fact]
    public async Task Failed_write_does_not_overwrite_a_new_clipboard_owner()
    {
        var clipboard = SelectedSource;
        var sequence = 1u;
        var restores = 0;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => new ClipboardHelper.Snapshot(),
            _ => { clipboard = "other application data"; sequence++; return false; },
            () => clipboard,
            () => sequence,
            _ => { restores++; return true; },
            _ => { throw new Xunit.Sdk.XunitException("Failed write must not paste."); },
            (_, _) => Task.CompletedTask);

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.WriteFailed, outcome);
        Assert.Equal(0, restores);
        Assert.Equal("other application data", clipboard);
    }

    [Fact]
    public void A_later_sequence_must_not_be_restored_as_our_failed_write()
    {
        Assert.True(JarvisAutomationService.FailedClipboardWriteMayHaveClearedPrior(
            false, 1, 2, 2, "", true));
        Assert.False(JarvisAutomationService.FailedClipboardWriteMayHaveClearedPrior(
            false, 1, 2, 3, "", true));
        Assert.False(JarvisAutomationService.FailedClipboardWriteMayHaveClearedPrior(
            false, 1, 2, 2, "other application data", true));
        Assert.False(JarvisAutomationService.FailedClipboardWriteMayHaveClearedPrior(
            true, 1, 2, 2, "", true));
        Assert.False(JarvisAutomationService.FailedClipboardWriteMayHaveClearedPrior(
            false, 1, 2, 2, "", false));
    }

    [Fact]
    public async Task Mismatched_clipboard_readback_never_pastes_stale_source()
    {
        var clipboard = SelectedSource;
        var pastes = 0;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => new ClipboardHelper.Snapshot(),
            _ => true,
            () => clipboard,
            () => 2,
            _ => true,
            _ => pastes++,
            (_, _) => Task.CompletedTask);

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.VerificationFailed, outcome);
        Assert.Equal(0, pastes);
    }

    [Fact]
    public async Task Verified_payload_pastes_once_then_restores_the_prior_clipboard()
    {
        var clipboard = SelectedSource;
        var sequence = 1u;
        var pastes = 0;
        var restores = 0;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => new ClipboardHelper.Snapshot(),
            text => { clipboard = text; sequence++; return true; },
            () => clipboard,
            () => sequence,
            _ => { restores++; clipboard = SelectedSource; return true; },
            _ => { Assert.Equal(Replacement, clipboard); pastes++; },
            (_, _) => Task.CompletedTask);

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.Pasted, outcome);
        Assert.Equal(1, pastes);
        Assert.Equal(1, restores);
        Assert.Equal(SelectedSource, clipboard);
    }

    [Fact]
    public async Task Cancellation_after_input_does_not_end_the_paste_grace_early()
    {
        using var cancellation = new CancellationTokenSource();
        var clipboard = SelectedSource;
        var graceCompleted = false;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, cancellation.Token,
            () => new ClipboardHelper.Snapshot(),
            text => { clipboard = text; return true; },
            () => clipboard,
            () => 2,
            _ => { Assert.True(graceCompleted); clipboard = SelectedSource; return true; },
            _ => cancellation.Cancel(),
            (_, graceToken) =>
            {
                Assert.False(graceToken.CanBeCanceled);
                Assert.Equal(Replacement, clipboard);
                graceCompleted = true;
                return Task.CompletedTask;
            });

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.Pasted, outcome);
        Assert.True(graceCompleted);
    }

    [Fact]
    public async Task A_new_clipboard_owner_is_not_overwritten_after_paste()
    {
        var clipboard = SelectedSource;
        var sequence = 1u;
        var restores = 0;
        var outcome = await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => new ClipboardHelper.Snapshot(),
            text => { clipboard = text; sequence++; return true; },
            () => clipboard,
            () => sequence,
            _ => { restores++; return true; },
            _ => { },
            (_, _) =>
            {
                clipboard = "newer user clipboard";
                sequence++;
                return Task.CompletedTask;
            });

        Assert.Equal(JarvisAutomationService.ClipboardPasteOutcome.Pasted, outcome);
        Assert.Equal(0, restores);
        Assert.Equal("newer user clipboard", clipboard);
    }

    [Fact]
    public async Task Same_text_but_new_sequence_is_not_restored_as_ours()
    {
        var clipboard = SelectedSource;
        var sequence = 1u;
        var restores = 0;
        await JarvisAutomationService.PasteTextWithClipboardAsync(
            Replacement, CancellationToken.None,
            () => new ClipboardHelper.Snapshot(),
            text => { clipboard = text; sequence++; return true; },
            () => clipboard,
            () => sequence,
            _ => { restores++; return true; },
            _ => { },
            (_, _) => { sequence++; return Task.CompletedTask; });

        Assert.Equal(0, restores);
        Assert.Equal(Replacement, clipboard);
    }

    [Fact]
    public async Task Cancellation_before_input_restores_prepared_payload_without_pasting()
    {
        using var cancellation = new CancellationTokenSource();
        var clipboard = SelectedSource;
        var pastes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            JarvisAutomationService.PasteTextWithClipboardAsync(
                Replacement, cancellation.Token,
                () => new ClipboardHelper.Snapshot(),
                text => { clipboard = text; return true; },
                () => { cancellation.Cancel(); return clipboard; },
                () => 2,
                _ => { clipboard = SelectedSource; return true; },
                _ => pastes++,
                (_, _) => Task.CompletedTask));

        Assert.Equal(0, pastes);
        Assert.Equal(SelectedSource, clipboard);
    }
}
