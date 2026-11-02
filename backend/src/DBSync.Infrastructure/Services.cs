using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DBSync.Application;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DBSync.Infrastructure;

/// <summary>AES-256-GCM dengan versi kunci. Format: base64(nonce[12] | tag[16] | cipher).</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly SecurityOptions _opt;
    public AesGcmSecretProtector(IOptions<SecurityOptions> opt)
    {
        _opt = opt.Value;
        if (!_opt.MasterKeys.ContainsKey(_opt.CurrentKeyVersion))
            throw new InvalidOperationException($"Security:MasterKeys:{_opt.CurrentKeyVersion} belum dikonfigurasi. Lihat PANDUAN_SETUP.md.");
    }

    public string CurrentKeyVersion => _opt.CurrentKeyVersion;

    private byte[] Key(string version)
    {
        if (!_opt.MasterKeys.TryGetValue(version, out var b64)) throw new CryptographicException($"Master key versi {version} tidak tersedia.");
        var k = Convert.FromBase64String(b64);
        if (k.Length != 32) throw new CryptographicException("Master key harus 32 byte (base64).");
        return k;
    }

    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16]; var cipher = new byte[data.Length];
        using var aes = new AesGcm(Key(CurrentKeyVersion), 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public string Unprotect(string ciphertext, string keyVersion)
    {
        var all = Convert.FromBase64String(ciphertext);
        var nonce = all[..12]; var tag = all[12..28]; var cipher = all[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key(keyVersion), 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 210_000;
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(h)}";
    }

    public bool Verify(string password, string hash)
    {
        var p = hash.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2") return false;
        var salt = Convert.FromBase64String(p[2]); var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(p[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed class AuditService : IAuditService
{
    private readonly IServiceScopeFactory _sf;
    private static readonly string[] SensitiveKeys = { "password", "token", "secret", "encryptedpassword" };
    public AuditService(IServiceScopeFactory sf) => _sf = sf;

    public async Task WriteAsync(Guid? userId, string? username, string action, string resourceType, string? resourceId,
        object? before = null, object? after = null, string? ip = null, CancellationToken ct = default)
    {
        using var scope = _sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbSyncContext>();
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId, Username = username, Action = action, ResourceType = resourceType, ResourceId = resourceId,
            BeforeValue = Sanitize(before), AfterValue = Sanitize(after), IpAddress = ip,
        });
        await db.SaveChangesAsync(ct);
    }

    private static JsonDocument? Sanitize(object? o)
    {
        if (o is null) return null;
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(o, Json.Options));
        Mask(node);
        return JsonDocument.Parse(node!.ToJsonString());
    }

    private static void Mask(System.Text.Json.Nodes.JsonNode? n)
    {
        if (n is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var k in obj.Select(x => x.Key).ToList())
            {
                if (SensitiveKeys.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase))) obj[k] = "***";
                else Mask(obj[k]);
            }
        }
        else if (n is System.Text.Json.Nodes.JsonArray arr) foreach (var i in arr) Mask(i);
    }
}

public interface IConnectionProfileFactory
{
    ConnectionProfile Create(DbConnection c, int commandTimeoutSeconds = 60);
    Task<ConnectionProfile> CreateAsync(Guid connectionId, int commandTimeoutSeconds = 60, CancellationToken ct = default);
    /// <summary>Profil dari input mentah (belum disimpan) untuk test koneksi.</summary>
    ConnectionProfile FromRaw(string dbType, string host, int port, string db, string user, string password, bool ssl, IReadOnlyDictionary<string, string>? options);
}

public sealed class ConnectionProfileFactory : IConnectionProfileFactory
{
    private readonly ISecretProtector _protector;
    private readonly DbSyncContext _db;
    public ConnectionProfileFactory(ISecretProtector protector, DbSyncContext db) { _protector = protector; _db = db; }

    public ConnectionProfile Create(DbConnection c, int timeout = 60)
    {
        if (c.Secret is null) throw new InvalidOperationException("Secret koneksi tidak dimuat.");
        var pw = _protector.Unprotect(c.Secret.EncryptedPassword, c.Secret.EncryptionKeyVersion);
        IReadOnlyDictionary<string, string>? opts = null;
        if (c.ConnectionOptions != null)
            opts = JsonSerializer.Deserialize<Dictionary<string, string>>(c.ConnectionOptions.RootElement.GetRawText());
        return new ConnectionProfile(c.DbType, c.Host, c.Port, c.DatabaseName, c.Username, pw, c.SslEnabled, opts, timeout);
    }

    public async Task<ConnectionProfile> CreateAsync(Guid id, int timeout = 60, CancellationToken ct = default)
    {
        var c = await _db.DbConnections.Include(x => x.Secret).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException($"Koneksi {id} tidak ditemukan.");
        return Create(c, timeout);
    }

    public ConnectionProfile FromRaw(string dbType, string host, int port, string db, string user, string password, bool ssl, IReadOnlyDictionary<string, string>? options)
        => new(dbType, host, port, db, user, password, ssl, options);
}

public sealed class DatabaseSeeder
{
    private readonly DbSyncContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly SeedOptions _seed;
    private readonly ILogger<DatabaseSeeder> _log;
    public DatabaseSeeder(DbSyncContext db, IPasswordHasher hasher, IOptions<SeedOptions> seed, ILogger<DatabaseSeeder> log)
    { _db = db; _hasher = hasher; _seed = seed.Value; _log = log; }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _db.Database.EnsureCreatedAsync(ct);

        foreach (var r in Roles.All)
            if (!await _db.Roles.AnyAsync(x => x.Name == r, ct)) _db.Roles.Add(new Role { Name = r });
        await _db.SaveChangesAsync(ct);

        if (!await _db.Users.AnyAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(_seed.AdminPassword))
            { _log.LogWarning("Seed:AdminPassword kosong; user admin awal tidak dibuat."); return; }
            var role = await _db.Roles.FirstAsync(x => x.Name == Roles.SuperAdmin, ct);
            var u = new User { Username = _seed.AdminUsername, Email = _seed.AdminEmail, PasswordHash = _hasher.Hash(_seed.AdminPassword) };
            u.UserRoles.Add(new UserRole { RoleId = role.Id, UserId = u.Id });
            _db.Users.Add(u);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("User admin awal '{User}' dibuat. Segera ganti password.", u.Username);
        }
    }
}

public static class InfrastructureRegistration
{
    public static IServiceCollection AddDbSyncInfrastructure(this IServiceCollection s, IConfiguration cfg)
    {
        s.Configure<SecurityOptions>(cfg.GetSection(SecurityOptions.Section));
        s.Configure<JwtOptions>(cfg.GetSection(JwtOptions.Section));
        s.Configure<SeedOptions>(cfg.GetSection(SeedOptions.Section));
        s.Configure<CdcOptions>(cfg.GetSection(CdcOptions.Section));
        s.AddDbContext<DbSyncContext>(o => o.UseNpgsql(cfg.GetConnectionString("ConfigDb")));
        s.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        s.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        s.AddSingleton<IAuditService, AuditService>();
        s.AddScoped<IConnectionProfileFactory, ConnectionProfileFactory>();
        s.AddScoped<DatabaseSeeder>();
        s.AddSingleton<IConnectorRegistry>(sp => new ConnectorRegistry(sp.GetServices<IDatabaseConnector>()));
        return s;
    }
}
