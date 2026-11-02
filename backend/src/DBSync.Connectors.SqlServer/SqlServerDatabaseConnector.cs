using System.Data.Common;
using DBSync.Connectors.Abstractions;
using Microsoft.Data.SqlClient;

namespace DBSync.Connectors.SqlServer;

public sealed class SqlServerDatabaseConnector : AdoNetConnectorBase
{
    public override string DatabaseType => "SQLSERVER";
    protected override string ServerVersionSql => "SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(50)) + ' ' + CAST(SERVERPROPERTY('Edition') AS varchar(100))";
    protected override bool UseTopForLimit => true;
    protected override string Quote(string id) => "[" + id.Replace("]", "]]") + "]";

    protected override DbConnection CreateConnection(ConnectionProfile p)
    {
        var host = p.Port is > 0 and not 1433 ? $"{p.Host},{p.Port}" : p.Host;
        var b = new SqlConnectionStringBuilder
        {
            DataSource = host, InitialCatalog = p.Database, UserID = p.Username, Password = p.Password,
            Encrypt = p.SslEnabled ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true, ConnectTimeout = 15, CommandTimeout = p.CommandTimeoutSeconds,
        };
        if (p.Options != null && p.Options.TryGetValue("trustServerCertificate", out var t)) b.TrustServerCertificate = t == "true";
        return new SqlConnection(b.ConnectionString);
    }

    protected override string[] MaintenanceDatabases => new[] { "master" };

    protected override async Task<List<string>> QueryDatabasesAsync(DbConnection c, ConnectionProfile p, CancellationToken ct)
    {
        var rows = await QueryAsync(c, p, "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", ct);
        return rows.Select(r => r["name"]!.ToString()!).ToList();
    }

    protected override async Task<List<string>> QuerySchemasAsync(DbConnection c, ConnectionProfile p, CancellationToken ct)
    {
        var rows = await QueryAsync(c, p, "SELECT name FROM sys.schemas WHERE name NOT IN ('sys','INFORMATION_SCHEMA','guest') AND name NOT LIKE 'db[_]%' AND name <> 'cdc' ORDER BY name", ct);
        return rows.Select(r => r["name"]!.ToString()!).ToList();
    }

    protected override async Task<List<TableMetadata>> QueryTablesAsync(DbConnection c, ConnectionProfile p, string? schema, CancellationToken ct)
    {
        var rows = await QueryAsync(c, p,
            @"SELECT s.name AS s, t.name AS t, (SELECT SUM(ps.rows) FROM sys.partitions ps WHERE ps.object_id=t.object_id AND ps.index_id IN (0,1)) AS r
              FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
              WHERE t.is_ms_shipped=0 AND s.name NOT IN ('cdc') AND (@s IS NULL OR s.name=@s) ORDER BY 1,2",
            ct, ("s", schema));
        return rows.Select(r => new TableMetadata(r["s"]!.ToString()!, r["t"]!.ToString()!, r["r"] is null ? null : Convert.ToInt64(r["r"]))).ToList();
    }

    protected override async Task<TableSchema> QueryTableSchemaAsync(DbConnection c, ConnectionProfile p, string schema, string table, CancellationToken ct)
    {
        var cols = await QueryAsync(c, p,
            @"SELECT c.name AS name, t.name AS tn, c.is_nullable AS nullable, c.is_identity AS ident, c.is_computed AS comp,
                     c.max_length AS len, c.precision AS prec, c.scale AS scale, c.column_id AS ord,
                     CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS pk, dc.definition AS def
              FROM sys.columns c
              LEFT JOIN sys.default_constraints dc ON dc.parent_object_id=c.object_id AND dc.parent_column_id=c.column_id
              JOIN sys.types t ON t.user_type_id=c.user_type_id
              JOIN sys.tables tb ON tb.object_id=c.object_id
              JOIN sys.schemas s ON s.schema_id=tb.schema_id
              LEFT JOIN (SELECT ic.object_id, ic.column_id FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id WHERE i.is_primary_key=1) pk
                     ON pk.object_id=c.object_id AND pk.column_id=c.column_id
              WHERE s.name=@s AND tb.name=@t ORDER BY c.column_id",
            ct, ("s", schema), ("t", table));
        if (cols.Count == 0) throw new ConnectorException(ErrorClass.Configuration, "TABLE_NOT_FOUND", $"Tabel {schema}.{table} tidak ditemukan.");

        var columns = cols.Select(r =>
        {
            var tn = r["tn"]!.ToString()!.ToLowerInvariant();
            int len = Convert.ToInt32(r["len"]);
            if (tn.StartsWith("n") && tn is "nvarchar" or "nchar" && len > 0) len /= 2;
            return new ColumnInfo(r["name"]!.ToString()!, tn, MapLogical(tn), Convert.ToBoolean(r["nullable"]), Convert.ToInt32(r["pk"]) == 1,
                Convert.ToInt32(r["ord"]), len, Convert.ToInt32(r["prec"]), Convert.ToInt32(r["scale"]),
                Convert.ToBoolean(r["ident"]), Convert.ToBoolean(r["comp"]) || tn is "timestamp" or "rowversion", false, SqlDefaults.ParseLiteral(r["def"]?.ToString()));
        }).ToList();

        var fkRows = await QueryAsync(c, p,
            @"SELECT fk.name AS cn, pc.name AS col, rs.name AS rs, rt.name AS rt, rc.name AS rcol
              FROM sys.foreign_keys fk
              JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
              JOIN sys.tables pt ON pt.object_id=fk.parent_object_id JOIN sys.schemas ps ON ps.schema_id=pt.schema_id
              JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id
              JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
              JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id
              WHERE ps.name=@s AND pt.name=@t ORDER BY fk.name, fkc.constraint_column_id",
            ct, ("s", schema), ("t", table));
        var fks = fkRows.GroupBy(r => r["cn"]!.ToString()!).Select(g =>
            new ForeignKeyInfo(g.Key, g.Select(x => x["col"]!.ToString()!).ToList(), g.First()["rs"]!.ToString()!, g.First()["rt"]!.ToString()!, g.Select(x => x["rcol"]!.ToString()!).ToList())).ToList();
        return new TableSchema(schema, table, columns, fks);
    }

