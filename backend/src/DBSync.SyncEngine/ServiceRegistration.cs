using DBSync.SyncEngine.Cdc;
using DBSync.SyncEngine.Conflict;
using DBSync.SyncEngine.Incremental;
using DBSync.SyncEngine.InitialSync;
using DBSync.SyncEngine.Queue;
using Microsoft.Extensions.DependencyInjection;

namespace DBSync.SyncEngine;

public static class ServiceRegistration
{
    /// <summary>Layanan engine yang dipakai API dan Worker (tanpa hosted service).</summary>
    public static IServiceCollection AddDbSyncEngine(this IServiceCollection s)
    {
        s.AddScoped<IRuleContextFactory, RuleContextFactory>();
        s.AddScoped<ChangeApplier>();
        s.AddScoped<IncrementalSyncService>();
        s.AddScoped<InitialSyncService>();
        s.AddScoped<ConflictService>();
        s.AddScoped<CdcApplier>();
        s.AddHttpClient<DebeziumConnectManager>();
        s.AddSingleton<RunExecutor>();
        return s;
    }

    /// <summary>Hosted service: eksekusi antrean run, consumer CDC, retry CDC. Dipasang di Worker (atau API bila embedded).</summary>
    public static IServiceCollection AddDbSyncWorkers(this IServiceCollection s)
    {
        s.AddHostedService<RunQueueWorker>();
        s.AddHostedService<CdcConsumerWorker>();
        s.AddHostedService<CdcRetryWorker>();
        s.AddHostedService<RetentionWorker>();
        return s;
    }
}
