using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DonationAutomate.Services;

/// <summary>An email problem that is safe to show to the admin.</summary>
public sealed class EmailException(string message) : Exception(message);

public enum Audience { All, NotDonated, Donated }

/// <param name="Email">The cell as typed, shown in the table — may hold more than one address.</param>
/// <param name="Emails">The valid addresses in <paramref name="Email"/>, split out; what actually gets sent to.</param>
/// <param name="Pending">Names of the children this donor asked for that await approval.</param>
/// <param name="Approved">Names of the children whose request was approved.</param>
public sealed record Donor(string Name, string Email, IReadOnlyList<string> Emails, string Origin, string LastEmail, IReadOnlyList<string> Pending, IReadOnlyList<string> Approved)
{
    public bool HasDonated => Pending.Count + Approved.Count > 0;
    public bool CanReceive => Emails.Count > 0;

    public string Situation => (Pending.Count, Approved.Count) switch
    {
        (0, 0) => "Ainda não doou",
        (_, 0) => "Aguardando aprovação",
        (0, _) => "Doação confirmada",
        _ => "Confirmada + aguardando",
    };

    public string SituationClass => (Pending.Count, Approved.Count) switch
    {
        (0, 0) => "none",
        (_, 0) => "pending",
        _ => "approved",
    };
}

public sealed record CampaignStats(int Total, int Available, int Pending, int Approved)
{
    public int Chosen => Pending + Approved;
    public string PercentAvailable => Percent(Available);
    public string PercentChosen => Percent(Chosen);
    private string Percent(int part) => Total == 0 ? "0%" : $"{Math.Round(part * 100.0 / Total)}%";
}

public sealed record SendResult(int Sent, IReadOnlyList<(string Email, string Error)> Failed, bool TestMode)
{
    public string Summary(string what)
    {
        var sb = new StringBuilder();
        // Sent counts email addresses, not people: someone with two addresses on their row counts as two here.
        var pessoas = Sent == 1 ? "1 e-mail" : $"{Sent} e-mails";
        sb.Append(TestMode
            ? $"Modo de teste: {what} gerado para {pessoas} e salvo em data\\outbox. Nada foi enviado de verdade."
            : $"{what} enviado para {pessoas}.");
        if (Failed.Count > 0)
            sb.Append($" {Failed.Count} falha(s): ").Append(string.Join("; ", Failed.Take(5).Select(f => $"{f.Email} ({f.Error})")));
        return sb.ToString();
    }
}

/// <summary>
/// Builds emails from the templates and the spreadsheets, and sends them through the configured
/// SMTP server. With no server configured, emails are written to data/outbox as .eml files
/// (they open in Outlook or any mail program), so everything can be tried out safely.
/// </summary>
public sealed class MailService(SpreadsheetStore sheets, EmailSettingsStore settings, ILogger<MailService> logger)
{
    private sealed record Outgoing(string Name, string Email, string Subject, string Body);

    public static CampaignStats Stats(SheetData children)
    {
        var statuses = children.Rows
            .Where(r => r.Key != "")
            .Select(r => ChildStatus.Of(r.Values[ChildColumns.Status]))
            .ToList();
        return new CampaignStats(
            statuses.Count,
            statuses.Count(s => s == ChildStatus.Available),
            statuses.Count(s => s == ChildStatus.Pending),
            statuses.Count(s => s == ChildStatus.Approved));
    }

    /// <summary>Everyone in the emails sheet, with what they have asked for according to the children sheet.</summary>
    public static IReadOnlyList<Donor> Donors(SheetData emails, SheetData children)
    {
        var byEmail = children.Rows
            .Where(r => r.Key != "")
            .GroupBy(r => r.Values[ChildColumns.DonorEmail].Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key != "")
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        int Column(string name)
        {
            for (var i = EmailColumns.Expected.Count; i < emails.Headers.Count; i++)
                if (TextFold.SameName(emails.Headers[i], name)) return i;
            return -1;
        }
        var originCol = Column(EmailColumns.Origin);
        var lastCol = Column(EmailColumns.LastEmail);

        // One cell can hold several addresses ("a@x.com; b@y.com"), each counted and emailed
        // separately. A row is shown (and skipped for dedup) by its raw cell text, so an exact
        // repeat of the same text isn't listed and emailed twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var donors = new List<Donor>();
        foreach (var row in emails.Rows)
        {
            var raw = row.Values[EmailColumns.Email].Trim();
            if (raw != "" && !seen.Add(raw)) continue;

            var addresses = SpreadsheetStore.SplitEmails(raw)
                .Where(SpreadsheetStore.LooksLikeEmail)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requests = addresses
                .SelectMany(a => byEmail.GetValueOrDefault(a) ?? [])
                .DistinctBy(r => r.Key)
                .ToList();
            List<string> Names(string status) => requests
                .Where(r => ChildStatus.Of(r.Values[ChildColumns.Status]) == status)
                .Select(r => r.Values[ChildColumns.Nome].Trim())
                .ToList();

            donors.Add(new Donor(
                row.Values[EmailColumns.Name].Trim(),
                raw,
                addresses,
                originCol >= 0 ? row.Values[originCol] : "",
                lastCol >= 0 ? row.Values[lastCol] : "",
                Names(ChildStatus.Pending),
                Names(ChildStatus.Approved)));
        }
        return donors;
    }

