using System.Text;
using System.Text.Json;
using BudgetPortal;
using BudgetPortal.Authentication;
using BudgetPortal.Services;
using BudgetPortal.Pages;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
var portalJson = builder.Configuration["PORTAL_CONFIGURATION_JSON"];
if (!string.IsNullOrWhiteSpace(portalJson))
{
    using var document = JsonDocument.Parse(portalJson);
    var wrapped = JsonSerializer.Serialize(new { Portal = document.RootElement });
    builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(wrapped)));
}

builder.Services.AddOptions<EntraOptions>()
    .Bind(builder.Configuration.GetSection("Entra"))
    .ValidateDataAnnotations()
    .Validate(o => Guid.TryParse(o.TenantId, out _), "Entra:TenantId must be a tenant GUID.")
    .Validate(o => Guid.TryParse(o.ClientId, out _), "Entra:ClientId must be an application GUID.")
    .ValidateOnStart();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "__Host-BudgetPortal";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = false;
    options.AccessDeniedPath = "/AccessDenied";
    options.LoginPath = "/Login";
})
.AddOpenIdConnect(options =>
{
    var entra = builder.Configuration.GetSection("Entra").Get<EntraOptions>() ?? new EntraOptions();
    options.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
    options.ClientId = entra.ClientId;
    options.ClientSecret = entra.ClientSecret;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.UsePkce = true;
    options.MapInboundClaims = false;
    options.SaveTokens = false;
    options.GetClaimsFromUserInfoEndpoint = false;
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.CallbackPath = "/signin-oidc";
    options.SignedOutCallbackPath = "/signout-callback-oidc";
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        NameClaimType = "name"
    };
    options.Events.OnTokenValidated = context =>
    {
        var tenant = context.Principal?.FindFirst("tid")?.Value;
        var subject = context.Principal?.FindFirst("oid")?.Value;
        if (!string.Equals(tenant, entra.TenantId, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(subject, out _))
        {
            context.Fail("Tenant or immutable user identifier not accepted.");
        }
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToIdentityProvider = context =>
    {
        if (GetAzureOrigin(builder.Configuration) is { } origin)
            context.ProtocolMessage.RedirectUri = origin + "/signin-oidc";
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToIdentityProviderForSignOut = context =>
    {
        if (GetAzureOrigin(builder.Configuration) is { } origin)
            context.ProtocolMessage.PostLogoutRedirectUri = origin + "/signout-callback-oidc";
        return Task.CompletedTask;
    };
    options.Events.OnRemoteFailure = context =>
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("EntraAuthentication");
        logger.LogWarning("Entra authentication failed. TraceId: {TraceId}", context.HttpContext.TraceIdentifier);
        context.HandleResponse();
        context.Response.Redirect("/AccessDenied?reason=authentication");
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-BudgetPortal.Antiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("en").AddSupportedCultures("en", "it").AddSupportedUICultures("en", "it");
    options.RequestCultureProviders = [new CookieRequestCultureProvider { CookieName = LanguageModel.CookieName }];
    options.ApplyCurrentCultureToResponseHeaders = true;
});
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Language");
}).AddViewLocalization().AddDataAnnotationsLocalization(options =>
    options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
builder.Services.AddOptions<MvcOptions>().Configure<IStringLocalizer<SharedResource>>((options, localizer) =>
{
    var messages = options.ModelBindingMessageProvider;
    messages.SetAttemptedValueIsInvalidAccessor((value, field) => localizer["InvalidFieldValue", value, field]);
    messages.SetMissingBindRequiredValueAccessor(field => localizer["MissingField", field]);
    messages.SetMissingKeyOrValueAccessor(() => localizer["MissingValue"]);
    messages.SetMissingRequestBodyRequiredValueAccessor(() => localizer["MissingRequestBody"]);
    messages.SetNonPropertyAttemptedValueIsInvalidAccessor(value => localizer["InvalidValue", value]);
    messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => localizer["InvalidValueGeneral"]);
    messages.SetNonPropertyValueMustBeANumberAccessor(() => localizer["NumberRequired"]);
    messages.SetUnknownValueIsInvalidAccessor(field => localizer["InvalidFieldGeneral", field]);
    messages.SetValueIsInvalidAccessor(value => localizer["InvalidValue", value]);
    messages.SetValueMustBeANumberAccessor(field => localizer["FieldNumberRequired", field]);
    messages.SetValueMustNotBeNullAccessor(_ => localizer["MissingValue"]);
});
builder.Services.AddBudgetServices(builder.Configuration);
builder.Services.AddHealthChecks();

var protection = builder.Services.AddDataProtection().SetApplicationName("CopilotBudgetPortal");
if (!string.IsNullOrEmpty(builder.Configuration["WEBSITE_INSTANCE_ID"]))
{
    var keys = Path.Combine(builder.Configuration["HOME"] ?? "/home", "data", "BudgetPortal", "keys");
    Directory.CreateDirectory(keys);
    protection.PersistKeysToFileSystem(new DirectoryInfo(keys));
}

var app = builder.Build();
app.UseRequestLocalization();
app.UseExceptionHandler("/Error");
if (!app.Environment.IsDevelopment())
    app.UseHsts();
// App Service terminates TLS. OIDC callback origins are pinned above, not taken from forwarded headers.
if (GetAzureOrigin(builder.Configuration) is null)
    app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; " +
        "form-action 'self' https://login.microsoftonline.com; object-src 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapHealthChecks("/healthz").AllowAnonymous();
app.Run();

static string? GetAzureOrigin(IConfiguration configuration)
{
    var hostname = configuration["WEBSITE_HOSTNAME"];
    if (string.IsNullOrEmpty(hostname))
        return null;
    if (!Uri.TryCreate("https://" + hostname, UriKind.Absolute, out var uri) ||
        uri.Host != hostname || !hostname.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("WEBSITE_HOSTNAME must be an Azure App Service hostname.");
    return uri.GetLeftPart(UriPartial.Authority);
}

public partial class Program;
