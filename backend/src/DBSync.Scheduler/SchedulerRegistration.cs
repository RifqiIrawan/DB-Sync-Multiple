using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace DBSync.Scheduler;

/// <summary>
/// Dipanggil Quartz setiap 10 detik. Untuk setiap group ACTIVE (mode SCHEDULED/HYBRID) yang interval-nya sudah lewat
/// dan tidak sedang memiliki run QUEUED/RUNNING, sebuah run SCHEDULED dimasukkan ke antrean durable.
/// [DisallowConcurrentExecution] + pengecekan antrean mencegah dua job yang sama berjalan bersamaan.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ScheduleTickJob : IJob
{
    private readonly IServiceScopeFactory _sf;
    private readonly ILogger<ScheduleTickJob> _log;
    public ScheduleTickJob(IServiceScopeFactory sf, ILogger<ScheduleTickJob> log) { _sf = sf; _log = log; }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        using var scope = _sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbSyncContext>();
        var groups = await db.SyncGroups.AsNoTracking()
            .Where(g => g.Status == GroupStatus.Active && (g.SyncMode == SyncModes.Scheduled || g.SyncMode == SyncModes.Hybrid))
            .Select(g => new { g.Id, g.ScheduleIntervalSeconds }).ToListAsync(ct);

        foreach (var g in groups)
        {
            var busy = await db.SyncRuns.AnyAsync(r => r.SyncGroupId == g.Id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.Cancelling), ct);
            if (busy) continue;
            // tidak ada tabel yang siap (initial sync belum selesai) -> jangan menumpuk run kosong di riwayat
            if (!await db.SyncTableRules.AnyAsync(r => r.SyncGroupId == g.Id && r.IsEnabled && r.InitialSyncStatus.StartsWith("COMPLETED"), ct)) continue;
            var last = await db.SyncRuns.Where(r => r.SyncGroupId == g.Id && r.RunType != RunTypes.Initial)
                .OrderByDescending(r => r.QueuedAt).Select(r => (DateTimeOffset?)r.QueuedAt).FirstOrDefaultAsync(ct);
            var interval = TimeSpan.FromSeconds(Math.Max(60, g.ScheduleIntervalSeconds)); // minimum 1 menit
            if (last != null && DateTimeOffset.UtcNow - last < interval) continue;

            db.SyncRuns.Add(new SyncRun { SyncGroupId = g.Id, RunType = RunTypes.Scheduled });
            try { await db.SaveChangesAsync(ct); _log.LogInformation("Run terjadwal untuk group {Group} dimasukkan antrean", g.Id); }
            catch (DbUpdateException ex) { _log.LogWarning(ex, "Gagal enqueue group {Group}", g.Id); db.ChangeTracker.Clear(); }
        }
    }
}

public static class SchedulerRegistration
{
    public static IServiceCollection AddDbSyncScheduler(this IServiceCollection s)
    {
        s.AddQuartz(q =>
        {
            var key = new JobKey("schedule-tick");
            q.AddJob<ScheduleTickJob>(o => o.WithIdentity(key));
            q.AddTrigger(t => t.ForJob(key).WithIdentity("schedule-tick-trigger")
                .StartNow()
                .WithSimpleSchedule(x => x.WithInterval(TimeSpan.FromSeconds(10)).RepeatForever()));
        });
        s.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        return s;
    }
}
