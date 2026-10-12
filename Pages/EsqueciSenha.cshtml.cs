using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

/// <summary>Emails the admin a link for creating or resetting the password (see <see cref="PasswordResets"/>).</summary>
public class EsqueciSenhaModel(IConfiguration config, AdminPasswords passwords, PasswordResets resets,
    EmailSettingsStore settings, MailService mail, ILogger<EsqueciSenhaModel> logger) : PageModel
{
    public string Email => config["Admin:Email"]?.Trim() ?? "";
    public bool NoPassword => !passwords.IsSet;

    /// <summary>The request went through (or one was already sent moments ago).</summary>
    public bool Sent { get; set; }

    /// <summary>No email server configured: the link went to the outbox folder instead.</summary>
    public bool InOutbox { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Email == "")
        {
            ModelState.AddModelError(string.Empty, "O acesso da equipe ainda não foi configurado neste servidor.");
            return Page();
        }

        // Null when a link went out less than 2 minutes ago: say it was sent either way.
        if (resets.Create(Email) is { } token)
        {
            // The configured site address, not the request's: the link must point to the real site.
            var site = (settings.Get().SiteUrl ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}/").TrimEnd('/');
            var link = $"{site}/RedefinirSenha?token={Uri.EscapeDataString(token)}";
            try
            {
                var result = await mail.SendPasswordLinkAsync(Email, link);
                if (result.Failed.Count > 0)
                    throw new EmailException(result.Failed[0].Error);
                InOutbox = result.TestMode;
            }
            catch (EmailException ex)
            {
                logger.LogWarning("Password link not sent: {Error}", ex.Message);
                ModelState.AddModelError(string.Empty, $"Não foi possível enviar o e-mail: {ex.Message}");
                return Page();
            }
        }
        Sent = true;
        return Page();
    }
}
