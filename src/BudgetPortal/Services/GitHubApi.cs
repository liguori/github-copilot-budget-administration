using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BudgetPortal.Domain;
using Microsoft.Extensions.Options;

namespace BudgetPortal.Services;

public sealed record GitHubResponse(JsonElement Body, bool HasNextLink);

/// <summary>All requests are relative to an allowlisted origin; pagination never follows supplied URLs.</summary>
public sealed class GitHubApi(HttpClient http, IOptions<PortalOptions> options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<long, Token> tokens = new();
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private readonly SemaphoreSlim requestGate = new(GitHubReadBatch.MaximumConcurrency, GitHubReadBatch.MaximumConcurrency);
    private readonly ConcurrentDictionary<long, DateTimeOffset> rateLimitResets = new();
    private sealed record Token(string Value, DateTimeOffset ExpiresAt);
    private const string RateLimitMessage = "GitHub API rate limit reached. Wait before reloading; no successful change was confirmed.";

    public static Uri ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" ||
            !(uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
              System.Text.RegularExpressions.Regex.IsMatch(uri.Host,
                  @"\Aapi\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.ghe\.com\z",
                  System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new PortalException(502, "The GitHub API origin is not configured correctly.");
        return uri;
    }

    public async Task<GitHubResponse?> SendAsync(long installationId, HttpMethod method, string path,
        object? payload, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        var token = await GetTokenAsync(installationId, cancellationToken);
        return await SendCoreAsync(method, path, token, payload, cancellationToken, allowNotFound, installationId);
    }

    public Task<GitHubResponse?> SendAsAppAsync(HttpMethod method, string path, object? payload,
        CancellationToken cancellationToken, bool allowNotFound = false) =>
        SendCoreAsync(method, path, CreateJwt(), payload, cancellationToken, allowNotFound);

    private async Task<string> GetTokenAsync(long installationId, CancellationToken cancellationToken)
    {
        if (installationId <= 0)
            throw new PortalException(502, "A GitHub App installation is not configured.");
        if (tokens.TryGetValue(installationId, out var cached) && cached.ExpiresAt > clock.GetUtcNow().AddMinutes(1))
            return cached.Value;
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (tokens.TryGetValue(installationId, out cached) && cached.ExpiresAt > clock.GetUtcNow().AddMinutes(1))
                return cached.Value;
            var response = await SendCoreAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens",
                CreateJwt(), null, cancellationToken, false);
            var body = response!.Body;
            var value = RequiredString(body, "token");
            if (!body.TryGetProperty("expires_at", out var expires) ||
                !expires.TryGetDateTimeOffset(out var expiresAt) || expiresAt <= clock.GetUtcNow().AddMinutes(1))
                throw InvalidResponse();
            // Never cache longer than GitHub's expiry, even if a server returns an unexpectedly long lifetime.
            tokens[installationId] = new Token(value, expiresAt < clock.GetUtcNow().AddHours(1) ? expiresAt : clock.GetUtcNow().AddHours(1));
            return value;
        }
        finally { tokenLock.Release(); }
    }

    private string CreateJwt()
    {
        try
        {
            var config = options.Value.GitHub;
            if (string.IsNullOrWhiteSpace(config.AppId)) throw new CryptographicException();
            using var rsa = RSA.Create();
            rsa.ImportFromPem(config.PrivateKeyPem);
            if (rsa.KeySize < 2048) throw new CryptographicException();
            var now = clock.GetUtcNow();
            var header = Encode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
                exp = now.AddMinutes(9).ToUnixTimeSeconds(),
                iss = config.AppId
            }));
            var signed = $"{header}.{payload}";
            return signed + "." + Encode(rsa.SignData(Encoding.UTF8.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new PortalException(502, "GitHub App signing credentials are not configured correctly.");
        }
    }

    private async Task<GitHubResponse?> SendCoreAsync(HttpMethod method, string path, string bearer,
        object? payload, CancellationToken cancellationToken, bool allowNotFound, long rateLimitScope = 0)
    {
        EnsureRateLimitAvailable(rateLimitScope);
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            EnsureRateLimitAvailable(rateLimitScope);
            return await SendRequestAsync(method, path, bearer, payload, cancellationToken, allowNotFound, rateLimitScope);
        }
        finally { requestGate.Release(); }
    }

    private void EnsureRateLimitAvailable(long scope)
    {
        if (rateLimitResets.TryGetValue(scope, out var reset) && reset > clock.GetUtcNow())
            throw new PortalException(502, RateLimitMessage);
    }

    private async Task<GitHubResponse?> SendRequestAsync(HttpMethod method, string path, string bearer,
        object? payload, CancellationToken cancellationToken, bool allowNotFound, long rateLimitScope)
    {
        var origin = ValidateBaseUrl(options.Value.GitHub.ApiBaseUrl);
        var destination = new Uri(origin, path);
        if (!destination.GetLeftPart(UriPartial.Authority).Equals(origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            throw new PortalException(502, "Invalid GitHub request destination.");
        using var request = new HttpRequestMessage(method, destination);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("BudgetPortal/1.0");
        request.Headers.Add("X-GitHub-Api-Version", options.Value.GitHub.ApiVersion);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound) return null;
            var primaryLimitReached = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0");
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.Forbidden &&
                (response.Headers.RetryAfter is not null || primaryLimitReached))
            {
                var now = clock.GetUtcNow();
                var reset = response.Headers.RetryAfter?.Date;
                if (response.Headers.RetryAfter?.Delta is { } delay && delay > TimeSpan.Zero)
                    reset = now + delay;
                if (primaryLimitReached && response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
                    long.TryParse(resets.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
                    seconds >= 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                {
                    var primaryReset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (primaryReset > now && (!reset.HasValue || primaryReset > reset)) reset = primaryReset;
                }
                var retryAt = reset.HasValue && reset > now ? reset.Value : now.AddMinutes(1);
                rateLimitResets.AddOrUpdate(rateLimitScope, retryAt, (_, previous) => previous > retryAt ? previous : retryAt);
                throw new PortalException(502, RateLimitMessage);
            }
            if (!response.IsSuccessStatusCode)
            {
                // Never surface GitHub bodies, credentials, or internal endpoint details.
                var status = (int)response.StatusCode;
                throw new PortalException(status is 409 or 412 ? 409 : 502,
                    status is 409 or 412 ? "GitHub reported a conflicting change. Reload and try again." :
                    "GitHub could not complete the request. No successful change was confirmed.");
            }
            if (response.StatusCode == HttpStatusCode.NoContent)
                return new GitHubResponse(JsonSerializer.SerializeToElement(new { }), false);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var hasNext = response.Headers.TryGetValues("Link", out var links) &&
                          links.Any(link => link.Split(',').Any(part =>
                              System.Text.RegularExpressions.Regex.IsMatch(part, """;\s*rel\s*=\s*"?next"?(\s|;|$)""",
                                  System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
            return new GitHubResponse(document.RootElement.Clone(), hasNext);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PortalException(502, "GitHub returned an unavailable or invalid response. Reload before retrying a change.");
        }
    }

    internal static string RequiredString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
            throw InvalidResponse();
        return field.GetString()!;
    }

    internal static PortalException InvalidResponse() => new(502, "GitHub returned incomplete or invalid authorization data.");
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
