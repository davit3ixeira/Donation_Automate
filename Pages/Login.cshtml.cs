using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

/// <summary>
/// One admin account, set in configuration (Admin:Email and Admin:Password): environment variables
/// Admin__Email / Admin__Password on the server, appsettings.Local.json on a development machine.
/// </summary>
public class LoginModel(IConfiguration config) : PageModel
{
    [BindProperty]
    [Display(Name = "E-mail")]
    [Required(ErrorMessage = "Informe o e-mail.")]
    public string Usuario { get; set; } = "";

    [BindProperty]
    [Display(Name = "Senha")]
    [Required(ErrorMessage = "Informe a senha.")]
    public string Senha { get; set; } = "";

    public string? ReturnUrl { get; set; }

    public void OnGet(string? returnUrl) => ReturnUrl = returnUrl;

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        ReturnUrl = returnUrl;
        if (!ModelState.IsValid) return Page();

        var email = config["Admin:Email"];
        var password = config["Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            ModelState.AddModelError(string.Empty, "O acesso da equipe ainda não foi configurado neste servidor.");
            return Page();
        }

        if (!string.Equals(Usuario.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Senha), Encoding.UTF8.GetBytes(password)))
        {
            ModelState.AddModelError(string.Empty, "E-mail ou senha incorretos.");
            return Page();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Usuario)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Admin");
    }
}
