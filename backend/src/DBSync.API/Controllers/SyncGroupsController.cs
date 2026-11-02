using System.Text.Json;
using DBSync.API.Common;
using DBSync.Application;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Cdc;
using DBSync.SyncEngine.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DBSync.API.Controllers;

public record GroupRequest(string GroupName, string? Description, string SyncDirection, string SyncMode, string? ConflictStrategy,
    Guid EndpointAConnectionId, Guid EndpointBConnectionId, int? ScheduleIntervalSeconds, int? BatchSize, int? MaxRetries, int? MaxParallelTables, int? CommandTimeoutSeconds);

[Route("api/v1/sync-groups")]
public class SyncGroupsController : ApiBase
{
    private readonly DbSyncContext _db;
    private readonly IConnectionProfileFactory _profiles;
    private readonly IConnectorRegistry _registry;
    private readonly DebeziumConnectManager _cdc;
    private readonly CdcOptions _cdcOpt;

    public SyncGroupsController(DbSyncContext db, IConnectionProfileFactory profiles, IConnectorRegistry registry, DebeziumConnectManager cdc, IOptions<CdcOptions> cdcOpt)
    { _db = db; _profiles = profiles; _registry = registry; _cdc = cdc; _cdcOpt = cdcOpt.Value; }

    // ---------- helpers ----------
    private IQueryable<SyncGroup> Full() => _db.SyncGroups.Include(g => g.Endpoints).ThenInclude(e => e.Connection).ThenInclude(c => c!.Secret);

    private async Task<SyncGroup> LoadAsync(Guid id, CancellationToken ct) =>
        await Full().FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new KeyNotFoundException("Synchronization group tidak ditemukan.");

    private static object Dto(SyncGroup g, int rules = 0) => new
    {
        g.Id, g.GroupName, g.Description, g.SyncDirection, g.SyncMode, g.ConflictStrategy, g.Status, g.ScheduleIntervalSeconds, g.BatchSize, g.MaxRetries,
        g.MaxParallelTables, g.CommandTimeoutSeconds, g.CreatedAt, g.UpdatedAt, tableCount = rules,
        endpointA = g.Endpoints.Where(e => e.EndpointRole == "A").Select(e => new { e.ConnectionId, e.Connection!.ConnectionName, e.Connection.DbType, e.Connection.DatabaseName, e.Connection.Host, e.Connection.Status }).FirstOrDefault(),
        endpointB = g.Endpoints.Where(e => e.EndpointRole == "B").Select(e => new { e.ConnectionId, e.Connection!.ConnectionName, e.Connection.DbType, e.Connection.DatabaseName, e.Connection.Host, e.Connection.Status }).FirstOrDefault(),
    };

    private static object RuleDto(SyncTableRule r) => new
    {
        r.Id, r.SyncGroupId, r.EndpointASchema, r.EndpointATable, r.EndpointBSchema, r.EndpointBTable, r.SyncAToB, r.SyncBToA, r.SyncInsert, r.SyncUpdate, r.SyncDelete,
        r.WriteMode, r.InitialSyncStatus, r.ValidationStatus, r.ValidationResult, r.UpdatedAtColumn, r.IsEnabled, r.CreatedAt, r.UpdatedAt,
    };

