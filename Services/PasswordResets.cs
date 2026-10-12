using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace DonationAutomate.Services;

/// <summary>
/// Signed tokens for the "create / reset password" link emailed to the admin. A token is valid
/// for 30 minutes, only for the current Admin:Email, and only until the password changes, so a
/// link works once. At most one email every 2 minutes, so the button can't be used to flood the
/// inbox.
/// </summary>
public sealed class PasswordResets(IDataProtectionProvider protection, AdminPasswords passwords, IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);
    private const string SentKey = "password-reset-sent";

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("DonationAutomate.PasswordReset").ToTimeLimitedDataProtector();

    /// <summary>A token for a new link, or null if one was sent less than 2 minutes ago.</summary>
    public string? Create(string email)
    {
        if (cache.TryGetValue(SentKey, out _)) return null;
        cache.Set(SentKey, true, MinInterval);
        return _protector.Protect($"{email}|{passwords.Stamp}", Lifetime);
    }

    public bool IsValid(string? token, string email)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            return parts.Length == 2
                && string.Equals(parts[0], email, StringComparison.OrdinalIgnoreCase)
                && parts[1] == passwords.Stamp;
        }
        catch (CryptographicException)
        {
            return false;  // expired or tampered with
        }
    }
}
