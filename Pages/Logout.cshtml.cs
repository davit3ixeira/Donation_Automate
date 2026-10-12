using DonationAutomate.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

/// <summary>Logs out and forgets this browser, so the next login here asks for an emailed code again.</summary>
public class LogoutModel(TrustedDevices devices) : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        devices.Forget(HttpContext);
        return RedirectToPage("/Login");
    }
}
