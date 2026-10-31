namespace KeyboardWtf.Helpers;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

[SupportedOSPlatform("windows")]
internal static class ClipboardHelper
{
    public sealed class Snapshot
    {
        internal DataObject Data { get; init; }
        internal bool WasEmpty { get; init; }
    }

    public static string GetText()
    {
        string result = null;
        var thread = new Thread(() => result = GetClipboardText());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return result ?? "";
    }

    public static bool SetText(string text)
    {
        bool success = false;
        var thread = new Thread(() => success = RetryClipboardOperation(
            () => SetClipboardText(text)));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return success;
    }

    public static uint GetSequenceNumber() => GetClipboardSequenceNumber();

    // Jarvis temporary clipboard operations use OLE's owner window instead
    // of the legacy NULL-owner setter used by unrelated destination owners.
    public static bool SetOwnedText(string text)
    {
        var success = false;
        var thread = new Thread(() => success = RetryClipboardOperation(() =>
        {
            var data = new DataObject(DataFormats.UnicodeText, text);
            Clipboard.SetDataObject(data, true, 0, 0);
            return true;
        }));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return success;
    }

    public static bool IsCurrentProcessOwner()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out var processId);
        return processId == Environment.ProcessId;
    }

    // OLE/shell formats whose data is a live COM object or a reference back into the source app
    // (embedded objects, links, virtual files, metafiles). Their objects belong to the capture
    // thread's apartment; restoring them after that thread ends made ole32 crash the whole app
    // (access violation 0xc0000005), reproduced with Word content on the clipboard.
    internal static readonly IReadOnlySet<string> NonRestorableFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Ole Private Data", "Object Descriptor", "Link Source", "Link Source Descriptor", "Embed Source",
        "Embedded Object", "ObjectLink", "OwnerLink", "Native", "DataObject", "FileContents",
        "FileGroupDescriptor", "FileGroupDescriptorW", "EnhancedMetafile", "MetaFilePict",
    };

    private const long MaxRestorableBytes = 32L * 1024 * 1024;

    /// <summary>Detached copy of clipboard data that is safe to put back on the clipboard from
    /// another thread, or null when the value cannot be restored safely.</summary>
    internal static object ToRestorableValue(object value) => value switch
    {
        null => null,
        string text => text,
        string[] list => (string[])list.Clone(),
        byte[] bytes => bytes.LongLength <= MaxRestorableBytes ? (byte[])bytes.Clone() : null,
        MemoryStream stream => stream.Length <= MaxRestorableBytes ? new MemoryStream(stream.ToArray()) : null,
        System.Drawing.Bitmap bitmap => new System.Drawing.Bitmap(bitmap),
        _ => null, // COM objects, native-storage streams, metafiles, serialized managed objects
    };

    public static Snapshot Capture()
    {
        Snapshot snapshot = null;
        var thread = new Thread(() =>
        {
            for (var attempt = 0; attempt < 5 && snapshot is null; attempt++)
            {
                try
                {
                    var source = Clipboard.GetDataObject();
                    if (source == null)
                    {
                        snapshot = new Snapshot { WasEmpty = true };
                        return;
                    }
                    var copy = new DataObject();
                    foreach (var format in source.GetFormats())
                    {
                        if (NonRestorableFormats.Contains(format))
                            continue;
                        object value = null;
                        try
                        {
                            value = source.GetData(format);
                            var restorable = ToRestorableValue(value);
                            if (restorable != null)
                                copy.SetData(format, restorable);
                        }
                        catch { }
                        finally
                        {
                            // Release apartment-bound objects on this (still alive) STA thread and
                            // dispose originals that were copied; never let them outlive the capture.
                            try
                            {
                                if (value != null && Marshal.IsComObject(value))
                                    Marshal.FinalReleaseComObject(value);
                                else if (value is IDisposable disposable)
                                    disposable.Dispose();
                            }
                            catch { }
                        }
                    }
                    snapshot = new Snapshot { Data = copy, WasEmpty = false };
                }
                catch
                {
                    if (attempt < 4)
                        Thread.Sleep(20 * (attempt + 1));
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return snapshot;
    }

    public static bool Restore(Snapshot snapshot)
    {
        if (snapshot == null)
            return false;
        var success = false;
        var thread = new Thread(() =>
        {
            success = RetryClipboardOperation(() =>
            {
                try
                {
                    if (snapshot.WasEmpty)
                        Clipboard.Clear();
                    else if (snapshot.Data != null)
                        Clipboard.SetDataObject(snapshot.Data, true);
                    else
                        return false;
                    return true;
                }
                catch { return false; }
            });
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return success;
    }

    private static bool RetryClipboardOperation(Func<bool> operation)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (operation())
                    return true;
            }
            catch { }
            if (attempt < 4)
                Thread.Sleep(20 * (attempt + 1));
        }
        return false;
    }

    private static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return "";
        try
        {
            var handle = GetClipboardData(13);
            if (handle == IntPtr.Zero)
                return "";
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return "";
            try { return Marshal.PtrToStringUni(pointer) ?? ""; }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    private static bool SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var hGlobal = GlobalAlloc(0x0002, (UIntPtr)bytes);
            if (hGlobal == IntPtr.Zero) return false;
            var locked = GlobalLock(hGlobal);
            if (locked == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
            Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
            Marshal.WriteInt16(locked, text.Length * 2, 0);
            GlobalUnlock(hGlobal);
            if (SetClipboardData(13, hGlobal) == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint f, IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint f, UIntPtr n);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);
}
