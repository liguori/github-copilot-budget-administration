using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BudgetPortal.Domain;
using Microsoft.Extensions.Options;

namespace BudgetPortal.Services;

public sealed class GitHubBudgetService(
    GitHubApi api, GitHubInstallationDiscovery discovery, IOptions<PortalOptions> options,
    TimeProvider clock, ILogger<GitHubBudgetService> logger) : IBudgetService
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private const int MaxPages = 10000;
    private string BillingPath => $"enterprises/{Escape(options.Value.GitHub.Enterprise)}/settings/billing";
    private sealed record Resource(string Type, string Name);
    private sealed record Center(string Id, string Name, List<Resource> Resources);
    private sealed record StoredBudget(BudgetRecord Record, JsonElement Alerting);
    private sealed record ResolvedIdentity(string GitHubLogin, long GitHubUserId);
    private sealed record Access(
        long EnterpriseInstallationId,
        ResolvedIdentity Identity,
        Dictionary<string, GitHubOrganization> Owned,
        HashSet<string> OrganizationLogins,
        Dictionary<string, Center> Centers,
        HashSet<string> IsolatedCenters,
        HashSet<string> AllowedUserCenters,
        HashSet<string> Users,
        List<StoredBudget> Budgets);

    public Task<BudgetDashboard> GetDashboardAsync(PortalIdentity identity, CancellationToken cancellationToken) =>
        GuardAsync(identity, "read", async () =>
        {
            var access = await LoadAccessAsync(identity, cancellationToken);
            return Dashboard(access);
        });

    public Task<BudgetDashboard> GetDestinationsAsync(PortalIdentity identity, CancellationToken cancellationToken) =>
        GuardAsync(identity, "destinations", async () =>
            Dashboard(await LoadAccessAsync(identity, cancellationToken, includeBudgets: false)));

    public Task<BudgetRecord> GetBudgetAsync(PortalIdentity identity, string id, CancellationToken cancellationToken) =>
        GuardAsync(identity, "read-budget", async () =>
        {
            var access = await LoadIdentityAccessAsync(identity, cancellationToken);
            var scope = await ReadBudgetScopeAsync(access, id, cancellationToken, missingIsConflict: false);
            access = await LoadInventoriesAsync(access, cancellationToken, scope, includeBudgets: false);
            return (await FreshBudgetAsync(access, id, cancellationToken, missingIsConflict: false)).Record;
        });

    private BudgetDashboard Dashboard(Access access) => new()
    {
        GitHubLogin = access.Identity.GitHubLogin,
        Organizations = access.Owned.Keys.Order(Names).ToArray(),
        CostCenters = access.AllowedUserCenters.Select(id => new CostCenterOption(id, access.Centers[id].Name))
            .OrderBy(center => center.Name, Names).ToArray(),
        Users = access.Users.Order(Names).ToArray(),
        Budgets = access.Budgets.Where(budget => IsAuthorized(access, budget.Record.Scope, budget.Record.Target))
            .Select(budget => budget.Record).OrderBy(budget => budget.Scope).ThenBy(budget => budget.Target, Names).ToArray(),
        RetrievedAt = clock.GetUtcNow()
    };

    public Task CreateAsync(PortalIdentity identity, BudgetInput input, CancellationToken cancellationToken) =>
        GuardAsync(identity, "create", async () =>
        {
            ValidateInput(input);
            var access = await LoadAccessAsync(identity, cancellationToken, input.Scope);
            EnsureAuthorized(access, input.Scope, input.Target);
            ValidateAmount(access, input);
            EnsureUnique(access, input.Scope, input.Target);
            var payload = Payload(input, EntityName(access, input.Scope, input.Target), null);
            Audit(identity, access.Identity.GitHubLogin, "create", "attempt", null, input, input.Reason);
            await api.SendAsync(access.EnterpriseInstallationId, HttpMethod.Post, $"{BillingPath}/budgets", payload, cancellationToken);
            Audit(identity, access.Identity.GitHubLogin, "create", "success", null, input, input.Reason);
            return true;
        });

    public Task UpdateAsync(PortalIdentity identity, string id, string fingerprint, BudgetInput input, CancellationToken cancellationToken) =>
        GuardAsync(identity, "update", async () =>
        {
            ValidateInput(input);
            ValidateRevision(id, fingerprint);
            var access = await LoadIdentityAccessAsync(identity, cancellationToken);
            var scope = await ReadBudgetScopeAsync(access, id, cancellationToken, missingIsConflict: true);
            access = await LoadInventoriesAsync(access, cancellationToken, scope, includeBudgets: true);
            var existing = await CurrentAsync(access, id, fingerprint, cancellationToken);
            if (existing.Record.Scope != input.Scope || !Names.Equals(existing.Record.Target, input.Target))
                throw new PortalException(400, "A budget's scope and target cannot be changed. Delete it and create another budget.");
            EnsureAuthorized(access, input.Scope, input.Target);
            ValidateAmount(access, input);
            EnsureUnique(access, input.Scope, input.Target, id);
            Audit(identity, access.Identity.GitHubLogin, "update", "attempt", existing.Record, input, input.Reason);
            await api.SendAsync(access.EnterpriseInstallationId, HttpMethod.Patch, $"{BillingPath}/budgets/{Escape(id)}",
                Payload(input, EntityName(access, input.Scope, input.Target), existing.Alerting), cancellationToken);
            Audit(identity, access.Identity.GitHubLogin, "update", "success", existing.Record, input, input.Reason);
            return true;
        });

    public Task DeleteAsync(PortalIdentity identity, string id, string fingerprint, string reason, CancellationToken cancellationToken) =>
        GuardAsync(identity, "delete", async () =>
        {
            ValidateReason(reason);
            ValidateRevision(id, fingerprint);
            var access = await LoadIdentityAccessAsync(identity, cancellationToken);
            var scope = await ReadBudgetScopeAsync(access, id, cancellationToken, missingIsConflict: true);
            access = await LoadInventoriesAsync(access, cancellationToken, scope, includeBudgets: false);
            var existing = await FreshBudgetAsync(access, id, cancellationToken, missingIsConflict: true);
            if (!string.Equals(existing.Record.Fingerprint, fingerprint, StringComparison.Ordinal)) throw Conflict();
            Audit(identity, access.Identity.GitHubLogin, "delete", "attempt", existing.Record, null, reason);
            await api.SendAsync(access.EnterpriseInstallationId, HttpMethod.Delete, $"{BillingPath}/budgets/{Escape(id)}", null, cancellationToken);
            Audit(identity, access.Identity.GitHubLogin, "delete", "success", existing.Record, null, reason);
            return true;
        });

    private async Task<Access> LoadAccessAsync(PortalIdentity identity, CancellationToken cancellationToken,
        BudgetScope? scope = null, bool includeBudgets = true) =>
        await LoadInventoriesAsync(await LoadIdentityAccessAsync(identity, cancellationToken),
            cancellationToken, scope, includeBudgets);

    private async Task<Access> LoadIdentityAccessAsync(PortalIdentity identity, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        ValidateConfiguration(configuration);
        var installations = await discovery.GetAsync(cancellationToken);
        var mapped = await ResolveIdentityAsync(identity, installations, cancellationToken);

        var owned = new Dictionary<string, GitHubOrganization>(Names);
        var organizationLogins = installations.EnterpriseOrganizationLogins.ToHashSet(Names);
        var organizationAccess = await GitHubReadBatch.SelectAsync(installations.Organizations, async (organization, token) =>
        {
            var membership = await api.SendAsync(organization.InstallationId, HttpMethod.Get,
                $"orgs/{Escape(organization.Login)}/memberships/{Escape(mapped.GitHubLogin)}", null, token, allowNotFound: true);
            var isOwner = false;
            if (membership is not null)
            {
                var role = GitHubApi.RequiredString(membership.Body, "role");
                var state = GitHubApi.RequiredString(membership.Body, "state");
                isOwner = role == "admin" && state == "active";
            }
            return (Organization: organization, IsOwner: isOwner);
        }, cancellationToken);
        foreach (var entry in organizationAccess)
        {
            if (entry.IsOwner) owned.Add(entry.Organization.Login, entry.Organization);
        }
        if (owned.Count == 0) throw Denied();

        return new Access(installations.EnterpriseInstallationId, mapped, owned,
            organizationLogins, new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            new(Names), []);
    }

    private async Task<Access> LoadInventoriesAsync(Access access, CancellationToken cancellationToken,
        BudgetScope? scope, bool includeBudgets)
    {
        var owned = access.Owned;
        // Foreign member contents do not affect eligibility; center isolation uses the complete organization inventory.
        var membersTask = GitHubReadBatch.SelectAsync(owned.Values.ToArray(), async (organization, token) =>
            KeyValuePair.Create(organization.Login, await ReadMembersAsync(organization, token)), cancellationToken);
        var seatsTask = GitHubReadBatch.SelectAsync(owned.Values.ToArray(), async (organization, token) =>
            KeyValuePair.Create(organization.Login, await ReadCopilotUsersAsync(organization, token)), cancellationToken);
        var centersTask = scope == BudgetScope.User
            ? Task.FromResult(new Dictionary<string, Center>(StringComparer.Ordinal))
            : ReadCentersAsync(access.EnterpriseInstallationId, cancellationToken);
        await Task.WhenAll(membersTask, seatsTask, centersTask);
        var members = (await membersTask).ToDictionary(entry => entry.Key, entry => entry.Value, Names);
        var centers = await centersTask;
        var users = new HashSet<string>(Names);
        foreach (var entry in await seatsTask)
        {
            entry.Value.IntersectWith(members[entry.Key]);
            users.UnionWith(entry.Value);
        }
        var individualBudgetUsers = new HashSet<string>(users, Names);

        var allowedCenters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var center in centers.Values)
        {
            var assignedOrganizations = center.Resources
                .Where(resource => resource.Type == "organization")
                .Select(resource => resource.Name)
                .Distinct(Names)
                .ToArray();
            var delegated = assignedOrganizations.Length > 0
                ? assignedOrganizations.All(org => owned.ContainsKey(org))
                : IsUserOnlyCenter(center) && center.Resources.All(resource => users.Contains(resource.Name));
            var uniqueName = centers.Values.Count(other =>
                Names.Equals(other.Name, center.Name) || Names.Equals(other.Id, center.Name) ||
                Names.Equals(other.Name, center.Id)) == 1;
            var safeResources = center.Resources.All(resource => resource.Type switch
            {
                "organization" => owned.ContainsKey(resource.Name),
                "user" => users.Contains(resource.Name),
                _ => false // Teams/repositories/unknown resource kinds require additional proof we do not have.
            });
            if (delegated && uniqueName && safeResources) allowedCenters.Add(center.Id);
        }

        // Cost-center isolation still requires complete, unambiguous resource attribution.
        if (centers.Values.Any(center => center.Resources.Any(resource =>
            resource.Type is not ("organization" or "user") ||
            resource.Type == "organization" && !access.OrganizationLogins.Contains(resource.Name))))
            users.Clear();
        bool changed;
        do
        {
            var previousUserCount = users.Count;
            var previousCenterCount = allowedCenters.Count;
            foreach (var center in centers.Values.Where(center => !allowedCenters.Contains(center.Id)))
            {
                foreach (var resource in center.Resources.Where(resource => resource.Type == "user"))
                    users.Remove(resource.Name);
            }
            // A center invalidated by an unauthorized user becomes foreign to its remaining users too.
            allowedCenters.RemoveWhere(centerId => centers[centerId].Resources.Any(resource => resource.Type == "user" && !users.Contains(resource.Name)));
            changed = previousUserCount != users.Count || previousCenterCount != allowedCenters.Count;
        }
        while (changed);
        var allowedUserCenters = allowedCenters.Where(id => IsUserOnlyCenter(centers[id]))
            .ToHashSet(StringComparer.Ordinal);
        access = access with
        {
            Centers = centers,
            IsolatedCenters = allowedCenters,
            AllowedUserCenters = allowedUserCenters,
            Users = individualBudgetUsers
        };
        if (includeBudgets) access.Budgets.AddRange(await ReadBudgetsAsync(access, cancellationToken));
        return access;
    }

    private async Task<ResolvedIdentity> ResolveIdentityAsync(
        PortalIdentity identity, GitHubInstallations installations, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(identity.TenantId, out _) || !Guid.TryParse(identity.ObjectId, out _))
            throw Denied();
        var filter = Uri.EscapeDataString($"externalId eq \"{identity.ObjectId}\"");
        var response = (await api.SendAsync(installations.EnterpriseInstallationId, HttpMethod.Get,
            $"scim/v2/enterprises/{Escape(options.Value.GitHub.Enterprise)}/Users?filter={filter}",
            null, cancellationToken))!;
        if (!response.Body.TryGetProperty("totalResults", out var total) || !total.TryGetInt32(out var count) || count != 1 ||
            !response.Body.TryGetProperty("Resources", out var resources) ||
            resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() != 1)
            throw Denied();
        var resource = resources[0];
        if (!Names.Equals(GitHubApi.RequiredString(resource, "externalId"), identity.ObjectId) ||
            !resource.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
            throw Denied();
        var login = ResolveScimLogin(resource);
        var user = await api.SendAsync(installations.Organizations[0].InstallationId, HttpMethod.Get,
            $"users/{Escape(login)}", null, cancellationToken, allowNotFound: true);
        if (user is null || !user.Body.TryGetProperty("id", out var githubId) ||
            !githubId.TryGetInt64(out var id) || id <= 0 ||
            !Names.Equals(GitHubApi.RequiredString(user.Body, "login"), login))
            throw Denied();
        return new ResolvedIdentity(login, id);
    }

    private string ResolveScimLogin(JsonElement resource)
    {
        if (!resource.TryGetProperty("profileUrl", out _))
            return GitHubApi.RequiredString(resource, "userName");

        var profileUrl = GitHubApi.RequiredString(resource, "profileUrl");
        var apiOrigin = GitHubApi.ValidateBaseUrl(options.Value.GitHub.ApiBaseUrl);
        var expectedHost = apiOrigin.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            ? "github.com" : apiOrigin.Host["api.".Length..];
        if (!Uri.TryCreate(profileUrl, UriKind.Absolute, out var profile) ||
            profile.Scheme != Uri.UriSchemeHttps || !profile.IsDefaultPort ||
            profile.UserInfo.Length != 0 || profile.Query.Length != 0 || profile.Fragment.Length != 0 ||
            !profile.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(profile.AbsolutePath, @"\A/[a-zA-Z0-9][a-zA-Z0-9_-]*\z"))
            throw Denied();
        return profile.AbsolutePath[1..];
    }

    private async Task<HashSet<string>> ReadMembersAsync(GitHubOrganization organization, CancellationToken cancellationToken)
    {
        var members = new HashSet<string>(Names);
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = (await api.SendAsync(organization.InstallationId, HttpMethod.Get,
                $"orgs/{Escape(organization.Login)}/members?per_page=100&page={page}", null, cancellationToken))!;
            if (response.Body.ValueKind != JsonValueKind.Array) throw GitHubApi.InvalidResponse();
            foreach (var member in response.Body.EnumerateArray())
                if (!members.Add(GitHubApi.RequiredString(member, "login"))) throw GitHubApi.InvalidResponse();
            if (!response.HasNextLink) return members;
        }
        throw GitHubApi.InvalidResponse();
    }

    private async Task<HashSet<string>> ReadCopilotUsersAsync(GitHubOrganization organization, CancellationToken cancellationToken)
    {
        var users = new HashSet<string>(Names);
        int? expectedCount = null;
        var received = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = (await api.SendAsync(organization.InstallationId, HttpMethod.Get,
                $"orgs/{Escape(organization.Login)}/copilot/billing/seats?per_page=100&page={page}",
                null, cancellationToken))!;
            var seats = Array(response.Body, "seats");
            if (response.Body.TryGetProperty("total_seats", out var total))
            {
                if (!total.TryGetInt32(out var count) || count < 0 ||
                    expectedCount.HasValue && expectedCount.Value != count)
                    throw GitHubApi.InvalidResponse();
                expectedCount = count;
            }
            foreach (var seat in seats.EnumerateArray())
            {
                received++;
                if (!seat.TryGetProperty("assignee", out var assignee) || assignee.ValueKind == JsonValueKind.Null)
                    continue;
                if (!users.Add(GitHubApi.RequiredString(assignee, "login")))
                    throw GitHubApi.InvalidResponse();
            }
            if (!response.HasNextLink)
            {
                if (expectedCount.HasValue && expectedCount.Value != received) throw GitHubApi.InvalidResponse();
                return users;
            }
            if (seats.GetArrayLength() == 0) throw GitHubApi.InvalidResponse();
        }
        throw GitHubApi.InvalidResponse();
    }

    private async Task<Dictionary<string, Center>> ReadCentersAsync(long enterpriseInstallation, CancellationToken cancellationToken)
    {
        var response = (await api.SendAsync(enterpriseInstallation, HttpMethod.Get,
            $"{BillingPath}/cost-centers", null, cancellationToken))!;
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var listed = Array(response.Body, "costCenters");
        if (response.HasNextLink || Next(response.Body)) throw GitHubApi.InvalidResponse();
        foreach (var summary in listed.EnumerateArray())
        {
            var state = GitHubApi.RequiredString(summary, "state");
            if (state == "deleted") continue;
            if (state != "active") throw GitHubApi.InvalidResponse();
            var id = GitHubApi.RequiredString(summary, "id");
            var name = GitHubApi.RequiredString(summary, "name");
            if (!summaries.TryAdd(id, name)) throw GitHubApi.InvalidResponse();
        }
        var centers = await GitHubReadBatch.SelectAsync(summaries.ToArray(), async (summary, token) =>
        {
            var id = summary.Key;
            var name = summary.Value;
            var resources = new List<Resource>();
            var complete = false;
            for (var page = 1; page <= MaxPages; page++)
            {
                var detail = (await api.SendAsync(enterpriseInstallation, HttpMethod.Get,
                    $"{BillingPath}/cost-centers/{Escape(id)}?per_page=100&page={page}", null, token))!;
                if (GitHubApi.RequiredString(detail.Body, "id") != id ||
                    GitHubApi.RequiredString(detail.Body, "name") != name ||
                    GitHubApi.RequiredString(detail.Body, "state") != "active")
                    throw GitHubApi.InvalidResponse();
                var items = Array(detail.Body, "resources");
                resources.AddRange(items.EnumerateArray().Select(resource =>
                    new Resource(GitHubApi.RequiredString(resource, "type") switch
                    {
                        "Org" => "organization",
                        "User" => "user",
                        var type => type
                    }, GitHubApi.RequiredString(resource, "name"))));
                if (!detail.HasNextLink && !Next(detail.Body) &&
                    (detail.Body.TryGetProperty("has_next_page", out _) || items.GetArrayLength() < 100))
                {
                    complete = true;
                    break;
                }
            }
            if (!complete) throw GitHubApi.InvalidResponse();
            return new Center(id, name, resources);
        }, cancellationToken);
        return centers.ToDictionary(center => center.Id, StringComparer.Ordinal);
    }

    private async Task<List<StoredBudget>> ReadBudgetsAsync(Access access, CancellationToken cancellationToken)
    {
        var budgets = new List<StoredBudget>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int? expectedCount = null;
        var received = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = (await api.SendAsync(access.EnterpriseInstallationId, HttpMethod.Get,
                $"{BillingPath}/budgets?per_page=100&page={page}", null, cancellationToken))!;
            var rows = Array(response.Body, "budgets");
            if (!response.Body.TryGetProperty("has_next_page", out var hasNext) ||
                hasNext.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw GitHubApi.InvalidResponse();
            if (response.Body.TryGetProperty("total_count", out var count))
            {
                if (!count.TryGetInt32(out var total) || total < 0 || expectedCount.HasValue && total != expectedCount.Value)
                    throw GitHubApi.InvalidResponse();
                expectedCount = total;
            }
            foreach (var row in rows.EnumerateArray())
            {
                if (!ids.Add(GitHubApi.RequiredString(row, "id"))) throw GitHubApi.InvalidResponse();
                received++;
                var budget = ParseBudget(row, access);
                if (budget is not null) budgets.Add(budget);
            }
            if (!hasNext.GetBoolean() && !response.HasNextLink)
            {
                if (expectedCount.HasValue && received != expectedCount) throw GitHubApi.InvalidResponse();
                return budgets;
            }
            if (rows.GetArrayLength() == 0) throw GitHubApi.InvalidResponse();
        }
        throw GitHubApi.InvalidResponse();
    }

    private async Task<StoredBudget> CurrentAsync(Access access, string id, string fingerprint, CancellationToken cancellationToken)
    {
        ValidateRevision(id, fingerprint);
        var listed = access.Budgets.SingleOrDefault(budget => budget.Record.Id == id);
        if (listed is null || !IsAuthorized(access, listed.Record.Scope, listed.Record.Target)) throw Denied();
        var current = await FreshBudgetAsync(access, id, cancellationToken, missingIsConflict: true);
        if (!string.Equals(current.Record.Fingerprint, fingerprint, StringComparison.Ordinal)) throw Conflict();
        return current;
    }

    private static void ValidateRevision(string id, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fingerprint))
            throw new PortalException(400, "A budget ID and revision are required.");
    }

    private async Task<BudgetScope> ReadBudgetScopeAsync(Access access, string id, CancellationToken cancellationToken, bool missingIsConflict)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new PortalException(400, "A budget ID and revision are required.");
        var response = await api.SendAsync(access.EnterpriseInstallationId, HttpMethod.Get,
            $"{BillingPath}/budgets/{Escape(id)}", null, cancellationToken, allowNotFound: true);
        if (response is null) throw missingIsConflict ? Conflict() : BudgetNotFound();
        if (GitHubApi.RequiredString(response.Body, "id") != id) throw GitHubApi.InvalidResponse();
        return ReadScope(response.Body) ?? throw Denied();
    }

    private async Task<StoredBudget> FreshBudgetAsync(Access access, string id, CancellationToken cancellationToken, bool missingIsConflict)
    {
        var result = await api.SendAsync(access.EnterpriseInstallationId, HttpMethod.Get, $"{BillingPath}/budgets/{Escape(id)}",
            null, cancellationToken, allowNotFound: true);
        if (result is null) throw missingIsConflict ? Conflict() : BudgetNotFound();
        var current = ParseBudget(result.Body, access);
        if (current is null || current.Record.Id != id || !IsAuthorized(access, current.Record.Scope, current.Record.Target))
            throw Denied();
        return current;
    }

    private static BudgetScope? ReadScope(JsonElement row)
    {
        if (GitHubApi.RequiredString(row, "budget_type") != "BundlePricing" ||
            GitHubApi.RequiredString(row, "budget_product_sku") != "ai_credits") return null;
        return GitHubApi.RequiredString(row, "budget_scope") switch
        {
            "multi_user_cost_center" => BudgetScope.CostCenterUsers,
            "user" => BudgetScope.User,
            _ => null
        };
    }

    private static StoredBudget? ParseBudget(JsonElement row, Access access)
    {
        var scope = ReadScope(row);
        if (scope is null) return null;
        var entity = OptionalString(row, "budget_entity_name") ?? "";
        string target;
        if (scope == BudgetScope.User) target = GitHubApi.RequiredString(row, "user");
        else
        {
            // GitHub returns cost-center names on reads but requires immutable IDs on writes.
            // Accept either representation only when ID/name resolution is unambiguous.
            var matches = access.Centers.Values.Where(center => center.Id == entity || Names.Equals(center.Name, entity)).ToArray();
            if (matches.Length != 1) return null;
            target = matches[0].Id;
        }
        if (!row.TryGetProperty("budget_amount", out var amount) || !amount.TryGetInt32(out var dollars) || dollars < 0 ||
            !row.TryGetProperty("prevent_further_usage", out var prevent) || prevent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw GitHubApi.InvalidResponse();
        DateOnly? expiration = null;
        var expires = OptionalString(row, "expires_at");
        if (expires is not null)
        {
            if (!DateOnly.TryParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw GitHubApi.InvalidResponse();
            expiration = date;
        }
        if (!row.TryGetProperty("budget_alerting", out var alerting) || alerting.ValueKind != JsonValueKind.Object ||
            !alerting.TryGetProperty("will_alert", out var willAlert) || willAlert.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw GitHubApi.InvalidResponse();
        var recipients = Array(alerting, "alert_recipients").EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String ? item.GetString()! : throw GitHubApi.InvalidResponse()).ToArray();
        var budgetId = GitHubApi.RequiredString(row, "id");
        var revision = JsonSerializer.SerializeToUtf8Bytes(new
        {
            budgetId,
            scope,
            target,
            entity,
            dollars,
            prevent = prevent.GetBoolean(),
            expiration,
            willAlert = willAlert.GetBoolean(),
            recipients = recipients.Order(Names).ToArray()
        });
        return new StoredBudget(new BudgetRecord
        {
            Id = budgetId,
            Scope = scope.Value,
            Target = target,
            Amount = dollars,
            PreventFurtherUsage = prevent.GetBoolean(),
            ExpiresAt = expiration,
            Fingerprint = Convert.ToHexString(SHA256.HashData(revision))
        }, alerting.Clone());
    }

    private void ValidateInput(BudgetInput input)
    {
        if (!Enum.IsDefined(input.Scope) || string.IsNullOrWhiteSpace(input.Target) ||
            input.Target.Length > 255 || input.Target.Any(char.IsControl) || input.Target != input.Target.Trim())
            throw new PortalException(400, "Choose a valid budget scope and target.");
        if (input.Amount < 0) throw new PortalException(400, "The amount must be a nonnegative whole number of US dollars.");
        if (!input.PreventFurtherUsage)
            throw new PortalException(400, "Individual and per-user default budgets must stop further usage.");
        if (input.ExpiresAt is not null &&
            (input.Scope != BudgetScope.User || input.ExpiresAt <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)))
            throw new PortalException(400, "Only individual budgets can expire, and the expiration must be a future UTC date.");
        ValidateReason(input.Reason);
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5 || reason.Length > 500 || reason.Any(char.IsControl))
            throw new PortalException(400, "Provide a single-line reason of 5–500 characters without control characters.");
    }

    private static void ValidateConfiguration(PortalOptions configuration)
    {
        GitHubApi.ValidateBaseUrl(configuration.GitHub.ApiBaseUrl);
        if (string.IsNullOrWhiteSpace(configuration.GitHub.Enterprise) ||
            string.IsNullOrWhiteSpace(configuration.GitHub.AppId) ||
            configuration.MaxBudgetAmount < 0)
            throw new PortalException(502, "Portal delegation is not configured correctly.");
    }

    private void ValidateAmount(Access access, BudgetInput input)
    {
        EnsureAuthorized(access, input.Scope, input.Target);
        if (input.Amount > options.Value.MaxBudgetAmount)
            throw new PortalException(400, "The amount exceeds the administrator-configured limit for this target.");
    }

    private static bool IsAuthorized(Access access, BudgetScope scope, string target) => scope switch
    {
        BudgetScope.CostCenterUsers => access.AllowedUserCenters.Contains(target),
        BudgetScope.User => access.Users.Contains(target),
        _ => false
    };

    private static void EnsureAuthorized(Access access, BudgetScope scope, string target)
    {
        if (scope == BudgetScope.CostCenterUsers && access.IsolatedCenters.Contains(target) &&
            !access.AllowedUserCenters.Contains(target))
            throw new PortalException(400, "Il default per utente richiede un cost center composto solo da utenti. Organizzazioni e repository non sono supportati.");
        if (!IsAuthorized(access, scope, target)) throw Denied();
    }

    private static bool IsUserOnlyCenter(Center center) =>
        center.Resources.Count > 0 && center.Resources.All(resource => resource.Type == "user");

    private static void EnsureUnique(Access access, BudgetScope scope, string target, string? exceptId = null)
    {
        if (access.Budgets.Any(budget => budget.Record.Scope == scope && Names.Equals(budget.Record.Target, target) && budget.Record.Id != exceptId))
            throw new PortalException(409, "An AI credit budget already exists for this scope and target.");
    }

    private static string EntityName(Access access, BudgetScope scope, string target) => scope switch
    {
        BudgetScope.CostCenterUsers => access.Centers[target].Id,
        BudgetScope.User => "",
        _ => throw new PortalException(400, "Choose a valid budget scope and target.")
    };

    private static Dictionary<string, object?> Payload(BudgetInput input, string entity, JsonElement? alerting)
    {
        var payload = new Dictionary<string, object?>
        {
            ["budget_scope"] = input.Scope switch
            {
                BudgetScope.CostCenterUsers => "multi_user_cost_center",
                BudgetScope.User => "user",
                _ => throw new PortalException(400, "Unsupported scope.")
            },
            ["budget_entity_name"] = entity,
            ["budget_amount"] = input.Amount,
            ["budget_type"] = "BundlePricing",
            ["budget_product_sku"] = "ai_credits",
            ["prevent_further_usage"] = input.PreventFurtherUsage,
            ["budget_alerting"] = alerting.HasValue ? alerting.Value : new { will_alert = false, alert_recipients = System.Array.Empty<string>() }
        };
        if (input.Scope == BudgetScope.User)
        {
            payload["user"] = input.Target;
            // Null explicitly removes an existing expiration on PATCH.
            if (alerting.HasValue || input.ExpiresAt.HasValue)
                payload["expires_at"] = input.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return payload;
    }

    private async Task<T> GuardAsync<T>(PortalIdentity identity, string operation, Func<Task<T>> action)
    {
        using var activity = new Activity($"BudgetPortal.{operation}").Start();
        try { return await action(); }
        catch (PortalException exception)
        {
            logger.LogWarning("Budget operation {Operation} failed with {StatusCode}; tenant {TenantId}, object {ObjectId}, correlation {CorrelationId}",
                operation, exception.StatusCode, identity.TenantId, identity.ObjectId, Activity.Current?.TraceId.ToString());
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            logger.LogWarning("Budget operation {Operation} received invalid upstream data; tenant {TenantId}, object {ObjectId}, correlation {CorrelationId}",
                operation, identity.TenantId, identity.ObjectId, Activity.Current?.TraceId.ToString());
            throw GitHubApi.InvalidResponse();
        }
    }

    private void Audit(PortalIdentity identity, string login, string operation, string outcome, BudgetRecord? before, BudgetInput? after, string reason) =>
        logger.LogInformation(
            "Budget audit {Operation} {Outcome}; tenant {TenantId}, object {ObjectId}, GitHub {GitHubLogin}, scope {Scope}, target {Target}, before {Before}, after {After}, reason {Reason}, correlation {CorrelationId}",
            operation, outcome, identity.TenantId, identity.ObjectId, login, after?.Scope ?? before?.Scope,
            after?.Target ?? before?.Target, before is null ? null : JsonSerializer.Serialize(before),
            after is null ? null : JsonSerializer.Serialize(new { after.Amount, after.PreventFurtherUsage, after.ExpiresAt }),
            reason, Activity.Current?.TraceId.ToString() ?? "unavailable");

    private static JsonElement Array(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            throw GitHubApi.InvalidResponse();
        return array;
    }

    private static string? OptionalString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw GitHubApi.InvalidResponse();
        return value.GetString();
    }

    private static bool Next(JsonElement body)
    {
        if (!body.TryGetProperty("has_next_page", out var next)) return false;
        if (next.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw GitHubApi.InvalidResponse();
        return next.GetBoolean();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static PortalException Denied() => new(403, "You are not authorized to manage this budget or target.");
    private static PortalException BudgetNotFound() => new(404, "Budget non trovato nel tuo perimetro autorizzato.");
    private static PortalException Conflict() => new(409, "This budget changed or was deleted. Reload before making another change.");
}
