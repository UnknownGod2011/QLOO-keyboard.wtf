namespace KeyboardWtf.Services.Connectors;

using System.Net.Mail;
using System.Text.Json;

internal static class ConnectorRetainedMailSelection
{
    // Selection is evidence, not execution authority. Only normalized, verified
    // retained headers may distinguish the user's sent email from another match.
    // A recipient address or the model's arguments cannot identify the sender.
    internal static string Thread(ConnectorSessionResult context, ConnectorMailSelection selection)
    {
        if (selection == ConnectorMailSelection.Unspecified) return context.UniqueMailThread();
        var self = Address(context.AccountLabel);
        if (context.Provider != "gmail" || self is null) return null;
        var threads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in context.Resources.Take(12))
        {
            if (resource.ValueKind != JsonValueKind.Object
                || !resource.TryGetProperty("threadId", out var thread) || thread.ValueKind != JsonValueKind.String
                || thread.GetString() is not { Length: >= 8 and <= 64 } id || !id.All(char.IsAsciiHexDigit)) continue;
            var from = Text(resource, "from");
            if (string.IsNullOrWhiteSpace(from) && resource.TryGetProperty("headers", out var headers)
                && headers.ValueKind == JsonValueKind.Object)
                from = headers.EnumerateObject().Where(property => property.Name.Equals("From", StringComparison.OrdinalIgnoreCase))
                    .Select(property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : "")
                    .SingleOrDefault() ?? "";
            var sender = Address(from);
            if (sender is null) continue;
            var sent = sender.Equals(self, StringComparison.OrdinalIgnoreCase);
            if (selection == ConnectorMailSelection.Sent ? sent : !sent) threads.Add(id);
        }
        return threads.Count == 1 ? threads.Single() : null;
    }

    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Address(string value)
    {
        if (value?.Length is not (> 0 and <= 500) || value.Contains('\r') || value.Contains('\n')) return null;
        try { var address = new MailAddress(value).Address; return address.Contains('@') ? address : null; }
        catch (FormatException) { return null; }
    }
}
