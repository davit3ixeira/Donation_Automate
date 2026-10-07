using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace DonationAutomate.Services;

/// <summary>An error that is safe to show to the person who caused it.</summary>
public sealed class SpreadsheetException(string message) : Exception(message);

public static class TextFold
{
    /// <summary>Trimmed, lower-case, accents removed: "Disponível " → "disponivel".</summary>
    public static string Fold(string? value)
    {
        var decomposed = (value ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    public static bool SameName(string a, string b) => Fold(a) == Fold(b);
}

public static class ChildStatus
{
    public const string Available = "disponível";
    public const string Pending = "pendente";
    public const string Approved = "aprovado";

    public static readonly IReadOnlyList<string> All = [Available, Pending, Approved];

    /// <summary>
    /// Canonical status for a raw cell value. Accent and case are ignored, a blank status counts as
    /// available, and the English names are accepted too. Unknown values are returned unchanged.
    /// </summary>
    public static string Of(string? raw) => TextFold.Fold(raw) switch
    {
        "" or "disponivel" or "available" => Available,
        "pendente" or "pending" => Pending,
        "aprovado" or "approved" => Approved,
        _ => (raw ?? "").Trim(),
    };

    /// <summary>Display form, also what gets written to the spreadsheet: "Disponível".</summary>
    public static string Label(string status) =>
        status.Length == 0 ? status : char.ToUpper(status[0]) + status[1..];

    /// <summary>ASCII code for a status, used in CSS classes, form values and URLs.</summary>
    public static string CssClass(string status) => status switch
    {
        Available => "available",
        Pending => "pending",
        Approved => "approved",
        _ => "unknown",
    };
}

/// <summary>
/// The children table's columns are identified by position, not by name: the header text may
/// vary from sheet to sheet, but the order is always the one in <see cref="Expected"/>.
/// </summary>
public static class ChildColumns
{
    public static readonly IReadOnlyList<string> Expected =
    [
        "id", "turma", "faixa etária", "nome da criança", "idade", "calçado", "roupa",
        "nome do doador", "e-mail do doador", "telefone do doador", "data do pedido", "status",
    ];

    // 0-based positions, counted from the table's first column.
    public const int Id = 0;
    public const int Turma = 1;
    public const int AgeGroup = 2;
    public const int Nome = 3;
    public const int Age = 4;
    public const int Shoes = 5;
    public const int Clothes = 6;
    public const int DonorName = 7;
    public const int DonorEmail = 8;
    public const int DonorPhone = 9;
    public const int RequestedAt = 10;
    public const int Status = 11;

    /// <summary>The first columns, up to "roupa", describe the child. Users only ever see these.</summary>
    public const int PublicCount = 7;

    /// <summary>Filled in when someone requests the child; cleared when it becomes available again.</summary>
    public static readonly IReadOnlyList<int> Donor = [DonorName, DonorEmail, DonorPhone, RequestedAt];

    /// <summary>
    /// Appended by the system after the expected columns (found by name) the first time a kit is
    /// marked as delivered. "Sim" means delivered.
    /// </summary>
    public const string KitDelivered = "kit entregue";

    public static string ExpectedList => string.Join(", ", Expected);

    /// <summary>Accepts what people tend to type in Excel for "yes".</summary>
    public static bool IsYes(string? value) => TextFold.Fold(value) is "sim" or "s" or "x" or "ok" or "entregue" or "true" or "1";
}

/// <summary>
/// The donors (emails) table: name and email by position, like the children table, plus two
/// columns the system appends and keeps up to date, found by name.
/// </summary>
public static class EmailColumns
{
    public static readonly IReadOnlyList<string> Expected = ["nome", "email"];

    public const int Name = 0;
    public const int Email = 1;

    public const string Origin = "origem";
    public const string LastEmail = "último e-mail";
    public static readonly IReadOnlyList<string> Added = [Origin, LastEmail];

    public static string ExpectedList => string.Join(", ", Expected);
}

/// <param name="Key">The child's id (or the row number, for the emails sheet).</param>
public sealed record SheetRow(int RowNumber, string Key, IReadOnlyList<string> Values);

public sealed record SheetData(IReadOnlyList<string> Headers, IReadOnlyList<SheetRow> Rows, DateTime? LastModified)
{
    public static readonly SheetData Empty = new([], [], null);

    public bool IsEmpty => Headers.Count == 0;

    /// <summary>Index of a column with this header, searched from <paramref name="from"/> on, or -1.</summary>
    public int ColumnIndex(string name, int from = 0)
    {
        for (var i = from; i < Headers.Count; i++)
            if (TextFold.SameName(Headers[i], name)) return i;
        return -1;
    }

    /// <summary>The children sheet's "kit entregue" column, or -1 while no kit has been marked yet.</summary>
    public int KitColumn => ColumnIndex(ChildColumns.KitDelivered, ChildColumns.Expected.Count);

    public bool KitDelivered(SheetRow row) => KitColumn is var i and >= 0 && ChildColumns.IsYes(row.Values[i]);
}

public sealed record RequestResult(int Requested, int Unavailable);

/// <param name="Values">The child's row (table columns) as it was before the change.</param>
public sealed record StatusChange(string Previous, string Current, IReadOnlyList<string> Values);

/// <summary>
/// Treats the spreadsheets as the source of truth: every read opens the file, every change is
/// written straight back to it. Each spreadsheet is either a copy kept in the data folder
/// (uploaded through the site) or a file elsewhere on this computer that the admin linked, in
/// which case the system works on that very file. Either way, editing the file is visible in
/// the site right away, and vice versa.
/// </summary>
public sealed class SpreadsheetStore
{
    /// <summary>How far down a sheet to look for the table's header row (below a title block, logo, etc.).</summary>
    private const int HeaderSearchRows = 50;

    private readonly Lock _gate = new();
    private readonly string _defaultChildrenPath;
    private readonly string _defaultEmailsPath;
    private readonly string _settingsPath;
    private SheetLinks _links;

    /// <summary>Linked files, saved in the data folder so they survive a restart.</summary>
    private sealed record SheetLinks(string? Children = null, string? Emails = null);

    public SpreadsheetStore(IConfiguration config, IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(dir);
        _defaultChildrenPath = Path.Combine(dir, "children.xlsx");
        _defaultEmailsPath = Path.Combine(dir, "emails.xlsx");
        _settingsPath = Path.Combine(dir, "settings.json");
        _links = LoadLinks(_settingsPath);
    }

    /// <summary>The file the system currently works on: the linked file, or its own copy.</summary>
    public string ChildrenPath => _links.Children ?? _defaultChildrenPath;
    public string EmailsPath => _links.Emails ?? _defaultEmailsPath;

    /// <summary>The linked file, or null when the system works on its own copy.</summary>
    public string? ChildrenLink => _links.Children;
    public string? EmailsLink => _links.Emails;

    /// <summary>
    /// Changes whenever either spreadsheet file changes, however it was changed. Cheap enough
    /// (two file stats) for open pages to poll it.
    /// </summary>
    public string Version()
    {
        static string Stamp(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.LastWriteTimeUtc.Ticks}:{file.Length}" : "-";
        }
        return $"{Stamp(ChildrenPath)}|{Stamp(EmailsPath)}";
    }

    public SheetData ReadChildren()
    {
        lock (_gate)
        {
            var path = ChildrenPath;
            if (!File.Exists(path))
                return ChildrenLink is null ? SheetData.Empty : throw MissingLink(path);
            using var wb = LoadExisting(path);
            return Read(ChildrenTable(wb), ChildColumns.PublicCount, ChildColumns.Id, File.GetLastWriteTime(path));
        }
    }

    public SheetData ReadEmails()
    {
        lock (_gate)
        {
            var path = EmailsPath;
            if (!File.Exists(path))
                return EmailsLink is null ? SheetData.Empty : throw MissingLink(path);
            using var wb = LoadExisting(path);
            var table = FindTable(wb, EmailColumns.Expected.Count);
            return table is null ? SheetData.Empty : Read(table, EmailColumns.Expected.Count, keyIndex: null, File.GetLastWriteTime(path));
        }
    }

    public byte[]? ReadBytes(string path)
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            using var fs = OpenShared(path);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
    }

