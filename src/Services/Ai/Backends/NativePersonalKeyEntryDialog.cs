namespace KeyboardWtf.Services.Ai.Backends;

using System.Drawing;
using System.Windows.Forms;

/// <summary>Independent, bounded STA dialog; no raw key is sent through the browser.</summary>
internal sealed class NativePersonalKeyEntryDialog : IPersonalKeyEntryDialog
{
    private readonly object _pendingGate = new();
    private Form _pendingForm;

    public bool RevealPending()
    {
        Form form;
        lock (_pendingGate) form = _pendingForm;
        if (form is null) return false;
        try
        {
            form.BeginInvoke((Action)(() => BringToFront(form)));
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public Task<PersonalKeyEntry> ShowAsync(bool replacing, CancellationToken token) => RunDialogAsync(() =>
    {
        var form = CreateForm(replacing ? "Replace Personal Gemini key" : "Add Personal Gemini key", 300);
        var labelCaption = new Label { Text = "Label (optional)", Left = 20, Top = 20, AutoSize = true };
        var label = new TextBox { Left = 20, Top = 44, Width = 430, MaxLength = 64, Enabled = !replacing };
        var keyCaption = new Label { Text = "Gemini API key", Left = 20, Top = 82, AutoSize = true };
        var key = new TextBox { Left = 20, Top = 106, Width = 430, MaxLength = 512, UseSystemPasswordChar = true };
        var note = new Label { Left = 20, Top = 144, Width = 430, Height = 64,
            Text = "Saved only in Windows-encrypted local storage. Validation contacts Google for model metadata, not generation. Unverified additions are saved inactive; failed replacements keep the old key." };
        var save = new Button { Text = "Validate & save", Left = 178, Top = 226, Width = 142, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 332, Top = 226, Width = 118, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { labelCaption, label, keyCaption, key, note, save, cancel });
        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Shown += (_, _) => key.Focus();
        return (form, (Func<PersonalKeyEntry>)(() => new(label.Text, key.Text)), (Action)(() => { key.Clear(); label.Clear(); }));
    }, token);

    public Task<bool> ConfirmResetAsync(CancellationToken token) => RunDialogAsync(() =>
    {
        var form = CreateForm("Reset Personal API configuration?", 204);
        var note = new Label { Left = 20, Top = 20, Width = 430, Height = 96,
            Text = "This explicitly deletes the Personal key pool and restores Managed policy. Future AI work may consume managed keyboard.wtf usage. Managed sign-in, saved mappings and workflows are not changed.\n\nContinue only if you want this reset." };
        var reset = new Button { Text = "Reset to Managed", Left = 160, Top = 138, Width = 160, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 332, Top = 138, Width = 118, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { note, reset, cancel });
        form.CancelButton = cancel;
        // Deliberately no default Enter action for destructive reset.
        return (form, (Func<bool>)(() => true), (Action)(() => { }));
    }, token);

    private static Form CreateForm(string title, int height) => new()
    {
        Text = title, ClientSize = new Size(474, height), StartPosition = FormStartPosition.CenterScreen,
        FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
        ShowInTaskbar = true, TopMost = true, AutoScaleMode = AutoScaleMode.Dpi,
    };

    private static void BringToFront(Form form)
    {
        if (form.IsDisposed || !form.Visible) return;
        form.WindowState = FormWindowState.Normal;
        form.BringToFront();
        form.Activate();
    }

    private Task<T> RunDialogAsync<T>(Func<(Form Form, Func<T> Read, Action Clear)> create, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            T acceptedValue = default;
            try
            {
                var content = create();
                using var form = content.Form;
                _ = form.Handle;
                form.Shown += (_, _) => BringToFront(form);
                lock (_pendingGate) _pendingForm = form;
                try
                {
                    using var cancelled = token.Register(() =>
                    {
                        try { form.BeginInvoke((Action)(() => form.Close())); }
                        catch (InvalidOperationException) { }
                    });
                    token.ThrowIfCancellationRequested();
                    var accepted = form.ShowDialog() == DialogResult.OK;
                    token.ThrowIfCancellationRequested();
                    acceptedValue = accepted ? content.Read() : default;
                }
                finally
                {
                    lock (_pendingGate)
                    {
                        if (ReferenceEquals(_pendingForm, form)) _pendingForm = null;
                    }
                    content.Clear();
                }
            }
            catch (OperationCanceledException) { result.TrySetCanceled(token); return; }
            catch { result.TrySetException(new InvalidOperationException("The native Personal API dialog could not be opened.")); return; }
            // The existing Settings owner must not finish before field clearing,
            // cancellation-registration disposal and native form disposal.
            result.TrySetResult(acceptedValue);
        }) { IsBackground = true, Name = "Personal API key entry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
