using ClosedXML.Excel;
using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;

namespace DonationAutomate.Pages;

public class EmailsModel(SpreadsheetStore store, MailService mail, EmailSettingsStore settings) : SheetPageModel(store)
{
    public SheetData Emails { get; private set; } = SheetData.Empty;
    public IReadOnlyList<Donor> Donors { get; private set; } = [];
    public CampaignStats Stats { get; private set; } = new(0, 0, 0, 0);
    public EmailSettings Settings { get; private set; } = new();

    /// <summary>The address {link} turns into.</summary>
    public string Link { get; private set; } = "";

    public int AudienceCount(Audience audience) => MailService.Select(Donors, audience).Count;

    // Optional filters for the donor table (null = all), combined. Sending to everyone ignores them.

    /// <summary>Situation: "none" (ainda não doou), "pending" (aguardando) or "approved" (confirmada), as in <see cref="Donor.SituationClass"/>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Situacao { get; set; }

    /// <summary>Origin: "planilha" or "site".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Origem { get; set; }

    public IReadOnlyList<Donor> FilteredDonors { get; private set; } = [];
    public bool IsFiltered => !string.IsNullOrEmpty(Situacao) || !string.IsNullOrEmpty(Origem);

    public static readonly IReadOnlyList<(string Value, string Label)> Situations =
        [("none", "Ainda não doou"), ("pending", "Aguardando"), ("approved", "Confirmada")];
    public static readonly IReadOnlyList<(string Value, string Label)> Origins = [("planilha", "Planilha"), ("site", "Site")];

    public static string OriginOf(Donor d) => d.Origin == "" ? "Planilha" : d.Origin;

    /// <summary>The current filters, for links and forms that should keep them.</summary>
    public Dictionary<string, string> FilterRoute(string? situacao = null, string? origem = null) => new()
    {
        ["situacao"] = situacao ?? Situacao ?? "",
        ["origem"] = origem ?? Origem ?? "",
    };

    private List<Donor> ApplyFilters(IEnumerable<Donor> donors) => donors
        .Where(d => string.IsNullOrEmpty(Situacao) || d.SituationClass == Situacao)
        .Where(d => string.IsNullOrEmpty(Origem) || TextFold.Fold(OriginOf(d)) == Origem)
        .ToList();

    public SheetCard EmailsCard => new("Planilha de doadores", "Emails", "live-emails-meta", Emails,
        $"{EmailColumns.ExpectedList} (o sistema acrescenta \"{EmailColumns.Origin}\" e \"{EmailColumns.LastEmail}\")", ConfirmReplace: true);

    public void OnGet()
    {
        ViewData["SheetVersion"] = Store.Version();
        Settings = settings.Get();
        Link = mail.SiteUrl(Request);
        try
        {
            Emails = Store.ReadEmails();
            var children = Store.ReadChildren();
            Stats = MailService.Stats(children);
            Donors = MailService.Donors(Emails, children);
            FilteredDonors = ApplyFilters(Donors);
        }
        catch (SpreadsheetException ex)
        {
            ViewData["Error"] = ex.Message;
        }
    }

    /// <summary>A campaign template filled in with real numbers and a sample donor, as recipients will see it.</summary>
    public EmailTemplate Preview(string key)
    {
        var template = Settings.Template(key);
        var name = Donors.FirstOrDefault(d => d.Name != "")?.Name ?? "Maria";
        var values = MailService.CampaignValues(Stats, name, Link);
        return new EmailTemplate(MailService.Fill(template.Subject, values), MailService.Fill(template.Body, values));
    }

    public Task<IActionResult> OnPostUploadEmailsAsync(IFormFile? file) =>
        UploadAsync(file, Store.ReplaceEmails, "Planilha de doadores enviada.");

    public IActionResult OnPostRemoveEmails() =>
        Run(Store.RemoveEmails, () => "Planilha de doadores removida. Envie outra quando quiser.");

    public IActionResult OnGetDownloadEmails() => Download(Store.EmailsPath, "doadores.xlsx");

