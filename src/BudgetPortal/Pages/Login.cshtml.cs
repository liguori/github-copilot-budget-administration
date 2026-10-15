using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BudgetPortal.Pages;

public sealed class LoginModel : PageModel
{
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();

    public IActionResult OnPost() => Challenge(new AuthenticationProperties
    {
        RedirectUri = "/"
    }, OpenIdConnectDefaults.AuthenticationScheme);
}
