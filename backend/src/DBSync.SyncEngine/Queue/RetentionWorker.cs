using DBSync.Application;
using DBSync.Domain;
using DBSync.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DBSync.SyncEngine.Queue;

/// <summary>Membersihkan data historis agar tabel event/audit tidak tumbuh tanpa batas. Berjalan sekali sehari (dan 2 menit setelah start).</summary>
public sealed class RetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _sf;
    private readonly RetentionOptions _opt;
    private readonly ILogger<RetentionWorker> _log;
    public RetentionWorker(IServiceScopeFactory sf, IOptions<RetentionOptions> opt, ILogger<RetentionWorker> log) { _sf = sf; _opt = opt.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Retention gagal"); }
            try { await Task.Delay(TimeSpan.FromHours(24), ct); } catch { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbSyncContext>();
        var now = DateTimeOffset.UtcNow;
        var ev = now.AddDays(-Math.Max(1, _opt.EventDays));
        var pe = now.AddDays(-Math.Max(1, _opt.ProcessedEventDays));
        var run = now.AddDays(-Math.Max(7, _opt.RunDays));
        var audit = now.AddDays(-Math.Max(30, _opt.AuditDays));

        var events = await db.SyncEvents.Where(e => (e.Status == EventStatus.Success || e.Status == EventStatus.Skipped) && e.CreatedAt < ev).ExecuteDeleteAsync(ct);
        var processed = await db.ProcessedEvents.Where(p => p.ProcessedAt < pe).ExecuteDeleteAsync(ct);
        var runs = await db.SyncRuns.Where(r => r.FinishedAt != null && r.FinishedAt < run && r.Status != RunStatus.Running && r.Status != RunStatus.Queued).ExecuteDeleteAsync(ct);
        var logs = await db.AuditLogs.Where(a => a.CreatedAt < audit).ExecuteDeleteAsync(ct);
        var tokens = await db.RefreshTokens.Where(t => t.ExpiresAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        var workers = await db.WorkerHeartbeats.Where(w => w.LastHeartbeatAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        _log.LogInformation("Retention: events={E} processed={P} runs={R} audit={A} tokens={T} workers={W}", events, processed, runs, logs, tokens, workers);
    }
}