    public static IReadOnlyList<Donor> Select(IReadOnlyList<Donor> donors, Audience audience) => donors
        .Where(d => d.CanReceive)
        .Where(d => audience switch
        {
            Audience.NotDonated => !d.HasDonated,
            Audience.Donated => d.HasDonated,
            _ => true,
        })
        .ToList();

    /// <summary>The address used for {link}: the configured one, or the one the admin is browsing.</summary>
    public string SiteUrl(HttpRequest request) =>
        settings.Get().SiteUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}/";

    public static Dictionary<string, string> CampaignValues(CampaignStats stats, string name, string link) => new()
    {
        ["nome"] = Greeting(name),
        ["link"] = link,
        ["total"] = stats.Total.ToString(),
        ["disponiveis"] = stats.Available.ToString(),
        ["percentual_disponivel"] = stats.PercentAvailable,
        ["apadrinhadas"] = stats.Chosen.ToString(),
        ["percentual_apadrinhado"] = stats.PercentChosen,
        ["aprovadas"] = stats.Approved.ToString(),
    };

    public static Dictionary<string, string> ApprovalValues(IReadOnlyList<string> child, string link) => new()
    {
        ["nome"] = Greeting(child[ChildColumns.DonorName]),
        ["crianca"] = child[ChildColumns.Nome].Trim(),
        ["id"] = child[ChildColumns.Id].Trim(),
        ["turma"] = child[ChildColumns.Turma].Trim(),
        ["faixa"] = child[ChildColumns.AgeGroup].Trim(),
        ["idade"] = child[ChildColumns.Age].Trim(),
        ["calcado"] = child[ChildColumns.Shoes].Trim(),
        ["roupa"] = child[ChildColumns.Clothes].Trim(),
        ["link"] = link,
    };

    /// <summary>Replaces {placeholders}; unknown ones are left as typed so the mistake is visible.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Regex.Replace(text, @"\{(\w+)\}", m =>
            values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var value) ? value : m.Value);

    private static string Greeting(string name) => string.IsNullOrWhiteSpace(name) ? "amigo(a)" : name.Trim();

    /// <summary>Sends one of the campaign templates to everyone in the chosen audience.</summary>
    public async Task<SendResult> SendCampaignAsync(string templateKey, Audience audience, string link)
    {
        if (!TemplateKeys.Campaign.Contains(templateKey))
            throw new EmailException("Escolha um dos modelos: Convite, Progresso ou Agradecimento final.");

        var children = sheets.ReadChildren();
        var stats = Stats(children);
        var recipients = Select(Donors(sheets.ReadEmails(), children), audience);
        if (recipients.Count == 0)
            throw new EmailException("Ninguém na lista para esse público.");

        var template = settings.Get().Template(templateKey);
        // A donor with more than one address in their cell gets one message per address.
        var messages = recipients.SelectMany(d =>
        {
            var values = CampaignValues(stats, d.Name, link);
            var subject = Fill(template.Subject, values);
            var body = Fill(template.Body, values);
            return d.Emails.Select(email => new Outgoing(d.Name, email, subject, body));
        }).ToList();

        return await SendAndRecordAsync(messages, TemplateKeys.Label(templateKey));
    }

    /// <summary>
    /// A free-text email to one person, written entirely by the admin. <paramref name="email"/>
    /// may hold more than one address (as typed in the donors sheet); each gets its own message.
    /// </summary>
    public Task<SendResult> SendOneAsync(string name, string email, string subject, string body)
    {
        var addresses = SpreadsheetStore.SplitEmails(email ?? "")
            .Where(SpreadsheetStore.LooksLikeEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (addresses.Count == 0)
            throw new EmailException($"\"{email}\" não tem nenhum e-mail válido.");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            throw new EmailException("Preencha o assunto e a mensagem.");

        var messages = addresses.Select(a => new Outgoing(name, a, subject.Trim(), body.Trim())).ToList();
        return SendAndRecordAsync(messages, "Mensagem individual");
    }

    /// <summary>
    /// The approval thank-you, sent when a request goes from pending to approved, to every valid
    /// address in the donor's cell. Null if none of them is valid.
    /// </summary>
    public async Task<SendResult?> SendApprovalAsync(StatusChange change, string link)
    {
        var addresses = SpreadsheetStore.SplitEmails(change.Values[ChildColumns.DonorEmail])
            .Where(SpreadsheetStore.LooksLikeEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (addresses.Count == 0) return null;

        var values = ApprovalValues(change.Values, link);
        var template = settings.Get().Template(TemplateKeys.Approval);
        var subject = Fill(template.Subject, values);
        var body = Fill(template.Body, values);
        var name = change.Values[ChildColumns.DonorName].Trim();
        var messages = addresses.Select(a => new Outgoing(name, a, subject, body)).ToList();
        return await SendAndRecordAsync(messages, $"Aprovação: {values["crianca"]}");
    }

    public Task<SendResult> SendTestAsync()
    {
        var s = settings.Get();
        if (!s.Smtp.IsConfigured)
            throw new EmailException("Preencha e salve o servidor e o e-mail do remetente primeiro.");
        return SendAsync([new Outgoing(s.Smtp.FromName ?? "", s.Smtp.FromAddress!,
            "Teste do sistema de doações", "Se você recebeu esta mensagem, o envio de e-mails está funcionando.")]);
    }

    private async Task<SendResult> SendAndRecordAsync(IReadOnlyList<Outgoing> messages, string label)
    {
        var result = await SendAsync(messages);
        var failed = result.Failed.Select(f => f.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var delivered = messages.Select(m => m.Email).Where(e => !failed.Contains(e)).ToList();
        try
        {
            sheets.RecordEmailSent(delivered, result.TestMode ? $"{label} (teste)" : label);
        }
        catch (SpreadsheetException ex)
        {
            // The emails went out; only the note in the sheet is missing. Not worth failing for.
            logger.LogWarning("Emails sent but not recorded in the sheet: {Error}", ex.Message);
        }
        return result;
    }

    private async Task<SendResult> SendAsync(IReadOnlyList<Outgoing> messages)
    {
        var s = settings.Get();
        if (!s.Smtp.IsConfigured)
        {
            Directory.CreateDirectory(settings.OutboxPath);
            foreach (var m in messages)
            {
                var file = Path.Combine(settings.OutboxPath, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{SafeFileName(m.Email)}.eml");
                await Build(s, m).WriteToAsync(file);
            }
            return new SendResult(messages.Count, [], TestMode: true);
        }

        using var client = new SmtpClient { Timeout = 30_000 };
        try
        {
            var security = s.Smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(s.Smtp.Host!, s.Smtp.Port, security);  // IsConfigured checked above
            if (!string.IsNullOrWhiteSpace(s.Smtp.User))
                await client.AuthenticateAsync(s.Smtp.User, settings.Password(s) ?? "");
        }
        catch (MailKit.Security.AuthenticationException)
        {
            throw new EmailException("O servidor de e-mail recusou o usuário ou a senha. No Gmail, use uma \"senha de app\", não a senha normal da conta.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EmailException($"Não foi possível conectar ao servidor de e-mail ({s.Smtp.Host}:{s.Smtp.Port}): {ex.Message}");
        }

        var sent = 0;
        var failed = new List<(string, string)>();
        foreach (var m in messages)
        {
            try
            {
                await client.SendAsync(Build(s, m));
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Email to {Email} failed", m.Email);
                failed.Add((m.Email, ex.Message));
            }
        }
        await client.DisconnectAsync(quit: true);
        return new SendResult(sent, failed, TestMode: false);
    }

    private static MimeMessage Build(EmailSettings s, Outgoing m)
    {
        var message = new MimeMessage();
        var fromAddress = s.Smtp.FromAddress ?? "teste@exemplo.com";
        message.From.Add(new MailboxAddress(s.Smtp.FromName ?? fromAddress, fromAddress));
        message.To.Add(new MailboxAddress(m.Name, m.Email));
        message.Subject = m.Subject;
        message.Body = new BodyBuilder { TextBody = m.Body, HtmlBody = ToHtml(m.Body) }.ToMessageBody();
        return message;
    }

    private static readonly Regex Url = new(@"https?://[^\s<]+");

    /// <summary>The plain-text body as simple HTML: paragraphs, line breaks and clickable links.</summary>
    private static string ToHtml(string text)
    {
        var sb = new StringBuilder("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.5;color:#1d2330\">");
        foreach (var paragraph in Regex.Split(text.Replace("\r\n", "\n").Trim(), @"\n\s*\n"))
        {
            var html = WebUtility.HtmlEncode(paragraph);
            html = Url.Replace(html, u => $"<a href=\"{u.Value}\">{u.Value}</a>");
            sb.Append("<p>").Append(html.Replace("\n", "<br>")).Append("</p>");
        }
        return sb.Append("</div>").ToString();
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
