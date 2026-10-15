using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BudgetPortal.Pages;
using BudgetPortal.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace BudgetPortal.Web.Tests;

public sealed class ApiReferenceTests
{
    [Fact]
    public async Task AnonymousVisitorsCannotAccessReference()
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient(culture: null);
        using var response = await client.GetAsync("/ApiReference");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Budgets.ReadCount);
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Theory]
    [InlineData(null, "en", "API reference", "Documentation only: no GitHub requests")]
    [InlineData("en", "en", "API reference", "Documentation only: no GitHub requests")]
    [InlineData("it", "it", "Riferimento API", "Solo documentazione: nessuna richiesta a GitHub")]
    public async Task ReferenceIsLocalizedCompleteAndMakesNoGitHubCalls(
        string? culture, string expectedLanguage, string title, string notice)
    {
        await using var factory = new PortalFactory();
        factory.Budgets.FailRead = true;
        var github = new RejectGitHubRequests();
        await using var application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddHttpClient<GitHubApi>()
                .ConfigurePrimaryHttpMessageHandler(() => github)));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        if (culture is not null)
            client.DefaultRequestHeaders.Add("Cookie", $"{LanguageModel.CookieName}=c={culture}|uic={culture}");

        using var response = await client.GetAsync("/ApiReference");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"<html lang=\"{expectedLanguage}\">", html);
        Assert.Contains($"<h1>{title}</h1>", html);
        Assert.Contains(notice, html);
        Assert.Equal(15, Regex.Matches(html, "data-api-method=\"").Count);
        Assert.Equal(7, Regex.Matches(html, "<th scope=\"row\">").Count);
        Assert.Contains("api-metrics", html);
        Assert.Contains("api-flow-table", html);
        Assert.Contains(expectedLanguage == "it" ? "non richieste per pagina" : "not requests per page", html);

        var localizer = application.Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(expectedLanguage);
            foreach (var operation in ApiReferenceModel.Operations)
            {
                Assert.Contains($"data-api-method=\"{operation.Method}\" data-api-path=\"{operation.Path}\"", html);
                Assert.Contains($"href=\"{operation.DocumentationUrl}\"", html);
                Assert.Contains($"href=\"#{operation.Id}\"", html);
                foreach (var key in new[] { operation.GroupKey, operation.AuthenticationKey, operation.PermissionKey,
                             operation.PurposeKey, operation.RequestKey, operation.ResponseKey, operation.NotesKey, operation.EffectKey })
                    Assert.False(localizer[key].ResourceNotFound, $"Missing {expectedLanguage} resource: {key}");
            }
            foreach (var flow in ApiReferenceModel.CallFlows)
            {
                Assert.False(localizer[flow.TitleKey].ResourceNotFound);
                Assert.False(localizer[flow.DescriptionKey].ResourceNotFound);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
        Assert.Contains("GitHub Copilot Business", html);
        Assert.Contains("Enterprise SCIM", html);
        Assert.Contains("Enterprise organization installations", html);
        Assert.Contains("Enterprise billing", html);
        Assert.Contains("Members", html);
        Assert.Contains("budget_entity_name", html);
        Assert.Contains("BundlePricing", html);
        Assert.Contains("ai_credits", html);
        Assert.Contains("OpenID Connect", html);
        Assert.DoesNotContain("repository_selection", html);
        Assert.DoesNotContain("/apps/organizations/{org}/installations", html);
        Assert.DoesNotContain("consumed_amount", html);
        Assert.DoesNotContain("<code>organization</code>", html);
        Assert.DoesNotContain("<code>cost_center</code>", html);
        Assert.Contains("<code>multi_user_cost_center</code>", html);
        Assert.Contains("<code>user</code>", html);
        Assert.DoesNotContain("test-only-client-secret", html);
        Assert.DoesNotContain("PRIVATE KEY", html);
        Assert.DoesNotContain("test-enterprise", html);
        Assert.Equal(0, github.RequestCount);
        Assert.Equal(0, factory.Budgets.ReadCount);
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Fact]
    public void CatalogMatchesRuntimeSourceMethodPathPairs()
    {
        var directory = Path.Combine(SourceRoot(), "src", "BudgetPortal", "Services");
        var sourceFiles = Directory.GetFiles(directory, "*.cs");
        var runtime = new HashSet<string>(StringComparer.Ordinal);
        var callPattern = new Regex(
            @"\b(?:\w+\.Send(?:AsApp)?Async|SendCoreAsync)\(\s*(?:[^,;\r\n]+,\s*)?HttpMethod\.(?<method>\w+),\s*\$?""(?<path>[^""]+)""");
        foreach (var file in sourceFiles)
        {
            var source = File.ReadAllText(file);
            var calls = callPattern.Matches(source);
            var resourceCalls = Regex.Matches(source, @"\b\w+\.Send(?:AsApp)?Async\s*\(").Count;
            // The transport sends a built request; its routes are covered by the literal call sites.
            var transportCalls = Path.GetFileName(file) == "GitHubApi.cs"
                ? Regex.Matches(source, @"\bhttp\.SendAsync\(\s*request,").Count : 0;
            var tokenCalls = Regex.Matches(source, @"\bSendCoreAsync\s*\(\s*HttpMethod\.").Count;
            Assert.Equal(resourceCalls - transportCalls + tokenCalls, calls.Count);
            foreach (Match call in calls)
            {
                var path = call.Groups["path"].Value;
                if (path.Contains("{BillingPath}", StringComparison.Ordinal))
                {
                    var billing = Regex.Match(source, """private string BillingPath => \$"(?<path>[^"]+)";""");
                    Assert.True(billing.Success, "Billing route must be resolved from runtime source.");
                    path = path.Replace("{BillingPath}", billing.Groups["path"].Value, StringComparison.Ordinal);
                }
                runtime.Add(call.Groups["method"].Value.ToUpperInvariant() + " " + NormalizePath(path));
            }
        }
        Assert.NotEmpty(runtime);
        Assert.Equal(runtime.Order(StringComparer.Ordinal), ApiReferenceModel.Operations
            .Select(operation => operation.Method + " " + NormalizePath(operation.Path)).Order(StringComparer.Ordinal));
        Assert.Equal(15, ApiReferenceModel.Operations.Select(operation => operation.Id).Distinct().Count());
        Assert.Equal(11, ApiReferenceModel.Operations.Count(operation => operation.Method == "GET"));
        Assert.Single(ApiReferenceModel.Operations, operation => operation.EffectKey == "ApiEffectToken");
        Assert.Equal(3, ApiReferenceModel.Operations.Count(operation => operation.IsBudgetWrite));
    }

    [Fact]
    public void CallMapCoversEveryCatalogOperationExactlyOnce()
    {
        var mapped = ApiReferenceModel.CallFlows.SelectMany(flow => flow.OperationIds).Order(StringComparer.Ordinal);
        Assert.Equal(ApiReferenceModel.Operations.Select(operation => operation.Id).Order(StringComparer.Ordinal), mapped);
        Assert.All(ApiReferenceModel.CallFlows, flow => Assert.NotEmpty(flow.OperationIds));
        Assert.Equal(7, ApiReferenceModel.CallFlows.Select(flow => flow.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("ApiListInstallations", "ApiAuthApp", "ApiPermissionApp")]
    [InlineData("ApiCreateToken", "ApiAuthApp", "ApiPermissionApp")]
    [InlineData("ApiListOrganizations", "ApiAuthEnterprise", "ApiPermissionInstallationsRead")]
    [InlineData("ApiScimUsers", "ApiAuthEnterprise", "ApiPermissionScim")]
    [InlineData("ApiGetUser", "ApiAuthFirstOrganization", "ApiPermissionPublic")]
    [InlineData("ApiGetMembership", "ApiAuthOrganization", "ApiPermissionMembers")]
    [InlineData("ApiListMembers", "ApiAuthOrganization", "ApiPermissionMembers")]
    [InlineData("ApiListSeats", "ApiAuthOrganization", "ApiPermissionCopilot")]
    [InlineData("ApiListCenters", "ApiAuthEnterprise", "ApiPermissionBillingRead")]
    [InlineData("ApiGetCenter", "ApiAuthEnterprise", "ApiPermissionBillingRead")]
    [InlineData("ApiListBudgets", "ApiAuthEnterprise", "ApiPermissionBillingRead")]
    [InlineData("ApiGetBudget", "ApiAuthEnterprise", "ApiPermissionBillingRead")]
    [InlineData("ApiCreateBudget", "ApiAuthEnterprise", "ApiPermissionBillingWrite")]
    [InlineData("ApiUpdateBudget", "ApiAuthEnterprise", "ApiPermissionBillingWrite")]
    [InlineData("ApiDeleteBudget", "ApiAuthEnterprise", "ApiPermissionBillingWrite")]
    public void EndpointAuthenticationAndPermissionsAreSpecific(string id, string authenticationKey, string permissionKey)
    {
        var operation = Assert.Single(ApiReferenceModel.Operations, operation => operation.Id == id);
        Assert.Equal(authenticationKey, operation.AuthenticationKey);
        Assert.Equal(permissionKey, operation.PermissionKey);
        var documentation = new Uri(operation.DocumentationUrl);
        Assert.Equal("https", documentation.Scheme);
        Assert.Equal("docs.github.com", documentation.Host);
        Assert.NotEmpty(documentation.Fragment);
    }

    [Theory]
    [InlineData("/ApiReference", "/ApiReference")]
    [InlineData("/", "/")]
    [InlineData("/Budgets/Edit", "/")]
    public async Task NavigationMarksOnlyTheCurrentArea(string page, string activeArea)
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient(culture: null);
        var html = await client.GetStringAsync(page);
        Assert.Contains("href=\"/ApiReference\"", html);
        var active = Assert.Single(Regex.Matches(html, "<a\\b[^>]*aria-current=\"page\"[^>]*>").Cast<Match>());
        Assert.Contains("class=\"nav-active\"", active.Value);
        Assert.Contains($"href=\"{activeArea}\"", active.Value);
    }

    private static string NormalizePath(string path) =>
        "/" + Regex.Replace(path.Split('?')[0].TrimStart('/'), @"\{[^{}]+\}", "{}");

    private static string SourceRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));

    private sealed class RejectGitHubRequests : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("The API reference must not send requests to GitHub.");
        }
    }
}
