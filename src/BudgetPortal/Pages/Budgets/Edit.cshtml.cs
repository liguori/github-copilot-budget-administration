using System.ComponentModel.DataAnnotations;
using BudgetPortal.Authentication;
using BudgetPortal.Domain;
using BudgetPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BudgetPortal.Pages.Budgets;

public sealed class EditModel(IBudgetService budgets, ILogger<EditModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Id { get; set; }
    [BindProperty] public FormInput Input { get; set; } = new();
    [BindProperty] public string? Fingerprint { get; set; }
    public BudgetDashboard? Dashboard { get; private set; }
    public BudgetRecord? Existing { get; private set; }
    public bool IsEdit => Id is not null;
    public bool Stale { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
            if (IsEdit)
            {
                var existing = Existing ?? throw new InvalidOperationException("The authorized budget was not loaded.");
                Input = new FormInput
                {
                    Scope = existing.Scope,
                    Target = existing.Target,
                    Amount = existing.Amount,
                    PreventFurtherUsage = true,
                    ExpiresAt = existing.ExpiresAt
                };
                Fingerprint = existing.Fingerprint;
            }
            return Page();
        }
        catch (PortalException exception)
        {
            return Handle(exception);
        }
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
            var identity = User.RequirePortalIdentity();
            var value = new BudgetInput
            {
                Scope = Input.Scope,
                Target = Input.Target,
                Amount = Input.Amount,
                PreventFurtherUsage = Input.PreventFurtherUsage,
                ExpiresAt = Input.ExpiresAt,
                Reason = Input.Reason
            };
            if (Id is null)
                await budgets.CreateAsync(identity, value, cancellationToken);
            else
                await budgets.UpdateAsync(identity, Id, Fingerprint ?? "", value, cancellationToken);
            TempData["Notice"] = Id is null ? "BudgetCreated" : "BudgetUpdated";
            return RedirectToPage("/Index");
        }
        catch (PortalException exception)
        {
            if (exception.StatusCode == 400)
            {
                try { await LoadAsync(cancellationToken); }
                catch (PortalException loadingFailure) { return Handle(loadingFailure); }
            }
            return Handle(exception);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (Id is not null)
            Existing = await budgets.GetBudgetAsync(User.RequirePortalIdentity(), Id, cancellationToken);
        else
            Dashboard = await budgets.GetDestinationsAsync(User.RequirePortalIdentity(), cancellationToken);
    }

    private IActionResult Handle(PortalException exception)
    {
        logger.LogWarning("Budget form rejected: {StatusCode}. TraceId: {TraceId}", exception.StatusCode, HttpContext.TraceIdentifier);
        if (exception.StatusCode == 403)
            return RedirectToPage("/AccessDenied");
        Response.StatusCode = exception.StatusCode;
        ErrorMessage = exception.Message;
        Stale = exception.StatusCode == 409;
        return Page();
    }

    public sealed class FormInput
    {
        [Display(Name = "Scope"), EnumDataType(typeof(BudgetScope), ErrorMessage = "InvalidScope")]
        public BudgetScope Scope { get; set; } = BudgetScope.CostCenterUsers;

        [Display(Name = "Target"), Required(ErrorMessage = "Seleziona una destinazione.")]
        [StringLength(200, ErrorMessage = "TargetTooLong")]
        public string Target { get; set; } = "";

        [Display(Name = "MonthlyAmountUsd"), Range(0, 100000000, ErrorMessage = "Inserisci un importo intero in USD, maggiore o uguale a zero.")]
        public int Amount { get; set; }

        [Range(typeof(bool), "true", "true", ErrorMessage = "Individual and per-user default budgets must stop further usage.")]
        public bool PreventFurtherUsage { get; set; } = true;

        [Display(Name = "OptionalExpiration"), DataType(DataType.Date)]
        public DateOnly? ExpiresAt { get; set; }

        [Display(Name = "Reason"), Required(ErrorMessage = "La motivazione e' obbligatoria.")]
        [StringLength(500, MinimumLength = 5, ErrorMessage = "La motivazione deve contenere da 5 a 500 caratteri.")]
        public string Reason { get; set; } = "";

        [Range(typeof(bool), "true", "true", ErrorMessage = "Conferma di aver verificato l'impatto della modifica.")]
        public bool Confirmed { get; set; }
    }
}
