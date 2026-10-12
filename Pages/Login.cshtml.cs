using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DonationAutomate.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

/// <summary>
/// One admin account, set in configuration (Admin:Email and Admin:Password): environment variables
/// Admin__Email / Admin__Password on the server, appsettings.Local.json on a development machine.
/// The first time in a browser, after the password, a code is emailed to Admin:Email and must be
/// typed in too (unless Admin:LoginCode is false); that browser is then trusted and not asked
/// again. The login itself lasts a year while the site is used.
/// </summary>
public class LoginModel(IConfiguration config, LoginCodes codes, TrustedDevices devices, MailService mail, ILogger<LoginModel> logger) : PageModel
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

    /// <summary>Set once the password was right and a code was sent: the page then asks for the code.</summary>
    public string? Ticket { get; set; }

    /// <summary>Where the code was sent, shown on the code step.</summary>
    public string SentTo { get; set; } = "";

    /// <summary>No email server configured: the code went to the outbox folder instead.</summary>
    public bool CodeInOutbox { get; set; }

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

        if (!config.GetValue("Admin:LoginCode", true) || devices.IsTrusted(HttpContext, email.Trim()))
            return await SignInAsync(email.Trim());

        var (ticket, code) = codes.Create();
        try
        {
            var result = await mail.SendLoginCodeAsync(email.Trim(), code);
            if (result.Failed.Count > 0)
                throw new EmailException(result.Failed[0].Error);
            CodeInOutbox = result.TestMode;
        }
        catch (EmailException ex)
        {
            logger.LogWarning("Login code not sent: {Error}", ex.Message);
            ModelState.AddModelError(string.Empty, $"Não foi possível enviar o código de acesso: {ex.Message}");
            return Page();
        }

        Ticket = ticket;
        SentTo = email.Trim();
        return Page();
    }

    public async Task<IActionResult> OnPostCodeAsync(string? ticket, string? code, string? returnUrl)
    {
        // Only the code is posted on this step; the password fields' "required" errors don't apply.
        ModelState.Clear();
        ReturnUrl = returnUrl;
        var email = config["Admin:Email"]?.Trim() ?? "";

        switch (codes.Check(ticket, code))
        {
            case CodeCheck.Ok:
                devices.Trust(HttpContext, email);
                return await SignInAsync(email);
            case CodeCheck.Wrong:
                Ticket = ticket;
                SentTo = email;
                ModelState.AddModelError(string.Empty, "Código incorreto. Confira o e-mail mais recente e tente de novo.");
                return Page();
            default:
                ModelState.AddModelError(string.Empty, "O código expirou ou houve tentativas demais. Entre de novo para receber um novo código.");
                return Page();
        }
    }

    private async Task<IActionResult> SignInAsync(string email)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, email)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : RedirectToPage("/Admin");
    }
}
