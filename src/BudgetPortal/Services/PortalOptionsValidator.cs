using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BudgetPortal.Domain;
using Microsoft.Extensions.Options;

namespace BudgetPortal.Services;

/// <summary>Validates trusted deployment configuration without contacting GitHub.</summary>
public sealed class PortalOptionsValidator : IValidateOptions<PortalOptions>
{
    public ValidateOptionsResult Validate(string? name, PortalOptions options)
    {
        var errors = new List<string>();
        if (options.GitHub is null)
            errors.Add("Portal:GitHub is required.");
        else
        {
            var github = options.GitHub;
            try { GitHubApi.ValidateBaseUrl(github.ApiBaseUrl); }
            catch (PortalException) { errors.Add("Portal:GitHub:ApiBaseUrl must be an approved HTTPS GitHub API origin."); }
            if (!Matches(github.Enterprise, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,98}[A-Za-z0-9])?\z"))
                errors.Add("Portal:GitHub:Enterprise must be a nonempty enterprise slug.");
            if (!long.TryParse(github.AppId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                errors.Add("Portal:GitHub:AppId must be a positive numeric App ID.");
            if (!DateOnly.TryParseExact(github.ApiVersion, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add("Portal:GitHub:ApiVersion must be a YYYY-MM-DD date.");
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(github.PrivateKeyPem);
                if (rsa.KeySize < 2048) throw new CryptographicException();
                // Parsing a public-only PEM succeeds, but an App JWT requires the private component.
                rsa.SignData([0], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException)
            {
                errors.Add("Portal:GitHub:PrivateKeyPem must contain an unencrypted RSA private key of at least 2048 bits.");
            }
        }

        if (options.MaxBudgetAmount < 0)
            errors.Add("Portal:MaxBudgetAmount must be nonnegative.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool Matches(string? value, string pattern) =>
        value is not null && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
