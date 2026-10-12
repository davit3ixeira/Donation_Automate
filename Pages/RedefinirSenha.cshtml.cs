using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

/// <summary>
/// Where the emailed link leads: sets the new admin password. Opening the link proves access to
/// the admin inbox, the same proof as a login code, so the admin is then logged in and this
/// browser trusted. Every other session ends, since the password stamp changed.
/// </summary>
public class RedefinirSenhaModel(IConfiguration config, AdminPasswords passwords, PasswordResets resets,
    TrustedDevices devices) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public bool ValidLink { get; private set; }

    private string Email => config["Admin:Email"]?.Trim() ?? "";

    public void OnGet() => ValidLink = Email != "" && resets.IsValid(Token, Email);

    public async Task<IActionResult> OnPostAsync(string? password, string? confirmation)
    {
        ValidLink = Email != "" && resets.IsValid(Token, Email);
        if (!ValidLink) return Page();

        if (AdminPasswords.Problem(password, confirmation) is { } problem)
        {
            ModelState.AddModelError(string.Empty, problem);
            return Page();
        }

        passwords.Set(password!);
        devices.Trust(HttpContext, Email, passwords.Stamp);
        await AdminSession.SignInAsync(HttpContext, Email, passwords.Stamp);
        TempData["Message"] = "Senha definida. Use-a nos próximos logins.";
        return RedirectToPage("/Admin");
    }
}
