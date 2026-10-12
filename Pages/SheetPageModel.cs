using DonationAutomate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DonationAutomate.Pages;

/// <summary>What the _SheetCard partial needs to show one spreadsheet.</summary>
/// <param name="Handler">Suffix of this sheet's page handlers: Upload…, Download….</param>
public record SheetCard(string Title, string Handler, string MetaId, SheetData Data, string? Columns, bool ConfirmReplace);

/// <summary>Shared plumbing for pages that manage a spreadsheet: upload and download.</summary>
public abstract class SheetPageModel(SpreadsheetStore store) : PageModel
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    protected SpreadsheetStore Store { get; } = store;

    /// <summary>Query values to keep when redirecting back to the page (e.g. a filter).</summary>
    protected virtual object? RouteValues => null;

    /// <summary>Polled by live.js to notice spreadsheet changes.</summary>
    public JsonResult OnGetVersion() => new(new { version = Store.Version() });

    protected IActionResult Run(Action action, Func<string> success)
    {
        try
        {
            action();
            TempData["Message"] = success();
        }
        catch (SpreadsheetException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage(RouteValues);
    }

    protected IActionResult Download(string path, string fileName) =>
        Store.ReadBytes(path) is { } bytes ? File(bytes, XlsxContentType, fileName) : NotFound();

    protected async Task<IActionResult> UploadAsync(IFormFile? file, Action<Stream> replace, string success)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Escolha um arquivo para enviar.";
        }
        else
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;
            try
            {
                replace(ms);
                TempData["Message"] = success;
            }
            catch (SpreadsheetException ex)
            {
                TempData["Error"] = ex.Message;
            }
        }
        return RedirectToPage(RouteValues);
    }
}
