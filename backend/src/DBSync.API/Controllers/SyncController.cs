using DBSync.API.Common;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBSync.API.Controllers;

public record InitialSyncRequest(Guid[]? TableRuleIds, string? SourceRole, bool? Restart, string? ConflictMode, string? DifferentPolicy = null, string? FillFallback = null);
public record CompareRequest(Guid TableRuleId, string Kind, int? Limit);
public record InitialPreviewRequest(Guid[]? TableRuleIds, string? SourceRole);

[Route("api/v1")]
public class SyncController : ApiBase
{
    private readonly DbSyncContext _db;
    private readonly DBSync.SyncEngine.IRuleContextFactory _factory;
    private readonly DBSync.SyncEngine.InitialSync.InitialSyncService _initial;
    public SyncController(DbSyncContext db, DBSync.SyncEngine.IRuleContextFactory factory, DBSync.SyncEngine.InitialSync.InitialSyncService initial)
    { _db = db; _factory = factory; _initial = initial; }

    /// <summary>Periksa (TANPA menulis) apa yang akan terjadi bila initial sync dijalankan: baris baru, identik, dan bentrok (PK sama, isi berbeda).</summary>
    /// <summary>Cek data (read-only): daftar baris yang belum ada di satu sisi (ONLY_A / ONLY_B) atau tidak sama (DIFFERENT, per kolom).</summary>
    [HttpPost("sync-groups/{id:guid}/compare"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> Compare(Guid id, CompareRequest req, CancellationToken ct)
    {
        if (!await _db.SyncTableRules.AnyAsync(r => r.Id == req.TableRuleId && r.SyncGroupId == id, ct)) throw new KeyNotFoundException("Tabel tidak ditemukan pada grup ini.");
        var ctx = await _factory.CreateAsync(req.TableRuleId, ct);
        return Success(await _initial.CompareDetailAsync(ctx, (req.Kind ?? "").ToUpperInvariant(), req.Limit ?? 50, ct));
    }

    [HttpPost("sync-groups/{id:guid}/initial-sync/preview"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> InitialPreview(Guid id, InitialPreviewRequest? req, CancellationToken ct)
    {
        req ??= new(null, null);
        var srcName = (req.SourceRole ?? "A").ToUpperInvariant();
        char src = srcName == "BOTH" ? 'A' : srcName[0];   // pratinjau gabungan dua arah memakai perbandingan simetris
        if (src is not ('A' or 'B')) throw new ApiException(400, "INVALID", "sourceRole harus A atau B.", "sourceRole");
        var rules = await _db.SyncTableRules.AsNoTracking()
            .Where(r => r.SyncGroupId == id && r.IsEnabled && (req.TableRuleIds == null || req.TableRuleIds.Length == 0 || req.TableRuleIds.Contains(r.Id)))
            .OrderBy(r => r.EndpointATable).ToListAsync(ct);
        if (rules.Count == 0) throw new ApiException(400, "NO_TABLES", "Belum ada tabel terpilih.");
        var list = new List<object>();
        foreach (var rule in rules)
        {
            try
            {
                var ctx = await _factory.CreateAsync(rule.Id, ct);
                var p = await _initial.PreviewAsync(ctx, src, ct);
                list.Add(new { ruleId = rule.Id, table = rule.EndpointATable, p.SourceRows, p.TargetRows, p.OnlyInSource, p.OnlyInTarget, p.Identical, p.Different, p.Fillable, error = (string?)null });
            }
            catch (Exception ex) when (ex is DBSync.Connectors.Abstractions.ConnectorException or KeyNotFoundException)
            {
                list.Add(new { ruleId = rule.Id, table = rule.EndpointATable, SourceRows = 0L, TargetRows = 0L, OnlyInSource = 0L, OnlyInTarget = 0L, Identical = 0L, Different = 0L, Fillable = (long?)null, error = (string?)ex.Message });
            }
        }
        var g = await _db.SyncGroups.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        return Success(new { sourceRole = src.ToString(), groupDirection = g.SyncDirection, tables = list, recommendedMode = g.SyncDirection == Directions.Bidirectional ? "SKIP_EXISTING" : "OVERWRITE" });
    }

    private async Task<SyncRun> EnqueueAsync(Guid groupId, string type, object? options, CancellationToken ct)
    {
        var g = await _db.SyncGroups.Include(x => x.Endpoints).FirstOrDefaultAsync(x => x.Id == groupId, ct) ?? throw new KeyNotFoundException("Group tidak ditemukan.");
        if (g.Endpoints.Count != 2) throw new ApiException(400, "ENDPOINTS", "Group harus memiliki Endpoint A dan B.");
        if (g.Status == GroupStatus.Stopped) throw new ApiException(409, "GROUP_STOPPED", "Group dihentikan.");
        if (await _db.SyncRuns.AnyAsync(r => r.SyncGroupId == groupId && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.Cancelling), ct))
            throw new ApiException(409, "RUN_ACTIVE", "Sudah ada run yang antre/berjalan untuk group ini.");
        if (!await _db.SyncTableRules.AnyAsync(r => r.SyncGroupId == groupId && r.IsEnabled, ct)) throw new ApiException(400, "NO_TABLES", "Belum ada tabel terpilih.");
        var run = new SyncRun { SyncGroupId = groupId, RunType = type, RequestedBy = UserId, Options = options == null ? null : DBSync.Application.Json.ToDoc(options) };
        _db.SyncRuns.Add(run); await _db.SaveChangesAsync(ct);
        return run;
    }

    [HttpPost("sync-groups/{id:guid}/initial-sync"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> Initial(Guid id, InitialSyncRequest? req, CancellationToken ct)
    {
        req ??= new(null, null, null, null);
        if (req.SourceRole is not (null or "A" or "B" or "BOTH")) throw new ApiException(400, "INVALID", "sourceRole harus A, B, atau BOTH (gabungkan dua arah).", "sourceRole");
        if (req.TableRuleIds is { Length: > 0 })
        {
            var valid = await _db.SyncTableRules.CountAsync(r => r.SyncGroupId == id && req.TableRuleIds.Contains(r.Id) && r.IsEnabled, ct);
            if (valid != req.TableRuleIds.Distinct().Count()) throw new ApiException(400, "INVALID_TABLES", "Sebagian tableRuleIds tidak valid untuk group ini.");
        }
        // hanya tabel valid
        var invalid = await _db.SyncTableRules.Where(r => r.SyncGroupId == id && r.IsEnabled && r.ValidationStatus == "INVALID" && (req.TableRuleIds == null || req.TableRuleIds.Length == 0 || req.TableRuleIds.Contains(r.Id))).Select(r => r.EndpointATable).ToListAsync(ct);
        if (invalid.Count > 0) throw new ApiException(422, "TABLE_INVALID", "Tabel berstatus INVALID tidak dapat di-initial-sync: " + string.Join(", ", invalid));
        if (req.DifferentPolicy is not (null or "CONFLICT" or "FILL_EMPTY" or "A_WINS" or "B_WINS")) throw new ApiException(400, "INVALID", "differentPolicy tidak valid.", "differentPolicy");
        if (req.FillFallback is not (null or "CONFLICT" or "A_WINS" or "B_WINS")) throw new ApiException(400, "INVALID", "fillFallback tidak valid.", "fillFallback");
        if (req.ConflictMode is not (null or "OVERWRITE" or "SKIP_EXISTING")) throw new ApiException(400, "INVALID", "conflictMode harus OVERWRITE atau SKIP_EXISTING.", "conflictMode");
        // default aman: pada grup dua arah jangan menimpa data tujuan yang sudah ada tanpa persetujuan eksplisit
        var grp = await _db.SyncGroups.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        if (req.SourceRole == "BOTH" && grp.SyncDirection != Directions.Bidirectional)
            throw new ApiException(400, "NOT_BIDIRECTIONAL", "Gabungkan dua arah hanya untuk grup berarah dua arah (BIDIRECTIONAL).", "sourceRole");
        var mode = req.ConflictMode ?? (grp.SyncDirection == Directions.Bidirectional ? "SKIP_EXISTING" : "OVERWRITE");
        var run = await EnqueueAsync(id, RunTypes.Initial, new { tableRuleIds = req.TableRuleIds, sourceRole = req.SourceRole ?? "A", restart = req.Restart ?? false, conflictMode = mode, differentPolicy = req.DifferentPolicy ?? "CONFLICT", fillFallback = req.FillFallback ?? "CONFLICT" }, ct);
        await Log("INITIAL_SYNC", "SyncGroup", id.ToString(), null, req);
        return Accepted202(new { runId = run.Id, jobId = run.Id, status = run.Status });
    }

    [HttpPost("sync-groups/{id:guid}/sync-now"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> SyncNow(Guid id, CancellationToken ct)
    {
        var run = await EnqueueAsync(id, RunTypes.Manual, null, ct);
        await Log("SYNC_NOW", "SyncGroup", id.ToString());
        return Accepted202(new { runId = run.Id, jobId = run.Id, status = run.Status });
    }

    [HttpGet("sync-groups/{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id, CancellationToken ct)
    {
        var g = await _db.SyncGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Group tidak ditemukan.");
        var last = await _db.SyncRuns.AsNoTracking().Where(r => r.SyncGroupId == id).OrderByDescending(r => r.QueuedAt).Select(r => new { r.Id, r.RunType, r.Status, r.QueuedAt, r.StartedAt, r.FinishedAt, r.RecordsRead, r.RecordsFailed, r.ErrorSummary }).FirstOrDefaultAsync(ct);
        var tables = await _db.SyncTableRules.AsNoTracking().Where(r => r.SyncGroupId == id).Select(r => new { r.Id, r.EndpointATable, r.InitialSyncStatus, r.ValidationStatus, r.IsEnabled }).ToListAsync(ct);
        var ev = await _db.SyncEvents.AsNoTracking().Where(e => e.SyncGroupId == id).GroupBy(e => e.Status).Select(x => new { Status = x.Key, N = x.Count() }).ToListAsync(ct);
        var conflicts = await _db.SyncConflicts.CountAsync(c => c.SyncGroupId == id && c.Status == "PENDING", ct);
        return Success(new { g.Id, g.Status, g.SyncMode, g.SyncDirection, lastRun = last, tables, events = ev.ToDictionary(x => x.Status, x => x.N), pendingConflicts = conflicts });
    }

    [HttpGet("sync-groups/{id:guid}/progress")]
    public async Task<IActionResult> Progress(Guid id, CancellationToken ct)
    {
        var run = await _db.SyncRuns.AsNoTracking().Where(r => r.SyncGroupId == id && (r.Status == RunStatus.Running || r.Status == RunStatus.Queued || r.Status == RunStatus.Cancelling))
            .OrderByDescending(r => r.QueuedAt).FirstOrDefaultAsync(ct)
            ?? await _db.SyncRuns.AsNoTracking().Where(r => r.SyncGroupId == id).OrderByDescending(r => r.QueuedAt).FirstOrDefaultAsync(ct);
        if (run == null) return Success(null);
        return Success(await ProgressDto(run, ct));
    }

    private async Task<object> ProgressDto(SyncRun run, CancellationToken ct)
    {
        var items = await _db.SyncRunItems.AsNoTracking().Where(i => i.SyncRunId == run.Id).OrderBy(i => i.TableLabel).ToListAsync(ct);
        return new
        {
            run.Id, run.SyncGroupId, run.RunType, run.Status, run.QueuedAt, run.StartedAt, run.FinishedAt, run.ErrorSummary,
            items = items.Select(i => new
            {
                i.Id, i.TableRuleId, i.TableLabel, i.Status, i.TotalRows, i.RecordsRead, i.RecordsWritten, i.RecordsSkipped, i.RecordsFailed, i.BatchNumber, i.StartedAt, i.FinishedAt, i.ErrorMessage,
                // persentase hanya bila total diketahui (jangan menampilkan persentase palsu)
                percent = i.TotalRows is > 0 && run.RunType == RunTypes.Initial ? (double?)Math.Min(100, Math.Round(100.0 * i.RecordsRead / i.TotalRows.Value, 1)) : null,
            }),
        };
    }

    [HttpGet("sync-runs")]
    public async Task<IActionResult> Runs([FromQuery] Guid? groupId, [FromQuery] string? status, [FromQuery] string? runType, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, s) = Page(page, pageSize);
        var q = _db.SyncRuns.AsNoTracking().AsQueryable();
        if (groupId != null) q = q.Where(r => r.SyncGroupId == groupId);
        if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrEmpty(runType)) q = q.Where(r => r.RunType == runType);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.QueuedAt).Skip((p - 1) * s).Take(s)
            .Select(r => new { r.Id, r.SyncGroupId, groupName = r.SyncGroup!.GroupName, r.RunType, r.Status, r.QueuedAt, r.StartedAt, r.FinishedAt, r.RecordsRead, r.RecordsInserted, r.RecordsUpdated, r.RecordsDeleted, r.RecordsFailed, r.ConflictsDetected, r.ErrorSummary }).ToListAsync(ct);
        return Paged(items, total, p, s);
    }

    [HttpGet("sync-runs/{id:guid}")]
    public async Task<IActionResult> Run(Guid id, CancellationToken ct)
    {
        var run = await _db.SyncRuns.AsNoTracking().Include(r => r.SyncGroup).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException("Run tidak ditemukan.");
        var dto = await ProgressDto(run, ct);
        return Success(new { run.Id, groupName = run.SyncGroup!.GroupName, run.RecordsRead, run.RecordsInserted, run.RecordsUpdated, run.RecordsDeleted, run.RecordsSkipped, run.RecordsFailed, run.ConflictsDetected, detail = dto });
    }

    [HttpPost("sync-runs/{id:guid}/cancel"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var run = await _db.SyncRuns.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException("Run tidak ditemukan.");
        if (run.Status == RunStatus.Queued) { run.Status = RunStatus.Cancelled; run.FinishedAt = DateTimeOffset.UtcNow; }
        else if (run.Status == RunStatus.Running) { run.CancelRequested = true; run.Status = RunStatus.Cancelling; }
        else throw new ApiException(409, "NOT_CANCELLABLE", $"Run berstatus {run.Status}.");
        await _db.SaveChangesAsync(ct);
        await Log("CANCEL_RUN", "SyncRun", id.ToString());
        return Accepted202(new { runId = id, status = run.Status }, "Pembatalan diminta");
    }
}
