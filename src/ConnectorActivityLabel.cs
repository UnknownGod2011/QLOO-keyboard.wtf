namespace KeyboardWtf;

using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

// Native equivalent of the requested restrained shiny text. Uses only the
// existing status label; never participates in audio, dispatch or task timing.
internal sealed class ConnectorActivityLabel : Label
{
    private readonly System.Windows.Forms.Timer _shineTimer = new() { Interval = 33 };
    private bool _shiny;
    private long _started;
    private Control _visibilityParent;
    private bool _fitInk;
    internal bool FitInk
    {
        get => _fitInk;
        set { if (_fitInk == value) return; _fitInk = value; Invalidate(); }
    }
    internal bool Shiny
    {
        get => _shiny;
        set
        {
            if (_shiny == value) return;
            _shiny = value;
            _started = Environment.TickCount64;
            UpdateAnimation();
            Invalidate();
        }
    }
    internal bool AnimationRunning => _shineTimer.Enabled;

    internal ConnectorActivityLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        _shineTimer.Tick += (_, _) => Invalidate();
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimation();
    }
    protected override void OnParentChanged(EventArgs e)
    {
        if (_visibilityParent is not null) _visibilityParent.VisibleChanged -= ParentVisibilityChanged;
        base.OnParentChanged(e);
        _visibilityParent = Parent;
        if (_visibilityParent is not null) _visibilityParent.VisibleChanged += ParentVisibilityChanged;
        UpdateAnimation();
    }
    private void ParentVisibilityChanged(object sender, EventArgs e) => UpdateAnimation();
    private void UpdateAnimation() => _shineTimer.Enabled = _shiny && Visible
        && AnimationsAllowed;

    internal static bool AnimationsAllowed => SystemInformation.UIEffectsEnabled
        && SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out var enabled, 0) && enabled;
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_shiny && !_fitInk
            || Width <= 0 || Height <= 0 || string.IsNullOrEmpty(Text))
        {
            base.OnPaint(e);
            return;
        }
        DrawActivityText(e.Graphics, _shiny && AnimationsAllowed);
    }
    internal void DrawActivityText(Graphics graphics, bool animate = true)
    {
        var width = (float)Width;
        var shineWidth = width * 0.4f;
        var progress = Math.Max(0, Environment.TickCount64 - _started) % 2000 / 2000f;
        var center = (width - shineWidth + progress * (width + 2 * shineWidth)) / (3 * width);
        var radius = shineWidth / (3 * width);
        using var brush = new LinearGradientBrush(new RectangleF(-width, 0, width * 3, Height),
            ForeColor, ForeColor, LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = animate ? new[] { ForeColor, ForeColor, Color.FromArgb(231, 236, 246), Color.White,
                Color.FromArgb(231, 236, 246), ForeColor, ForeColor } : Enumerable.Repeat(ForeColor, 7).ToArray(),
            Positions = new[] { 0f, center - radius, center - radius * 0.4f, center,
                center + radius * 0.4f, center + radius, 1f }
        };
        using var format = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near
        };
        // A fixed 24px row has room for the glyphs, but GDI+'s full line metrics
        // plus the old 1px top inset crop descenders at 144 DPI. Align actual ink
        // inside that same row; retain the font, width, ellipsis and shine.
        using var glyphs = new GraphicsPath();
        glyphs.AddString(Text, Font.FontFamily, (int)Font.Style,
            Font.SizeInPoints * graphics.DpiY / 72f, PointF.Empty, format);
        var ink = glyphs.GetBounds();
        var top = (Height - ink.Height) / 2f - ink.Top;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.DrawString(Text, Font, brush,
            new RectangleF(0, top, Width, Math.Max(Height, Font.GetHeight(graphics))), format);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_visibilityParent is not null) _visibilityParent.VisibleChanged -= ParentVisibilityChanged;
            _shineTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
