namespace KeyboardWtf.Tests;

using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using NAudio.Wave;
using Xunit;

[Collection("Live event integration")]
public sealed class PopupQuickActionTests
{
    [Fact]
    public void Owned_mute_is_immediately_visible_through_quiet_loud_and_replacement_epochs()
    {
        var presentation = new PopupQuickActionPresentation();
        var first = new PopupQuickAction(PopupQuickActionKind.Mute, "session-one", 1);
        foreach (var level in new[] { -96d, -10d, 0d, double.NaN, double.PositiveInfinity })
            Assert.Equal(first, presentation.Select(first, VoiceUiPhase.Listening, false, level, 0));
        var next = first with { Owner = "session-two" };
        Assert.Equal(next, presentation.Select(next, VoiceUiPhase.Listening, false, -10, 1));
        Assert.Equal(PopupQuickAction.Hidden, presentation.Select(PopupQuickAction.Hidden,
            VoiceUiPhase.Listening, false, -96, 2));
    }

    [Theory]
    [InlineData(VoiceUiPhase.Idle)]
    [InlineData(VoiceUiPhase.Listening)]
    [InlineData(VoiceUiPhase.Speaking)]
    [InlineData(VoiceUiPhase.Thinking)]
    [InlineData(VoiceUiPhase.Executing)]
    [InlineData(VoiceUiPhase.Transcribing)]
    [InlineData(VoiceUiPhase.Error)]
    [InlineData(VoiceUiPhase.Done)]
    [InlineData(VoiceUiPhase.Cancelled)]
    public void Both_owned_microphone_actions_survive_every_voice_phase(VoiceUiPhase phase)
    {
        var presentation = new PopupQuickActionPresentation();
        var mute = new PopupQuickAction(PopupQuickActionKind.Mute, "session", 3);
        Assert.Equal(mute, presentation.Select(mute, phase, false, 0, 0));
        var unmute = mute with { Kind = PopupQuickActionKind.Unmute, Revision = 4 };
        Assert.Equal(unmute, presentation.Select(unmute, phase, false, 0, 501));
    }

    [Theory]
    [InlineData(VoiceUiPhase.Thinking)]
    [InlineData(VoiceUiPhase.Executing)]
    public void Owned_connector_mute_remains_visible_during_input_without_replacing_its_task(VoiceUiPhase phase)
    {
        var presentation = new PopupQuickActionPresentation();
        var action = new PopupQuickAction(PopupQuickActionKind.Mute, "session", 2);
        Assert.Equal(action, presentation.Select(action, phase, true, 0, 0));
        Assert.Equal("Reading document…", VoiceOverlayForm.BuildDetail(phase, "Other detail",
            taskDetail: "Reading document…", microphoneMuted: true));
    }

    [Fact]
    public void Mouse_release_keeps_the_owner_that_was_displayed_on_press() => OnUiThread(() =>
    {
        using var button = new PopupQuickActionButton { Size = new Size(20, 20) };
        var pressed = new PopupQuickAction(PopupQuickActionKind.Done, "smart-writing", 7);
        PopupQuickAction invoked = null;
        button.ActionInvoked += value => invoked = value;
        button.Action = pressed;
        Mouse(button, "OnMouseDown");
        button.Action = pressed with { Revision = 8 };
        Mouse(button, "OnMouseUp");
        Assert.Equal(pressed, invoked);
        Assert.False(button.TabStop);
    });

    [Fact]
    public void Identical_immutable_action_does_not_repaint_on_each_overlay_tick() => OnUiThread(() =>
    {
        using var button = new PopupQuickActionButton { Size = new Size(20, 20) };
        _ = button.Handle;
        var invalidated = 0;
        button.Invalidated += (_, _) => invalidated++;
        var action = new PopupQuickAction(PopupQuickActionKind.Mute, "session", 4);
        button.Action = action;
        Assert.True(invalidated > 0);
        invalidated = 0;
        button.Action = action with { };
        Assert.Equal(0, invalidated);
        button.Action = action with { Revision = 5 };
        Assert.True(invalidated > 0);
    });

