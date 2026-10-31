namespace KeyboardWtf;

/// <summary>
/// Product identity for the isolated hackathon build. Keeping this small and
/// central prevents QLOO from sharing profiles, startup ownership, encrypted
/// values, or Chrome native-host registrations with keyboard.wtf.
/// </summary>
internal static class QlooProduct
{
    internal const string Name = "QLOO";
    internal const string AppDataFolder = "QLOO";
    internal const string AppDataEnvironmentVariable = "QLOO_APPDATA_DIR";
    internal const string ChromeNativeHost = "qloo.jarvis.browser";
    internal const string ChromePipe = "qloo.jarvis.browser.v1";
    internal const string ChromeExtensionId = "cgclpameieckfoacikkebkagipnlnaag";
}
