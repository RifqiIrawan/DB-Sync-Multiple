using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;

namespace DBSync.Connectors.Abstractions;

/// <summary>
/// Basis connector berbasis ADO.NET. Seluruh SQL menggunakan parameter; identifier di-quote oleh vendor.
/// </summary>
public abstract class AdoNetConnectorBase : IDatabaseConnector
{
    public abstract string DatabaseType { get; }

    // ---- hooks vendor ----
    protected abstract DbConnection CreateConnection(ConnectionProfile profile);
    protected abstract string Quote(string identifier);
    protected virtual string ParamPrefix => "@";
    protected virtual bool UseTopForLimit => false;
    protected abstract string ServerVersionSql { get; }
    protected abstract Task<List<string>> QuerySchemasAsync(DbConnection c, ConnectionProfile p, CancellationToken ct);
    protected abstract Task<List<TableMetadata>> QueryTablesAsync(DbConnection c, ConnectionProfile p, string? schema, CancellationToken ct);
    protected abstract Task<TableSchema> QueryTableSchemaAsync(DbConnection c, ConnectionProfile p, string schema, string table, CancellationToken ct);
    protected abstract string BuildUpsertSql(TableSchema t, List<string> cols, List<string> paramNames);
    protected virtual string WrapInsertIdentity(TableSchema t, IEnumerable<string> cols, string sql) => sql;
    protected abstract ErrorClass Classify(Exception ex, out string code);
    protected virtual void ConfigureParameter(DbParameter p, ColumnInfo? col, object? value) { }
    public abstract Task<IReadOnlyList<CdcCheck>> CheckCdcPrerequisitesAsync(ConnectionProfile profile, IReadOnlyList<(string Schema, string Table)> tables, CancellationToken ct);
    public abstract IDictionary<string, string> BuildDebeziumConfig(ConnectionProfile profile, string topicPrefix, IReadOnlyList<(string Schema, string Table)> tables, string kafkaBootstrap, string connectorName);

    protected string Qualified(string schema, string table) =>
        string.IsNullOrEmpty(schema) || !UsesSchemaPrefix ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";
    /// <summary>MySQL: schema == nama database yang sama dengan koneksi, jadi cukup nama tabel jika tidak ingin prefix.</summary>
    protected virtual bool UsesSchemaPrefix => true;

    // ---- helpers ----
    protected async Task<DbConnection> OpenAsync(ConnectionProfile p, CancellationToken ct)
    {
        var c = CreateConnection(p);
        try { await c.OpenAsync(ct); return c; }
        catch { await c.DisposeAsync(); throw; }
    }

