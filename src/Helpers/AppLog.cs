namespace KeyboardWtf;

using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.PaidStaging;

internal static class AppLog
{
    private static readonly object Lock = new();
    private static string _logFile;
    private const long MaximumLogFileBytes = 5L * 1024 * 1024;
    private const int MaximumLogEntryBytes = 32 * 1024;

    public static void Init()
    {
        var dir = Path.Combine(SettingsService.AppDataDir, "logs");
        Directory.CreateDirectory(dir);
        _logFile = Path.Combine(dir, "qloo.log");
        Info("QLOO logging started");
    }

    public static void Verbose(string text) => Write("VERBOSE", text);
    public static void Verbose(Exception ex, string text) => Write("VERBOSE", $"{text}: {FormatException(ex)}");
    public static void Info(string text) => Write("INFO", text);
    public static void Info(Exception ex, string text) => Write("INFO", $"{text}: {FormatException(ex)}");
    public static void Warning(string text) => Write("WARN", text);
    public static void Warning(Exception ex, string text) => Write("WARN", $"{text}: {FormatException(ex)}");
    public static void Error(string text) => Write("ERROR", text);
    public static void Error(Exception ex, string text) => Write("ERROR", $"{text}: {FormatException(ex)}");

    // Provider/native exception messages can contain echoed user content or
    // credentials. Keep operational codes without altering the exception/UI.
    internal static string FormatException(Exception exception)
    {
        if (exception is null) return "exception=none";
        var metadata = $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}";
        if (exception is HttpRequestException { StatusCode: { } status })
            metadata += $" httpStatus={(int)status}";
        if (exception is WebSocketException socket)
            metadata += $" webSocketError={socket.WebSocketErrorCode} nativeError={socket.NativeErrorCode}";
        if (exception is SocketException network)
            metadata += $" socketError={network.SocketErrorCode} nativeError={network.NativeErrorCode}";
        if (exception is PersonalAiException personal)
            metadata += $" personalFailure={personal.Failure} httpStatus={personal.HttpStatusCode?.ToString() ?? "none"}";
        if (exception is PaidApiException paid)
            metadata += $" httpStatus={(int)paid.StatusCode}";
        if (exception is ManagedAllowanceRefusedException)
            metadata += " managedAllowance=preDispatchRefused";
        return metadata;
    }

    private static void Write(string level, string text)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {text}";
        Debug.WriteLine(line);

        if (string.IsNullOrEmpty(_logFile))
            return;

        TryWriteLineToFile(_logFile, line, MaximumLogFileBytes);
    }

    // Only the exact operational log and two generations are owned here.
    // No recursive/age cleanup, retry queue, or user-data retention policy.
    internal static bool TryWriteLineToFile(string logFile, string line, long maximumFileBytes)
    {
        try
        {
            if (maximumFileBytes < 16 || maximumFileBytes > MaximumLogFileBytes
                || !Path.IsPathFullyQualified(logFile) || Path.GetFileName(logFile) != "qloo.log")
                return false;
            logFile = Path.GetFullPath(logFile);
            var first = logFile + ".1";
            var second = logFile + ".2";
            var payload = BoundedLogLine(line ?? "", (int)Math.Min(MaximumLogEntryBytes, maximumFileBytes));
            lock (Lock)
            {
                // Reject links/directories before touching any generation. The
                // GUI's existing profile lock is the disk writer's ownership.
                for (var directory = Path.GetDirectoryName(logFile); !string.IsNullOrEmpty(directory);
                     directory = Path.GetDirectoryName(directory))
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        return false;
                foreach (var path in new[] { logFile, first, second })
                {
                    if (Directory.Exists(path)) return false;
                    if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        return false;
                }
                // Discard legacy oversized operational generations only; never
                // read an unbounded old log into memory or delete similar names.
                foreach (var path in new[] { logFile, first, second })
                    if (File.Exists(path) && new FileInfo(path).Length > maximumFileBytes)
                        File.Delete(path);
                if (File.Exists(logFile) && new FileInfo(logFile).Length > maximumFileBytes - payload.Length)
                {
                    if (File.Exists(second)) File.Delete(second);
                    if (File.Exists(first)) File.Move(first, second);
                    File.Move(logFile, first);
                }
                using var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(payload);
            }
            return true;
        }
        catch { return false; } // Logging must never interrupt a voice command.
    }

    private static byte[] BoundedLogLine(string line, int maximumBytes)
    {
        var newline = Environment.NewLine;
        if ((long)Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(newline) <= maximumBytes)
            return Encoding.UTF8.GetBytes(line + newline);
        var suffix = Encoding.UTF8.GetBytes("[truncated]" + newline);
        var bounded = new byte[maximumBytes];
        var capacity = maximumBytes - suffix.Length;
        var chars = 0;
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > capacity - bytes) break;
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        var written = Encoding.UTF8.GetBytes(line.AsSpan(0, chars), bounded);
        suffix.CopyTo(bounded, written);
        return bounded[..(written + suffix.Length)];
    }
}
