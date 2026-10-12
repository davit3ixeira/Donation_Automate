using System.Text.Json;
using System.Text.Json.Serialization;

namespace DonationAutomate.Services;

public sealed record EmailTemplate(string Subject, string Body);

/// <summary>
/// The editable email templates. Campaign ones go to the whole donor list; the others go to one
/// donor along a request: Received when they choose, Confirmation when the admin sends the
/// confirmation link, Approval once the choice is confirmed.
/// </summary>
public static class TemplateKeys
{
    public const string Invite = "convite";
    public const string Progress = "progresso";
    public const string Final = "final";
    public const string Received = "pedido";
    public const string Confirmation = "confirmacao";
    public const string Approval = "aprovacao";

    public static readonly IReadOnlyList<string> Campaign = [Invite, Progress, Final];
    public static readonly IReadOnlyList<string> Donor = [Received, Confirmation, Approval];
    public static readonly IReadOnlyList<string> All = [Invite, Progress, Final, Received, Confirmation, Approval];

    public static string Label(string key) => key switch
    {
        Invite => "Convite",
        Progress => "Progresso",
        Final => "Agradecimento final",
        Received => "Pedido recebido",
        Confirmation => "Confirme sua escolha",
        Approval => "Instruções de entrega",
        _ => key,
    };

    public static string Description(string key) => key switch
    {
        Invite => "No início da campanha, convidando a apadrinhar.",
        Progress => "No meio da campanha, com quantas crianças ainda estão disponíveis.",
        Final => "No fim da campanha, agradecendo a todos.",
        Received => "Enviado automaticamente ao padrinho assim que ele escolhe as crianças no site.",
        Confirmation => "Enviado pelo botão \"Enviar e-mail de confirmação\" do admin, com o link para o padrinho confirmar.",
        Approval => "Enviado quando o padrinho confirma pelo link (ou o admin confirma): como e até quando entregar o kit ou fazer o Pix.",
        _ => "",
    };

    /// <summary>Placeholders the admin can use, with what they turn into.</summary>
    public static IReadOnlyList<(string Name, string Meaning)> Placeholders(string key) => key switch
    {
        Received =>
        [
            ("{nome}", "nome do padrinho"), ("{criancas}", "lista das crianças escolhidas"),
            ("{quantidade}", "quantas crianças"), ("{doacao}", "kit ou Pix"), ("{prazo}", "data limite de entrega"),
        ],
        Confirmation =>
        [
            ("{nome}", "nome do padrinho"), ("{criancas}", "lista das crianças"), ("{quantidade}", "quantas crianças"),
            ("{doacao}", "kit ou Pix"), ("{prazo}", "data limite de entrega"), ("{link_confirmar}", "link para confirmar"),
        ],
        Approval =>
        [
            ("{nome}", "nome do padrinho"), ("{criancas}", "lista das crianças"), ("{quantidade}", "quantas crianças"),
            ("{doacao}", "kit ou Pix"), ("{prazo}", "data limite de entrega"),
            ("{instrucoes}", "como entregar: endereço do kit e/ou chave Pix"),
        ],
        _ =>
        [
            ("{nome}", "nome do doador"), ("{link}", "endereço do site"), ("{total}", "total de crianças"),
            ("{disponiveis}", "crianças disponíveis"), ("{percentual_disponivel}", "% disponíveis"),
            ("{apadrinhadas}", "crianças já escolhidas"), ("{percentual_apadrinhado}", "% escolhidas"),
            ("{aprovadas}", "pedidos aprovados"),
        ],
    };

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
        [Received] = new(
            "Recebemos sua escolha no Natal Solidário",
            """
            Olá, {nome}!

            Recebemos o seu pedido para apadrinhar:

            {criancas}

            Forma de doação: {doacao}.

            A nossa equipe vai conferir e, em breve, você recebe outro e-mail com um link para confirmar a sua escolha e saber os próximos passos. Se ele não aparecer, dê uma olhada na caixa de spam ou de promoções.

            Muito obrigado por fazer parte do nosso Natal!
            """),
        [Confirmation] = new(
            "Pedido aprovado: confirme sua escolha",
            """
            Olá, {nome}!

            O seu pedido foi aprovado! Estas são as crianças que você vai apadrinhar:

            {criancas}

            Para confirmar, abra o link abaixo e clique em "Confirmar":
            {link_confirmar}

            Assim que você confirmar, enviamos as instruções de entrega ({doacao}), que deve ser feita até {prazo}.

            Muito obrigado!
            """),
        [Approval] = new(
            "Tudo certo! Próximos passos do seu apadrinhamento",
            """
            Olá, {nome}!

            Sua escolha está confirmada. Muito obrigado pelo carinho!

            {criancas}

            {instrucoes}

            Qualquer dúvida, é só responder este e-mail.

            Com carinho,
            """),
    };
}

/// <summary>The "Smtp" configuration section.</summary>
public sealed class SmtpSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? FromName { get; set; }
    public string? FromAddress { get; set; }
    /// <summary>Where replies go. The sending domain receives no mail, so without it replies would bounce.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>Without a server and sender, emails are saved to the outbox folder instead of sent.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class EmailSettings
{
    /// <summary>From configuration, not saved in the file.</summary>
    [JsonIgnore]
    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>Address put in {link}, from configuration. Empty means "the address the admin is using", which may be localhost.</summary>
    [JsonIgnore]
    public string? SiteUrl { get; set; }

    public Dictionary<string, EmailTemplate> Templates { get; set; } = new();

    public EmailTemplate Template(string key) =>
        Templates.TryGetValue(key, out var t) ? t : TemplateKeys.Defaults[key];
}

/// <summary>
/// Email settings. The templates are edited on the page and kept in data/email.json. The server,
/// credentials, sender, reply-to and site address are internal: they come from configuration
/// (the "Smtp" section and "SiteUrl": environment variables such as Smtp__Host on the server,
/// appsettings.Local.json on a development machine) and are read on every use, so a changed
/// appsettings.Local.json applies without a restart.
/// </summary>
public sealed class EmailSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IConfiguration _config;
    private readonly string _path;
    private readonly Lock _gate = new();

    public EmailSettingsStore(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        var dir = Path.Combine(env.ContentRootPath, config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "email.json");
        OutboxPath = Path.Combine(dir, "outbox");
    }

    /// <summary>Where emails are saved (as .eml files) while no email server is configured.</summary>
    public string OutboxPath { get; }

    public EmailSettings Get()
    {
        lock (_gate)
        {
            var settings = Load();
            settings.Smtp = _config.GetSection("Smtp").Get<SmtpSettings>() ?? new();
            settings.SiteUrl = Clean(_config["SiteUrl"]);
            return settings;
        }
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