    [Fact]
    public void Quiet_same_phase_mute_and_unmute_repaint_the_orb_without_restarting_its_phase() => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var action = new PopupQuickAction(PopupQuickActionKind.Mute, "quiet-session", 1);
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => action, _ => Task.FromResult(true), () => true);
        overlay.InitializeUiThreadHandle();
        var orb = Field<Panel>(overlay, "_orb");
        var oldLevel = KeyboardWtfState.InputLevelDb;
        try
        {
            KeyboardWtfState.InputLevelDb = -96;
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            Refresh(overlay); orb.Update();
            var phaseStarted = Field<long>(overlay, "_orbPhaseStarted");
            var listeningColor = Field<Color>(overlay, "_orbColor");
            var invalidated = 0;
            orb.Invalidated += (_, _) => invalidated++;
            action = action with { Kind = PopupQuickActionKind.Unmute, Revision = 2 };
            Refresh(overlay);
            Assert.True(invalidated > 0);
            Assert.NotEqual(listeningColor, Field<Color>(overlay, "_orbColor"));
            Assert.Equal(phaseStarted, Field<long>(overlay, "_orbPhaseStarted"));
            Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);
            Assert.Equal(-96, KeyboardWtfState.InputLevelDb);
            orb.Update(); invalidated = 0;
            action = action with { Kind = PopupQuickActionKind.Mute, Revision = 3 };
            Refresh(overlay);
            Assert.True(invalidated > 0);
            Assert.Equal(listeningColor, Field<Color>(overlay, "_orbColor"));
            Assert.Equal(phaseStarted, Field<long>(overlay, "_orbPhaseStarted"));
        }
        finally
        {
            KeyboardWtfState.InputLevelDb = oldLevel;
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Theory]
    [InlineData(1)] // Mute.
    [InlineData(2)] // Unmute.
    [InlineData(3)] // Done.
    public void Accessible_default_action_invokes_current_displayed_owner_once_without_focus(int kindValue) => OnUiThread(() =>
    {
        var kind = (PopupQuickActionKind)kindValue;
        using var button = new PopupQuickActionButton { Size = new Size(20, 20) };
        _ = button.Handle;
        button.Action = new(kind, "accessible-owner", 7);
        var calls = new List<PopupQuickAction>();
        button.ActionInvoked += calls.Add;
        var accessible = button.AccessibilityObject;
        Assert.Equal(AccessibleRole.PushButton, accessible.Role);
        Assert.Equal(button.AccessibleName, accessible.Name);
        Assert.Equal(button.AccessibleName, accessible.DefaultAction);
        accessible.DoDefaultAction();
        Assert.Equal(button.Action, Assert.Single(calls));
        Assert.False(button.Focused); Assert.False(button.TabStop);
    });

    [Fact]
    public void Accessible_default_action_fails_closed_for_hidden_disabled_or_disposed_controls() => OnUiThread(() =>
    {
        using var button = new PopupQuickActionButton { Size = new Size(20, 20) };
        _ = button.Handle;
        var calls = 0;
        button.ActionInvoked += _ => calls++;
        var accessible = button.AccessibilityObject;
        accessible.DoDefaultAction(); // Hidden action.
        button.Action = new(PopupQuickActionKind.Done, "smart-writing", 7);
        button.Enabled = false; accessible.DoDefaultAction();
        button.Enabled = true; button.Visible = false; accessible.DoDefaultAction();
        button.Visible = true; button.Dispose(); accessible.DoDefaultAction();
        Assert.Equal(0, calls);
    });

    [Fact]
    public void Accessible_background_invocation_stays_on_UI_thread_and_cannot_borrow_a_new_owner() => OnUiThread(() =>
    {
        using var button = new PopupQuickActionButton { Size = new Size(20, 20) };
        _ = button.Handle;
        var oldAction = new PopupQuickAction(PopupQuickActionKind.Done, "smart-writing", 7);
        var nextAction = oldAction with { Revision = 8 };
        button.Action = oldAction;
        var accessible = button.AccessibilityObject;
        var calls = new List<PopupQuickAction>();
        var uiThreadId = Environment.CurrentManagedThreadId;
        button.ActionInvoked += action => { Assert.Equal(uiThreadId, Environment.CurrentManagedThreadId); calls.Add(action); };
        Assert.True(Task.Run(accessible.DoDefaultAction).Wait(TimeSpan.FromSeconds(5)));
        button.Action = nextAction;
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        button.BeginInvoke((Action)(() => drained.TrySetResult(true)));
        PumpUntil(() => drained.Task.IsCompleted);
        Assert.Empty(calls);
        Assert.True(Task.Run(accessible.DoDefaultAction).Wait(TimeSpan.FromSeconds(5)));
        PumpUntil(() => calls.Count > 0);
        Assert.Equal(nextAction, Assert.Single(calls));
        Assert.False(button.Focused);
    });

    [Theory]
    [InlineData(120, "PerMonitorV2")]
    [InlineData(144, "PerMonitorV2")]
    [InlineData(192, "PerMonitorV2")]
    [InlineData(0, "SystemAware")]
    public async Task Actual_form_keeps_legacy_absolute_bounds_in_native_and_per_monitor_DPI_hosts(
        int targetDpi, string hostMode)
    {
        var repository = FindRepository();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var executable = Path.Combine(repository, "tools", "KeyboardWtf.PopupDpiFixture", "bin",
            configuration, "net8.0-windows", "KeyboardWtf.PopupDpiFixture.exe");
        Assert.True(File.Exists(executable), "The separately built DPI fixture is missing.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable),
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(PopupQuickActionTests).Assembly.Location);
        start.ArgumentList.Add(targetDpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(hostMode);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            // Only this exact, inert fixture child is owned by this test.
            // Never enumerate or stop a desktop application process.
            process.Kill(entireProcessTree: false);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
            throw;
        }
        var diagnostics = await output + await error;
        Assert.True(process.ExitCode == 0, diagnostics);
        Assert.Contains($"POPUP_DPI_PASS|{hostMode}|{targetDpi}", diagnostics);
        Assert.Contains($"POPUP_DPI_HOST|{hostMode}|", diagnostics);
        var evidenceDirectory = Environment.GetEnvironmentVariable("KEYBOARD_WTF_UI_EVIDENCE_DIR");
        if (!string.IsNullOrEmpty(evidenceDirectory))
        {
            Directory.CreateDirectory(evidenceDirectory);
            File.WriteAllText(Path.Combine(evidenceDirectory, $"quick-action-fixed-dpi-{hostMode}-{targetDpi}.log"), diagnostics);
        }
    }

    // Invoked only by the inert .NET 8 child after it sets process DPI awareness.
    // This is the same actual overlay/assertion body, not a second UI implementation.
    internal static void RunDpiFixtureAssertions(int targetDpi, string hostMode) => OnUiThread(() =>
    {
        var systemAware = hostMode == "SystemAware";
        Assert.Equal(systemAware ? HighDpiMode.SystemAware : HighDpiMode.PerMonitorV2, Application.HighDpiMode);
        using var recorder = new AudioRecorderService();
        var action = new PopupQuickAction(PopupQuickActionKind.Unmute, "dpi-fixture", 1);
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => action, _ => Task.FromResult(true), () => true);
        var button = Field<PopupQuickActionButton>(overlay, "_quickActionButton");
        var title = Field<Label>(overlay, "_statusLabel");
        var detail = Field<ConnectorActivityLabel>(overlay, "_detailLabel");
        var mark = Field<PictureBox>(overlay, "_providerIcon");
        AssertGeometry(overlay, button, title, detail);
        overlay.InitializeUiThreadHandle();
        Assert.True(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(overlay.Handle),
            new IntPtr(systemAware ? -2 : -4)));
        AssertGeometry(overlay, button, title, detail);
        var initialDpi = overlay.DeviceDpi;
        if (systemAware) Assert.Equal((int)GetDpiForSystem(), initialDpi);
        Console.WriteLine($"POPUP_DPI_HOST|{hostMode}|{initialDpi}|client=460x82|button=20x20");
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("quick-action-dpi-message", "Reading Gmail…", 0, "gmail"));
            Refresh(overlay);
            AssertGeometry(overlay, button, title, detail);
            Assert.Equal(new Size(detail.Height, detail.Height), mark.Size);
            Assert.True(mark.Right < detail.Left);
            Assert.Equal("Reading Gmail…", detail.Text);
            Capture(overlay, $"quick-action-fixed-native-{hostMode}-{initialDpi}.png");
            KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Reconnecting", "Connecting…");
            KeyboardWtfState.TryUpdateLongTaskUi("quick-action-dpi-message", "Thinking…");
            Refresh(overlay);
            Capture(overlay, $"connector-reconnecting-{hostMode}-{initialDpi}.png");
            AssertAllTextRowsVisible(title);
            AssertAllTextRowsVisible(detail);
            KeyboardWtfState.TryUpdateLongTaskUi("quick-action-dpi-message", "Reading Gmail…");
            Refresh(overlay);
            if (!systemAware)
            {
                // Deliberately offer enlarged suggested rectangles. Neither the
                // original footprint nor its child geometry/font points may
                // grow, even after repeated different and round-trip messages.
                var sequence = new[] { targetDpi, 96, 192, 120, 144, initialDpi };
                for (var index = 0; index < sequence.Length; index++)
                {
                    var dpi = sequence[index];
                    DispatchDpiChange(overlay, dpi, new Rectangle(overlay.Location,
                        new Size((int)Math.Round(460 * dpi / 96d), (int)Math.Round(82 * dpi / 96d))));
                    Refresh(overlay);
                    Assert.Equal(dpi, overlay.DeviceDpi);
                    AssertGeometry(overlay, button, title, detail);
                    Assert.Equal("Reading Gmail…", detail.Text);
                    Capture(overlay, $"quick-action-fixed-dpi-{targetDpi}-{index}-{dpi}.png");
                }
            }
        }
        finally
        {
            KeyboardWtfState.ClearLongTaskUi("quick-action-dpi-message");
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Fact]
    public void Actual_popup_keeps_original_footprint_and_top_right_action_through_all_owned_phases() => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var candidate = new PopupQuickAction(PopupQuickActionKind.Mute, "fixture-session", 1);
        var enabled = true;
        long now = 0;
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => candidate, _ => Task.FromResult(true), () => enabled, () => now);
        var button = Field<PopupQuickActionButton>(overlay, "_quickActionButton");
        var title = Field<Label>(overlay, "_statusLabel");
        var detail = Field<ConnectorActivityLabel>(overlay, "_detailLabel");
        AssertGeometry(overlay, button, title, detail);
        overlay.InitializeUiThreadHandle();
        AssertGeometry(overlay, button, title, detail);
        var mark = Field<PictureBox>(overlay, "_providerIcon");
        var oldLevel = KeyboardWtfState.InputLevelDb;
        try
        {
            KeyboardWtfState.InputLevelDb = -96;
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening for your next request…");
            now = 300; Refresh(overlay);
            Assert.True(button.Visible);
            Assert.Equal(PopupQuickActionKind.Mute, button.Action.Kind);
            var reservedWidth = title.Width;
            AssertGeometry(overlay, button, title, detail);
            Capture(overlay, "quick-action-fixed-mute.png");

            KeyboardWtfState.InputLevelDb = -10; Refresh(overlay);
            Assert.True(button.Visible); Assert.Equal(reservedWidth, title.Width);
            AssertGeometry(overlay, button, title, detail);
            Capture(overlay, "quick-action-fixed-speaking-input.png");

            foreach (var phase in new[] { VoiceUiPhase.Listening, VoiceUiPhase.Speaking,
                VoiceUiPhase.Thinking, VoiceUiPhase.Executing, VoiceUiPhase.Transcribing,
                VoiceUiPhase.Error, VoiceUiPhase.Done, VoiceUiPhase.Cancelled })
            {
                var revision = KeyboardWtfState.SetUi(phase, "Jarvis", "Original voice detail…");
                Refresh(overlay);
                Assert.True(overlay.Visible); Assert.True(button.Visible);
                Assert.Equal(PopupQuickActionKind.Mute, button.Action.Kind);
                Assert.Equal(phase, KeyboardWtfState.UiPhase);
                AssertGeometry(overlay, button, title, detail);
                Assert.True(KeyboardWtfState.TrySetUiIfRevision(revision, phase, "Jarvis", "Original voice detail…"));
                Capture(overlay, $"quick-action-fixed-mute-{phase}.png");
            }

            candidate = PopupQuickAction.Hidden;
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
            Assert.False(overlay.Visible); Assert.False(button.Visible);
            KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "Jarvis", "Speaking…");
            Assert.False(button.Visible); Assert.Equal(reservedWidth, title.Width);
            Capture(overlay, "quick-action-fixed-owner-ended-speaking.png");
            KeyboardWtfState.SetUi(VoiceUiPhase.Transcribing, "Smart writing", "Understanding your recording…");
            Assert.False(button.Visible);
            Capture(overlay, "quick-action-fixed-smart-writing-processing.png");
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Dictation", "Recording…");
            Assert.False(button.Visible);
            Capture(overlay, "quick-action-fixed-dictation.png");

            candidate = new(PopupQuickActionKind.Done, "smart-writing", 12);
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Smart writing", "Recording…");
            Assert.True(button.Visible); Assert.Equal(PopupQuickActionKind.Done, button.Action.Kind);
            AssertGeometry(overlay, button, title, detail);
            Capture(overlay, "quick-action-fixed-done.png");

            candidate = new(PopupQuickActionKind.Unmute, "fixture-session", 2);
            foreach (var phase in new[] { VoiceUiPhase.Listening, VoiceUiPhase.Speaking,
                VoiceUiPhase.Thinking, VoiceUiPhase.Executing, VoiceUiPhase.Transcribing,
                VoiceUiPhase.Error, VoiceUiPhase.Done, VoiceUiPhase.Cancelled })
            {
                var revision = KeyboardWtfState.SetUi(phase, "Jarvis", "Original voice detail…");
                Refresh(overlay);
                Assert.True(overlay.Visible); Assert.True(button.Visible);
                AssertGeometry(overlay, button, title, detail);
                Assert.Equal("Microphone muted", detail.Text); Assert.Equal(phase, KeyboardWtfState.UiPhase);
                Assert.True(KeyboardWtfState.TrySetUiIfRevision(revision, phase, "Jarvis", "Original voice detail…"));
                Capture(overlay, $"quick-action-fixed-unmute-{phase}.png");
            }
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("quick-action-render", "Reading a long Gmail thread…", 0, "gmail"));
            Assert.Equal("Reading a long Gmail thread…", detail.Text);
            Assert.True(mark.Visible); Assert.True(mark.Right < detail.Left);
            AssertGeometry(overlay, button, title, detail);
            Capture(overlay, "quick-action-fixed-connector-muted.png");
            enabled = false; Refresh(overlay);
            Assert.False(button.Visible); Assert.True(title.Width > reservedWidth);
            AssertGeometry(overlay, button, title, detail, reserveAction: false);
            Assert.Equal("Reading a long Gmail thread…", detail.Text);
            Assert.Equal(PopupQuickActionKind.Unmute, candidate.Kind);
            Capture(overlay, "quick-action-fixed-off.png");
            enabled = true; Refresh(overlay);
            Assert.True(button.Visible); AssertGeometry(overlay, button, title, detail);
            KeyboardWtfState.ClearLongTaskUi("quick-action-render");
            candidate = PopupQuickAction.Hidden;
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
            Assert.False(button.Visible); Assert.False(overlay.Visible);
        }
        finally
        {
            KeyboardWtfState.ClearLongTaskUi("quick-action-render");
            KeyboardWtfState.InputLevelDb = oldLevel;
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Theory]
    [InlineData(false, VoiceUiPhase.Idle)]
    [InlineData(false, VoiceUiPhase.Done)]
    [InlineData(false, VoiceUiPhase.Error)]
    [InlineData(false, VoiceUiPhase.Cancelled)]
    [InlineData(true, VoiceUiPhase.Idle)]
    [InlineData(true, VoiceUiPhase.Done)]
    [InlineData(true, VoiceUiPhase.Error)]
    [InlineData(true, VoiceUiPhase.Cancelled)]
    public void Task_owner_clears_its_decoration_without_dismissing_a_continuing_live_session(
        bool muted, VoiceUiPhase phase) => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var action = new PopupQuickAction(muted ? PopupQuickActionKind.Unmute : PopupQuickActionKind.Mute,
            "continuing-live-session", 5);
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => action, showQuickAction: () => true);
        overlay.InitializeUiThreadHandle();
        var button = Field<PopupQuickActionButton>(overlay, "_quickActionButton");
        var detail = Field<ConnectorActivityLabel>(overlay, "_detailLabel");
        var mark = Field<PictureBox>(overlay, "_providerIcon");
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("ending-connector-ui", "Thinking…", 0, "gmail"));
            Assert.True(KeyboardWtfState.TryUpdateLongTaskUi("ending-connector-ui", "Thinking…", connectorBetweenSteps: true));
            KeyboardWtfState.SetUi(phase, "Jarvis", "Ready");
            typeof(KeyboardWtfState).GetProperty("UiPhaseStartedUtc").SetValue(null, DateTime.UtcNow.AddSeconds(-10));
            Refresh(overlay);
            Assert.True(overlay.Visible); Assert.True(button.Visible);
            Assert.True(mark.Visible); Assert.True(detail.Shiny); Assert.Equal("Thinking…", detail.Text);
            Assert.True(KeyboardWtfState.TryTransferConnectorTaskUi("ending-connector-ui",
                "replacement-connector-ui", "Reading thread…", "gmail"));
            Assert.False(KeyboardWtfState.ClearLongTaskUi("ending-connector-ui"));
            Assert.True(overlay.Visible); Assert.Equal("Reading thread…", detail.Text);
            Assert.True(KeyboardWtfState.ClearLongTaskUi("replacement-connector-ui"));
            Assert.True(overlay.Visible); Assert.True(button.Visible);
            Assert.False(mark.Visible); Assert.False(detail.Shiny); Assert.False(detail.AnimationRunning);
            Assert.Equal(action, button.Action);
            Assert.Equal(phase, KeyboardWtfState.UiPhase);
            action = PopupQuickAction.Hidden; // Actual session end releases the view.
            Refresh(overlay);
            Assert.False(overlay.Visible); Assert.False(button.Visible);
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            Assert.True(overlay.Visible); Assert.False(button.Visible); Assert.Equal(action, button.Action);
        }
        finally
        {
            KeyboardWtfState.ClearLongTaskUi("ending-connector-ui");
            KeyboardWtfState.ClearLongTaskUi("replacement-connector-ui");
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Live_owner_keeps_photo_done_visible_without_mutating_phase_or_restarting_work(
        bool muted, bool showButton) => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var action = new PopupQuickAction(muted ? PopupQuickActionKind.Unmute : PopupQuickActionKind.Mute,
            "photo-live-session", 17);
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => action, showQuickAction: () => showButton);
        overlay.InitializeUiThreadHandle();
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Executing, "Camera ready", "Capturing one approved photo…");
            var revision = KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Photo saved", "Fixture photo path");
            typeof(KeyboardWtfState).GetProperty("UiPhaseStartedUtc").SetValue(null, DateTime.UtcNow.AddSeconds(-10));
            Refresh(overlay);
            Assert.True(overlay.Visible);
            Assert.Equal(showButton, Field<PopupQuickActionButton>(overlay, "_quickActionButton").Visible);
            Assert.Equal(VoiceUiPhase.Done, KeyboardWtfState.UiPhase);
            Assert.Equal(revision, KeyboardWtfState.UiRevision);
            Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
            QuietFormFixture.AssertQuiet(overlay);
            action = PopupQuickAction.Hidden;
            Refresh(overlay);
            Assert.False(overlay.Visible);
            Assert.Equal(VoiceUiPhase.Done, KeyboardWtfState.UiPhase);
            Assert.Equal(revision, KeyboardWtfState.UiRevision);
        }
        finally { KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready"); }
    });

    [Fact]
    public void Original_capture_and_awaited_polish_keep_view_owned_without_borrowing_live_lifetime() => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var action = new PopupQuickAction(PopupQuickActionKind.Done, "smart-writing", 23);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => action, _ => completion.Task, () => true);
        overlay.InitializeUiThreadHandle();
        Field<System.Windows.Forms.Timer>(overlay, "_timer").Stop();
        var button = Field<PopupQuickActionButton>(overlay, "_quickActionButton");
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "Smart writing", "Recording…");
            Assert.True(overlay.Visible); Assert.True(button.Visible);
            Click(button);
            action = PopupQuickAction.Hidden;
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "Smart writing", "Polishing…");
            Assert.True(overlay.Visible); Assert.False(button.Visible);
            Assert.False(completion.Task.IsCompleted);
            completion.TrySetResult(true);
            PumpUntil(() => Field<PopupQuickAction>(overlay, "_pendingCompletionAction") is null);
            Assert.False(overlay.Visible);
        }
        finally
        {
            completion.TrySetResult(true);
            PumpUntil(() => Field<PopupQuickAction>(overlay, "_pendingCompletionAction") is null);
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Fact]
    public void Unmute_remains_clickable_while_the_prior_mute_wire_flush_is_pending() => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var candidate = new PopupQuickAction(PopupQuickActionKind.Mute, "session", 1);
        var flush = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<PopupQuickAction>();
        long now = 0;
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => candidate, action =>
        {
            calls.Add(action);
            if (action.Kind == PopupQuickActionKind.Mute)
            {
                candidate = action with { Kind = PopupQuickActionKind.Unmute, Revision = 2 };
                return flush.Task;
            }
            candidate = action with { Kind = PopupQuickActionKind.Mute, Revision = 3 };
            return Task.FromResult(true);
        }, () => true, () => now);
        overlay.InitializeUiThreadHandle();
        var oldLevel = KeyboardWtfState.InputLevelDb;
        try
        {
            KeyboardWtfState.InputLevelDb = -96;
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            now = 300; Refresh(overlay);
            Click(Field<PopupQuickActionButton>(overlay, "_quickActionButton"));
            KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Jarvis", "Working…");
            var button = Field<PopupQuickActionButton>(overlay, "_quickActionButton");
            Assert.True(button.Visible); Assert.True(button.Enabled);
            Assert.Equal(PopupQuickActionKind.Unmute, button.Action.Kind);
            Click(button);
            Assert.Equal(new[] { PopupQuickActionKind.Mute, PopupQuickActionKind.Unmute }, calls.Select(action => action.Kind));
            Assert.False(flush.Task.IsCompleted);
        }
        finally
        {
            flush.TrySetResult(true); PumpUntil(() => flush.Task.IsCompleted);
            KeyboardWtfState.InputLevelDb = oldLevel;
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Fact]
    public void Pending_done_cannot_block_replacement_live_or_clear_a_new_done_owner() => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var first = new PopupQuickAction(PopupQuickActionKind.Done, "smart-writing", 7);
        var second = first with { Revision = 8 };
        var live = new PopupQuickAction(PopupQuickActionKind.Unmute, "replacement-live", 1);
        var candidate = first;
        var oldPolish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newPolish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<PopupQuickAction>();
        long refreshes = 0;
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => candidate, action =>
        {
            calls.Add(action);
            return action == first ? oldPolish.Task : action == second ? newPolish.Task : Task.FromResult(true);
        }, () => true, () => ++refreshes);
        overlay.InitializeUiThreadHandle();
        // Make completion's finally/refresh the sole cause of the later count.
        Field<System.Windows.Forms.Timer>(overlay, "_timer").Stop();
        var button = Field<PopupQuickActionButton>(overlay, "_quickActionButton");
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Smart writing", "Recording…");
            Click(button);
            Assert.False(button.Enabled);
            Click(button); Assert.Equal(first, Assert.Single(calls));

            candidate = live;
            KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Jarvis", "Working…");
            Assert.True(button.Visible); Assert.True(button.Enabled);
            Click(button);
            Assert.Equal(live, calls[1]);
            Assert.False(oldPolish.Task.IsCompleted);

            candidate = second;
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Smart writing", "Recording…");
            Assert.True(button.Visible); Assert.True(button.Enabled);
            Click(button);
            Assert.False(button.Enabled);
            Assert.Equal(second, Field<PopupQuickAction>(overlay, "_pendingCompletionAction"));
            var beforeOldCompletion = refreshes;
            oldPolish.TrySetResult(true);
            PumpUntil(() => refreshes > beforeOldCompletion);
            Assert.Equal(second, Field<PopupQuickAction>(overlay, "_pendingCompletionAction"));
            Assert.False(button.Enabled);
            Click(button); Assert.Equal(new[] { first, live, second }, calls);

            newPolish.TrySetResult(true);
            PumpUntil(() => Field<PopupQuickAction>(overlay, "_pendingCompletionAction") is null);
            Assert.True(button.Enabled);
        }
        finally
        {
            oldPolish.TrySetResult(true); newPolish.TrySetResult(true);
            PumpUntil(() => Field<PopupQuickAction>(overlay, "_pendingCompletionAction") is null);
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Fact]
    public void Disposing_popup_during_original_smart_writing_polish_does_not_cancel_or_duplicate_completion() => OnUiThread(() =>
    {
        var input = new FakeInput();
        using var recorder = new AudioRecorderService(_ => input);
        var gate = new UpdateWorkAdmissionGate();
        var capture = new VoiceCaptureService(recorder, null, new NotificationService(null), null, gate);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> completion = null;
        var calls = 0;
        var oldRecording = KeyboardWtfState.IsRecording;
        var oldProcessing = KeyboardWtfState.IsProcessing;
        var oldAi = KeyboardWtfState.IsProcessingAi;
        var oldMode = KeyboardWtfState.CurrentRecordingMode;
        Set(capture, "_operationId", 37);
        Set(capture, "_recordingStartUtc", DateTime.UtcNow);
        Set(capture, "_audioCompletionHandler", (Func<SmartWritingAudioCapture, Task>)(async audio =>
        {
            calls++; Assert.True(audio.WavData.Length >= 44);
            entered.TrySetResult(true); await release.Task;
        }));
        var overlay = QuietFormFixture.CreateOverlay(recorder,
            () => capture.SmartWritingCompletionOperationId is int id
                ? new(PopupQuickActionKind.Done, "smart-writing", id) : PopupQuickAction.Hidden,
            action => completion = capture.TryCompleteSmartWritingAsync((int)action.Revision), () => true);
        try
        {
            KeyboardWtfState.IsProcessingAi = false; KeyboardWtfState.IsProcessing = false;
            recorder.StartRecording(); input.Emit(new byte[1600]);
            KeyboardWtfState.IsRecording = true; KeyboardWtfState.CurrentRecordingMode = RecordingMode.VoiceAssistant;
            overlay.InitializeUiThreadHandle();
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Smart writing", "Recording…");
            Click(Field<PopupQuickActionButton>(overlay, "_quickActionButton"));
            PumpUntil(() => input.StopCalls == 1); input.Finish();
            PumpUntil(() => entered.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            overlay.Dispose();
            Assert.True(KeyboardWtfState.IsProcessing);
            Assert.Equal(1, calls); Assert.Equal(1, input.StopCalls);
            release.TrySetResult(true); PumpUntil(() => completion.IsCompleted);
            Assert.True(completion.GetAwaiter().GetResult()); Assert.False(KeyboardWtfState.IsProcessing);
            Assert.Equal(1, calls); Assert.Equal(1, input.StopCalls); Assert.Equal(1, input.StartCalls);
        }
        finally
        {
            input.Finish(); release.TrySetResult(true);
            if (completion is not null) PumpUntil(() => completion.IsCompleted);
            overlay.Dispose(); capture.Cancel();
            KeyboardWtfState.IsRecording = oldRecording; KeyboardWtfState.IsProcessing = oldProcessing;
            KeyboardWtfState.IsProcessingAi = oldAi; KeyboardWtfState.CurrentRecordingMode = oldMode;
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    private static void AssertGeometry(VoiceOverlayForm overlay, Control button, Control title,
        Control detail, bool reserveAction = true)
    {
        Assert.Equal(AutoScaleMode.None, overlay.AutoScaleMode);
        Assert.Equal(new Size(460, 82), overlay.ClientSize);
        Assert.Equal(new Rectangle(16, 19, 42, 42), Field<Panel>(overlay, "_orb").Bounds);
        Assert.Equal(new Rectangle(430, 8, 20, 20), button.Bounds);
        Assert.Equal(new Rectangle(74, 13, reserveAction ? 350 : 382, 26), title.Bounds);
        Assert.Equal(40, detail.Top); Assert.Equal(24, detail.Height); Assert.Equal(456, detail.Right);
        var mark = Field<PictureBox>(overlay, "_providerIcon");
        Assert.Equal(mark.Visible ? 104 : 74, detail.Left);
        if (mark.Visible)
        {
            Assert.Equal(new Rectangle(74, 40, 24, 24), mark.Bounds);
            Assert.True(mark.Right < detail.Left);
        }
        Assert.Equal(11f, title.Font.SizeInPoints); Assert.True(title.Font.Bold);
        Assert.Equal(9f, detail.Font.SizeInPoints);
        Assert.Equal("Segoe UI", title.Font.Name); Assert.Equal("Segoe UI", detail.Font.Name);
        Assert.False(button.Bounds.IntersectsWith(detail.Bounds));
        if (reserveAction) Assert.Equal(6, button.Left - title.Right);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private static void Refresh(VoiceOverlayForm overlay)
    {
        typeof(VoiceOverlayForm).GetMethod("RefreshState", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(overlay, null);
        QuietFormFixture.AssertQuiet(overlay);
    }
    private static void Click(PopupQuickActionButton button) { Mouse(button, "OnMouseDown"); Mouse(button, "OnMouseUp"); }
    private static void Mouse(PopupQuickActionButton button, string method) => typeof(PopupQuickActionButton).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(button, [new MouseEventArgs(MouseButtons.Left, 1, button.Width / 2, button.Height / 2, 0)]);
    private static void Capture(Form form, string name)
    {
        QuietFormFixture.AssertQuiet(form);
        var directory = Environment.GetEnvironmentVariable("KEYBOARD_WTF_UI_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, form.ClientRectangle);
        image.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
    }
    private static void AssertAllTextRowsVisible(Label label)
    {
        using Label reference = label is ConnectorActivityLabel ? new ConnectorActivityLabel { Shiny = true } : new Label();
        reference.AutoSize = false; reference.Size = new Size(label.Width, 100);
        reference.Font = label.Font; reference.Text = label.Text;
        reference.BackColor = Color.FromArgb(18, 18, 18); reference.ForeColor = label.ForeColor;
        reference.CreateControl();
        using var actual = new Bitmap(label.Width, label.Height);
        using var expected = new Bitmap(reference.Width, reference.Height);
        label.DrawToBitmap(actual, label.ClientRectangle);
        reference.DrawToBitmap(expected, reference.ClientRectangle);
        static int Span(Bitmap image)
        {
            var rows = Enumerable.Range(0, image.Height).Where(y => Enumerable.Range(0, image.Width).Any(x =>
            { var c = image.GetPixel(x, y); return c.R > 90 && c.G > 90 && c.B > 90; })).ToArray();
            Assert.NotEmpty(rows); return rows.Last() - rows.First();
        }
        Assert.InRange(Span(expected) - Span(actual), -1, 1);
    }
    private static void DispatchDpiChange(Form form, int dpi, Rectangle suggested)
    {
        // Mirror the PMv2 child-before / parent / child-after message sequence.
        // WinForms explicitly accepts test DPI in BEFOREPARENT wParam, allowing
        // child notifications without changing a Windows monitor setting. The
        // fixed legacy overlay must reject suggested container growth and keep
        // its explicit child/font geometry despite this adversarial sequence.
        // This exercises real window-message handling, not physical-DPI proof.
        var children = form.Controls.Cast<Control>().ToArray();
        foreach (var child in children.Reverse())
            SendMessage(child.Handle, 0x02E2, new IntPtr(dpi), IntPtr.Zero);
        var rectangle = Marshal.AllocHGlobal(sizeof(int) * 4);
        try
        {
            Marshal.WriteInt32(rectangle, 0, suggested.Left);
            Marshal.WriteInt32(rectangle, 4, suggested.Top);
            Marshal.WriteInt32(rectangle, 8, suggested.Right);
            Marshal.WriteInt32(rectangle, 12, suggested.Bottom);
            SendMessage(form.Handle, 0x02E0, new IntPtr(dpi | dpi << 16), rectangle);
            foreach (var child in children)
                SendMessage(child.Handle, 0x02E3, IntPtr.Zero, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(rectangle); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
    private static string FindRepository()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "tools", "KeyboardWtf.PopupDpiFixture", "KeyboardWtf.PopupDpiFixture.csproj")))
                return current.FullName;
        throw new InvalidOperationException("The inert DPI fixture project was not found in this checkout.");
    }
    private static void PumpUntil(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents(); Assert.True(completed());
    }
    private static void OnUiThread(Action action)
    {
        Exception failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } finally { done.Set(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(20))); thread.Join(); Assert.Null(failure);
    }
    private sealed class FakeInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 1);
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        internal int StartCalls;
        internal int StopCalls;
        public void StartRecording() => Interlocked.Increment(ref StartCalls);
        public void StopRecording() => Interlocked.Increment(ref StopCalls);
        public void Dispose() { }
        internal void Emit(byte[] data) => DataAvailable?.Invoke(this, new(data, data.Length));
        internal void Finish() => RecordingStopped?.Invoke(this, new());
    }
}
