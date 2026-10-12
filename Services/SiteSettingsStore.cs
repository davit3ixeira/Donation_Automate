using System.Globalization;
using System.Text.Json;

namespace DonationAutomate.Services;

/// <summary>
/// Campaign settings edited on the admin page. The Pix key and the delivery address are personal
/// data, so they live here (data/site.json, not in Git) rather than in the code or templates.
/// </summary>
public sealed class SiteSettings
{
    /// <summary>Deadline for delivering kits (and Pix), shown on the home page and in the emails.</summary>
    public DateOnly KitsDeadline { get; set; } = new(2026, 12, 1);
    public DateOnly Festa { get; set; } = new(2026, 12, 1);

    /// <summary>Average cost of a kit in reais, shown to donors, and the suggested Pix per child.</summary>
    public int PixValue { get; set; } = 300;

    public string PixKey { get; set; } = "";

    /// <summary>Where donors deliver the kits.</summary>
    public string DeliveryAddress { get; set; } = "";
}

/// <summary>Editable campaign settings, kept in data/site.json.</summary>
public sealed class SiteSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();

    public SiteSettingsStore(IConfiguration config, IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "site.json");
    }

    public SiteSettings Get()
    {
        lock (_gate) return Load();
    }

    private SiteSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<SiteSettings>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <param name="kitsDeadline">An HTML date input value (yyyy-MM-dd), or null/empty to keep the current one.</param>
    /// <param name="festa">Same format as <paramref name="kitsDeadline"/>.</param>
    /// <param name="pixValue">Whole reais; anything else keeps the current value.</param>
    public void Save(string? kitsDeadline, string? festa, string? pixValue, string? pixKey, string? deliveryAddress)
    {
        lock (_gate)
        {
            var settings = Load();
            if (DateOnly.TryParseExact(kitsDeadline, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var kits))
                settings.KitsDeadline = kits;
            if (DateOnly.TryParseExact(festa, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var party))
                settings.Festa = party;
            if (int.TryParse(pixValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
                settings.PixValue = value;
            settings.PixKey = (pixKey ?? "").Trim();
            settings.DeliveryAddress = (deliveryAddress ?? "").Trim();
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
        }
    }

    public static string Money(int reais) => reais.ToString("N0", new CultureInfo("pt-BR"));
}
