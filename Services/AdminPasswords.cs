using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DonationAutomate.Services;

/// <summary>
/// The admin password. It is set through the site (first access, or "Esqueci a senha") and kept
/// only as a salted PBKDF2 hash in data/admin.json; no configuration file holds it. Changing it
/// changes <see cref="Stamp"/>, which logs out every session and forgets every trusted browser.
/// </summary>
public sealed class AdminPasswords
{
    public const int MinLength = 8;
    private const int Iterations = 600_000;
    private const string Scheme = "pbkdf2-sha256";

    private sealed record Stored(string Hash, DateTime UpdatedUtc);

    private readonly string _path;
    private readonly Lock _gate = new();
    private string? _hash;

    public AdminPasswords(IConfiguration config, IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "admin.json");
        _hash = Load(_path);
    }

    /// <summary>False until the admin creates a password through the emailed link.</summary>
    public bool IsSet
    {
        get { lock (_gate) return _hash is not null; }
    }

    /// <summary>Short fingerprint of the current password; empty while there is none.</summary>
    public string Stamp
    {
        get
        {
            lock (_gate)
                return _hash is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_hash)))[..16];
        }
    }

    public bool Verify(string? password)
    {
        string? hash;
        lock (_gate) hash = _hash;
        if (hash is null || string.IsNullOrEmpty(password)) return false;

        var parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>What is wrong with a new password, or null if it can be used.</summary>
    public static string? Problem(string? password, string? confirmation) =>
        (password ?? "").Length < MinLength ? $"A senha precisa ter pelo menos {MinLength} caracteres."
        : password != confirmation ? "As duas senhas digitadas não são iguais."
        : null;

    public void Set(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var hash = $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        lock (_gate)
        {
            var temp = _path + ".saving";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(hash, DateTime.UtcNow)));
            File.Move(temp, _path, overwrite: true);
            _hash = hash;
        }
    }

    private static string? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path))?.Hash : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
