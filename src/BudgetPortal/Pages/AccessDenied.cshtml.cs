using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BudgetPortal.Pages;

public sealed class AccessDeniedModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status403Forbidden;
}
