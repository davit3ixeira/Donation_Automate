using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;

namespace DonationAutomate.Pages;

public class AdminModel(SpreadsheetStore store, MailService mail, RequestFlow flow, SiteSettingsStore siteSettings) : SheetPageModel(store)
{
    public SheetData Children { get; private set; } = SheetData.Empty;
    public IReadOnlyList<SheetRow> FilteredRows { get; private set; } = [];
    public Dictionary<string, int> Counts { get; } = ChildStatus.All.ToDictionary(s => s, _ => 0);
    public SiteSettings SiteSettings { get; private set; } = new();

    // Optional filters for the children table (null = all), combined.

    /// <summary>Status: "available", "pending" or "approved".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Filter { get; set; }

    /// <summary>Donation: "kit" or "pix".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Doacao { get; set; }

    /// <summary>Done: "sim" or "nao".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Realizado { get; set; }

    protected override object RouteValues => new { Filter, Doacao, Realizado };

    /// <summary>The current filters, for links and forms that should keep them.</summary>
    public Dictionary<string, string> FilterRoute(string? filter = null, string? doacao = null, string? realizado = null) => new()
    {
        ["filter"] = filter ?? Filter ?? "",
        ["doacao"] = doacao ?? Doacao ?? "",
        ["realizado"] = realizado ?? Realizado ?? "",
    };

    public SheetCard ChildrenCard => new("Planilha de crianças", "Children", "live-children-meta", Children,
        ChildColumns.ExpectedList, ConfirmReplace: true);

    public void OnGet()
    {
        ViewData["SheetVersion"] = Store.Version();
        SiteSettings = siteSettings.Get();
        try
        {
            Children = Store.ReadChildren();
        }
        catch (SpreadsheetException ex)
        {
            ViewData["Error"] = ex.Message;
        }

        foreach (var row in Children.Rows)
        {
            var status = StatusOf(row);
            if (Counts.ContainsKey(status)) Counts[status]++;
        }

        FilteredRows = ApplyFilters(Children);
    }

    public bool IsFiltered => !string.IsNullOrEmpty(Filter) || !string.IsNullOrEmpty(Doacao) || !string.IsNullOrEmpty(Realizado);

    private List<SheetRow> ApplyFilters(SheetData sheet)
    {
        var donation = ChildDonation.Of(Doacao);
        return sheet.Rows
            .Where(r => string.IsNullOrEmpty(Filter) || StatusOf(r) == ChildStatus.Of(Filter))
            .Where(r => string.IsNullOrEmpty(donation) || sheet.Donation(r) == donation)
            .Where(r => Realizado switch
            {
                "sim" => sheet.Done(r),
                "nao" => StatusOf(r) == ChildStatus.Approved && !sheet.Done(r),
                _ => true,
            })
            .ToList();
    }

    public static string StatusOf(SheetRow row) => ChildStatus.Of(row.Values[ChildColumns.Status]);

    /// <summary>Whether the row has a donor (pending or approved), so a donation type applies.</summary>
    public static bool HasDonor(SheetRow row) => StatusOf(row) is ChildStatus.Pending or ChildStatus.Approved;

    public int DoneCount => Children.Rows.Count(r => StatusOf(r) == ChildStatus.Approved && Children.Done(r));
    public int DonationCount(string donation) => Children.Rows.Count(r => HasDonor(r) && Children.Donation(r) == donation);

    /// <summary>Ticks or unticks "realizado" for an approved child. Called from the page's script; answers in JSON.</summary>
    public IActionResult OnPostDone(string id, bool done) => Json(() =>
    {
        Store.SetDone(id, done);
        return done ? $"Criança nº {id} marcada como realizada." : $"Criança nº {id} desmarcada.";
    });

    /// <summary>Kit or Pix for a child with a donor. Called from the page's script; answers in JSON.</summary>
    public IActionResult OnPostDonation(string id, string value) => Json(() =>
    {
        Store.SetDonation(id, value);
        return $"Doação da criança nº {id}: {ChildDonation.Of(value)}.";
    });

