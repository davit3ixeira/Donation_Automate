using System.Security.Cryptography;
using System.Text;

namespace DonationAutomate.Services;

/// <summary>
/// Keeps the donor site private: only people who opened it through the link with the access key
/// (SiteAccessKey, put in the emails' {link}) get in, and their browser remembers it. Anyone else
/// is sent to a page asking for the link. The staff pages, the login and static files are not
/// affected. Without a key configured, the site is open.
/// </summary>
public static class SiteAccess
{
    public const string QueryName = "acesso";
    private const string CookieName = "acesso";

    /// <summary>Pages that don't need the key (the staff pages have their own login).</summary>
    private static readonly string[] OpenPaths = ["/Privado", "/Login", "/Logout", "/Admin", "/Emails", "/Error"];

    public static string? Key(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config["SiteAccessKey"]) ? null : config["SiteAccessKey"]!.Trim();

    /// <summary>The link donors get: the site address with the access key, if there is one.</summary>
    public static string Link(string siteUrl, IConfiguration config) =>
        Key(config) is { } key ? $"{siteUrl}?{QueryName}={Uri.EscapeDataString(key)}" : siteUrl;

    /// <summary>Goes after UseAuthentication, so a logged-in admin always gets in.</summary>
    public static IApplicationBuilder UseSiteAccess(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // Private either way: keep search engines out.
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        var key = Key(context.RequestServices.GetRequiredService<IConfiguration>());
        if (key is null
            || context.User.Identity?.IsAuthenticated == true
            || OpenPaths.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
            || HasAccess(context, key))
        {
            await next();
            return;
        }
        context.Response.Redirect($"{context.Request.PathBase}/Privado");
    });

    private static bool HasAccess(HttpContext context, string key)
    {
        if (Matches(context.Request.Query[QueryName], key))
        {
            // Remember it, so later visits (and the page's own requests) don't need the key in the address.
            context.Response.Cookies.Append(CookieName, key, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
            });
            return true;
        }
        return Matches(context.Request.Cookies[CookieName], key);
    }

    private static bool Matches(string? given, string key) =>
        !string.IsNullOrEmpty(given)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given.Trim()), Encoding.UTF8.GetBytes(key));
}
