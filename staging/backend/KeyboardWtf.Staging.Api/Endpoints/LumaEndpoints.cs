using System.Text.Json;
using System.Text.Json.Serialization;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LumaVoiceRequest(string Section);

/// <summary>Not registered by the existing paid API. Enabling needs a separate reviewed deployment.</summary>
public static class LumaEndpoints
{
    public static void MapLumaEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/luma/live-token", async (HttpContext context, IIdentityService identities,
            LumaLiveTokenService service, IOptions<LumaLiveOptions> configuration, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store, private";
            context.Response.Headers.Pragma = "no-cache";
            // Bearer owner authentication, not cookies. CORS alone is never authentication.
            // Require exact approved HTTPS origin and JSON to reject cross-site form/opaque requests.
            var origin = context.Request.Headers.Origin.ToString();
            if (!configuration.Value.AllowedOrigins.Contains(origin, StringComparer.Ordinal)
                || !context.Request.HasJsonContentType() || context.Request.ContentLength is > 1024)
                return Results.Json(new { available = false, message = "Luma voice request was not allowed." }, statusCode: 403);
            if (!service.Available)
                return Results.Json(new { available = false, message = "Luma voice is not configured yet. The tour and text help still work." }, statusCode: 503);
            var caller = await identities.AuthenticateAsync(context, token);
            if (caller is null || caller.AuthenticationKind != "firebase")
                return Results.Json(new { available = false, message = "Sign in to talk with Luma. The tour and text help still work." }, statusCode: 401);
            // Read a bounded body before deserializing, even with chunked transfer encoding.
            using var body = new MemoryStream();
            var chunk = new byte[256];
            int count;
            while ((count = await context.Request.Body.ReadAsync(chunk, token)) > 0)
            {
                if (body.Length + count > 1024)
                    return Results.Json(new { available = false, message = "Invalid Luma help request." }, statusCode: 400);
                body.Write(chunk, 0, count);
            }
            LumaVoiceRequest? request;
            try { request = JsonSerializer.Deserialize<LumaVoiceRequest>(body.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { return Results.Json(new { available = false, message = "Invalid Luma help request." }, statusCode: 400); }
            if (request?.Section is null || !LumaLiveTokenService.Sections.Contains(request.Section))
                return Results.Json(new { available = false, message = "Unknown Luma help section." }, statusCode: 400);
            try { return Results.Ok(await service.IssueAsync(caller.UserId, request.Section, token)); }
            catch (LumaVoiceUnavailableException exception)
            { return Results.Json(new { available = false, message = exception.Message }, statusCode: 503); }
            catch (UnauthorizedAccessException)
            { return Results.Json(new { available = false, message = "This account is not active." }, statusCode: 401); }
        }).RequireRateLimiting("api");
    }
}
