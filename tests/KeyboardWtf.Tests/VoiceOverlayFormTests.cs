namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

[Collection("Live event integration")]
public sealed class VoiceOverlayFormTests
{
    [Fact]
    public void Task_status_has_independent_ownership_without_changing_voice_state_or_revision()
    {
        var processing = KeyboardWtfState.IsProcessingAi;
        var revision = KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "Jarvis", "Speaking...");
        var phaseStarted = KeyboardWtfState.UiPhaseStartedUtc;
        try
        {
            KeyboardWtfState.BeginLongTaskUi("old", "Translating...");
            KeyboardWtfState.BeginLongTaskUi("current", "Translating...");
            Assert.False(KeyboardWtfState.TryUpdateLongTaskUi("old", "Copying..."));
            Assert.False(KeyboardWtfState.ClearLongTaskUi("old"));
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
            Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);
            Assert.Equal("Speaking...", KeyboardWtfState.UiDetail);
            Assert.Equal(phaseStarted, KeyboardWtfState.UiPhaseStartedUtc);
            Assert.Equal(processing, KeyboardWtfState.IsProcessingAi);
            Assert.True(KeyboardWtfState.TryUpdateLongTaskUi("current", "Copying..."));
            Assert.Equal("Copying...", KeyboardWtfState.LongTaskDetail);
            Assert.True(KeyboardWtfState.TrySetUiIfRevision(revision, VoiceUiPhase.Listening, "Jarvis", "Listening..."));
            Assert.True(KeyboardWtfState.ClearLongTaskUi("current"));
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);
        }
        finally
        {
            KeyboardWtfState.ClearLongTaskUi("current");
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    }

    [Fact]
    public void Task_state_subscriber_faults_are_best_effort()
    {
        Action fault = () => throw new InvalidOperationException("UI subscriber fault");
        KeyboardWtfState.UiChanged += fault;
        try
        {
            KeyboardWtfState.BeginLongTaskUi("event-fault", "Translating...");
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
            Assert.True(KeyboardWtfState.TryUpdateLongTaskUi("event-fault", "Copying..."));
            Assert.Equal("Copying...", KeyboardWtfState.LongTaskDetail);
            Assert.True(KeyboardWtfState.ClearLongTaskUi("event-fault"));
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
        }
        finally
        {
            KeyboardWtfState.UiChanged -= fault;
            KeyboardWtfState.ClearLongTaskUi("event-fault");
        }
    }

    [Fact]
    public async Task Late_cancelled_translation_cannot_clear_a_replacement_task_status()
    {
        using var old = new SelectedTextTranslationOperation("old", "French", CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyboardWtfState.BeginLongTaskUi(old.Id, "Translating...");
        try
        {
            old.Start("source", (_, _, _) => { entered.SetResult(); return generation.Task; },
                _ => throw new InvalidOperationException("Cancelled task must not copy"), _ => Task.CompletedTask,
                detail => KeyboardWtfState.TryUpdateLongTaskUi(old.Id, detail));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(old.Cancel());
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            KeyboardWtfState.BeginLongTaskUi("replacement", "Translating...");
            generation.SetResult("old late output");
            await old.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
        }
        finally
        {
            generation.TrySetResult("late output");
            KeyboardWtfState.ClearLongTaskUi(old.Id);
            KeyboardWtfState.ClearLongTaskUi("replacement");
        }
    }

    [Theory]
    [InlineData(VoiceUiPhase.Listening)]
    [InlineData(VoiceUiPhase.Speaking)]
    [InlineData(VoiceUiPhase.Idle)]
    [InlineData(VoiceUiPhase.Done)]
    public void Active_task_detail_does_not_depend_on_a_fake_elapsed_timer(VoiceUiPhase phase)
    {
        Assert.Equal("Translating...", VoiceOverlayForm.BuildDetail(phase, "Listening...", TimeSpan.Zero, "Translating..."));
        Assert.Equal("Copying...", VoiceOverlayForm.BuildDetail(phase, "Speaking...", TimeSpan.FromHours(1), "Copying..."));
    }

    [Fact]
    public void Background_task_keeps_the_overlay_visible_and_clears_without_a_voice_phase_change()
    {
        Exception failure = null;
        var completed = new ManualResetEventSlim();
        var uiThread = new Thread(() =>
        {
            try
            {
                using var recorder = new AudioRecorderService();
                using var overlay = QuietFormFixture.CreateOverlay(recorder);
                overlay.InitializeUiThreadHandle();
                QuietFormFixture.AssertQuiet(overlay);
                KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
                KeyboardWtfState.BeginLongTaskUi("overlay-test", "Translating...");
                Assert.True(overlay.Visible);
                QuietFormFixture.AssertQuiet(overlay);
                Assert.Equal(VoiceUiPhase.Idle, KeyboardWtfState.UiPhase);
                var detail = (Label)typeof(VoiceOverlayForm).GetField("_detailLabel",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(overlay);
                Assert.Equal("Translating...", detail.Text);
                KeyboardWtfState.TryUpdateLongTaskUi("overlay-test", "Copying...");
                Assert.Equal("Copying...", detail.Text);
                KeyboardWtfState.ClearLongTaskUi("overlay-test");
                Assert.False(overlay.Visible);
                Assert.Equal(VoiceUiPhase.Idle, KeyboardWtfState.UiPhase);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                KeyboardWtfState.ClearLongTaskUi("overlay-test");
                KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
                completed.Set();
            }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
        uiThread.Join();
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(-96, 12)]
    [InlineData(-60, 12)]
    [InlineData(-30, 20)]
    [InlineData(0, 29)]
    [InlineData(10, 29)]
    public void Listening_inner_orb_preserves_the_existing_microphone_meter(double level, int expected)
    {
        Assert.Equal(expected, VoiceOverlayForm.InnerOrbDiameter(VoiceUiPhase.Listening, level, 0));
        Assert.Equal(expected, VoiceOverlayForm.InnerOrbDiameter(VoiceUiPhase.Listening, level, 600));
    }

    [Fact]
    public void Speaking_inner_orb_fluctuates_without_using_microphone_volume()
    {
        var frames = Enumerable.Range(0, 31)
            .Select(index => VoiceOverlayForm.InnerOrbDiameter(VoiceUiPhase.Speaking, -96, index * 80))
            .ToArray();
        Assert.True(frames.Distinct().Count() > 5);
        Assert.All(frames, size => Assert.InRange(size, 12, 29));
        Assert.Equal(12, VoiceOverlayForm.InnerOrbDiameter(VoiceUiPhase.Speaking, 0, 0));
        Assert.Equal(29, VoiceOverlayForm.InnerOrbDiameter(VoiceUiPhase.Speaking, -96, 600));
        Assert.Equal(12, VoiceOverlayForm.InnerOrbDiameter(VoiceUiPhase.Speaking, 0, 1200));
    }

    [Theory]
    [InlineData(VoiceUiPhase.Idle)]
    [InlineData(VoiceUiPhase.Thinking)]
    [InlineData(VoiceUiPhase.Executing)]
    [InlineData(VoiceUiPhase.Cancelled)]
    [InlineData(VoiceUiPhase.Done)]
    [InlineData(VoiceUiPhase.Error)]
    public void Non_speech_states_do_not_show_a_voice_activity_pulse(VoiceUiPhase phase) =>
        Assert.Equal(0, VoiceOverlayForm.InnerOrbDiameter(phase, 0, 600));

    [Fact]
    public void Reconnect_ui_revision_restores_only_when_no_newer_ui_owns_the_overlay()
    {
        var reconnectRevision = KeyboardWtfState.SetUi(
            VoiceUiPhase.Thinking,
            "Reconnecting",
            "Refreshing the secure Live connection...");

        Assert.True(KeyboardWtfState.TrySetUiIfRevision(
            reconnectRevision,
            VoiceUiPhase.Listening,
            "Jarvis",
            "Listening for your next request..."));
        Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);

        reconnectRevision = KeyboardWtfState.SetUi(
            VoiceUiPhase.Thinking,
            "Reconnecting",
            "Refreshing the secure Live connection...");
        KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "Jarvis", "Speaking...");

        Assert.False(KeyboardWtfState.TrySetUiIfRevision(
            reconnectRevision,
            VoiceUiPhase.Listening,
            "Jarvis",
            "Listening for your next request..."));
        Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);
        KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
    }

    [Fact]
    public void BackgroundStatusUpdatesReuseTheUiThreadWindowHandle()
    {
        Exception failure = null;
        var completed = new ManualResetEventSlim();
        var uiThread = new Thread(() =>
        {
            try
            {
                using var recorder = new AudioRecorderService();
                using var overlay = QuietFormFixture.CreateOverlay(recorder);
                overlay.InitializeUiThreadHandle();
                QuietFormFixture.AssertQuiet(overlay);
                var originalHandle = overlay.Handle;
                var uiThreadId = GetCurrentThreadId();
                Assert.Equal(uiThreadId, GetWindowThreadProcessId(originalHandle, IntPtr.Zero));

                var published = new ManualResetEventSlim();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    for (var index = 0; index < 250; index++)
                    {
                        KeyboardWtfState.SetUi(
                            index % 3 == 0 ? VoiceUiPhase.Listening
                                : index % 3 == 1 ? VoiceUiPhase.Speaking : VoiceUiPhase.Thinking,
                            "Jarvis",
                            $"background update {index}");
                    }
                    published.Set();
                });

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((!published.IsSet || !overlay.Visible) && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }

                Application.DoEvents();
                Assert.True(published.IsSet);
                Assert.True(overlay.Visible);
                QuietFormFixture.AssertQuiet(overlay);
                Assert.True(overlay.IsHandleCreated);
                Assert.Equal(originalHandle, overlay.Handle);
                Assert.Equal(uiThreadId, GetWindowThreadProcessId(overlay.Handle, IntPtr.Zero));
                var extendedStyle = GetWindowLongPtr(overlay.Handle, GwlExStyle).ToInt64();
                Assert.NotEqual(0, extendedStyle & WsExTopmost);
                Assert.NotEqual(0, extendedStyle & WsExNoActivate);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
                completed.Set();
            }
        });

        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
        uiThread.Join();
        Assert.Null(failure);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
