using System.Text.Json;

namespace DBSync.Application;

public interface ISecretProtector
{
    string CurrentKeyVersion { get; }
    string Protect(string plaintext);
    string Unprotect(string ciphertext, string keyVersion);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IAuditService
{
    Task WriteAsync(Guid? userId, string? username, string action, string resourceType, string? resourceId,
        object? before = null, object? after = null, string? ip = null, CancellationToken ct = default);
}

public sealed class SecurityOptions
{
    public const string Section = "Security";
    /// <summary>Dictionary versi kunci (mis. v1) -> base64 32 byte. Master key harus di luar repository.</summary>
    public Dictionary<string, string> MasterKeys { get; set; } = new();
    public string CurrentKeyVersion { get; set; } = "v1";
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "dbsync";
    public string Audience { get; set; } = "dbsync-ui";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 7;
}

public sealed class SeedOptions
{
    public const string Section = "Seed";
    public string AdminUsername { get; set; } = "admin";
    public string AdminEmail { get; set; } = "admin@dbsync.local";
    public string AdminPassword { get; set; } = "";
}

public sealed class CdcOptions
{
    public const string Section = "Cdc";
    public bool Enabled { get; set; } = false;
    public string KafkaBootstrap { get; set; } = "localhost:9092";
    /// <summary>Alamat Kafka sebagaimana dilihat oleh Kafka Connect (di dalam jaringan Docker).</summary>
    public string KafkaBootstrapForConnect { get; set; } = "kafka:9092";
    public string KafkaConnectUrl { get; set; } = "http://localhost:8083";
    public string ConsumerGroupId { get; set; } = "dbsync-cdc";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static JsonDocument ToDoc(object? o) => JsonSerializer.SerializeToDocument(o, Options);
}
