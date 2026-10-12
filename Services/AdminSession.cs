using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DonationAutomate.Services;

/// <summary>
/// The admin's login cookie. It records which password it was issued under, so changing the
/// password logs out every browser at its next request.
/// </summary>
public static class AdminSession
{
    private const string StampClaim = "password-stamp";

    public static Task SignInAsync(HttpContext context, string email, string passwordStamp)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, email), new Claim(StampClaim, passwordStamp)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>Cookie check on every request: the password must still be the one it was issued under.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var passwords = context.HttpContext.RequestServices.GetRequiredService<AdminPasswords>();
        if (passwords.IsSet && context.Principal?.FindFirst(StampClaim)?.Value == passwords.Stamp) return;

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
