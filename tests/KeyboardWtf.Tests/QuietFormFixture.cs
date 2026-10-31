using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

// Only inert forms constructed by desktop tests use this scope. The product
// form, global DPI policy, native region and visibility logic remain unchanged.
internal static class QuietFormFixture
{
    private static readonly ConditionalWeakTable<Form, State> States = new();
    internal sealed class State
    {
        internal bool Parking;
        internal int Shows, Hides, NativeVisibleObservations, UnsafeNativeVisibleObservations;
    }

    internal static VoiceOverlayForm CreateOverlay(AudioRecorderService recorder,
        Func<PopupQuickAction> getQuickAction = null,
        Func<PopupQuickAction, Task<bool>> executeQuickAction = null,
        Func<bool> showQuickAction = null, Func<long> monotonicClock = null)
    {
        var form = new VoiceOverlayForm(recorder, getQuickAction, executeQuickAction, showQuickAction, monotonicClock);
        Attach(form);
        return form;
    }

    internal static Form CreateForm()
    {
        var form = new NonActivatingForm { StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
        Attach(form);
        return form;
    }

    private static void Attach(Form form)
    {
        if (form.IsHandleCreated || form.Visible) throw new InvalidOperationException("Quiet presentation must precede any fixture HWND or Show.");
        var state = new State(); States.Add(form, state);
        // Alpha is independent of product PositionOverlay/rounded Region. It
        // also protects transient moves before the synchronous parking callback.
        form.Opacity = 0;
        EventHandler park = (_, _) => Park(form, state);
        EventHandler observe = (_, _) =>
        {
            if (!form.Visible) { state.Hides++; return; }
            state.Shows++; Park(form, state);
            state.NativeVisibleObservations++;
            if (!NativeQuiet(form)) state.UnsafeNativeVisibleObservations++;
            Assert.True(NativeQuiet(form), "A logical fixture Show must retain alpha0 and an entirely offscreen owned HWND.");
        };
        form.LocationChanged += park;
        form.SizeChanged += park;
        form.HandleCreated += park;
        form.VisibleChanged += observe;
        form.Disposed += (_, _) =>
        {
            form.LocationChanged -= park; form.SizeChanged -= park;
            form.HandleCreated -= park; form.VisibleChanged -= observe;
        };
        Park(form, state);
    }

    private static void Park(Form form, State state)
    {
        if (state.Parking || form.IsDisposed) return;
        state.Parking = true;
        try
        {
            var screen = SystemInformation.VirtualScreen;
            var point = new Point(checked(screen.Left - form.Width - 64), checked(screen.Top - form.Height - 64));
            if (form.Location != point) form.Location = point;
        }
        finally { state.Parking = false; }
    }

    internal static State AssertQuiet(Form form)
    {
        Assert.True(States.TryGetValue(form, out var state));
        Assert.Equal(0d, form.Opacity);
        Assert.False(SystemInformation.VirtualScreen.IntersectsWith(form.Bounds));
        if (form.IsHandleCreated) Assert.True(NativeQuiet(form), "Owned fixture HWND must remain outside every monitor with native alpha0.");
        Assert.Equal(0, state.UnsafeNativeVisibleObservations);
        return state;
    }

    private static bool NativeQuiet(Form form)
    {
        if (!form.IsHandleCreated || form.IsDisposed) return false;
        return GetWindowRect(form.Handle, out var rectangle)
            && !SystemInformation.VirtualScreen.IntersectsWith(Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom))
            && GetLayeredWindowAttributes(form.Handle, out _, out var alpha, out var flags)
            && alpha == 0 && (flags & 2) != 0; // LWA_ALPHA, never a color-key substitute.
    }

    private sealed class NonActivatingForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colorKey, out byte alpha, out uint flags);
}
