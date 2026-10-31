using KeyboardWtf.Models;
using KeyboardWtf.Services;
using System.Reflection;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class PersonalLiveResponseHealthTests
{
    [Fact]
    public void Actual_close_feedback_is_revision_owned_and_clears_only_its_own_notice()
    {
        using var live = new GeminiLiveConversationService(null, null, null);
        using var session = new CancellationTokenSource();
        using var microphone = new WaveInEvent(); // Never initialized/started; no device input.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GeminiLiveConversationService).GetField("_sessionCts", flags).SetValue(live, session);
        typeof(GeminiLiveConversationService).GetField("_microphone", flags).SetValue(live, microphone);
        var diagnosticsType = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic);
        var diagnostics = Activator.CreateInstance(diagnosticsType, "synthetic-close", 1, DateTimeOffset.UtcNow, 0, false);
        diagnosticsType.GetProperty("UsesPersonalToolProtocol").SetValue(diagnostics, true);
        var publish = typeof(GeminiLiveConversationService).GetMethod("PublishPersonalProviderCloseAdvisory", flags);
        var clear = typeof(GeminiLiveConversationService).GetMethod("ClearPersonalResponseAdvisory", flags);
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening");
            publish.Invoke(live, new[] { (object)session, diagnostics, "remote_close" });
            Assert.Contains("repeat your request", KeyboardWtfState.UiDetail);
            // A fast recovered setup must not erase the confirmed lost-turn
            // advisory before a useful response or new user interaction.
            typeof(GeminiLiveConversationService).GetMethod("ShowConversationReady", flags)
                .Invoke(live, new object[] { false });
            Assert.Contains("repeat your request", KeyboardWtfState.UiDetail);
            clear.Invoke(live, null);
            Assert.Equal("Listening for your next request...", KeyboardWtfState.UiDetail);

            publish.Invoke(live, new[] { (object)session, diagnostics, "remote_close" });
            KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "Jarvis", "New useful response");
            clear.Invoke(live, null);
            Assert.Equal("New useful response", KeyboardWtfState.UiDetail);

            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening");
            KeyboardWtfState.BeginLongTaskUi("close-health-test", "Translating...");
            publish.Invoke(live, new[] { (object)session, diagnostics, "remote_close" });
            Assert.Equal("Listening", KeyboardWtfState.UiDetail);
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
            KeyboardWtfState.ClearLongTaskUi("close-health-test");
            using var stale = new CancellationTokenSource();
            publish.Invoke(live, new[] { (object)stale, diagnostics, "remote_close" });
            Assert.Equal("Listening", KeyboardWtfState.UiDetail);
            session.Cancel();
            publish.Invoke(live, new[] { (object)session, diagnostics, "remote_close" });
            Assert.Equal("Listening", KeyboardWtfState.UiDetail);
        }
        finally
        {
            typeof(GeminiLiveConversationService).GetField("_microphone", flags).SetValue(live, null);
            typeof(GeminiLiveConversationService).GetField("_sessionCts", flags).SetValue(live, null);
            KeyboardWtfState.ClearLongTaskUi("close-health-test");
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    }

    [Theory]
    [InlineData(true, true, "remote_close", true)]
    [InlineData(true, true, "transport_error", true)]
    [InlineData(true, true, "policy_or_auth_rejection", true)]
    [InlineData(false, true, "remote_close", false)]
    [InlineData(true, false, "remote_close", false)]
    [InlineData(true, true, "client_cancel", false)]
    [InlineData(true, true, "safe_physical_rollover", false)]
    [InlineData(true, true, "provider_connection_rollover", false)]
    [InlineData(true, true, "remote_normal_close", false)]
    public void Confirmed_personal_failure_feedback_does_not_wait_for_slow_reconnect(
        bool personal, bool active, string category, bool expected)
    {
        var detail = GeminiLiveConversationService.PersonalProviderCloseAdvisory(personal, active, category);
        Assert.Equal(expected, detail != null);
        if (expected)
        {
            Assert.StartsWith("Live connection interrupted.", detail);
            Assert.DoesNotContain("Gemini", detail);
            Assert.Contains("repeat your request", detail);
        }
    }

    private static PersonalLiveResponseHealth ActiveAudio()
    {
        var health = new PersonalLiveResponseHealth(1000);
        for (var index = 0; index < 8; index++) health.AudioSent(2880, -20, 1000 + index * 90, true);
        return health;
    }

    [Fact]
    public void Sent_activity_without_response_advises_once_after_bounded_wait()
    {
        var health = ActiveAudio();
        Assert.Null(health.TakeAdvisory(20999, true));
        Assert.Contains("Audio is being sent", health.TakeAdvisory(21000, true));
        Assert.Null(health.TakeAdvisory(50000, true));
        Assert.Contains("completedAudioChunks=8 activeAudioChunks=8", health.Snapshot(21000));
    }

    [Fact]
    public void Quiet_frames_or_brief_noise_do_not_claim_a_request()
    {
        var health = new PersonalLiveResponseHealth(1000);
        for (var index = 1; index < 400; index++) health.AudioSent(2880, -60, index * 90, true);
        health.AudioSent(2880, -20, 40000, true);
        Assert.Null(health.TakeAdvisory(90000, true));
    }

    [Fact]
    public void Long_utterance_is_not_timed_out_while_activity_continues()
    {
        var health = ActiveAudio();
        health.AudioSent(2880, -20, 50000, true);
        Assert.Null(health.TakeAdvisory(51000, true));
        Assert.NotNull(health.TakeAdvisory(52000, true));
    }

    [Fact]
    public void Separate_short_noise_bursts_do_not_accumulate_into_a_request()
    {
        var health = new PersonalLiveResponseHealth(1000);
        for (var index = 1; index < 20; index++) health.AudioSent(2880, -20, index * 5000, true);
        Assert.Null(health.TakeAdvisory(200000, true));
    }

    [Fact]
    public void Busy_ui_tools_or_playback_suppress_feedback_and_echo_tracking()
    {
        var health = ActiveAudio();
        Assert.Null(health.TakeAdvisory(30000, false));
        health.AudioSent(2880, -10, 31000, false);
        Assert.Null(health.TakeAdvisory(90000, true));
    }

    [Fact]
    public void Recognized_request_has_distinct_truthful_advisory()
    {
        var health = new PersonalLiveResponseHealth(1000);
        health.InputRecognized(1000);
        Assert.Contains("has not answered", health.TakeAdvisory(21000, true));
    }

    [Fact]
    public void Useful_response_clears_wait_and_next_request_can_be_observed()
    {
        var health = ActiveAudio();
        Assert.NotNull(health.TakeAdvisory(21000, true));
        health.ResponseReceived(22000);
        Assert.Null(health.TakeAdvisory(90000, true));
        health.InputRecognized(91000);
        Assert.NotNull(health.TakeAdvisory(111000, true));
    }

    [Fact]
    public void New_socket_has_no_old_pending_wait_or_content()
    {
        Assert.NotNull(ActiveAudio().TakeAdvisory(21000, true));
        var next = new PersonalLiveResponseHealth(1000);
        Assert.Null(next.TakeAdvisory(90000, true));
        Assert.DoesNotContain("key", next.Snapshot(90000));
    }

    [Fact]
    public void Advisory_cannot_overwrite_new_response_or_owned_long_task()
    {
        KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening");
        var revision = KeyboardWtfState.UiRevision;
        KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "Jarvis", "Answer");
        Assert.Equal(0, KeyboardWtfState.TrySetListeningAdvisory(revision, "Jarvis", "No response"));
        Assert.Equal("Answer", KeyboardWtfState.UiDetail);
        KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening");
        KeyboardWtfState.BeginLongTaskUi("health-test", "Translating…");
        try { Assert.Equal(0, KeyboardWtfState.TrySetListeningAdvisory(KeyboardWtfState.UiRevision, "Jarvis", "No response")); }
        finally { KeyboardWtfState.ClearLongTaskUi("health-test"); }
        var owned = KeyboardWtfState.TrySetListeningAdvisory(KeyboardWtfState.UiRevision, "Jarvis", "No response");
        Assert.NotEqual(0, owned);
        KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Jarvis", "Ended");
        Assert.False(KeyboardWtfState.TrySetUiIfRevision(owned, VoiceUiPhase.Listening, "Jarvis", "Listening"));
        Assert.Equal("Ended", KeyboardWtfState.UiDetail);
    }
}
