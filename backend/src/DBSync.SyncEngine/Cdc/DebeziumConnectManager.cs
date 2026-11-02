using System.Net.Http.Json;
using System.Text.Json;
using DBSync.Application;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DBSync.SyncEngine.Cdc;

public sealed record ConnectorStatusInfo(string Name, string State, string? Trace, List<string> Tasks);

/// <summary>Mengelola connector Debezium via Kafka Connect REST. Satu connector per koneksi database.</summary>
public sealed class DebeziumConnectManager
{
    private readonly HttpClient _http;
    private readonly CdcOptions _opt;
    private readonly DbSyncContext _db;
    private readonly IConnectionProfileFactory _profiles;
    private readonly IConnectorRegistry _registry;

    public DebeziumConnectManager(HttpClient http, IOptions<CdcOptions> opt, DbSyncContext db, IConnectionProfileFactory profiles, IConnectorRegistry registry)
    { _http = http; _opt = opt.Value; _db = db; _profiles = profiles; _registry = registry; _http.BaseAddress = new Uri(_opt.KafkaConnectUrl.TrimEnd('/') + "/"); _http.Timeout = TimeSpan.FromSeconds(20); }

    public static string TopicPrefix(Guid connectionId) => "dbsync_" + connectionId.ToString("N");
    public static string ConnectorName(Guid connectionId) => "dbsync-" + connectionId.ToString("N");

    private async Task<List<(string Schema, string Table)>> TablesForAsync(Guid connectionId, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        var eps = await _db.SyncEndpoints.AsNoTracking()
            .Where(e => e.ConnectionId == connectionId && e.SyncGroup!.Status != GroupStatus.Stopped && (e.SyncGroup.SyncMode == SyncModes.Cdc || e.SyncGroup.SyncMode == SyncModes.Hybrid))
            .Select(e => new { e.SyncGroupId, e.EndpointRole }).ToListAsync(ct);
        foreach (var ep in eps)
        {
            var rules = await _db.SyncTableRules.AsNoTracking().Where(r => r.SyncGroupId == ep.SyncGroupId && r.IsEnabled).ToListAsync(ct);
            foreach (var r in rules)
                list.Add(ep.EndpointRole == "A" ? (r.EndpointASchema, r.EndpointATable) : (r.EndpointBSchema, r.EndpointBTable));
        }
        return list.Distinct().ToList();
    }

    public async Task<IReadOnlyList<CdcCheck>> CheckPrerequisitesAsync(Guid connectionId, CancellationToken ct)
    {
        var profile = await _profiles.CreateAsync(connectionId, 30, ct);
        var tables = await TablesForAsync(connectionId, ct);
        return await _registry.Get(profile.DbType).CheckCdcPrerequisitesAsync(profile, tables, ct);
    }

    /// <summary>Buat/perbarui connector (idempotent) berdasarkan seluruh tabel CDC aktif pada koneksi ini.</summary>
    public async Task<string> EnsureConnectorAsync(Guid connectionId, CancellationToken ct)
    {
        var profile = await _profiles.CreateAsync(connectionId, 30, ct);
        var tables = await TablesForAsync(connectionId, ct);
        if (tables.Count == 0) throw new InvalidOperationException("Tidak ada tabel CDC pada koneksi ini.");
        var name = ConnectorName(connectionId);
        var cfg = _registry.Get(profile.DbType).BuildDebeziumConfig(profile, TopicPrefix(connectionId), tables, _opt.KafkaBootstrapForConnect, name);
        cfg["tasks.max"] = "1";
        cfg["key.converter"] = "org.apache.kafka.connect.json.JsonConverter";
        cfg["value.converter"] = "org.apache.kafka.connect.json.JsonConverter";
        cfg["key.converter.schemas.enable"] = "false";
        cfg["value.converter.schemas.enable"] = "true";
        cfg["decimal.handling.mode"] = "string";
        cfg["tombstones.on.delete"] = "false";
        cfg["snapshot.mode"] = "no_data"; // data awal dipindahkan oleh Initial Sync DBSync
        var res = await _http.PutAsJsonAsync($"connectors/{name}/config", cfg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Kafka Connect menolak konfigurasi ({(int)res.StatusCode}): {body}");
        return name;
    }

    public async Task<ConnectorStatusInfo?> GetStatusAsync(Guid connectionId, CancellationToken ct)
    {
        var name = ConnectorName(connectionId);
        var res = await _http.GetAsync($"connectors/{name}/status", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var c = doc.RootElement.GetProperty("connector");
        var tasks = doc.RootElement.TryGetProperty("tasks", out var t) ? t.EnumerateArray().Select(x => x.GetProperty("state").GetString() ?? "").ToList() : new();
        string? trace = doc.RootElement.TryGetProperty("tasks", out var tt) ? tt.EnumerateArray().Select(x => x.TryGetProperty("trace", out var tr) ? tr.GetString() : null).FirstOrDefault(x => x != null) : null;
        return new ConnectorStatusInfo(name, c.GetProperty("state").GetString() ?? "UNKNOWN", trace, tasks);
    }

    public async Task DeleteAsync(Guid connectionId, CancellationToken ct)
    {
        var res = await _http.DeleteAsync($"connectors/{ConnectorName(connectionId)}", ct);
        if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }

    public async Task RestartAsync(Guid connectionId, CancellationToken ct)
    {
        var res = await _http.PostAsync($"connectors/{ConnectorName(connectionId)}/restart?includeTasks=true&onlyFailed=false", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
        res.EnsureSuccessStatusCode();
    }
}
