using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

// Inert test host only. No app entry point, profile, microphone, provider,
// installer or update peer is referenced or launched by this executable.
// Its only entry into the existing test assembly is the real DPI assertion body.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var dpi)
                || args[2] is not ("PerMonitorV2" or "SystemAware")
                || (args[2] == "SystemAware" ? dpi != 0 : dpi is not (120 or 144 or 192)))
                throw new ArgumentException("Expected the existing test assembly and one supported fixture DPI/host mode.");
            var hostMode = args[2];
            var testsPath = Path.GetFullPath(args[0]);
            if (!File.Exists(testsPath) || Path.GetFileName(testsPath) != "KeyboardWtf.Tests.dll")
                throw new ArgumentException("The existing desktop test assembly is required.");

            // Must precede loading the tests/SUT and creating any fixture HWND.
            // This process exits afterward; the app's global DPI policy is untouched.
            if (!Application.SetHighDpiMode(hostMode == "SystemAware"
                ? HighDpiMode.SystemAware : HighDpiMode.PerMonitorV2))
                throw new InvalidOperationException("The isolated fixture could not enable its requested DPI mode.");

            var resolver = new AssemblyDependencyResolver(testsPath);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var resolved = resolver.ResolveAssemblyToPath(name);
                return resolved is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(resolved);
            };
            var tests = AssemblyLoadContext.Default.LoadFromAssemblyPath(testsPath);
            var assertions = tests.GetType("KeyboardWtf.Tests.PopupQuickActionTests", throwOnError: true)!
                .GetMethod("RunDpiFixtureAssertions", BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null, types: [typeof(int), typeof(string)], modifiers: null)
                ?? throw new MissingMethodException("The actual overlay DPI assertion body is unavailable.");
            assertions.Invoke(null, [dpi, hostMode]);
            Console.WriteLine($"POPUP_DPI_PASS|{hostMode}|{dpi}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("POPUP_DPI_FAILED|" + (error is TargetInvocationException invocation
                ? invocation.InnerException ?? invocation : error));
            return 1;
        }
    }
}
