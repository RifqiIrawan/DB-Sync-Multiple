using DBSync.API.Common;
using DBSync.Domain;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Conflict;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBSync.API.Controllers;

public record ResolveRequest(string Winner);

[Route("api/v1")]
public class MonitoringController : ApiBase
{
    private readonly DbSyncContext _db;
    private readonly ConflictService _conflicts;
    public MonitoringController(DbSyncContext db, ConflictService conflicts) { _db = db; _conflicts = conflicts; }

    [HttpGet("monitoring/dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct) => Success(await BuildDashboardAsync(_db, ct));

    public static async Task<object> BuildDashboardAsync(DbSyncContext db, CancellationToken ct)
    {
        var conns = await db.DbConnections.AsNoTracking().GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var groups = await db.SyncGroups.AsNoTracking().GroupBy(g => new { g.Status, g.SyncMode }).Select(g => new { g.Key.Status, g.Key.SyncMode, N = g.Count() }).ToListAsync(ct);
        var ev = await db.SyncEvents.AsNoTracking().GroupBy(e => e.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var conflicts = await db.SyncConflicts.CountAsync(c => c.Status == "PENDING", ct);
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var processed24h = await db.SyncEvents.CountAsync(e => e.Status == EventStatus.Success && e.CreatedAt > since, ct);
        var recent = await db.SyncRuns.AsNoTracking().OrderByDescending(r => r.QueuedAt).Take(10)
            .Select(r => new { r.Id, r.SyncGroupId, groupName = r.SyncGroup!.GroupName, r.RunType, r.Status, r.QueuedAt, r.StartedAt, r.FinishedAt, r.RecordsRead, r.RecordsFailed }).ToListAsync(ct);
        var running = await db.SyncRuns.AsNoTracking().Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.Queued || r.Status == RunStatus.Cancelling)
            .Select(r => new { r.Id, r.SyncGroupId, groupName = r.SyncGroup!.GroupName, r.RunType, r.Status, items = r.Items.Select(i => new { i.TableLabel, i.Status, i.TotalRows, i.RecordsRead, i.RecordsWritten, i.RecordsFailed }) }).ToListAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        var workers = await db.WorkerHeartbeats.AsNoTracking().OrderBy(w => w.WorkerType).Select(w => new { w.WorkerId, w.WorkerType, w.Hostname, w.Status, w.LastHeartbeatAt, online = w.LastHeartbeatAt > cutoff && w.Status == "RUNNING" }).ToListAsync(ct);
        int E(string k) => ev.GetValueOrDefault(k);
        return new
        {
            connections = new { total = conns.Sum(c => c.N), connected = conns.FirstOrDefault(c => c.Key == "CONNECTED")?.N ?? 0, disconnected = conns.FirstOrDefault(c => c.Key == "DISCONNECTED")?.N ?? 0 },
            groups = new { total = groups.Sum(g => g.N), active = groups.Where(g => g.Status == GroupStatus.Active).Sum(g => g.N), scheduled = groups.Where(g => g.Status == GroupStatus.Active && g.SyncMode == SyncModes.Scheduled).Sum(g => g.N), cdc = groups.Where(g => g.Status == GroupStatus.Active && g.SyncMode == SyncModes.Cdc).Sum(g => g.N), error = groups.Where(g => g.Status == GroupStatus.Error).Sum(g => g.N) },
            events = new { processed = E(EventStatus.Success), processed24h, pending = E(EventStatus.Pending) + E(EventStatus.Processing), retry = E(EventStatus.Retry), failed = E(EventStatus.Failed), deadLetter = E(EventStatus.DeadLetter), conflict = E(EventStatus.Conflict) },
            pendingConflicts = conflicts, recentRuns = recent, runningRuns = running, workers,
        };
    }

    [HttpGet("monitoring/events")]
    public async Task<IActionResult> Events([FromQuery] Guid? groupId, [FromQuery] Guid? tableRuleId, [FromQuery] string? status, [FromQuery] string? operation, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, s) = Page(page, pageSize);
        var q = _db.SyncEvents.AsNoTracking().AsQueryable();
        if (groupId != null) q = q.Where(e => e.SyncGroupId == groupId);
        if (tableRuleId != null) q = q.Where(e => e.TableRuleId == tableRuleId);
        if (!string.IsNullOrEmpty(status)) q = q.Where(e => e.Status == status);
        if (!string.IsNullOrEmpty(operation)) q = q.Where(e => e.Operation == operation);
        if (from != null) q = q.Where(e => e.CreatedAt >= from);
        if (to != null) q = q.Where(e => e.CreatedAt <= to);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.CreatedAt).Skip((p - 1) * s).Take(s)
            .Select(e => new { e.Id, e.EventId, e.SyncGroupId, e.TableRuleId, e.RunId, e.Direction, e.Operation, e.PkKey, e.PrimaryKey, e.Status, e.RetryCount, e.NextRetryAt, e.ErrorCode, e.ErrorMessage, e.CreatedAt, e.ProcessedAt, table = _db.SyncTableRules.Where(r => r.Id == e.TableRuleId).Select(r => r.EndpointATable).FirstOrDefault() }).ToListAsync(ct);
        return Paged(items, total, p, s);
    }

    [HttpGet("monitoring/events/{id:guid}")]
    public async Task<IActionResult> EventDetail(Guid id, CancellationToken ct) =>
        Success(await _db.SyncEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new KeyNotFoundException("Event tidak ditemukan."));

