using System.Diagnostics;

namespace KeyboardWtf.Services.Updates;

// Cold-process admission only. Original body/disposal remains synchronous and
// unchanged. This is NOT proof that the OS process or native threads have exited.
internal static class UpdateProcessEntry
{
    internal static bool RequiredForCurrentBuild
    {
        get
        {
#if KEYBOARD_WTF_PAID_PRODUCTION && !KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
            return true;
#else
            return false;
#endif
        }
    }

    internal static bool RunForCurrentBuild(Action originalBody, Action<string> reportRefusal) =>
        RequiredForCurrentBuild
            ? Run(Environment.ProcessPath, originalBody, reportRefusal)
            : InvokeOriginal(originalBody);

    private static bool InvokeOriginal(Action body)
    {
        body();
        return true;
    }

    internal static bool Run(string independentlyTrustedExecutablePath,
        Action originalBody, Action<string> reportRefusal)
    {
        ArgumentNullException.ThrowIfNull(originalBody);
        ArgumentNullException.ThrowIfNull(reportRefusal);
        UpdateInstallationIngressLease entry = null;
        string refusal = null;
        var clock = Stopwatch.StartNew();
        // Another cold entrant briefly holds reservation during shared-activity
        // acquisition. Retry only that known contention, never unknown metadata
        // or access failure. No feature work, provider call or GUI has started.
        while (entry is null)
        {
            try { entry = UpdateInstallationIngressLease.TryEnter(independentlyTrustedExecutablePath); }
            catch
            {
                refusal = "keyboard.wtf could not safely access its installation. Please try again or reinstall.";
                break;
            }
            if (entry is not null) break;
            var remaining = TimeSpan.FromSeconds(2) - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                refusal = "keyboard.wtf's installation is busy. Please try again shortly.";
                break;
            }
            Thread.Sleep(Math.Max(1, (int)Math.Min(25, remaining.TotalMilliseconds)));
        }
        if (entry is null)
        {
            reportRefusal(refusal);
            return false;
        }
        // Deliberately outside the acquisition catch: do not reclassify/swallow
        // existing app failures or overwrite native-host return state.
        using (entry) originalBody();
        return true;
    }
}