    private void ValidateRequest(GroupRequest r)
    {
        var e = new List<ApiError>();
        if (string.IsNullOrWhiteSpace(r.GroupName)) e.Add(new("REQUIRED", "groupName", "Nama group wajib."));
        if (!Directions.All.Contains(r.SyncDirection)) e.Add(new("INVALID", "syncDirection", "Arah harus A_TO_B, B_TO_A, atau BIDIRECTIONAL."));
        if (r.SyncMode == SyncModes.Hybrid) e.Add(new("HYBRID_UNSUPPORTED", "syncMode", "Mode HYBRID belum diaktifkan: ownership perubahan & deduplication antar-engine belum dirancang. Pilih SCHEDULED atau CDC."));
        else if (!SyncModes.All.Contains(r.SyncMode)) e.Add(new("INVALID", "syncMode", "Mode harus SCHEDULED atau CDC."));
        if (r.ConflictStrategy != null && !ConflictStrategies.All.Contains(r.ConflictStrategy)) e.Add(new("INVALID", "conflictStrategy", "Strategi konflik tidak valid."));
        if (r.EndpointAConnectionId == r.EndpointBConnectionId) e.Add(new("SAME_ENDPOINT", "endpointBConnectionId", "Endpoint A dan B harus berbeda."));
        if (r.ScheduleIntervalSeconds is < 60) e.Add(new("INVALID", "scheduleIntervalSeconds", "Interval minimum 60 detik."));
        if (r.BatchSize is < 1 or > 10000) e.Add(new("INVALID", "batchSize", "Batch size 1-10000."));
        if (e.Count > 0) throw new ApiException(400, "Validation failed", e);
    }