    public void ReplaceChildren(Stream upload)
    {
        if (ChildrenLink is not null)
            throw new SpreadsheetException("A planilha de crianças está vinculada a um arquivo. Edite esse arquivo, ou desvincule antes de enviar outro.");

        using var wb = OpenUpload(upload);
        ValidateChildren(ChildrenTable(wb), normalize: true);
        lock (_gate) Save(wb, _defaultChildrenPath);
    }

    public void ReplaceEmails(Stream upload)
    {
        if (EmailsLink is not null)
            throw new SpreadsheetException("A planilha de e-mails está vinculada a um arquivo. Edite esse arquivo, ou desvincule antes de enviar outro.");

        using var wb = OpenUpload(upload);
        ValidateEmails(EmailsTable(wb));
        lock (_gate) Save(wb, _defaultEmailsPath);
    }

    /// <summary>
    /// From now on, work directly on this file. It is checked first but not changed: it is only
    /// written to when something changes in the site.
    /// </summary>
    public void LinkChildren(string? input)
    {
        var path = CheckLinkPath(input);
        using (var wb = LoadExisting(path))
            ValidateChildren(ChildrenTable(wb), normalize: false);
        lock (_gate) SetLinks(_links with { Children = path });
    }

    public void LinkEmails(string? input)
    {
        var path = CheckLinkPath(input);
        using (var wb = LoadExisting(path))
            ValidateEmails(EmailsTable(wb));
        lock (_gate) SetLinks(_links with { Emails = path });
    }

