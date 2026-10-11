using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace DonationAutomate.Services;

public enum CodeCheck { Ok, Wrong, Expired }

/// <summary>
/// The codes emailed during the admin login, kept in memory for 10 minutes. Each login gets a
/// random ticket that the code form sends back; after 5 wrong codes the ticket is dropped and the
/// admin has to start over.
/// </summary>
public sealed class LoginCodes(IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 5;

    private sealed class Pending(string code)
    {
        public string Code { get; } = code;
        public int Attempts { get; set; }
    }

    /// <returns>The ticket identifying this login, and the code to email.</returns>
    public (string Ticket, string Code) Create()
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        cache.Set(CacheKey(ticket), new Pending(code), Lifetime);
        return (ticket, code);
    }

    public CodeCheck Check(string? ticket, string? code)
    {
        if (string.IsNullOrEmpty(ticket) || !cache.TryGetValue(CacheKey(ticket), out Pending? pending) || pending is null)
            return CodeCheck.Expired;

        lock (pending)
        {
            var given = new string((code ?? "").Where(char.IsDigit).ToArray());
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(pending.Code)))
            {
                cache.Remove(CacheKey(ticket));
                return CodeCheck.Ok;
            }
            if (++pending.Attempts >= MaxAttempts)
            {
                cache.Remove(CacheKey(ticket));
                return CodeCheck.Expired;
            }
            return CodeCheck.Wrong;
        }
    }

    private static string CacheKey(string ticket) => $"login-code:{ticket}";
}
