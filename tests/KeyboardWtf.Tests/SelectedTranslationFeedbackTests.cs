using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class SelectedTranslationFeedbackTests
{
    [Theory]
    [InlineData("Translate that Devpost selection to French and copy it to my clipboard.")]
    [InlineData("Translate the earlier Devpost selection to French and copy it to my clipboard.")]
    [InlineData("Translate earlier Devpost selection to French and copy it to my clipboard.")]
    [InlineData("Translate my Atlas project selection to French and copy it to my clipboard.")]
    [InlineData("Translate my Atlas-Project selection to French and copy it to my clipboard.")]
    [InlineData("Translate this Atlas-Project selection to French and copy it to my clipboard.")]
    [InlineData("Translate the earlier selection to French and copy it to my clipboard.")]
    [InlineData("Translate that selection to French and copy it to my clipboard.")]
    public void Explicit_named_or_earlier_selection_copy_request_reaches_authoritative_source_resolution(string request)
    {
        Assert.True(SelectedTextTranslationOperation.TryParseRequest(request, hasOriginal: true, out var language));
        Assert.Equal("French", language);
    }

    [Theory]
    [InlineData("Translate that Devpost selection to French.")]
    [InlineData("Translate it to French.")]
    [InlineData("Translate the selected text to French.")]
    [InlineData("Don't translate that Devpost selection to French and copy it to clipboard.")]
    [InlineData("Explain how to translate that Devpost selection to French and copy it to clipboard.")]
    [InlineData("Translate that Devpost selection to French and copy it to clipboard then submit the form.")]
    public void No_copy_negated_or_compound_effect_requests_cannot_infer_clipboard_authority(string request) =>
        Assert.False(SelectedTextTranslationOperation.TryParseRequest(request, hasOriginal: true, out _));

    [Fact]
    public async Task Pending_and_fast_completed_receipts_leave_speech_to_one_lifecycle_event()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var pending = operation.Receipt();
        Assert.Equal("selected_text_translation_lifecycle", pending["announcement_owner"]);
        Assert.Equal("", pending["message"]); // Pending work has UI, not a spoken start acknowledgement.
        var announcements = 0;
        operation.Start("source", (_, _, _) => Task.FromResult("result"),
            _ => new() { ["ok"] = true, ["clipboard_verified"] = true, ["message"] = "Copied." },
            _ => { announcements++; return Task.CompletedTask; });
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, announcements);
        Assert.Equal(true, operation.Receipt()["completed"]);
        Assert.Equal("selected_text_translation_lifecycle", operation.Receipt()["announcement_owner"]);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("NeedsUserAction")]
    public void Translation_lifecycle_requests_one_short_truthful_outcome_not_progress_narration(string state)
    {
        var text = GeminiLiveConversationService.BuildLifecycleEventText(new()
        {
            Source = "selected_text_translation", State = state, Detail = "Verified outcome.",
        });
        Assert.Contains("only completion acknowledgement", text);
        Assert.Contains("one brief outcome", text);
        Assert.Contains("language the user used to request the translation", text);
        Assert.Contains("target applies only to the document, not your acknowledgement", text);
        Assert.Contains("Do not explain task ownership or background processing", text);
        Assert.Contains("never claim it was copied before confirmation", text);
        Assert.DoesNotContain("same browser task remains paused", text);
    }

    [Fact]
    public void Short_task_clears_before_display_threshold_without_delaying_work_or_ownership()
    {
        try
        {
            KeyboardWtfState.BeginLongTaskUi("short-feedback", "Translating...", displayDelayMilliseconds: 500);
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
            Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.Zero));
            Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromMilliseconds(499)));
            Assert.True(KeyboardWtfState.ClearLongTaskUi("short-feedback"));
            Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
        }
        finally { KeyboardWtfState.ClearLongTaskUi("short-feedback"); }
    }

    [Fact]
    public void Long_task_displays_real_state_then_clears_without_an_additional_copying_delay()
    {
        try
        {
            KeyboardWtfState.BeginLongTaskUi("long-feedback", "Translating...", displayDelayMilliseconds: 500);
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromMilliseconds(500)));
            Assert.True(KeyboardWtfState.TryUpdateLongTaskUi("long-feedback", "Copying..."));
            Assert.Equal("Copying...", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromMilliseconds(501)));
            Assert.True(KeyboardWtfState.ClearLongTaskUi("long-feedback"));
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(2)));
        }
        finally { KeyboardWtfState.ClearLongTaskUi("long-feedback"); }
    }

    [Fact]
    public void Replacement_resets_only_display_age_and_stale_cleanup_cannot_clear_new_owner()
    {
        try
        {
            KeyboardWtfState.BeginLongTaskUi("old-feedback", "Translating...", displayDelayMilliseconds: 500);
            KeyboardWtfState.BeginLongTaskUi("replacement-feedback", "Translating...", displayDelayMilliseconds: 500);
            Assert.False(KeyboardWtfState.ClearLongTaskUi("old-feedback"));
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
            Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.Zero));
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
        }
        finally { KeyboardWtfState.ClearLongTaskUi("replacement-feedback"); }
    }

    [Fact]
    public void Existing_task_callers_keep_immediate_display_by_default()
    {
        try
        {
            KeyboardWtfState.BeginLongTaskUi("immediate-feedback", "Working...");
            Assert.Equal("Working...", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.Zero));
        }
        finally { KeyboardWtfState.ClearLongTaskUi("immediate-feedback"); }
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("empty")]
    [InlineData("cancel")]
    public async Task Failed_or_cancelled_owned_translation_clears_delayed_display_and_cannot_publish_late_copy(string outcome)
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = 0;
        KeyboardWtfState.BeginLongTaskUi(operation.Id, "Translating...", displayDelayMilliseconds: 500);
        try
        {
            operation.Start("source", (_, _, _) => { entered.SetResult(); return generation.Task; },
                _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask,
                status => KeyboardWtfState.TryUpdateLongTaskUi(operation.Id, status));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(1)));
            if (outcome == "cancel")
            {
                Assert.True(operation.Cancel());
                Assert.Equal("", KeyboardWtfState.LongTaskDetail);
                generation.SetResult("late result");
            }
            else if (outcome == "empty") generation.SetResult("");
            else generation.SetException(new InvalidOperationException("provider failed"));
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, copies);
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            Assert.Equal("", KeyboardWtfState.LongTaskDisplayDetail(TimeSpan.FromSeconds(2)));
        }
        finally { KeyboardWtfState.ClearLongTaskUi(operation.Id); }
    }
}