    private static string MapLogical(string tn) => tn switch
    {
        "tinyint" or "smallint" or "int" or "bigint" => LogicalTypes.Integer,
        "decimal" or "numeric" or "money" or "smallmoney" => LogicalTypes.Decimal,
        "float" or "real" => LogicalTypes.Float,
        "bit" => LogicalTypes.Boolean,
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => LogicalTypes.Text,
        "date" => LogicalTypes.Date,
        "time" => LogicalTypes.Time,
        "datetime" or "datetime2" or "smalldatetime" => LogicalTypes.Timestamp,
        "datetimeoffset" => LogicalTypes.TimestampTz,
        "binary" or "varbinary" or "image" => LogicalTypes.Binary,
        "uniqueidentifier" => LogicalTypes.Uuid,
        _ => LogicalTypes.Other, // xml, geography, hierarchyid, sql_variant, ...
    };

    protected override void ConfigureParameter(DbParameter prm, ColumnInfo? col, object? value)
    {
        if (col is null) return;
        // NULL tanpa tipe dikirim sebagai nvarchar -> gagal untuk varbinary/uniqueidentifier/dll. Tetapkan tipe dari metadata kolom.
        if (value is null && prm is SqlParameter sp)
        {
            switch (col.NativeType)
            {
                case "varbinary" or "binary" or "image" or "timestamp" or "rowversion": sp.SqlDbType = System.Data.SqlDbType.VarBinary; sp.Size = -1; break;
                case "uniqueidentifier": sp.SqlDbType = System.Data.SqlDbType.UniqueIdentifier; break;
                case "bit": sp.SqlDbType = System.Data.SqlDbType.Bit; break;
                case "int": sp.SqlDbType = System.Data.SqlDbType.Int; break;
                case "bigint": sp.SqlDbType = System.Data.SqlDbType.BigInt; break;
                case "smallint": sp.SqlDbType = System.Data.SqlDbType.SmallInt; break;
                case "tinyint": sp.SqlDbType = System.Data.SqlDbType.TinyInt; break;
                case "decimal" or "numeric" or "money" or "smallmoney": sp.SqlDbType = System.Data.SqlDbType.Decimal; break;
                case "float": sp.SqlDbType = System.Data.SqlDbType.Float; break;
                case "real": sp.SqlDbType = System.Data.SqlDbType.Real; break;
                case "date": sp.SqlDbType = System.Data.SqlDbType.Date; break;
                case "datetime": sp.SqlDbType = System.Data.SqlDbType.DateTime; break;
                case "smalldatetime": sp.SqlDbType = System.Data.SqlDbType.SmallDateTime; break;
                case "datetime2": sp.SqlDbType = System.Data.SqlDbType.DateTime2; break;
                case "datetimeoffset": sp.SqlDbType = System.Data.SqlDbType.DateTimeOffset; break;
                case "time": sp.SqlDbType = System.Data.SqlDbType.Time; break;
            }
            return;
        }
        if (col.NativeType == "datetimeoffset" && value is DateTime dt)
            prm.Value = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        if (col.NativeType == "time" && value is DateTime d2) prm.Value = d2.TimeOfDay;
    }

