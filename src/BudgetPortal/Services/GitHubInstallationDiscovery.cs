using System.Text.Json;
using BudgetPortal.Domain;
using Microsoft.Extensions.Options;

namespace BudgetPortal.Services;

public sealed record GitHubOrganization(string Login, long InstallationId);
public sealed record GitHubInstallations(long EnterpriseInstallationId,
    IReadOnlyList<GitHubOrganization> Organizations, IReadOnlyList<string> EnterpriseOrganizationLogins);

public sealed class GitHubInstallationDiscovery(
    GitHubApi api, IOptions<PortalOptions> options, TimeProvider clock)
{
    private const int MaxPages = 10000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private GitHubInstallations? cached;
    private DateTimeOffset refreshAt;
    private sealed record InstalledOrganization(GitHubOrganization Organization, bool Suspended);
    private sealed record AppInstallations(long EnterpriseInstallationId, IReadOnlyList<InstalledOrganization> Organizations);

    public async Task<GitHubInstallations> GetAsync(CancellationToken cancellationToken)
    {
        if (cached is not null && refreshAt > clock.GetUtcNow()) return cached;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null && refreshAt > clock.GetUtcNow()) return cached;
            cached = await DiscoverAsync(cancellationToken);
            refreshAt = clock.GetUtcNow().AddMinutes(10);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GitHubInstallations> DiscoverAsync(CancellationToken cancellationToken)
    {
        var installed = await ReadInstallationsAsync(cancellationToken);
        var organizationLogins = await ReadOrganizationsAsync(installed.EnterpriseInstallationId, cancellationToken);
        if (organizationLogins.Count == 0) throw GitHubApi.InvalidResponse();
        var selected = installed.Organizations
            .Where(installation => organizationLogins.Contains(installation.Organization.Login))
            .OrderBy(installation => installation.Organization.Login, StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0)
            throw new PortalException(502, "Install the GitHub App in at least one organization of the configured enterprise.");
        if (selected.Any(installation => installation.Suspended))
            throw new PortalException(502, "A GitHub App installation in the configured enterprise is suspended. Contact the enterprise administrator.");
        return new GitHubInstallations(installed.EnterpriseInstallationId,
            selected.Select(installation => installation.Organization).ToArray(),
            organizationLogins.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<AppInstallations> ReadInstallationsAsync(CancellationToken cancellationToken)
    {
        var matches = new List<long>();
        var organizations = new Dictionary<string, InstalledOrganization>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<long>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = (await api.SendAsAppAsync(HttpMethod.Get,
                $"app/installations?per_page=100&page={page}", null, cancellationToken))!;
            if (response.Body.ValueKind != JsonValueKind.Array) throw GitHubApi.InvalidResponse();
            foreach (var installation in response.Body.EnumerateArray())
            {
                var type = GitHubApi.RequiredString(installation, "target_type");
                if (!installation.TryGetProperty("account", out var account) ||
                    account.ValueKind != JsonValueKind.Object ||
                    !installation.TryGetProperty("id", out var idField) ||
                    !idField.TryGetInt64(out var id) || id <= 0 || !ids.Add(id))
                    throw GitHubApi.InvalidResponse();
                var isSuspended = installation.TryGetProperty("suspended_at", out var suspended) &&
                    suspended.ValueKind != JsonValueKind.Null;
                if (type == "Enterprise" &&
                    string.Equals(GitHubApi.RequiredString(account, "slug"), options.Value.GitHub.Enterprise, StringComparison.OrdinalIgnoreCase))
                {
                    if (isSuspended)
                        throw new PortalException(502, "The GitHub App enterprise installation is suspended. Contact the enterprise administrator.");
                    matches.Add(id);
                }
                else if (type == "Organization")
                {
                    var login = GitHubApi.RequiredString(account, "login");
                    if (!organizations.TryAdd(login, new InstalledOrganization(new GitHubOrganization(login, id), isSuspended)))
                        throw GitHubApi.InvalidResponse();
                }
            }
            if (!response.HasNextLink)
            {
                if (matches.Count != 1)
                    throw new PortalException(502, "The GitHub App must have exactly one installation on the configured enterprise. Contact the enterprise administrator.");
                return new AppInstallations(matches[0], organizations.Values.ToArray());
            }
            if (response.Body.GetArrayLength() == 0) throw GitHubApi.InvalidResponse();
        }
        throw GitHubApi.InvalidResponse();
    }

    private async Task<HashSet<string>> ReadOrganizationsAsync(long enterpriseInstallation, CancellationToken cancellationToken)
    {
        var organizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxPages; page++)
        {
            var response = (await api.SendAsync(enterpriseInstallation, HttpMethod.Get,
                $"enterprises/{Escape(options.Value.GitHub.Enterprise)}/apps/installable_organizations?per_page=100&page={page}",
                null, cancellationToken))!;
            if (response.Body.ValueKind != JsonValueKind.Array) throw GitHubApi.InvalidResponse();
            foreach (var organization in response.Body.EnumerateArray())
                if (!organizations.Add(GitHubApi.RequiredString(organization, "login")))
                    throw GitHubApi.InvalidResponse();
            if (!response.HasNextLink) return organizations;
            if (response.Body.GetArrayLength() == 0) throw GitHubApi.InvalidResponse();
        }
        throw GitHubApi.InvalidResponse();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
