using System.Data.Common;
using DBSync.Connectors.Abstractions;
using MySqlConnector;

namespace DBSync.Connectors.MySql;

public sealed class MySqlDatabaseConnector : AdoNetConnectorBase
{
    public override string DatabaseType => "MYSQL";
    protected override string ServerVersionSql => "SELECT VERSION()";
    protected override string Quote(string id) => "`" + id.Replace("`", "``") + "`";

    protected override DbConnection CreateConnection(ConnectionProfile p)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = p.Host, Port = (uint)p.Port, Database = p.Database, UserID = p.Username, Password = p.Password,
            SslMode = p.SslEnabled ? MySqlSslMode.Required : MySqlSslMode.Preferred,
            ConvertZeroDateTime = true, AllowZeroDateTime = false, CharacterSet = "utf8mb4",
            DefaultCommandTimeout = (uint)p.CommandTimeoutSeconds, ConnectionTimeout = 15,
        };
        return new MySqlConnection(b.ConnectionString);
    }

    protected override async Task<List<string>> QueryDatabasesAsync(DbConnection c, ConnectionProfile p, CancellationToken ct)
    {
        var system = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "information_schema", "mysql", "performance_schema", "sys" };
        var rows = await QueryAsync(c, p, "SHOW DATABASES", ct);
        return rows.Select(r => r.Values.First()!.ToString()!).Where(n => !system.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    protected override async Task<List<string>> QuerySchemasAsync(DbConnection c, ConnectionProfile p, CancellationToken ct)
        => new() { p.Database }; // di MySQL, schema == database

    protected override async Task<List<TableMetadata>> QueryTablesAsync(DbConnection c, ConnectionProfile p, string? schema, CancellationToken ct)
    {
        var rows = await QueryAsync(c, p,
            "SELECT TABLE_NAME AS n, TABLE_ROWS AS r FROM information_schema.TABLES WHERE TABLE_SCHEMA=@s AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME",
            ct, ("s", schema ?? p.Database));
        return rows.Select(r => new TableMetadata(schema ?? p.Database, r["n"]!.ToString()!, r["r"] is null ? null : Convert.ToInt64(r["r"]))).ToList();
    }

    protected override async Task<TableSchema> QueryTableSchemaAsync(DbConnection c, ConnectionProfile p, string schema, string table, CancellationToken ct)
    {
        var cols = await QueryAsync(c, p,
            @"SELECT COLUMN_NAME AS name, DATA_TYPE AS dt, COLUMN_TYPE AS ct, IS_NULLABLE AS nullable, COLUMN_KEY AS ck, EXTRA AS extra,
                     ORDINAL_POSITION AS ord, CHARACTER_MAXIMUM_LENGTH AS len, NUMERIC_PRECISION AS prec, NUMERIC_SCALE AS scale
              FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t ORDER BY ORDINAL_POSITION",
            ct, ("s", schema), ("t", table));
        if (cols.Count == 0) throw new ConnectorException(ErrorClass.Configuration, "TABLE_NOT_FOUND", $"Tabel {schema}.{table} tidak ditemukan.");

        var columns = cols.Select(r =>
        {
            var dt = r["dt"]!.ToString()!.ToLowerInvariant();
            var ct2 = r["ct"]!.ToString()!.ToLowerInvariant();
            var extra = (r["extra"]?.ToString() ?? "").ToLowerInvariant();
            return new ColumnInfo(
                r["name"]!.ToString()!, ct2, MapLogical(dt, ct2),
                r["nullable"]!.ToString() == "YES", r["ck"]?.ToString() == "PRI", Convert.ToInt32(r["ord"]),
                r["len"] is null ? null : (int?)Math.Min(Convert.ToInt64(r["len"]), int.MaxValue),
                r["prec"] is null ? null : Convert.ToInt32(r["prec"]), r["scale"] is null ? null : Convert.ToInt32(r["scale"]),
                extra.Contains("auto_increment"), extra.Contains("generated"), ct2.Contains("unsigned"));
        }).ToList();

        var fkRows = await QueryAsync(c, p,
            @"SELECT CONSTRAINT_NAME AS cn, COLUMN_NAME AS col, REFERENCED_TABLE_SCHEMA AS rs, REFERENCED_TABLE_NAME AS rt, REFERENCED_COLUMN_NAME AS rc
              FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t AND REFERENCED_TABLE_NAME IS NOT NULL ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION",
            ct, ("s", schema), ("t", table));
        var fks = fkRows.GroupBy(r => r["cn"]!.ToString()!).Select(g =>
            new ForeignKeyInfo(g.Key, g.Select(x => x["col"]!.ToString()!).ToList(), g.First()["rs"]!.ToString()!, g.First()["rt"]!.ToString()!, g.Select(x => x["rc"]!.ToString()!).ToList())).ToList();

        return new TableSchema(schema, table, columns, fks);
    }

    private static string MapLogical(string dt, string columnType) => dt switch
    {
        "tinyint" when columnType.StartsWith("tinyint(1)") => LogicalTypes.Boolean,
        "bit" => LogicalTypes.Boolean,
        "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "year" => LogicalTypes.Integer,
        "decimal" or "numeric" => LogicalTypes.Decimal,
        "float" or "double" or "real" => LogicalTypes.Float,
        "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" => LogicalTypes.Text,
        "date" => LogicalTypes.Date,
        "time" => LogicalTypes.Time,
        "datetime" or "timestamp" => LogicalTypes.Timestamp,
        "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => LogicalTypes.Binary,
        "json" => LogicalTypes.Json,
        _ => LogicalTypes.Other,
    };

    protected override string BuildUpsertSql(TableSchema t, List<string> cols, List<string> pn)
    {
        var pkSet = t.PrimaryKey.Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var upd = cols.Where(x => !pkSet.Contains(x)).Select(x => $"{Quote(x)}=VALUES({Quote(x)})").ToList();
        if (upd.Count == 0) upd.Add($"{Quote(cols[0])}={Quote(cols[0])}");
        return $"INSERT INTO {Qualified(t.Schema, t.Name)} ({string.Join(", ", cols.Select(Quote))}) VALUES ({string.Join(", ", pn)}) ON DUPLICATE KEY UPDATE {string.Join(", ", upd)}";
    }

    protected override ErrorClass Classify(Exception ex, out string code)
    {
        code = "MYSQL_ERROR";
        if (ex is MySqlException m)
        {
            code = $"MYSQL_{(int)m.ErrorCode}";
            if (m.IsTransient) return ErrorClass.Transient;
            return (int)m.ErrorCode switch
            {
                1045 or 1044 or 1142 or 1143 => ErrorClass.Permanent,
                1049 or 2002 or 2003 => ErrorClass.Configuration,
                _ => ErrorClass.Permanent
            };
        }
        if (ex is TimeoutException || ex is OperationCanceledException) return ErrorClass.Transient;
        return ErrorClass.Permanent;
    }

    public override async Task<IReadOnlyList<CdcCheck>> CheckCdcPrerequisitesAsync(ConnectionProfile p, IReadOnlyList<(string Schema, string Table)> tables, CancellationToken ct)
    {
        var res = new List<CdcCheck>();
        try
        {
            await using var c = await OpenAsync(p, ct);
            async Task<string?> Var(string name) => (await QueryAsync(c, p, $"SHOW VARIABLES LIKE '{name}'", ct)).FirstOrDefault()?["Value"]?.ToString();
            var logBin = await Var("log_bin");
            res.Add(new("log_bin", logBin is "ON" or "1", $"log_bin={logBin} (harus ON)"));
            var fmt = await Var("binlog_format");
            res.Add(new("binlog_format", fmt == "ROW", $"binlog_format={fmt} (harus ROW)"));
            var img = await Var("binlog_row_image");
            res.Add(new("binlog_row_image", img is null or "FULL", $"binlog_row_image={img} (harus FULL)"));
            var grants = await QueryAsync(c, p, "SHOW GRANTS FOR CURRENT_USER()", ct);
            var text = string.Join(" ", grants.Select(g => g.Values.First()?.ToString())).ToUpperInvariant();
            bool all = text.Contains("ALL PRIVILEGES ON *.*");
            res.Add(new("privileges", all || (text.Contains("REPLICATION SLAVE") && text.Contains("REPLICATION CLIENT")),
                "User butuh SELECT, RELOAD, SHOW DATABASES, REPLICATION SLAVE, REPLICATION CLIENT"));
        }
        catch (Exception ex) { res.Add(new("connection", false, ex.Message)); }
        return res;
    }

    public override IDictionary<string, string> BuildDebeziumConfig(ConnectionProfile p, string prefix, IReadOnlyList<(string Schema, string Table)> tables, string kafka, string name)
    {
        var cfg = new Dictionary<string, string>
        {
            ["connector.class"] = "io.debezium.connector.mysql.MySqlConnector",
            ["database.hostname"] = p.Host, ["database.port"] = p.Port.ToString(), ["database.user"] = p.Username, ["database.password"] = p.Password,
            ["database.server.id"] = (Math.Abs(name.GetHashCode()) % 400000000 + 5400).ToString(),
            ["topic.prefix"] = prefix,
            ["database.include.list"] = p.Database,
            ["table.include.list"] = string.Join(",", tables.Select(t => $"{t.Schema}.{t.Table}")),
            ["schema.history.internal.kafka.bootstrap.servers"] = kafka,
            ["schema.history.internal.kafka.topic"] = $"schemahistory.{prefix}",
            ["include.schema.changes"] = "false",
        };
        return WithCdcEndpoint(cfg, p);
    }
}
