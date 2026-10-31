using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace KeyboardWtf.Staging.Api.Infrastructure;

public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 499;
        }
        catch (Exception exception)
        {
            var (status, title) = exception switch
            {
                ArgumentException => (HttpStatusCode.BadRequest, "Invalid request"),
                KeyNotFoundException => (HttpStatusCode.NotFound, "Not found"),
                AuthenticationRequiredException => (HttpStatusCode.Unauthorized, "Sign-in required"),
                ClientUpgradeRequiredException => ((HttpStatusCode)426, "App update required"),
                UnauthorizedAccessException => (HttpStatusCode.Forbidden, "Access denied"),
                InvalidOperationException when exception.Message.Contains("credits", StringComparison.OrdinalIgnoreCase)
                    => ((HttpStatusCode)402, "Credits required"),
                InvalidOperationException => (HttpStatusCode.Conflict, "Request could not be completed"),
                _ => (HttpStatusCode.InternalServerError, "Unexpected server error"),
            };
            if ((int)status >= 500)
                _logger.LogError(exception, "Unhandled API failure for {Method} {Path}", context.Request.Method, context.Request.Path);
            else
                _logger.LogWarning("API request rejected: {Status} {Title} ({Method} {Path})", (int)status, title, context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                return;
            context.Response.StatusCode = (int)status;
            var problem = new ProblemDetails
            {
                Status = (int)status,
                Title = title,
                Detail = (int)status >= 500 ? "The service could not complete this request." : exception.Message,
                Instance = context.Request.Path,
            };
            if (exception is ManagedAllowanceRefusedException
                && (context.Request.Path == "/v1/ai/generate" || context.Request.Path == "/v1/ai/live"))
            {
                // Additive contract, issued only before the initial reservation commits.
                // Failed WebSocket clients can read the header even without a response body.
                context.Response.Headers["X-Keyboard-Wtf-AI-Refusal"] = "allowance-pre-dispatch-v1";
                problem.Extensions["code"] = "managed_allowance_unavailable";
                problem.Extensions["stage"] = "reservation_pre_dispatch";
                problem.Extensions["reservationCreated"] = false;
                problem.Extensions["providerDispatched"] = false;
            }
            await context.Response.WriteAsJsonAsync(problem);
        }
    }
}

public sealed class ManagedAllowanceRefusedException : InvalidOperationException
{
    public ManagedAllowanceRefusedException()
        : base("No usable credits are available. Add credits to continue.") { }
}

public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException(string message) : base(message) { }
}

public sealed class ClientUpgradeRequiredException : Exception
{
    public ClientUpgradeRequiredException(string minimumVersion)
        : base($"Update keyboard.wtf to version {minimumVersion} or newer before using this AI route.") { }
}
