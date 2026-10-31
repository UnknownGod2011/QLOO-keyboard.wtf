namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Native window requests never search browser tabs or inject foreground keys.
// Snapshot identity is retained through dispatch and verification; a window
// opened after the request is not part of its target set.
internal static class NativeWindowCloser
{
    internal sealed record Window(IntPtr Handle, uint ProcessId, uint ThreadId, string ClassName, string ProcessName, string Title)
    {
        public bool IsVisible { get; init; } = true;
        public string Id => Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        public bool IsExplorer => ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            && ClassName is "CabinetWClass" or "ExploreWClass";
        public bool IsBrowser => ProcessName.ToLowerInvariant() is
            "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "arc";
    }

    internal sealed record Outcome(Window[] Targets, Window[] Remaining, int Requested)
    {
        public bool Succeeded => Targets.Length > 0 && Remaining.Length == 0;
    }

    internal static Window[] Enumerate()
    {
        var windows = new List<Window>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && GetWindow(handle, 4) == IntPtr.Zero)
            {
                var window = Read(handle);
                if (window != null && window.Title.Length > 0
                    && (window.ProcessName != "explorer" || window.IsExplorer))
                    windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    internal static Window[] Select(Window[] windows, string target, bool all, string windowId, IntPtr foreground,
        IReadOnlyDictionary<IntPtr, string[]> explorerPaths = null)
    {
        var name = (target ?? "").Trim().ToLowerInvariant();
        Window[] ById(IEnumerable<Window> candidates) => candidates
            .Where(w => string.IsNullOrWhiteSpace(windowId) || w.Id.Equals(windowId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (name.Length == 0)
            return !string.IsNullOrWhiteSpace(windowId) ? ById(windows)
                : all ? [] : windows.Where(w => w.Handle == foreground).ToArray();

        // A folder path is identity, not a title hint. Never close another
        // same-named folder, or a whole multi-tab window containing other paths.
        if (TryNormalizeFolderPath(target, out var folderPath))
            return ById(windows.Where(w => w.IsExplorer && explorerPaths != null
                && explorerPaths.TryGetValue(w.Handle, out var paths) && paths.Length > 0
                && paths.All(path => TryNormalizeFolderPath(path, out var actual)
                    && string.Equals(folderPath, actual, StringComparison.OrdinalIgnoreCase))));

        // These are OS app identities, not natural-language command examples.
        var process = ProcessTarget(name).ProcessName;
        if (process == "explorer")
            return ById(windows.Where(w => w.IsExplorer));

        var byProcess = windows.Where(w => w.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byProcess.Length > 0)
            return ById(byProcess);

        var nativeTitle = (target ?? "").Trim();

        // A website title is not authority to close its entire browser window.
        // Resolve native titles only. Ambiguous matches are returned to the caller.
        return ById(windows.Where(w => !w.IsBrowser
            && (w.Title.Equals(nativeTitle, StringComparison.OrdinalIgnoreCase)
                || (w.IsExplorer
                    && w.Title.Equals(
                        $"{nativeTitle} - File Explorer",
                        StringComparison.OrdinalIgnoreCase)))));
    }

    internal static bool IsKnownAppIdentity(string target) => ProcessTarget(target).IsKnownIdentity;

    // Fallback only after the existing process/title selection found nothing.
    // Match a trusted installed catalog entry to the exact OS app ID or image;
    // neither a fuzzy title nor a webpage title authorizes a native close.
    internal static Window[] SelectInstalledApp(Window[] windows, string target,
        IReadOnlyList<AppCandidate> installedApps, string windowId, Func<uint, string> readApplicationId = null,
        Func<uint, string> readImagePath = null)
    {
        if (TryNormalizeFolderPath(target, out _)) return [];
        var requested = FuzzyMatcher.Normalize(target);
        if (requested.Length == 0) return [];
        var candidates = (installedApps ?? []).Where(app => app is { Trusted: true }
            && ((app.Source == "StartApps"
                    && app.LaunchTarget.StartsWith("appx:", StringComparison.OrdinalIgnoreCase)
                    && app.LaunchTarget.Length > 5)
                || RegisteredImagePath(app).Length > 0)).ToArray();
        var exact = candidates.Where(app => FuzzyMatcher.Normalize(app.Name) == requested).ToArray();
        // An exact label or a trailing name, e.g. Teams in Microsoft Teams.
        // Never use fuzzy canonicalization/ranking for destructive authority.
        var selected = (exact.Length > 0 ? exact : candidates.Where(app =>
            FuzzyMatcher.Normalize(app.Name).EndsWith(" " + requested, StringComparison.Ordinal))).ToArray();
        var identities = selected.Where(app => app.Source == "StartApps")
            .Select(app => app.LaunchTarget[5..]).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        var paths = identities.Length == 0 ? selected.Select(RegisteredImagePath)
            .Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray() : [];
        if (identities.Length != 1 && (identities.Length != 0 || paths.Length != 1)) return [];
        // Preserve StartApps authority, including ambiguity/read failure. AppPaths
        // is an alternative catalog source, never a fallback from a different ID.
        readApplicationId ??= ReadApplicationId;
        readImagePath ??= ReadImagePath;
        return windows.Where(window => !window.IsBrowser && !window.IsExplorer
            && (string.IsNullOrWhiteSpace(windowId)
                || window.Id.Equals(windowId.Trim(), StringComparison.OrdinalIgnoreCase))
            && (identities.Length == 1
                ? identities[0].Equals(readApplicationId(window.ProcessId), StringComparison.Ordinal)
                : TryNormalizeFolderPath(readImagePath(window.ProcessId), out var image)
                    && paths[0].Equals(image, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private static string RegisteredImagePath(AppCandidate app)
    {
        if (app is not { Trusted: true, Source: "AppPaths" }
            || !app.LaunchTarget.StartsWith("path:", StringComparison.OrdinalIgnoreCase)) return "";
        var path = app.LaunchTarget[5..];
        return TryNormalizeFolderPath(path, out var full) && File.Exists(full)
            && AppResolverService.IsTrustedLaunchPath(full) ? full : "";
    }

    private static string ReadImagePath(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) return "";
        var value = new StringBuilder(32768);
        var length = value.Capacity;
        return QueryFullProcessImageName(process, 0, value, ref length) ? value.ToString() : "";
    }

    private static string ReadApplicationId(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // query-limited information only
        if (process.IsInvalid) return "";
        uint length = 0;
        if (GetApplicationUserModelId(process, ref length, null) != 122 || length is < 2 or > 512)
            return "";
        var value = new StringBuilder((int)length);
        return GetApplicationUserModelId(process, ref length, value) == 0 ? value.ToString() : "";
    }

    internal static string KnownProcessName(string target)
    {
        var (processName, isKnownIdentity) = ProcessTarget(target);
        return isKnownIdentity ? processName : null;
    }

    private static (string ProcessName, bool IsKnownIdentity) ProcessTarget(string target)
    {
        var name = (target ?? "").Trim().ToLowerInvariant();
        return name switch
        {
            "file explorer" or "windows explorer" or "explorer.exe" or "explorer" => ("explorer", true),
            "google chrome" or "chrome" => ("chrome", true),
            "microsoft edge" or "edge" or "msedge" => ("msedge", true),
            "word" or "microsoft word" or "winword" => ("winword", true),
            "firefox" or "brave" or "opera" or "vivaldi" or "arc" => (name, true),
            _ when name.EndsWith(".exe", StringComparison.Ordinal) => (name[..^4], true),
            _ => (name, false),
        };
    }

    internal static bool TryNormalizeFolderPath(string value, out string normalized)
    {
        normalized = "";
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value ?? "").Trim();
            if (!Path.IsPathFullyQualified(expanded)) return false;
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static IReadOnlyDictionary<IntPtr, string[]> ReadExplorerFolderPaths(Window[] snapshot, CancellationToken token)
    {
        var handles = snapshot.Where(w => w.IsExplorer).Select(w => w.Handle).ToHashSet();
        var paths = new Dictionary<IntPtr, List<string>>();
        if (handles.Count == 0) return new Dictionary<IntPtr, string[]>();
        object shell = null, windows = null;
        var timer = Stopwatch.StartNew();
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(2))
                throw new TimeoutException("Explorer folder identification took too long; no window was closed.");
        }
        try
        {
            Check();
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true));
            windows = ((dynamic)shell).Windows();
            var count = (int)((dynamic)windows).Count;
            for (var index = 0; index < count; index++)
            {
                object window = null;
                try
                {
                    Check();
                    window = ((dynamic)windows).Item(index);
                    if (window == null) continue;
                    var handle = new IntPtr(Convert.ToInt64(((dynamic)window).HWND));
                    if (!handles.Contains(handle)) continue;
                    if (!paths.TryGetValue(handle, out var locations)) paths[handle] = locations = [];
                    // Unknown or virtual tabs must not authorize closing their
                    // entire window merely because another tab matches.
                    locations.Add("");
                    var location = (string)((dynamic)window).LocationURL;
                    locations[^1] = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile
                        ? uri.LocalPath : location;
                    Check();
                }
                finally
                {
                    ReleaseShellObject(window);
                }
            }
            Check();
            return paths.ToDictionary(p => p.Key, p => p.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        finally { ReleaseShellObject(windows); ReleaseShellObject(shell); }
    }

    private static void ReleaseShellObject(object value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    internal static async Task<Outcome> CloseAsync(
        Window[] targets,
        CancellationToken token,
        Func<IntPtr, Window> read = null,
        Func<IntPtr, bool> requestClose = null,
        TimeSpan? timeout = null,
        Func<Window, bool> validateTarget = null,
        Func<IntPtr, bool> hasVisibleOwnedWindow = null)
    {
        read ??= Read;
        requestClose ??= handle => PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
        hasVisibleOwnedWindow ??= HasVisibleOwnedWindow;
        targets = targets.DistinctBy(w => w.Handle).ToArray();
        var requested = 0;
        var dispatched = new HashSet<IntPtr>();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (validateTarget != null && !validateTarget(target))
                continue; // The requested folder may have navigated elsewhere.
            if (!SameIdentity(target, read(target.Handle)))
                continue; // Already closed, or handle recycled. Never touch its replacement.
            token.ThrowIfCancellationRequested();
            if (requestClose(target.Handle))
            {
                requested++;
                dispatched.Add(target.Handle);
            }
        }
        var watch = Stopwatch.StartNew();
        var hiddenSince = new Dictionary<IntPtr, TimeSpan>();
        bool StillOpen(Window target)
        {
            var current = read(target.Handle);
            if (!SameIdentity(target, current)) return false;
            // A successful window close can retain a hidden HWND for a tray app.
            // Only accept this after our close was sent, and never across a
            // visible owned Save/confirmation dialog. Minimized/cloaked windows
            // retain WS_VISIBLE and therefore continue to count as open.
            if (!dispatched.Contains(target.Handle) || current.IsVisible || hasVisibleOwnedWindow(target.Handle))
            {
                hiddenSince.Remove(target.Handle);
                return true;
            }
            hiddenSince.TryAdd(target.Handle, watch.Elapsed);
            return watch.Elapsed - hiddenSince[target.Handle] < TimeSpan.FromMilliseconds(120);
        }
        Window[] remaining;
        do
        {
            token.ThrowIfCancellationRequested();
            remaining = targets.Where(StillOpen).ToArray();
            if (remaining.Length == 0 || watch.Elapsed >= (timeout ?? TimeSpan.FromSeconds(2)))
                break;
            await Task.Delay(40, token).ConfigureAwait(false);
        } while (true);
        return new Outcome(targets, remaining, requested);
    }

    private static bool HasVisibleOwnedWindow(IntPtr target)
    {
        var found = false;
        var completed = EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var visited = new HashSet<IntPtr>();
            for (var owner = GetWindow(handle, 4); owner != IntPtr.Zero && visited.Add(owner); owner = GetWindow(owner, 4))
            {
                if (owner != target) continue;
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found || !completed; // An incomplete native snapshot cannot prove closure.
    }

    private static bool SameIdentity(Window expected, Window current) => current != null
        && expected.Handle == current.Handle && expected.ProcessId == current.ProcessId
        && expected.ThreadId == current.ThreadId && expected.ClassName == current.ClassName;

    internal static Window Read(IntPtr handle)
    {
        if (!IsWindow(handle))
            return null;
        var thread = GetWindowThreadProcessId(handle, out var pid);
        var className = new StringBuilder(256);
        GetClassName(handle, className, className.Capacity);
        var title = new StringBuilder(GetWindowTextLength(handle) + 1);
        GetWindowText(handle, title, title.Capacity);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new Window(handle, pid, thread, className.ToString(), process.ProcessName, title.ToString())
            { IsVisible = IsWindowVisible(handle) };
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder value, ref int length);
}
