using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

public class LoginModel : PageModel
{
    private const string FixedUser = "AdmNatalSantaMarta";
    private const string FixedPassword = "FelizNatal";

    [BindProperty]
    [Display(Name = "Login")]
    [Required(ErrorMessage = "Informe o login.")]
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

        if (Usuario != FixedUser || Senha != FixedPassword)
        {
            ModelState.AddModelError(string.Empty, "Login ou senha incorretos.");
            return Page();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Usuario)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Admin");
    }
}
