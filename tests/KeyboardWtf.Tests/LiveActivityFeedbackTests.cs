namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

[Collection("Live event integration")]
public sealed class LiveActivityFeedbackTests
{
    [Fact]
    public void Ordinary_wait_begins_only_after_final_input_and_uses_existing_listening_display()
    {
        var feedback = new LiveActivityFeedback(1000);
        feedback.BeginInput();
        feedback.ObserveTranscript();
        Assert.Equal(0, feedback.TryPublish(5000, true, _ => 1));
        feedback.FinalInput(5000);
        Assert.Equal(0, feedback.TryPublish(6999, true, _ => 1));
        string detail = null;
        Assert.Equal(8, feedback.TryPublish(7000, true, text => { detail = text; return 8; }));
        Assert.Equal("Working on your request…", detail);
        Assert.Equal(0, feedback.TryPublish(8000, true, _ => 9));
    }

    [Fact]
    public void Selected_source_receipt_wait_survives_tool_only_boundary_and_ends_on_output()
    {
        var feedback = FinalInput();
        var input = feedback.InputIdentity;
        feedback.ToolStarted(input);
        feedback.ToolReceipt(input, 1000, selectedSource: true, featureOwned: false);
        feedback.ProviderBoundary(input);
        string detail = null;
        Assert.Equal(11, feedback.TryPublish(3000, true, text => { detail = text; return 11; }));
        Assert.Equal("Processing selected text…", detail);
        Assert.Equal(11, feedback.OutputOrCancel(input));
        Assert.Equal(0, feedback.TryPublish(6000, true, _ => 12));
    }

    [Fact]
    public void New_input_rejects_old_receipt_output_and_boundary()
    {
        var feedback = FinalInput();
        var oldInput = feedback.InputIdentity;
        Assert.Equal(11, feedback.TryPublish(2000, true, _ => 11));
        Assert.Equal(11, feedback.BeginInput());
        feedback.FinalInput(3000, submittedText: true);
        feedback.ToolReceipt(oldInput, 3000, selectedSource: true, featureOwned: false);
        feedback.OutputOrCancel(oldInput);
        feedback.ProviderBoundary(oldInput);
        // No response identifier distinguishes an old reply from the overlap;
        // fail visually quiet rather than relabel the newer input.
        Assert.Equal(0, feedback.TryPublish(5000, true, _ => 12));
        var current = feedback.BindToolInput();
        Assert.Equal(feedback.InputIdentity, current);
        feedback.ToolReceipt(current, 5000, selectedSource: true, featureOwned: false);
        Assert.Equal(12, feedback.TryPublish(7000, true, _ => 12));
    }

    [Fact]
    public void Ordinary_tool_receipt_can_wait_for_model_delivery_without_becoming_a_long_task()
    {
        var feedback = FinalInput();
        feedback.ToolReceipt(feedback.InputIdentity, 1000, selectedSource: false, featureOwned: false);
        feedback.ProviderBoundary(feedback.ResponseIdentity);
        string detail = null;
        Assert.Equal(2, feedback.TryPublish(3000, true, text => { detail = text; return 2; }));
        Assert.Equal("Working on your request…", detail);
    }

    [Fact]
    public void Translation_or_connector_owned_receipts_do_not_create_generic_wait()
    {
        var feedback = FinalInput();
        feedback.ToolReceipt(feedback.InputIdentity, 1000, selectedSource: true, featureOwned: true);
        Assert.Equal(0, feedback.TryPublish(5000, true, _ => 1));
    }

    [Fact]
    public void Busy_or_health_owned_display_does_not_consume_a_future_display_opportunity()
    {
        var feedback = FinalInput();
        Assert.Equal(0, feedback.TryPublish(5000, false, _ => throw new Exception("Must not publish")));
        Assert.Equal(0, feedback.TryPublish(5000, true, _ => 0));
        Assert.Equal(4, feedback.TryPublish(5000, true, _ => 4));
    }

    [Theory]
    [InlineData(VoiceUiPhase.Speaking)]
    [InlineData(VoiceUiPhase.Error)]
    [InlineData(VoiceUiPhase.Done)]
    [InlineData(VoiceUiPhase.Cancelled)]
    public void Display_cas_preserves_response_error_end_and_cancel(VoiceUiPhase nextPhase)
    {
        var feedback = FinalInput();
        var revision = KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening");
        KeyboardWtfState.SetUi(nextPhase, "Jarvis", "New owner");
        try
        {
            Assert.Equal(0, feedback.TryPublish(5000, true,
                text => KeyboardWtfState.TrySetListeningAdvisory(revision, "Jarvis", text)));
            Assert.Equal(nextPhase, KeyboardWtfState.UiPhase);
            Assert.Equal("New owner", KeyboardWtfState.UiDetail);
        }
        finally { KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready"); }
    }

    [Theory]
    [InlineData("translation")]
    [InlineData("connector")]
    public void Existing_long_task_owner_keeps_its_activity_and_phase(string owner)
    {
        var feedback = FinalInput();
        var revision = KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening");
        KeyboardWtfState.BeginLongTaskUi(owner, "Existing activity");
        try
        {
            Assert.Equal(0, feedback.TryPublish(5000, !KeyboardWtfState.HasLongTaskUiOwner,
                text => KeyboardWtfState.TrySetListeningAdvisory(revision, "Jarvis", text)));
            Assert.Equal("Existing activity", KeyboardWtfState.LongTaskDetail);
            Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);
        }
        finally { KeyboardWtfState.ClearLongTaskUi(owner); KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready"); }
    }

    [Fact]
    public void Cancelled_wait_never_reappears_at_a_late_boundary()
    {
        var feedback = FinalInput();
        feedback.OutputOrCancel(feedback.InputIdentity);
        feedback.ProviderBoundary(feedback.InputIdentity);
        Assert.Equal(0, feedback.TryPublish(5000, true, _ => 1));
    }

    private static LiveActivityFeedback FinalInput()
    {
        var feedback = new LiveActivityFeedback(1000);
        feedback.BeginInput();
        feedback.FinalInput(0, submittedText: true);
        return feedback;
    }
}
