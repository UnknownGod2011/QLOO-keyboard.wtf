using System.Diagnostics;
using System.Windows.Forms;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class NativeWindowCloseDesktopTests
{
    [Theory]
    [InlineData("destroy")]
    [InlineData("tray")]
    [InlineData("prompt")]
    [InlineData("indirect-prompt")]
    [InlineData("delayed-prompt")]
    public void Native_close_verifies_owned_window_visibility_and_untitled_prompt_chains(string behavior)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Real native HWNDs, parked offscreen/alpha0 without activation.
                // These fixtures never target or interact with the user's apps.
                using var app = QuietFormFixture.CreateForm();
                using var intermediate = QuietFormFixture.CreateForm();
                using var prompt = QuietFormFixture.CreateForm();
                using var delay = new System.Windows.Forms.Timer { Interval = 60 };
                app.Text = "Native close fixture";
                prompt.Text = ""; // Titleless owned prompts must still block success.
                app.Show();
                var snapshot = NativeWindowCloser.Read(app.Handle);
                Assert.NotNull(snapshot);
                Assert.True(snapshot.IsVisible);
                void ShowPrompt()
                {
                    if (behavior == "indirect-prompt")
                    {
                        intermediate.Show(app);
                        intermediate.Hide();
                        prompt.Show(intermediate);
                    }
                    else prompt.Show(app);
                    QuietFormFixture.AssertQuiet(prompt);
                }
                delay.Tick += (_, _) => { delay.Stop(); ShowPrompt(); };
                app.FormClosing += (_, args) =>
                {
                    if (behavior == "destroy") return;
                    args.Cancel = true;
                    app.Hide();
                    if (behavior == "delayed-prompt") delay.Start();
                    else if (behavior != "tray") ShowPrompt();
                };
                var close = NativeWindowCloser.CloseAsync([snapshot], CancellationToken.None,
                    timeout: TimeSpan.FromMilliseconds(500));
                var clock = Stopwatch.StartNew();
                while (!close.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
                Assert.True(close.IsCompleted, "Owned native fixture close must finish within the bounded check.");
                var outcome = close.GetAwaiter().GetResult();
                Assert.Equal(1, outcome.Requested);
                Assert.Equal(behavior is "destroy" or "tray", outcome.Succeeded);
                if (behavior == "destroy") Assert.Null(NativeWindowCloser.Read(snapshot.Handle));
                else
                {
                    var retained = NativeWindowCloser.Read(snapshot.Handle);
                    Assert.NotNull(retained);
                    Assert.False(retained.IsVisible);
                    if (behavior != "tray") Assert.True(NativeWindowCloser.Read(prompt.Handle).IsVisible);
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native fixture STA must terminate.");
        Assert.Null(failure);
    }
}