    protected DbCommand NewCommand(DbConnection c, string sql, ConnectionProfile p, DbTransaction? tx = null)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = p.CommandTimeoutSeconds;
        cmd.Transaction = tx;
        return cmd;
    }

    protected DbParameter AddParam(DbCommand cmd, string name, object? value, ColumnInfo? col = null)
    {
        var prm = cmd.CreateParameter();
        prm.ParameterName = name;
        prm.Value = value ?? DBNull.Value;
        ConfigureParameter(prm, col, value);
        cmd.Parameters.Add(prm);
        return prm;
    }

    protected async Task<List<Dictionary<string, object?>>> QueryAsync(DbConnection c, ConnectionProfile p, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var cmd = NewCommand(c, sql, p);
        foreach (var (n, v) in ps) AddParam(cmd, ParamPrefix + n, v);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    protected async Task<object?> ScalarAsync(DbConnection c, ConnectionProfile p, string sql, CancellationToken ct)
    {
        await using var cmd = NewCommand(c, sql, p);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    protected ConnectorException Wrap(Exception ex)
    {
        if (ex is ConnectorException ce) return ce;
        var cls = Classify(ex, out var code);
        return new ConnectorException(cls, code, ex.Message, ex);
    }

    // ---- metadata ----
    public async Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        try
        {
            await using var c = await OpenAsync(profile, ct);
            var v = await ScalarAsync(c, profile, ServerVersionSql, ct);
            return new ConnectionTestResult(true, "Koneksi berhasil.", v?.ToString());
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<DatabaseSchema>> GetSchemasAsync(ConnectionProfile profile, CancellationToken ct)
    {
        try
        {
            await using var c = await OpenAsync(profile, ct);
            return (await QuerySchemasAsync(c, profile, ct)).Select(s => new DatabaseSchema(s)).ToList();
        }
        catch (Exception ex) { throw Wrap(ex); }
    }

    public async Task<IReadOnlyList<TableMetadata>> GetTablesAsync(ConnectionProfile profile, string? schema, CancellationToken ct)
    {
        try
        {
            await using var c = await OpenAsync(profile, ct);
            return await QueryTablesAsync(c, profile, schema, ct);
        }
        catch (Exception ex) { throw Wrap(ex); }
    }

    public async Task<TableSchema> GetTableSchemaAsync(ConnectionProfile profile, string schema, string table, CancellationToken ct)
    {
        try
        {
            await using var c = await OpenAsync(profile, ct);
            return await QueryTableSchemaAsync(c, profile, schema, table, ct);
        }
        catch (Exception ex) { throw Wrap(ex); }
    }

    public async Task<long> CountRowsAsync(ConnectionProfile profile, string schema, string table, CancellationToken ct)
    {
        try
        {
            await using var c = await OpenAsync(profile, ct);
            var v = await ScalarAsync(c, profile, $"SELECT COUNT(*) FROM {Qualified(schema, table)}", ct);
            return Convert.ToInt64(v);
        }
        catch (Exception ex) { throw Wrap(ex); }
    }

    // ---- read ----
    protected (string Sql, List<object?> Params) BuildSelect(ReadQuery q)
    {
        var sb = new StringBuilder("SELECT ");
        if (UseTopForLimit && q.Limit is > 0) sb.Append($"TOP ({q.Limit}) ");
        sb.Append(string.Join(", ", q.Columns.Select(Quote)));
        sb.Append(" FROM ").Append(Qualified(q.Schema, q.Table));
        var ps = new List<object?>();

        if (q.KeyEquals is { Count: > 0 })
        {
            sb.Append(" WHERE ");
            sb.Append(string.Join(" AND ", q.KeyColumns.Select((k, i) =>
            {
                ps.Add(q.KeyEquals[i]);
                return $"{Quote(k)} = {ParamPrefix}p{ps.Count - 1}";
            })));
        }
        else if (q.After is { Count: > 0 })
        {
            // keyset: (k1>a1) OR (k1=a1 AND k2>a2) ...
            var ors = new List<string>();
            for (int i = 0; i < q.KeyColumns.Count; i++)
            {
                var ands = new List<string>();
                for (int j = 0; j < i; j++)
                {
                    ps.Add(q.After[j]);
                    ands.Add($"{Quote(q.KeyColumns[j])} = {ParamPrefix}p{ps.Count - 1}");
                }
                ps.Add(q.After[i]);
                ands.Add($"{Quote(q.KeyColumns[i])} > {ParamPrefix}p{ps.Count - 1}");
                ors.Add("(" + string.Join(" AND ", ands) + ")");
            }
            sb.Append(" WHERE ").Append(string.Join(" OR ", ors));
        }

        if (q.KeyColumns.Count > 0) sb.Append(" ORDER BY ").Append(string.Join(", ", q.KeyColumns.Select(Quote)));
        if (!UseTopForLimit && q.Limit is > 0) sb.Append($" LIMIT {q.Limit}");
        return (sb.ToString(), ps);
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(ConnectionProfile profile, ReadQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        DbConnection? c = null;
        DbCommand? cmd = null;
        DbDataReader? r = null;
        try
        {
            try
            {
                c = await OpenAsync(profile, ct);
                var (sql, ps) = BuildSelect(query);
                cmd = NewCommand(c, sql, profile);
                for (int i = 0; i < ps.Count; i++) AddParam(cmd, $"{ParamPrefix}p{i}", ps[i]);
                r = await cmd.ExecuteReaderAsync(ct);
            }
            catch (Exception ex) { throw Wrap(ex); }

            while (true)
            {
                bool has;
                try { has = await r.ReadAsync(ct); }
                catch (Exception ex) { throw Wrap(ex); }
                if (!has) break;
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                yield return row;
            }
        }
        finally
        {
            if (r != null) await r.DisposeAsync();
            if (cmd != null) await cmd.DisposeAsync();
            if (c != null) await c.DisposeAsync();
        }
    }

    // ---- write ----
    public async Task<WriteResult> ApplyChangesAsync(ConnectionProfile profile, IReadOnlyList<ChangeOperation> operations, CancellationToken ct)
    {
        if (operations.Count == 0) return new WriteResult(0);
        DbConnection? c = null;
        try
        {
            c = await OpenAsync(profile, ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            int applied = 0;
            try
            {
                foreach (var op in operations)
                {
                    applied += await ExecuteOperationAsync(c, tx, profile, op, ct);
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch { /* ignore */ }
                throw;
            }
            return new WriteResult(applied);
        }
        catch (Exception ex) { throw Wrap(ex); }
        finally { if (c != null) await c.DisposeAsync(); }
    }

    private async Task<int> ExecuteOperationAsync(DbConnection c, DbTransaction tx, ConnectionProfile profile, ChangeOperation op, CancellationToken ct)
    {
        var t = op.Target;
        string sql;
        var parameters = new List<(string Name, object? Value, ColumnInfo? Col)>();

        if (op.Kind == ChangeKind.Delete)
        {
            var keyCols = t.PrimaryKey;
            var conds = new List<string>();
            foreach (var k in keyCols)
            {
                var pn = $"{ParamPrefix}k{parameters.Count}";
                conds.Add($"{Quote(k.Name)} = {pn}");
                parameters.Add((pn, op.Key[k.Name], k));
            }
            sql = $"DELETE FROM {Qualified(t.Schema, t.Name)} WHERE {string.Join(" AND ", conds)}";
        }
        else
        {
            var cols = new List<string>();
            var pnames = new List<string>();
            foreach (var (name, val) in op.Values!)
            {
                var col = t.Find(name);
                if (col is { IsGenerated: true }) continue; // kolom generated/computed tidak ditulis
                var pn = $"{ParamPrefix}v{cols.Count}";
                cols.Add(col?.Name ?? name);
                pnames.Add(pn);
                parameters.Add((pn, val, col));
            }
            if (op.Kind == ChangeKind.Insert)
            {
                sql = $"INSERT INTO {Qualified(t.Schema, t.Name)} ({string.Join(", ", cols.Select(Quote))}) VALUES ({string.Join(", ", pnames)})";
            }
            else sql = BuildUpsertSql(t, cols, pnames);
            sql = WrapInsertIdentity(t, cols, sql);
        }

        await using var cmd = NewCommand(c, sql, profile, tx);
        foreach (var (n, v, col) in parameters) AddParam(cmd, n, v, col);
        await cmd.ExecuteNonQueryAsync(ct);
        return 1;
    }

    // ---- util ----
    /// <summary>Alamat DB bagi Debezium bisa berbeda (mis. nama container). Opsi koneksi "cdcHost"/"cdcPort" menimpa host/port.</summary>
    protected static IDictionary<string, string> WithCdcEndpoint(IDictionary<string, string> cfg, ConnectionProfile p)
    {
        if (p.Options != null && p.Options.TryGetValue("cdcHost", out var h) && !string.IsNullOrWhiteSpace(h)) cfg["database.hostname"] = h;
        if (p.Options != null && p.Options.TryGetValue("cdcPort", out var pt) && !string.IsNullOrWhiteSpace(pt)) cfg["database.port"] = pt;
        return cfg;
    }

    protected static string Esc(string s) => s.Replace("'", "''");
}
