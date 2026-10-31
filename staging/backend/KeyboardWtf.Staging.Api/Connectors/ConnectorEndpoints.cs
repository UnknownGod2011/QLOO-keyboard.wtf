using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Endpoints;
using KeyboardWtf.Staging.Api.Services;

namespace KeyboardWtf.Staging.Api.Connectors;

public static class ConnectorEndpoints
{
    public static IServiceCollection AddConnectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        var paid = configuration.GetSection(PaidSystemOptions.SectionName).Get<PaidSystemOptions>() ?? new();
        services.AddOptions<ConnectorOptions>().Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(value => value.Validate(paid), "Connector configuration is unsafe.").ValidateOnStart();
        services.AddSingleton<ConnectorRegistry>();
        services.AddSingleton<IConnectorStore, FirestoreConnectorStore>();
        services.AddHttpClient<IConnectorProvider, ComposioConnectorProvider>(client => client.Timeout = TimeSpan.FromSeconds(35))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ConnectorConnectionService>();
        services.AddScoped<ConnectorTaskService>();
        // AccountDeletionService is singleton. Its cleanup facade shares the
        // same stateless connection service; no separate deletion authority.
        services.AddSingleton<ConnectorDeletionCleanup>();
        services.AddSingleton<ConnectorMaintenance>();
        return services;
    }

    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/v1/connectors").RequireRateLimiting("api")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try { return await next(context); }
                catch (ConnectorProviderException exception)
                {
                    return Results.Problem(statusCode: exception.Code == "rate_limited" ? 429
                        : exception.OutcomeUnknown ? 409 : 503, title: "Connected service unavailable",
                        detail: exception.Message, extensions: new Dictionary<string, object?>
                        { ["code"] = exception.Code, ["outcomeUnknown"] = exception.OutcomeUnknown });
                }
            });
        routes.MapGet("/catalog", (ConnectorRegistry registry) => Results.Ok(registry.Catalog()));
        routes.MapGet("/", async (HttpContext context, IIdentityService identities,
            ConnectorConnectionService connections, CancellationToken ct) =>
            Results.Ok(await connections.SnapshotAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), ct)));
        routes.MapPost("/connect", async (ConnectRequest request, HttpContext context, IIdentityService identities,
            ConnectorConnectionService connections, CancellationToken ct) =>
            Results.Ok(await connections.ConnectAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), request, ct)));
        routes.MapGet("/attempts/{id}", async (string id, HttpContext context, IIdentityService identities,
            ConnectorConnectionService connections, CancellationToken ct) =>
            Results.Ok(await connections.AttemptAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, null, ct)));
        routes.MapPost("/attempts/{id}/complete", async (string id, CompleteConnectRequest request,
            HttpContext context, IIdentityService identities, ConnectorConnectionService connections, CancellationToken ct) =>
            Results.Ok(await connections.AttemptAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, request.State, ct)));
        routes.MapPost("/attempts/{id}/cancel", async (string id, HttpContext context, IIdentityService identities,
            ConnectorConnectionService connections, CancellationToken ct) =>
        {
            await connections.CancelAttemptAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, ct);
            return Results.Ok(new { state = "cancelled" });
        });
        routes.MapDelete("/accounts/{id}", async (string id, HttpContext context, IIdentityService identities,
            ConnectorConnectionService connections, CancellationToken ct) =>
        {
            await connections.DisconnectAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, ct);
            return Results.Ok(new { state = "disconnected" });
        });
        routes.MapPost("/accounts/{id}/default", async (string id, HttpContext context, IIdentityService identities,
            IConnectorStore store, CancellationToken ct) =>
        {
            var caller = await ApiEndpoints.RequireCallerAsync(context, identities, ct);
            await store.SetDefaultAsync(caller.UserId, id, ct);
            return Results.Ok(new { state = "saved" });
        });
        routes.MapPost("/resolve", async (ConnectorResolveRequest request, HttpContext context, IIdentityService identities,
            ConnectorTaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.ResolveAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), request, ct)));
        routes.MapPost("/execute", async (ConnectorExecuteRequest request, HttpContext context, IIdentityService identities,
            ConnectorTaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.ExecuteAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), request, ct)));
        routes.MapGet("/tasks/{id}", async (string id, HttpContext context, IIdentityService identities,
            ConnectorTaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.StatusAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, ct)));
        routes.MapPost("/tasks/{id}/cancel", async (string id, HttpContext context, IIdentityService identities,
            ConnectorTaskService tasks, CancellationToken ct) =>
        {
            await tasks.CancelAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, ct);
            return Results.Ok(new { state = "cancelled" });
        });
        routes.MapPost("/tasks/{id}/complete", async (string id, HttpContext context, IIdentityService identities,
            ConnectorTaskService tasks, CancellationToken ct) =>
        {
            await tasks.FinishAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, ct);
            return Results.Ok(new { state = "completed" });
        });
        routes.MapPost("/tasks/{id}/account", async (string id, ConnectorAccountChangeRequest request, HttpContext context,
            IIdentityService identities, ConnectorTaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.ChangeAccountAsync(await ApiEndpoints.RequireCallerAsync(context, identities, ct), id, request.AccountId, ct)));
        return app;
    }
}

public sealed class ConnectorDeletionCleanup(IServiceScopeFactory scopes)
{
    public async Task CleanupAsync(string uid, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConnectorConnectionService>().CleanupDeletedUserAsync(uid, ct);
    }
}
