namespace KeyboardWtf;

using System.Drawing.Drawing2D;
using KeyboardWtf.Models;
using KeyboardWtf.Services;

internal sealed class VoiceOverlayForm : Form
{
    private readonly AudioRecorderService _audioRecorder;
    private readonly int _uiThreadId;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _statusLabel;
    private readonly ConnectorActivityLabel _detailLabel;
    private readonly Font _statusFont;
    private readonly Font _detailFont;
    private readonly PictureBox _providerIcon;
    private readonly PopupQuickActionButton _quickActionButton;
    private readonly ToolTip _quickActionTip;
    private readonly Func<PopupQuickAction> _getQuickAction;
    private readonly Func<PopupQuickAction, Task<bool>> _executeQuickAction;
    private readonly Func<bool> _showQuickAction;
    private readonly Func<long> _monotonicClock;
    private readonly PopupQuickActionPresentation _quickActionPresentation = new();
    private PopupQuickAction _pendingCompletionAction;
    private bool _microphoneMuted;
    private bool _layingOut;
    private readonly Dictionary<string, Image> _providerImages = new(StringComparer.Ordinal);
    private bool _connectorActivity;
    private readonly Panel _orb;
    private Color _orbColor = Color.FromArgb(90, 90, 90);
    private VoiceUiPhase _lastPhase = VoiceUiPhase.Idle;
    private double _lastLevelDb = -96;
    private long _orbPhaseStarted = Environment.TickCount64;

    public VoiceOverlayForm(AudioRecorderService audioRecorder,
        Func<PopupQuickAction> getQuickAction = null,
        Func<PopupQuickAction, Task<bool>> executeQuickAction = null,
        Func<bool> showQuickAction = null,
        Func<long> monotonicClock = null)
    {
        _audioRecorder = audioRecorder;
        _getQuickAction = getQuickAction ?? (() => PopupQuickAction.Hidden);
        _executeQuickAction = executeQuickAction;
        // Existing fixture callers keep their original layout. The real app
        // supplies the persisted preference, whose default is ON.
        _showQuickAction = showQuickAction ?? (() => false);
        _monotonicClock = monotonicClock ?? (() => Environment.TickCount64);
        _uiThreadId = Environment.CurrentManagedThreadId;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.White;
        DoubleBuffered = true;
        ClientSize = new Size(460, 82);
        Opacity = 0.96;
        Padding = new Padding(12);

        _orb = new Panel
        {
            Location = new Point(16, 19),
            Size = new Size(42, 42),
            BackColor = Color.FromArgb(18, 18, 18),
        };
        _orb.Paint += PaintOrb;

        _statusLabel = new ConnectorActivityLabel
        {
            AutoSize = false,
            Location = new Point(74, 13),
            Size = new Size(382, 26),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            Text = QlooProduct.Name,
        };

        _detailLabel = new ConnectorActivityLabel
        {
            AutoEllipsis = true,
            AutoSize = false,
            Location = new Point(74, 40),
            Size = new Size(382, 24),
            Font = new Font("Segoe UI", 9),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(205, 205, 205),
            Text = "Ctrl+Alt+Q for Jarvis",
        };
        _statusFont = _statusLabel.Font;
        _detailFont = _detailLabel.Font;
        // Child DPI notifications can rescale explicit fonts independently of
        // the parent's fixed-size policy. Retain the original popup typography.
        _statusLabel.DpiChangedAfterParent += RestoreLegacyFontAfterDpi;
        _detailLabel.DpiChangedAfterParent += RestoreLegacyFontAfterDpi;

        _providerIcon = new ConnectorProviderIcon
        {
            Location = new Point(74, 40), Size = new Size(24, 24),
            BackColor = Color.Transparent,
            Visible = false, TabStop = false,
            AccessibleName = "Active connected service"
        };
        _quickActionButton = new PopupQuickActionButton
        {
            Location = new Point(430, 8), Size = new Size(20, 20), Visible = false,
        };
        _quickActionButton.ActionInvoked += HandleQuickAction;
        _quickActionTip = new ToolTip { InitialDelay = 350, ReshowDelay = 100, ShowAlways = true };

        Controls.Add(_orb);
        Controls.Add(_statusLabel);
        Controls.Add(_detailLabel);
        Controls.Add(_providerIcon);
        Controls.Add(_quickActionButton);
        // Preserve the original hand-built popup's physical 460x82 footprint.
        // The application's existing SystemAware policy handles its DPI context;
        // opting this one form into 96-DPI autoscaling enlarges the old popup.
        AutoScaleMode = AutoScaleMode.None;
        UpdateRoundedRegion();

        _timer = new System.Windows.Forms.Timer { Interval = 80 };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        KeyboardWtfState.UiChanged += HandleUiChanged;
    }

