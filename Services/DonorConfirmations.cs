using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace DonationAutomate.Services;

/// <summary>
/// Signed tokens for the link in the "Confirme sua escolha" email. A token names the donor's email
/// and the children that email covered, and lasts until after the campaign. Using it twice is
/// harmless: children already approved are simply left as they are.
/// </summary>
public sealed class DonorConfirmations(IDataProtectionProvider protection)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(120);

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("DonationAutomate.DonorConfirmation").ToTimeLimitedDataProtector();

    public string Create(string email, IEnumerable<string> ids) =>
        _protector.Protect($"{email.Trim()}\n{string.Join("\n", ids)}", Lifetime);

    /// <summary>The donor's email and the children's ids, or null if the token is invalid or expired.</summary>
    public (string Email, IReadOnlyList<string> Ids)? Read(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var lines = _protector.Unprotect(token).Split('\n');
            return lines.Length < 2 ? null : (lines[0], lines[1..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
