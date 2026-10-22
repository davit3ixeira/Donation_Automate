using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

public class IndexModel(SpreadsheetStore store, SiteSettingsStore siteSettings, MailService mail, RequestFlow flow, IWebHostEnvironment env, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>One child as shown on a card, already formatted for reading.</summary>
    public sealed record ChildCard(
        string Id, string Name, string FirstName, string Initials, string Gender, string Turma, string AgeGroup,
        string Age, string Shoes, string Clothes, string Search, string Color)
    {
        /// <summary>The filter chip this child falls under: the turma, or the age group when there's no turma.</summary>
        public string Group => Turma != "" ? Turma : AgeGroup;
    }

    /// <summary>A filter chip: one per turma, labelled with its ages, e.g. "Fundamental 1B · 6 a 8 anos".</summary>
    public sealed record ChildGroup(string Key, string Label, int Count);

    /// <summary>A photo of a past Christmas party, from wwwroot/img.</summary>
    public sealed record YearPhoto(int Year, string Src);

    public bool HasSpreadsheet { get; private set; }
    public IReadOnlyList<ChildCard> Children { get; private set; } = [];
    public IReadOnlyList<ChildGroup> Groups { get; private set; } = [];
    public int Boys => Children.Count(c => c.Gender == ChildGender.Male);
    public int Girls => Children.Count(c => c.Gender == ChildGender.Female);
    public CampaignStats Stats { get; private set; } = new(0, 0, 0, 0);
    public SiteSettings SiteSettings { get; private set; } = new();
    public IReadOnlyList<YearPhoto> YearPhotos { get; private set; } = [];

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

    /// <summary>The email typed a second time, so a typo doesn't send the confirmation link nowhere.</summary>
    [BindProperty]
    [Display(Name = "Confirme seu e-mail")]
    [Required(ErrorMessage = "Digite o e-mail de novo para confirmar.")]
    public string EmailConfirmation { get; set; } = "";

    /// <summary>How the donor will help: the kit (the usual way) or, failing that, a Pix.</summary>
    [BindProperty]
    public string Donation { get; set; } = ChildDonation.Kit;

    /// <summary>Re-open the send dialog when the server rejected what was typed in it.</summary>
    public bool ReopenDialog =>
        new[] { nameof(Name), nameof(Email), nameof(EmailConfirmation), nameof(Donation) }.Any(k => ModelState[k]?.Errors.Count > 0);

    public void OnGet()
    {
        ThanksName = TempData["ThanksName"] as string;
        ThanksCount = int.TryParse(TempData["ThanksCount"] as string, out var n) ? n : 0;
        Load();
    }

    /// <summary>Polled by live.js to notice spreadsheet changes.</summary>
    public JsonResult OnGetVersion() => new(new { version = store.Version() });

    public async Task<IActionResult> OnPostAsync()
    {
        if (Selected.Count == 0)
            ModelState.AddModelError(nameof(Selected), "Escolha pelo menos uma criança.");
        // Blank fields arrive as null; [Required] reports those.
        if (!string.IsNullOrWhiteSpace(EmailConfirmation)
            && !SameEmail(Email, EmailConfirmation))
            ModelState.AddModelError(nameof(EmailConfirmation), "Os dois e-mails não são iguais. Confira se não há erro de digitação.");
        if (ChildDonation.Of(Donation) is not { Length: > 0 })
            ModelState.AddModelError(nameof(Donation), "Escolha entre kit e Pix.");

        if (!ModelState.IsValid)
        {
            Load();
            return Page();
        }

        try
        {
            var result = store.RequestChildren(Selected, Name.Trim(), Email.Trim(), Donation);

            if (result.Requested > 0)
            {
                TempData["ThanksName"] = FirstWord(Pretty(Name));
                TempData["ThanksCount"] = result.Requested.ToString();
                AddToDonorList(Name.Trim(), Email.Trim());
                await SendConfirmationAsync(result.Children[0].Values[ChildColumns.Id].Trim());
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

    /// <summary>The same address typed twice, character for character (case and spaces count).</summary>
    private static bool SameEmail(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// "Confirme sua escolha", with the link, to the donor of this child (covering all their
    /// pending children), and a copy to the team. The request is saved by now, and if the email
    /// fails the admin page offers to send it again, so a failure here is only logged.
    /// </summary>
    private async Task SendConfirmationAsync(string childId)
    {
        try
        {
            var result = await flow.SendConfirmationAsync(childId, mail.SiteUrlWithoutKey(Request));
            if (result.Warning is not null) logger.LogWarning("{Warning}", result.Warning);
        }
        catch (Exception ex) when (ex is EmailException or SpreadsheetException)
        {
            logger.LogWarning("Confirmation email not sent for child {Id}: {Error}", childId, ex.Message);
        }
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
        SiteSettings = siteSettings.Get();
        YearPhotos = LoadYearPhotos();
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
        Groups = Children
            .Where(c => c.Group != "")
            .GroupBy(c => c.Group)
            .Select(g => new ChildGroup(g.Key, GroupLabel(g.Key, g.ToList()), g.Count()))
            .ToList();
    }

    private static readonly Regex Number = new(@"\d+");

    /// <summary>
    /// "Fundamental 1B · 6 a 8 anos". When the turma mixes age groups (like the staff's children),
    /// the ages run from the youngest to the oldest: "Filhos de funcionários · 2 a 10 anos".
    /// </summary>
    private static string GroupLabel(string key, IReadOnlyList<ChildCard> children)
    {
        if (children[0].Turma == "") return key;
        var groups = children.Select(c => c.AgeGroup).Where(s => s != "").Distinct().ToList();
        var ages = groups.Count > 1
            ? groups.SelectMany(s => Number.Matches(s)).Select(m => int.Parse(m.Value)).ToList()
            : [];
        var range = groups.Count switch
        {
            0 => "",
            1 => groups[0],
            _ when ages.Count == 0 => string.Join(", ", groups),
            _ when ages.Min() == ages.Max() => ages.Min() == 1 ? "1 ano" : $"{ages.Min()} anos",
            _ => $"{ages.Min()} a {ages.Max()} anos",
        };
        return range == "" ? key : $"{key} · {range}";
    }

    private static readonly Regex YearInName = new(@"(?<!\d)(?:19|20)\d{2}(?!\d)");
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>Every image in wwwroot/img with a year in its name (e.g. "ccmi 2019.jpg"), oldest first.</summary>
    private IReadOnlyList<YearPhoto> LoadYearPhotos()
    {
        var dir = Path.Combine(env.WebRootPath, "img");
        if (!Directory.Exists(dir)) return [];

        return Directory.EnumerateFiles(dir)
            .Select(path => Path.GetFileName(path))
            .Where(name => PhotoExtensions.Contains(Path.GetExtension(name)))
            .Select(name => (Name: name, Year: YearInName.Match(Path.GetFileNameWithoutExtension(name))))
            .Where(x => x.Year.Success)
            .OrderBy(x => x.Year.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new YearPhoto(int.Parse(x.Year.Value), Url.Content($"~/img/{Uri.EscapeDataString(x.Name)}")))
            .ToList();
    }

    private static readonly string[] AvatarColors =["#b8322a", "#1f5a3d", "#9a6e1c", "#2e7d5b", "#1f4f8a", "#a4462c"];

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
            row.Key, name, FirstWord(name), initials.ToUpperInvariant(), ChildGender.Of(Value(ChildColumns.Gender)) ?? "",
            Pretty(Value(ChildColumns.Turma)), Value(ChildColumns.AgeGroup), Value(ChildColumns.Age),
            Value(ChildColumns.Shoes), Value(ChildColumns.Clothes),
            TextFold.Fold($"{name} {row.Key}"), color);
    }

    private static readonly HashSet<string> Particles = ["da", "de", "do", "das", "dos", "e"];
    private static readonly Regex Roman = new(@"^(i{1,3}|iv|vi{0,3}|ix|x)$", RegexOptions.IgnoreCase);
    private static readonly CultureInfo PtBr = new("pt-BR");

    public static string FormatDate(DateOnly date) => date.ToString("d 'de' MMMM 'de' yyyy", PtBr);

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
