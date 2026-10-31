namespace KeyboardWtf;

using System.Drawing;
using System.Windows.Forms;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using KeyboardWtf.Services.Updates;

internal static class Program
{
    private static FileStream _instanceLock;

    [STAThread]
    private static void Main(string[] args)
    {
#if KEYBOARD_WTF_PAID_PRODUCTION && !KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
        UpdateProcessEntry.RunForCurrentBuild(() => RunOriginal(args), message => ReportStartupRefusal(args, message));
#else
        RunOriginal(args);
#endif
    }

    private static void ReportStartupRefusal(string[] args, string message)
    {
        Environment.ExitCode = 1;
        // Refused native hosts must not connect/auto-start the GUI, write stdout
        // outside the framed protocol, or open a user-blocking message dialog.
        if (!BrowserNativeMessagingHost.IsNativeHostInvocation(args))
            MessageBox.Show(message, QlooProduct.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void RunOriginal(string[] args)
    {
        if (BrowserNativeMessagingHost.IsNativeHostInvocation(args))
        {
            Environment.ExitCode = BrowserNativeMessagingHost.Run();
            return;
        }

        WindowsAppLifecycleService.WaitForRestartParent(args);

        if (!TryAcquireInstanceLock())
            return;

        try
        {
            ApplicationConfiguration.Initialize();
            using var notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "QLOO Jarvis",
                Visible = true,
            };

            using var app = new KeyboardWtfApp(notifyIcon);
            app.Start();
            notifyIcon.ContextMenuStrip = BuildMenu(app);
            notifyIcon.DoubleClick += (_, _) => app.OpenSettings();

            Application.Run();
        }
        finally
        {
            _instanceLock?.Dispose();
            _instanceLock = null;
        }
    }

    private static bool TryAcquireInstanceLock()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDataDir);
            _instanceLock = new FileStream(
                Path.Combine(SettingsService.AppDataDir, "qloo.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static ContextMenuStrip BuildMenu(KeyboardWtfApp app)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Start or end Jarvis", null, (_, _) => app.Commands.ToggleJarvisMode());
        menu.Items.Add("End Jarvis", null, (_, _) => app.Commands.CancelCurrent());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open settings", null, (_, _) => app.OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        return menu;
    }
}