    [HttpGet("monitoring/conflicts")]
    public async Task<IActionResult> Conflicts([FromQuery] string? status, [FromQuery] Guid? groupId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, s) = Page(page, pageSize);
        var q = _db.SyncConflicts.AsNoTracking().AsQueryable();
        q = q.Where(c => c.Status == (status ?? "PENDING"));
        if (groupId != null) q = q.Where(c => c.SyncGroupId == groupId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(c => c.CreatedAt).Skip((p - 1) * s).Take(s).Select(c => new
        {
            c.Id, c.SyncGroupId, c.TableRuleId, groupName = _db.SyncGroups.Where(g => g.Id == c.SyncGroupId).Select(g => g.GroupName).FirstOrDefault(),
            table = _db.SyncTableRules.Where(r => r.Id == c.TableRuleId).Select(r => r.EndpointATable).FirstOrDefault(),
            c.PrimaryKey, c.EndpointAValue, c.EndpointBValue, c.EndpointAVersion, c.EndpointBVersion, c.ConflictType, c.Status, c.ResolutionStrategy, c.ResolvedValue, c.ResolvedBy,
            resolvedByName = _db.Users.Where(u => u.Id == c.ResolvedBy).Select(u => u.Username).FirstOrDefault(), c.ResolvedAt, c.CreatedAt,
        }).ToListAsync(ct);
        return Paged(items, total, p, s);
    }

    [HttpPost("monitoring/conflicts/{id:guid}/resolve"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> Resolve(Guid id, ResolveRequest req, CancellationToken ct)
    {
        var w = req.Winner?.ToUpperInvariant();
        if (w is not ("A" or "B")) throw new ApiException(400, "INVALID_WINNER", "winner harus A atau B.", "winner");
        var c = await _conflicts.ResolveAsync(id, w[0], UserId, ct);
        await Log("RESOLVE_CONFLICT", "SyncConflict", id.ToString(), null, new { winner = w, c.PkKey });
        return Success(new { c.Id, c.Status, c.ResolutionStrategy }, "Konflik diselesaikan");
    }

    [HttpGet("monitoring/dead-letters")]
    public async Task<IActionResult> DeadLetters([FromQuery] Guid? groupId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, s) = Page(page, pageSize);
        var q = _db.SyncEvents.AsNoTracking().Where(e => e.Status == EventStatus.DeadLetter);
        if (groupId != null) q = q.Where(e => e.SyncGroupId == groupId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.CreatedAt).Skip((p - 1) * s).Take(s)
            .Select(e => new { e.Id, e.SyncGroupId, e.TableRuleId, e.Direction, e.Operation, e.PkKey, e.Payload, e.RetryCount, e.ErrorCode, e.ErrorMessage, e.CreatedAt, table = _db.SyncTableRules.Where(r => r.Id == e.TableRuleId).Select(r => r.EndpointATable).FirstOrDefault() }).ToListAsync(ct);
        return Paged(items, total, p, s);
    }

    /// <summary>Retry: event dikembalikan ke RETRY. Scheduled engine akan mengulang baris itu pada run berikutnya; CDC retry worker mengulang dari payload.</summary>
    [HttpPost("monitoring/dead-letters/{id:guid}/retry"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var e = await _db.SyncEvents.FirstOrDefaultAsync(x => x.Id == id && x.Status == EventStatus.DeadLetter, ct) ?? throw new KeyNotFoundException("Dead letter tidak ditemukan.");
        if (!EventStatus.CanTransition(e.Status, EventStatus.Retry)) throw new ApiException(409, "INVALID_STATE", "Transisi tidak diizinkan.");
        e.Status = EventStatus.Retry; e.RetryCount = 0; e.NextRetryAt = null; e.ErrorMessage = "Di-retry oleh operator";
        await _db.SaveChangesAsync(ct);
        await Log("RETRY_DEAD_LETTER", "SyncEvent", id.ToString());
        return Accepted202(new { id, status = e.Status }, "Event dijadwalkan ulang");
    }

    [HttpGet("monitoring/workers")]
    public async Task<IActionResult> Workers(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        return Success(await _db.WorkerHeartbeats.AsNoTracking().OrderBy(w => w.WorkerType).ThenBy(w => w.Hostname)
            .Select(w => new { w.WorkerId, w.WorkerType, w.Hostname, w.Status, w.LastHeartbeatAt, w.Metadata, online = w.LastHeartbeatAt > cutoff && w.Status == "RUNNING" }).ToListAsync(ct));
    }

    [HttpGet("audit-logs"), Authorize(Policy = Policies.Audit)]
    public async Task<IActionResult> AuditLogs([FromQuery] string? action, [FromQuery] string? resourceType, [FromQuery] string? username, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, s) = Page(page, pageSize);
        var q = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrEmpty(resourceType)) q = q.Where(a => a.ResourceType == resourceType);
        if (!string.IsNullOrEmpty(username)) q = q.Where(a => a.Username != null && EF.Functions.ILike(a.Username, $"%{username}%"));
        if (from != null) q = q.Where(a => a.CreatedAt >= from);
        if (to != null) q = q.Where(a => a.CreatedAt <= to);
        var total = await q.CountAsync(ct);
        return Paged(await q.OrderByDescending(a => a.CreatedAt).Skip((p - 1) * s).Take(s).ToListAsync(ct), total, p, s);
    }
}