    /// <summary>
    /// Creates the native window handle on the application's UI thread before
    /// background services can publish their first status update. Without this,
    /// WinForms can report InvokeRequired=false for a handle-less form and the
    /// first worker-thread update may accidentally create the window there.
    /// </summary>
    internal void InitializeUiThreadHandle()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
            throw new InvalidOperationException("The voice overlay must be initialized on the UI thread.");

        if (!IsHandleCreated)
            _ = Handle;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message message)
    {
        const int wmMouseActivate = 0x0021;
        if (message.Msg == wmMouseActivate)
        {
            message.Result = new IntPtr(3); // MA_NOACTIVATE: preserve the typing target.
            return;
        }
        base.WndProc(ref message);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExTopmost = 0x00000008;
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            // Put the invariant on every native handle, including a recreated
            // one. This keeps Jarvis above ordinary apps without activating it.
            cp.ExStyle |= wsExTopmost | wsExToolWindow | wsExNoActivate;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionOverlay();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRoundedRegion();
        LayoutQuickAction();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Even in a per-monitor fixture, keep the original compact footprint.
        // The actual application retains its unchanged SystemAware DPI policy.
        e.Cancel = true;
        base.OnDpiChanged(e);
        ClientSize = new Size(460, 82);
        if (_providerIcon is not null && _detailLabel is not null)
        {
            _providerIcon.Size = new Size(_detailLabel.Height, _detailLabel.Height);
            _providerIcon.Location = new Point(_statusLabel.Left, _detailLabel.Top);
        }
        LayoutQuickAction();
        UpdateRoundedRegion();
        Invalidate(invalidateChildren: true);
    }

    private void UpdateRoundedRegion()
    {
        if (ClientRectangle.Width > 0 && ClientRectangle.Height > 0)
        {
            var previous = Region;
            Region = RoundedRegion(ClientRectangle, Math.Max(1, (int)Math.Round(12 * LayoutScale)));
            previous?.Dispose();
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        LayoutQuickAction();
    }

    private float LayoutScale => _orb is null ? DeviceDpi / 96f : _orb.Width / 42f;

    private void RestoreLegacyFontAfterDpi(object sender, EventArgs e)
    {
        if (sender == _statusLabel) _statusLabel.Font = _statusFont;
        else if (sender == _detailLabel) _detailLabel.Font = _detailFont;
        LayoutQuickAction();
    }

    private void LayoutQuickAction()
    {
        if (_layingOut || _quickActionButton is null || _statusLabel is null) return;
        _layingOut = true;
        try
        {
            var scale = LayoutScale;
            var side = Math.Max(1, (int)Math.Round(20 * scale));
            _quickActionButton.Bounds = new Rectangle(
                ClientSize.Width - (int)Math.Round(10 * scale) - side,
                (int)Math.Round(8 * scale), side, side);
            // The small corner control reserves only the title row. The detail
            // row keeps its original right edge below the button.
            var right = _showQuickAction()
                ? _quickActionButton.Left - (int)Math.Round(6 * scale)
                : ClientSize.Width - (int)Math.Round(4 * scale);
            _statusLabel.Width = Math.Max(1, right - _statusLabel.Left);
            var offset = _providerIcon?.Visible == true ? _providerIcon.Width
                + Math.Max(1, (int)Math.Round(_detailLabel.Height / 4d)) : 0;
            _detailLabel.Left = _statusLabel.Left + offset;
            _detailLabel.Width = Math.Max(1, ClientSize.Width - (int)Math.Round(4 * scale) - _detailLabel.Left);
        }
        finally { _layingOut = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            KeyboardWtfState.UiChanged -= HandleUiChanged;
            _statusLabel.DpiChangedAfterParent -= RestoreLegacyFontAfterDpi;
            _detailLabel.DpiChangedAfterParent -= RestoreLegacyFontAfterDpi;
            _timer.Dispose();
            _quickActionButton.ActionInvoked -= HandleQuickAction;
            _quickActionTip.Dispose();
            foreach (var image in _providerImages.Values) image.Dispose();
            _providerImages.Clear();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _statusFont.Dispose();
            _detailFont.Dispose();
        }
    }

    private void HandleUiChanged()
    {
        if (IsDisposed)
            return;

        // A handle-less WinForms control cannot reliably determine whether a
        // caller is on another thread. Never let a background status event
        // become the thread that creates this form's native window.
        if (!IsHandleCreated)
        {
            if (Environment.CurrentManagedThreadId != _uiThreadId)
                return;
            InitializeUiThreadHandle();
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke((Action)RefreshState);
            }
            catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated)
            {
                // Shutdown raced a best-effort visual status update.
            }
            return;
        }

        RefreshState();
        // Force the tiny overlay to paint before a slow audio driver blocks
        // the UI thread while opening the selected microphone.
        Update();
    }

    private void RefreshState()
    {
        var phase = KeyboardWtfState.UiPhase;
        var taskDisplay = KeyboardWtfState.LongTaskDisplayState();
        var taskDetail = taskDisplay.Detail;
        var hasTask = KeyboardWtfState.HasLongTaskUiOwner;
        var action = _getQuickAction() ?? PopupQuickAction.Hidden;
        var hasCapture = action.Kind == PopupQuickActionKind.Done || _pendingCompletionAction is not null;
        var hasLiveOwner = action.Kind is PopupQuickActionKind.Mute or PopupQuickActionKind.Unmute
            && !string.IsNullOrEmpty(action.Owner);
        var microphoneMuted = action.Kind == PopupQuickActionKind.Unmute;
        var muteChanged = _microphoneMuted != microphoneMuted;
        _microphoneMuted = microphoneMuted;
        var presented = _quickActionPresentation.Select(action, phase,
            hasTask && taskDisplay.ConnectorId.Length > 0,
            KeyboardWtfState.InputLevelDb, _monotonicClock());
        _quickActionButton.Action = presented;
        _quickActionButton.Enabled = presented.Kind != PopupQuickActionKind.Done || presented != _pendingCompletionAction;
        _quickActionButton.Visible = _showQuickAction() && presented.Kind != PopupQuickActionKind.Hidden;
        _quickActionTip.SetToolTip(_quickActionButton, _quickActionButton.AccessibleName);
        LayoutQuickAction();
        // A camera/tool can publish Done before Live delivers its next reply.
        // Keep the view visible for that existing session owner, independently
        // of whether its quick-action button is enabled. Do not change phases
        // or extend the session; ending it removes the owner and restores hide.
        if (phase == VoiceUiPhase.Idle && !hasTask && !hasCapture && !hasLiveOwner)
        {
            SetConnectorActivity("");
            if (Visible)
                Hide();
            _lastPhase = phase;
            return;
        }

        var terminal = phase is VoiceUiPhase.Done
            or VoiceUiPhase.Cancelled
            or VoiceUiPhase.Error;
        if (terminal && !hasTask && !hasCapture && !hasLiveOwner
            && DateTime.UtcNow - KeyboardWtfState.UiPhaseStartedUtc > TimeSpan.FromSeconds(3.2))
        {
            SetConnectorActivity("");
            if (Visible)
                Hide();
            _lastPhase = phase;
            return;
        }

        if (!Visible)
        {
            PositionOverlay();
            Show();
        }

        _statusLabel.Text = hasTask && phase == VoiceUiPhase.Idle ? KeyboardWtfState.AssistantName : KeyboardWtfState.UiTitle;
        _detailLabel.Text = BuildDetail(phase, KeyboardWtfState.UiDetail,
            taskDetail: taskDetail, microphoneMuted: _microphoneMuted);
        var decorated = taskDetail.Length > 0 && taskDisplay.ConnectorId.Length > 0;
        var changed = _connectorActivity != decorated;
        SetConnectorActivity(decorated ? taskDisplay.ConnectorId : "");
        _orbColor = decorated ? Color.FromArgb(99, 139, 235)
            : _microphoneMuted && phase == VoiceUiPhase.Listening ? Color.FromArgb(105, 115, 130) : PhaseColor(phase);

        if (phase != _lastPhase || changed)
        {
            _orbPhaseStarted = Environment.TickCount64;
            _orb.Invalidate();
        }
        else if (muteChanged || phase == VoiceUiPhase.Speaking)
            _orb.Invalidate();
        else if ((phase == VoiceUiPhase.Listening || decorated && phase != VoiceUiPhase.Speaking)
            && Math.Abs(KeyboardWtfState.InputLevelDb - _lastLevelDb) > 2)
        {
            _orb.Invalidate();
        }

        _lastLevelDb = KeyboardWtfState.InputLevelDb;
        _lastPhase = phase;
    }

    private async void HandleQuickAction(PopupQuickAction action)
    {
        if (action is null || !_showQuickAction() || _executeQuickAction is null) return;
        // Finishing capture is single-flight. Live mute/unmute already has an
        // owner/revision CAS and changes locally before its wire flush; keep
        // Unmute reachable while a prior flush is waiting on the socket lock.
        var finishingCapture = action.Kind == PopupQuickActionKind.Done;
        if (finishingCapture)
        {
            if (action == _pendingCompletionAction) return;
            _pendingCompletionAction = action;
            _quickActionButton.Enabled = _quickActionButton.Action != action;
        }
        try
        {
            // This awaited task belongs to the original capture/Live owner.
            // Hiding or disposing the view never cancels its work.
            await _executeQuickAction(action);
        }
        catch (Exception exception)
        {
            AppLog.Warning(exception, "Popup quick action failed");
        }
        finally
        {
            if (finishingCapture && _pendingCompletionAction == action) _pendingCompletionAction = null;
            if (!IsDisposed && !Disposing) RefreshState();
        }
    }

    private void SetConnectorActivity(string provider)
    {
        _connectorActivity = provider.Length > 0;
        ((ConnectorActivityLabel)_statusLabel).FitInk = _connectorActivity;
        _detailLabel.Shiny = _connectorActivity;
        _providerIcon.Image = ProviderImage(provider);
        _providerIcon.Visible = _providerIcon.Image is not null;
        _providerIcon.Left = _statusLabel.Left;
        // Fit the mark inside the detail row at its actual scaled height. Both
        // its clear inset and text gap scale with that row, not a fixed DPI.
        _providerIcon.Size = new Size(_detailLabel.Height, _detailLabel.Height);
        _providerIcon.Top = _detailLabel.Top;
        var offset = _providerIcon.Image is null ? 0 : _providerIcon.Width
            + Math.Max(1, (int)Math.Round(_detailLabel.Height / 4d));
        _detailLabel.Left = _statusLabel.Left + offset;
        _detailLabel.Width = _statusLabel.Width - offset;
        LayoutQuickAction();
    }

    private Image ProviderImage(string provider)
    {
        if (provider.Length == 0) return null;
        if (_providerImages.TryGetValue(provider, out var image)) return image;
        // Only bundled marks can be displayed. No URLs, provider/account IDs or
        // remote image loading; resource names are not rendered to the customer.
        using var stream = typeof(VoiceOverlayForm).Assembly.GetManifestResourceStream("icons/connectors/" + provider + ".png");
        if (stream is null) return null;
        using var original = Image.FromStream(stream);
        image = new Bitmap(original);
        _providerImages.Add(provider, image);
        return image;
    }

    private void PaintOrb(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.ScaleTransform(_orb.ClientSize.Width / 42f, _orb.ClientSize.Height / 42f);
        using var brush = new SolidBrush(_orbColor);
        e.Graphics.FillEllipse(brush, 2, 2, 38, 38);

        // Connector work keeps its blue outer color while using the existing
        // real input meter between speech replies. This is presentation only;
        // it neither changes voice phase nor fabricates a thinking pulse.
        var phase = KeyboardWtfState.UiPhase;
        var meterPhase = _connectorActivity && phase != VoiceUiPhase.Speaking
            ? VoiceUiPhase.Listening : phase;
        if (_microphoneMuted && meterPhase == VoiceUiPhase.Listening)
            return;

        var diameter = InnerOrbDiameter(meterPhase,
            KeyboardWtfState.InputLevelDb, Environment.TickCount64 - _orbPhaseStarted);
        if (diameter == 0)
            return;

        var offset = (42 - diameter) / 2;
        using var center = new SolidBrush(Color.White);
        e.Graphics.FillEllipse(center, offset, offset, diameter, diameter);
    }

    internal static int InnerOrbDiameter(VoiceUiPhase phase, double inputLevelDb, long elapsedMs)
    {
        // Listening keeps its real microphone meter. Speaking is a small
        // activity pulse on the existing UI timer, not a fabricated output
        // level or an audio callback; playback and interruption stay untouched.
        var normalized = phase switch
        {
            VoiceUiPhase.Listening => Math.Clamp((inputLevelDb + 60) / 60, 0, 1),
            VoiceUiPhase.Speaking => (1 - Math.Cos(Math.Max(0, elapsedMs) % 1200 * Math.PI / 600)) / 2,
            _ => -1,
        };
        return normalized < 0 ? 0 : 12 + (int)(normalized * 17);
    }

    private void PositionOverlay()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 18);
    }

    private static Region RoundedRegion(Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }

    private static string Shorten(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Ready for Ctrl+Alt+Q";
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 1)] + "...";
    }

    internal static string BuildDetail(VoiceUiPhase phase, string detail, TimeSpan? elapsedOverride = null,
        string taskDetail = null, bool microphoneMuted = false)
    {
        if (!string.IsNullOrWhiteSpace(taskDetail))
            return Shorten(taskDetail, 88);
        if (microphoneMuted)
            return "Microphone muted";
        var elapsed = Math.Max(0, (int)(elapsedOverride
            ?? DateTime.UtcNow - KeyboardWtfState.UiPhaseStartedUtc).TotalSeconds);
        var activeDetail = (phase is VoiceUiPhase.Thinking or VoiceUiPhase.Executing)
            && elapsed >= 4
            ? $"{detail} — working on it"
            : detail;
        return phase switch
        {
            VoiceUiPhase.Listening => Shorten(detail, 88),
            VoiceUiPhase.Transcribing or VoiceUiPhase.Thinking or VoiceUiPhase.Executing =>
                Shorten($"{activeDetail}  {elapsed}s - Ctrl+Alt+X cancels", 88),
            _ => Shorten(detail, 88),
        };
    }

    private static Color PhaseColor(VoiceUiPhase phase) => phase switch
    {
        VoiceUiPhase.Listening => Color.FromArgb(239, 68, 68),
        VoiceUiPhase.Transcribing => Color.FromArgb(59, 130, 246),
        VoiceUiPhase.Thinking => Color.FromArgb(168, 85, 247),
        VoiceUiPhase.Executing => Color.FromArgb(245, 158, 11),
        VoiceUiPhase.Speaking => Color.FromArgb(20, 184, 166),
        VoiceUiPhase.Done => Color.FromArgb(34, 197, 94),
        VoiceUiPhase.Cancelled => Color.FromArgb(245, 158, 11),
        VoiceUiPhase.Error => Color.FromArgb(220, 38, 38),
        _ => Color.FromArgb(90, 90, 90),
    };
}