    protected override string BuildUpsertSql(TableSchema t, List<string> cols, List<string> pn)
    {
        var q = Qualified(t.Schema, t.Name);
        var pk = t.PrimaryKey.Select(k => k.Name).ToList();
        var pkSet = pk.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // nilai PK diambil dari parameter yang sama dengan kolom
        string PkCond() => string.Join(" AND ", pk.Select(k => $"{Quote(k)} = {pn[cols.FindIndex(x => x.Equals(k, StringComparison.OrdinalIgnoreCase))]}"));
        var insert = $"INSERT INTO {q} ({string.Join(", ", cols.Select(Quote))}) VALUES ({string.Join(", ", pn)})";
        var set = cols.Select((x, i) => (x, i)).Where(z => !pkSet.Contains(z.x)).Select(z => $"{Quote(z.x)} = {pn[z.i]}").ToList();
        if (set.Count == 0) return $"IF NOT EXISTS (SELECT 1 FROM {q} WITH (UPDLOCK, SERIALIZABLE) WHERE {PkCond()}) {insert};";
        return $"UPDATE {q} WITH (UPDLOCK, SERIALIZABLE) SET {string.Join(", ", set)} WHERE {PkCond()}; IF @@ROWCOUNT = 0 {insert};";
    }

    protected override string WrapInsertIdentity(TableSchema t, IEnumerable<string> cols, string sql)
    {
        if (!cols.Any(x => t.Find(x)?.IsIdentity == true)) return sql;
        var q = Qualified(t.Schema, t.Name);
        return $"SET IDENTITY_INSERT {q} ON; {sql} SET IDENTITY_INSERT {q} OFF;";
    }

    protected override ErrorClass Classify(Exception ex, out string code)
    {
        code = "MSSQL_ERROR";
        if (ex is SqlException s)
        {
            code = "MSSQL_" + s.Number;
            if (s.IsTransient) return ErrorClass.Transient;
            return s.Number switch
            {
                18456 or 4060 or 53 or 40 => ErrorClass.Configuration,
                1205 or -2 => ErrorClass.Transient,
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
            var dbCdc = await ScalarAsync(c, p, "SELECT is_cdc_enabled FROM sys.databases WHERE name = DB_NAME()", ct);
            res.Add(new("database_cdc", Convert.ToBoolean(dbCdc ?? false), "Jalankan EXEC sys.sp_cdc_enable_db"));
            // sys.dm_server_services butuh izin tingkat server (VIEW SERVER PERFORMANCE STATE). User DBSync berhak minimum
            // tidak punya izin itu: jangan gagalkan semua pemeriksaan, cukup tandai "tidak dapat dipastikan".
            try
            {
                var agent = await QueryAsync(c, p, "SELECT status_desc FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server Agent%'", ct);
                res.Add(new("sql_server_agent", agent.Count > 0 && agent[0]["status_desc"]?.ToString() == "Running", "SQL Server Agent harus Running untuk capture job"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                res.Add(new("sql_server_agent", true, "Tidak dapat dipastikan dengan izin user ini (butuh VIEW SERVER PERFORMANCE STATE). Pastikan SQL Server Agent berjalan; tanpanya capture job CDC tidak berjalan."));
            }
            foreach (var (s, t) in tables)
            {
                var en = await QueryAsync(c, p, "SELECT is_tracked_by_cdc FROM sys.tables tb JOIN sys.schemas sc ON sc.schema_id=tb.schema_id WHERE sc.name=@s AND tb.name=@t", ct, ("s", s), ("t", t));
                res.Add(new($"table_cdc:{s}.{t}", en.Count > 0 && Convert.ToBoolean(en[0]["is_tracked_by_cdc"]),
                    $"EXEC sys.sp_cdc_enable_table @source_schema=N'{Esc(s)}', @source_name=N'{Esc(t)}', @role_name=NULL"));
            }
        }
        catch (Exception ex) { res.Add(new("connection", false, ex.Message)); }
        return res;
    }

    public override IDictionary<string, string> BuildDebeziumConfig(ConnectionProfile p, string prefix, IReadOnlyList<(string Schema, string Table)> tables, string kafka, string name)
        => WithCdcEndpoint(new Dictionary<string, string>
        {
            ["connector.class"] = "io.debezium.connector.sqlserver.SqlServerConnector",
            ["database.hostname"] = p.Host, ["database.port"] = p.Port.ToString(), ["database.user"] = p.Username, ["database.password"] = p.Password,
            ["database.names"] = p.Database,
            ["database.encrypt"] = "false", ["database.trustServerCertificate"] = "true",
            ["topic.prefix"] = prefix,
            ["table.include.list"] = string.Join(",", tables.Select(t => $"{t.Schema}.{t.Table}")),
            ["schema.history.internal.kafka.bootstrap.servers"] = kafka,
            ["schema.history.internal.kafka.topic"] = $"schemahistory.{prefix}",
        }, p);
}
