using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BudgetPortal.Domain;
using BudgetPortal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace BudgetPortal.Tests;

public sealed class BudgetServiceTests(ITestOutputHelper output)
{
    private static readonly PortalIdentity Identity = new(
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers, "cc-1")]
    [InlineData(BudgetScope.User, "developer")]
    public async Task BothScopesRejectDisabledStoppingBeforeCreateAndUpdate(BudgetScope scope, string target)
    {
        using var fixture = new Fixture();
        var input = Input(scope, target);
        input.PreventFurtherUsage = false;
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, input, default));
        await AssertStatus(400, () => fixture.Service.UpdateAsync(Identity, "b1", "revision", input, default));
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task RemovedNumericScopeCannotBeCreatedOrSubmittedOnUpdate()
    {
        using var fixture = new Fixture();
        Assert.Equal([BudgetScope.CostCenterUsers, BudgetScope.User], Enum.GetValues<BudgetScope>());
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, Input((BudgetScope)1, "cc-1"), default));
        await AssertStatus(400, () => fixture.Service.UpdateAsync(Identity, "b1", "revision",
            Input((BudgetScope)1, "cc-1"), default));
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("Engineering")]
    [InlineData("cc-1")]
    public async Task UnsupportedBudgetCannotBeExposedUpdatedDeletedOrReinterpreted(string entity)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("unsupported", "cost_center", entity));
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        await AssertStatus(403, () => fixture.Service.GetBudgetAsync(Identity, "unsupported", default));
        await AssertStatus(403, () => fixture.Service.UpdateAsync(Identity, "unsupported", "revision",
            Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, "unsupported", "revision",
            "Removing unsupported budget", default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task UninstalledOrganizationsDoNotBlockInstalledOrganizationAccessOrWrites()
    {
        using var fixture = new Fixture();
        fixture.SetInstalledOrganizations("acme");
        fixture.SetCenter("cc-3", "User defaults", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("center", "multi_user_cost_center", "User defaults"));
        fixture.Budgets.Add(Fixture.Budget("user", "user", "", "developer"));
        fixture.Routes["GET /orgs/beta/memberships/alice"] =
            () => throw new InvalidOperationException("An uninstalled organization must not be queried.");

        var installations = await fixture.Discovery.GetAsync(default);
        Assert.Equal(["acme"], installations.Organizations.Select(organization => organization.Login));
        Assert.Equal(["acme", "beta"], installations.EnterpriseOrganizationLogins);
        var dashboard = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Equal(["acme"], dashboard.Organizations);
        Assert.Equal(["cc-3"], dashboard.CostCenters.Select(center => center.Id));
        Assert.Equal(["alice", "developer", "shared"], dashboard.Users);
        Assert.Equal(["center", "user"], dashboard.Budgets.Select(budget => budget.Id));
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "alice"), default);
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "shared"), default);
        var center = await fixture.Service.GetBudgetAsync(Identity, "center", default);
        await fixture.Service.UpdateAsync(Identity, center.Id, center.Fingerprint, Input(center.Scope, center.Target), default);
        await fixture.Service.DeleteAsync(Identity, center.Id, center.Fingerprint, "Removing authorized center budget", default);
        Assert.Equal(4, fixture.Writes.Count);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.StartsWith("/orgs/beta/", StringComparison.Ordinal));
        Assert.All(fixture.Requests.Where(request => request.Method != HttpMethod.Get &&
            !request.Path.Contains("/budgets", StringComparison.Ordinal)),
            request => Assert.EndsWith("/access_tokens", request.Path));
    }

    [Fact]
    public async Task UninstalledOrganizationOwnershipDoesNotGrantPortalAccess()
    {
        using var fixture = new Fixture();
        fixture.SetInstalledOrganizations("acme");
        fixture.Routes["GET /orgs/acme/memberships/alice"] = () => Fixture.Json(new { role = "member", state = "active" });
        fixture.Routes["GET /orgs/beta/memberships/alice"] = () => Fixture.Json(new { role = "admin", state = "active" });
        await AssertStatus(403, () => fixture.Service.GetDashboardAsync(Identity, default));
        Assert.DoesNotContain(fixture.Requests, request => request.Path.StartsWith("/orgs/beta/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/cost-centers", StringComparison.Ordinal));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstalledOrganizationsRemainForeignForCostCenterAndUserDefaultAuthorization(bool directUserAssignment)
    {
        using var fixture = new Fixture();
        fixture.SetInstalledOrganizations("acme");
        fixture.SetCenter("cc-3", "User defaults", [new { type = "User", name = "developer" }]);
        fixture.SetCenter("cc-4", "Mixed organizations",
            [new { type = "Org", name = "acme" }, new { type = "Org", name = "beta" }]);
        if (directUserAssignment)
            fixture.SetCenter("cc-2", "Other", [new { type = "Org", name = "beta" }, new { type = "User", name = "developer" }]);
        var dashboard = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.DoesNotContain(dashboard.CostCenters, center => center.Id is "cc-2" or "cc-4");
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-2"), default));
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-4"), default));
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "outsider"), default));
        if (directUserAssignment)
        {
            Assert.DoesNotContain(dashboard.CostCenters, center => center.Id == "cc-3");
            await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-3"), default));
        }
        else
        {
            Assert.Contains(dashboard.CostCenters, center => center.Id == "cc-3");
        }
        Assert.Empty(fixture.Writes);
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallationsOutsideConfiguredEnterpriseAreExcluded(bool suspended)
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /app/installations?per_page=100&page=1"] = () => Fixture.Json(new object[]
        {
            new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" } },
            Fixture.Installation("acme", 2),
            new { id = 4, target_type = "Organization", account = new { login = "foreign" },
                suspended_at = suspended ? "2030-09-23T10:00:00Z" : null },
            new { id = 5, target_type = "Enterprise", account = new { slug = "foreign-enterprise" },
                suspended_at = suspended ? "2030-09-23T10:00:00Z" : null }
        });
        var installations = await fixture.Discovery.GetAsync(default);
        Assert.Equal(["acme"], installations.Organizations.Select(organization => organization.Login));
        Assert.Equal(["acme"], (await fixture.Service.GetDashboardAsync(Identity, default)).Organizations);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/foreign/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstalledOrganizationOutsideEnterpriseCannotSatisfyRequiredOrganizationInstallation()
    {
        using var fixture = new Fixture();
        fixture.SetInstalledOrganizations("foreign");
        var exception = await AssertStatus(502, () => fixture.Service.GetDashboardAsync(Identity, default));
        Assert.Equal("Install the GitHub App in at least one organization of the configured enterprise.", exception.Message);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/memberships/", StringComparison.Ordinal));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task DashboardShowsDelegatedAiCreditsAndLicensedMembersIncludingSharedUsers()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-3", "User defaults", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("default", "multi_user_cost_center", "User defaults"));
        fixture.Budgets.Add(Fixture.Budget("user", "user", "", "developer"));
        fixture.Budgets.Add(Fixture.Budget("foreign", "organization", "beta"));
        fixture.Budgets.Add(Fixture.Budget("shared", "user", "", "shared"));
        fixture.Budgets.Add(Fixture.Budget("enterprise", "enterprise", ""));
        fixture.Budgets.Add(Fixture.Budget("repository", "repository", "acme/repo"));
        fixture.Budgets.Add(Fixture.Budget("universal", "multi_user_customer", ""));
        var nonAi = Fixture.Budget("actions", "organization", "acme");
        nonAi["budget_type"] = "ProductPricing";
        nonAi["budget_product_sku"] = "actions";
        fixture.Budgets.Add(nonAi);
        var dashboard = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Equal("alice", dashboard.GitHubLogin);
        Assert.Equal(["acme"], dashboard.Organizations);
        Assert.Equal(["cc-3"], dashboard.CostCenters.Select(center => center.Id));
        Assert.Equal(["alice", "developer", "shared"], dashboard.Users);
        Assert.Equal(["default", "shared", "user"], dashboard.Budgets.Select(budget => budget.Id).Order());
    }

    [Fact]
    public async Task DiscoveryReadsAdministratorInstalledAppAndCachesTheInventory()
    {
        using var fixture = new Fixture();
        await fixture.Service.GetDashboardAsync(Identity, default);
        await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Single(fixture.Requests, request => request.Path == "/app/installations?per_page=100&page=1");
        Assert.Single(fixture.Requests,
            request => request.Path == "/enterprises/enterprise/apps/installable_organizations?per_page=100&page=1");
        Assert.All(fixture.Requests.Where(request => request.Method != HttpMethod.Get),
            request => Assert.EndsWith("/access_tokens", request.Path));
    }

    [Theory]
    [InlineData("no-enterprise-organizations-installed")]
    [InlineData("missing-enterprise")]
    [InlineData("suspended")]
    [InlineData("enterprise-suspended")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-login")]
    [InlineData("foreign-enterprise")]
    public async Task IncompleteOrAmbiguousAdministratorInstallationsFailClosed(string problem)
    {
        using var fixture = new Fixture();
        var installed = new List<object>
        {
            new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" } },
            Fixture.Installation("acme", 2), Fixture.Installation("beta", 3)
        };
        switch (problem)
        {
            case "no-enterprise-organizations-installed": installed.RemoveRange(1, 2); break;
            case "missing-enterprise": installed.RemoveAt(0); break;
            case "suspended":
                installed[1] = new { id = 2, target_type = "Organization", account = new { login = "acme" }, suspended_at = "2030-09-23T10:00:00Z" };
                break;
            case "enterprise-suspended":
                installed[0] = new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" }, suspended_at = "2030-09-23T10:00:00Z" };
                break;
            case "duplicate-id": installed[2] = Fixture.Installation("beta", 2); break;
            case "duplicate-login": installed[2] = Fixture.Installation("ACME", 3); break;
            case "foreign-enterprise":
                installed[0] = new { id = 1, target_type = "Enterprise", account = new { slug = "foreign" } };
                break;
        }
        fixture.Routes["GET /app/installations?per_page=100&page=1"] = () => Fixture.Json(installed);
        await AssertStatus(502, () => fixture.Service.GetDashboardAsync(Identity, default));
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/memberships/", StringComparison.Ordinal));
        Assert.Empty(fixture.Writes);
        Assert.All(fixture.Requests.Where(request => request.Method != HttpMethod.Get),
            request => Assert.EndsWith("/access_tokens", request.Path));
    }

    [Fact]
    public async Task AdministratorInstallationDiscoveryReadsEveryPageWithoutMutation()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /app/installations?per_page=100&page=1"] = () => Fixture.Page(new object[]
        {
            new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" } },
            Fixture.Installation("acme", 2)
        }, true);
        fixture.Routes["GET /app/installations?per_page=100&page=2"] =
            () => Fixture.Json(new[] { Fixture.Installation("beta", 3) });
        fixture.Routes["GET /enterprises/enterprise/apps/installable_organizations?per_page=100&page=1"] =
            () => Fixture.Page(new[] { new { login = "acme" } }, true);
        fixture.Routes["GET /enterprises/enterprise/apps/installable_organizations?per_page=100&page=2"] =
            () => Fixture.Json(new[] { new { login = "beta" } });
        Assert.Equal(["acme"], (await fixture.Service.GetDashboardAsync(Identity, default)).Organizations);
        Assert.Equal(["acme", "beta"], (await fixture.Discovery.GetAsync(default)).EnterpriseOrganizationLogins);
        Assert.Contains(fixture.Requests, request => request.Path == "/app/installations?per_page=100&page=2");
        Assert.Contains(fixture.Requests, request => request.Path.EndsWith("/apps/installable_organizations?per_page=100&page=2"));
        Assert.All(fixture.Requests.Where(request => request.Method != HttpMethod.Get),
            request => Assert.EndsWith("/access_tokens", request.Path));
    }

    [Fact]
    public async Task FailedInstallationDiscoveryIsNotCached()
    {
        using var fixture = new Fixture();
        var complete = fixture.Routes["GET /app/installations?per_page=100&page=1"];
        fixture.Routes["GET /app/installations?per_page=100&page=1"] = () => Fixture.Json(new object[]
        {
            new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" } }
        });
        await AssertStatus(502, () => fixture.Service.GetDashboardAsync(Identity, default));
        fixture.Routes["GET /app/installations?per_page=100&page=1"] = complete;
        Assert.Equal(["acme"], (await fixture.Service.GetDashboardAsync(Identity, default)).Organizations);
        Assert.Equal(2, fixture.Requests.Count(request => request.Path == "/app/installations?per_page=100&page=1"));
    }

    [Fact]
    public async Task EmptyInstallationPageWithContinuationFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /app/installations?per_page=100&page=1"] =
            () => Fixture.Page(Array.Empty<object>(), true);
        await AssertStatus(502, () => fixture.Service.GetDashboardAsync(Identity, default));
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData(12, 250)]
    [InlineData(10, 200)]
    public async Task LargePaginatedEnterpriseLoadsConcurrentDashboardsWithBoundedRequests(int organizationCount, int memberCount)
    {
        using var fixture = new Fixture();
        var organizations = Enumerable.Range(1, organizationCount).Select(index => $"org-{index}").ToArray();
        fixture.Routes["GET /enterprises/enterprise/apps/installable_organizations?per_page=100&page=1"] =
            () => Fixture.Json(organizations.Select(login => new { login }));
        fixture.Routes["GET /app/installations?per_page=100&page=1"] =
            () => Fixture.Json(new object[] { new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" } } }
                .Concat(organizations.Select((login, index) => Fixture.Installation(login, index + 2))));
        fixture.ClearCenters();
        foreach (var (organization, index) in organizations.Select((organization, index) => (organization, index)))
        {
            fixture.Routes[$"GET /orgs/{organization}/memberships/alice"] = () => Fixture.Json(new { role = "admin", state = "active" });
            var members = Enumerable.Range(0, memberCount).Select(number => new { login = number == 0 ? "alice" : $"{organization}-dev-{number}" }).ToArray();
            var resources = members.Select(member => new { type = "User", name = member.login }).ToArray();
            fixture.SetCenter(organization, $"Center {organization}", resources);
            var pages = (memberCount + 99) / 100;
            for (var page = 1; page <= pages; page++)
            {
                var rows = members.Skip((page - 1) * 100).Take(100).ToArray();
                var centerRows = resources.Skip((page - 1) * 100).Take(100).ToArray();
                var hasNext = page < pages;
                fixture.Routes[$"GET /orgs/{organization}/members?per_page=100&page={page}"] =
                    () => Fixture.Page(rows, hasNext);
                fixture.Routes[$"GET /orgs/{organization}/copilot/billing/seats?per_page=100&page={page}"] =
                    () => Fixture.Page(new { total_seats = memberCount, seats = rows.Select(assignee => new { assignee }) }, hasNext);
                fixture.Routes[$"GET /enterprises/enterprise/settings/billing/cost-centers/{organization}?per_page=100&page={page}"] =
                    () => Fixture.Json(new { id = organization, name = $"Center {organization}", state = "active", resources = centerRows, has_next_page = hasNext });
            }
            fixture.Budgets.Add(Fixture.Budget(organization, "multi_user_cost_center", $"Center {organization}"));
            fixture.Budgets.AddRange(members.Skip(1).Take(10).Select(member =>
                Fixture.Budget(member.login, "user", "", member.login)));
        }
        for (var page = 1; page <= 2; page++)
        {
            var rows = fixture.Budgets.Skip((page - 1) * 100).Take(100).ToArray();
            var hasNext = page < 2;
            fixture.Routes[$"GET /enterprises/enterprise/settings/billing/budgets?per_page=100&page={page}"] =
                () => Fixture.Json(new { budgets = rows, has_next_page = hasNext, total_count = fixture.Budgets.Count });
        }

        var initial = await fixture.Service.GetDashboardAsync(Identity, default);
        fixture.Requests.Clear();
        fixture.ResetMetrics();
        fixture.ReadDelay = TimeSpan.FromMilliseconds(20);
        var stopwatch = Stopwatch.StartNew();
        var dashboards = await Task.WhenAll(
            fixture.Service.GetDashboardAsync(Identity, default),
            fixture.Service.GetDashboardAsync(Identity, default));
        stopwatch.Stop();

        var inventoryPages = organizationCount * ((memberCount + 99) / 100);
        var dashboardCalls = 2 + organizationCount + 3 * inventoryPages + 1 + 2;
        Assert.Equal(4, fixture.PeakConcurrency);
        Assert.Equal(2 * dashboardCalls, fixture.Requests.Count);
        Assert.Equal(2 * inventoryPages, fixture.Requests.Count(request => request.Path.Contains("/members?")));
        Assert.Equal(2 * inventoryPages, fixture.Requests.Count(request => request.Path.Contains("/copilot/billing/seats?")));
        Assert.Equal(2 * inventoryPages, fixture.Requests.Count(request => request.Path.Contains("/cost-centers/")));
        Assert.All(dashboards, dashboard =>
        {
            Assert.Equal(organizations.Order(StringComparer.OrdinalIgnoreCase), dashboard.Organizations);
            Assert.Equal(organizationCount * (memberCount - 1) + 1, dashboard.Users.Count);
            Assert.Equal(organizationCount, dashboard.CostCenters.Count);

            Assert.Equal(organizationCount * 11, dashboard.Budgets.Count);
            Assert.Equal(initial.Budgets, dashboard.Budgets);
        });
        var sequentialDelay = fixture.Requests.Count * fixture.ReadDelay.TotalMilliseconds;
        output.WriteLine("Two dashboards: {0:N0} ms elapsed vs {1:N0} ms injected sequential delay; {2} calls, peak concurrency {3}.",
            stopwatch.Elapsed.TotalMilliseconds, sequentialDelay, fixture.Requests.Count, fixture.PeakConcurrency);
        Assert.True(stopwatch.Elapsed.TotalMilliseconds < sequentialDelay * 0.65,
            $"Expected at least 35% less latency than sequential injected delays; elapsed {stopwatch.Elapsed}, sequential {sequentialDelay} ms.");

        fixture.Requests.Clear();
        stopwatch.Restart();
        var budget = await fixture.Service.GetBudgetAsync(Identity, "org-1-dev-1", default);
        stopwatch.Stop();
        var detailCalls = 2 + organizationCount + 2 * inventoryPages + 2;
        Assert.Equal(detailCalls, fixture.Requests.Count);
        Assert.Equal(initial.Budgets.Single(record => record.Id == budget.Id), budget);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/cost-centers", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Requests, IsBudgetList);
        output.WriteLine("{0} organizations x {1} members: individual edit read uses {2} calls vs {3} dashboard calls, elapsed {4:N0} ms.",
            organizationCount, memberCount, detailCalls, dashboardCalls, stopwatch.Elapsed.TotalMilliseconds);
        Assert.True(detailCalls <= dashboardCalls * 0.75);

        fixture.Requests.Clear();
        await fixture.Service.UpdateAsync(Identity, budget.Id, budget.Fingerprint, Input(target: "org-1-dev-1"), default);
        Assert.Equal(detailCalls + 3, fixture.Requests.Count);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/cost-centers", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Requests.Count(IsBudgetList));

        fixture.Requests.Clear();
        await fixture.Service.DeleteAsync(Identity, budget.Id, budget.Fingerprint, "Removing individual budget", default);
        Assert.Equal(detailCalls + 1, fixture.Requests.Count);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/cost-centers", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Requests, IsBudgetList);
    }

    private static bool IsBudgetList(Request request) => request.Path.Contains("/budgets?", StringComparison.Ordinal);
    private static bool IsDestinationInventory(Request request) =>
        request.Path.Contains("/members?", StringComparison.Ordinal) ||
        request.Path.Contains("/copilot/billing/seats?", StringComparison.Ordinal) ||
        request.Path.Contains("/cost-centers", StringComparison.Ordinal);

    [Fact]
    public async Task NewBudgetDestinationsDoNotReadBudgetsOrForeignMembers()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Routes["GET /enterprises/enterprise/settings/billing/budgets?per_page=100&page=1"] =
            () => throw new InvalidOperationException("Destinations must not enumerate budgets.");
        fixture.Routes["GET /orgs/beta/members?per_page=100&page=1"] =
            () => throw new InvalidOperationException("Foreign membership contents must not be fetched.");
        var destinations = await fixture.Service.GetDestinationsAsync(Identity, default);
        Assert.Equal(["acme"], destinations.Organizations);
        Assert.Equal(["alice", "developer", "shared"], destinations.Users);
        Assert.Equal("cc-1", Assert.Single(destinations.CostCenters).Id);
        Assert.Empty(destinations.Budgets);
        Assert.DoesNotContain(fixture.Requests, IsBudgetList);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.StartsWith("/orgs/beta/members?", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("user", "", "developer")]
    [InlineData("multi_user_cost_center", "Engineering", null)]
    public async Task SingleBudgetReadsPreserveFingerprintAndScopeRelevantAuthorization(string scope, string entity, string? user)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("single", scope, entity, user));
        var before = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        fixture.Requests.Clear();
        var current = await fixture.Service.GetBudgetAsync(Identity, before.Id, default);
        Assert.Equal(before, current);
        Assert.DoesNotContain(fixture.Requests, IsBudgetList);
        Assert.Equal(2, fixture.Requests.Count(request => request.Path.EndsWith("/budgets/single", StringComparison.Ordinal)));
        if (scope == "user") Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/cost-centers", StringComparison.Ordinal));
        if (scope == "multi_user_cost_center") Assert.Contains(fixture.Requests, request => request.Path.Contains("/cost-centers/cc-2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("organization", "acme", null)]
    [InlineData("organization", "beta", null)]
    [InlineData("user", "", "outsider")]
    [InlineData("cost_center", "Other", null)]
    [InlineData("repository", "acme/repo", null)]
    public async Task SingleBudgetReadAndDeleteRejectForeignOrUnsupportedTargets(string scope, string entity, string? user)
    {
        using var fixture = new Fixture();
        fixture.Budgets.Add(Fixture.Budget("foreign", scope, entity, user));
        await AssertStatus(403, () => fixture.Service.GetBudgetAsync(Identity, "foreign", default));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, "foreign", "forged-revision", "Removing foreign budget", default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("identity")]
    [InlineData("membership")]
    [InlineData("seat")]
    [InlineData("center")]
    public async Task SingleBudgetPathsRecheckRevocationsBeforeDisplayAndAllWrites(string revocation)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        var scope = revocation == "center" ? "multi_user_cost_center" : "user";
        var target = revocation == "center" ? "Engineering" : "";
        fixture.Budgets.Add(Fixture.Budget("b1", scope, target, revocation == "center" ? null : "developer"));
        var record = await fixture.Service.GetBudgetAsync(Identity, "b1", default);
        switch (revocation)
        {
            case "owner":
                fixture.Routes["GET /orgs/acme/memberships/alice"] = () => Fixture.Json(new { role = "member", state = "active" });
                break;
            case "identity":
                var filter = Uri.EscapeDataString($"externalId eq \"{Identity.ObjectId}\"");
                fixture.Routes[$"GET /scim/v2/enterprises/enterprise/Users?filter={filter}"] =
                    () => Fixture.Json(new { totalResults = 1, Resources = new[] { new { externalId = Identity.ObjectId, userName = "alice", active = false } } });
                break;
            case "membership":
                fixture.Routes["GET /orgs/acme/members?per_page=100&page=1"] = () => Fixture.Json(new[] { new { login = "alice" } });
                break;
            case "seat":
                fixture.Routes["GET /orgs/acme/copilot/billing/seats?per_page=100&page=1"] =
                    () => Fixture.Json(new { total_seats = 1, seats = new[] { new { assignee = new { login = "alice" } } } });
                break;
            case "center":
                fixture.SetCenter("cc-1", "Engineering", [new { type = "Org", name = "acme" }, new { type = "User", name = "outsider" }]);
                break;
        }
        await AssertStatus(403, () => fixture.Service.GetBudgetAsync(Identity, "b1", default));
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(record.Scope, record.Target), default));
        await AssertStatus(403, () => fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint, Input(record.Scope, record.Target), default));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, record.Id, record.Fingerprint, "Removing revoked budget", default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task MissingBudgetIsNotFoundForReadsAndConflictForWrites()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /enterprises/enterprise/settings/billing/budgets/missing"] =
            () => new HttpResponseMessage(HttpStatusCode.NotFound);
        await AssertStatus(404, () => fixture.Service.GetBudgetAsync(Identity, "missing", default));
        await AssertStatus(409, () => fixture.Service.UpdateAsync(Identity, "missing", "revision", Input(), default));
        await AssertStatus(409, () => fixture.Service.DeleteAsync(Identity, "missing", "revision", "Removing missing budget", default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task NonOwnerIsRejectedBeforeAnySingleBudgetIsRead()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /orgs/acme/memberships/alice"] = () => Fixture.Json(new { role = "member", state = "active" });
        await AssertStatus(403, () => fixture.Service.GetBudgetAsync(Identity, "untrusted-id", default));
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/budgets", StringComparison.Ordinal));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task BudgetScopeChangeDuringAuthorizationCannotBroadenAccess()
    {
        using var fixture = new Fixture();
        var reads = 0;
        fixture.Routes["GET /enterprises/enterprise/settings/billing/budgets/racing"] =
            () => Fixture.Json(Interlocked.Increment(ref reads) % 2 == 1
                ? Fixture.Budget("racing", "user", "", "developer")
                : Fixture.Budget("racing", "multi_user_cost_center", "Engineering"));
        await AssertStatus(403, () => fixture.Service.GetBudgetAsync(Identity, "racing", default));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, "racing", "revision", "Removing racing budget", default));
        Assert.Equal(4, reads);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task TargetedCostCenterReadFailsClosedOnIncompleteForeignResources()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("center", "multi_user_cost_center", "Engineering"));
        fixture.Routes["GET /enterprises/enterprise/settings/billing/cost-centers/cc-2?per_page=100&page=1"] =
            () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await AssertStatus(502, () => fixture.Service.GetBudgetAsync(Identity, "center", default));
        await AssertStatus(502, () => fixture.Service.DeleteAsync(Identity, "center", "revision", "Removing center budget", default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task SharedTransportCapsConcurrencyAndCancellationDoesNotConsumeASlot()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        fixture.AsyncRoutes["GET /probe"] = async token =>
        {
            if (Interlocked.Increment(ref active) == 4) started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Fixture.Json(new { });
        };
        var requests = Enumerable.Range(0, 12)
            .Select(_ => fixture.Api.SendAsAppAsync(HttpMethod.Get, "probe", null, default)).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4, fixture.Requests.Count);
            using var cancellation = new CancellationTokenSource();
            var queued = fixture.Api.SendAsAppAsync(HttpMethod.Get, "probe", null, cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(4, fixture.Requests.Count);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(requests);
        await fixture.Api.SendAsAppAsync(HttpMethod.Get, "probe", null, default);
        Assert.Equal(13, fixture.Requests.Count);
        Assert.Equal(4, fixture.PeakConcurrency);
    }

    [Theory]
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(403, true)]
    public async Task RateLimitPausesOnlyAffectedInstallationUntilItsReset(int status, bool primaryLimit)
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /limited"] = () =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret upstream detail") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            response.Headers.Add("X-RateLimit-Remaining", primaryLimit ? "0" : "100");
            response.Headers.Add("X-RateLimit-Reset", fixture.Clock.Now.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        };
        fixture.Routes["GET /available"] = () => Fixture.Json(new { });
        var error = await AssertStatus(502, () => fixture.Api.SendAsync(2, HttpMethod.Get, "limited", null, default));
        Assert.Contains("rate limit", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        await AssertStatus(502, () => fixture.Api.SendAsync(2, HttpMethod.Get, "limited", null, default));
        Assert.Single(fixture.Requests, request => request.Path == "/limited");
        await fixture.Api.SendAsync(3, HttpMethod.Get, "available", null, default);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(31);
        fixture.Routes["GET /limited"] = () => Fixture.Json(new { });
        if (primaryLimit)
        {
            await AssertStatus(502, () => fixture.Api.SendAsync(2, HttpMethod.Get, "limited", null, default));
            fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        }
        await fixture.Api.SendAsync(2, HttpMethod.Get, "limited", null, default);
        Assert.Equal(2, fixture.Requests.Count(request => request.Path == "/limited"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task RateLimitedWritesAreNeverAutomaticallyRetried(string method)
    {
        using var fixture = new Fixture();
        fixture.Routes[$"{method} /write"] = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        await AssertStatus(502, () => fixture.Api.SendAsync(2, new HttpMethod(method), "write", null, default));
        await AssertStatus(502, () => fixture.Api.SendAsync(2, new HttpMethod(method), "write", null, default));
        Assert.Single(fixture.Requests, request => request.Path == "/write");
    }

    [Theory]
    [InlineData("https://api.github.com/", "https://github.com/alice")]
    [InlineData("https://api.enterprise.ghe.com/", "https://enterprise.ghe.com/alice")]
    public async Task ScimProfileResolvesCanonicalEmuLoginWithoutMatchingEmail(string origin, string profileUrl)
    {
        using var fixture = new Fixture();
        fixture.Options.GitHub.ApiBaseUrl = origin;
        var filter = Uri.EscapeDataString($"externalId eq \"{Identity.ObjectId}\"");
        fixture.Routes[$"GET /scim/v2/enterprises/enterprise/Users?filter={filter}"] = () => Fixture.Json(new
        {
            totalResults = 1,
            Resources = new[] { new
            {
                externalId = Identity.ObjectId, userName = "different.upn@example.com",
                profileUrl, active = true
            } }
        });
        var dashboard = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Equal("alice", dashboard.GitHubLogin);
        Assert.Contains(fixture.Requests, request => request.Path == "/users/alice");
        Assert.Contains(fixture.Requests, request => request.Path == "/orgs/acme/memberships/alice");
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("different.upn"));
    }

    [Theory]
    [InlineData("http://github.com/alice")]
    [InlineData("https://evil.example/alice")]
    [InlineData("https://github.com.evil.example/alice")]
    [InlineData("https://github.com:8443/alice")]
    [InlineData("https://user@github.com/alice")]
    [InlineData("https://github.com/alice?other=1")]
    [InlineData("https://github.com/alice#other")]
    [InlineData("https://github.com/alice/repositories")]
    [InlineData("https://github.com/")]
    public async Task UntrustedScimProfilesAreDenied(string profileUrl)
    {
        using var fixture = new Fixture();
        var filter = Uri.EscapeDataString($"externalId eq \"{Identity.ObjectId}\"");
        fixture.Routes[$"GET /scim/v2/enterprises/enterprise/Users?filter={filter}"] = () => Fixture.Json(new
        {
            totalResults = 1,
            Resources = new[] { new { externalId = Identity.ObjectId, userName = "alice", profileUrl, active = true } }
        });
        await AssertStatus(403, () => fixture.Service.GetDashboardAsync(Identity, default));
        Assert.DoesNotContain(fixture.Requests, request => request.Path.StartsWith("/users/"));
    }

    [Fact]
    public async Task ScimProfileCannotResolveToADifferentCanonicalUser()
    {
        using var fixture = new Fixture();
        var filter = Uri.EscapeDataString($"externalId eq \"{Identity.ObjectId}\"");
        fixture.Routes[$"GET /scim/v2/enterprises/enterprise/Users?filter={filter}"] = () => Fixture.Json(new
        {
            totalResults = 1,
            Resources = new[] { new
            {
                externalId = Identity.ObjectId, userName = "alice",
                profileUrl = "https://github.com/alice", active = true
            } }
        });
        fixture.Routes["GET /users/alice"] = () => Fixture.Json(new { login = "other", id = 7 });
        await AssertStatus(403, () => fixture.Service.GetDashboardAsync(Identity, default));
    }

    [Fact]
    public async Task InactiveScimIdentityIsDenied()
    {
        using var fixture = new Fixture();
        var filter = Uri.EscapeDataString($"externalId eq \"{Identity.ObjectId}\"");
        fixture.Routes[$"GET /scim/v2/enterprises/enterprise/Users?filter={filter}"] = () => Fixture.Json(new
        {
            totalResults = 1,
            Resources = new[] { new { externalId = Identity.ObjectId, userName = "alice", active = false } }
        });
        await AssertStatus(403, () => fixture.Service.GetDashboardAsync(Identity, default));
    }

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers, "multi_user_cost_center", 0)]
    public async Task CreatesCostCenterBudgetUsingImmutableId(BudgetScope scope, string apiScope, int amount)
    {
        using var fixture = new Fixture();
        if (scope == BudgetScope.CostCenterUsers)
            fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        await fixture.Service.CreateAsync(Identity, Input(scope, "cc-1", amount), default);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(HttpMethod.Post, write.Method);
        Assert.Equal("/enterprises/enterprise/settings/billing/budgets", write.Path);
        Assert.Equal(apiScope, write.Body.GetProperty("budget_scope").GetString());
        Assert.Equal("cc-1", write.Body.GetProperty("budget_entity_name").GetString());
        Assert.Equal("BundlePricing", write.Body.GetProperty("budget_type").GetString());
        Assert.Equal("ai_credits", write.Body.GetProperty("budget_product_sku").GetString());
        Assert.Equal(amount, write.Body.GetProperty("budget_amount").GetInt32());
        Assert.True(write.Body.GetProperty("prevent_further_usage").GetBoolean());
        Assert.False(write.Body.GetProperty("budget_alerting").GetProperty("will_alert").GetBoolean());
        Assert.Equal(0, write.Body.GetProperty("budget_alerting").GetProperty("alert_recipients").GetArrayLength());
        Assert.False(write.Body.TryGetProperty("expires_at", out _));
        Assert.Equal("installation-1", write.Token);
        Assert.Contains(fixture.Requests, request => request.Path.Contains("/orgs/acme/memberships/") && request.Token == "installation-2");
        Assert.Contains(fixture.Requests, request => request.Path.Contains("/orgs/beta/memberships/") && request.Token == "installation-3");
    }

    [Fact]
    public async Task IndividualCreateIncludesUserAndFutureExpiration()
    {
        using var fixture = new Fixture();
        var input = Input(BudgetScope.User, "developer");
        input.ExpiresAt = new DateOnly(2030, 9, 24);
        await fixture.Service.CreateAsync(Identity, input, default);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal("developer", write.Body.GetProperty("user").GetString());
        Assert.Equal("", write.Body.GetProperty("budget_entity_name").GetString());
        Assert.Equal("2030-09-24", write.Body.GetProperty("expires_at").GetString());
    }

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers, "cc-2")]
    [InlineData(BudgetScope.User, "outsider")]
    [InlineData(BudgetScope.User, "unlicensed")]
    public async Task RejectsCrossBoundaryTargets(BudgetScope scope, string target)
    {
        using var fixture = new Fixture();
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(scope, target), default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task SharedLicensedMemberBudgetCanBeCreated()
    {
        using var fixture = new Fixture();
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "shared"), default);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(HttpMethod.Post, write.Method);
        Assert.Equal("shared", write.Body.GetProperty("user").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLicensedMemberExistingBudgetCanBeUpdatedOrDeleted(bool delete)
    {
        using var fixture = new Fixture();
        fixture.Budgets.Add(Fixture.Budget("shared-budget", "user", "", "shared"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        if (delete)
            await fixture.Service.DeleteAsync(Identity, record.Id, record.Fingerprint, "Removing shared user limit", default);
        else
            await fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint,
                Input(BudgetScope.User, "shared", 50), default);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(delete ? HttpMethod.Delete : HttpMethod.Patch, write.Method);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedUserMembershipOrSeatRevocationBlocksSubsequentWrites(bool revokeSeat)
    {
        using var fixture = new Fixture();
        fixture.Budgets.Add(Fixture.Budget("shared-budget", "user", "", "shared"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        if (revokeSeat)
            fixture.Routes["GET /orgs/acme/copilot/billing/seats?per_page=100&page=1"] =
                () => Fixture.Json(new { total_seats = 1, seats = new[] { new { assignee = new { login = "alice" } } } });
        else
            fixture.Routes["GET /orgs/acme/members?per_page=100&page=1"] =
                () => Fixture.Json(new[] { new { login = "alice" } });

        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "shared"), default));
        await AssertStatus(403, () => fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint,
            Input(BudgetScope.User, "shared", 50), default));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, record.Id, record.Fingerprint,
            "Removing shared user limit", default));
        Assert.Empty(fixture.Writes);
        Assert.DoesNotContain("shared", (await fixture.Service.GetDashboardAsync(Identity, default)).Users);
    }

    [Fact]
    public async Task UnmappedIdentityCannotSupplyGithubUsername()
    {
        using var fixture = new Fixture();
        await AssertStatus(403, () => fixture.Service.CreateAsync(
            new(Identity.TenantId, "33333333-3333-3333-3333-333333333333"), Input(), default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ScimIdentityWithMismatchedExternalIdIsDenied()
    {
        using var fixture = new Fixture();
        var filter = Uri.EscapeDataString($"externalId eq \"{Identity.ObjectId}\"");
        fixture.Routes[$"GET /scim/v2/enterprises/enterprise/Users?filter={filter}"] = () => Fixture.Json(new
        {
            totalResults = 1,
            Resources = new[]
            {
                new { externalId = "33333333-3333-3333-3333-333333333333", userName = "alice", active = true }
            }
        });
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(), default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("admin", "pending")]
    [InlineData("member", "active")]
    public async Task RequiresLiveActiveOwner(string role, string state)
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /orgs/acme/memberships/alice"] = () => Fixture.Json(new { role, state });
        await AssertStatus(403, () => fixture.Service.GetDashboardAsync(Identity, default));
    }

    [Fact]
    public async Task OwnershipIsRecheckedAfterDashboard()
    {
        using var fixture = new Fixture();
        await fixture.Service.GetDashboardAsync(Identity, default);
        fixture.Routes["GET /orgs/acme/memberships/alice"] = () => Fixture.Json(new { role = "member", state = "active" });
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(), default));
        Assert.Empty(fixture.Writes);
        Assert.Equal(2, fixture.Requests.Count(request => request.Path == "/orgs/acme/memberships/alice"));
    }

    [Fact]
    public async Task OrganizationCentersRemainIneligibleEvenWhenEveryOrganizationIsOwned()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering",
            [new { type = "organization", name = "acme" }, new { type = "organization", name = "beta" }]);
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Empty(fixture.Writes);
        fixture.Routes["GET /orgs/beta/memberships/alice"] = () => Fixture.Json(new { role = "admin", state = "active" });
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ForeignDirectCostCenterUserAssignmentDoesNotBlockLicensedMemberOverride()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-2", "Other", [new { type = "user", name = "developer" }]);
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData("Org", "User")]
    [InlineData("organization", "user")]
    public async Task CostCenterResourceDiscriminatorsAreNormalized(string organizationType, string userType)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering",
            [new { type = organizationType, name = "acme" }, new { type = userType, name = "developer" }]);
        var dashboard = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Empty(dashboard.CostCenters);
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers)]
    public async Task CompatibleResourcesSupportCostCenterBudgetUpdateAndDelete(BudgetScope scope)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering",
            [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("existing-center",
            "multi_user_cost_center", "Engineering"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        await fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint, Input(scope, "cc-1", 75), default);
        var updated = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        await fixture.Service.DeleteAsync(Identity, updated.Id, updated.Fingerprint, "Removing center budget", default);
        Assert.Equal([HttpMethod.Patch, HttpMethod.Delete], fixture.Writes.Select(write => write.Method).ToArray());
    }

    [Theory]
    [InlineData("Org", "beta")]
    [InlineData("User", "outside")]
    [InlineData("Team", "acme/team")]
    [InlineData("Repo", "acme/repository")]
    [InlineData("Unknown", "acme")]
    public async Task GithubResourceTypesStillEnforceCostCenterIsolation(string type, string name)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering",
            [new { type = "Org", name = "acme" }, new { type, name }]);
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task UnknownCostCenterResourceDoesNotBlockLicensedMemberOverride()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-2", "Other", [new { type = "team", name = "beta/team" }]);
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task OutsideUserInDelegatedCostCenterDeniesCenter()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "user", name = "outside" }]);
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
    }

    [Fact]
    public async Task UnknownOrganizationInAnyCostCenterDoesNotBlockLicensedMemberOverride()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-2", "Other", [new { type = "organization", name = "not-configured" }]);
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task InvalidatedDelegatedCenterDoesNotRevokeIndividualLicensedMemberAuthorization()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering",
            [new { type = "user", name = "developer" }, new { type = "user", name = "alice" }]);
        fixture.SetCenter("cc-2", "Other",
            [new { type = "organization", name = "beta" }, new { type = "user", name = "alice" }]);
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default);
        Assert.Single(fixture.Writes);
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
    }

    [Fact]
    public async Task DuplicateCostCenterNamesAreNotResolvedByGuessing()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.SetCenter("cc-2", "Engineering", []);
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
    }

    [Fact]
    public async Task UpdatePreservesAlertsAndUsesPatch()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        var budget = Fixture.Budget("b/1", "multi_user_cost_center", "Engineering");
        budget["budget_alerting"] = new { will_alert = true, alert_recipients = new[] { "billing", "alice" } };
        fixture.Budgets.Add(budget);
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        await fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint, Input(BudgetScope.CostCenterUsers, "cc-1", 75), default);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(HttpMethod.Patch, write.Method);
        Assert.EndsWith("/budgets/b%2F1", write.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(write.Body.GetProperty("budget_alerting").GetProperty("will_alert").GetBoolean());
        Assert.Equal(["billing", "alice"], write.Body.GetProperty("budget_alerting").GetProperty("alert_recipients")
            .EnumerateArray().Select(element => element.GetString()));
        Assert.Equal(75, write.Body.GetProperty("budget_amount").GetInt32());
        Assert.Equal("cc-1", write.Body.GetProperty("budget_entity_name").GetString());
    }

    [Fact]
    public async Task IndividualUpdateCanClearExpiration()
    {
        using var fixture = new Fixture();
        var budget = Fixture.Budget("b1", "user", "", "developer");
        budget["expires_at"] = "2030-09-24";
        fixture.Budgets.Add(budget);
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        await fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint, Input(BudgetScope.User, "developer"), default);
        Assert.Equal(JsonValueKind.Null, Assert.Single(fixture.Writes).Body.GetProperty("expires_at").ValueKind);
    }

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers, "multi_user_cost_center", "cc-1")]
    [InlineData(BudgetScope.CostCenterUsers, "multi_user_cost_center", "Engineering")]
    public async Task ReturnedCostCenterNameOrIdUsesImmutableIdOnUpdate(BudgetScope scope, string apiScope, string entity)
    {
        using var fixture = new Fixture();
        if (scope == BudgetScope.CostCenterUsers)
            fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("b1", apiScope, entity));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        Assert.Equal("cc-1", record.Target);
        await fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint, Input(scope, "cc-1"), default);
        Assert.Equal("cc-1", Assert.Single(fixture.Writes).Body.GetProperty("budget_entity_name").GetString());
    }

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers, "multi_user_cost_center")]
    public async Task ExistingCostCenterNameBudgetBlocksDuplicateIdTargetedCreation(BudgetScope scope, string apiScope)
    {
        using var fixture = new Fixture();
        if (scope == BudgetScope.CostCenterUsers)
            fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("existing-center", apiScope, "Engineering"));
        await AssertStatus(409, () => fixture.Service.CreateAsync(Identity, Input(scope, "cc-1"), default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers)]
    public async Task AuthorizedUserOnlyCenterIsDiscoveredAndCanReceiveBudget(BudgetScope scope)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        var center = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
        Assert.Equal("cc-1", center.Id);
        await fixture.Service.CreateAsync(Identity, Input(scope, center.Id, 1290), default);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal("cc-1", write.Body.GetProperty("budget_entity_name").GetString());
        Assert.Equal(1290, write.Body.GetProperty("budget_amount").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrganizationCentersAreExcludedAndRejectUserDefaultBeforeWriting(bool mixed)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", mixed
            ? [new { type = "Org", name = "acme" }, new { type = "User", name = "developer" }]
            : [new { type = "Org", name = "acme" }]);
        fixture.Budgets.Add(Fixture.Budget("unsupported-default", "multi_user_cost_center", "Engineering"));
        var dashboard = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Empty(dashboard.CostCenters);
        Assert.Empty(dashboard.Budgets);
        var exception = await Assert.ThrowsAsync<PortalException>(
            () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("composto solo da utenti", exception.Message);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ForeignUsersDoNotDelegateUserOnlyCostCenters()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "outside" }]);
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("membership")]
    [InlineData("license")]
    [InlineData("assignment")]
    [InlineData("foreign-center")]
    [InlineData("unknown-resource")]
    [InlineData("empty")]
    public async Task UserOnlyCenterEligibilityIsRecheckedForEveryWrite(string revocation)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("existing-default", "multi_user_cost_center", "Engineering"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        switch (revocation)
        {
            case "owner":
                fixture.Routes["GET /orgs/acme/memberships/alice"] = () => Fixture.Json(new { role = "member", state = "active" });
                break;
            case "membership":
                fixture.Routes["GET /orgs/acme/members?per_page=100&page=1"] =
                    () => Fixture.Json(new[] { new { login = "alice" }, new { login = "shared" } });
                break;
            case "license":
                fixture.Routes["GET /orgs/acme/copilot/billing/seats?per_page=100&page=1"] =
                    () => Fixture.Json(new { total_seats = 1, seats = new[] { new { assignee = new { login = "alice" } } } });
                break;
            case "assignment":
                fixture.SetCenter("cc-1", "Engineering", [new { type = "Org", name = "acme" }]);
                break;
            case "foreign-center":
                fixture.SetCenter("cc-2", "Other",
                    [new { type = "Org", name = "beta" }, new { type = "User", name = "developer" }]);
                break;
            case "unknown-resource":
                fixture.SetCenter("cc-2", "Other", [new { type = "Team", name = "beta/team" }]);
                break;
            case "empty":
                fixture.SetCenter("cc-1", "Engineering", []);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(revocation));
        }
        await AssertStatus(revocation == "assignment" ? 400 : 403,
            () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        await AssertStatus(403, () => fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint,
            Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, record.Id, record.Fingerprint,
            "Removing user default", default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(BudgetScope.CostCenterUsers, false)]
    [InlineData(BudgetScope.CostCenterUsers, true)]
    public async Task SharedLicensedUsersDelegateCentersDespiteForeignOrganizationCenter(BudgetScope scope, bool existing)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "shared" }]);
        if (existing)
            fixture.Budgets.Add(Fixture.Budget("shared-center",
                "multi_user_cost_center", "Engineering"));
        var dashboard = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Equal("cc-1", Assert.Single(dashboard.CostCenters).Id);
        if (existing)
        {
            var record = Assert.Single(dashboard.Budgets);
            await fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint, Input(scope, "cc-1", 75), default);
            await fixture.Service.DeleteAsync(Identity, record.Id, record.Fingerprint, "Removing shared center limit", default);
            Assert.Equal([HttpMethod.Patch, HttpMethod.Delete], fixture.Writes.Select(write => write.Method).ToArray());
        }
        else
        {
            await fixture.Service.CreateAsync(Identity, Input(scope, "cc-1"), default);
            Assert.Equal(HttpMethod.Post, Assert.Single(fixture.Writes).Method);
        }
        Assert.Equal("cc-1", fixture.Writes.First().Body.GetProperty("budget_entity_name").GetString());
    }

    [Fact]
    public async Task OrganizationContainingCenterDoesNotExposeDefaultButPreservesIndividualEligibility()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering",
            [new { type = "Org", name = "acme" }, new { type = "User", name = "shared" }]);
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "shared"), default);
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task AdditionalOrganizationMembershipDoesNotRevokeUserOnlyCenterAccess()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "developer" }]);
        fixture.Budgets.Add(Fixture.Budget("default", "multi_user_cost_center", "Engineering"));
        var before = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        fixture.Routes["GET /orgs/beta/members?per_page=100&page=1"] =
            () => Fixture.Json(new[] { new { login = "outsider" }, new { login = "shared" }, new { login = "developer" } });
        var after = await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Equal("cc-1", Assert.Single(after.CostCenters).Id);
        Assert.Equal(before.Fingerprint, Assert.Single(after.Budgets).Fingerprint);
        await fixture.Service.UpdateAsync(Identity, before.Id, before.Fingerprint,
            Input(BudgetScope.CostCenterUsers, "cc-1"), default);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedUserCenterMembershipOrLicenseRevocationStillBlocksAllWrites(bool revokeSeat)
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering", [new { type = "User", name = "shared" }]);
        fixture.Budgets.Add(Fixture.Budget("default", "multi_user_cost_center", "Engineering"));
        var before = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        if (revokeSeat)
            fixture.Routes["GET /orgs/acme/copilot/billing/seats?per_page=100&page=1"] =
                () => Fixture.Json(new { total_seats = 1, seats = new[] { new { assignee = new { login = "alice" } } } });
        else
            fixture.Routes["GET /orgs/acme/members?per_page=100&page=1"] =
                () => Fixture.Json(new[] { new { login = "alice" }, new { login = "developer" } });
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        await AssertStatus(403, () => fixture.Service.UpdateAsync(Identity, before.Id, before.Fingerprint,
            Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, before.Id, before.Fingerprint, "Removing center default", default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task SharedLicensedUserDoesNotAuthorizeOtherForeignUsersInSameCenter()
    {
        using var fixture = new Fixture();
        fixture.SetCenter("cc-1", "Engineering",
            [new { type = "User", name = "shared" }, new { type = "User", name = "outsider" }]);
        Assert.Empty((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters);
        await AssertStatus(403, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task UpdateCannotChangeScopeEvenBetweenAuthorizedTargets()
    {
        using var fixture = new Fixture();
        fixture.Budgets.Add(Fixture.Budget("b1", "user", "", "developer"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        await AssertStatus(400, () => fixture.Service.UpdateAsync(Identity, record.Id, record.Fingerprint,
            Input(BudgetScope.CostCenterUsers, "cc-1"), default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleUpdateAndDeleteAreRejected(bool delete)
    {
        using var fixture = new Fixture();
        fixture.Budgets.Add(Fixture.Budget("b1", "user", "", "developer"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        fixture.Budgets[0]["budget_amount"] = 99;
        await AssertStatus(409, () => delete
            ? fixture.Service.DeleteAsync(Identity, "b1", record.Fingerprint, "Removing budget", default)
            : fixture.Service.UpdateAsync(Identity, "b1", record.Fingerprint, Input(), default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task FreshDeleteUsesExactBudgetEndpoint()
    {
        using var fixture = new Fixture();
        fixture.Budgets.Add(Fixture.Budget("b1", "user", "", "developer"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        await fixture.Service.DeleteAsync(Identity, "b1", record.Fingerprint, "Removing budget", default);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(HttpMethod.Delete, write.Method);
        Assert.EndsWith("/budgets/b1", write.Path);
    }

    [Fact]
    public async Task LatestBudgetIsReadBeforeWriteAndRetargetingIsDenied()
    {
        using var fixture = new Fixture();
        fixture.Budgets.Add(Fixture.Budget("b1", "user", "", "developer"));
        var record = Assert.Single((await fixture.Service.GetDashboardAsync(Identity, default)).Budgets);
        fixture.Routes["GET /enterprises/enterprise/settings/billing/budgets/b1"] =
            () => Fixture.Json(Fixture.Budget("b1", "user", "", "outsider"));
        await AssertStatus(403, () => fixture.Service.DeleteAsync(Identity, "b1", record.Fingerprint, "Removing budget", default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task BudgetPaginationDetectsDuplicateOnLaterPage()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /enterprises/enterprise/settings/billing/budgets?per_page=100&page=1"] =
            () => Fixture.Json(new { budgets = new[] { Fixture.Budget("foreign", "organization", "beta") }, has_next_page = true, total_count = 2 });
        fixture.Routes["GET /enterprises/enterprise/settings/billing/budgets?per_page=100&page=2"] =
            () => Fixture.Json(new { budgets = new[] { Fixture.Budget("existing", "user", "", "developer") }, has_next_page = false, total_count = 2 });
        await AssertStatus(409, () => fixture.Service.CreateAsync(Identity, Input(), default));
        Assert.Empty(fixture.Writes);
        Assert.Contains(fixture.Requests, request => request.Path.EndsWith("budgets?per_page=100&page=2"));
    }

    [Fact]
    public async Task MembershipLinkPaginationPreservesSharedUserEligibility()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /orgs/acme/members?per_page=100&page=1"] = () =>
        {
            var response = Fixture.Json(new[] { new { login = "alice" }, new { login = "shared" } });
            response.Headers.Add("Link", """<https://evil.example/leak>; rel="next" """);
            return response;
        };
        fixture.Routes["GET /orgs/acme/members?per_page=100&page=2"] = () => Fixture.Json(new[] { new { login = "developer" } });
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default);
        Assert.Single(fixture.Writes);
        Assert.All(fixture.Requests, request => Assert.Equal("api.github.com", request.Host));
        Assert.Contains(fixture.Requests, request => request.Path == "/orgs/acme/members?per_page=100&page=2");
    }

    [Fact]
    public async Task CostCenterResourcePaginationDoesNotRevokeLicensedMemberOverride()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /enterprises/enterprise/settings/billing/cost-centers/cc-2?per_page=100&page=1"] =
            () => Fixture.Json(new { id = "cc-2", name = "Other", state = "active", resources = new[] { new { type = "user", name = "outsider" } }, has_next_page = true });
        fixture.Routes["GET /enterprises/enterprise/settings/billing/cost-centers/cc-2?per_page=100&page=2"] =
            () => Fixture.Json(new { id = "cc-2", name = "Other", state = "active", resources = new[] { new { type = "user", name = "developer" } }, has_next_page = false });
        await fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default);
        Assert.Single(fixture.Writes);
        Assert.DoesNotContain((await fixture.Service.GetDashboardAsync(Identity, default)).CostCenters,
            center => center.Id == "cc-2");
    }

    [Fact]
    public async Task UpstreamFailureNeverBecomesEmptyAuthorizationData()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /orgs/acme/members?per_page=100&page=1"] =
            () => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret upstream detail") };
        var exception = await AssertStatus(502, () => fixture.Service.CreateAsync(Identity, Input(BudgetScope.User, "developer"), default));
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task IncompleteBudgetPageFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /enterprises/enterprise/settings/billing/budgets?per_page=100&page=1"] =
            () => Fixture.Json(new { budgets = System.Array.Empty<object>(), has_next_page = false, total_count = 1 });
        await AssertStatus(502, () => fixture.Service.CreateAsync(Identity, Input(), default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(BudgetScope.User, false, 10, null)]
    [InlineData(BudgetScope.CostCenterUsers, false, 10, null)]
    [InlineData(BudgetScope.CostCenterUsers, true, -1, null)]
    [InlineData(BudgetScope.CostCenterUsers, true, 10, "2030-09-24")]
    [InlineData(BudgetScope.User, true, 10, "2030-09-23")]
    [InlineData(BudgetScope.User, true, 10, "2030-09-22")]
    [InlineData((BudgetScope)99, true, 10, null)]
    [InlineData((BudgetScope)0, true, 10, null)]
    public async Task RejectsInvalidAmountScopeHardStopAndExpiry(BudgetScope scope, bool hardStop, int amount, string? expires)
    {
        using var fixture = new Fixture();
        var input = Input(scope, "developer", amount);
        input.PreventFurtherUsage = hardStop;
        input.ExpiresAt = expires is null ? null : DateOnly.Parse(expires);
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, input, default));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task EnforcesConfiguredMaximum()
    {
        using var fixture = new Fixture();
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, Input(amount: 10001), default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("abcd")]
    [InlineData("reason\nforged log")]
    public async Task RejectsMissingOrUnsafeAuditReason(string reason)
    {
        using var fixture = new Fixture();
        var input = Input();
        input.Reason = reason;
        await AssertStatus(400, () => fixture.Service.CreateAsync(Identity, input, default));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("http://api.github.com/")]
    [InlineData("https://github.com/")]
    [InlineData("https://api.github.com.evil.example/")]
    [InlineData("https://api.github.com:8443/")]
    [InlineData("https://alice:password@api.github.com/")]
    [InlineData("https://api.github.com/path")]
    [InlineData("https://api.github.com/?query=value")]
    [InlineData("https://api.ghe.com/")]
    [InlineData("https://api.one.two.ghe.com/")]
    [InlineData("https://api.-enterprise.ghe.com/")]
    [InlineData("https://api.enterprise-.ghe.com/")]
    public void RejectsUntrustedApiOrigins(string origin) =>
        Assert.Equal(502, Assert.Throws<PortalException>(() => GitHubApi.ValidateBaseUrl(origin)).StatusCode);

    [Theory]
    [InlineData("https://api.github.com/")]
    [InlineData("https://api.enterprise.ghe.com/")]
    public void AcceptsSupportedApiOrigins(string origin) => Assert.Equal(origin, GitHubApi.ValidateBaseUrl(origin).AbsoluteUri);

    [Fact]
    public async Task RedirectIsAnExplicitFailure()
    {
        using var fixture = new Fixture();
        fixture.Routes["GET /users/alice"] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/");
            return response;
        };
        await AssertStatus(502, () => fixture.Service.GetDashboardAsync(Identity, default));
        Assert.DoesNotContain(fixture.Requests, request => request.Host == "evil.example");
    }

    [Fact]
    public async Task InstallationTokensAreCachedOnlyUntilExpiry()
    {
        using var fixture = new Fixture();
        await fixture.Service.GetDashboardAsync(Identity, default);
        await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Equal(3, fixture.Requests.Count(request => request.Path.EndsWith("/access_tokens")));
        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        await fixture.Service.GetDashboardAsync(Identity, default);
        Assert.Equal(6, fixture.Requests.Count(request => request.Path.EndsWith("/access_tokens")));
    }

    [Fact]
    public async Task AppTokenUsesShortLivedSignedRsaJwt()
    {
        using var fixture = new Fixture();
        await fixture.Service.GetDashboardAsync(Identity, default);
        var tokenRequest = fixture.Requests.First(request => request.Path.EndsWith("/access_tokens"));
        var parts = tokenRequest.Token.Split('.');
        Assert.Equal(3, parts.Length);
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("123", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(fixture.Clock.Now.AddSeconds(-60).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(fixture.Clock.Now.AddMinutes(9).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
        using var rsa = RSA.Create();
        rsa.ImportFromPem(fixture.Options.GitHub.PrivateKeyPem);
        Assert.True(rsa.VerifyData(System.Text.Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    private static BudgetInput Input(BudgetScope scope = BudgetScope.User, string target = "developer", int amount = 10) =>
        new() { Scope = scope, Target = target, Amount = amount, Reason = "Approved budget change" };

    private static async Task<PortalException> AssertStatus(int expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PortalException>(action);
        Assert.Equal(expected, exception.StatusCode);
        return exception;
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 9, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Request(HttpMethod Method, string Path, string Host, string Token, JsonElement Body);

    private sealed class Fixture : IDisposable
    {
        private static readonly string Key = CreateKey();
        private readonly HttpClient client;
        private readonly Dictionary<string, object> centers = [];
        private int activeRequests;
        private int peakConcurrency;
        public FakeClock Clock { get; } = new();
        public PortalOptions Options { get; }
        public GitHubBudgetService Service { get; }
        public GitHubInstallationDiscovery Discovery { get; }
        public GitHubApi Api { get; }
        public TimeSpan ReadDelay { get; set; }
        public int PeakConcurrency => Volatile.Read(ref peakConcurrency);
        public List<Dictionary<string, object?>> Budgets { get; } = [];
        public ConcurrentQueue<Request> Requests { get; } = [];
        public ConcurrentQueue<Request> Writes { get; } = [];
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> AsyncRoutes { get; } = [];

        public Fixture()
        {
            Options = new PortalOptions
            {
                GitHub = new GitHubOptions { Enterprise = "enterprise", AppId = "123", PrivateKeyPem = Key },
                MaxBudgetAmount = 10000
            };
            Routes["GET /app/installations?per_page=100&page=1"] = () => Json(new object[]
            {
                new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" } },
                Installation("acme", 2), Installation("beta", 3)
            });
            Routes["GET /enterprises/enterprise/apps/installable_organizations?per_page=100&page=1"] =
                () => Json(new[] { new { login = "acme" }, new { login = "beta" } });
            var filter = Uri.EscapeDataString($"externalId eq \"{Identity.ObjectId}\"");
            Routes[$"GET /scim/v2/enterprises/enterprise/Users?filter={filter}"] = () => Json(new
            {
                totalResults = 1,
                Resources = new[] { new { externalId = Identity.ObjectId, userName = "alice", active = true } }
            });
            Routes["GET /users/alice"] = () => Json(new { login = "alice", id = 7 });
            Routes["GET /orgs/acme/memberships/alice"] = () => Json(new { role = "admin", state = "active" });
            Routes["GET /orgs/beta/memberships/alice"] = () => new HttpResponseMessage(HttpStatusCode.NotFound);
            Routes["GET /orgs/acme/members?per_page=100&page=1"] =
                () => Json(new[]
                {
                    new { login = "alice" },
                    new { login = "developer" },
                    new { login = "shared" },
                    new { login = "unlicensed" }
                });
            Routes["GET /orgs/beta/members?per_page=100&page=1"] =
                () => Json(new[] { new { login = "outsider" }, new { login = "shared" } });
            Routes["GET /orgs/acme/copilot/billing/seats?per_page=100&page=1"] = () => Json(new
            {
                total_seats = 3,
                seats = new[]
                {
                    new { assignee = new { login = "alice" } },
                    new { assignee = new { login = "developer" } },
                    new { assignee = new { login = "shared" } }
                }
            });
            Routes["GET /orgs/beta/copilot/billing/seats?per_page=100&page=1"] = () => Json(new
            {
                total_seats = 2,
                seats = new[]
                {
                    new { assignee = new { login = "outsider" } },
                    new { assignee = new { login = "shared" } }
                }
            });
            SetCenter("cc-1", "Engineering", [new { type = "Org", name = "acme" }]);
            SetCenter("cc-2", "Other", [new { type = "Org", name = "beta" }]);
            Routes["GET /enterprises/enterprise/settings/billing/cost-centers"] = () => Json(new { costCenters = centers.Values });
            client = new HttpClient(new Handler(Handle));
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            Api = new GitHubApi(client, options, Clock);
            Discovery = new GitHubInstallationDiscovery(Api, options, Clock);
            Service = new GitHubBudgetService(Api, Discovery,
                options, Clock, NullLogger<GitHubBudgetService>.Instance);
        }

        public void SetInstalledOrganizations(params string[] logins) =>
            Routes["GET /app/installations?per_page=100&page=1"] = () => Json(new object[]
            {
                new { id = 1, target_type = "Enterprise", account = new { slug = "enterprise" } }
            }.Concat(logins.Select((login, index) => Installation(login, index + 2))));

        public void ClearCenters() => centers.Clear();
        public void ResetMetrics() => Interlocked.Exchange(ref peakConcurrency, 0);

        public void SetCenter(string id, string name, object[] resources)
        {
            var center = new { id, name, state = "active", resources };
            centers[id] = center;
            Routes[$"GET /enterprises/enterprise/settings/billing/cost-centers/{id}?per_page=100&page=1"] = () => Json(center);
        }

        private async Task<HttpResponseMessage> Handle(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref activeRequests);
            int previous;
            do { previous = Volatile.Read(ref peakConcurrency); }
            while (active > previous && Interlocked.CompareExchange(ref peakConcurrency, active, previous) != previous);
            try
            {
                if (message.Method == HttpMethod.Get && ReadDelay > TimeSpan.Zero)
                    await Task.Delay(ReadDelay, cancellationToken);
                return await HandleCore(message, cancellationToken);
            }
            finally { Interlocked.Decrement(ref activeRequests); }
        }

        private async Task<HttpResponseMessage> HandleCore(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            var request = new Request(message.Method, message.RequestUri!.PathAndQuery, message.RequestUri.Host,
                message.Headers.Authorization!.Parameter!,
                message.Content is null ? default : JsonDocument.Parse(await message.Content.ReadAsStringAsync()).RootElement.Clone());
            Requests.Enqueue(request);
            if (AsyncRoutes.TryGetValue($"{request.Method} {request.Path}", out var asyncResponse))
                return await asyncResponse(cancellationToken);
            if (Routes.TryGetValue($"{request.Method} {request.Path}", out var response)) return response();
            if (request.Method == HttpMethod.Get &&
                request.Path.StartsWith("/scim/v2/enterprises/enterprise/Users?filter=", StringComparison.Ordinal))
                return Json(new { totalResults = 0, Resources = System.Array.Empty<object>() });
            if (request.Path.StartsWith("/app/installations/") && request.Path.EndsWith("/access_tokens"))
            {
                var installation = request.Path.Split('/')[3];
                return Json(new { token = $"installation-{installation}", expires_at = Clock.Now.AddHours(1) });
            }
            if (request.Method == HttpMethod.Get && request.Path == "/enterprises/enterprise/settings/billing/budgets?per_page=100&page=1")
                return Json(new { budgets = Budgets, has_next_page = false, total_count = Budgets.Count });
            if (request.Method == HttpMethod.Get && request.Path.StartsWith("/enterprises/enterprise/settings/billing/budgets/"))
            {
                var id = Uri.UnescapeDataString(request.Path["/enterprises/enterprise/settings/billing/budgets/".Length..]);
                return Json(Budgets.Single(budget => (string)budget["id"]! == id));
            }
            if (request.Method != HttpMethod.Get && request.Path.StartsWith("/enterprises/enterprise/settings/billing/budgets"))
            {
                Writes.Enqueue(request);
                return Json(new { message = "Success" });
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.Path}");
        }

        public static Dictionary<string, object?> Budget(string id, string scope, string entity, string? user = null) => new()
        {
            ["id"] = id,
            ["budget_scope"] = scope,
            ["budget_entity_name"] = entity,
            ["user"] = user,
            ["budget_amount"] = 25,
            ["prevent_further_usage"] = true,
            ["budget_type"] = "BundlePricing",
            ["budget_product_sku"] = "ai_credits",
            ["budget_alerting"] = new { will_alert = false, alert_recipients = System.Array.Empty<string>() }
        };

        public static HttpResponseMessage Json(object? value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Page(object value, bool hasNext)
        {
            var response = Json(value);
            if (hasNext) response.Headers.Add("Link", """<https://api.github.com/?page=2>; rel="next" """);
            return response;
        }

        public static object Installation(string login, long id) => new
        {
            id,
            account = new { login },
            target_type = "Organization"
        };

        private static string CreateKey()
        {
            using var rsa = RSA.Create(2048);
            return rsa.ExportRSAPrivateKeyPem();
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
