using System.Security.Cryptography;
using BudgetPortal.Domain;
using BudgetPortal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BudgetPortal.Tests;

public sealed class PortalOptionsValidatorTests
{
    private static readonly string PrivateKey = MakeKey(2048);
    private readonly PortalOptionsValidator validator = new();

    [Fact]
    public void MinimalDiscoveryConfigurationIsValid() =>
        Assert.True(validator.Validate(null, ValidOptions()).Succeeded);

    [Theory]
    [InlineData("missing-github")]
    [InlineData("http")]
    [InlineData("nested-host")]
    [InlineData("invalid-host-label")]
    [InlineData("empty-enterprise")]
    [InlineData("unsafe-enterprise")]
    [InlineData("empty-app")]
    [InlineData("nonnumeric-app")]
    [InlineData("unsafe-app")]
    [InlineData("invalid-api-version")]
    [InlineData("empty-key")]
    [InlineData("invalid-key")]
    [InlineData("small-key")]
    [InlineData("public-key-only")]
    [InlineData("negative-maximum")]
    public void InvalidStaticConfigurationFailsWithoutLeakingValues(string scenario)
    {
        var options = ValidOptions();
        switch (scenario)
        {
            case "missing-github": options.GitHub = null!; break;
            case "http": options.GitHub.ApiBaseUrl = "http://api.github.com/"; break;
            case "nested-host": options.GitHub.ApiBaseUrl = "https://api.one.two.ghe.com/"; break;
            case "invalid-host-label": options.GitHub.ApiBaseUrl = "https://api.-enterprise.ghe.com/"; break;
            case "empty-enterprise": options.GitHub.Enterprise = ""; break;
            case "unsafe-enterprise": options.GitHub.Enterprise = "../enterprise"; break;
            case "empty-app": options.GitHub.AppId = ""; break;
            case "nonnumeric-app": options.GitHub.AppId = "not-an-id"; break;
            case "unsafe-app": options.GitHub.AppId = "123\nsecret"; break;
            case "invalid-api-version": options.GitHub.ApiVersion = "not-a-date"; break;
            case "empty-key": options.GitHub.PrivateKeyPem = ""; break;
            case "invalid-key": options.GitHub.PrivateKeyPem = "sensitive-invalid-key"; break;
            case "small-key": options.GitHub.PrivateKeyPem = MakeKey(1024); break;
            case "public-key-only":
                using (var rsa = RSA.Create(2048)) options.GitHub.PrivateKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
                break;
            case "negative-maximum": options.MaxBudgetAmount = -1; break;
            default: throw new InvalidOperationException(scenario);
        }

        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.NotEmpty(result.Failures);
        Assert.DoesNotContain("sensitive-invalid-key", result.FailureMessage);
        Assert.DoesNotContain("BEGIN", result.FailureMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostValidatesOnStartWithoutCreatingAnHttpClient(bool invalidKey)
    {
        var options = ValidOptions();
        if (invalidKey) options.GitHub.PrivateKeyPem = "invalid";
        var clients = new NoNetworkClientFactory();
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBudgetServices(new ConfigurationBuilder().Build());
                services.Configure<PortalOptions>(configured =>
                {
                    configured.GitHub = options.GitHub;
                    configured.MaxBudgetAmount = options.MaxBudgetAmount;
                });
                services.AddSingleton<IHttpClientFactory>(clients);
            }).Build();

        if (invalidKey) await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        else
        {
            await host.StartAsync();
            await host.StopAsync();
        }
        Assert.Equal(0, clients.Calls);
    }

    private static PortalOptions ValidOptions() => new()
    {
        GitHub = new()
        {
            ApiBaseUrl = "https://api.enterprise.ghe.com/",
            Enterprise = "enterprise",
            AppId = "123",
            PrivateKeyPem = PrivateKey
        },
        MaxBudgetAmount = 10000
    };

    private static string MakeKey(int bits)
    {
        using var rsa = RSA.Create(bits);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private sealed class NoNetworkClientFactory : IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("Static validation must not create an HTTP client.");
        }
    }
}
