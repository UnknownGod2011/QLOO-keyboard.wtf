using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationSettingsTests(ITestOutputHelper output)
{
    [Fact]
    public void New_install_is_disabled_with_memory_off()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-browser-settings-{Guid.NewGuid():N}.json");
        try
        {
            WithSaveFailureDiagnostics(path, () =>
            {
                var settings = new SettingsService(path);
                settings.Load();

                Assert.False(settings.Current.BrowserAutomationEnabled);
                Assert.False(settings.HasBrowserAutomationConsent);
                Assert.False(settings.Current.BrowserAutomationMemoryEnabled);
                Assert.Equal(
                    "gemini-3.1-flash-lite-preview",
                    settings.Current.BrowserAutomationModel);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Browser_settings_persist()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-browser-settings-{Guid.NewGuid():N}.json");
        try
        {
            WithSaveFailureDiagnostics(path, () =>
            {
                var first = new SettingsService(path);
                first.Load();
                first.SaveBrowserAutomationConsent(true);
                first.SaveBrowserAutomationEnabled(true);
                first.SaveBrowserAutomationModel("gemini-3-flash-preview");

                var second = new SettingsService(path);
                second.Load();
                Assert.True(second.HasBrowserAutomationConsent);
                Assert.True(second.Current.BrowserAutomationEnabled);
                Assert.Equal("gemini-3-flash-preview", second.Current.BrowserAutomationModel);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Browser_automation_cannot_be_enabled_before_affirmative_consent()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-browser-consent-{Guid.NewGuid():N}.json");
        try
        {
            WithSaveFailureDiagnostics(path, () =>
            {
                var settings = new SettingsService(path);
                settings.Load();
                settings.SaveBrowserAutomationEnabled(true);
                Assert.False(settings.Current.BrowserAutomationEnabled);

                settings.SaveBrowserAutomationConsent(true);
                settings.SaveBrowserAutomationEnabled(true);
                Assert.True(settings.HasBrowserAutomationConsent);
                Assert.True(settings.Current.BrowserAutomationEnabled);

                settings.SaveBrowserAutomationConsent(false);
                Assert.False(settings.HasBrowserAutomationConsent);
                Assert.False(settings.Current.BrowserAutomationEnabled);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private void WithSaveFailureDiagnostics(string ownedSettingsPath, Action action)
    {
        var testThread = Environment.CurrentManagedThreadId;
        var records = new List<string>(16);
        var handling = false;
        EventHandler<FirstChanceExceptionEventArgs> listener = (_, args) =>
        {
            if (Environment.CurrentManagedThreadId != testThread || handling || records.Count >= 16)
                return;
            handling = true;
            try
            {
                // Inspect only to classify the original synchronous throw site;
                // no stack, message, exception data, path, or content is emitted.
                var methods = new StackTrace(false).GetFrames()
                    .Select(frame => frame.GetMethod()).Where(method => method is not null).ToArray();
                if (!methods.Any(method => method.DeclaringType == typeof(SettingsService)
                    && method.Name is "SaveSnapshot" or "ProtectSecretValue"))
                    return;
                var stage = methods.Any(method => method.DeclaringType == typeof(File) && method.Name == "Move")
                    ? "move"
                    : methods.Any(method => method.DeclaringType == typeof(File) && method.Name == "WriteAllText")
                        ? "write"
                        : methods.Any(method => method.DeclaringType?.Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true)
                            ? "serialize"
                            : "other";
                records.Add($"type={args.Exception.GetType().Name} hresult=0x{args.Exception.HResult.ToString("X8", CultureInfo.InvariantCulture)} "
                    + $"stage={stage} finalExists={File.Exists(ownedSettingsPath)} temporaryExists={File.Exists(ownedSettingsPath + ".tmp")}");
            }
            catch
            {
                // Diagnostics must not replace the original test failure.
            }
            finally { handling = false; }
        };
        AppDomain.CurrentDomain.FirstChanceException += listener;
        try
        {
            action();
        }
        catch
        {
            try
            {
                output.WriteLine($"Settings first-chance diagnostics: records={records.Count}");
                foreach (var record in records) output.WriteLine(record);
            }
            catch
            {
                // Preserve the original exception even if test output fails.
            }
            throw;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= listener;
        }
    }
}
