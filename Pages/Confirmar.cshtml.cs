using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

/// <summary>
/// Where the link in "Confirme sua escolha" leads. Opening it only shows the children and a button:
/// confirming takes the click (a POST), because corporate email filters open links by themselves
/// to check them, and that must not confirm anything.
/// </summary>
public class ConfirmarModel(DonorConfirmations confirmations, RequestFlow flow, SpreadsheetStore sheets) : PageModel
{
    public enum State { Invalid, Pending, AlreadyConfirmed, Unavailable, Done }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public State Status { get; private set; }
    public string DonorName { get; private set; } = "";
    public IReadOnlyList<string> ChildNames { get; private set; } = [];

    /// <summary>Shown after confirming when the instructions email failed.</summary>
    public string? Warning { get; private set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync()
    {
        if (confirmations.Read(Token) is not { } request)
        {
            Status = State.Invalid;
            return Page();
        }

        var result = await flow.ConfirmByDonorAsync(request.Email, request.Ids);
        Load();
        if (result.Approved > 0 && Status == State.AlreadyConfirmed)
        {
            Status = State.Done;
            Warning = result.Warning is null ? null : "Sua confirmação foi registrada, mas não conseguimos enviar o e-mail com as instruções. A equipe vai entrar em contato.";
        }
        return Page();
    }

    private void Load()
    {
        if (confirmations.Read(Token) is not { } request)
        {
            Status = State.Invalid;
            return;
        }

        var sheet = sheets.ReadChildren();
        var mine = sheet.Rows
            .Where(r => request.Ids.Contains(r.Key))
            .Where(r => string.Equals(r.Values[ChildColumns.DonorEmail].Trim(), request.Email, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DonorName = mine.Select(r => r.Values[ChildColumns.DonorName].Trim()).FirstOrDefault(n => n != "") ?? "";
        ChildNames = mine.Select(r => r.Values[ChildColumns.Nome].Trim()).ToList();

        var statuses = mine.Select(r => ChildStatus.Of(r.Values[ChildColumns.Status])).ToList();
        Status = statuses.Count == 0 ? State.Unavailable
            : statuses.Contains(ChildStatus.Pending) ? State.Pending
            : State.AlreadyConfirmed;
    }
}
