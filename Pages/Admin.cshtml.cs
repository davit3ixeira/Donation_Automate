using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;

namespace DonationAutomate.Pages;

public class AdminModel(SpreadsheetStore store, MailService mail) : SheetPageModel(store)
{
    public SheetData Children { get; private set; } = SheetData.Empty;
    public IReadOnlyList<SheetRow> FilteredRows { get; private set; } = [];
    public Dictionary<string, int> Counts { get; } = ChildStatus.All.ToDictionary(s => s, _ => 0);

    /// <summary>Optional status filter for the children table (null = all).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Filter { get; set; }

    protected override object RouteValues => new { Filter };

    public SheetCard ChildrenCard => new("Planilha de crianças", "Children", "live-children-meta", Children, Store.ChildrenLink,
        ChildColumns.ExpectedList, ConfirmReplace: true);

    public void OnGet()
    {
        ViewData["SheetVersion"] = Store.Version();
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

        FilteredRows = string.IsNullOrEmpty(Filter)
            ? Children.Rows
            : Children.Rows.Where(r => StatusOf(r) == ChildStatus.Of(Filter)).ToList();
    }

    public static string StatusOf(SheetRow row) => ChildStatus.Of(row.Values[ChildColumns.Status]);

    public int KitsDelivered => Children.Rows.Count(r => StatusOf(r) == ChildStatus.Approved && Children.KitDelivered(r));

    /// <summary>Ticks or unticks "kit entregue" for an approved child. Called from the page's script; answers in JSON.</summary>
    public IActionResult OnPostKit(string id, bool delivered)
    {
        try
        {
            Store.SetKitDelivered(id, delivered);
            return new JsonResult(new
            {
                message = delivered ? $"Kit da criança nº {id} marcado como entregue." : $"Kit da criança nº {id} desmarcado.",
                version = Store.Version(),
            });
        }
        catch (SpreadsheetException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    public Task<IActionResult> OnPostUploadChildrenAsync(IFormFile? file) =>
        UploadAsync(file, Store.ReplaceChildren, "Planilha de crianças enviada.");

    public IActionResult OnPostLinkChildren(string? path) =>
        Run(() => Store.LinkChildren(path),
            () => $"Planilha de crianças vinculada a {Store.ChildrenLink}. Alterações salvas nesse arquivo aparecem aqui automaticamente, e alterações feitas aqui são gravadas nele.");

    public IActionResult OnPostUnlinkChildren() =>
        Run(Store.UnlinkChildren, () => "Planilha de crianças desvinculada. O sistema continua com uma cópia dos dados atuais.");

    public IActionResult OnGetDownloadChildren() => Download(Store.ChildrenPath, "criancas.xlsx");

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
    /// Changes the status and, when a pending request is approved, emails the donor the approval
    /// thank-you. A failed email doesn't undo the change; it comes back as a warning.
    /// </summary>
    private async Task<(string Message, string? Warning)> ChangeStatusAsync(string id, string status)
    {
        var change = Store.SetStatus(id, status);
        var message = $"Status alterado para {ChildStatus.Label(change.Current)}.";
        if (change.Previous != ChildStatus.Pending || change.Current != ChildStatus.Approved)
            return (message, null);

        try
        {
            var sent = await mail.SendApprovalAsync(change, mail.SiteUrl(Request));
            if (sent is null)
                return (message, "O doador não tem nenhum e-mail válido na planilha, então o agradecimento não foi enviado.");
            // The donor's cell may hold more than one address; Summary covers both and any partial failure.
            var summary = sent.Summary("E-mail de agradecimento");
            return sent.Failed.Count > 0 ? (message, summary) : ($"{message} {summary}", null);
        }
        catch (EmailException ex)
        {
            return (message, $"O e-mail de agradecimento não foi enviado: {ex.Message}");
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
}
