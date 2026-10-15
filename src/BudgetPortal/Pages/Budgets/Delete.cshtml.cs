using System.ComponentModel.DataAnnotations;
using BudgetPortal.Authentication;
using BudgetPortal.Domain;
using BudgetPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BudgetPortal.Pages.Budgets;

public sealed class DeleteModel(IBudgetService budgets, ILogger<DeleteModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";
    [BindProperty] public string Fingerprint { get; set; } = "";
    [BindProperty, Required(ErrorMessage = "La motivazione e' obbligatoria."),
        StringLength(500, MinimumLength = 5, ErrorMessage = "La motivazione deve contenere da 5 a 500 caratteri.")]
    public string Reason { get; set; } = "";
    [BindProperty, Range(typeof(bool), "true", "true", ErrorMessage = "Conferma la cancellazione.")]
    public bool Confirmed { get; set; }
    public BudgetRecord? Budget { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool Stale { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
            Fingerprint = Budget!.Fingerprint;
            return Page();
        }
        catch (PortalException exception) { return Handle(exception); }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                await LoadAsync(cancellationToken);
                Response.StatusCode = 400;
                return Page();
            }
            await budgets.DeleteAsync(User.RequirePortalIdentity(), Id, Fingerprint, Reason, cancellationToken);
            TempData["Notice"] = "BudgetDeleted";
            return RedirectToPage("/Index");
        }
        catch (PortalException exception) { return Handle(exception); }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Budget = await budgets.GetBudgetAsync(User.RequirePortalIdentity(), Id, cancellationToken);
    }

    private IActionResult Handle(PortalException exception)
    {
        logger.LogWarning("Budget deletion rejected: {StatusCode}. TraceId: {TraceId}", exception.StatusCode, HttpContext.TraceIdentifier);
        if (exception.StatusCode == 403)
            return RedirectToPage("/AccessDenied");
        Response.StatusCode = exception.StatusCode;
        ErrorMessage = exception.Message;
        Stale = exception.StatusCode == 409;
        return Page();
    }
}
