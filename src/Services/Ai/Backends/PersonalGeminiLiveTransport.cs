namespace KeyboardWtf.Services.Ai.Backends;

using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Connection/setup adapter only. Audio, tools and resumption remain owned by Jarvis.</summary>
internal sealed class PersonalGeminiLiveTransport : IDisposable
{
    internal static readonly Uri Endpoint = new("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");
    // Preserve the effective managed proxy instruction, not the less constrained legacy direct-key path.
    internal const string SafetyInstruction =
        "This is keyboard.wtf Jarvis. Follow the user's current request, but never expose credentials, " +
        "bypass authentication or security controls, or claim a local action succeeded without a tool result. " +
        "Passwords, OTPs, CAPTCHAs, payments, destructive actions, and public sends/submissions require explicit user control.";
    private static readonly HashSet<string> Voices = new(StringComparer.Ordinal)
        { "Kore", "Orus", "Puck", "Charon", "Aoede", "Zephyr", "Leda", "Fenrir" };
    private readonly PersonalApiVault _vault;
    private readonly HttpMessageInvoker _http = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private readonly Func<ClientWebSocket, Uri, HttpMessageInvoker, CancellationToken, Task> _connect;

    internal PersonalGeminiLiveTransport(PersonalApiVault vault,
        Func<ClientWebSocket, Uri, HttpMessageInvoker, CancellationToken, Task> connect = null)
    {
        _vault = vault;
        _connect = connect ?? ((socket, endpoint, http, token) => socket.ConnectAsync(endpoint, http, token));
    }

    internal async Task<ClientWebSocket> ConnectAsync(IPersonalCredentialLease credential, string model, CancellationToken token)
    {
        if (model != PersonalGeminiModels.Live) throw new PersonalAiException(PersonalAiFailure.Capability);
        token.ThrowIfCancellationRequested();
        _vault.EnsureDispatchAllowed(credential);
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("x-goog-api-key", _vault.ReadSecretForTransport(credential));
        try
        {
            // No setup, microphone data, or tool results are sent at this boundary.
            await _connect(socket, Endpoint, _http, token);
            token.ThrowIfCancellationRequested();
            return socket;
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException)
        {
            var failure = ClassifyHandshakeFailure(socket.HttpStatusCode);
            socket.Dispose();
            token.ThrowIfCancellationRequested();
            if (failure == PersonalAiFailure.Credential) _vault.RecordCredentialRejection(credential);
            else if (failure == PersonalAiFailure.Quota) _vault.CoolDownUnknownScopePool(TimeSpan.FromMinutes(1));
            // Never retain an underlying exception that could contain a credential header.
            throw new PersonalAiException(failure);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static PersonalAiFailure ClassifyHandshakeFailure(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized => PersonalAiFailure.Credential,
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound => PersonalAiFailure.Capability,
        HttpStatusCode.TooManyRequests => PersonalAiFailure.Quota,
        null or 0 or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            => PersonalAiFailure.PreDispatchConnection,
        _ => PersonalAiFailure.Provider,
    };

    internal static JsonObject PrepareSetup(object original)
    {
        var root = JsonSerializer.SerializeToNode(original) as JsonObject;
        if (root is null || root.Count != 1 || root["setup"] is not JsonObject setup)
            throw new ArgumentException("Personal Live requires a setup message.");
        if (Encoding.UTF8.GetByteCount(root.ToJsonString()) > 2 * 1024 * 1024)
            throw new ArgumentException("Personal Live setup is too large.");
        var instruction = setup["systemInstruction"]?["parts"]?[0]?["text"]?.GetValue<string>() ?? "";
        var voice = setup["generationConfig"]?["speechConfig"]?["voiceConfig"]?["prebuiltVoiceConfig"] as JsonObject
            ?? throw new ArgumentException("Personal Live voice configuration is missing.");
        var name = voice["voiceName"]?.GetValue<string>();
        voice["voiceName"] = name is not null && Voices.Contains(name) ? name : "Kore";
        setup["model"] = "models/" + PersonalGeminiModels.Live;
        setup["systemInstruction"] = new JsonObject
        {
            ["parts"] = new JsonArray(new JsonObject { ["text"] = SafetyInstruction + "\n\nDesktop context:\n" + instruction }),
        };
        // Bound the actual outgoing payload, including the safety prefix.
        // Provider token/context limits remain authoritative; character count
        // is not a token count and must not reject a valid saved setup.
        if (Encoding.UTF8.GetByteCount(root.ToJsonString()) > 2 * 1024 * 1024)
            throw new ArgumentException("Personal Live setup is too large.");
        // The caller's existing catalog, transcription, VAD and compression fields are unchanged.
        return root;
    }

    public void Dispose() => _http.Dispose();
}
