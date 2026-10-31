using System.Globalization;

namespace KeyboardWtf.Services.Updates;

// Declared metadata, NOT proof of the identity/liveness of an OS process.
internal sealed record DeclaredUpdateParent(int ProcessId, long CreationTimeUtcTicks, string ExecutablePath);

// Initially UNWIRED: validates and preserves argument boundaries, without
// opening files/processes or establishing trust. The installer has only a
// non-disruptive /KWTUPDATE branch, not a parent handshake. A future adapter
// must use ProcessStartInfo.ArgumentList,
// independently authenticate the parent and wait for its held process handle.
internal sealed class UpdateInstallerInvocation
{
    internal PreparedUpdate Prepared { get; }
    internal DeclaredUpdateParent Parent { get; }
    internal string TargetExecutablePath { get; }
    internal IReadOnlyList<string> Arguments { get; }

    private UpdateInstallerInvocation(PreparedUpdate prepared, DeclaredUpdateParent parent,
        string targetExecutablePath, string[] arguments)
    {
        Prepared = prepared;
        Parent = parent;
        TargetExecutablePath = targetExecutablePath;
        Arguments = Array.AsReadOnly(arguments);
    }

    internal static UpdateInstallerInvocation Create(PreparedUpdate prepared,
        DeclaredUpdateParent parent, string trustedInstallationExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(trustedInstallationExecutablePath);
        ArgumentNullException.ThrowIfNull(prepared.Update);
        if (parent.ProcessId <= 0 || parent.CreationTimeUtcTicks <= 0
            || parent.CreationTimeUtcTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("The declared update parent is invalid.");

        UpdatePathIdentity.RequireCanonicalLocalPath(trustedInstallationExecutablePath);
        UpdatePathIdentity.RequireCanonicalLocalPath(parent.ExecutablePath);
        UpdatePathIdentity.RequireCanonicalLocalPath(prepared.InstallerPath);
        if (!string.Equals(Path.GetFileName(trustedInstallationExecutablePath), "keyboard.wtf.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parent.ExecutablePath, trustedInstallationExecutablePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(prepared.InstallerPath), "keyboard.wtf-setup.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The declared update installation paths are invalid.");

        var update = prepared.Update;
        if (update.Version is null || update.Version.Build < 0 || update.Version.Revision != -1)
            throw new InvalidDataException("The prepared update version is invalid.");
        if (update.Channel is not ("stable" or "beta"))
            throw new InvalidDataException("The prepared update channel is invalid.");

        return new UpdateInstallerInvocation(prepared, parent, trustedInstallationExecutablePath,
        [
            "/SILENT", "/KWTUPDATE",
            "/NOCLOSEAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS",
            "/NORESTART", "/RESTARTEXITCODE=3010",
            "/KWT_PARENT_PID=" + parent.ProcessId.ToString(CultureInfo.InvariantCulture),
            "/KWT_PARENT_STARTED_UTC_TICKS=" + parent.CreationTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
            "/KWT_PARENT_EXE=" + trustedInstallationExecutablePath,
            "/KWT_TARGET_VERSION=" + update.Version.ToString(3),
            "/KWT_TARGET_CHANNEL=" + update.Channel,
            "/DIR=" + Path.GetDirectoryName(trustedInstallationExecutablePath),
        ]);
    }
}
