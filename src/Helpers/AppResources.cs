namespace KeyboardWtf;

using System.Reflection;
using System.Text.RegularExpressions;

internal static class AppResources
{
    private static Assembly _assembly;

    public static void Init(Assembly assembly) => _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));

    public static string[] FindFiles(string regexPattern)
    {
        EnsureInitialized();
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return _assembly.GetManifestResourceNames().Where(name => regex.IsMatch(name)).ToArray();
    }

    public static string FindFile(string fileName)
    {
        EnsureInitialized();
        var normalized = fileName.Replace('\\', '/');
        var found = _assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
        return found ?? throw new FileNotFoundException($"Embedded resource not found: {fileName}");
    }

    public static Stream GetStream(string resourceName)
    {
        EnsureInitialized();
        return _assembly.GetManifestResourceStream(FindFile(resourceName))
            ?? throw new FileNotFoundException($"Embedded resource stream not found: {resourceName}");
    }

    public static string ReadTextFile(string resourceName)
    {
        using var stream = GetStream(resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[] ReadBinaryFile(string resourceName)
    {
        using var stream = GetStream(resourceName);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static void ExtractFile(string resourceName, string filePathName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePathName)!);
        File.WriteAllBytes(filePathName, ReadBinaryFile(resourceName));
    }

    private static void EnsureInitialized()
    {
        if (_assembly == null)
            Init(Assembly.GetExecutingAssembly());
    }
}
