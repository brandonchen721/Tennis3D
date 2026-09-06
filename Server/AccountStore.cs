using System.Security.Cryptography;
using System.Text.Json;

namespace Tennis3D.Server;

/// <summary>
/// File-backed account foundation for development and private servers.
/// Production deployments should replace this with a transactional database.
/// Passwords are never stored directly; PBKDF2-SHA256 with per-account salt is used.
/// </summary>
public sealed class AccountStore
{
    public sealed class Account
    {
        public required string UserName { get; init; }
        public required string Salt { get; init; }
        public required string PasswordHash { get; init; }
        public int Rating { get; set; } = 1200;
        public bool Banned { get; set; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    private readonly string path;
    private readonly object sync = new();
    private Dictionary<string, Account> accounts;

    public AccountStore(string path)
    {
        this.path = path;
        accounts = Load(path);
    }

    public bool Create(string userName, string password)
    {
        userName = Normalize(userName);
        if (userName.Length is < 3 or > 24 || password.Length < 10) return false;
        lock (sync)
        {
            if (accounts.ContainsKey(userName)) return false;
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            accounts[userName] = new Account
            {
                UserName = userName,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Hash(password, salt)
            };
            SaveLocked();
            return true;
        }
    }

    public bool Verify(string userName, string password, out Account? account)
    {
        userName = Normalize(userName);
        lock (sync)
        {
            if (!accounts.TryGetValue(userName, out account) || account.Banned) return false;
            byte[] salt = Convert.FromBase64String(account.Salt);
            byte[] expected = Convert.FromBase64String(account.PasswordHash);
            byte[] actual = Convert.FromBase64String(Hash(password, salt));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }

    public void Backup(string backupDirectory)
    {
        lock (sync)
        {
            Directory.CreateDirectory(backupDirectory);
            string target = Path.Combine(backupDirectory, $"accounts-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.Copy(path, target, true);
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static Dictionary<string, Account> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Account>>(File.ReadAllText(path)) ?? new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static string Hash(string password, byte[] salt) => Convert.ToBase64String(
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32));
}
