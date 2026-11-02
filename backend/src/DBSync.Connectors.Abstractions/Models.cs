namespace DBSync.Connectors.Abstractions;

public sealed record ConnectionProfile(
    string DbType, string Host, int Port, string Database, string Username, string Password,
    bool SslEnabled = false, IReadOnlyDictionary<string, string>? Options = null, int CommandTimeoutSeconds = 60);

public sealed record ConnectionTestResult(bool Success, string Message, string? ServerVersion = null);

public sealed record DatabaseSchema(string Name);

public sealed record TableMetadata(string Schema, string Name, long? ApproxRows);

public static class LogicalTypes
{
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Float = "float";
    public const string Boolean = "boolean";
    public const string Text = "text";
    public const string Date = "date";
    public const string Time = "time";
    public const string Timestamp = "timestamp";
    public const string TimestampTz = "timestamptz";
    public const string Binary = "binary";
    public const string Json = "json";
    public const string Uuid = "uuid";
    public const string Other = "other";
}

public sealed record ColumnInfo(
    string Name, string NativeType, string LogicalType, bool IsNullable, bool IsPrimaryKey, int Ordinal,
    int? MaxLength = null, int? Precision = null, int? Scale = null, bool IsIdentity = false, bool IsGenerated = false,
    bool IsUnsigned = false, string? DefaultValue = null);

/// <summary>Mengurai nilai DEFAULT kolom yang berupa literal sederhana (angka, teks, true/false). Fungsi/ekspresi (now(), nextval(), ...) dianggap tidak didukung (null).</summary>
public static class SqlDefaults
{
    public static string? ParseLiteral(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        while (s.Length > 1 && s[0] == '(' && s[^1] == ')') s = s[1..^1].Trim();
        if (s.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        if (s.StartsWith('\'')) // 'teks' atau 'teks'::tipe
        {
            var sb = new System.Text.StringBuilder(); int i = 1;
            for (; i < s.Length; i++)
            {
                if (s[i] == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; continue; } break; }
                sb.Append(s[i]);
            }
            var rest = i + 1 < s.Length ? s[(i + 1)..].Trim() : "";
            return rest.Length == 0 || rest.StartsWith("::") ? sb.ToString() : null;
        }
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return s.ToLowerInvariant();
        return decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? s : null;
    }
}

public sealed record ForeignKeyInfo(string Name, IReadOnlyList<string> Columns, string RefSchema, string RefTable, IReadOnlyList<string> RefColumns);

public sealed record TableSchema(string Schema, string Name, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<ForeignKeyInfo> ForeignKeys)
{
    public IReadOnlyList<ColumnInfo> PrimaryKey => Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).ToList();
    public ColumnInfo? Find(string column) => Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Query baca. After = nilai primary key terakhir (keyset pagination). KeyEquals = filter satu baris.</summary>
public sealed record ReadQuery(
    string Schema, string Table, IReadOnlyList<string> Columns, IReadOnlyList<string> KeyColumns,
    IReadOnlyList<object?>? After = null, IReadOnlyList<object?>? KeyEquals = null, int? Limit = null);

public enum ChangeKind { Upsert, Insert, Delete }

/// <summary>Satu operasi tulis. Values = seluruh kolom yang ditulis, Key = nilai primary key.</summary>
public sealed record ChangeOperation(
    ChangeKind Kind, TableSchema Target, IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?>? Values);

public sealed record WriteResult(int Applied);

public sealed record CdcCheck(string Name, bool Ok, string Detail);

public enum ErrorClass { Transient, Permanent, Configuration, Conflict }

public class ConnectorException : Exception
{
    public ErrorClass Class { get; }
    public string Code { get; }
    public ConnectorException(ErrorClass cls, string code, string message, Exception? inner = null) : base(message, inner)
    { Class = cls; Code = code; }
}

public interface IDatabaseConnector
{
    string DatabaseType { get; }

    Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct);
    /// <summary>Daftar database yang bisa diakses user pada server ini (tanpa perlu memilih database lebih dulu).</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct);
    Task<IReadOnlyList<DatabaseSchema>> GetSchemasAsync(ConnectionProfile profile, CancellationToken ct);
    Task<IReadOnlyList<TableMetadata>> GetTablesAsync(ConnectionProfile profile, string? schema, CancellationToken ct);
    Task<TableSchema> GetTableSchemaAsync(ConnectionProfile profile, string schema, string table, CancellationToken ct);
    Task<long> CountRowsAsync(ConnectionProfile profile, string schema, string table, CancellationToken ct);
    IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(ConnectionProfile profile, ReadQuery query, CancellationToken ct);
    Task<WriteResult> ApplyChangesAsync(ConnectionProfile profile, IReadOnlyList<ChangeOperation> operations, CancellationToken ct);

    /// <summary>Pemeriksaan prasyarat Debezium/CDC untuk vendor ini.</summary>
    Task<IReadOnlyList<CdcCheck>> CheckCdcPrerequisitesAsync(ConnectionProfile profile, IReadOnlyList<(string Schema, string Table)> tables, CancellationToken ct);

    /// <summary>Konfigurasi connector Debezium (Kafka Connect) untuk endpoint ini.</summary>
    IDictionary<string, string> BuildDebeziumConfig(ConnectionProfile profile, string topicPrefix, IReadOnlyList<(string Schema, string Table)> tables, string kafkaBootstrap, string connectorName);
}

public interface IConnectorRegistry
{
    IDatabaseConnector Get(string dbType);
    IEnumerable<string> SupportedTypes { get; }
}

public sealed class ConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<string, IDatabaseConnector> _map;
    public ConnectorRegistry(IEnumerable<IDatabaseConnector> connectors) =>
        _map = connectors.ToDictionary(c => c.DatabaseType, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> SupportedTypes => _map.Keys;

    public IDatabaseConnector Get(string dbType) =>
        _map.TryGetValue(dbType, out var c)
            ? c
            : throw new ConnectorException(ErrorClass.Configuration, "UNSUPPORTED_DB_TYPE", $"Tipe database '{dbType}' tidak didukung.");
}
