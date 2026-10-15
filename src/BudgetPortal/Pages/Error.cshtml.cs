using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BudgetPortal.Pages;

public sealed class ErrorModel : PageModel
{
    public string TraceId { get; private set; } = "";
    public void OnGet()
    {
        TraceId = HttpContext.TraceIdentifier;
        Response.StatusCode = StatusCodes.Status500InternalServerError;
    }
    public void OnPost() => OnGet();
}
