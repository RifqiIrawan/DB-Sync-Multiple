using System.Data.Common;
using System.Text.Json;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DBSync.SyncEngine.Queue;

/// <summary>Mengambil run dari antrean durable (PostgreSQL, SKIP LOCKED + lease + fencing token) dan mengeksekusinya.</summary>
public sealed class RunQueueWorker : BackgroundService
{
    private readonly IServiceScopeFactory _sf;
    private readonly RunExecutor _executor;
    private readonly ILogger<RunQueueWorker> _log;
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}"[..28];
    private readonly Dictionary<Task, Guid> _running = new();
    private const int MaxConcurrentRuns = 2;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;

    public RunQueueWorker(IServiceScopeFactory sf, RunExecutor executor, ILogger<RunQueueWorker> log) { _sf = sf; _executor = executor; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("RunQueueWorker {Id} dimulai", _workerId);
        await Task.Delay(1500, ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var done in _running.Keys.Where(t => t.IsCompleted).ToList()) _running.Remove(done);
                await HeartbeatAsync(ct);
                await RecoverExpiredAsync(ct);
                while (_running.Count < MaxConcurrentRuns)
                {
                    var claim = await ClaimAsync(ct);
                    if (claim == null) break;
                    var (id, token) = claim.Value;
                    _log.LogInformation("Run {Run} diambil (fencing {Token})", id, token);
                    _running[Task.Run(() => _executor.ExecuteAsync(id, _workerId, token, ct), CancellationToken.None)] = id;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Loop worker error"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
        }
        try { await Task.WhenAll(_running.Keys).WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastHeartbeat < TimeSpan.FromSeconds(10)) return;
        using var s = _sf.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
        var hb = await db.WorkerHeartbeats.FindAsync(new object[] { _workerId }, ct);
        if (hb == null)   // proses baru: buang catatan proses lama pada host yang sama yang sudah tidak berdetak
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-45);
            await db.WorkerHeartbeats.Where(w => w.Hostname == Environment.MachineName && w.WorkerType == "SYNC" && w.WorkerId != _workerId && w.LastHeartbeatAt < cutoff).ExecuteDeleteAsync(ct);
        }
        var meta = JsonSerializer.SerializeToDocument(new { runningRuns = _running.Values.ToArray(), maxConcurrentRuns = MaxConcurrentRuns, pid = Environment.ProcessId });
        if (hb == null) db.WorkerHeartbeats.Add(new WorkerHeartbeat { WorkerId = _workerId, WorkerType = "SYNC", Hostname = Environment.MachineName, Metadata = meta });
        else { hb.LastHeartbeatAt = DateTimeOffset.UtcNow; hb.Status = "RUNNING"; hb.Metadata = meta; }
        await db.SaveChangesAsync(ct);
        _lastHeartbeat = DateTimeOffset.UtcNow;
    }

    /// <summary>Run RUNNING dengan lease kedaluwarsa (worker mati) dikembalikan ke antrean; initial sync lanjut dari checkpoint.</summary>
    private async Task RecoverExpiredAsync(CancellationToken ct)
    {
        using var s = _sf.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
        var n = await db.Database.ExecuteSqlRawAsync(
            @"UPDATE sync_runs SET status = CASE WHEN cancel_requested THEN 'CANCELLED' ELSE 'QUEUED' END, locked_by = NULL, lease_expires_at = NULL,
                     finished_at = CASE WHEN cancel_requested THEN now() ELSE finished_at END
              WHERE status IN ('RUNNING','CANCELLING') AND lease_expires_at < now()", ct);
        if (n > 0) _log.LogWarning("{N} run dengan lease kedaluwarsa dipulihkan", n);
        await db.Database.ExecuteSqlRawAsync("UPDATE worker_heartbeats SET status='STALE' WHERE last_heartbeat_at < now() - interval '60 seconds' AND status='RUNNING'", ct);
    }

    private async Task<(Guid Id, long Token)?> ClaimAsync(CancellationToken ct)
    {
        using var s = _sf.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE sync_runs SET status='RUNNING', locked_by=@w, lease_expires_at=now()+interval '60 seconds',
                       fencing_token=fencing_token+1, started_at=COALESCE(started_at, now())
                WHERE id = (SELECT id FROM sync_runs r WHERE r.status='QUEUED' AND NOT cancel_requested
                              AND NOT EXISTS (SELECT 1 FROM sync_runs x WHERE x.sync_group_id=r.sync_group_id AND x.status IN ('RUNNING','CANCELLING'))
                            ORDER BY r.queued_at FOR UPDATE SKIP LOCKED LIMIT 1)
                RETURNING id, fencing_token";
            var p = cmd.CreateParameter(); p.ParameterName = "w"; p.Value = _workerId; cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) return (r.GetGuid(0), r.GetInt64(1));
            return null;
        }
        finally { await conn.CloseAsync(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var s = _sf.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE worker_heartbeats SET status='STOPPED', last_heartbeat_at=now() WHERE worker_id={0}", new object[] { _workerId });
        }
        catch { }
    }
}
