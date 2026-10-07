using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace DonationAutomate.Services;

public sealed record EmailTemplate(string Subject, string Body);

/// <summary>The editable email templates. Campaign ones go to the whole donor list; Approval goes to one donor.</summary>
public static class TemplateKeys
{
    public const string Invite = "convite";
    public const string Progress = "progresso";
    public const string Final = "final";
    public const string Approval = "aprovacao";

    public static readonly IReadOnlyList<string> Campaign = [Invite, Progress, Final];
    public static readonly IReadOnlyList<string> All = [Invite, Progress, Final, Approval];

    public static string Label(string key) => key switch
    {
        Invite => "Convite",
        Progress => "Progresso",
        Final => "Agradecimento final",
        Approval => "Aprovação do pedido",
        _ => key,
    };

    public static string Description(string key) => key switch
    {
        Invite => "No início da campanha, convidando a apadrinhar.",
        Progress => "No meio da campanha, com quantas crianças ainda estão disponíveis.",
        Final => "No fim da campanha, agradecendo a todos.",
        Approval => "Enviado automaticamente ao doador quando um pedido passa de Pendente para Aprovado.",
        _ => "",
    };

    /// <summary>Placeholders the admin can use, with what they turn into.</summary>
    public static IReadOnlyList<(string Name, string Meaning)> Placeholders(string key) => key == Approval
        ?
        [
            ("{nome}", "nome do doador"), ("{crianca}", "nome da criança"), ("{turma}", "turma"),
            ("{faixa}", "faixa etária"), ("{idade}", "idade"), ("{calcado}", "calçado"), ("{roupa}", "roupa"),
            ("{id}", "nº da criança"), ("{link}", "endereço do site"),
        ]
        :
        [
            ("{nome}", "nome do doador"), ("{link}", "endereço do site"), ("{total}", "total de crianças"),
            ("{disponiveis}", "crianças disponíveis"), ("{percentual_disponivel}", "% disponíveis"),
            ("{apadrinhadas}", "crianças já escolhidas"), ("{percentual_apadrinhado}", "% escolhidas"),
            ("{aprovadas}", "pedidos aprovados"),
        ];

    public static readonly IReadOnlyDictionary<string, EmailTemplate> Defaults = new Dictionary<string, EmailTemplate>
    {
        [Invite] = new(
            "Natal solidário: apadrinhe uma criança",
            """
            Olá, {nome}!

            Estamos começando a nossa campanha de Natal e gostaríamos de convidar você a apadrinhar uma ou mais crianças. São {total} crianças esperando um padrinho ou madrinha.

            Para escolher, é só acessar: {link}

            Muito obrigado!
            """),
        [Progress] = new(
            "Ainda dá tempo: {percentual_disponivel} das crianças esperam um padrinho",
            """
            Olá, {nome}!

            A campanha de Natal continua: {disponiveis} das {total} crianças ({percentual_disponivel}) ainda estão disponíveis para receber doação.

            Se puder ajudar, escolha uma criança em: {link}

            Obrigado!
            """),
        [Final] = new(
            "Obrigado por fazer parte do nosso Natal",
            """
            Olá, {nome}!

            A nossa campanha de Natal chegou ao fim, e {apadrinhadas} crianças ganharam um padrinho ou madrinha. Nada disso seria possível sem você.

            Muito obrigado, e boas festas!
            """),
        [Approval] = new(
            "Pedido aprovado: {crianca}",
            """
            Olá, {nome}!

            Seu pedido para apadrinhar {crianca} ({turma}, {idade}) foi aprovado. Muito obrigado pelo carinho!

            Para ajudar na escolha do presente: calçado {calcado}, roupa {roupa}.

            Com carinho,
            """),
    };
}

public sealed class SmtpSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    /// <summary>Encrypted with ASP.NET Data Protection; never stored in plain text.</summary>
    public string? ProtectedPassword { get; set; }
    public string? FromName { get; set; }
    public string? FromAddress { get; set; }

    /// <summary>Without a server and sender, emails are saved to the outbox folder instead of sent.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class EmailSettings
{
    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>Address put in {link}. Empty means "the address the admin is using", which may be localhost.</summary>
    public string? SiteUrl { get; set; }

    public Dictionary<string, EmailTemplate> Templates { get; set; } = new();

    public EmailTemplate Template(string key) =>
        Templates.TryGetValue(key, out var t) ? t : TemplateKeys.Defaults[key];
}

/// <summary>Email server settings and templates, kept in data/email.json.</summary>
public sealed class EmailSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly Lock _gate = new();

    public EmailSettingsStore(IConfiguration config, IWebHostEnvironment env, IDataProtectionProvider protection)
    {
        var dir = Path.Combine(env.ContentRootPath, config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "email.json");
        OutboxPath = Path.Combine(dir, "outbox");
        _protector = protection.CreateProtector("DonationAutomate.SmtpPassword");
    }

    /// <summary>Where emails are saved (as .eml files) while no email server is configured.</summary>
    public string OutboxPath { get; }

    public EmailSettings Get()
    {
        lock (_gate) return Load();
    }

    private EmailSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<EmailSettings>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void SaveTemplates(IReadOnlyDictionary<string, EmailTemplate> templates) =>
        Update(s =>
        {
            foreach (var (key, template) in templates)
                if (TemplateKeys.All.Contains(key))
                    s.Templates[key] = template;
        });

    /// <param name="password">New password, or null/empty to keep the current one.</param>
    public void SaveServer(string? host, int port, string? user, string? password, string? fromName, string? fromAddress, string? siteUrl) =>
        Update(s =>
        {
            s.Smtp.Host = Clean(host);
            s.Smtp.Port = port is > 0 and < 65536 ? port : 587;
            s.Smtp.User = Clean(user);
            if (!string.IsNullOrEmpty(password))
                s.Smtp.ProtectedPassword = _protector.Protect(password);
            s.Smtp.FromName = Clean(fromName);
            s.Smtp.FromAddress = Clean(fromAddress);
            s.SiteUrl = Clean(siteUrl);
        });

    public void ClearPassword() => Update(s => s.Smtp.ProtectedPassword = null);

    /// <summary>The SMTP password, or null if none is saved or it can no longer be decrypted.</summary>
    public string? Password(EmailSettings settings)
    {
        if (settings.Smtp.ProtectedPassword is not { } protectedPassword) return null;
        try
        {
            return _protector.Unprotect(protectedPassword);
        }
        catch (CryptographicException)
        {
            // Encryption keys lost (e.g. moved to another computer): the admin must re-enter it.
            return null;
        }
    }

    private void Update(Action<EmailSettings> change)
    {
        lock (_gate)
        {
            var settings = Load();
            change(settings);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
