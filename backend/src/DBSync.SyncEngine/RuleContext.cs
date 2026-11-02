using System.Text.Json;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Pipeline;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DBSync.SyncEngine;

public sealed class MappedColumn
{
    public string AName { get; init; } = "";
    public string BName { get; init; } = "";
    public bool IsPk { get; init; }
    public ColumnInfo? A { get; init; }
    public ColumnInfo? B { get; init; }
}

/// <summary>Seluruh konteks yang dibutuhkan untuk menyinkronkan satu table rule.</summary>
public sealed class RuleContext
{
    public required SyncGroup Group { get; init; }
    public required SyncTableRule Rule { get; init; }
    public required ConnectionProfile ProfileA { get; init; }
    public required ConnectionProfile ProfileB { get; init; }
    public required IDatabaseConnector ConnA { get; init; }
    public required IDatabaseConnector ConnB { get; init; }
    public required TableSchema SchemaA { get; init; }
    public required TableSchema SchemaB { get; init; }
    public required List<MappedColumn> Columns { get; init; }

    public string Label => $"{Rule.EndpointASchema}.{Rule.EndpointATable} ⇄ {Rule.EndpointBSchema}.{Rule.EndpointBTable}";
    public List<MappedColumn> Pk => Columns.Where(c => c.IsPk).OrderBy(c => c.A!.Ordinal).ToList();

    public bool CanAToB => Rule.SyncAToB && Group.SyncDirection is Directions.AToB or Directions.Bidirectional;
    public bool CanBToA => Rule.SyncBToA && Group.SyncDirection is Directions.BToA or Directions.Bidirectional;

    public IDatabaseConnector Conn(char side) => side == 'A' ? ConnA : ConnB;
    public ConnectionProfile Profile(char side) => side == 'A' ? ProfileA : ProfileB;
    public TableSchema Schema(char side) => side == 'A' ? SchemaA : SchemaB;
    public static char Other(char side) => side == 'A' ? 'B' : 'A';
    public string Name(MappedColumn c, char side) => side == 'A' ? c.AName : c.BName;
    public ColumnInfo? Col(MappedColumn c, char side) => side == 'A' ? c.A : c.B;

    /// <summary>Baris dari sisi tertentu -> baris logis (key = nama kolom A).</summary>
    public Dictionary<string, object?> ToLogical(IReadOnlyDictionary<string, object?> row, char side)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Columns)
            d[c.AName] = row.TryGetValue(Name(c, side), out var v) ? v : null;
        return d;
    }

    public string PkKey(IReadOnlyDictionary<string, object?> logical) =>
        JsonSerializer.Serialize(Pk.Select(c => Values.Normalize(logical[c.AName], c.A!.LogicalType)));

    public string RowHash(IReadOnlyDictionary<string, object?> logical) =>
        Values.Hash(Columns.Select(c => Values.Normalize(logical[c.AName], c.A!.LogicalType)));

    public JsonDocument PkJson(IReadOnlyDictionary<string, object?> logical) =>
        JsonSerializer.SerializeToDocument(Pk.ToDictionary(c => c.AName, c => Values.ToJsonSafe(logical[c.AName])));

    public JsonDocument RowJson(IReadOnlyDictionary<string, object?> logical) =>
        JsonSerializer.SerializeToDocument(logical.ToDictionary(k => k.Key, k => Values.ToJsonSafe(k.Value)));

    public Dictionary<string, object?> KeyFor(IReadOnlyDictionary<string, object?> logical, char target)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Pk) d[Name(c, target)] = Values.Coerce(logical[c.AName], Col(c, target)!);
        return d;
    }

    public ChangeOperation BuildUpsert(IReadOnlyDictionary<string, object?> logical, char target)
    {
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Columns)
        {
            var tc = Col(c, target)!;
            if (tc.IsGenerated) continue;
            vals[tc.Name] = Values.Coerce(logical[c.AName], tc);
        }
        var kind = Rule.WriteMode == "INSERT_ONLY" ? ChangeKind.Insert : ChangeKind.Upsert;
        return new ChangeOperation(kind, Schema(target), KeyFor(logical, target), vals);
    }

    public ChangeOperation BuildDelete(IReadOnlyDictionary<string, object?> logicalWithPk, char target) =>
        new(ChangeKind.Delete, Schema(target), KeyFor(logicalWithPk, target), null);

    public ReadQuery BuildRead(char side, IReadOnlyList<object?>? after = null, IReadOnlyList<object?>? keyEquals = null, int? limit = null)
    {
        var schema = side == 'A' ? Rule.EndpointASchema : Rule.EndpointBSchema;
        var table = side == 'A' ? Rule.EndpointATable : Rule.EndpointBTable;
        return new ReadQuery(schema, table, Columns.Select(c => Name(c, side)).ToList(), Pk.Select(c => Name(c, side)).ToList(), after, keyEquals, limit);
    }

    /// <summary>Nilai PK (tipe sisi tertentu) dari baris logis, untuk keyset.</summary>
    public List<object?> PkValues(IReadOnlyDictionary<string, object?> logical, char side) =>
        Pk.Select(c => Values.Coerce(logical[c.AName], Col(c, side)!)).ToList();
}

public interface IRuleContextFactory
{
    Task<RuleContext> CreateAsync(Guid ruleId, CancellationToken ct);
}

