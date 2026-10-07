# Donation_Automate
Online donation automation and management system

Built with ASP.NET Core Razor Pages (.NET 10) and [ClosedXML](https://github.com/ClosedXML/ClosedXML) for Excel files.

## Run

```
dotnet run
```

Then open http://localhost:5080.

- `/` — user page: lists the children whose status is **disponível**. Select any number, press **Enviar**, enter an email (required) and phone (optional).
- `/Admin` — upload the children and emails spreadsheets, see every row, change statuses (e.g. pendente → aprovado), and download the current spreadsheet.

## Children spreadsheet

The table must have these 12 columns side by side, **in this order**. Columns are recognised by position, so the header names can be anything. The table does not have to start at A1: the system uses the first row (within the first 50, on the first sheet that has one) with 12 filled cells side by side as the header, so a title block or logo above it is fine. Other sheets are kept as they are.

`id, turma, faixa etária, nome da criança, idade, calçado, roupa, nome do doador, e-mail do doador, telefone do doador, data do pedido, status`

- `id` must be filled in and unique for every child.
- `status` is `disponível`, `pendente` or `aprovado` (case and accents are ignored). A blank status counts as `disponível`.
- Uploads with fewer than 12 columns, a missing or repeated id, or an unrecognised status are rejected with a message saying what to fix. An unrecognised status usually means the columns are out of order.
- Status values are written capitalised (`Disponível`, `Pendente`, `Aprovado`).
- Pictures that share an internal id (Google Sheets exports do this) are renumbered automatically when the file is read.
- The user page shows only the first 7 columns (id to roupa).
- When a user sends children (giving name, email and optionally phone), they become `pendente` and the four donor columns are filled in.
- Setting a child back to `disponível` clears the four donor columns.
- **Kit entregue**: approved children get a switch in the first column of the admin table. The first time one is ticked, the system appends a `kit entregue` column to the sheet (value `Sim`). Moving a child out of `aprovado` clears it.
- On the admin page every cell can be edited in place (Enter saves, Esc cancels), and children can be added or deleted. Each change is written to the spreadsheet immediately.

## Donors and emails (`/Emails`)

The donors spreadsheet has `nome, email` side by side (by position, header names can vary). The system appends `origem` and `último e-mail` and keeps them up to date.

- The `email` cell can hold more than one address, separated by `;` or `,` (e.g. `a@x.com; b@y.com`): they count as one donor on the list, but each address gets its own message when something is sent.

- When someone requests children on the user page and their email isn't in the donors sheet, they are added with `origem` = `Site`.
- The donor table shows each donor's situation, computed from the children sheet: how many children await approval, how many were approved (hover for names).
- **Enviar para todos**: pick one of three templates (Convite, Progresso, Agradecimento final) and an audience (everyone, those who haven't donated, those who have). A preview shows the text with real numbers.
- **✉** next to a donor: a free-text email to that person only.
- **Aprovação**: when a child goes from Pendente to Aprovado on the admin page, the donor gets the approval template automatically. Status changes made directly in the Excel file don't send it.
- All four templates are editable on the page. Placeholders such as `{nome}`, `{link}`, `{percentual_disponivel}` or `{crianca}` are filled in per recipient.
- Templates and the email server settings are saved in `data/email.json`; the SMTP password is encrypted with ASP.NET Data Protection (keys stay on this computer).
- **Test mode**: until an SMTP server and sender are configured, nothing is sent. Emails are saved as `.eml` files in `data/outbox` instead.

## How the spreadsheets are stored

Each spreadsheet is either **linked** to a file on this computer or an **uploaded copy**:

- **Linked** (recommended while the system runs on this computer): on the admin page, paste the file's full path (Explorer: right-click → *Copiar como caminho*) and press **Vincular**. The system then reads and writes that very file, so editing it in Excel shows up in the site, and changes made in the site are saved into it. The link is kept in `data/settings.json`. **Desvincular** switches back to a copy of the file's current data.
- **Uploaded copy**: stored in `data/` (configurable via `DataDirectory` in `appsettings.json`; this folder is git-ignored because it holds personal data). Later edits to the original file are not seen.

Either way, the file is the source of truth:

- Every page load reads the file, and every change is written straight back into it.
- Open pages check for changes every 5 seconds and update their tables in place, without reloading (ticked checkboxes on the user page are kept).
- While the spreadsheet is open in Excel, the site can still read it but cannot save, and shows a message asking to close it.

## Not done yet

- Access control: both pages are currently open to anyone who can reach the site. Login and admin/user roles come next. Until then, keep the site on this computer only: the admin page can link to (and so read and download) any `.xlsx` file on it.
- Double confirmation of email/phone.
- The emails spreadsheet is stored and shown on the admin page but not used for anything else yet.
