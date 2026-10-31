#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE || KEYBOARD_WTF_CONNECTOR_STAGING_CANDIDATE
namespace KeyboardWtf.Services;

using System.Net;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;

public sealed partial class WebSettingsService
{
    private readonly SemaphoreSlim _privateRecordingAction = new(1, 1);

    private async Task HandlePrivateRecordingAudit(HttpListenerRequest request, HttpListenerResponse response)
    {
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        if (!HasSettingsActionHeader(request)) { response.StatusCode = 403; return; }
        if (request.ContentLength64 is < 2 or > 900000) { response.StatusCode = 413; return; }
        if (!await _privateRecordingAction.WaitAsync(0)) { response.StatusCode = 409; return; }
        byte[] pcm = null;
        var ownedAuditAction = false;
        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync(_cts.Token));
            var action = body.RootElement.GetProperty("action").GetString();
            var live = _app?.PrivateAuditConversation ?? throw new InvalidOperationException();
            if (action == "stop")
            {
                await live.StopPrivateRecordingAuditAsync();
                await WriteJson(response, new { ok = true, stopped = true });
                return;
            }
            var view = _personalApiSettings?.GetState().State;
            if (view?.Policy != AiBackendPolicy.PersonalOnly
                || !view.Keys.Any(key => key.Enabled && key.AuthenticationValidated))
                throw new InvalidOperationException("Personal-only preflight failed.");
            object result;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if (action == "start")
            {
                result = await live.StartPrivateRecordingAuditAsync();
                ownedAuditAction = true;
            }
            else if (action == "replay")
            {
                ownedAuditAction = live.PrivateRecordingAuditActive;
                pcm = Convert.FromBase64String(body.RootElement.GetProperty("pcm16Mono16kBase64").GetString());
                result = await live.ReplayPrivateRecordingAsync(pcm, deadline.Token);
            }
            else if (action == "observe")
            {
                result = live.ObservePrivateRecording(body.RootElement.GetProperty("expectedLogicalSession").GetString());
            }
            else throw new InvalidOperationException("Unknown private audit action.");
            await WriteJson(response, new { ok = true, result });
        }
        catch
        {
            if (ownedAuditAction && _app?.PrivateAuditConversation is { } live)
                await live.StopPrivateRecordingAuditAsync();
            // Never echo audio bodies, credentials, or provider exceptions.
            response.StatusCode = 400;
            await WriteJson(response, new { ok = false, error = "Private recording audit refused or cancelled. No Managed fallback." });
        }
        finally
        {
            if (pcm != null) Array.Clear(pcm);
            _privateRecordingAction.Release();
        }
    }
}
#endif
