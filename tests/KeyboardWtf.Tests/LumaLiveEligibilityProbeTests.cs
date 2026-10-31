namespace KeyboardWtf.Tests;

using System.Net.Http;
using System.Security.Cryptography;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;
using Xunit.Abstractions;

/// <summary>Explicit opt-in metadata-only check; ordinary tests never access a real profile/provider.</summary>
public sealed class LumaLiveEligibilityProbeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "LumaMetadataOnly")]
    public async Task Explicitly_approved_model_metadata_only_probe_has_no_inference_or_state_mutation()
    {
        if (Environment.GetEnvironmentVariable("KEYBOARD_WTF_LUMA_METADATA_PROBE") != "1")
        {
            output.WriteLine("Not enabled: no real credential or provider access.");
            return;
        }
        var path = Environment.GetEnvironmentVariable("KEYBOARD_WTF_LUMA_VAULT_PATH");
        Assert.False(string.IsNullOrWhiteSpace(path));
        path = Path.GetFullPath(path);
        Assert.Equal("luma-personal-api-v1.dat", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".configured"));
        var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var vault = new PersonalApiVault(path);
        var before = vault.GetView();
        using var handler = new MetadataOnlyHandler();
        using var service = new LumaLocalTokenService(vault, () => false, handler: handler);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        var model = await service.ProbeEligibilityAsync(deadline.Token);
        var after = vault.GetView();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Policy, after.Policy);
        Assert.Equal(before.Keys, after.Keys);
        Assert.Equal(beforeHash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.InRange(handler.Calls, 1, 2);
        Assert.True(model is LumaLiveProtocol.PreferredModel or LumaLiveProtocol.AlternativeModel);
        output.WriteLine("Metadata eligibility: " + model + "; metadata GETs=" + handler.Calls
            + "; inference=0; Live sessions=0; tokens minted=0; Managed=0; configuration changed=0.");
        // Metadata eligibility is not proof of available Live quota, audible quality or latency.
    }

    private sealed class MetadataOnlyHandler() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.Scheme != "https"
                || request.RequestUri.Host != "generativelanguage.googleapis.com" || request.RequestUri.Query != ""
                || request.RequestUri.AbsolutePath is not ("/v1beta/models/gemini-3.8-live" or "/v1beta/models/gemini-3.1-flash-live-preview")
                || Interlocked.Increment(ref Calls) > 2)
                throw new InvalidOperationException("The metadata-only probe refused a non-metadata request.");
            return base.SendAsync(request, token);
        }
    }
}
