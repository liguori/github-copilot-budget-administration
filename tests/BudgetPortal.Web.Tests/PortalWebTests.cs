using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BudgetPortal.Authentication;
using BudgetPortal.Domain;
using BudgetPortal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BudgetPortal.Web.Tests;

public sealed class PortalWebTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/Budgets/Edit")]
    [InlineData("/Budgets/Delete?id=budget-1")]
    public async Task AnonymousVisitorsCannotAccessBudgetPages(string url)
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Budgets.ReadCount);
    }

    [Fact]
    public async Task LoginIsPublicAndHasNoSecrets()
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Login");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Accedi con Microsoft Entra ID", html);
        Assert.DoesNotContain("test-only-client-secret", html);
        Assert.DoesNotContain("PRIVATE KEY", html);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("/Login")]
    [InlineData("/")]
    public async Task FormActionAllowsOnlySameOriginAndEntraAuthentication(string url)
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var directives = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("form-action 'self' https://login.microsoftonline.com", directives);
        Assert.Contains("script-src 'self'", directives);
        Assert.Contains("object-src 'none'", directives);
        Assert.Contains("frame-ancestors 'none'", directives);
    }

    [Theory]
    [InlineData("/Login")]
    [InlineData("/Logout")]
    [InlineData("/Budgets/Edit")]
    [InlineData("/Budgets/Delete?id=budget-1")]
    public async Task PostsWithoutAntiforgeryAreRejected(string url)
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(url, new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Fact]
    public async Task DashboardRendersOnlySupportedBudgetScopes()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("platform-engineering", html);
        Assert.Contains("Engineering", html);
        Assert.Contains("alice_emu", html);
        Assert.Contains("Default per utente", html);
        Assert.Contains("CostCenterUsers", html);
        Assert.DoesNotContain("data-scope=\"Organization\"", html);
        Assert.DoesNotContain("data-scope=\"CostCenter\"", html);
        Assert.DoesNotContain("<progress", html);
        Assert.Contains("href=\"/ApiReference\"", html);
        Assert.Equal(1, factory.Budgets.ReadCount);
    }

    [Fact]
    public async Task BudgetFormExplainsSharedUserEligibilityAndGlobalImpact()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Budgets/Edit");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("anche se membri di altre organizzazioni", html);
        Assert.Contains("in tutte le organizzazioni", html);
        Assert.Contains("consumi Copilot inclusi e aggiuntivi", html);
        Assert.Contains("data-for-scope=\"User\"", html);
        Assert.DoesNotContain("Gli utenti con attribuzione ambigua non sono modificabili", html);
        Assert.Equal(1, factory.Budgets.DestinationReadCount);
        Assert.Equal(0, factory.Budgets.DashboardReadCount);
    }

    [Theory]
    [InlineData("/Budgets/Edit?id=budget-1", "Input_Amount", "300")]
    [InlineData("/Budgets/Delete?id=budget-1", "Fingerprint", "version-1")]
    public async Task ExistingBudgetPagesUseSingleBudgetReadsInsteadOfDashboard(string url, string field, string value)
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(field, html);
        Assert.Contains(value, html);
        Assert.DoesNotContain("data-for-scope=", html);
        Assert.Equal(1, factory.Budgets.BudgetReadCount);
        Assert.Equal(0, factory.Budgets.DashboardReadCount);
        Assert.Equal(0, factory.Budgets.DestinationReadCount);
    }

    [Fact]
    public async Task UserDefaultRequirementsHaveDocumentationAndScopeSpecificTargets()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Budgets/Edit");
        Assert.Contains("id=\"cost-center-user-requirements\"", html);
        Assert.Contains("almeno un utente o team e soltanto utenti o team", html);
        Assert.Contains("team enterprise sono esclusi", html);
        Assert.Contains("anche se appartengono ad altre organizzazioni", html);
        Assert.Contains("accesso allo stesso cost center", html);
        Assert.DoesNotContain("Utenti condivisi con organizzazioni non amministrate e team enterprise sono esclusi", html);
        Assert.Contains("2026-07-07-per-user-budgets-for-cost-centers-in-the-billing-ui/", html);
        Assert.Contains("billing-and-usage/organizations-and-enterprises/budgets", html);
        Assert.Contains("value=\"cc-1\" data-for-scope=\"CostCenterUsers\"", html);
        Assert.DoesNotContain("value=\"cc-2\"", html);
        Assert.DoesNotContain("value=\"CostCenter\"", html);
        Assert.DoesNotContain("id=\"hard-stop\"", html);
        Assert.Contains("name=\"Input.PreventFurtherUsage\" value=\"true\"", html);
    }

    [Fact]
    public async Task EditingBudgetWithDisabledStopWarnsBeforeEnablingIt()
    {
        await using var factory = new PortalFactory();
        factory.Budgets.BudgetOverride = new BudgetRecord
        {
            Id = "budget-1", Scope = BudgetScope.CostCenterUsers, Target = "cc-1",
            Amount = 300, PreventFurtherUsage = false, Fingerprint = "version-1"
        };
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Budgets/Edit?id=budget-1");
        Assert.Contains("ha attualmente il blocco dell&#x27;utilizzo disattivato", html);
        Assert.Contains("name=\"Input.PreventFurtherUsage\" value=\"true\"", html);
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Fact]
    public async Task IncompatibleUserDefaultSubmissionShowsRequirementAndDocumentation()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        var form = await CreateForm(client);
        form["Input.Scope"] = "CostCenterUsers";
        form["Input.Target"] = "cc-2";
        form["Input.Amount"] = "1290";
        using var response = await client.PostAsync("/Budgets/Edit", new FormUrlEncodedContent(form));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("composto solo da utenti", html);
        Assert.Contains("almeno un utente o team e soltanto utenti o team", html);
        Assert.Contains("2026-07-07-per-user-budgets-for-cost-centers-in-the-billing-ui/", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Fact]
    public async Task NonOwnerGetsDeniedNotAnEmptyDashboard()
    {
        await using var factory = new PortalFactory();
        factory.Budgets.Deny = true;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/AccessDenied", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ApiFailureIsVisibleAndDoesNotLookLikeNoBudgets()
    {
        await using var factory = new PortalFactory();
        factory.Budgets.FailRead = true;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Dati non disponibili", html);
        Assert.DoesNotContain("Nessun budget AI credits configurato", html);
    }

    [Fact]
    public async Task CreatePostsSupportedScopeAndRequiresExplicitConfirmation()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        var form = await CreateForm(client);
        using var response = await client.PostAsync("/Budgets/Edit", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, factory.Budgets.WriteCount);
        Assert.Equal(BudgetScope.CostCenterUsers, factory.Budgets.LastInput!.Scope);
        Assert.Equal("cc-1", factory.Budgets.LastInput.Target);
        Assert.Equal(300, factory.Budgets.LastInput.Amount);
        Assert.Equal(1, factory.Budgets.ReadCount);
    }

    [Fact]
    public async Task CreateFormHasAssociatedScopeAndTargetLabels()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Budgets/Edit");
        Assert.Contains("for=\"Input_Scope\"", html);
        Assert.Contains("id=\"Input_Scope\"", html);
        Assert.Contains("for=\"Input_Target\"", html);
        Assert.Contains("id=\"Input_Target\"", html);
    }

    [Theory]
    [InlineData("Input.Confirmed", "false")]
    [InlineData("Input.Scope", "999")]
    [InlineData("Input.Scope", "Organization")]
    [InlineData("Input.Scope", "0")]
    [InlineData("Input.Scope", "1")]
    [InlineData("Input.Scope", "CostCenter")]
    [InlineData("Input.PreventFurtherUsage", "false")]
    [InlineData("Input.Amount", "-1")]
    [InlineData("Input.Reason", "")]
    public async Task InvalidFormsDoNotWrite(string field, string value)
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        var form = await CreateForm(client);
        form[field] = value;
        using var response = await client.PostAsync("/Budgets/Edit", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Fact]
    public async Task StaleEditsRequireReloadAndDoNotOfferAnotherSubmit()
    {
        await using var factory = new PortalFactory();
        factory.Budgets.Conflict = true;
        using var client = factory.CreateClient();
        var form = await CreateForm(client, "/Budgets/Edit?id=budget-1");
        form["Id"] = "budget-1";
        form["Fingerprint"] = "old-fingerprint";
        using var response = await client.PostAsync("/Budgets/Edit?id=budget-1", new FormUrlEncodedContent(form));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Ricarica prima di modificare", html);
        Assert.DoesNotContain("Salva su GitHub", html);
    }

    [Fact]
    public async Task DeletionRequiresPostCsrfAndConfirmation()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Budgets/Delete?id=budget-1");
        Assert.Equal(0, factory.Budgets.WriteCount);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["Id"] = "budget-1",
            ["Fingerprint"] = "version-1",
            ["Reason"] = "Budget approvato per cancellazione",
            ["Confirmed"] = "true"
        };
        using var response = await client.PostAsync("/Budgets/Delete?id=budget-1", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, factory.Budgets.WriteCount);
    }

    [Fact]
    public void IdentityMustHaveImmutableTenantAndObjectIds()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", "owner@example.com"), new Claim("name", "owner")], "test"));
        var exception = Assert.Throws<PortalException>(() => principal.RequirePortalIdentity());
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task HealthEndpointDoesNotRequireLogin()
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal static async Task<Dictionary<string, string>> CreateForm(HttpClient client, string url = "/Budgets/Edit")
    {
        var html = await client.GetStringAsync(url);
        return new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["Input.Scope"] = "CostCenterUsers",
            ["Input.Target"] = "cc-1",
            ["Input.Amount"] = "300",
            ["Input.PreventFurtherUsage"] = "true",
            ["Input.Reason"] = "Budget per nuovo progetto approvato",
            ["Input.Confirmed"] = "true"
        };
    }

    internal static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected a Razor antiforgery field.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}

