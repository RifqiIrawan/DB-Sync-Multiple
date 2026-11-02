using DBSync.API.Controllers;
using DBSync.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DBSync.API.Hubs;

[Authorize]
public sealed class MonitorHub : Hub { }

/// <summary>
/// Mendorong snapshot dashboard ke browser setiap 3 detik. Sumber kebenaran tetap database/REST API;
/// bila SignalR terputus, UI jatuh kembali ke polling.
/// </summary>
public sealed class MonitorBroadcaster : BackgroundService
{
    private readonly IHubContext<MonitorHub> _hub;
    private readonly IServiceScopeFactory _sf;
    private readonly ILogger<MonitorBroadcaster> _log;
    public MonitorBroadcaster(IHubContext<MonitorHub> hub, IServiceScopeFactory sf, ILogger<MonitorBroadcaster> log) { _hub = hub; _sf = sf; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sf.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DbSyncContext>();
                await _hub.Clients.All.SendAsync("dashboard", await MonitoringController.BuildDashboardAsync(db, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "Broadcast gagal"); }
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }
}