    /// <summary>Deletes a donor's row from the donors sheet; back to the list, where the button was.</summary>
    public IActionResult OnPostDeleteDonor(int row, string email, string? name)
    {
        try
        {
            Store.DeleteDonor(row, email ?? "");
            TempData["Message"] = $"{(string.IsNullOrWhiteSpace(name) ? email : name)} excluído(a) da lista de doadores.";
        }
        catch (SpreadsheetException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(null, null, new { Situacao, Origem }, "doadores");
    }

    /// <summary>The donor table as shown on the page (filters applied, with the computed columns), as a spreadsheet.</summary>
    public IActionResult OnGetExportDonors()
    {
        try
        {
            var donors = ApplyFilters(MailService.Donors(Store.ReadEmails(), Store.ReadChildren()));
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Doadores");
            string[] headers =
                ["Nome", "E-mail", "Situação", "Aguardando", "Crianças aguardando", "Aprovadas", "Crianças aprovadas", "Origem", "Último e-mail"];
            double[] widths = [28, 34, 22, 12, 40, 12, 40, 12, 40];
            for (var c = 0; c < headers.Length; c++)
            {
                ws.Cell(1, c + 1).Value = headers[c];
                ws.Column(c + 1).Width = widths[c];
            }
            ws.Row(1).Style.Font.Bold = true;

            var r = 2;
            foreach (var d in donors)
            {
                ws.Cell(r, 1).Value = d.Name;
                ws.Cell(r, 2).Value = d.Email;
                ws.Cell(r, 3).Value = d.Situation;
                ws.Cell(r, 4).Value = d.Pending.Count;
                ws.Cell(r, 5).Value = string.Join(", ", d.Pending);
                ws.Cell(r, 6).Value = d.Approved.Count;
                ws.Cell(r, 7).Value = string.Join(", ", d.Approved);
                ws.Cell(r, 8).Value = OriginOf(d);
                ws.Cell(r, 9).Value = d.LastEmail;
                r++;
            }
            ws.SheetView.FreezeRows(1);

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var situation = Situations.FirstOrDefault(s => s.Value == Situacao).Label;
            var name = string.Join("-", new[] { situation is null ? null : TextFold.Fold(situation).Replace(' ', '-'), Origem }
                .Where(s => !string.IsNullOrEmpty(s)));
            return File(ms.ToArray(), XlsxContentType, name == "" ? "doadores.xlsx" : $"doadores-{name}.xlsx");
        }
        catch (SpreadsheetException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(null, null, new { Situacao, Origem }, "doadores");
        }
    }

    public Task<IActionResult> OnPostSendAllAsync(string template, Audience audience) =>
        SendAsync(() => mail.SendCampaignAsync(template, audience, mail.SiteUrl(Request)), $"E-mail \"{TemplateKeys.Label(template)}\"");

    public Task<IActionResult> OnPostSendOneAsync(string name, string email, string subject, string body) =>
        SendAsync(() => mail.SendOneAsync(name, email, subject, body), "Mensagem");

    private async Task<IActionResult> SendAsync(Func<Task<SendResult>> send, string what)
    {
        try
        {
            var result = await send();
            // Any failure shows as an error, even if the other emails went out.
            TempData[result.Failed.Count > 0 ? "Error" : "Message"] = result.Summary(what);
        }
        catch (Exception ex) when (ex is EmailException or SpreadsheetException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public IActionResult OnPostTemplates(Dictionary<string, string> subjects, Dictionary<string, string> bodies)
    {
        var templates = new Dictionary<string, EmailTemplate>();
        foreach (var key in TemplateKeys.All)
        {
            var subject = subjects.GetValueOrDefault(key)?.Trim() ?? "";
            var body = bodies.GetValueOrDefault(key)?.Trim() ?? "";
            if (subject == "" || body == "")
            {
                TempData["Error"] = $"O modelo \"{TemplateKeys.Label(key)}\" precisa de assunto e mensagem. Nada foi salvo.";
                return RedirectToPage(null, null, null, "modelos");
            }
            templates[key] = new EmailTemplate(subject, body.Replace("\r\n", "\n"));
        }
        settings.SaveTemplates(templates);
        TempData["Message"] = "Modelos de e-mail salvos.";
        return RedirectToPage(null, null, null, "modelos");
    }

    public IActionResult OnPostResetTemplate(string key)
    {
        if (TemplateKeys.Defaults.TryGetValue(key, out var template))
        {
            settings.SaveTemplates(new Dictionary<string, EmailTemplate> { [key] = template });
            TempData["Message"] = $"Modelo \"{TemplateKeys.Label(key)}\" voltou ao texto padrão.";
        }
        return RedirectToPage(null, null, null, "modelos");
    }

}
