using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace BudgetPortal.Pages;

public sealed class LanguageModel(IStringLocalizer<SharedResource> localizer, ILogger<LanguageModel> logger) : PageModel
{
    public const string CookieName = "__Host-BudgetPortal.Culture";
    [BindProperty] public string Culture { get; set; } = "";
    [BindProperty] public string ReturnUrl { get; set; } = "/";

    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public IActionResult OnPost()
    {
        if (Culture is not ("en" or "it") || !Url.IsLocalUrl(ReturnUrl))
        {
            logger.LogWarning("Language selection rejected. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return BadRequest(localizer["InvalidLanguageSelection"].Value);
        }
        Response.Cookies.Append(CookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(Culture)),
            new CookieOptions
            {
                Path = "/",
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });
        return LocalRedirect(ReturnUrl);
    }
}
