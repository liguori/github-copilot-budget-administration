using System.Security.Claims;
using BudgetPortal.Domain;

namespace BudgetPortal.Authentication;

public static class CurrentIdentity
{
    public static PortalIdentity RequirePortalIdentity(this ClaimsPrincipal principal)
    {
        var tenant = principal.FindFirstValue("tid");
        var subject = principal.FindFirstValue("oid");
        if (principal.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(tenant, out _) || !Guid.TryParse(subject, out _))
        {
            throw new PortalException(403, "L'identita' aziendale non contiene un tenant e un object ID validi.");
        }

        return new PortalIdentity(tenant, subject);
    }
}
