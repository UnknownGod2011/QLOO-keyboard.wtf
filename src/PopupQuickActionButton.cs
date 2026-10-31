namespace KeyboardWtf;

using System.Drawing.Drawing2D;
using KeyboardWtf.Models;

internal sealed class PopupQuickActionButton : Control
{
    private volatile PopupQuickAction _action = PopupQuickAction.Hidden;
    private PopupQuickAction _pressedAction;
    private bool _hover;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    internal PopupQuickActionButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(18, 18, 18);
        AccessibleRole = AccessibleRole.PushButton;
    }

    internal event Action<PopupQuickAction> ActionInvoked;
    protected override AccessibleObject CreateAccessibilityInstance() => new QuickActionAccessibleObject(this);

    private void InvokeAccessibleAction(PopupQuickAction displayed)
    {
        if (IsDisposed || Disposing) return;
        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke((Action)(() => InvokeAccessibleAction(displayed))); }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
            return;
        }
        // Accessibility invokes exactly the action advertised when requested.
        // A queued request must never borrow a replacement capture/session.
        if (!IsHandleCreated || !Enabled || !Visible || displayed.Kind == PopupQuickActionKind.Hidden
            || displayed != _action) return;
        ActionInvoked?.Invoke(displayed);
    }

    private sealed class QuickActionAccessibleObject : ControlAccessibleObject
    {
        private readonly PopupQuickActionButton _button;
        internal QuickActionAccessibleObject(PopupQuickActionButton button) : base(button) => _button = button;
        public override string DefaultAction => _button.AccessibleName;
        public override void DoDefaultAction() => _button.InvokeAccessibleAction(_button._action);
    }

    internal PopupQuickAction Action
    {
        get => _action;
        set
        {
            var next = value ?? PopupQuickAction.Hidden;
            if (next == _action) return;
            _action = next;
            AccessibleName = _action.Kind switch
            {
                PopupQuickActionKind.Mute => "Mute microphone",
                PopupQuickActionKind.Unmute => "Unmute microphone",
                PopupQuickActionKind.Done => "Finish Smart Writing recording",
                _ => "Voice quick action",
            };
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled && _action.Kind != PopupQuickActionKind.Hidden)
            _pressedAction = _action;
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressedAction;
        _pressedAction = null;
        Invalidate();
        if (e.Button == MouseButtons.Left && Enabled && pressed is not null
            && ClientRectangle.Contains(e.Location))
            ActionInvoked?.Invoke(pressed);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_action.Kind == PopupQuickActionKind.Hidden) return;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(Width / 20f, Height / 20f);
        var pressed = _pressedAction is not null;
        var background = pressed ? Color.FromArgb(55, 59, 67)
            : _hover ? Color.FromArgb(42, 46, 54) : Color.FromArgb(30, 33, 39);
        using var fill = new SolidBrush(background);
        using var border = new Pen(Color.FromArgb(60, 65, 73), 0.7f);
        graphics.FillEllipse(fill, 1, 1, 18, 18);
        graphics.DrawEllipse(border, 1, 1, 18, 18);
        var color = Enabled ? Color.FromArgb(228, 234, 241) : Color.FromArgb(125, 130, 139);
        using var pen = new Pen(color, 1.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (_action.Kind == PopupQuickActionKind.Done)
        {
            graphics.DrawLines(pen, [new PointF(5, 10), new PointF(8, 13), new PointF(15, 6)]);
            return;
        }
        using var microphone = RoundedRectangle(new RectangleF(8, 4, 4, 8), 2);
        graphics.DrawPath(pen, microphone);
        graphics.DrawArc(pen, 6, 7, 8, 7, 0, 180);
        graphics.DrawLine(pen, 10, 14, 10, 16);
        graphics.DrawLine(pen, 8, 16, 12, 16);
        if (_action.Kind == PopupQuickActionKind.Unmute)
        {
            using var cutout = new Pen(background, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(cutout, 5, 5, 15, 15);
            using var slash = new Pen(Color.FromArgb(218, 146, 146), 1.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(slash, 5, 5, 15, 15);
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
