using BudgetPortal.Domain;

namespace BudgetPortal.Services;

public interface IBudgetService
{
    Task<BudgetDashboard> GetDashboardAsync(PortalIdentity identity, CancellationToken cancellationToken);
    Task<BudgetDashboard> GetDestinationsAsync(PortalIdentity identity, CancellationToken cancellationToken);
    Task<BudgetRecord> GetBudgetAsync(PortalIdentity identity, string id, CancellationToken cancellationToken);
    Task CreateAsync(PortalIdentity identity, BudgetInput input, CancellationToken cancellationToken);
    Task UpdateAsync(PortalIdentity identity, string id, string fingerprint, BudgetInput input, CancellationToken cancellationToken);
    Task DeleteAsync(PortalIdentity identity, string id, string fingerprint, string reason, CancellationToken cancellationToken);
}
