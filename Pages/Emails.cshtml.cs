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
        }
        catch (SpreadsheetException ex)
        {
            ViewData["Error"] = ex.Message;
        }
    }

    /// <summary>A template filled in with real numbers and a sample donor, as recipients will see it.</summary>
    public EmailTemplate Preview(string key)
    {
        var template = Settings.Template(key);
        var name = Donors.FirstOrDefault(d => d.Name != "")?.Name ?? "Maria";
        var values = key == TemplateKeys.Approval
            ? new Dictionary<string, string>
            {
                ["nome"] = name, ["crianca"] = "Ana Souza", ["id"] = "12", ["turma"] = "Maternal I",
                ["faixa"] = "2-3 anos", ["idade"] = "3 anos", ["calcado"] = "25", ["roupa"] = "4", ["link"] = Link,
            }
            : MailService.CampaignValues(Stats, name, Link);
        return new EmailTemplate(MailService.Fill(template.Subject, values), MailService.Fill(template.Body, values));
    }

    public Task<IActionResult> OnPostUploadEmailsAsync(IFormFile? file) =>
        UploadAsync(file, Store.ReplaceEmails, "Planilha de doadores enviada.");

    public IActionResult OnPostRemoveEmails() =>
        Run(Store.RemoveEmails, () => "Planilha de doadores removida. Envie outra quando quiser.");

    public IActionResult OnGetDownloadEmails() => Download(Store.EmailsPath, "doadores.xlsx");

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
