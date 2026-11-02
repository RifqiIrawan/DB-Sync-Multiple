using System.Data.Common;
using DBSync.Connectors.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace DBSync.Connectors.PostgreSql;

public sealed class PostgreSqlDatabaseConnector : AdoNetConnectorBase
{
    public override string DatabaseType => "POSTGRESQL";
    protected override string ServerVersionSql => "SHOW server_version";
    protected override string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    protected override DbConnection CreateConnection(ConnectionProfile p)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = p.Host, Port = p.Port, Database = p.Database, Username = p.Username, Password = p.Password,
            SslMode = p.SslEnabled ? SslMode.Require : SslMode.Prefer, Timeout = 15, CommandTimeout = p.CommandTimeoutSeconds,
            Pooling = true, MaxPoolSize = 20,
        };
        return new NpgsqlConnection(b.ConnectionString);
    }

    protected override string[] MaintenanceDatabases => new[] { "postgres", "template1" };

    protected override async Task<List<string>> QueryDatabasesAsync(DbConnection c, ConnectionProfile p, CancellationToken ct)
    {
        var rows = await QueryAsync(c, p, "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn AND has_database_privilege(datname, 'CONNECT') ORDER BY datname", ct);
        return rows.Select(r => r["datname"]!.ToString()!).ToList();
    }

    protected override async Task<List<string>> QuerySchemasAsync(DbConnection c, ConnectionProfile p, CancellationToken ct)
    {
        var rows = await QueryAsync(c, p, "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('pg_catalog','information_schema','pg_toast') AND schema_name NOT LIKE 'pg_temp%' AND schema_name NOT LIKE 'pg_toast_temp%' ORDER BY schema_name", ct);
        return rows.Select(r => r["schema_name"]!.ToString()!).ToList();
    }

    protected override async Task<List<TableMetadata>> QueryTablesAsync(DbConnection c, ConnectionProfile p, string? schema, CancellationToken ct)
    {
        var rows = await QueryAsync(c, p,
            @"SELECT n.nspname AS s, c.relname AS t, c.reltuples::bigint AS r FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema','pg_toast') AND (@s::text IS NULL OR n.nspname=@s) ORDER BY 1,2",
            ct, ("s", schema));
        return rows.Select(r => new TableMetadata(r["s"]!.ToString()!, r["t"]!.ToString()!, Convert.ToInt64(r["r"]) < 0 ? null : Convert.ToInt64(r["r"]))).ToList();
    }

    protected override async Task<TableSchema> QueryTableSchemaAsync(DbConnection c, ConnectionProfile p, string schema, string table, CancellationToken ct)
    {
        var cols = await QueryAsync(c, p,
            @"SELECT c.column_name AS name, c.data_type AS dt, c.udt_name AS udt, c.is_nullable AS nullable, c.ordinal_position AS ord,
                     c.character_maximum_length AS len, c.numeric_precision AS prec, c.numeric_scale AS scale,
                     c.is_identity AS ident, c.is_generated AS gen, c.column_default AS def,
                     EXISTS (SELECT 1 FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage k
                             ON k.constraint_name=tc.constraint_name AND k.table_schema=tc.table_schema AND k.table_name=tc.table_name
                             WHERE tc.constraint_type='PRIMARY KEY' AND tc.table_schema=c.table_schema AND tc.table_name=c.table_name AND k.column_name=c.column_name) AS pk
              FROM information_schema.columns c WHERE c.table_schema=@s AND c.table_name=@t ORDER BY c.ordinal_position",
            ct, ("s", schema), ("t", table));
        if (cols.Count == 0) throw new ConnectorException(ErrorClass.Configuration, "TABLE_NOT_FOUND", $"Tabel {schema}.{table} tidak ditemukan.");

        var columns = cols.Select(r =>
        {
            var dt = r["dt"]!.ToString()!; var udt = r["udt"]!.ToString()!;
            var def = r["def"]?.ToString() ?? "";
            return new ColumnInfo(r["name"]!.ToString()!, dt == "USER-DEFINED" || dt == "ARRAY" ? udt : dt, MapLogical(dt, udt),
                r["nullable"]!.ToString() == "YES", (bool)r["pk"]!, Convert.ToInt32(r["ord"]),
                r["len"] is null ? null : Convert.ToInt32(r["len"]), r["prec"] is null ? null : Convert.ToInt32(r["prec"]), r["scale"] is null ? null : Convert.ToInt32(r["scale"]),
                r["ident"]!.ToString() == "YES", r["gen"]!.ToString() == "ALWAYS");
        }).ToList();

        var fkRows = await QueryAsync(c, p,
            @"SELECT con.conname AS cn, a.attname AS col, rn.nspname AS rs, rc.relname AS rt, ra.attname AS rcol
              FROM pg_constraint con
              JOIN pg_class cl ON cl.oid=con.conrelid JOIN pg_namespace n ON n.oid=cl.relnamespace
              JOIN pg_class rc ON rc.oid=con.confrelid JOIN pg_namespace rn ON rn.oid=rc.relnamespace
              CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS u(ck, fk, ord)
              JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=u.ck
              JOIN pg_attribute ra ON ra.attrelid=con.confrelid AND ra.attnum=u.fk
              WHERE con.contype='f' AND n.nspname=@s AND cl.relname=@t ORDER BY con.conname, u.ord",
            ct, ("s", schema), ("t", table));
        var fks = fkRows.GroupBy(r => r["cn"]!.ToString()!).Select(g =>
            new ForeignKeyInfo(g.Key, g.Select(x => x["col"]!.ToString()!).ToList(), g.First()["rs"]!.ToString()!, g.First()["rt"]!.ToString()!, g.Select(x => x["rcol"]!.ToString()!).ToList())).ToList();
        return new TableSchema(schema, table, columns, fks);
    }

    private static string MapLogical(string dt, string udt) => dt switch
    {
        "smallint" or "integer" or "bigint" => LogicalTypes.Integer,
        "numeric" or "money" => LogicalTypes.Decimal,
        "real" or "double precision" => LogicalTypes.Float,
        "boolean" => LogicalTypes.Boolean,
        "character varying" or "character" or "text" or "citext" => LogicalTypes.Text,
        "date" => LogicalTypes.Date,
        "time without time zone" or "time with time zone" => LogicalTypes.Time,
        "timestamp without time zone" => LogicalTypes.Timestamp,
        "timestamp with time zone" => LogicalTypes.TimestampTz,
        "bytea" => LogicalTypes.Binary,
        "json" or "jsonb" => LogicalTypes.Json,
        "uuid" => LogicalTypes.Uuid,
        _ => LogicalTypes.Other,
    };

    protected override void ConfigureParameter(DbParameter prm, ColumnInfo? col, object? value)
    {
        if (prm is not NpgsqlParameter np || col is null) return;
        switch (col.NativeType.ToLowerInvariant())
        {
            case "json": np.NpgsqlDbType = NpgsqlDbType.Json; break;
            case "jsonb": np.NpgsqlDbType = NpgsqlDbType.Jsonb; break;
        }
        if (col.LogicalType == LogicalTypes.Other && value is string) np.NpgsqlDbType = NpgsqlDbType.Unknown; // enum/tipe kustom
        if (value is DateTime dt)
        {
            if (col.LogicalType == LogicalTypes.TimestampTz) np.Value = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            else np.Value = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }
    }

    protected override string WrapInsertIdentity(TableSchema t, IEnumerable<string> cols, string sql) => sql;

    protected override string BuildUpsertSql(TableSchema t, List<string> cols, List<string> pn)
    {
        var pk = t.PrimaryKey.Select(k => k.Name).ToList();
        var pkSet = pk.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var upd = cols.Where(x => !pkSet.Contains(x)).Select(x => $"{Quote(x)}=EXCLUDED.{Quote(x)}").ToList();
        bool identity = cols.Any(x => t.Find(x)?.IsIdentity == true);
        var sql = $"INSERT INTO {Qualified(t.Schema, t.Name)} ({string.Join(", ", cols.Select(Quote))}) " +
                  (identity ? "OVERRIDING SYSTEM VALUE " : "") +
                  $"VALUES ({string.Join(", ", pn)}) ON CONFLICT ({string.Join(", ", pk.Select(Quote))}) ";
        return sql + (upd.Count == 0 ? "DO NOTHING" : "DO UPDATE SET " + string.Join(", ", upd));
    }

    protected override ErrorClass Classify(Exception ex, out string code)
    {
        code = "PG_ERROR";
        if (ex is PostgresException pe)
        {
            code = "PG_" + pe.SqlState;
            if (pe.IsTransient) return ErrorClass.Transient;
            return pe.SqlState switch
            {
                "28P01" or "28000" or "3D000" => ErrorClass.Configuration,
                _ => ErrorClass.Permanent
            };
        }
        if (ex is NpgsqlException ne && ne.IsTransient) return ErrorClass.Transient;
        if (ex is TimeoutException || ex is OperationCanceledException || ex is System.Net.Sockets.SocketException) return ErrorClass.Transient;
        return ErrorClass.Permanent;
    }

    public override async Task<IReadOnlyList<CdcCheck>> CheckCdcPrerequisitesAsync(ConnectionProfile p, IReadOnlyList<(string Schema, string Table)> tables, CancellationToken ct)
    {
        var res = new List<CdcCheck>();
        try
        {
            await using var c = await OpenAsync(p, ct);
            var wal = (await ScalarAsync(c, p, "SHOW wal_level", ct))?.ToString();
            res.Add(new("wal_level", wal == "logical", $"wal_level={wal} (harus logical)"));
            var priv = await QueryAsync(c, p, "SELECT rolreplication OR rolsuper AS ok FROM pg_roles WHERE rolname=current_user", ct);
            res.Add(new("replication_privilege", priv.Count > 0 && (bool)priv[0]["ok"]!, "User butuh atribut REPLICATION (atau superuser)"));
            var canCreate = await QueryAsync(c, p, "SELECT has_database_privilege(current_user, current_database(), 'CREATE') AS ok", ct);
            res.Add(new("create_publication_privilege", canCreate.Count > 0 && (bool)canCreate[0]["ok"]!,
                "Debezium membuat publication otomatis: user butuh GRANT CREATE ON DATABASE dan harus pemilik tabel (ALTER TABLE ... OWNER TO user)"));
            var slots = await QueryAsync(c, p, "SELECT slot_name, active, pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS lag FROM pg_replication_slots", ct);
            res.Add(new("replication_slots", true, slots.Count == 0 ? "Belum ada slot" : string.Join("; ", slots.Select(s => $"{s["slot_name"]} active={s["active"]} lag={s["lag"]}"))));
            foreach (var (s, t) in tables)
            {
                var r = await QueryAsync(c, p, "SELECT relreplident FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@s AND c.relname=@t", ct, ("s", s), ("t", t));
                var ident = r.FirstOrDefault()?["relreplident"]?.ToString();
                res.Add(new($"replica_identity:{s}.{t}", ident is "d" or "f" or "i" || ident == "d", $"REPLICA IDENTITY={ident} (default memakai PK; FULL bila butuh nilai 'before' lengkap)"));
            }
        }
        catch (Exception ex) { res.Add(new("connection", false, ex.Message)); }
        return res;
    }

    public override IDictionary<string, string> BuildDebeziumConfig(ConnectionProfile p, string prefix, IReadOnlyList<(string Schema, string Table)> tables, string kafka, string name)
    {
        var slot = ("dbsync_" + prefix).ToLowerInvariant().Replace('-', '_');
        return WithCdcEndpoint(new Dictionary<string, string>
        {
            ["connector.class"] = "io.debezium.connector.postgresql.PostgresConnector",
            ["plugin.name"] = "pgoutput",
            ["database.hostname"] = p.Host, ["database.port"] = p.Port.ToString(), ["database.user"] = p.Username, ["database.password"] = p.Password,
            ["database.dbname"] = p.Database,
            ["topic.prefix"] = prefix,
            ["slot.name"] = slot.Length > 63 ? slot[..63] : slot,
            ["publication.name"] = ("dbsync_pub_" + prefix).ToLowerInvariant().Replace('-', '_'),
            ["publication.autocreate.mode"] = "filtered",
            ["table.include.list"] = string.Join(",", tables.Select(t => $"{t.Schema}.{t.Table}")),
        }, p);
    }
}
