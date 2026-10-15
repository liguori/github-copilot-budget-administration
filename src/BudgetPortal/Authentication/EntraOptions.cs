using System.ComponentModel.DataAnnotations;

namespace BudgetPortal.Authentication;

public sealed class EntraOptions
{
    [Required]
    public string TenantId { get; set; } = "";

    [Required]
    public string ClientId { get; set; } = "";

    [Required]
    public string ClientSecret { get; set; } = "";
}
