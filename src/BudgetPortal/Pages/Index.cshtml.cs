using System.Globalization;
using BudgetPortal.Authentication;
using BudgetPortal.Domain;
using BudgetPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BudgetPortal.Pages;

public sealed class IndexModel(IBudgetService budgets, ILogger<IndexModel> logger) : PageModel
{
    public BudgetDashboard? Dashboard { get; private set; }
    public string? ErrorMessage { get; private set; }
    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Dashboard = await budgets.GetDashboardAsync(User.RequirePortalIdentity(), cancellationToken);
            return Page();
        }
        catch (PortalException exception)
        {
            logger.LogWarning("Dashboard rejected: {StatusCode}. TraceId: {TraceId}", exception.StatusCode, HttpContext.TraceIdentifier);
            if (exception.StatusCode == 403)
                return RedirectToPage("/AccessDenied");
            Response.StatusCode = exception.StatusCode;
            ErrorMessage = exception.Message;
            return Page();
        }
    }

    public static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture) + " USD";

    public static string ScopeLabel(BudgetScope scope) => scope switch
    {
        BudgetScope.CostCenterUsers => "ScopeCostCenterUsers",
        BudgetScope.User => "ScopeUser",
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public string TargetLabel(BudgetRecord budget) =>
        budget.Scope == BudgetScope.CostCenterUsers
            ? Dashboard?.CostCenters.FirstOrDefault(c => c.Id == budget.Target)?.Name ?? budget.Target
            : budget.Target;
}
