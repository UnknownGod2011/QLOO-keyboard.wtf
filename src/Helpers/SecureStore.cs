namespace KeyboardWtf.Helpers;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// DPAPI-backed at-rest encryption for API keys, webhook URLs, and other sensitive
/// values persisted in QLOO settings.
/// </summary>
internal static class SecureStore
{
    public const string EncryptedPrefix = "enc:v1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("qloo.jarvis.Secret.v1");


    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;
        if (!OperatingSystem.IsWindows())
            throw new SecretPersistenceException();

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(protectedBytes);
        }
        catch (Exception)
        {
            AppLog.Warning("SecureStore protection failed; no plaintext fallback is permitted.");
            throw new SecretPersistenceException();
        }
    }

    public static string Unprotect(string stored) => Unprotect(stored, out _);

    public static string Unprotect(string stored, out bool requiresResave)
    {
        requiresResave = false;
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            requiresResave = true;
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            AppLog.Warning("SecureStore.Unprotect called on non-Windows; ciphertext is unavailable");
            return string.Empty;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
        }
        catch (FormatException ex)
        {
            AppLog.Warning(ex, "SecureStore.Unprotect: stored value is not valid base64");
            return string.Empty;
        }

        try
        {
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            AppLog.Warning(ex, "SecureStore.Unprotect failed; caller will see an empty value");
            return string.Empty;
        }
    }
}

// Never attach an upstream message/inner exception that could contain a secret.
internal sealed class SecretPersistenceException : InvalidOperationException
{
    internal const string SafeMessage = "Settings could not be saved securely. Your previous saved settings were preserved. Please try again.";
    public SecretPersistenceException() : base(SafeMessage) { }
}
