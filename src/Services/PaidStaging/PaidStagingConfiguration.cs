namespace KeyboardWtf.Services.PaidStaging;

internal sealed record PaidStagingConfiguration(
    bool Enabled,
    bool EmergencyByokEnabled,
    Uri BackendBaseUri,
    Uri WebBaseUri,
    string DeploymentEnvironment = "staging",
    bool PreserveExistingRegistrations = false)
{
    public bool IsProduction => false;

    public static PaidStagingConfiguration FromEnvironment()
    {
        // QLOO never inherits keyboard.wtf's managed or billing endpoints.
        // Native connectors remain available when an independently deployed
        // QLOO connector relay is supplied; otherwise direct Personal Gemini
        // mode continues without pretending that connector authorization
        // exists.
        var backend = ParseBaseUri(
            Environment.GetEnvironmentVariable("QLOO_CONNECTOR_BACKEND_URL"),
            "QLOO_CONNECTOR_BACKEND_URL",
            false);
        var web = ParseBaseUri(
            Environment.GetEnvironmentVariable("QLOO_CONNECTOR_WEB_URL"),
            "QLOO_CONNECTOR_WEB_URL",
            false);
        if ((backend is null) != (web is null))
            throw new InvalidOperationException(
                "QLOO_CONNECTOR_BACKEND_URL and QLOO_CONNECTOR_WEB_URL must be configured together.");
        var enabled = backend is not null;
        return new PaidStagingConfiguration(
            Enabled: enabled,
            EmergencyByokEnabled: false,
            BackendBaseUri: backend,
            WebBaseUri: web,
            DeploymentEnvironment: enabled ? "qloo" : "disabled",
            PreserveExistingRegistrations: true);
    }

    private static Uri ParseBaseUri(string value, string name, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                throw new InvalidOperationException($"{name} is required when paid mode is enabled.");
            return null;
        }
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new InvalidOperationException($"{name} must be an HTTPS or loopback URL.");
        return uri;
    }
}
