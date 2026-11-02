using DBSync.Connectors.Abstractions;
using DBSync.Connectors.MySql;
using DBSync.Connectors.PostgreSql;
using DBSync.Connectors.SqlServer;
using DBSync.Infrastructure;
using DBSync.Scheduler;
using DBSync.SyncEngine;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbSyncInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IDatabaseConnector, MySqlDatabaseConnector>();
builder.Services.AddSingleton<IDatabaseConnector, PostgreSqlDatabaseConnector>();
builder.Services.AddSingleton<IDatabaseConnector, SqlServerDatabaseConnector>();
builder.Services.AddDbSyncEngine().AddDbSyncWorkers().AddDbSyncScheduler();

var host = builder.Build();

// pastikan skema database konfigurasi siap (idempotent; API melakukan hal yang sama)
for (int attempt = 1; ; attempt++)
{
    try
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().RunAsync();
        break;
    }
    catch (Exception ex) when (attempt < 15)
    {
        host.Services.GetRequiredService<ILogger<Program>>().LogWarning("Database konfigurasi belum siap ({Msg}); coba lagi {A}/15", ex.Message, attempt);
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}

await host.RunAsync();