    public void UnlinkChildren()
    {
        lock (_gate)
        {
            if (_links.Children is not { } linked) return;
            CopyInto(linked, _defaultChildrenPath);
            SetLinks(_links with { Children = null });
        }
    }

    public void UnlinkEmails()
    {
        lock (_gate)
        {
            if (_links.Emails is not { } linked) return;
            CopyInto(linked, _defaultEmailsPath);
            SetLinks(_links with { Emails = null });
        }
    }

    /// <summary>
    /// Checks every child row has a unique id and a known status. Columns are matched by position,
    /// so an unknown status usually means the columns are out of order: better to refuse the file
    /// than to misread every row. With <paramref name="normalize"/>, statuses are rewritten in
    /// their standard spelling.
    /// </summary>
    private static void ValidateChildren(Table t, bool normalize)
    {
        var seen = new Dictionary<string, int>();
        foreach (var r in ContentRows(t, ChildColumns.PublicCount))
        {
            var id = Text(t.Cell(r, ChildColumns.Id));
            if (id == "")
                throw new SpreadsheetException($"A linha {r} está sem id ({t.Headers[ChildColumns.Id]}). Todas as crianças precisam de um id.");
            if (!seen.TryAdd(id, r))
                throw new SpreadsheetException($"O id \"{id}\" aparece mais de uma vez (linhas {seen[id]} e {r}). Cada id deve ser único.");

            var cell = t.Cell(r, ChildColumns.Status);
            var status = ChildStatus.Of(Text(cell));
            if (!ChildStatus.All.Contains(status))
                throw new SpreadsheetException(
                    $"Linha {r}: \"{status}\" não é um status válido. A {ChildColumns.Status + 1}ª coluna da tabela ({t.Headers[ChildColumns.Status]}) " +
                    $"deve conter Disponível, Pendente ou Aprovado. Confira se as colunas estão na ordem: {ChildColumns.ExpectedList}.");
            if (normalize)
                cell.Value = ChildStatus.Label(status);
        }
    }