internal sealed class PortalFactory(bool authenticate = true) : WebApplicationFactory<Program>
{
    public FakeBudgetService Budgets { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath("../../../../../src/BudgetPortal", AppContext.BaseDirectory));
        builder.UseEnvironment("Testing");
        using var rsa = RSA.Create(2048);
        var settings = new Dictionary<string, string?>
        {
            ["Entra:TenantId"] = TestAuthentication.Tenant,
            ["Entra:ClientId"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            ["Entra:ClientSecret"] = "test-only-client-secret",
            ["Portal:GitHub:Enterprise"] = "test-enterprise",
            ["Portal:GitHub:AppId"] = "12345",
            ["Portal:GitHub:PrivateKeyPem"] = rsa.ExportRSAPrivateKeyPem(),
            ["Portal:MaxBudgetAmount"] = "10000"
        };
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBudgetService>();
            services.AddSingleton<IBudgetService>(Budgets);
            services.AddSingleton(new TestAuthenticationState(authenticate));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
        });
    }

    public new HttpClient CreateClient() => CreateClient("it");

    public HttpClient CreateClient(string? culture)
    {
        var client = base.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
        if (culture is not null)
            client.DefaultRequestHeaders.Add("Cookie", $"{BudgetPortal.Pages.LanguageModel.CookieName}=c={culture}|uic={culture}");
        return client;
    }
}

