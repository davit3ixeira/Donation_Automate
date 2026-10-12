namespace DonationAutomate.Services;

/// <summary>What happened, for the admin or the donor: a message, and a warning when an email failed.</summary>
public sealed record FlowResult(string Message, string? Warning = null, int Approved = 0);

/// <summary>
/// A donor's request after they choose on the site (which emails "Pedido recebido" and leaves the
/// children pending): the admin sends the confirmation link, then the donor confirms through it (or
/// the admin confirms for them). Confirming approves the children and emails the delivery
/// instructions; when the donor confirmed, the team is told too. Always per donor: one email and
/// one link cover all of their pending children.
/// </summary>
public sealed class RequestFlow(SpreadsheetStore sheets, MailService mail, DonorConfirmations confirmations, ILogger<RequestFlow> logger)
{
    /// <param name="childId">Any of the donor's pending children.</param>
    /// <param name="siteUrl">The site's address (ending in "/"), for the link.</param>
    public async Task<FlowResult> SendConfirmationAsync(string childId, string siteUrl)
    {
        var donor = sheets.PendingOfDonor(childId)
            ?? throw new SpreadsheetException("Esta criança não está mais pendente. Recarregue a página.");
        var token = confirmations.Create(donor.Email, donor.Ids);
        var link = $"{siteUrl.TrimEnd('/')}/Confirmar?token={Uri.EscapeDataString(token)}";

        var sent = await mail.SendConfirmationRequestAsync(donor, link)
            ?? throw new EmailException("O padrinho não tem nenhum e-mail válido na planilha.");
        if (sent.Sent == 0)
            throw new EmailException(sent.Summary("E-mail de confirmação"));

        sheets.MarkConfirmationSent(donor.Ids);
        var who = donor.Name != "" ? donor.Name : donor.Email;
        var message = $"E-mail de confirmação enviado para {who} ({Count(donor.Children.Count)}).";
        return sent.Failed.Count > 0 ? new(message, sent.Summary("E-mail de confirmação")) : new(message);
    }

    /// <summary>The admin confirms for the donor of this child: all of the donor's pending children.</summary>
    public async Task<FlowResult> ConfirmByAdminAsync(string childId)
    {
        var donor = sheets.PendingOfDonor(childId)
            ?? throw new SpreadsheetException("Esta criança não está mais pendente. Recarregue a página.");
        return await ConfirmAsync(donor.Email, donor.Ids, byDonor: false);
    }

    /// <summary>The donor confirmed through the emailed link.</summary>
    public Task<FlowResult> ConfirmByDonorAsync(string email, IReadOnlyList<string> ids) => ConfirmAsync(email, ids, byDonor: true);

    private async Task<FlowResult> ConfirmAsync(string email, IReadOnlyList<string> ids, bool byDonor)
    {
        var approved = sheets.ApproveForDonor(email, ids);
        if (approved.Children.Count == 0)
            return new("Nada a confirmar: estas crianças não estão mais pendentes.");

        var who = approved.Name != "" ? approved.Name : approved.Email;
        var message = $"{who}: {Count(approved.Children.Count)} aprovada{(approved.Children.Count == 1 ? "" : "s")}.";
        var warnings = new List<string>();

        try
        {
            var sent = await mail.SendApprovalAsync(approved);
            if (sent is null) warnings.Add("O padrinho não tem nenhum e-mail válido, então as instruções de entrega não foram enviadas.");
            else if (sent.Failed.Count > 0) warnings.Add(sent.Summary("Instruções de entrega"));
            else message += " Instruções de entrega enviadas.";
        }
        catch (EmailException ex)
        {
            warnings.Add($"As instruções de entrega não foram enviadas: {ex.Message}");
        }

        if (byDonor)
        {
            try
            {
                await mail.SendDonorConfirmedNoticeAsync(approved);
            }
            catch (EmailException ex)
            {
                // The donor's part worked; the team still sees the approval in the admin.
                logger.LogWarning("Confirmation notice not sent to the team: {Error}", ex.Message);
            }
        }

        foreach (var warning in warnings) logger.LogWarning("{Warning}", warning);
        return new(message, warnings.Count > 0 ? string.Join(" ", warnings) : null, approved.Children.Count);
    }

    private static string Count(int n) => n == 1 ? "1 criança" : $"{n} crianças";
}
