# Donation_Automate
Online donation automation and management system

Built with ASP.NET Core Razor Pages (.NET 10) and [ClosedXML](https://github.com/ClosedXML/ClosedXML) for Excel files.

## Run

```
dotnet run
```

Then open http://localhost:5080.

- `/` — user page: lists the children whose status is **disponível**. Select any number, press **Enviar**, enter their name and email.
- `/Admin` — upload the children and emails spreadsheets, see every row, change statuses (e.g. pendente → aprovado), and download the current spreadsheet.
- `/Emails` — donors list, email templates and sending.

`/Admin` and `/Emails` require the admin login (see below): email and password, then, the first time in each browser, a 6-digit code emailed to `Admin:Email` (valid 10 minutes, 5 tries). That browser is then trusted for a year and only asks for email and password, and the login lasts a year while the site is used. **Sair** (logout) also forgets the browser, so the next login there asks for a code again. Without an email server (test mode), the code email is saved in `data/outbox`.

The admin password is not in any configuration file. On the login page, **Criar senha** (when there is none yet) or **Esqueci a senha** emails a link to `Admin:Email`, valid 30 minutes and only once, where the password is set (at least 8 characters). It is stored only as a salted PBKDF2 hash in `data/admin.json`. Setting a new password logs out every browser and forgets every trusted one. At most one link is emailed every 2 minutes.

The donor site is private when `SiteAccessKey` is set: it only opens through the link with the key (`https://doe-natal.com.br/?acesso=<key>`), which is what `{link}` in the emails contains and what `/Emails` shows for sharing. The browser remembers the key, so later visits work without it. Anyone else gets a page asking for the link. To cut off an old link, change the key (everyone then needs the new link). Search engines are told not to index the site.

## Configuration: login, email server and passwords

These settings are internal: they are not editable in the site, and they are never in the code or in Git. The app reads them from configuration:

| Setting | What it is | Required |
|---|---|---|
| `Admin:Email` | Admin login (an email address) | Yes. Without it nobody can log in. |
| `Admin:LoginCode` | `false` turns off the emailed login code (e.g. if email sending is broken) | No (default `true`) |
| `Smtp:Host` | SMTP server (Amazon SES São Paulo: `email-smtp.sa-east-1.amazonaws.com`) | To send* |
| `Smtp:Port` | SMTP port (`587`; `465` also works) | No (default `587`) |
| `Smtp:User` | SMTP username (Amazon SES: *Nome de usuário SMTP*, starts with `AKIA`) | To send |
| `Smtp:Password` | SMTP password (Amazon SES: *Senha SMTP*) | To send |
| `Smtp:FromName` | Sender name donors see in "From" | No |
| `Smtp:FromAddress` | Sender address. Must be on a domain verified in SES (`@doe-natal.com.br`) | To send* |
| `Smtp:ReplyTo` | Where replies go. Keep it the same as the ImprovMX alias below | Recommended |
| `SiteUrl` | Site address used in `{link}` in emails, e.g. `https://doe-natal.com.br/`. Empty: the address the admin is browsing | On the server |
| `SiteAccessKey` | Key in the donor link (letters and digits, e.g. 16 random characters). Empty: the site is open to anyone | Recommended |

\* Without `Smtp:Host` and `Smtp:FromAddress`, the app is in **test mode**: nothing is sent, emails are saved as `.eml` files in `data/outbox`.

**Incoming mail**: the app only sends. Mail written to the sender address (`papainoel@doe-natal.com.br`) is received by [ImprovMX](https://improvmx.com) (free plan, MX records at Registro.br) and forwarded according to the alias set in its panel. That alias is not read from this configuration: if `Smtp:ReplyTo` changes, change the alias too.

The SES credentials come from the `.csv` downloaded when they were created (SES → *Configurações SMTP* → *Credenciais SMTP do IAM*). The IAM user name (`ses-smtp-user...`) is not needed. The SMTP password is shown only once: if it is lost, create new credentials.

**On a development machine**: copy `appsettings.Local.example.json` to `appsettings.Local.json` (git-ignored) and fill it in. Changes to it apply without restarting the app.

**On the server**: set environment variables instead, with `__` (two underscores) in place of `:`:

```
Admin__Email=papainoel@doe-natal.com.br
Smtp__Host=email-smtp.sa-east-1.amazonaws.com
Smtp__Port=587
Smtp__User=AKIA...
Smtp__Password=...
Smtp__FromName=Natal Solidário · Creche Mundo Infantil
Smtp__FromAddress=papainoel@doe-natal.com.br
Smtp__ReplyTo=...
SiteUrl=https://doe-natal.com.br/
SiteAccessKey=...
```

Ask the project owner for the actual values; never commit them or paste them in issues or chats.

## Children spreadsheet

The table must have these 11 columns side by side, **in this order**. Columns are recognised by position, so the header names can be anything. The table does not have to start at A1: the system uses the first row (within the first 50, on the first sheet that has one) with 11 filled cells side by side as the header, so a title block or logo above it is fine. Other sheets are kept as they are.

`id, turma, faixa etária, nome da criança, idade, calçado, roupa, nome do doador, e-mail do doador, data do pedido, status`

Sheets in the older format, with a `telefone do doador` column between the donor's email and the request date, still work: that column is dropped when the file is read, and is gone from the file the next time it is saved.

- `id` must be filled in and unique for every child.
- `status` is `disponível`, `pendente` or `aprovado` (case and accents are ignored). A blank status counts as `disponível`.
- Uploads with fewer than 11 columns, a missing or repeated id, or an unrecognised status are rejected with a message saying what to fix. An unrecognised status usually means the columns are out of order.
- Status values are written capitalised (`Disponível`, `Pendente`, `Aprovado`).
- Pictures that share an internal id (Google Sheets exports do this) are renumbered automatically when the file is read.
- The user page shows only the first 7 columns (id to roupa).
- When a user sends children (giving name and email), they become `pendente` and the three donor columns are filled in.
- Setting a child back to `disponível` clears the three donor columns.
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
- Templates are saved in `data/email.json`. The email server, sender and reply-to address are not on the page: see *Configuration*.
- **Test mode**: until an SMTP server and sender are configured, nothing is sent. Emails are saved as `.eml` files in `data/outbox` instead.

## How the spreadsheets are stored

Each spreadsheet is uploaded through the site (**Enviar**) as an Excel file (`.xlsx`, also what Google Sheets downloads as Excel) or a CSV (comma, semicolon or tab separated; turned into `.xlsx`). The format is recognised by the content, not the file name, since phones often send files without an extension; `.ods`, old `.xls` and PDF files are refused with a message on how to export. It is stored in `data/` (configurable via `DataDirectory` in `appsettings.json`; this folder is git-ignored because it holds personal data). Later edits to the original file on the admin's computer are not seen: to get the current data, use **Baixar planilha atual**; to replace it, upload a new file.

The stored file is the source of truth:

- Every page load reads the file, and every change is written straight back into it.
- Open pages check for changes every 5 seconds and update their tables in place, without reloading (ticked checkboxes on the user page are kept).
- While the spreadsheet is open in Excel, the site can still read it but cannot save, and shows a message asking to close it.

## Production

The site runs at https://doe-natal.com.br on an Amazon Lightsail server (São Paulo, Ubuntu 24.04, static IP `54.94.141.39`; DNS at Registro.br).

- **Deploy**: every push to `main` runs `.github/workflows/deploy.yml`, which builds the app (self-contained, linux-x64) and sends it to the server, where `/usr/local/bin/doe-natal-deploy` swaps it in and restarts the service. If the new version doesn't start, the server goes back to the previous one and the run fails. It can also be started by hand in GitHub → *Actions* → *Deploy* → *Run workflow*.
- **GitHub secret** `DEPLOY_SSH_KEY`: private key whose public half is in the server's `~ubuntu/.ssh/authorized_keys`, restricted to running the deploy script only. If it leaks, remove that line on the server and create a new pair.
- **On the server**: the app is in `/opt/doe-natal/app` (the previous build in `app.old`), runs as the `doe-natal` systemd service (user `doenatal`, `127.0.0.1:5000`), and Caddy (`/etc/caddy/Caddyfile`) serves it over HTTPS with an automatic Let's Encrypt certificate. Data (spreadsheets, templates) is in `/var/lib/doe-natal/data` and is never touched by a deploy. Settings and passwords are environment variables in `/etc/doe-natal.env` (readable by root only): after changing it, run `sudo systemctl restart doe-natal`. `production.env.example` lists every variable; a filled-in copy may be kept in the project folder as `production.env` (git-ignored, like every `*.env`). Neither one changes the server, and a deploy never touches `/etc/doe-natal.env`.
- **Admin access to the server**: SSH as `ubuntu` with the Lightsail default key, or *Connect using SSH* in the Lightsail console. Logs: `sudo journalctl -u doe-natal -f`.
- **AWS account**: on the free plan (credits, about 6 months from October 2026). Upgrade it before it expires, or the account is closed and the site and emails stop.

## Not done yet

- Access control: there is a single admin account (see *Configuration*); no separate roles or per-person logins yet.
- Double confirmation of the email.
- The emails spreadsheet is stored and shown on the admin page but not used for anything else yet.
