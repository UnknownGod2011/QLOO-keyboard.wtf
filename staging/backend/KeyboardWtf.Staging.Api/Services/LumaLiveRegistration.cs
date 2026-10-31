namespace KeyboardWtf.Staging.Api.Services;

/// <summary>Explicit registration only; existing Program/paid endpoints do not call this.</summary>
public static class LumaLiveRegistration
{
    public static IServiceCollection AddLumaLiveServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LumaLiveOptions>().Bind(configuration.GetSection(LumaLiveOptions.SectionName));
        services.AddSingleton<ILumaServerCredential, LumaEnvironmentCredential>();
        services.AddSingleton<ILumaGrantAdmission, FirestoreLumaGrantAdmission>();
        // Never forward a permanent custom x-goog-api-key header through redirects.
        services.AddHttpClient<LumaLiveTokenService>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
