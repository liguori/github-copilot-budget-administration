using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BudgetPortal.Pages;

public sealed class ApiReferenceModel : PageModel
{
    public sealed record Operation(string Id, string Method, string Path, string GroupKey,
        string AuthenticationKey, string PermissionKey, string DocumentationUrl)
    {
        public string PurposeKey => $"{Id}Purpose";
        public string RequestKey => $"{Id}Request";
        public string ResponseKey => $"{Id}Response";
        public string NotesKey => $"{Id}Notes";
        public bool IsBudgetWrite => GroupKey == "ApiGroupBudgets" && Method != "GET";
        public string EffectKey => IsBudgetWrite ? "ApiEffectWrite" : Method == "POST" ? "ApiEffectToken" : "ApiEffectRead";
    }

    public sealed record CallFlow(string Id, IReadOnlyList<string> OperationIds)
    {
        public string TitleKey => $"{Id}Title";
        public string DescriptionKey => $"{Id}Description";
    }

    public static IReadOnlyList<Operation> Operations { get; } = Array.AsReadOnly<Operation>(
    [
        new("ApiListInstallations", "GET", "/app/installations", "ApiGroupApp", "ApiAuthApp", "ApiPermissionApp",
            Docs("apps/apps", "list-installations-for-the-authenticated-app")),
        new("ApiCreateToken", "POST", "/app/installations/{installation_id}/access_tokens", "ApiGroupApp",
            "ApiAuthApp", "ApiPermissionApp", Docs("apps/apps", "create-an-installation-access-token-for-an-app")),
        new("ApiListOrganizations", "GET", "/enterprises/{enterprise}/apps/installable_organizations",
            "ApiGroupDiscovery", "ApiAuthEnterprise", "ApiPermissionInstallationsRead",
            Docs("enterprise-admin/organization-installations",
                "get-enterprise-owned-organizations-that-can-have-github-apps-installed")),
        new("ApiScimUsers", "GET", "/scim/v2/enterprises/{enterprise}/Users", "ApiGroupAccess",
            "ApiAuthEnterprise", "ApiPermissionScim", Docs("enterprise-admin/scim",
                "list-scim-provisioned-identities-for-an-enterprise")),
        new("ApiGetUser", "GET", "/users/{username}", "ApiGroupAccess", "ApiAuthFirstOrganization",
            "ApiPermissionPublic", Docs("users/users", "get-a-user")),
        new("ApiGetMembership", "GET", "/orgs/{org}/memberships/{username}", "ApiGroupAccess",
            "ApiAuthOrganization", "ApiPermissionMembers", Docs("orgs/members", "get-organization-membership-for-a-user")),
        new("ApiListMembers", "GET", "/orgs/{org}/members", "ApiGroupAccess", "ApiAuthOrganization",
            "ApiPermissionMembers", Docs("orgs/members", "list-organization-members")),
        new("ApiListSeats", "GET", "/orgs/{org}/copilot/billing/seats", "ApiGroupAccess",
            "ApiAuthOrganization", "ApiPermissionCopilot",
            Docs("copilot/copilot-user-management", "list-all-copilot-seat-assignments-for-an-organization")),
        new("ApiListCenters", "GET", "/enterprises/{enterprise}/settings/billing/cost-centers", "ApiGroupCenters",
            "ApiAuthEnterprise", "ApiPermissionBillingRead", Docs("billing/cost-centers",
                "get-all-cost-centers-for-an-enterprise")),
        new("ApiGetCenter", "GET", "/enterprises/{enterprise}/settings/billing/cost-centers/{cost_center_id}",
            "ApiGroupCenters", "ApiAuthEnterprise", "ApiPermissionBillingRead",
            Docs("billing/cost-centers", "get-a-cost-center-by-id")),
        new("ApiListBudgets", "GET", "/enterprises/{enterprise}/settings/billing/budgets", "ApiGroupBudgets",
            "ApiAuthEnterprise", "ApiPermissionBillingRead", Docs("billing/budgets", "get-all-budgets")),
        new("ApiGetBudget", "GET", "/enterprises/{enterprise}/settings/billing/budgets/{budget_id}", "ApiGroupBudgets",
            "ApiAuthEnterprise", "ApiPermissionBillingRead", Docs("billing/budgets", "get-a-budget-by-id")),
        new("ApiCreateBudget", "POST", "/enterprises/{enterprise}/settings/billing/budgets", "ApiGroupBudgets",
            "ApiAuthEnterprise", "ApiPermissionBillingWrite", Docs("billing/budgets", "create-a-budget")),
        new("ApiUpdateBudget", "PATCH", "/enterprises/{enterprise}/settings/billing/budgets/{budget_id}", "ApiGroupBudgets",
            "ApiAuthEnterprise", "ApiPermissionBillingWrite", Docs("billing/budgets", "update-a-budget")),
        new("ApiDeleteBudget", "DELETE", "/enterprises/{enterprise}/settings/billing/budgets/{budget_id}", "ApiGroupBudgets",
            "ApiAuthEnterprise", "ApiPermissionBillingWrite", Docs("billing/budgets", "delete-a-budget"))
    ]);

    public static IReadOnlyList<CallFlow> CallFlows { get; } = Array.AsReadOnly<CallFlow>(
    [
        new("ApiFlowDiscovery", Array.AsReadOnly<string>(["ApiListInstallations", "ApiListOrganizations"])),
        new("ApiFlowTokens", Array.AsReadOnly<string>(["ApiCreateToken"])),
        new("ApiFlowIdentity", Array.AsReadOnly<string>(["ApiScimUsers", "ApiGetUser", "ApiGetMembership"])),
        new("ApiFlowDestinations", Array.AsReadOnly<string>(["ApiListMembers", "ApiListSeats", "ApiListCenters", "ApiGetCenter"])),
        new("ApiFlowBudgetList", Array.AsReadOnly<string>(["ApiListBudgets"])),
        new("ApiFlowBudgetDetail", Array.AsReadOnly<string>(["ApiGetBudget"])),
        new("ApiFlowWrites", Array.AsReadOnly<string>(["ApiCreateBudget", "ApiUpdateBudget", "ApiDeleteBudget"]))
    ]);

    private static string Docs(string category, string anchor) =>
        $"https://docs.github.com/en/enterprise-cloud@latest/rest/{category}?apiVersion=2026-03-10#{anchor}";
}
