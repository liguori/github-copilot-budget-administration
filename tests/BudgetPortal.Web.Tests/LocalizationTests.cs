using System.Globalization;
using System.Net;
using BudgetPortal.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace BudgetPortal.Web.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public async Task EnglishIsDefaultEvenWithItalianBrowserLanguageOrQuery()
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient(culture: null);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("it-IT");
        using var response = await client.GetAsync("/Login?culture=it&ui-culture=it");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("Sign in with Microsoft Entra ID", html);
        Assert.Contains("data-loading-text=\"Working...\"", html);
        Assert.Contains("id=\"portal-language\"", html);
        Assert.Equal("en", Assert.Single(response.Content.Headers.ContentLanguage));
    }

    [Fact]
    public async Task LanguageSwitchIsPublicPersistentAndReturnsToLocalPage()
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient(culture: null);
        var token = PortalWebTests.Token(await client.GetStringAsync("/Login"));
        using var response = await Switch(client, token, "it", "/Login?source=language");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login?source=language", response.Headers.Location!.OriginalString);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(LanguageModel.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        var italian = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("<html lang=\"it\">", italian);
        Assert.Contains("Accedi con Microsoft Entra ID", italian);
        Assert.Contains("data-loading-text=\"Operazione in corso...\"", italian);
        using var deniedResponse = await client.GetAsync("/AccessDenied");
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        var denied = await deniedResponse.Content.ReadAsStringAsync();
        Assert.Contains("Non hai accesso a questo portale", denied);
        using var englishSwitch = await Switch(client, PortalWebTests.Token(italian), "en", "/Login");
        Assert.Equal(HttpStatusCode.Redirect, englishSwitch.StatusCode);
        Assert.Contains("Sign in with Microsoft Entra ID", await client.GetStringAsync("/Login"));
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Theory]
    [InlineData("fr", "/Login")]
    [InlineData("it", "https://example.com/")]
    [InlineData("it", "//example.com/")]
    [InlineData("it", "/\\example.com/")]
    public async Task InvalidLanguageOrExternalRedirectIsRejected(string culture, string returnUrl)
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient(culture: null);
        var token = PortalWebTests.Token(await client.GetStringAsync("/Login"));
        using var response = await Switch(client, token, culture, returnUrl);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        Assert.DoesNotContain(cookies ?? [],
            value => value.StartsWith(LanguageModel.CookieName + "=", StringComparison.Ordinal));
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Fact]
    public async Task LanguageChangeRequiresAntiforgery()
    {
        await using var factory = new PortalFactory(authenticate: false);
        using var client = factory.CreateClient(culture: null);
        using var response = await client.PostAsync("/Language", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Culture"] = "it",
            ["ReturnUrl"] = "/Login"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("en", "New budget", "Monthly amount (USD)", "A reason is required.")]
    [InlineData("it", "Nuovo budget", "Importo mensile (USD)", "La motivazione e' obbligatoria.")]
    public async Task BudgetFormsAndValidationUseSelectedLanguage(string culture, string heading, string amountLabel, string validation)
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient(culture);
        var html = await client.GetStringAsync("/Budgets/Edit");
        Assert.Contains(heading, html);
        Assert.Contains(amountLabel, html);
        var form = await PortalWebTests.CreateForm(client);
        form["Input.Reason"] = "";
        using var response = await client.PostAsync("/Budgets/Edit", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(validation, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Equal(0, factory.Budgets.WriteCount);
    }

    [Theory]
    [InlineData("en", "Data unavailable.", "GitHub is unavailable.")]
    [InlineData("it", "Dati non disponibili.", "GitHub non disponibile.")]
    public async Task ErrorsUseSelectedLanguage(string culture, string title, string message)
    {
        await using var factory = new PortalFactory();
        factory.Budgets.FailRead = true;
        using var client = factory.CreateClient(culture);
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(title, html);
        Assert.Contains(message, html);
    }

    [Theory]
    [InlineData("en", "300.00 USD", "12/31/2026")]
    [InlineData("it", "300,00 USD", "31/12/2026")]
    public async Task BudgetAndExpirationDisplayFormatsFollowCulture(string culture, string money, string expiration)
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient(culture);
        var html = await client.GetStringAsync("/");
        Assert.Contains(money, html);
        Assert.Contains(expiration, html);
    }

    [Fact]
    public async Task SuccessNoticeFollowsLanguageAtDisplayTime()
    {
        await using var factory = new PortalFactory();
        using var client = factory.CreateClient(culture: null);
        var form = await PortalWebTests.CreateForm(client);
        using var created = await client.PostAsync("/Budgets/Edit", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var switched = await Switch(client, form["__RequestVerificationToken"], "it", "/");
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Contains("Budget creato su GitHub.", await client.GetStringAsync("/"));
        Assert.Equal(1, factory.Budgets.WriteCount);
    }

    [Fact]
    public void EnglishAndItalianResourcesHaveMatchingKeysAndFormats()
    {
        using var factory = new PortalFactory();
        var localizer = factory.Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var english = localizer.GetAllStrings(includeParentCultures: true).ToDictionary(value => value.Name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it");
            var italian = localizer.GetAllStrings(includeParentCultures: false).ToDictionary(value => value.Name);
            Assert.Equal(english.Keys.Order(), italian.Keys.Order());
            foreach (var key in english.Keys)
            {
                Assert.False(localizer[key].ResourceNotFound);
                Assert.False(string.IsNullOrWhiteSpace(italian[key].Value));
                Assert.Equal(
                    System.Text.RegularExpressions.Regex.Matches(english[key].Value, @"\{\d+\}").Select(match => match.Value),
                    System.Text.RegularExpressions.Regex.Matches(italian[key].Value, @"\{\d+\}").Select(match => match.Value));
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static Task<HttpResponseMessage> Switch(HttpClient client, string token, string culture, string returnUrl) =>
        client.PostAsync("/Language", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Culture"] = culture,
            ["ReturnUrl"] = returnUrl
        }));
}