    private static string CheckLinkPath(string? input)
    {
        // Explorer's "Copiar como caminho" wraps the path in quotes.
        var path = Environment.ExpandEnvironmentVariables((input ?? "").Trim().Trim('"').Trim());
        if (path == "")
            throw new SpreadsheetException("Informe o caminho do arquivo.");
        if (!Path.IsPathFullyQualified(path))
            throw new SpreadsheetException(@"Informe o caminho completo do arquivo, por exemplo C:\Users\voce\Documents\criancas.xlsx.");
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new SpreadsheetException("O arquivo precisa ser uma planilha .xlsx.");
        if (!File.Exists(path))
            throw new SpreadsheetException($"Arquivo não encontrado: {path}");
        return Path.GetFullPath(path);
    }

    private static SpreadsheetException MissingLink(string path) =>
        new($"O arquivo vinculado não foi encontrado: {path}. Ele foi movido, renomeado ou apagado? Vincule o arquivo de novo na Administração.");

    private void SetLinks(SheetLinks links)
    {
        File.WriteAllText(_settingsPath, System.Text.Json.JsonSerializer.Serialize(links, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        _links = links;
    }

    private static SheetLinks LoadLinks(string settingsPath)
    {
        try
        {
            return File.Exists(settingsPath)
                ? System.Text.Json.JsonSerializer.Deserialize<SheetLinks>(File.ReadAllText(settingsPath)) ?? new()
                : new();
        }
        catch (System.Text.Json.JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// Copies the linked file into the system's own copy when unlinking, so the site carries on
    /// with the current data rather than whatever was uploaded before.
    /// </summary>
    private static void CopyInto(string source, string destination)
    {
        if (!File.Exists(source)) return;
        var temp = Path.ChangeExtension(destination, ".saving.xlsx");
        using (var src = OpenShared(source))
        using (var dst = File.Create(temp))
            src.CopyTo(dst);
        File.Move(temp, destination, overwrite: true);
    }

    /// <summary>Marks the given children as pending for this donor. Skips any no longer available.</summary>
    public RequestResult RequestChildren(IEnumerable<string> ids, string name, string email, string? phone)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            var rowsById = IndexById(t);
            var now = DateTime.Now;
            int requested = 0, unavailable = 0;

            foreach (var id in ids.Distinct())
            {
                if (!rowsById.TryGetValue(id, out var row)
                    || ChildStatus.Of(Text(t.Cell(row, ChildColumns.Status))) != ChildStatus.Available)
                {
                    unavailable++;
                    continue;
                }

                t.Cell(row, ChildColumns.Status).Value = ChildStatus.Label(ChildStatus.Pending);
                t.Cell(row, ChildColumns.DonorName).Value = name;
                t.Cell(row, ChildColumns.DonorEmail).Value = email;
                t.Cell(row, ChildColumns.DonorPhone).Value = phone ?? "";
                var at = t.Cell(row, ChildColumns.RequestedAt);
                at.Value = now;
                at.Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
                requested++;
            }

            if (requested > 0) Save(wb, ChildrenPath);
            return new RequestResult(requested, unavailable);
        }
    }

    /// <summary>Changes a child's status and reports what it was, so callers can react (e.g. email the donor on approval).</summary>
    public StatusChange SetStatus(string id, string status)
    {
        status = ChildStatus.Of(status);
        if (!ChildStatus.All.Contains(status))
            throw new SpreadsheetException($"Status desconhecido: \"{status}\".");

        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            if (!IndexById(t).TryGetValue(id, out var row))
                throw new SpreadsheetException($"Criança com id \"{id}\" não encontrada. A planilha pode ter mudado; recarregue a página.");

            var before = RowValues(t, row);
            var previous = ChildStatus.Of(before[ChildColumns.Status]);
            t.Cell(row, ChildColumns.Status).Value = ChildStatus.Label(status);
            if (status == ChildStatus.Available)
            {
                // Back on the public list, so the previous donor no longer applies.
                foreach (var column in ChildColumns.Donor)
                    t.Cell(row, column).Clear(XLClearOptions.Contents);
            }
            if (status != ChildStatus.Approved && ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.KitDelivered) is var kit and >= 0)
                t.Cell(row, kit).Clear(XLClearOptions.Contents);  // only approved requests have a kit

            Save(wb, ChildrenPath);
            return new StatusChange(previous, status, before);
        }
    }

