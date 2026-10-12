using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace DonationAutomate.Services;

/// <summary>
/// Browsers where the admin already typed an emailed login code (or used an emailed password
/// link). They get a signed cookie, valid for a year, and are not asked for a code again. It holds
/// the admin email and the password stamp, so changing either forgets every browser; logging out
/// forgets the current one.
/// </summary>
public sealed class TrustedDevices(IDataProtectionProvider protection)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);
    private const string CookieName = "dispositivo";

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("DonationAutomate.TrustedDevice").ToTimeLimitedDataProtector();

    public bool IsTrusted(HttpContext context, string email, string passwordStamp)
    {
        if (context.Request.Cookies[CookieName] is not { Length: > 0 } value) return false;
        try
        {
            var parts = _protector.Unprotect(value).Split('|');
            return parts.Length == 2
                && string.Equals(parts[0], email, StringComparison.OrdinalIgnoreCase)
                && parts[1] == passwordStamp;
        }
        catch (CryptographicException)
        {
            return false;  // expired, tampered with, or signed with keys this server no longer has
        }
    }

    public void Trust(HttpContext context, string email, string passwordStamp) =>
        context.Response.Cookies.Append(CookieName, _protector.Protect($"{email}|{passwordStamp}", Lifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.Add(Lifetime),
        });

    public void Forget(HttpContext context) => context.Response.Cookies.Delete(CookieName);
}