public sealed class RuleContextFactory : IRuleContextFactory
{
    private readonly DbSyncContext _db;
    private readonly IConnectionProfileFactory _profiles;
    private readonly IConnectorRegistry _registry;
    public RuleContextFactory(DbSyncContext db, IConnectionProfileFactory profiles, IConnectorRegistry registry)
    { _db = db; _profiles = profiles; _registry = registry; }

    public async Task<RuleContext> CreateAsync(Guid ruleId, CancellationToken ct)
    {
        var rule = await _db.SyncTableRules.AsNoTracking().Include(r => r.ColumnMappings).FirstOrDefaultAsync(r => r.Id == ruleId, ct)
            ?? throw new KeyNotFoundException($"Table rule {ruleId} tidak ditemukan.");
        var group = await _db.SyncGroups.AsNoTracking()
            .Include(g => g.Endpoints).ThenInclude(e => e.Connection).ThenInclude(c => c!.Secret)
            .FirstAsync(g => g.Id == rule.SyncGroupId, ct);
        var ea = group.Endpoints.FirstOrDefault(e => e.EndpointRole == "A");
        var eb = group.Endpoints.FirstOrDefault(e => e.EndpointRole == "B");
        if (ea?.Connection is null || eb?.Connection is null)
            throw new ConnectorException(ErrorClass.Configuration, "ENDPOINT_MISSING", "Group harus memiliki Endpoint A dan B.");

        var pa = _profiles.Create(ea.Connection, group.CommandTimeoutSeconds);
        var pb = _profiles.Create(eb.Connection, group.CommandTimeoutSeconds);
        var ca = _registry.Get(pa.DbType); var cb = _registry.Get(pb.DbType);
        var sa = await ca.GetTableSchemaAsync(pa, rule.EndpointASchema, rule.EndpointATable, ct);
        var sb = await cb.GetTableSchemaAsync(pb, rule.EndpointBSchema, rule.EndpointBTable, ct);

        var cols = new List<MappedColumn>();
        foreach (var m in rule.ColumnMappings.Where(m => m.IsEnabled))
        {
            var a = sa.Find(m.EndpointAColumn); var b = sb.Find(m.EndpointBColumn);
            if (a is null || b is null)
                throw new ConnectorException(ErrorClass.Configuration, "COLUMN_MISSING", $"Kolom {m.EndpointAColumn}/{m.EndpointBColumn} tidak ada pada schema aktual.");
            cols.Add(new MappedColumn { AName = a.Name, BName = b.Name, IsPk = m.IsPrimaryKey, A = a, B = b });
        }
        if (!cols.Any(c => c.IsPk))
            throw new ConnectorException(ErrorClass.Configuration, "PRIMARY_KEY_REQUIRED", $"Tabel {rule.EndpointATable} tidak memiliki primary key terpetakan.");

        return new RuleContext { Group = group, Rule = rule, ProfileA = pa, ProfileB = pb, ConnA = ca, ConnB = cb, SchemaA = sa, SchemaB = sb, Columns = cols };
    }
}

/// <summary>Penyimpanan baseline hash per baris (change detection + echo suppression).</summary>
public static class RowStateStore
{
    public const string Deleted = "DELETED";

    public static async Task<Dictionary<string, string>> LoadAsync(DbSyncContext db, Guid ruleId, CancellationToken ct)
    {
        var d = new Dictionary<string, string>();
        await foreach (var r in db.SyncRowStates.AsNoTracking().Where(x => x.TableRuleId == ruleId).Select(x => new { x.PkKey, x.RowHash }).AsAsyncEnumerable().WithCancellation(ct))
            d[r.PkKey] = r.RowHash;
        return d;
    }

    public static async Task UpsertAsync(DbSyncContext db, Guid ruleId, IReadOnlyCollection<(string Pk, string Hash)> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        foreach (var chunk in items.Chunk(2000))
        {
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO sync_row_states (table_rule_id, pk_key, row_hash, updated_at)
                  SELECT @r, u.pk, u.h, now() FROM unnest(@pks, @hs) AS u(pk, h)
                  ON CONFLICT (table_rule_id, pk_key) DO UPDATE SET row_hash = EXCLUDED.row_hash, updated_at = now()",
                new object[]
                {
                    new NpgsqlParameter("r", ruleId),
                    new NpgsqlParameter("pks", chunk.Select(x => x.Pk).ToArray()),
                    new NpgsqlParameter("hs", chunk.Select(x => x.Hash).ToArray()),
                }, ct);
        }
    }

    public static async Task DeleteAsync(DbSyncContext db, Guid ruleId, IReadOnlyCollection<string> pks, CancellationToken ct)
    {
        if (pks.Count == 0) return;
        foreach (var chunk in pks.Chunk(2000))
            await db.Database.ExecuteSqlRawAsync("DELETE FROM sync_row_states WHERE table_rule_id = @r AND pk_key = ANY(@pks)",
                new object[] { new NpgsqlParameter("r", ruleId), new NpgsqlParameter("pks", chunk) }, ct);
    }

    public static async Task<string?> GetAsync(DbSyncContext db, Guid ruleId, string pk, CancellationToken ct) =>
        await db.SyncRowStates.AsNoTracking().Where(x => x.TableRuleId == ruleId && x.PkKey == pk).Select(x => x.RowHash).FirstOrDefaultAsync(ct);
}