internal sealed record TestAuthenticationState(bool Authenticated);

internal sealed class TestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, TestAuthenticationState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Tenant = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    public const string Subject = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!state.Authenticated)
            return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tid", Tenant), new Claim("oid", Subject), new Claim("name", "Demo Organization Owner")],
            Scheme.Name, "name", "role"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

internal sealed class FakeBudgetService : IBudgetService
{
    public int ReadCount { get; private set; }
    public int DashboardReadCount { get; private set; }
    public int DestinationReadCount { get; private set; }
    public int BudgetReadCount { get; private set; }
    public int WriteCount { get; private set; }
    public bool Deny { get; set; }
    public bool FailRead { get; set; }
    public bool Conflict { get; set; }
    public BudgetRecord? BudgetOverride { get; set; }
    public BudgetInput? LastInput { get; private set; }

    public Task<BudgetDashboard> GetDashboardAsync(PortalIdentity identity, CancellationToken cancellationToken)
    {
        DashboardReadCount++;
        return LoadDashboard();
    }

    public Task<BudgetDashboard> GetDestinationsAsync(PortalIdentity identity, CancellationToken cancellationToken)
    {
        DestinationReadCount++;
        return LoadDashboard();
    }

    public async Task<BudgetRecord> GetBudgetAsync(PortalIdentity identity, string id, CancellationToken cancellationToken)
    {
        BudgetReadCount++;
        return BudgetOverride ?? (await LoadDashboard()).Budgets.FirstOrDefault(budget => budget.Id == id)
            ?? throw new PortalException(404, "Budget non trovato nel tuo perimetro autorizzato.");
    }

    private Task<BudgetDashboard> LoadDashboard()
    {
        ReadCount++;
        if (Deny) throw new PortalException(403, "Organization owner required.");
        if (FailRead) throw new PortalException(502, "GitHub non disponibile.");
        const string json = """
            {
              "GitHubLogin": "owner_emu",
              "Organizations": ["platform-engineering", "developer-experience"],
              "CostCenters": [{"Id":"cc-1","Name":"Engineering"}],
              "Users": ["alice_emu","bob_emu"],
              "RetrievedAt": "2026-09-23T15:00:00Z",
              "Budgets": [
                {"Id":"budget-1","Scope":"CostCenterUsers","Target":"cc-1","Amount":300,"PreventFurtherUsage":true,"Fingerprint":"version-1"},
                {"Id":"budget-4","Scope":"User","Target":"alice_emu","Amount":150,"PreventFurtherUsage":true,"Fingerprint":"version-4","ExpiresAt":"2026-12-31"}
              ]
            }
            """;
        return Task.FromResult(JsonSerializer.Deserialize<BudgetDashboard>(json, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        })!);
    }

    public Task CreateAsync(PortalIdentity identity, BudgetInput input, CancellationToken cancellationToken)
    {
        if (input.Scope == BudgetScope.CostCenterUsers && input.Target == "cc-2")
            throw new PortalException(400, "Il default per utente richiede un cost center composto solo da utenti.");
        WriteCount++;
        LastInput = input;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(PortalIdentity identity, string id, string fingerprint, BudgetInput input, CancellationToken cancellationToken)
    {
        if (Conflict) throw new PortalException(409, "Budget modificato da un altro amministratore.");
        WriteCount++;
        LastInput = input;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(PortalIdentity identity, string id, string fingerprint, string reason, CancellationToken cancellationToken)
    {
        if (Conflict) throw new PortalException(409, "Budget modificato da un altro amministratore.");
        WriteCount++;
        return Task.CompletedTask;
    }
}
