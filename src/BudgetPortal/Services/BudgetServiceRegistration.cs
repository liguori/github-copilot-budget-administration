using BudgetPortal.Domain;
using Microsoft.Extensions.Options;

namespace BudgetPortal.Services;

public static class BudgetServiceRegistration
{
    public static IServiceCollection AddBudgetServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<PortalOptions>, PortalOptionsValidator>();
        services.AddOptions<PortalOptions>()
            .Bind(configuration.GetSection("Portal"))
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient("GitHubBudgets", client => client.Timeout = TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(provider => new GitHubApi(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubBudgets"),
            provider.GetRequiredService<IOptions<PortalOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<GitHubInstallationDiscovery>();
        services.AddScoped<IBudgetService, GitHubBudgetService>();
        return services;
    }
}
