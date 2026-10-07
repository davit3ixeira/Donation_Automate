using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

public class IndexModel(SpreadsheetStore store, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>One child as shown on a card, already formatted for reading.</summary>
    public sealed record ChildCard(
        string Id, string Name, string FirstName, string Initials, string Turma, string AgeGroup,
        string Age, string Shoes, string Clothes, string Search, string Color);

    public bool HasSpreadsheet { get; private set; }
    public IReadOnlyList<ChildCard> Children { get; private set; } = [];
    public IReadOnlyList<(string Turma, int Count)> Turmas { get; private set; } = [];
    public CampaignStats Stats { get; private set; } = new(0, 0, 0, 0);

    /// <summary>Set right after a successful request, to show the thank-you.</summary>
    public string? ThanksName { get; private set; }
    public int ThanksCount { get; private set; }

    [BindProperty]
    public List<string> Selected { get; set; } = [];

    [BindProperty]
    [Display(Name = "Seu nome")]
    [Required(ErrorMessage = "Informe seu nome.")]
    [StringLength(120, ErrorMessage = "Nome muito longo.")]
    public string Name { get; set; } = "";

    [BindProperty]
    [Display(Name = "Seu e-mail")]
    [Required(ErrorMessage = "Informe seu e-mail.")]
    [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
    [StringLength(254, ErrorMessage = "E-mail muito longo.")]
    public string Email { get; set; } = "";

    [BindProperty]
    [Display(Name = "Telefone / WhatsApp")]
    [Phone(ErrorMessage = "Informe um telefone válido.")]
    [StringLength(30, ErrorMessage = "Telefone muito longo.")]
    public string? Phone { get; set; }

    /// <summary>Re-open the send dialog when the server rejected what was typed in it.</summary>
    public bool ReopenDialog =>
        new[] { nameof(Name), nameof(Email), nameof(Phone) }.Any(k => ModelState[k]?.Errors.Count > 0);

    public void OnGet()
    {
        ThanksName = TempData["ThanksName"] as string;
        ThanksCount = int.TryParse(TempData["ThanksCount"] as string, out var n) ? n : 0;
        Load();
    }

    /// <summary>Polled by live.js to notice spreadsheet changes.</summary>
    public JsonResult OnGetVersion() => new(new { version = store.Version() });

    public IActionResult OnPost()
    {
        if (Selected.Count == 0)
            ModelState.AddModelError(nameof(Selected), "Escolha pelo menos uma criança.");

        if (!ModelState.IsValid)
        {
            Load();
            return Page();
        }

        try
        {
            var phone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim();
            var result = store.RequestChildren(Selected, Name.Trim(), Email.Trim(), phone);

            if (result.Requested > 0)
            {
                TempData["ThanksName"] = FirstWord(Pretty(Name));
                TempData["ThanksCount"] = result.Requested.ToString();
                AddToDonorList(Name.Trim(), Email.Trim());
            }
            if (result.Unavailable > 0)
                TempData["Error"] = result.Unavailable == 1
                    ? "Uma das crianças que você escolheu acabou de ser escolhida por outra pessoa, então ela não entrou no seu pedido."
                    : $"{result.Unavailable} das crianças que você escolheu acabaram de ser escolhidas por outras pessoas, então não entraram no seu pedido.";
        }
        catch (SpreadsheetException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(null, null, null, "obrigado");
    }

    /// <summary>
    /// New donors join the emails sheet so the campaign emails reach them too. The request has
    /// already been saved by now, so a failure here is only logged.
    /// </summary>
    private void AddToDonorList(string name, string email)
    {
        try
        {
            if (store.EnsureDonor(name, email))
                logger.LogInformation("Added {Email} to the donor list", email);
        }
        catch (SpreadsheetException ex)
        {
            logger.LogWarning("Could not add {Email} to the donor list: {Error}", email, ex.Message);
        }
    }

    private void Load()
    {
        ViewData["SheetVersion"] = store.Version();
        SheetData sheet;
        try
        {
            sheet = store.ReadChildren();
        }
        catch (SpreadsheetException ex)
        {
            ViewData["Error"] = ex.Message;
            return;
        }

        HasSpreadsheet = !sheet.IsEmpty;
        Stats = MailService.Stats(sheet);
        Children = sheet.Rows
            .Where(r => r.Key != "" && ChildStatus.Of(r.Values[ChildColumns.Status]) == ChildStatus.Available)
            .Select(ToCard)
            .ToList();
        Turmas = Children
            .Where(c => c.Turma != "")
            .GroupBy(c => c.Turma)
            .Select(g => (g.Key, g.Count()))
            .ToList();
    }

    private static readonly string[] AvatarColors = ["#b8322a", "#1f5a3d", "#9a6e1c", "#2e7d5b", "#1f4f8a", "#a4462c"];

    private static ChildCard ToCard(SheetRow row)
    {
        string Value(int column) => row.Values[column].Trim();
        var name = Pretty(Value(ChildColumns.Nome));
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Particles.Contains(w.ToLowerInvariant())).ToList();
        var initials = words.Count switch
        {
            0 => "?",
            1 => words[0][..1],
            _ => $"{words[0][0]}{words[^1][0]}",
        };
        var color = AvatarColors[(int)((uint)row.Key.GetHashCode(StringComparison.Ordinal) % AvatarColors.Length)];

        return new ChildCard(
            row.Key, name, FirstWord(name), initials.ToUpperInvariant(),
            Pretty(Value(ChildColumns.Turma)), Value(ChildColumns.AgeGroup), Value(ChildColumns.Age),
            Value(ChildColumns.Shoes), Value(ChildColumns.Clothes),
            TextFold.Fold($"{name} {row.Key}"), color);
    }

    private static readonly HashSet<string> Particles = ["da", "de", "do", "das", "dos", "e"];
    private static readonly Regex Roman = new(@"^(i{1,3}|iv|vi{0,3}|ix|x)$", RegexOptions.IgnoreCase);
    private static readonly CultureInfo PtBr = new("pt-BR");

    /// <summary>
    /// "ANNA JASMIN DO N. SANTOS" → "Anna Jasmin do N. Santos", "BERÇÁRIO II" → "Berçário II".
    /// Only text typed all in capitals is touched; anything else is shown as typed.
    /// </summary>
    public static string Pretty(string raw)
    {
        raw = Regex.Replace(raw.Trim(), @"\s+", " ");
        if (raw == "" || raw != raw.ToUpper(PtBr)) return raw;

        var words = raw.Split(' ').Select((word, i) =>
        {
            var lower = word.ToLower(PtBr);
            if (i > 0 && Particles.Contains(lower)) return lower;
            if (Roman.IsMatch(word)) return word;
            return string.Join('-', lower.Split('-').Select(part =>
                part.Length == 0 ? part : char.ToUpper(part[0], PtBr) + part[1..]));
        });
        return string.Join(' ', words);
    }

    private static string FirstWord(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
}
