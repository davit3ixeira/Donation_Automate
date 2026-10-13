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

/// <summary>How a donor helps a child: buying the Christmas kit (the usual way) or, failing that, a Pix.</summary>
public static class ChildDonation
{
    public const string Kit = "Kit";
    public const string Pix = "Pix";
    public static readonly IReadOnlyList<string> All = [Kit, Pix];

    /// <summary>"Kit", "Pix" or "" for a raw cell or form value; null if it is neither.</summary>
    public static string? Of(string? raw) => TextFold.Fold(raw) switch
    {
        "" => "",
        "kit" => Kit,
        "pix" => Pix,
        _ => null,
    };
}

/// <summary>The child's gender, "M" or "F" in the sheet. Blank is allowed (not informed).</summary>
public static class ChildGender
{
    public const string Male = "M";
    public const string Female = "F";

    /// <summary>"M", "F" or "" for a raw cell value; null if it isn't a gender at all.</summary>
    public static string? Of(string? raw) => TextFold.Fold(raw) switch
    {
        "" => "",
        "m" or "masculino" or "menino" or "masc" => Male,
        "f" or "feminino" or "menina" or "fem" => Female,
        _ => null,
    };

    public static string Label(string gender) => gender switch
    {
        Male => "Menino",
        Female => "Menina",
        _ => "",
    };
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
        "id", "turma", "faixa etária", "nome da criança", "gênero", "idade", "calçado", "roupa",
        "nome do doador", "e-mail do doador", "data do pedido", "status",
    ];

    // 0-based positions, counted from the table's first column.
    public const int Id = 0;
    public const int Turma = 1;
    public const int AgeGroup = 2;
    public const int Nome = 3;
    public const int Gender = 4;
    public const int Age = 5;
    public const int Shoes = 6;
    public const int Clothes = 7;
    public const int DonorName = 8;
    public const int DonorEmail = 9;
    public const int RequestedAt = 10;
    public const int Status = 11;

    /// <summary>The first columns, up to "roupa", describe the child. Users only ever see these.</summary>
    public const int PublicCount = 8;

    /// <summary>Filled in when someone requests the child; cleared when it becomes available again.</summary>
    public static readonly IReadOnlyList<int> Donor = [DonorName, DonorEmail, RequestedAt];

    // Columns the system appends after the expected ones the first time it needs them, found by name.

    /// <summary>"Sim" once the donation (kit or Pix) was received. Only approved requests have one.</summary>
    public const string Done = "realizado";

    /// <summary>What <see cref="Done"/> used to be called; renamed when a sheet is read.</summary>
    public const string OldDone = "kit entregue";

    /// <summary>How the donor will help: "Kit" or "Pix" (see <see cref="ChildDonation"/>).</summary>
    public const string Donation = "doação";

    /// <summary>When the admin emailed the donor the confirmation link; blank until then.</summary>
    public const string ConfirmationSent = "confirmação enviada";

    /// <summary>Filled in alongside a request; cleared when the child becomes available again.</summary>
    public static readonly IReadOnlyList<string> RequestExtras = [Donation, ConfirmationSent];

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

    // The children sheet's system columns, or -1 while the system hasn't needed them yet.
    public int DoneColumn => ColumnIndex(ChildColumns.Done, ChildColumns.Expected.Count);
    public int DonationColumn => ColumnIndex(ChildColumns.Donation, ChildColumns.Expected.Count);
    public int ConfirmationSentColumn => ColumnIndex(ChildColumns.ConfirmationSent, ChildColumns.Expected.Count);

    public bool Done(SheetRow row) => DoneColumn is var i and >= 0 && ChildColumns.IsYes(row.Values[i]);
    public string Donation(SheetRow row) => DonationColumn is var i and >= 0 ? ChildDonation.Of(row.Values[i]) ?? "" : "";
    public bool ConfirmationSent(SheetRow row) => ConfirmationSentColumn is var i and >= 0 && row.Values[i].Trim() != "";
}

/// <summary>A child requested by a donor: the row's expected columns, and how the donor will help.</summary>
public sealed record RequestedChild(IReadOnlyList<string> Values, string Donation);

/// <param name="Children">The children this request made pending, as saved.</param>
public sealed record RequestResult(int Requested, int Unavailable, IReadOnlyList<RequestedChild> Children);

