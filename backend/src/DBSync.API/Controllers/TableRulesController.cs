using DBSync.API.Common;
using DBSync.Application;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBSync.API.Controllers;

public record UpdateRuleRequest(bool? SyncAToB, bool? SyncBToA, bool? SyncInsert, bool? SyncUpdate, bool? SyncDelete, string? WriteMode, string? UpdatedAtColumn, bool? IsEnabled);
public record ColumnMappingItem(string EndpointAColumn, string EndpointBColumn, bool IsPrimaryKey, bool IsEnabled, string? DataTypeStrategy);

[Route("api/v1/table-rules")]
public class TableRulesController : ApiBase
{
    private readonly DbSyncContext _db;
    private readonly IConnectionProfileFactory _profiles;
    private readonly IConnectorRegistry _registry;
    public TableRulesController(DbSyncContext db, IConnectionProfileFactory profiles, IConnectorRegistry registry) { _db = db; _profiles = profiles; _registry = registry; }

    private async Task<SyncTableRule> LoadAsync(Guid id, CancellationToken ct) =>
        await _db.SyncTableRules.Include(r => r.ColumnMappings).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException("Table rule tidak ditemukan.");

    private async Task EnsureEditableAsync(SyncTableRule r, CancellationToken ct)
    {
        if (await _db.SyncRuns.AnyAsync(x => x.SyncGroupId == r.SyncGroupId && (x.Status == RunStatus.Running || x.Status == RunStatus.Queued), ct))
            throw new ApiException(409, "RUN_ACTIVE", "Tidak dapat mengubah tabel saat ada run berjalan/antre pada group ini.");
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Update(Guid id, UpdateRuleRequest u, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct); await EnsureEditableAsync(r, ct);
        var before = new { r.SyncAToB, r.SyncBToA, r.SyncInsert, r.SyncUpdate, r.SyncDelete, r.WriteMode, r.UpdatedAtColumn, r.IsEnabled };
        r.SyncAToB = u.SyncAToB ?? r.SyncAToB; r.SyncBToA = u.SyncBToA ?? r.SyncBToA; r.SyncInsert = u.SyncInsert ?? r.SyncInsert;
        r.SyncUpdate = u.SyncUpdate ?? r.SyncUpdate; r.SyncDelete = u.SyncDelete ?? r.SyncDelete; r.IsEnabled = u.IsEnabled ?? r.IsEnabled;
        if (u.WriteMode is "UPSERT" or "INSERT_ONLY") r.WriteMode = u.WriteMode;
        if (u.UpdatedAtColumn != null) r.UpdatedAtColumn = u.UpdatedAtColumn == "" ? null : u.UpdatedAtColumn;
        r.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await Log("UPDATE_TABLE_RULE", "SyncTableRule", id.ToString(), before, u);
        return Success(new { r.Id });
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct); await EnsureEditableAsync(r, ct);
        await _db.SyncRowStates.Where(x => x.TableRuleId == id).ExecuteDeleteAsync(ct);
        await _db.SyncCheckpoints.Where(x => x.TableRuleId == id).ExecuteDeleteAsync(ct);
        await _db.SyncConflicts.Where(x => x.TableRuleId == id).ExecuteDeleteAsync(ct);
        await _db.SyncEvents.Where(x => x.TableRuleId == id).ExecuteDeleteAsync(ct);
        await _db.SyncRunItems.Where(x => x.TableRuleId == id).ExecuteDeleteAsync(ct);
        _db.SyncTableRules.Remove(r); await _db.SaveChangesAsync(ct);
        await Log("DELETE_TABLE_RULE", "SyncTableRule", id.ToString(), new { r.EndpointATable });
        return Success(null, "Tabel dihapus dari group");
    }

    [HttpPost("{id:guid}/validate"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        var g = await _db.SyncGroups.Include(x => x.Endpoints).ThenInclude(e => e.Connection).ThenInclude(c => c!.Secret).FirstAsync(x => x.Id == r.SyncGroupId, ct);
        var pa = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "A").Connection!); var pb = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "B").Connection!);
        ValidationReport rep;
        try
        {
            var sa = await _registry.Get(pa.DbType).GetTableSchemaAsync(pa, r.EndpointASchema, r.EndpointATable, ct);
            var sb = await _registry.Get(pb.DbType).GetTableSchemaAsync(pb, r.EndpointBSchema, r.EndpointBTable, ct);
            rep = RuleValidator.Validate(sa, sb, r.ColumnMappings.ToList());
        }
        catch (ConnectorException ex) { rep = ValidationReport.From(new() { new("error", ex.Code, ex.Message) }); }
        r.ValidationStatus = rep.Status; r.ValidationResult = Json.ToDoc(rep.Issues); await _db.SaveChangesAsync(ct);
        return Success(rep);
    }

    [HttpGet("{id:guid}/column-mapping")]
    public async Task<IActionResult> GetMapping(Guid id, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        var g = await _db.SyncGroups.Include(x => x.Endpoints).ThenInclude(e => e.Connection).ThenInclude(c => c!.Secret).FirstAsync(x => x.Id == r.SyncGroupId, ct);
        var pa = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "A").Connection!); var pb = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "B").Connection!);
        var sa = await _registry.Get(pa.DbType).GetTableSchemaAsync(pa, r.EndpointASchema, r.EndpointATable, ct);
        var sb = await _registry.Get(pb.DbType).GetTableSchemaAsync(pb, r.EndpointBSchema, r.EndpointBTable, ct);
        return Success(new
        {
            mappings = r.ColumnMappings.OrderBy(m => sa.Find(m.EndpointAColumn)?.Ordinal ?? 999).Select(m => new { m.EndpointAColumn, m.EndpointBColumn, m.IsPrimaryKey, m.IsEnabled, m.DataTypeStrategy }),
            columnsA = sa.Columns.Select(c => new { c.Name, c.NativeType, c.LogicalType, c.IsPrimaryKey, c.IsNullable }),
            columnsB = sb.Columns.Select(c => new { c.Name, c.NativeType, c.LogicalType, c.IsPrimaryKey, c.IsNullable }),
        });
    }

    [HttpPut("{id:guid}/column-mapping"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> PutMapping(Guid id, List<ColumnMappingItem> items, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct); await EnsureEditableAsync(r, ct);
        if (items.Count == 0) throw new ApiException(400, "NO_COLUMNS", "Minimal satu kolom.");
        if (items.GroupBy(i => i.EndpointAColumn.ToLower()).Any(g => g.Count() > 1)) throw new ApiException(400, "DUPLICATE_COLUMN", "Kolom A terpetakan lebih dari sekali.");
        _db.SyncColumnMappings.RemoveRange(r.ColumnMappings);
        foreach (var i in items)
            _db.SyncColumnMappings.Add(new SyncColumnMapping { TableRuleId = id, EndpointAColumn = i.EndpointAColumn, EndpointBColumn = i.EndpointBColumn, IsPrimaryKey = i.IsPrimaryKey, IsEnabled = i.IsEnabled, DataTypeStrategy = i.DataTypeStrategy });
        r.ValidationStatus = "UNVALIDATED"; r.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await Log("UPDATE_COLUMN_MAPPING", "SyncTableRule", id.ToString(), null, new { count = items.Count });
        return Success(null, "Mapping kolom disimpan; jalankan validasi.");
    }
}
