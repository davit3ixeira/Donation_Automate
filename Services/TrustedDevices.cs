using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace DonationAutomate.Services;

/// <summary>
/// Browsers where the admin already typed an emailed login code. They get a signed cookie holding
/// the admin email, valid for a year, and are not asked for a code again. Changing Admin:Email
/// invalidates them all; logging out forgets the current one.
/// </summary>
public sealed class TrustedDevices(IDataProtectionProvider protection)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);
    private const string CookieName = "dispositivo";

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("DonationAutomate.TrustedDevice").ToTimeLimitedDataProtector();

    public bool IsTrusted(HttpContext context, string email)
    {
        if (context.Request.Cookies[CookieName] is not { Length: > 0 } value) return false;
        try
        {
            return string.Equals(_protector.Unprotect(value), email, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return false;  // expired, tampered with, or signed with keys this server no longer has
        }
    }

    public void Trust(HttpContext context, string email) =>
        context.Response.Cookies.Append(CookieName, _protector.Protect(email, Lifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.Add(Lifetime),
        });

    public void Forget(HttpContext context) => context.Response.Cookies.Delete(CookieName);
}
