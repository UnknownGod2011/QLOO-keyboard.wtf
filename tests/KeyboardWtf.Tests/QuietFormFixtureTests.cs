using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class QuietFormFixtureTests
{
    [Fact]
    public void Quiet_native_overlay_keeps_same_HWND_visibility_and_actual_nonblank_raster()
    {
        Exception failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var oldLevel = KeyboardWtfState.InputLevelDb;
            try
            {
                KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
                using var recorder = new AudioRecorderService();
                using var form = QuietFormFixture.CreateOverlay(recorder,
                    () => new(PopupQuickActionKind.Mute, "quiet-fixture", 1), _ => Task.FromResult(true), () => true);
                form.InitializeUiThreadHandle();
                var handle = form.Handle;
                var dpi = form.DeviceDpi;
                QuietFormFixture.AssertQuiet(form);
                Assert.False(form.Visible);
                KeyboardWtfState.InputLevelDb = 0;
                KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Fixture", "Quiet raster evidence");
                Assert.True(form.Visible);
                var state = QuietFormFixture.AssertQuiet(form);
                Assert.True(state.Shows > 0); Assert.Equal(state.Shows, state.NativeVisibleObservations);
                Assert.Equal(handle, form.Handle); Assert.Equal(dpi, form.DeviceDpi);
                Assert.Equal(new Size(460, 82), form.ClientSize);
                var title = (Label)typeof(VoiceOverlayForm).GetField("_statusLabel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                var detail = (Label)typeof(VoiceOverlayForm).GetField("_detailLabel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                Assert.Equal(11f, title.Font.SizeInPoints); Assert.Equal(9f, detail.Font.SizeInPoints);
                using var raster = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(raster, form.ClientRectangle);
                Assert.Equal(Color.White.ToArgb(), raster.GetPixel(37, 40).ToArgb()); // Existing real input meter.
                Assert.Equal(form.BackColor.ToArgb(), raster.GetPixel(12, 40).ToArgb());
                var titleInk = 0;
                for (var y = title.Top; y < title.Bottom; y++)
                    for (var x = title.Left; x < title.Right; x++)
                    {
                        var pixel = raster.GetPixel(x, y);
                        if (pixel.A == 255 && pixel.R > 100 && pixel.G > 100 && pixel.B > 100) titleInk++;
                    }
                Assert.True(titleInk > 30, "Alpha0 must not turn actual DrawToBitmap glyph evidence into an empty raster.");
                KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
                Assert.False(form.Visible); QuietFormFixture.AssertQuiet(form);
                Assert.True(state.Hides > 0); Assert.Equal(handle, form.Handle);
                Console.WriteLine($"QUIET_FORM_PASS|shows={state.Shows}|hides={state.Hides}|alpha0=true|outside=true|client=460x82|glyphInk={titleInk}");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                KeyboardWtfState.InputLevelDb = oldLevel;
                KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
                finished.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(15))); thread.Join(); Assert.Null(failure);
    }
}
