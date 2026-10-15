namespace BudgetPortal.Domain;

public sealed class PortalOptions
{
    public GitHubOptions GitHub { get; set; } = new();
    public int MaxBudgetAmount { get; set; } = 10000;
}

public sealed class GitHubOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
    public string Enterprise { get; set; } = "";
    public string AppId { get; set; } = "";
    public string PrivateKeyPem { get; set; } = "";
    public string ApiVersion { get; set; } = "2026-03-10";
}

public sealed record PortalIdentity(string TenantId, string ObjectId);
public enum BudgetScope { CostCenterUsers = 2, User = 3 }

public sealed record BudgetRecord
{
    public string Id { get; init; } = "";
    public BudgetScope Scope { get; init; }
    public string Target { get; init; } = "";
    public int Amount { get; init; }
    public bool PreventFurtherUsage { get; init; }
    public DateOnly? ExpiresAt { get; init; }
    public string Fingerprint { get; init; } = "";
}

public sealed class BudgetInput
{
    public BudgetScope Scope { get; set; } = BudgetScope.CostCenterUsers;
    public string Target { get; set; } = "";
    public int Amount { get; set; }
    public bool PreventFurtherUsage { get; set; } = true;
    public DateOnly? ExpiresAt { get; set; }
    public string Reason { get; set; } = "";
}

public sealed record CostCenterOption(string Id, string Name);
public sealed class BudgetDashboard
{
    public string GitHubLogin { get; init; } = "";
    public IReadOnlyList<string> Organizations { get; init; } = [];
    public IReadOnlyList<CostCenterOption> CostCenters { get; init; } = [];
    public IReadOnlyList<string> Users { get; init; } = [];
    public IReadOnlyList<BudgetRecord> Budgets { get; init; } = [];
    public DateTimeOffset RetrievedAt { get; init; }
}

public sealed class PortalException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