    private IActionResult Json(Func<string> change)
    {
        try
        {
            var message = change();
            return new JsonResult(new { message, version = Store.Version() });
        }
        catch (SpreadsheetException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    /// <summary>Emails the donor of this pending child the confirmation link (covering all their pending children).</summary>
    public Task<IActionResult> OnPostSendConfirmationAsync(string id) =>
        FlowAsync(() => flow.SendConfirmationAsync(id, mail.SiteUrlWithoutKey(Request)));

    /// <summary>Confirms for the donor of this pending child, as if they had used the link (without telling the team).</summary>
    public Task<IActionResult> OnPostConfirmAsync(string id) => FlowAsync(() => flow.ConfirmByAdminAsync(id));

    private async Task<IActionResult> FlowAsync(Func<Task<FlowResult>> step)
    {
        try
        {
            var result = await step();
            TempData["Message"] = result.Message;
            if (result.Warning is not null) TempData["Error"] = result.Warning;
        }
        catch (Exception ex) when (ex is SpreadsheetException or EmailException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(RouteValues);
    }

    public Task<IActionResult> OnPostUploadChildrenAsync(IFormFile? file) =>
        UploadAsync(file, Store.ReplaceChildren, "Planilha de crianças enviada.");

    public IActionResult OnPostRemoveChildren() =>
        Run(Store.RemoveChildren, () => "Planilha de crianças removida. Envie outra quando quiser.");

    /// <summary>The children spreadsheet; with filters in the address, only the rows they let through.</summary>
    public IActionResult OnGetDownloadChildren()
    {
        if (!IsFiltered) return Download(Store.ChildrenPath, "criancas.xlsx");
        try
        {
            var rows = ApplyFilters(Store.ReadChildren()).Select(r => r.RowNumber).ToHashSet();
            var status = string.IsNullOrEmpty(Filter) ? null : TextFold.Fold(ChildStatus.Of(Filter));
            var name = string.Join("-", new[] { status, Doacao, Realizado is { Length: > 0 } r ? $"realizado-{r}" : null }
                .Where(s => !string.IsNullOrEmpty(s)));
            return File(Store.ExportChildren(rows), XlsxContentType, $"criancas-{name}.xlsx");
        }
        catch (SpreadsheetException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(RouteValues);
        }
    }

    /// <summary>Status change from the no-script form.</summary>
    public async Task<IActionResult> OnPostStatusAsync(string id, string status)
    {
        try
        {
            var (message, warning) = await ChangeStatusAsync(id, status);
            TempData["Message"] = message;
            if (warning is not null) TempData["Error"] = warning;
        }
        catch (SpreadsheetException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(RouteValues);
    }

    /// <summary>Saves one edited cell of the grid. Called from the page's script; answers in JSON.</summary>
    public async Task<IActionResult> OnPostCellAsync(string id, int column, string? value)
    {
        try
        {
            if (column == ChildColumns.Status)
            {
                var (message, warning) = await ChangeStatusAsync(id, value ?? "");
                return new JsonResult(new { value = ChildStatus.Label(ChildStatus.Of(value)), message, warning, version = Store.Version() });
            }

            var shown = Store.UpdateCell(id, column, value ?? "");
            return new JsonResult(new { value = shown, version = Store.Version() });
        }
        catch (SpreadsheetException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    /// <summary>
    /// Changes the status from the dropdown. Approving a pending child this way confirms it for
    /// that child alone and emails the donor the delivery instructions. A failed email doesn't undo
    /// the change; it comes back as a warning.
    /// </summary>
    private async Task<(string Message, string? Warning)> ChangeStatusAsync(string id, string status)
    {
        var change = Store.SetStatus(id, status);
        var message = $"Status alterado para {ChildStatus.Label(change.Current)}.";
        if (change.Previous != ChildStatus.Pending || change.Current != ChildStatus.Approved)
            return (message, null);

        var sheet = Store.ReadChildren();
        var row = sheet.Rows.FirstOrDefault(r => r.Key == id);
        if (row is null) return (message, null);
        var donor = new DonorChildren(row.Values[ChildColumns.DonorName].Trim(), row.Values[ChildColumns.DonorEmail].Trim(),
            [new RequestedChild(row.Values, sheet.Donation(row))]);
        try
        {
            var sent = await mail.SendApprovalAsync(donor);
            if (sent is null)
                return (message, "O padrinho não tem nenhum e-mail válido na planilha, então as instruções de entrega não foram enviadas.");
            // The donor's cell may hold more than one address; Summary covers both and any partial failure.
            var summary = sent.Summary("Instruções de entrega");
            return sent.Failed.Count > 0 ? (message, summary) : ($"{message} {summary}", null);
        }
        catch (EmailException ex)
        {
            return (message, $"As instruções de entrega não foram enviadas: {ex.Message}");
        }
    }

    public IActionResult OnPostAddRow()
    {
        try
        {
            var id = Store.AddChild();
            TempData["Message"] = $"Criança nº {id} adicionada. Preencha os dados na linha destacada.";
            // Unfiltered, so the new (available) row is visible; the page script focuses it.
            return RedirectToPage(null, null, new { focus = id }, $"row-{id}");
        }
        catch (SpreadsheetException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(RouteValues);
        }
    }

    public IActionResult OnPostDeleteRow(string id) =>
        Run(() => Store.DeleteChild(id), () => $"Criança nº {id} excluída.");

    public IActionResult OnPostSiteSettings(string? kitsDeadline, string? festa, string? pixValue, string? pixKey, string? deliveryAddress)
    {
        siteSettings.Save(kitsDeadline, festa, pixValue, pixKey, deliveryAddress);
        TempData["Message"] = "Configurações da campanha salvas.";
        return RedirectToPage(RouteValues);
    }
}
