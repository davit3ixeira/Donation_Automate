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

    /// <summary>Everything about a child the donor should check before confirming.</summary>
    public sealed record ChildDetail(string Id, string Name, string Gender, string Turma, string AgeGroup, string Age,
        string Shoes, string Clothes, string Donation);

    public State Status { get; private set; }
    public string DonorName { get; private set; } = "";
    public IReadOnlyList<ChildDetail> Children { get; private set; } = [];

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
        Children = mine.Select(r =>
        {
            string Value(int column) => r.Values[column].Trim();
            return new ChildDetail(r.Key, Value(ChildColumns.Nome), ChildGender.Label(ChildGender.Of(Value(ChildColumns.Gender)) ?? ""),
                Value(ChildColumns.Turma), Value(ChildColumns.AgeGroup),
                Value(ChildColumns.Age) switch { "1" => "1 ano", var a when a != "" && a.All(char.IsDigit) => $"{a} anos", var a => a },
                Value(ChildColumns.Shoes), Value(ChildColumns.Clothes), sheet.Donation(r));
        }).ToList();

        var statuses = mine.Select(r => ChildStatus.Of(r.Values[ChildColumns.Status])).ToList();
        Status = statuses.Count == 0 ? State.Unavailable
            : statuses.Contains(ChildStatus.Pending) ? State.Pending
            : State.AlreadyConfirmed;
    }
}