    // ---------- CRUD ----------
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? search, CancellationToken ct)
    {
        var q = Full().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(g => g.Status == status);
        if (!string.IsNullOrEmpty(search)) q = q.Where(g => EF.Functions.ILike(g.GroupName, $"%{search}%"));
        var list = await q.OrderBy(g => g.GroupName).ToListAsync(ct);
        var counts = await _db.SyncTableRules.GroupBy(r => r.SyncGroupId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return Success(list.Select(g => Dto(g, counts.GetValueOrDefault(g.Id))).ToList());
    }

    [HttpPost, Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Create(GroupRequest r, CancellationToken ct)
    {
        ValidateRequest(r);
        var ca = await _db.DbConnections.FindAsync(new object[] { r.EndpointAConnectionId }, ct) ?? throw new ApiException(400, "NOT_FOUND", "Koneksi Endpoint A tidak ditemukan.", "endpointAConnectionId");
        var cb = await _db.DbConnections.FindAsync(new object[] { r.EndpointBConnectionId }, ct) ?? throw new ApiException(400, "NOT_FOUND", "Koneksi Endpoint B tidak ditemukan.", "endpointBConnectionId");
        var g = new SyncGroup
        {
            GroupName = r.GroupName.Trim(), Description = r.Description, SyncDirection = r.SyncDirection, SyncMode = r.SyncMode,
            ConflictStrategy = r.ConflictStrategy ?? ConflictStrategies.Manual, ScheduleIntervalSeconds = r.ScheduleIntervalSeconds ?? 300,
            BatchSize = r.BatchSize ?? 500, MaxRetries = r.MaxRetries ?? 3, MaxParallelTables = r.MaxParallelTables ?? 2, CommandTimeoutSeconds = r.CommandTimeoutSeconds ?? 60,
        };
        g.Endpoints.Add(new SyncEndpoint { SyncGroupId = g.Id, ConnectionId = ca.Id, EndpointRole = "A" });
        g.Endpoints.Add(new SyncEndpoint { SyncGroupId = g.Id, ConnectionId = cb.Id, EndpointRole = "B" });
        _db.SyncGroups.Add(g); await _db.SaveChangesAsync(ct);
        await Log("CREATE_GROUP", "SyncGroup", g.Id.ToString(), null, r);
        return Created201(new { g.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        return Success(Dto(g, await _db.SyncTableRules.CountAsync(x => x.SyncGroupId == id, ct)));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Update(Guid id, GroupRequest r, CancellationToken ct)
    {
        ValidateRequest(r);
        var g = await LoadAsync(id, ct);
        var before = Dto(g);
        bool structural = g.SyncMode != r.SyncMode || g.Endpoints.Any(e => (e.EndpointRole == "A" ? r.EndpointAConnectionId : r.EndpointBConnectionId) != e.ConnectionId);
        if (structural && g.Status is GroupStatus.Active or GroupStatus.Paused) throw new ApiException(409, "GROUP_ACTIVE", "Mode/endpoint hanya dapat diubah saat group DRAFT atau STOPPED.");
        g.GroupName = r.GroupName.Trim(); g.Description = r.Description; g.SyncDirection = r.SyncDirection; g.SyncMode = r.SyncMode;
        g.ConflictStrategy = r.ConflictStrategy ?? g.ConflictStrategy; g.ScheduleIntervalSeconds = r.ScheduleIntervalSeconds ?? g.ScheduleIntervalSeconds;
        g.BatchSize = r.BatchSize ?? g.BatchSize; g.MaxRetries = r.MaxRetries ?? g.MaxRetries; g.MaxParallelTables = r.MaxParallelTables ?? g.MaxParallelTables;
        g.CommandTimeoutSeconds = r.CommandTimeoutSeconds ?? g.CommandTimeoutSeconds; g.UpdatedAt = DateTimeOffset.UtcNow;
        if (structural)
        {
            foreach (var e in g.Endpoints) e.ConnectionId = e.EndpointRole == "A" ? r.EndpointAConnectionId : r.EndpointBConnectionId;
            if (g.Status != GroupStatus.Draft) g.Status = GroupStatus.Draft;
        }
        await _db.SaveChangesAsync(ct);
        await Log("UPDATE_GROUP", "SyncGroup", id.ToString(), before, r);
        return Success(Dto(await LoadAsync(id, ct)));
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        if (g.Status is GroupStatus.Active or GroupStatus.Paused) throw new ApiException(409, "GROUP_ACTIVE", "Hentikan (stop) group sebelum menghapus.");
        if (await _db.SyncRuns.AnyAsync(r => r.SyncGroupId == id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct)) throw new ApiException(409, "RUN_ACTIVE", "Masih ada run berjalan.");
        // bersihkan data turunan yang tidak bercascade
        var ruleIds = await _db.SyncTableRules.Where(r => r.SyncGroupId == id).Select(r => r.Id).ToListAsync(ct);
        await _db.SyncRowStates.Where(x => ruleIds.Contains(x.TableRuleId)).ExecuteDeleteAsync(ct);
        await _db.SyncConflicts.Where(x => x.SyncGroupId == id).ExecuteDeleteAsync(ct);
        await _db.SyncEvents.Where(x => x.SyncGroupId == id).ExecuteDeleteAsync(ct);
        await _db.SyncRuns.Where(x => x.SyncGroupId == id).ExecuteDeleteAsync(ct);
        _db.SyncGroups.Remove(g); await _db.SaveChangesAsync(ct);
        await Log("DELETE_GROUP", "SyncGroup", id.ToString(), new { g.GroupName });
        return Success(null, "Group dihapus");
    }

    // ---------- lifecycle ----------
    private async Task<List<ValidationIssue>> ValidateGroupAsync(SyncGroup g, bool persistRules, CancellationToken ct)
    {
        var issues = new List<ValidationIssue>();
        var ea = g.Endpoints.FirstOrDefault(e => e.EndpointRole == "A"); var eb = g.Endpoints.FirstOrDefault(e => e.EndpointRole == "B");
        if (ea == null || eb == null) { issues.Add(new("error", "ENDPOINTS", "Group harus memiliki tepat satu Endpoint A dan satu Endpoint B.")); return issues; }
        if (ea.ConnectionId == eb.ConnectionId) issues.Add(new("error", "SAME_ENDPOINT", "Endpoint A dan B harus berbeda."));

        foreach (var (role, ep) in new[] { ("A", ea), ("B", eb) })
        {
            var res = await _registry.Get(ep.Connection!.DbType).TestConnectionAsync(_profiles.Create(ep.Connection), ct);
            if (!res.Success) issues.Add(new("error", "CONNECTION_FAILED", $"Endpoint {role} ({ep.Connection.ConnectionName}): {res.Message}"));
        }

        // topologi: tolak siklus antar-group (mis. A-B, B-C, C-A) dan group duplikat
        var others = await _db.SyncEndpoints.AsNoTracking().Where(e => e.SyncGroupId != g.Id && e.SyncGroup!.Status != GroupStatus.Stopped && e.SyncGroup.Status != GroupStatus.Draft)
            .Select(e => new { e.SyncGroupId, e.ConnectionId, e.EndpointRole }).ToListAsync(ct);
        var edges = others.GroupBy(o => o.SyncGroupId).Where(x => x.Count() == 2).Select(x => (x.First().ConnectionId, x.Last().ConnectionId)).ToList();
        if (edges.Any(e => (e.Item1 == ea.ConnectionId && e.Item2 == eb.ConnectionId) || (e.Item1 == eb.ConnectionId && e.Item2 == ea.ConnectionId)))
            issues.Add(new("error", "TOPOLOGY_DUPLICATE", "Pasangan database yang sama sudah dipakai group aktif lain."));
        edges.Add((ea.ConnectionId, eb.ConnectionId));
        var parent = new Dictionary<Guid, Guid>();
        Guid Find(Guid x) { if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; } return p == x ? x : parent[x] = Find(p); }
        foreach (var (x, y) in edges)
        {
            var rx = Find(x); var ry = Find(y);
            if (rx == ry) { issues.Add(new("error", "TOPOLOGY_CYCLE", "Group ini membentuk jalur propagasi melingkar dengan group aktif lain. V1 menolak topologi siklus.")); break; }
            parent[rx] = ry;
        }
        if (others.Any(o => o.ConnectionId == ea.ConnectionId || o.ConnectionId == eb.ConnectionId))
            issues.Add(new("warning", "SHARED_DATABASE", "Salah satu database juga dipakai group aktif lain. Pastikan tabel yang disinkronkan tidak beririsan."));

        var rules = await _db.SyncTableRules.Include(r => r.ColumnMappings).Where(r => r.SyncGroupId == g.Id).ToListAsync(ct);
        if (!rules.Any(r => r.IsEnabled)) issues.Add(new("error", "NO_TABLES", "Belum ada tabel yang dipilih untuk disinkronkan."));
        foreach (var rule in rules.Where(r => r.IsEnabled))
        {
            var rep = await ValidateRuleAsync(g, rule, ct);
            if (persistRules) { rule.ValidationStatus = rep.Status; rule.ValidationResult = Json.ToDoc(rep.Issues); }
            foreach (var i in rep.Issues.Where(i => i.Severity == "error"))
                issues.Add(new("error", i.Code, $"{rule.EndpointATable}: {i.Message}", i.Field));
            if (rep.Issues.Any(i => i.Severity == "warning")) issues.Add(new("warning", "TABLE_WARNINGS", $"{rule.EndpointATable}: {rep.Issues.Count(i => i.Severity == "warning")} peringatan."));
            if (!rule.InitialSyncStatus.StartsWith("COMPLETED")) issues.Add(new("warning", "INITIAL_PENDING", $"{rule.EndpointATable}: initial sync belum dijalankan; tabel dilewati sampai selesai."));
        }
        if (persistRules) await _db.SaveChangesAsync(ct);

        if (g.SyncMode == SyncModes.Cdc)
        {
            if (!_cdcOpt.Enabled) issues.Add(new("error", "CDC_DISABLED", "Cdc:Enabled=false pada konfigurasi; aktifkan dan jalankan Kafka + Debezium."));
            else
                foreach (var ep in new[] { ea, eb })
                    foreach (var c in await _cdc.CheckPrerequisitesAsync(ep.ConnectionId, ct))
                        if (!c.Ok) issues.Add(new("error", "CDC_PREREQ", $"{ep.Connection!.ConnectionName}: {c.Name} â€” {c.Detail}"));
        }
        return issues;
    }

    private async Task<ValidationReport> ValidateRuleAsync(SyncGroup g, SyncTableRule rule, CancellationToken ct)
    {
        try
        {
            var ea = g.Endpoints.First(e => e.EndpointRole == "A").Connection!; var eb = g.Endpoints.First(e => e.EndpointRole == "B").Connection!;
            var pa = _profiles.Create(ea); var pb = _profiles.Create(eb);
            var sa = await _registry.Get(pa.DbType).GetTableSchemaAsync(pa, rule.EndpointASchema, rule.EndpointATable, ct);
            var sb = await _registry.Get(pb.DbType).GetTableSchemaAsync(pb, rule.EndpointBSchema, rule.EndpointBTable, ct);
            var maps = rule.ColumnMappings.Count > 0 ? rule.ColumnMappings.ToList() : await _db.SyncColumnMappings.Where(m => m.TableRuleId == rule.Id).ToListAsync(ct);
            return RuleValidator.Validate(sa, sb, maps);
        }
        catch (ConnectorException ex)
        {
            return ValidationReport.From(new() { new("error", ex.Code, ex.Message) });
        }
    }

    [HttpPost("{id:guid}/validate"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        var issues = await ValidateGroupAsync(g, true, ct);
        return Success(ValidationReport.From(issues));
    }

    [HttpPost("{id:guid}/activate"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        if (g.Status == GroupStatus.Active) throw new ApiException(409, "ALREADY_ACTIVE", "Group sudah aktif.");
        g.Status = GroupStatus.Validating; await _db.SaveChangesAsync(ct);
        var issues = await ValidateGroupAsync(g, true, ct);
        var errors = issues.Where(i => i.Severity == "error").ToList();
        if (errors.Count > 0)
        {
            g.Status = GroupStatus.Draft; await _db.SaveChangesAsync(ct);
            throw new ApiException(422, "Validasi gagal; group tidak diaktifkan", errors.Select(e => new ApiError(e.Code, e.Field, e.Message)).ToList());
        }
        if (g.SyncMode == SyncModes.Cdc)
        {
            try { foreach (var ep in g.Endpoints) await _cdc.EnsureConnectorAsync(ep.ConnectionId, ct); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                g.Status = GroupStatus.Error; await _db.SaveChangesAsync(ct);
                throw new ApiException(502, "CDC_SETUP_FAILED", "Gagal menyiapkan connector Debezium: " + ex.Message);
            }
        }
        g.Status = GroupStatus.Active; g.UpdatedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct);
        await Log("ACTIVATE_GROUP", "SyncGroup", id.ToString());
        return Success(new { status = g.Status, warnings = issues.Where(i => i.Severity == "warning") });
    }

    private async Task<IActionResult> Transition(Guid id, string from, string to, string action, CancellationToken ct, bool cancelQueued = false)
    {
        var g = await LoadAsync(id, ct);
        var allowed = from.Split(',');
        if (!allowed.Contains(g.Status)) throw new ApiException(409, "INVALID_STATE", $"Group berstatus {g.Status}; aksi {action} hanya untuk {from}.");
        g.Status = to; g.UpdatedAt = DateTimeOffset.UtcNow;
        if (cancelQueued)
        {
            // run yang belum dimulai dibatalkan; run berjalan diminta berhenti
            await _db.SyncRuns.Where(r => r.SyncGroupId == id && r.Status == RunStatus.Queued).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Cancelled).SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow), ct);
            await _db.SyncRuns.Where(r => r.SyncGroupId == id && r.Status == RunStatus.Running).ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequested, true).SetProperty(r => r.Status, RunStatus.Cancelling), ct);
        }
        await _db.SaveChangesAsync(ct);
        await Log(action, "SyncGroup", id.ToString());
        return Success(new { status = g.Status });
    }

    [HttpPost("{id:guid}/pause"), Authorize(Policy = Policies.Operate)]
    public Task<IActionResult> Pause(Guid id, CancellationToken ct) => Transition(id, GroupStatus.Active, GroupStatus.Paused, "PAUSE_GROUP", ct);

    [HttpPost("{id:guid}/resume"), Authorize(Policy = Policies.Operate)]
    public Task<IActionResult> Resume(Guid id, CancellationToken ct) => Transition(id, GroupStatus.Paused, GroupStatus.Active, "RESUME_GROUP", ct);

    [HttpPost("{id:guid}/stop"), Authorize(Policy = Policies.Manage)]
    public Task<IActionResult> Stop(Guid id, CancellationToken ct) => Transition(id, "ACTIVE,PAUSED,ERROR,VALIDATING", GroupStatus.Stopped, "STOP_GROUP", ct, true);

    // ---------- table discovery & rules ----------
    [HttpGet("{id:guid}/available-tables")]
    public async Task<IActionResult> AvailableTables(Guid id, [FromQuery] string? schemaA, [FromQuery] string? schemaB, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        var ea = g.Endpoints.First(e => e.EndpointRole == "A").Connection!; var eb = g.Endpoints.First(e => e.EndpointRole == "B").Connection!;
        var pa = _profiles.Create(ea); var pb = _profiles.Create(eb);
        schemaA ??= ConnectionsController.DefaultSchema(pa); schemaB ??= ConnectionsController.DefaultSchema(pb);
        var ca = _registry.Get(pa.DbType); var cb = _registry.Get(pb.DbType);
        var ta = await ca.GetTablesAsync(pa, schemaA, ct); var tb = await cb.GetTablesAsync(pb, schemaB, ct);
        var selected = await _db.SyncTableRules.AsNoTracking().Where(r => r.SyncGroupId == id).ToDictionaryAsync(r => r.EndpointATable.ToLower(), r => r.Id, ct);

        var sem = new SemaphoreSlim(6);
        async Task<TableSchema?> Schema(IDatabaseConnector c, ConnectionProfile p, string s, string t)
        {
            await sem.WaitAsync(ct);
            try { return await c.GetTableSchemaAsync(p, s, t, ct); } catch { return null; } finally { sem.Release(); }
        }
        var names = ta.Select(t => t.Name).Union(tb.Select(t => t.Name), StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = await Task.WhenAll(names.Select(async n =>
        {
            var a = ta.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); var b = tb.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            var sa = a == null ? null : await Schema(ca, pa, schemaA, a.Name); var sb = b == null ? null : await Schema(cb, pb, schemaB, b.Name);
            string status = a == null ? "ONLY_B" : b == null ? "ONLY_A" : "BOTH";
            bool eligible = sa != null && sb != null && sa.PrimaryKey.Count > 0 && sb.PrimaryKey.Count > 0;
            ValidationReport? rep = eligible ? RuleValidator.Validate(sa!, sb!, ColumnMappingBuilder.AutoMap(sa!, sb!)) : null;
            return new
            {
                name = n, status, schemaA, tableA = a?.Name, schemaB, tableB = b?.Name,
                aColumns = sa?.Columns.Count, bColumns = sb?.Columns.Count,
                aPrimaryKey = sa?.PrimaryKey.Select(c => c.Name), bPrimaryKey = sb?.PrimaryKey.Select(c => c.Name),
                aForeignKeys = sa?.ForeignKeys.Count, bForeignKeys = sb?.ForeignKeys.Count,
                aRows = a?.ApproxRows, bRows = b?.ApproxRows,
                eligible = eligible && rep!.Status != "INVALID",
                validation = rep?.Status ?? (status == "BOTH" ? "INVALID" : "N/A"),
                issues = rep?.Issues.Take(8) ?? (status == "BOTH" ? new List<ValidationIssue> { new("error", "PRIMARY_KEY_REQUIRED", "Tabel tanpa primary key tidak dapat disinkronkan pada V1.") } : new List<ValidationIssue> { new("error", "MISSING_TABLE", "Tabel tidak ada di salah satu sisi (DBSync tidak membuat tabel/DDL otomatis).") }),
                selected = selected.ContainsKey(n.ToLower()), ruleId = selected.TryGetValue(n.ToLower(), out var rid) ? (Guid?)rid : null,
            };
        }));
        return Success(rows);
    }

    [HttpPost("{id:guid}/discover-schema"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> DiscoverSchema(Guid id, [FromQuery] string? schemaA, [FromQuery] string? schemaB, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        int count = 0;
        foreach (var (role, schemaOverride) in new[] { ("A", schemaA), ("B", schemaB) })
        {
            var conn = g.Endpoints.First(e => e.EndpointRole == role).Connection!;
            var p = _profiles.Create(conn); var c = _registry.Get(p.DbType);
            var schema = schemaOverride ?? ConnectionsController.DefaultSchema(p);
            foreach (var t in await c.GetTablesAsync(p, schema, ct))
            {
                var ts = await c.GetTableSchemaAsync(p, schema, t.Name, ct);
                var doc = Json.ToDoc(ts);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(doc.RootElement.GetRawText())));
                var last = await _db.SchemaSnapshots.Where(s => s.ConnectionId == conn.Id && s.SchemaName == schema && s.TableName == t.Name).OrderByDescending(s => s.DiscoveredAt).FirstOrDefaultAsync(ct);
                if (last?.SchemaHash == hash) continue;
                _db.SchemaSnapshots.Add(new SchemaSnapshot { ConnectionId = conn.Id, SchemaName = schema, TableName = t.Name, SchemaHash = hash, SchemaDefinition = doc });
                count++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return Success(new { snapshotsStored = count }, "Schema discovery selesai");
    }

    public record RuleRequest(string EndpointASchema, string EndpointATable, string EndpointBSchema, string EndpointBTable, bool? SyncAToB, bool? SyncBToA,
        bool? SyncInsert, bool? SyncUpdate, bool? SyncDelete, string? WriteMode, string? UpdatedAtColumn, bool? IsEnabled);
    public record BulkRuleRequest(string? SchemaA, string? SchemaB, string[]? Tables, bool SelectAllEligible);

    private async Task<SyncTableRule> BuildRuleAsync(SyncGroup g, RuleRequest r, CancellationToken ct)
    {
        var pa = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "A").Connection!); var pb = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "B").Connection!);
        var sa = await _registry.Get(pa.DbType).GetTableSchemaAsync(pa, r.EndpointASchema, r.EndpointATable, ct);
        var sb = await _registry.Get(pb.DbType).GetTableSchemaAsync(pb, r.EndpointBSchema, r.EndpointBTable, ct);
        var rule = new SyncTableRule
        {
            SyncGroupId = g.Id, EndpointASchema = r.EndpointASchema, EndpointATable = sa.Name, EndpointBSchema = r.EndpointBSchema, EndpointBTable = sb.Name,
            SyncAToB = r.SyncAToB ?? true, SyncBToA = r.SyncBToA ?? true, SyncInsert = r.SyncInsert ?? true, SyncUpdate = r.SyncUpdate ?? true, SyncDelete = r.SyncDelete ?? true,
            WriteMode = r.WriteMode == "INSERT_ONLY" ? "INSERT_ONLY" : "UPSERT", UpdatedAtColumn = r.UpdatedAtColumn, IsEnabled = r.IsEnabled ?? true,
        };
        foreach (var m in ColumnMappingBuilder.AutoMap(sa, sb)) { m.TableRuleId = rule.Id; rule.ColumnMappings.Add(m); }
        var rep = RuleValidator.Validate(sa, sb, rule.ColumnMappings.ToList());
        rule.ValidationStatus = rep.Status; rule.ValidationResult = Json.ToDoc(rep.Issues);
        return rule;
    }

    [HttpGet("{id:guid}/table-rules")]
    public async Task<IActionResult> Rules(Guid id, CancellationToken ct) =>
        Success((await _db.SyncTableRules.AsNoTracking().Where(r => r.SyncGroupId == id).OrderBy(r => r.EndpointATable).ToListAsync(ct)).Select(RuleDto).ToList());

    [HttpPost("{id:guid}/table-rules"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> AddRule(Guid id, RuleRequest r, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        if (await _db.SyncTableRules.AnyAsync(x => x.SyncGroupId == id && x.EndpointATable == r.EndpointATable && x.EndpointASchema == r.EndpointASchema, ct))
            throw new ApiException(409, "DUPLICATE", "Tabel sudah dipilih.");
        var rule = await BuildRuleAsync(g, r, ct);
        _db.SyncTableRules.Add(rule); await _db.SaveChangesAsync(ct);
        await Log("ADD_TABLE_RULE", "SyncTableRule", rule.Id.ToString(), null, r);
        return Created201(RuleDto(rule));
    }

    [HttpPost("{id:guid}/table-rules/bulk"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> AddRulesBulk(Guid id, BulkRuleRequest r, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        var pa = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "A").Connection!); var pb = _profiles.Create(g.Endpoints.First(e => e.EndpointRole == "B").Connection!);
        var schemaA = r.SchemaA ?? ConnectionsController.DefaultSchema(pa); var schemaB = r.SchemaB ?? ConnectionsController.DefaultSchema(pb);
        var ta = (await _registry.Get(pa.DbType).GetTablesAsync(pa, schemaA, ct)).Select(t => t.Name).ToList();
        var tb = (await _registry.Get(pb.DbType).GetTablesAsync(pb, schemaB, ct)).Select(t => t.Name).ToList();
        var names = (r.SelectAllEligible ? ta : (r.Tables ?? Array.Empty<string>()).ToList()).Where(n => tb.Any(b => b.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        var existing = (await _db.SyncTableRules.Where(x => x.SyncGroupId == id).Select(x => x.EndpointATable.ToLower()).ToListAsync(ct)).ToHashSet();
        var added = new List<object>(); var skipped = new List<object>();
        foreach (var n in names)
        {
            if (existing.Contains(n.ToLower())) { skipped.Add(new { table = n, reason = "sudah dipilih" }); continue; }
            try
            {
                var rule = await BuildRuleAsync(g, new RuleRequest(schemaA, ta.First(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)), schemaB, tb.First(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)), null, null, null, null, null, null, null, null), ct);
                if (r.SelectAllEligible && rule.ValidationStatus == "INVALID") { skipped.Add(new { table = n, reason = "tidak memenuhi syarat (lihat validasi)" }); continue; }
                _db.SyncTableRules.Add(rule); added.Add(new { table = n, rule.ValidationStatus });
            }
            catch (ConnectorException ex) { skipped.Add(new { table = n, reason = ex.Message }); }
        }
        await _db.SaveChangesAsync(ct);
        await Log("ADD_TABLE_RULES_BULK", "SyncGroup", id.ToString(), null, new { added = added.Count, skipped = skipped.Count });
        return Success(new { added, skipped });
    }

    // ---------- CDC ----------
    [HttpGet("{id:guid}/cdc")]
    public async Task<IActionResult> CdcStatus(Guid id, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        if (!_cdcOpt.Enabled) return Success(new { enabled = false, message = "Cdc:Enabled=false" });
        var list = new List<object>();
        foreach (var ep in g.Endpoints.OrderBy(e => e.EndpointRole))
        {
            ConnectorStatusInfo? st = null; string? err = null; IReadOnlyList<CdcCheck>? pre = null;
            try { st = await _cdc.GetStatusAsync(ep.ConnectionId, ct); } catch (Exception ex) { err = ex.Message; }
            try { pre = await _cdc.CheckPrerequisitesAsync(ep.ConnectionId, ct); } catch (Exception ex) { err ??= ex.Message; }
            list.Add(new { role = ep.EndpointRole, connection = ep.Connection!.ConnectionName, connector = st, prerequisites = pre, error = err });
        }
        return Success(new { enabled = true, endpoints = list });
    }

    [HttpPost("{id:guid}/cdc/setup"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> CdcSetup(Guid id, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        if (g.SyncMode != SyncModes.Cdc) throw new ApiException(400, "NOT_CDC", "Group bukan mode CDC.");
        var names = new List<string>();
        foreach (var ep in g.Endpoints) names.Add(await _cdc.EnsureConnectorAsync(ep.ConnectionId, ct));
        await Log("CDC_SETUP", "SyncGroup", id.ToString(), null, new { connectors = names });
        return Success(new { connectors = names });
    }
}
