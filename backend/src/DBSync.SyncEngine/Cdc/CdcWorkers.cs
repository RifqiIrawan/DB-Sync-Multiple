using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using DBSync.Application;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Pipeline;
using DBSync.SyncEngine.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DBSync.SyncEngine.Cdc;

public enum CdcOutcome { Applied, SkippedEcho, SkippedDuplicate, SkippedPolicy, Conflict, Retry, DeadLetter }

/// <summary>Menerapkan satu event CDC ke endpoint seberang dengan idempotency, loop prevention, dan deteksi konflik.</summary>
public sealed class CdcApplier
{
    private readonly DbSyncContext _db;
    private readonly ChangeApplier _applier;
    public CdcApplier(DbSyncContext db, ChangeApplier applier) { _db = db; _applier = applier; }

    public async Task<CdcOutcome> ApplyAsync(RuleContext ctx, char src, CdcRecord rec, Guid eventId, object position, CancellationToken ct)
    {
        char tgt = RuleContext.Other(src);
        string tgtRole = tgt.ToString();
        if ((src == 'A' && !ctx.CanAToB) || (src == 'B' && !ctx.CanBToA)) return CdcOutcome.SkippedPolicy;

        bool isDelete = rec.Op == "d";
        var row = isDelete ? rec.Before : rec.After;
        if (row == null) return CdcOutcome.SkippedPolicy;
        var op = isDelete ? Ops.Delete : rec.Op == "u" ? Ops.Update : Ops.Insert;
        if (op == Ops.Insert && !ctx.Rule.SyncInsert || op == Ops.Update && !ctx.Rule.SyncUpdate || op == Ops.Delete && !ctx.Rule.SyncDelete) return CdcOutcome.SkippedPolicy;

        if (await _db.ProcessedEvents.AnyAsync(p => p.EventId == eventId && p.DestinationRole == tgtRole, ct)) return CdcOutcome.SkippedDuplicate;

        var logical = ctx.ToLogical(row, src);
        var pk = ctx.PkKey(logical);
        var hash = isDelete ? null : ctx.RowHash(logical);
        var baseline = await RowStateStore.GetAsync(_db, ctx.Rule.Id, pk, ct);

        // loop prevention / echo suppression: perubahan ini hasil tulisan DBSync sendiri atau no-op
        if (!isDelete && baseline == hash) { await MarkProcessedAsync(eventId, tgtRole, hash, ct); return CdcOutcome.SkippedEcho; }
        if (isDelete && baseline == RowStateStore.Deleted) { await MarkProcessedAsync(eventId, tgtRole, null, ct); return CdcOutcome.SkippedEcho; }

        // konflik pada mode dua arah
        if (ctx.CanAToB && ctx.CanBToA && baseline != null && baseline != RowStateStore.Deleted)
        {
            Dictionary<string, object?>? tgtRow = null;
            var keyVals = ctx.Pk.Select(c => (object?)Values.Coerce(logical[c.AName], ctx.Col(c, tgt)!)).ToList();
            await foreach (var r in ctx.Conn(tgt).ReadRowsAsync(ctx.Profile(tgt), ctx.BuildRead(tgt, keyEquals: keyVals), ct)) { tgtRow = ctx.ToLogical(r, tgt); break; }
            var th = tgtRow == null ? null : ctx.RowHash(tgtRow);
            if (th != baseline && th != hash)
            {
                var strat = ctx.Group.ConflictStrategy;
                if (strat == ConflictStrategies.APriority) { if (src != 'A') return CdcOutcome.SkippedPolicy; }
                else if (strat == ConflictStrategies.BPriority) { if (src != 'B') return CdcOutcome.SkippedPolicy; }
                else
                {
                    await RecordConflictAsync(ctx, pk, logical, tgtRow, src, isDelete, ct);
                    return CdcOutcome.Conflict;
                }
            }
        }

        var change = new PendingChange(src, tgt, op, pk, logical, hash, isDelete ? ctx.BuildDelete(logical, tgt) : ctx.BuildUpsert(logical, tgt));
        var ev = new SyncEvent
        {
            EventId = eventId, SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, SourceRole = src.ToString(),
            Direction = src == 'A' ? "A_TO_B" : "B_TO_A", Operation = op, PkKey = pk, PrimaryKey = ctx.PkJson(logical), Payload = ctx.RowJson(logical),
            SourcePosition = JsonSerializer.SerializeToDocument(new { position, hash, ts = rec.TsMs, lsn = rec.Lsn }),
            Status = EventStatus.Processing,
        };
        var outcome = (await _applier.ApplyAsync(ctx, tgt, new[] { change }, 1, ctx.Group.MaxRetries, ct)).Single();
        EventRecorder.UpdateFromOutcome(ev, outcome.Error, ctx.Group.MaxRetries);
        _db.SyncEvents.Add(ev);
        if (outcome.Error == null)
        {
            await RowStateStore.UpsertAsync(_db, ctx.Rule.Id, new[] { (pk, hash ?? RowStateStore.Deleted) }, ct);
            _db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, DestinationRole = tgtRole, ResultHash = hash });
        }
        await _db.SaveChangesAsync(ct);
        return outcome.Error == null ? CdcOutcome.Applied : ev.Status == EventStatus.DeadLetter ? CdcOutcome.DeadLetter : CdcOutcome.Retry;
    }


    /// <summary>Group di-pause: simpan event secara durable (PENDING) agar diproses setelah resume; tidak ada yang dibuang.</summary>
    public async Task<CdcOutcome> EnqueueAsync(RuleContext ctx, char src, CdcRecord rec, Guid eventId, object position, CancellationToken ct)
    {
        bool isDelete = rec.Op == "d";
        var row = isDelete ? rec.Before : rec.After;
        if (row == null) return CdcOutcome.SkippedPolicy;
        if (await _db.SyncEvents.AnyAsync(e => e.EventId == eventId, ct)) return CdcOutcome.SkippedDuplicate;
        var logical = ctx.ToLogical(row, src);
        _db.SyncEvents.Add(new SyncEvent
        {
            EventId = eventId, SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, SourceRole = src.ToString(),
            Direction = src == 'A' ? "A_TO_B" : "B_TO_A", Operation = isDelete ? Ops.Delete : rec.Op == "u" ? Ops.Update : Ops.Insert,
            PkKey = ctx.PkKey(logical), PrimaryKey = ctx.PkJson(logical), Payload = ctx.RowJson(logical),
            SourcePosition = JsonSerializer.SerializeToDocument(new { position, hash = isDelete ? null : ctx.RowHash(logical), ts = rec.TsMs, lsn = rec.Lsn }),
            Status = EventStatus.Pending,
        });
        await _db.SaveChangesAsync(ct);
        return CdcOutcome.SkippedPolicy;
    }
    private async Task MarkProcessedAsync(Guid eventId, string role, string? hash, CancellationToken ct)
    {
        _db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, DestinationRole = role, ResultHash = hash });
        await _db.SaveChangesAsync(ct);
    }

    private async Task RecordConflictAsync(RuleContext ctx, string pk, Dictionary<string, object?> srcLogical, Dictionary<string, object?>? tgtLogical, char src, bool isDelete, CancellationToken ct)
    {
        var existing = await _db.SyncConflicts.FirstOrDefaultAsync(c => c.TableRuleId == ctx.Rule.Id && c.PkKey == pk && c.Status == "PENDING", ct);
        var a = src == 'A' ? (isDelete ? null : srcLogical) : tgtLogical;
        var b = src == 'B' ? (isDelete ? null : srcLogical) : tgtLogical;
        var ja = a == null ? null : ctx.RowJson(a); var jb = b == null ? null : ctx.RowJson(b);
        if (existing != null) { existing.EndpointAValue = ja; existing.EndpointBValue = jb; }
        else
        {
            _db.SyncConflicts.Add(new SyncConflict
            {
                SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, PkKey = pk, PrimaryKey = ctx.PkJson(srcLogical),
                EndpointAValue = ja, EndpointBValue = jb, ConflictType = isDelete || tgtLogical == null ? "DELETE_UPDATE" : "BOTH_UPDATED",
            });
            _db.SyncEvents.Add(new SyncEvent
            {
                SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, PkKey = pk, PrimaryKey = ctx.PkJson(srcLogical), Direction = "BIDIRECTIONAL",
                Operation = Ops.Update, Status = EventStatus.Conflict, ErrorCode = "CONFLICT", SourcePosition = null,
            });
        }
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>Consumer Kafka: membaca event Debezium, menormalisasi, menerapkan ke target, lalu commit offset (at-least-once + idempotent).</summary>
public sealed class CdcConsumerWorker : BackgroundService
{
    private sealed record Route(Guid RuleId, char Side, bool Paused);

    private readonly IServiceScopeFactory _sf;
    private readonly CdcOptions _opt;
    private readonly ILogger<CdcConsumerWorker> _log;
    private Dictionary<(string Conn, string Schema, string Table), List<Route>> _routes = new();
    private readonly Dictionary<Guid, (RuleContext Ctx, DateTime Expires)> _ctxCache = new();
    private DateTime _routesAt = DateTime.MinValue;
    private readonly string _workerId = "cdc-" + Guid.NewGuid().ToString("N")[..8];

    public CdcConsumerWorker(IServiceScopeFactory sf, IOptions<CdcOptions> opt, ILogger<CdcConsumerWorker> log) { _sf = sf; _opt = opt.Value; _log = log; }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) { _log.LogInformation("CDC consumer dinonaktifkan (Cdc:Enabled=false)."); return Task.CompletedTask; }
        return Task.Run(() => LoopAsync(ct), ct);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var cfg = new ConsumerConfig
        {
            BootstrapServers = _opt.KafkaBootstrap, GroupId = _opt.ConsumerGroupId, EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest, TopicMetadataRefreshIntervalMs = 10000, AllowAutoCreateTopics = false,
            SessionTimeoutMs = 30000, EnablePartitionEof = false,
        };
        using var consumer = new ConsumerBuilder<string?, string?>(cfg)
            .SetErrorHandler((_, e) => _log.LogWarning("Kafka: {Reason}", e.Reason)).Build();
        consumer.Subscribe("^dbsync_.*");
        _log.LogInformation("CDC consumer berjalan; bootstrap={B} group={G}", _opt.KafkaBootstrap, _opt.ConsumerGroupId);

        while (!ct.IsCancellationRequested)
        {
            ConsumeResult<string?, string?>? msg = null;
            try
            {
                msg = consumer.Consume(TimeSpan.FromSeconds(1));
                if (msg?.Message?.Value == null) { if (msg != null) consumer.Commit(msg); await HeartbeatAsync(ct); continue; }
                await RefreshRoutesAsync(ct);
                await ProcessAsync(msg, ct);
                consumer.Commit(msg);          // commit hanya setelah event tersimpan/diterapkan
                await HeartbeatAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { _log.LogWarning("Consume: {E}", ex.Error.Reason); await Task.Delay(2000, ct).ContinueWith(_ => { }); }
            catch (Exception ex)
            {
                // kegagalan infrastruktur (mis. config DB mati): jangan commit, ulang dari offset yang sama
                _log.LogError(ex, "Gagal memproses pesan CDC; akan diulang.");
                if (msg != null) { try { consumer.Seek(msg.TopicPartitionOffset); } catch { } }
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }
        try { consumer.Close(); } catch { }
    }

    private DateTime _lastHb = DateTime.MinValue;
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastHb < TimeSpan.FromSeconds(10)) return;
        _lastHb = DateTime.UtcNow;
        using var s = _sf.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
        var hb = await db.WorkerHeartbeats.FindAsync(new object[] { _workerId }, ct);
        if (hb == null) db.WorkerHeartbeats.Add(new WorkerHeartbeat { WorkerId = _workerId, WorkerType = "CDC", Hostname = Environment.MachineName });
        else { hb.LastHeartbeatAt = DateTimeOffset.UtcNow; hb.Status = "RUNNING"; }
        await db.SaveChangesAsync(ct);
    }

    private async Task RefreshRoutesAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _routesAt < TimeSpan.FromSeconds(15)) return;
        using var s = _sf.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
        var map = new Dictionary<(string, string, string), List<Route>>();
        var groups = await db.SyncGroups.AsNoTracking().Include(g => g.Endpoints).Include(g => g.TableRules)
            .Where(g => (g.Status == GroupStatus.Active || g.Status == GroupStatus.Paused) && (g.SyncMode == SyncModes.Cdc || g.SyncMode == SyncModes.Hybrid)).ToListAsync(ct);
        foreach (var g in groups)
            foreach (var e in g.Endpoints)
                foreach (var r in g.TableRules.Where(r => r.IsEnabled && r.InitialSyncStatus.StartsWith("COMPLETED")))
                {
                    var side = e.EndpointRole[0];
                    var key = (e.ConnectionId.ToString("N"), (side == 'A' ? r.EndpointASchema : r.EndpointBSchema).ToLowerInvariant(), (side == 'A' ? r.EndpointATable : r.EndpointBTable).ToLowerInvariant());
                    if (!map.TryGetValue(key, out var l)) map[key] = l = new();
                    l.Add(new Route(r.Id, side, g.Status == GroupStatus.Paused));
                }
        _routes = map; _routesAt = DateTime.UtcNow;
    }

    private async Task ProcessAsync(ConsumeResult<string?, string?> msg, CancellationToken ct)
    {
        var topic = msg.Topic; // dbsync_<connN>.<schema|db>.<table>
        var connN = topic.Split('.')[0].Replace("dbsync_", "");
        var rec = DebeziumEventNormalizer.Parse(msg.Message.Value!);
        if (rec == null) return;
        if (!_routes.TryGetValue((connN, rec.EffectiveSchema.ToLowerInvariant(), rec.Table.ToLowerInvariant()), out var routes)) return; // tabel tidak aktif/ initial sync belum selesai

        foreach (var route in routes)
        {
            // event id deterministik -> replay/redelivery tidak menggandakan efek
            var id = DeterministicGuid($"{topic}|{msg.Partition.Value}|{msg.Offset.Value}|{route.RuleId}");
            using var scope = _sf.CreateScope();
            var ctx = await GetContextAsync(scope, route.RuleId, ct);
            var applier = scope.ServiceProvider.GetRequiredService<CdcApplier>();
            var pos = new { topic, partition = msg.Partition.Value, offset = msg.Offset.Value };
            var outcome = route.Paused ? await applier.EnqueueAsync(ctx, route.Side, rec, id, pos, ct) : await applier.ApplyAsync(ctx, route.Side, rec, id, pos, ct);
            _log.LogDebug("CDC {Topic}@{Off} -> {Outcome}", topic, msg.Offset.Value, outcome);
        }
    }

    private async Task<RuleContext> GetContextAsync(IServiceScope scope, Guid ruleId, CancellationToken ct)
    {
        if (_ctxCache.TryGetValue(ruleId, out var c) && c.Expires > DateTime.UtcNow) return c.Ctx;
        var ctx = await scope.ServiceProvider.GetRequiredService<IRuleContextFactory>().CreateAsync(ruleId, ct);
        _ctxCache[ruleId] = (ctx, DateTime.UtcNow.AddMinutes(2));
        return ctx;
    }

    public static Guid DeterministicGuid(string s) => new(MD5.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>Mengulang event CDC berstatus RETRY/PENDING dari payload tersimpan (termasuk setelah operator me-retry dead letter).</summary>
public sealed class CdcRetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _sf;
    private readonly ILogger<CdcRetryWorker> _log;
    public CdcRetryWorker(IServiceScopeFactory sf, ILogger<CdcRetryWorker> log) { _sf = sf; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(5000, ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "CdcRetryWorker error"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbSyncContext>();
        var now = DateTimeOffset.UtcNow;
        var activeGroups = db.SyncGroups.Where(g => g.Status == GroupStatus.Active).Select(g => g.Id);
        var events = await db.SyncEvents.Where(e => activeGroups.Contains(e.SyncGroupId) && e.SourcePosition != null && (e.Status == EventStatus.Retry || e.Status == EventStatus.Pending) && (e.NextRetryAt == null || e.NextRetryAt <= now))
            .OrderBy(e => e.CreatedAt).Take(50).ToListAsync(ct);
        foreach (var ev in events)
        {
            try
            {
                // superseded oleh event lebih baru yang sudah sukses?
                if (await db.SyncEvents.AnyAsync(x => x.TableRuleId == ev.TableRuleId && x.PkKey == ev.PkKey && x.Status == EventStatus.Success && x.CreatedAt > ev.CreatedAt, ct))
                { ev.Status = EventStatus.Skipped; ev.ErrorMessage = "Digantikan event yang lebih baru."; continue; }

                var ctx = await scope.ServiceProvider.GetRequiredService<IRuleContextFactory>().CreateAsync(ev.TableRuleId, ct);
                char src = ev.SourceRole[0], tgt = RuleContext.Other(src);
                var logical = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in ev.Payload!.RootElement.EnumerateObject())
                    logical[p.Name] = p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.StartsWith("base64:") ? Convert.FromBase64String(p.Value.GetString()![7..]) : p.Value.Clone();
                string? hash = ev.SourcePosition!.RootElement.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                var op = ev.Operation == Ops.Delete ? ctx.BuildDelete(logical, tgt) : ctx.BuildUpsert(logical, tgt);
                var cur = await RowStateStore.GetAsync(db, ctx.Rule.Id, ev.PkKey, ct);
                if ((ev.Operation != Ops.Delete && cur != null && cur == hash) || (ev.Operation == Ops.Delete && cur == RowStateStore.Deleted))
                { ev.Status = EventStatus.Skipped; ev.ErrorMessage = "Sudah sinkron (echo/no-op)."; continue; }
                var change = new PendingChange(src, tgt, ev.Operation, ev.PkKey, logical, hash, op);
                var outcome = (await scope.ServiceProvider.GetRequiredService<ChangeApplier>().ApplyAsync(ctx, tgt, new[] { change }, 1, ctx.Group.MaxRetries, ct)).Single();
                EventRecorder.UpdateFromOutcome(ev, outcome.Error, ctx.Group.MaxRetries);
                if (outcome.Error == null)
                {
                    await RowStateStore.UpsertAsync(db, ctx.Rule.Id, new[] { (ev.PkKey, hash ?? RowStateStore.Deleted) }, ct);
                    if (!await db.ProcessedEvents.AnyAsync(p => p.EventId == ev.EventId && p.DestinationRole == tgt.ToString(), ct))
                        db.ProcessedEvents.Add(new ProcessedEvent { EventId = ev.EventId, DestinationRole = tgt.ToString(), ResultHash = hash });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ev.RetryCount++; ev.ErrorMessage = ex.Message; ev.Status = ev.RetryCount > 5 ? EventStatus.DeadLetter : EventStatus.Retry;
                ev.NextRetryAt = DateTimeOffset.UtcNow + Backoff.Delay(ev.RetryCount, 30);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
