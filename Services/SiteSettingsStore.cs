using System.Globalization;
using System.Text.Json;

namespace DonationAutomate.Services;

public sealed class SiteSettings
{
    public DateOnly KitsDeadline { get; set; } = new(2026, 12, 1);
    public DateOnly Festa { get; set; } = new(2026, 12, 1);
}

/// <summary>Editable site content (the calendar dates on the home page), kept in data/site.json.</summary>
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
    public void Save(string? kitsDeadline, string? festa)
    {
        lock (_gate)
        {
            var settings = Load();
            if (DateOnly.TryParseExact(kitsDeadline, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var kits))
                settings.KitsDeadline = kits;
            if (DateOnly.TryParseExact(festa, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var party))
                settings.Festa = party;
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
        }
    }
}