    /// <summary>Marks an approved child's kit as delivered (or not), adding the column on first use.</summary>
    public void SetKitDelivered(string id, bool delivered)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            if (!IndexById(t).TryGetValue(id, out var row))
                throw new SpreadsheetException($"Criança com id \"{id}\" não encontrada. A planilha pode ter mudado; recarregue a página.");
            if (ChildStatus.Of(Text(t.Cell(row, ChildColumns.Status))) != ChildStatus.Approved)
                throw new SpreadsheetException("Só dá para marcar o kit de crianças com pedido aprovado.");

            EnsureColumns(t, ChildColumns.Expected.Count, [ChildColumns.KitDelivered]);
            var cell = t.Cell(row, ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.KitDelivered));
            if (delivered) cell.Value = "Sim";
            else cell.Clear(XLClearOptions.Contents);
            Save(wb, ChildrenPath);
        }
    }

    /// <summary>
    /// Sets one cell of a child's row, as typed by the admin, and returns the value as it now
    /// shows. Status changes go through <see cref="SetStatus"/> instead, which has side effects.
    /// </summary>
    public string UpdateCell(string id, int column, string value)
    {
        value = value.Trim();
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            if (column < 0 || column >= t.Headers.Count || column == ChildColumns.Status)
                throw new SpreadsheetException("Coluna inválida.");
            if (!IndexById(t).TryGetValue(id, out var row))
                throw new SpreadsheetException($"Criança com id \"{id}\" não encontrada. A planilha pode ter mudado; recarregue a página.");

            if (column == ChildColumns.Id && value != id)
            {
                if (value == "")
                    throw new SpreadsheetException("O id não pode ficar vazio.");
                if (ContentRows(t, ChildColumns.PublicCount).Any(r => Text(t.Cell(r, ChildColumns.Id)) == value))
                    throw new SpreadsheetException($"Já existe uma criança com o id \"{value}\".");
            }

            var cell = t.Cell(row, column);
            SetValue(cell, value);
            Save(wb, ChildrenPath);
            return cell.GetFormattedString();
        }
    }

    /// <summary>Adds an empty, available child at the end of the table and returns its new id.</summary>
    public string AddChild()
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            var rows = ContentRows(t, ChildColumns.PublicCount).ToList();
            var ids = rows.Select(r => Text(t.Cell(r, ChildColumns.Id))).ToList();

            // Next number after the highest numeric id, so the "Nº" sequence carries on.
            var id = (ids.Select(i => long.TryParse(i, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1).ToString();

            var row = AppendRow(t, rows);
            SetValue(t.Cell(row, ChildColumns.Id), id);
            t.Cell(row, ChildColumns.Status).Value = ChildStatus.Label(ChildStatus.Available);

            Save(wb, ChildrenPath);
            return id;
        }
    }

    /// <summary>Deletes a child's whole row from the sheet.</summary>
    public void DeleteChild(string id)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            if (!IndexById(t).TryGetValue(id, out var row))
                throw new SpreadsheetException($"Criança com id \"{id}\" não encontrada. A planilha pode ter mudado; recarregue a página.");

            t.Sheet.Row(row).Delete();
            Save(wb, ChildrenPath);
        }
    }

    /// <summary>
    /// Adds a donor to the emails sheet unless their email is already in it (creating the sheet
    /// if there is none yet). Returns whether they were added.
    /// </summary>
    public bool EnsureDonor(string name, string email)
    {
        lock (_gate)
        {
            var path = EmailsPath;
            XLWorkbook wb;
            Table t;
            if (File.Exists(path))
            {
                wb = LoadExisting(path);
                t = EmailsTable(wb);
            }
            else if (EmailsLink is not null)
            {
                throw MissingLink(path);
            }
            else
            {
                wb = new XLWorkbook();
                var ws = wb.AddWorksheet("Doadores");
                ws.Cell(1, 1).Value = "Nome";
                ws.Cell(1, 2).Value = "Email";
                ws.Row(1).Style.Font.Bold = true;
                ws.Column(1).Width = 32;
                ws.Column(2).Width = 32;
                t = new Table(ws, 1, 1, ["Nome", "Email"]);
            }

            using (wb)
            {
                EnsureColumns(t, EmailColumns.Expected.Count, EmailColumns.Added);
                var rows = ContentRows(t, EmailColumns.Expected.Count).ToList();
                if (rows.Any(r => SplitEmails(Text(t.Cell(r, EmailColumns.Email))).Any(e => SameEmail(e, email))))
                    return false;

                var row = AppendRow(t, rows);
                t.Cell(row, EmailColumns.Name).Value = name;
                t.Cell(row, EmailColumns.Email).Value = email;
                t.Cell(row, ColumnIndex(t, EmailColumns.Expected.Count, EmailColumns.Origin)).Value = "Site";
                Save(wb, path);
                return true;
            }
        }
    }

    /// <summary>Notes in the emails sheet's "último e-mail" column what was sent to these addresses, and when.</summary>
    public void RecordEmailSent(IReadOnlyCollection<string> emails, string label)
    {
        if (emails.Count == 0) return;
        lock (_gate)
        {
            var path = EmailsPath;
            if (!File.Exists(path)) return;
            using var wb = LoadExisting(path);
            var t = EmailsTable(wb);
            EnsureColumns(t, EmailColumns.Expected.Count, EmailColumns.Added);
            var column = ColumnIndex(t, EmailColumns.Expected.Count, EmailColumns.LastEmail);
            var sent = new HashSet<string>(emails.Select(e => e.Trim()), StringComparer.OrdinalIgnoreCase);
            var stamp = $"{DateTime.Now:dd/MM/yyyy HH:mm} · {label}";

            var changed = false;
            foreach (var r in ContentRows(t, EmailColumns.Expected.Count))
            {
                if (!SplitEmails(Text(t.Cell(r, EmailColumns.Email))).Any(sent.Contains)) continue;
                t.Cell(r, column).Value = stamp;
                changed = true;
            }
            if (changed) Save(wb, path);
        }
    }

    public static bool SameEmail(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Table EmailsTable(XLWorkbook wb) =>
        FindTable(wb, EmailColumns.Expected.Count)
        ?? throw new SpreadsheetException(
            $"Não encontrei a tabela de e-mails. Ela precisa de uma linha de cabeçalho com as colunas lado a lado, nesta ordem: {EmailColumns.ExpectedList}.");

    /// <summary>
    /// The email column is matched by position, so anything that isn't an address (or a list of
    /// addresses) means the columns are out of order.
    /// </summary>
    private static void ValidateEmails(Table t)
    {
        foreach (var r in ContentRows(t, EmailColumns.Expected.Count))
        {
            foreach (var email in SplitEmails(Text(t.Cell(r, EmailColumns.Email))))
            {
                if (!LooksLikeEmail(email))
                    throw new SpreadsheetException(
                        $"Linha {r}: \"{email}\" não parece um e-mail. A 2ª coluna da tabela ({t.Headers[EmailColumns.Email]}) deve ser o e-mail " +
                        $"(ou vários, separados por ponto e vírgula). Confira se as colunas estão na ordem: {EmailColumns.ExpectedList}.");
            }
        }
    }

    public static bool LooksLikeEmail(string value) =>
        Regex.IsMatch(value.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    private static readonly char[] EmailSeparators = [';', ',', '\n'];

    /// <summary>
    /// A cell can hold more than one address, separated by semicolons or commas (as Outlook and
    /// spreadsheets commonly do) — e.g. "a@x.com; b@y.com" is two different donors' emails.
    /// </summary>
    public static IReadOnlyList<string> SplitEmails(string raw) =>
        raw.Split(EmailSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static List<string> RowValues(Table t, int row) =>
        t.Headers.Select((_, i) => t.Cell(row, i).GetFormattedString()).ToList();

    /// <summary>
    /// Makes room for a new row at the end of the table and returns its number. Inserts rather
    /// than writing below the table, so anything under it moves down and the new row picks up
    /// the formatting of the one above.
    /// </summary>
    private static int AppendRow(Table t, List<int> contentRows)
    {
        if (contentRows.Count == 0) return t.HeaderRow + 1;
        var last = contentRows[^1];
        t.Sheet.Row(last).InsertRowsBelow(1);
        return last + 1;
    }

    /// <summary>Appends the named columns after the table's expected ones if they aren't there yet.</summary>
    private static void EnsureColumns(Table t, int expectedCount, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (ColumnIndex(t, expectedCount, name) >= 0) continue;
            var index = t.Headers.Count;
            var cell = t.Cell(t.HeaderRow, index);
            cell.Value = name;
            cell.Style = t.Cell(t.HeaderRow, 0).Style;
            if (t.Sheet.Column(cell.Address.ColumnNumber).Width < 16)
                t.Sheet.Column(cell.Address.ColumnNumber).Width = 22;
            t.Headers.Add(name);
        }
    }

    /// <summary>0-based table column with this header, searched after the expected (positional) ones, or -1.</summary>
    private static int ColumnIndex(Table t, int expectedCount, string name)
    {
        for (var i = expectedCount; i < t.Headers.Count; i++)
            if (TextFold.SameName(t.Headers[i], name))
                return i;
        return -1;
    }

    private static readonly Regex PlainInteger = new(@"^-?(0|[1-9]\d{0,14})$");

    /// <summary>Writes typed text the way Excel would: whole numbers as numbers, anything else as text.</summary>
    private static void SetValue(IXLCell cell, string value)
    {
        if (value == "")
            cell.Clear(XLClearOptions.Contents);
        else if (PlainInteger.IsMatch(value))
            cell.Value = long.Parse(value);
        else
            cell.Value = value;
    }

    /// <summary>
    /// Where a table sits in a workbook. Real spreadsheets often have a title block, logo or
    /// notes above the table, so it doesn't necessarily start at A1.
    /// </summary>
    private sealed record Table(IXLWorksheet Sheet, int HeaderRow, int FirstColumn, List<string> Headers)
    {
        /// <param name="index">0-based column within the table.</param>
        public IXLCell Cell(int row, int index) => Sheet.Cell(row, FirstColumn + index);
    }

    /// <summary>
    /// Finds the first row, on the first sheet that has one, with at least
    /// <paramref name="minColumns"/> filled cells side by side. That row is the table's header.
    /// </summary>
    private static Table? FindTable(XLWorkbook wb, int minColumns)
    {
        foreach (var ws in wb.Worksheets)
        {
            var lastRow = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, HeaderSearchRows);
            for (var r = 1; r <= lastRow; r++)
            {
                var lastCol = ws.Row(r).LastCellUsed()?.Address.ColumnNumber ?? 0;
                var run = new List<string>();
                for (var c = 1; c <= lastCol + 1; c++)
                {
                    var text = c <= lastCol ? Text(ws.Cell(r, c)) : "";
                    if (text != "")
                    {
                        run.Add(text);
                        continue;
                    }
                    if (run.Count >= minColumns)
                        return new Table(ws, r, c - run.Count, run);
                    run.Clear();
                }
            }
        }
        return null;
    }

    private static Table ChildrenTable(XLWorkbook wb) =>
        FindTable(wb, ChildColumns.Expected.Count)
        ?? throw new SpreadsheetException(
            $"Não encontrei a tabela de crianças. Ela precisa de uma linha de cabeçalho com {ChildColumns.Expected.Count} " +
            $"colunas preenchidas lado a lado, nesta ordem: {ChildColumns.ExpectedList}.");

    private static SheetData Read(Table t, int contentColumns, int? keyIndex, DateTime lastModified)
    {
        var rows = new List<SheetRow>();
        foreach (var r in ContentRows(t, contentColumns))
        {
            var values = t.Headers.Select((_, i) => t.Cell(r, i).GetFormattedString()).ToList();
            var key = keyIndex is int k ? values[k].Trim() : r.ToString();
            rows.Add(new SheetRow(r, key, values));
        }
        return new SheetData(t.Headers, rows, lastModified);
    }

    private static string Text(IXLCell cell) => cell.GetFormattedString().Trim();

    /// <summary>Rows below the header with something in at least one of the table's first <paramref name="columns"/> columns.</summary>
    private static IEnumerable<int> ContentRows(Table t, int columns)
    {
        var last = t.Sheet.LastRowUsed()?.RowNumber() ?? t.HeaderRow;
        for (var r = t.HeaderRow + 1; r <= last; r++)
        {
            for (var i = 0; i < columns; i++)
            {
                if (Text(t.Cell(r, i)) != "")
                {
                    yield return r;
                    break;
                }
            }
        }
    }

    /// <summary>Row number for each id. Ids that appear more than once are left out, so they can't be changed by mistake.</summary>
    private static Dictionary<string, int> IndexById(Table t)
    {
        var rows = new Dictionary<string, int>();
        var duplicates = new HashSet<string>();
        foreach (var r in ContentRows(t, ChildColumns.PublicCount))
        {
            var id = Text(t.Cell(r, ChildColumns.Id));
            if (id != "" && !rows.TryAdd(id, r)) duplicates.Add(id);
        }
        foreach (var id in duplicates) rows.Remove(id);
        return rows;
    }

    private static XLWorkbook OpenUpload(Stream upload)
    {
        try
        {
            return new XLWorkbook(FixPictureIds(upload));
        }
        catch (Exception)
        {
            throw new SpreadsheetException("Não foi possível ler o arquivo como planilha do Excel (.xlsx).");
        }
    }

    private static XLWorkbook LoadExisting(string path)
    {
        if (!File.Exists(path))
            throw new SpreadsheetException("Nenhuma planilha foi enviada ainda.");

        // Sharing flags that still allow reading while Excel has the file open.
        using var fs = OpenShared(path);
        try
        {
            return new XLWorkbook(FixPictureIds(fs));
        }
        catch (Exception)
        {
            throw new SpreadsheetException("Não foi possível ler a planilha salva. Envie-a novamente.");
        }
    }

    private static readonly Regex PictureId = new(@"(<(?:\w+:)?cNvPr\s+id="")(\d+)("")");

    /// <summary>
    /// Copies the workbook into memory, renumbering pictures that share an id. Google Sheets
    /// exports produce those; Excel doesn't mind, but ClosedXML refuses to open the file.
    /// </summary>
    private static MemoryStream FixPictureIds(Stream source)
    {
        var ms = new MemoryStream();
        source.CopyTo(ms);
        ms.Position = 0;

        try
        {
            List<string> broken;
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
            {
                broken = zip.Entries
                    .Where(e => e.FullName.StartsWith("xl/drawings/") && e.FullName.EndsWith(".xml"))
                    .Where(e =>
                    {
                        using var reader = new StreamReader(e.Open());
                        var ids = PictureId.Matches(reader.ReadToEnd()).Select(m => m.Groups[2].Value).ToList();
                        return ids.Count != ids.Distinct().Count();
                    })
                    .Select(e => e.FullName)
                    .ToList();
            }

            if (broken.Count > 0)
            {
                ms.Position = 0;
                using var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true);
                foreach (var name in broken)
                {
                    var entry = zip.GetEntry(name)!;
                    string xml;
                    using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
                    var next = 0;
                    xml = PictureId.Replace(xml, m => $"{m.Groups[1].Value}{++next}{m.Groups[3].Value}");
                    using var stream = entry.Open();
                    stream.SetLength(0);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.Write(xml);
                }
            }
        }
        catch (InvalidDataException)
        {
            // Not a zip at all; let ClosedXML report it as an unreadable spreadsheet.
        }

        ms.Position = 0;
        return ms;
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Writes to a temp file and swaps it in, so readers never see a half-written file.</summary>
    private static void Save(XLWorkbook wb, string path)
    {
        var temp = Path.ChangeExtension(path, ".saving.xlsx");
        wb.SaveAs(temp);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(temp);
            throw new SpreadsheetException("A planilha está aberta em outro programa (como o Excel). Feche-a e tente novamente.");
        }
    }
}