/// <summary>A donor's requested children, with the donor's name and address as in the sheet.</summary>
public sealed record DonorChildren(string Name, string Email, IReadOnlyList<RequestedChild> Children)
{
    public IReadOnlyList<string> Ids => Children.Select(c => c.Values[ChildColumns.Id].Trim()).ToList();
}

/// <param name="Values">The child's row (table columns) as it was before the change.</param>
public sealed record StatusChange(string Previous, string Current, IReadOnlyList<string> Values);

/// <summary>
/// Treats the spreadsheets as the source of truth: every read opens the file, every change is
/// written straight back to it. Each spreadsheet is the copy uploaded through the site and kept
/// in the data folder; the admin downloads it to get the current data.
/// </summary>
public sealed class SpreadsheetStore
{
    /// <summary>How far down a sheet to look for the table's header row (below a title block, logo, etc.).</summary>
    private const int HeaderSearchRows = 50;

    private readonly Lock _gate = new();

    public SpreadsheetStore(IConfiguration config, IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(dir);
        ChildrenPath = Path.Combine(dir, "children.xlsx");
        EmailsPath = Path.Combine(dir, "emails.xlsx");
    }

    public string ChildrenPath { get; }
    public string EmailsPath { get; }

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
                return SheetData.Empty;
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
                return SheetData.Empty;
            using var wb = LoadExisting(path);
            var table = FindTable(wb, EmailColumns.Expected.Count);
            return table is null ? SheetData.Empty : Read(table, EmailColumns.Expected.Count, keyIndex: null, File.GetLastWriteTime(path));
        }
    }

    /// <summary>
    /// The children spreadsheet with only these rows (by row number, as in <see cref="SheetRow.RowNumber"/>)
    /// left in the table; everything else in the file is kept. For exporting what the admin filtered.
    /// </summary>
    public byte[] ExportChildren(IReadOnlySet<int> keepRows)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            foreach (var row in ContentRows(t, ChildColumns.PublicCount).Where(r => !keepRows.Contains(r)).Reverse().ToList())
                t.Sheet.Row(row).Delete();
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
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
        using var wb = OpenUpload(upload);
        ValidateChildren(ChildrenTable(wb));
        lock (_gate) Save(wb, ChildrenPath);
    }

    /// <summary>
    /// Takes a spreadsheet off the site. The file isn't deleted: it moves to data/removidas with the
    /// date in its name, in case it was removed by mistake.
    /// </summary>
    public void RemoveChildren() => Remove(ChildrenPath, "criancas");
    public void RemoveEmails() => Remove(EmailsPath, "doadores");

    private void Remove(string path, string name)
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return;
            var dir = Path.Combine(Path.GetDirectoryName(path)!, "removidas");
            Directory.CreateDirectory(dir);
            try
            {
                File.Move(path, Path.Combine(dir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx"));
            }
            catch (IOException)
            {
                throw new SpreadsheetException("A planilha está aberta em outro programa (como o Excel). Feche-a e tente novamente.");
            }
        }
    }

    public void ReplaceEmails(Stream upload)
    {
        using var wb = OpenUpload(upload);
        ValidateEmails(EmailsTable(wb));
        lock (_gate) Save(wb, EmailsPath);
    }

    /// <summary>
    /// Checks every child row has a unique id and a known status. Columns are matched by position,
    /// so an unknown status usually means the columns are out of order: better to refuse the file
    /// than to misread every row. Statuses are rewritten in their standard spelling.
    /// </summary>
    private static void ValidateChildren(Table t)
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
            cell.Value = ChildStatus.Label(status);

            var genderCell = t.Cell(r, ChildColumns.Gender);
            if (ChildGender.Of(Text(genderCell)) is not { } gender)
                throw new SpreadsheetException(
                    $"Linha {r}: \"{Text(genderCell)}\" não é um gênero válido. A {ChildColumns.Gender + 1}ª coluna da tabela ({t.Headers[ChildColumns.Gender]}) " +
                    $"deve conter M, F ou ficar vazia. Confira se as colunas estão na ordem: {ChildColumns.ExpectedList}.");
            genderCell.Value = gender;
        }
    }

    /// <summary>Marks the given children as pending for this donor. Skips any no longer available.</summary>
    /// <param name="donation">"Kit" or "Pix" (see <see cref="ChildDonation"/>).</param>
    public RequestResult RequestChildren(IEnumerable<string> ids, string name, string email, string donation)
    {
        donation = ChildDonation.Of(donation) is { Length: > 0 } d ? d : ChildDonation.Kit;
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            var rowsById = IndexById(t);
            var now = DateTime.Now;
            int unavailable = 0;
            var children = new List<RequestedChild>();

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
                var at = t.Cell(row, ChildColumns.RequestedAt);
                at.Value = now;
                at.Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
                EnsureColumns(t, ChildColumns.Expected.Count, ChildColumns.RequestExtras);
                t.Cell(row, ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.Donation)).Value = donation;
                t.Cell(row, ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.ConfirmationSent)).Clear(XLClearOptions.Contents);
                children.Add(Requested(t, row));
            }

            if (children.Count > 0) Save(wb, ChildrenPath);
            return new RequestResult(children.Count, unavailable, children);
        }
    }

    /// <summary>
    /// The pending children of the donor who asked for this one (same email), including it. Null if
    /// the child isn't pending.
    /// </summary>
    public DonorChildren? PendingOfDonor(string id)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            if (!IndexById(t).TryGetValue(id, out var row))
                throw new SpreadsheetException($"Criança com id \"{id}\" não encontrada. A planilha pode ter mudado; recarregue a página.");
            if (ChildStatus.Of(Text(t.Cell(row, ChildColumns.Status))) != ChildStatus.Pending) return null;

            var email = Text(t.Cell(row, ChildColumns.DonorEmail));
            var rows = DonorRows(t, email, ChildStatus.Pending);
            return new DonorChildren(Text(t.Cell(row, ChildColumns.DonorName)), email, rows.Select(r => Requested(t, r)).ToList());
        }
    }

    /// <summary>Notes, on each of these children's rows, that the confirmation link was emailed now.</summary>
    public void MarkConfirmationSent(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            var rowsById = IndexById(t);
            EnsureColumns(t, ChildColumns.Expected.Count, ChildColumns.RequestExtras);
            var column = ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.ConfirmationSent);
            foreach (var id in ids)
            {
                if (!rowsById.TryGetValue(id, out var row)) continue;
                var cell = t.Cell(row, column);
                cell.Value = DateTime.Now;
                cell.Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
            }
            Save(wb, ChildrenPath);
        }
    }

    /// <summary>
    /// Approves these children, the ones still pending for this donor (same email); others are
    /// left alone (chosen again by someone else, or already approved). Returns the approved ones.
    /// </summary>
    public DonorChildren ApproveForDonor(string email, IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            var wanted = ids.ToHashSet();
            var rows = DonorRows(t, email, ChildStatus.Pending)
                .Where(r => wanted.Contains(Text(t.Cell(r, ChildColumns.Id))))
                .ToList();
            foreach (var r in rows)
                t.Cell(r, ChildColumns.Status).Value = ChildStatus.Label(ChildStatus.Approved);

            var name = rows.Count > 0 ? Text(t.Cell(rows[0], ChildColumns.DonorName)) : "";
            if (rows.Count > 0) Save(wb, ChildrenPath);
            return new DonorChildren(name, email, rows.Select(r => Requested(t, r)).ToList());
        }
    }

    /// <summary>Whether these children are already approved for this donor (e.g. a confirmation link opened twice).</summary>
    public bool AllApprovedForDonor(string email, IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            var approved = DonorRows(t, email, ChildStatus.Approved).Select(r => Text(t.Cell(r, ChildColumns.Id))).ToHashSet();
            return ids.All(approved.Contains);
        }
    }

    /// <summary>Rows with this status whose donor email matches (ignoring case and spaces).</summary>
    private static List<int> DonorRows(Table t, string email, string status) =>
        ContentRows(t, ChildColumns.PublicCount)
            .Where(r => ChildStatus.Of(Text(t.Cell(r, ChildColumns.Status))) == status)
            .Where(r => string.Equals(Text(t.Cell(r, ChildColumns.DonorEmail)), email.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static RequestedChild Requested(Table t, int row)
    {
        var donation = ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.Donation) is var d and >= 0
            ? ChildDonation.Of(Text(t.Cell(row, d))) ?? ""
            : "";
        return new RequestedChild(RowValues(t, row), donation);
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
                foreach (var name in ChildColumns.RequestExtras)
                    if (ColumnIndex(t, ChildColumns.Expected.Count, name) is var extra and >= 0)
                        t.Cell(row, extra).Clear(XLClearOptions.Contents);
            }
            if (status != ChildStatus.Approved && ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.Done) is var done and >= 0)
                t.Cell(row, done).Clear(XLClearOptions.Contents);  // only approved requests can be done

            Save(wb, ChildrenPath);
            return new StatusChange(previous, status, before);
        }
    }

    /// <summary>Marks an approved child's donation as done (or not), adding the column on first use.</summary>
    public void SetDone(string id, bool done)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            if (!IndexById(t).TryGetValue(id, out var row))
                throw new SpreadsheetException($"Criança com id \"{id}\" não encontrada. A planilha pode ter mudado; recarregue a página.");
            if (ChildStatus.Of(Text(t.Cell(row, ChildColumns.Status))) != ChildStatus.Approved)
                throw new SpreadsheetException("Só dá para marcar como realizado quando o pedido está aprovado.");

            EnsureColumns(t, ChildColumns.Expected.Count, [ChildColumns.Done]);
            var cell = t.Cell(row, ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.Done));
            if (done) cell.Value = "Sim";
            else cell.Clear(XLClearOptions.Contents);
            Save(wb, ChildrenPath);
        }
    }

    /// <summary>Sets how the donor of a pending or approved child will help: "Kit" or "Pix".</summary>
    public void SetDonation(string id, string value)
    {
        if (ChildDonation.Of(value) is not { Length: > 0 } donation)
            throw new SpreadsheetException("Escolha Kit ou Pix.");
        lock (_gate)
        {
            using var wb = LoadExisting(ChildrenPath);
            var t = ChildrenTable(wb);
            if (!IndexById(t).TryGetValue(id, out var row))
                throw new SpreadsheetException($"Criança com id \"{id}\" não encontrada. A planilha pode ter mudado; recarregue a página.");
            if (ChildStatus.Of(Text(t.Cell(row, ChildColumns.Status))) == ChildStatus.Available)
                throw new SpreadsheetException("Esta criança ainda não tem padrinho.");

            EnsureColumns(t, ChildColumns.Expected.Count, ChildColumns.RequestExtras);
            t.Cell(row, ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.Donation)).Value = donation;
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
    /// <summary>
    /// Deletes a donor's row from the emails sheet. The row must still hold the same email cell as
    /// shown on the page, so a sheet that changed meanwhile doesn't lose the wrong person. The
    /// children they asked for are not touched.
    /// </summary>
    public void DeleteDonor(int rowNumber, string emailCell)
    {
        lock (_gate)
        {
            using var wb = LoadExisting(EmailsPath);
            var t = EmailsTable(wb);
            if (!ContentRows(t, EmailColumns.Expected.Count).Contains(rowNumber)
                || Text(t.Cell(rowNumber, EmailColumns.Email)) != emailCell.Trim())
                throw new SpreadsheetException("Este doador não está mais nessa linha da planilha. Recarregue a página e tente de novo.");

            t.Sheet.Row(rowNumber).Delete();
            Save(wb, EmailsPath);
        }
    }

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

    /// <summary>
    /// The children table. Sheets in older layouts are brought up to date in the workbook, so they
    /// keep working; the file itself changes the next time it is saved.
    /// </summary>
    private static Table ChildrenTable(XLWorkbook wb)
    {
        var notFound = new SpreadsheetException(
            $"Não encontrei a tabela de crianças. Ela precisa de uma linha de cabeçalho com {ChildColumns.Expected.Count} " +
            $"colunas preenchidas lado a lado, nesta ordem: {ChildColumns.ExpectedList}.");

        // Sheets from before the gender column have one column fewer.
        var t = FindTable(wb, ChildColumns.Expected.Count - 1) ?? throw notFound;

        // Older sheets had a "telefone do doador" column among the donor's: drop it.
        var phone = t.Headers.FindIndex(h => TextFold.Fold(h).Contains("telefone"));
        if (phone > ChildColumns.Nome)
        {
            t.Sheet.Column(t.FirstColumn + phone).Delete();
            t = FindTable(wb, ChildColumns.Expected.Count - 1) ?? throw notFound;
        }

        // Sheets from before the gender column: add it, empty, after the name.
        if (!LooksLikeGender(t))
        {
            t.Sheet.Column(t.FirstColumn + ChildColumns.Gender).InsertColumnsBefore(1);
            t.Sheet.Cell(t.HeaderRow, t.FirstColumn + ChildColumns.Gender).Value = ChildColumns.Expected[ChildColumns.Gender];
            t = FindTable(wb, ChildColumns.Expected.Count) ?? throw notFound;
        }

        // "kit entregue" became "realizado" (a Pix can be done too).
        if (ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.OldDone) is var old and >= 0
            && ColumnIndex(t, ChildColumns.Expected.Count, ChildColumns.Done) < 0)
        {
            t.Cell(t.HeaderRow, old).Value = ChildColumns.Done;
            t.Headers[old] = ChildColumns.Done;
        }
        return t;
    }

    /// <summary>Whether the column after the name is the gender: titled so, or holding only M/F values.</summary>
    private static bool LooksLikeGender(Table t)
    {
        if (t.Headers.Count <= ChildColumns.Gender) return false;
        var header = TextFold.Fold(t.Headers[ChildColumns.Gender]);
        if (header.Contains("genero") || header.Contains("sexo")) return true;

        var values = ContentRows(t, ChildColumns.Nome + 1)
            .Select(r => Text(t.Cell(r, ChildColumns.Gender)))
            .Where(v => v != "")
            .ToList();
        return values.Count > 0 && values.All(v => ChildGender.Of(v) is ChildGender.Male or ChildGender.Female);
    }

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

    private const string HowToExport =
        "No Google Sheets, baixe como Microsoft Excel (.xlsx) ou CSV: no computador, Arquivo → Fazer download; " +
        "no celular, ⋮ → Compartilhar e exportar → Salvar como → Excel.";

    /// <summary>
    /// Reads an uploaded spreadsheet by its content, not its name (phones often send files without
    /// an extension): an Excel workbook (.xlsx, which is also what Google Sheets downloads as Excel)
    /// or a CSV (from Google Sheets or Excel), which is turned into a workbook.
    /// </summary>
    private static XLWorkbook OpenUpload(Stream upload)
    {
        var ms = new MemoryStream();
        upload.CopyTo(ms);
        var bytes = ms.ToArray();

        if (bytes.AsSpan().StartsWith("PK"u8))
        {
            if (IsOpenDocument(ms))
                throw new SpreadsheetException($"Este arquivo está no formato OpenDocument (.ods), que o site não lê. {HowToExport}");
            ms.Position = 0;
            try
            {
                return new XLWorkbook(FixPictureIds(ms));
            }
            catch (Exception)
            {
                throw new SpreadsheetException($"Não foi possível ler o arquivo como planilha. {HowToExport}");
            }
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }))
            throw new SpreadsheetException("Este arquivo está no formato antigo do Excel (.xls). No Excel, use Salvar como → Pasta de Trabalho do Excel (.xlsx).");
        if (bytes.AsSpan().StartsWith("%PDF"u8))
            throw new SpreadsheetException($"Este arquivo é um PDF, não uma planilha. {HowToExport}");

        return FromCsv(bytes)
            ?? throw new SpreadsheetException($"Não foi possível ler o arquivo como planilha. Envie um arquivo do Excel (.xlsx) ou CSV. {HowToExport}");
    }

    /// <summary>An OpenDocument file (.ods) is a zip whose "mimetype" entry says so.</summary>
    private static bool IsOpenDocument(MemoryStream zipBytes)
    {
        try
        {
            zipBytes.Position = 0;
            using var zip = new ZipArchive(zipBytes, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.GetEntry("mimetype") is not { } entry) return false;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd().Contains("opendocument", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// A CSV file as a one-sheet workbook, every value as text. The separator (comma from Google
    /// Sheets, semicolon from Excel in Portuguese, or tab) is the one the first line uses most.
    /// Null if it doesn't look like text with columns.
    /// </summary>
    private static XLWorkbook? FromCsv(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);  // Excel saves CSV in the Windows code page
        }
        text = text.TrimStart('﻿');
        if (text.Contains('\0')) return null;

        var firstLine = text.Split('\n', 2)[0];
        var separator = new[] { ';', ',', '\t' }.MaxBy(c => firstLine.Count(x => x == c));
        if (!firstLine.Contains(separator)) return null;

        var rows = ParseCsv(text, separator);
        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Planilha");
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                if (rows[r][c].Trim() is { Length: > 0 } value)
                    ws.Cell(r + 1, c + 1).Value = value;
        return wb;
    }

    /// <summary>CSV fields, with quoted values that may hold the separator, quotes ("") or line breaks.</summary>
    private static List<List<string>> ParseCsv(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
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
