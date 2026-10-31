namespace KeyboardWtf.Staging.Api.Services;

// Strict financial configuration reads. Never coerce null, strings or
// fractional amounts into production prices or enabled/disabled flags.
internal static class ProductionConfigurationValues
{
    internal static string Text(IDictionary<string, object> values, string key) =>
        values.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text : throw Invalid(key);

    internal static bool Flag(IDictionary<string, object> values, string key) =>
        values.TryGetValue(key, out var value) && value is bool flag ? flag : throw Invalid(key);

    internal static long Integer(IDictionary<string, object> values, string key, long minimum = 1)
    {
        var number = values.TryGetValue(key, out var value) ? value switch
        {
            long count => count,
            int count => count,
            short count => count,
            byte count => count,
            _ => throw Invalid(key),
        } : throw Invalid(key);
        return number >= minimum ? number : throw Invalid(key);
    }

    private static InvalidOperationException Invalid(string key) =>
        new($"Production configuration field '{key}' is missing or invalid.");
}
