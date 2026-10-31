namespace KeyboardWtf.Tests;

using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorActivityUiTests
{
    [Fact]
    public void Resolve_handoff_preserves_display_clock_and_does_not_hold_the_voice_response_health_gate()
    {
        try
        {
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("resolve-owner", "Checking connection…", 500, "gmail"));
            var clock = typeof(KeyboardWtfState).GetField("_longTaskStartedTimestamp", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.False(KeyboardWtfState.TryTransferConnectorTaskUi("wrong-owner", "task-owner", "Thinking…", "gmail"));
            Assert.True(KeyboardWtfState.TryTransferConnectorTaskUi("resolve-owner", "task-owner", "Thinking…", "gmail"));
            Assert.Equal(clock, typeof(KeyboardWtfState).GetField("_longTaskStartedTimestamp", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null));
            Assert.False(KeyboardWtfState.ClearLongTaskUi("resolve-owner"));
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            Assert.Equal(("Thinking…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(1)));
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("task-owner", "Reading email…", 500, "gmail"));
            Assert.Equal(clock, typeof(KeyboardWtfState).GetField("_longTaskStartedTimestamp", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null));
        }
        finally { KeyboardWtfState.ClearLongTaskUi("resolve-owner"); KeyboardWtfState.ClearLongTaskUi("task-owner"); }
    }
    [Fact]
    public void Decoration_obeys_the_existing_delay_and_owner_without_changing_voice_state()
    {
        var revision = KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
        try
        {
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("connector-ui", "Searching Gmail…", 500, "gmail"));
            Assert.Equal(("", ""), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromMilliseconds(499)));
            Assert.Equal(("Searching Gmail…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromMilliseconds(500)));
            Assert.False(KeyboardWtfState.TryBeginLongTaskUiIfIdle("other-owner", "Searching Slack…", 0, "slack"));
            Assert.False(KeyboardWtfState.ClearLongTaskUi("other-owner"));
            Assert.True(KeyboardWtfState.TryUpdateLongTaskUi("connector-ui", "Reading thread…"));
            Assert.Equal(("Reading thread…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(1)));
            Assert.True(KeyboardWtfState.TrySetUiIfRevision(revision, VoiceUiPhase.Speaking, "Jarvis", "Speaking…"));
            Assert.True(KeyboardWtfState.ClearLongTaskUi("connector-ui"));
            Assert.Equal(("", ""), KeyboardWtfState.LongTaskDisplayState());
            Assert.Equal(VoiceUiPhase.Speaking, KeyboardWtfState.UiPhase);
        }
        finally { KeyboardWtfState.ClearLongTaskUi("connector-ui"); KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready"); }
    }

    [Fact]
    public void Translation_replacement_does_not_inherit_a_provider_or_shine()
    {
        try
        {
            KeyboardWtfState.TryBeginLongTaskUiIfIdle("connector-ui", "Searching Gmail…", 0, "gmail");
            KeyboardWtfState.BeginLongTaskUi("translation-ui", "Translating…");
            Assert.Equal(("Translating…", ""), KeyboardWtfState.LongTaskDisplayState());
            Assert.False(KeyboardWtfState.ClearLongTaskUi("connector-ui"));
        }
        finally { KeyboardWtfState.ClearLongTaskUi("connector-ui"); KeyboardWtfState.ClearLongTaskUi("translation-ui"); }
    }

    [Fact]
    public void All_registry_provider_marks_are_bundled_and_decodable_without_network()
    {
        foreach (var name in new[] { "gmail", "googlecalendar", "googledrive", "googledocs", "googleforms", "googlemeet",
            "youtube", "github", "slack", "discord", "reddit", "linkedin" })
        {
            using var stream = typeof(VoiceOverlayForm).Assembly.GetManifestResourceStream("icons/connectors/" + name + ".png");
            Assert.NotNull(stream);
            using var image = Image.FromStream(stream);
            Assert.Equal(new Size(48, 48), image.Size);
        }
    }

    [Fact]
    public void Native_status_keeps_icon_and_shine_through_speech_interruption_and_restores_normal_ui() => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        using var overlay = QuietFormFixture.CreateOverlay(recorder);
        overlay.InitializeUiThreadHandle();
        var label = Field<ConnectorActivityLabel>(overlay, "_detailLabel");
        var icon = Field<PictureBox>(overlay, "_providerIcon");
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            var normalColor = Field<Color>(overlay, "_orbColor");
            KeyboardWtfState.TryBeginLongTaskUiIfIdle("connector-ui", "Searching Gmail…", 0, "gmail");
            var activityColor = Field<Color>(overlay, "_orbColor");
            Assert.NotEqual(normalColor, activityColor);
            foreach (var phase in new[] { VoiceUiPhase.Listening, VoiceUiPhase.Thinking, VoiceUiPhase.Speaking, VoiceUiPhase.Listening })
            {
                KeyboardWtfState.SetUi(phase, "Jarvis", "Ordinary conversation");
                Assert.Equal(phase, KeyboardWtfState.UiPhase);
                Assert.Equal("Searching Gmail…", label.Text);
                Assert.True(label.Shiny); Assert.True(icon.Visible); Assert.NotNull(icon.Image);
                Assert.Equal(activityColor, Field<Color>(overlay, "_orbColor"));
                Assert.True(icon.Right < label.Left && label.Right <= overlay.ClientSize.Width);
            }
            Capture(overlay, "gmail-native-100.png");
            overlay.Scale(new SizeF(1.5f, 1.5f));
            KeyboardWtfState.TryUpdateLongTaskUi("connector-ui", "Reading thread…");
            Assert.True(icon.Right < label.Left && label.Right <= overlay.ClientSize.Width);
            Capture(overlay, "gmail-native-150.png");
            KeyboardWtfState.ClearLongTaskUi("connector-ui");
            Assert.False(label.Shiny); Assert.False(icon.Visible); Assert.False(label.AnimationRunning);
            Assert.Equal(normalColor, Field<Color>(overlay, "_orbColor"));
            Assert.Equal("Ordinary conversation", label.Text);
            Capture(overlay, "listening-native-150.png");
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
            Assert.False(overlay.Visible);
        }
        finally { KeyboardWtfState.ClearLongTaskUi("connector-ui"); KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready"); }
    });

    [Fact]
    public void Shine_timer_runs_only_for_visible_connector_work_and_honors_system_animation_preference() => OnUiThread(() =>
    {
        using var form = QuietFormFixture.CreateForm();
        using var label = new ConnectorActivityLabel { Text = "Searching Gmail…", Shiny = true };
        form.Controls.Add(label);
        Assert.False(label.AnimationRunning);
        form.Show();
        QuietFormFixture.AssertQuiet(form);
        Assert.Equal(ConnectorActivityLabel.AnimationsAllowed, label.AnimationRunning);
        form.Hide(); Assert.False(label.AnimationRunning);
        form.Show(); QuietFormFixture.AssertQuiet(form); label.Shiny = false; Assert.False(label.AnimationRunning);
    });

    [Theory]
    [InlineData(1f, 100)]
    [InlineData(1.25f, 125)]
    [InlineData(1.5f, 150)]
    [InlineData(2f, 200)]
    public void Provider_mark_has_clear_edges_and_nonoverlapping_geometry_at_scaled_sizes(float scale, int percent) => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        using var overlay = QuietFormFixture.CreateOverlay(recorder);
        if (scale != 1) overlay.Scale(new SizeF(scale, scale));
        overlay.InitializeUiThreadHandle();
        var label = Field<ConnectorActivityLabel>(overlay, "_detailLabel");
        var icon = Field<PictureBox>(overlay, "_providerIcon");
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("connector-dpi-ui", "Searching Gmail…", 0, "gmail"));
            Assert.Equal(new Size(label.Height, label.Height), icon.Size);
            Assert.Equal(label.Top, icon.Top);
            Assert.True(icon.Right < label.Left && label.Right <= overlay.ClientSize.Width);
            Assert.True(icon.Bottom <= overlay.ClientSize.Height);
            foreach (var provider in new[] { "gmail", "googlecalendar", "googledrive", "googledocs", "googleforms", "googlemeet",
                "youtube", "github", "slack", "discord", "reddit", "linkedin" })
            {
                using var stream = typeof(VoiceOverlayForm).Assembly.GetManifestResourceStream("icons/connectors/" + provider + ".png");
                using var image = Image.FromStream(stream);
                var bounds = ConnectorProviderIcon.ImageBounds(image.Size, icon.ClientSize);
                Assert.True(bounds.Left >= 2 && bounds.Top >= 2);
                Assert.True(bounds.Right <= icon.ClientSize.Width - 2 && bounds.Bottom <= icon.ClientSize.Height - 2);
                Assert.Equal(bounds.Width, bounds.Height); // Full square resource, no fill/crop.
            }
            Capture(overlay, "gmail-inset-" + percent + ".png");
        }
        finally { KeyboardWtfState.ClearLongTaskUi("connector-dpi-ui"); KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready"); }
    });

    [Theory]
    [InlineData(VoiceUiPhase.Listening)]
    [InlineData(VoiceUiPhase.Speaking)]
    public void Connector_activity_keeps_the_existing_white_voice_meter_inside_its_blue_orb(VoiceUiPhase phase) => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        using var overlay = QuietFormFixture.CreateOverlay(recorder);
        overlay.InitializeUiThreadHandle();
        var orb = Field<Panel>(overlay, "_orb");
        try
        {
            KeyboardWtfState.SetUi(phase, "Jarvis", "Ordinary conversation");
            using (var normal = new Bitmap(orb.Width, orb.Height))
            {
                orb.DrawToBitmap(normal, orb.ClientRectangle);
                Assert.Equal(Color.White.ToArgb(), normal.GetPixel(21, 21).ToArgb());
            }
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("connector-orb-ui", "Searching Gmail…", 0, "gmail"));
            using (var active = new Bitmap(orb.Width, orb.Height))
            {
                orb.DrawToBitmap(active, orb.ClientRectangle);
                Assert.Equal(Color.White.ToArgb(), active.GetPixel(21, 21).ToArgb());
                Assert.Equal(Color.FromArgb(99, 139, 235).ToArgb(), active.GetPixel(21, 3).ToArgb());
            }
            KeyboardWtfState.ClearLongTaskUi("connector-orb-ui");
            using var restored = new Bitmap(orb.Width, orb.Height);
            orb.DrawToBitmap(restored, orb.ClientRectangle);
            Assert.Equal(Color.White.ToArgb(), restored.GetPixel(21, 21).ToArgb());
        }
        finally { KeyboardWtfState.ClearLongTaskUi("connector-orb-ui"); KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready"); }
    });

    [Theory]
    [InlineData(VoiceUiPhase.Listening)]
    [InlineData(VoiceUiPhase.Thinking)]
    [InlineData(VoiceUiPhase.Executing)]
    public void Connector_white_meter_uses_real_input_level_and_stops_for_muted_input(VoiceUiPhase phase) => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        var action = new PopupQuickAction(PopupQuickActionKind.Mute, "meter-session", 1);
        using var overlay = QuietFormFixture.CreateOverlay(recorder, () => action, showQuickAction: () => true);
        overlay.InitializeUiThreadHandle();
        var orb = Field<Panel>(overlay, "_orb");
        var oldLevel = KeyboardWtfState.InputLevelDb;
        try
        {
            KeyboardWtfState.SetUi(phase, "Jarvis", "Ordinary conversation");
            if (phase != VoiceUiPhase.Listening)
            {
                using var normal = new Bitmap(orb.Width, orb.Height);
                orb.DrawToBitmap(normal, orb.ClientRectangle);
                Assert.Equal(0, WhitePixels(normal));
            }
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("connector-meter-ui", "Reading email…", 0, "gmail"));
            KeyboardWtfState.InputLevelDb = -96;
            using var quiet = new Bitmap(orb.Width, orb.Height);
            orb.DrawToBitmap(quiet, orb.ClientRectangle);
            KeyboardWtfState.InputLevelDb = 0;
            using var loud = new Bitmap(orb.Width, orb.Height);
            orb.DrawToBitmap(loud, orb.ClientRectangle);
            Assert.True(WhitePixels(loud) > WhitePixels(quiet) * 3);
            action = action with { Kind = PopupQuickActionKind.Unmute, Revision = 2 };
            KeyboardWtfState.SetUi(phase, "Jarvis", "Ordinary conversation");
            using var muted = new Bitmap(orb.Width, orb.Height);
            orb.DrawToBitmap(muted, orb.ClientRectangle);
            Assert.Equal(0, WhitePixels(muted));
            Assert.Equal(Color.FromArgb(99, 139, 235).ToArgb(), muted.GetPixel(21, 21).ToArgb());
            KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "Jarvis", "Speaking…");
            using var playback = new Bitmap(orb.Width, orb.Height);
            orb.DrawToBitmap(playback, orb.ClientRectangle);
            Assert.True(WhitePixels(playback) > 0);
        }
        finally
        {
            KeyboardWtfState.InputLevelDb = oldLevel;
            KeyboardWtfState.ClearLongTaskUi("connector-meter-ui");
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Fixed_detail_row_retains_all_Thinking_descender_rows_at_real_font_DPI(int dpi) => OnUiThread(() =>
    {
        using var font = new Font("Segoe UI", 9);
        using var label = new ConnectorActivityLabel
        {
            Size = new Size(352, 24), Font = font, Text = "Thinking…",
            ForeColor = Color.FromArgb(205, 205, 205)
        };
        using var actual = new Bitmap(352, 24);
        actual.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(actual))
        {
            graphics.Clear(Color.Black);
            // Exercise the same production painter independently of the user's
            // animation preference; the real OnPaint still honors that setting.
            label.DrawActivityText(graphics);
        }
        using var reference = new Bitmap(352, 80);
        reference.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(reference))
        using (var brush = new SolidBrush(label.ForeColor))
        using (var format = new StringFormat(StringFormatFlags.NoWrap))
        {
            graphics.Clear(Color.Black);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString(label.Text, font, brush, new RectangleF(0, 1, 352, 79), format);
        }
        var expectedRows = InkRows(reference);
        var actualRows = InkRows(actual);
        Assert.NotEmpty(expectedRows); Assert.NotEmpty(actualRows);
        Assert.Equal(expectedRows.Last() - expectedRows.First(), actualRows.Last() - actualRows.First());
        Assert.InRange(actualRows.First(), 0, 23); Assert.InRange(actualRows.Last(), 0, 23);
        Assert.Equal(new Size(352, 24), label.Size); Assert.Equal(9f, font.SizeInPoints);
        if (dpi >= 144) Assert.True(expectedRows.Last() >= 24); // Causal old y1 row crop.
    });

    [Theory]
    [InlineData(48, 24, 48, 48)]
    [InlineData(24, 48, 48, 48)]
    [InlineData(48, 48, 36, 36)]
    public void Provider_mark_preserves_the_complete_source_aspect_ratio(int width, int height, int clientWidth, int clientHeight)
    {
        var bounds = ConnectorProviderIcon.ImageBounds(new Size(width, height), new Size(clientWidth, clientHeight));
        Assert.True(bounds.Left > 0 && bounds.Top > 0);
        Assert.True(bounds.Right < clientWidth && bounds.Bottom < clientHeight);
        Assert.Equal((double)width / height, (double)bounds.Width / bounds.Height);
    }

    [Fact]
    public void Sequential_service_tasks_switch_the_correct_mark_and_clear_every_terminal_owner() => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        using var overlay = QuietFormFixture.CreateOverlay(recorder);
        overlay.InitializeUiThreadHandle();
        var label = Field<ConnectorActivityLabel>(overlay, "_detailLabel");
        var icon = Field<PictureBox>(overlay, "_providerIcon");
        var providers = new[] { ("gmail", "Searching Gmail…"), ("googlecalendar", "Checking Calendar…"),
            ("googledrive", "Looking through Drive…"), ("googledocs", "Reading document…"),
            ("googleforms", "Reading form…"), ("youtube", "Checking YouTube…"),
            ("github", "Searching GitHub…"), ("slack", "Searching Slack…"), ("discord", "Checking Discord…") };
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            foreach (var (provider, detail) in providers)
            {
                var owner = "service-ui-" + provider;
                Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle(owner, detail, 0, provider));
                Assert.Equal((detail, provider), KeyboardWtfState.LongTaskDisplayState());
                using var mark = typeof(VoiceOverlayForm).Assembly.GetManifestResourceStream("icons/connectors/" + provider + ".png");
                using var original = Image.FromStream(mark);
                using var expected = new Bitmap(original);
                var actual = Assert.IsType<Bitmap>(icon.Image);
                for (var y = 0; y < expected.Height; y++)
                    for (var x = 0; x < expected.Width; x++) Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
                Assert.True(icon.Visible); Assert.True(label.Shiny); Assert.Equal(detail, label.Text);
                foreach (var terminal in new[] { VoiceUiPhase.Done, VoiceUiPhase.Error, VoiceUiPhase.Cancelled })
                {
                    KeyboardWtfState.SetUi(terminal, "Jarvis", "Terminal voice event");
                    Assert.Equal(detail, label.Text); Assert.True(icon.Visible);
                }
                Assert.True(KeyboardWtfState.ClearLongTaskUi(owner));
                Assert.False(icon.Visible); Assert.False(label.Shiny); Assert.False(label.AnimationRunning);
                Assert.Equal(("", ""), KeyboardWtfState.LongTaskDisplayState());
                Assert.False(KeyboardWtfState.TryUpdateLongTaskUi(owner, "Late completion"));
                KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Listening…");
            }
        }
        finally
        {
            foreach (var (provider, _) in providers) KeyboardWtfState.ClearLongTaskUi("service-ui-" + provider);
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
        }
    });

    [Theory]
    [InlineData(VoiceUiPhase.Idle)]
    [InlineData(VoiceUiPhase.Done)]
    [InlineData(VoiceUiPhase.Error)]
    [InlineData(VoiceUiPhase.Cancelled)]
    public void Between_step_visual_owner_survives_idle_and_aged_unrelated_terminal_voice_states(VoiceUiPhase phase) => OnUiThread(() =>
    {
        using var recorder = new AudioRecorderService();
        using var overlay = QuietFormFixture.CreateOverlay(recorder);
        overlay.InitializeUiThreadHandle();
        var refresh=typeof(VoiceOverlayForm).GetMethod("RefreshState",BindingFlags.Instance|BindingFlags.NonPublic);
        var label=Field<ConnectorActivityLabel>(overlay,"_detailLabel");
        var icon=Field<PictureBox>(overlay,"_providerIcon");
        try
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening,"Jarvis","Listening…");
            Assert.True(KeyboardWtfState.TryBeginLongTaskUiIfIdle("held-connector-ui","Searching Gmail…",0,"gmail"));
            KeyboardWtfState.TryUpdateLongTaskUi("held-connector-ui","Thinking…",connectorBetweenSteps:true);
            Assert.Equal("",KeyboardWtfState.LongTaskDetail); // Health stays independent.
            Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
            KeyboardWtfState.SetUi(phase,"Jarvis","Unrelated voice state");
            typeof(KeyboardWtfState).GetProperty("UiPhaseStartedUtc").SetValue(null,DateTime.UtcNow.AddSeconds(-10));
            refresh.Invoke(overlay,null);
            Assert.True(overlay.Visible); Assert.True(icon.Visible); Assert.True(label.Shiny);
            Assert.Equal("Thinking…",label.Text); Assert.Equal(phase,KeyboardWtfState.UiPhase);
            Capture(overlay,"held-owner-"+phase+".png");
            KeyboardWtfState.ClearLongTaskUi("held-connector-ui"); refresh.Invoke(overlay,null);
            Assert.False(overlay.Visible); Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        }
        finally { KeyboardWtfState.ClearLongTaskUi("held-connector-ui"); KeyboardWtfState.SetUi(VoiceUiPhase.Idle,"keyboard.wtf","Ready"); }
    });

    private static T Field<T>(object value, string name) => (T)value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
    private static int WhitePixels(Bitmap image)
    {
        var count = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y).ToArgb() == Color.White.ToArgb()) count++;
        return count;
    }
    private static int[] InkRows(Bitmap image) => Enumerable.Range(0, image.Height)
        .Where(y => Enumerable.Range(0, image.Width).Any(x =>
        {
            var pixel = image.GetPixel(x, y);
            return pixel.R + pixel.G + pixel.B > 100;
        })).ToArray();
    private static void Capture(Form form, string name)
    {
        QuietFormFixture.AssertQuiet(form);
        var directory = Environment.GetEnvironmentVariable("KEYBOARD_WTF_UI_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, form.ClientRectangle);
        image.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
    }
    private static void OnUiThread(Action action)
    {
        Exception failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } finally { done.Set(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15))); thread.Join(); Assert.Null(failure);
    }
}
