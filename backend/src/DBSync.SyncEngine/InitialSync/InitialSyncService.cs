using System.Text.Json;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Incremental;
using DBSync.SyncEngine.Pipeline;
using DBSync.SyncEngine.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DBSync.SyncEngine.InitialSync;

public sealed record InitialSyncOptions(char SourceRole = 'A', bool Restart = false);

/// <summary>
/// Initial sync: salin baseline dari sumber ke target (UPSERT, batch, keyset pagination) dengan checkpoint
/// sehingga dapat dilanjutkan setelah worker restart. Setelah baris tertulis, baseline hash disimpan agar
/// incremental sync/CDC mengenali baris tersebut sebagai "sudah sinkron".
/// </summary>
public sealed class InitialSyncService
{
    private readonly DbSyncContext _db;
    private readonly ChangeApplier _applier;
    private readonly ILogger<InitialSyncService> _log;
    public InitialSyncService(DbSyncContext db, ChangeApplier applier, ILogger<InitialSyncService> log)
    { _db = db; _applier = applier; _log = log; }

    public async Task<TableResult> RunAsync(RuleContext ctx, SyncRunItem item, InitialSyncOptions opt, Func<CancellationToken, Task> progress, CancellationToken ct)
    {
        var res = new TableResult();
        var rule = await _db.SyncTableRules.FirstAsync(r => r.Id == ctx.Rule.Id, ct);
        char src = opt.SourceRole, tgt = RuleContext.Other(src);
        if (src == 'A' && !ctx.Rule.SyncAToB && !ctx.Rule.SyncBToA) throw new ConnectorException(ErrorClass.Configuration, "DIRECTION", "Arah tabel tidak mengizinkan initial sync.");

        var dir = src == 'A' ? "A_TO_B" : "B_TO_A";
        var cp = await _db.SyncCheckpoints.FirstOrDefaultAsync(x => x.TableRuleId == rule.Id && x.Direction == dir, ct);
        if (cp == null)
        { cp = new SyncCheckpoint { SyncGroupId = ctx.Group.Id, TableRuleId = rule.Id, Direction = dir }; _db.SyncCheckpoints.Add(cp); }

        List<object?>? after = null;
        if (!opt.Restart && rule.InitialSyncStatus == "RUNNING" && cp.DurablePosition != null &&
            cp.DurablePosition.RootElement.TryGetProperty("after", out var afterEl) && afterEl.ValueKind == JsonValueKind.Array)
        {
            var pk = ctx.Pk;
            after = afterEl.EnumerateArray().Select((e, i) => Values.Coerce(e, ctx.Col(pk[i], src)!)).ToList();
            item.RecordsRead = cp.CapturePosition?.RootElement.TryGetProperty("read", out var r) == true ? r.GetInt64() : 0;
            res.Read = item.RecordsRead; res.Inserted = item.RecordsWritten;
            _log.LogInformation("Initial sync {Table} dilanjutkan dari checkpoint.", ctx.Label);
        }
        else
        {
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM sync_row_states WHERE table_rule_id = {0}", new object[] { rule.Id }, ct);
        }

        rule.InitialSyncStatus = "RUNNING";
        item.TotalRows = await ctx.Conn(src).CountRowsAsync(ctx.Profile(src), src == 'A' ? rule.EndpointASchema : rule.EndpointBSchema, src == 'A' ? rule.EndpointATable : rule.EndpointBTable, ct);
        await _db.SaveChangesAsync(ct);

        int batchNo = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<Dictionary<string, object?>>();
            await foreach (var raw in ctx.Conn(src).ReadRowsAsync(ctx.Profile(src), ctx.BuildRead(src, after, null, ctx.Group.BatchSize), ct))
                rows.Add(ctx.ToLogical(raw, src));
            if (rows.Count == 0) break;
            batchNo++;

            var changes = rows.Select(l => new PendingChange(src, tgt, Ops.Insert, ctx.PkKey(l), l, ctx.RowHash(l), ctx.BuildUpsert(l, tgt))).ToList();
            var outcomes = await _applier.ApplyAsync(ctx, tgt, changes, ctx.Group.BatchSize, ctx.Group.MaxRetries, ct);

            var ok = outcomes.Where(o => o.Error == null).Select(o => (o.Change.PkKey, o.Change.NewHash!)).ToList();
            await RowStateStore.UpsertAsync(_db, rule.Id, ok, ct);
            foreach (var bad in outcomes.Where(o => o.Error != null))
            {
                var ev = EventRecorder.Create(ctx, item.SyncRunId, bad.Change, ctx.RowJson(bad.Change.Logical));
                EventRecorder.UpdateFromOutcome(ev, bad.Error, 0); // initial sync: gagal -> dead letter agar terlihat
                _db.SyncEvents.Add(ev);
            }

            res.Read += rows.Count; res.Inserted += ok.Count; res.Failed += outcomes.Count - ok.Count;
            item.RecordsRead = res.Read; item.RecordsWritten = res.Inserted; item.RecordsFailed = res.Failed; item.BatchNumber = batchNo;

            var last = rows[^1];
            after = ctx.PkValues(last, src);
            cp.CapturePosition = JsonSerializer.SerializeToDocument(new { read = res.Read });
            cp.DurablePosition = JsonSerializer.SerializeToDocument(new { after = after.Select(Values.ToJsonSafe).ToArray() });
            cp.Status = "RUNNING"; cp.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await progress(ct);
            if (rows.Count < ctx.Group.BatchSize) break;
        }

        // validasi sederhana: jumlah baris target >= jumlah sumber yang ditulis
        var tgtCount = await ctx.Conn(tgt).CountRowsAsync(ctx.Profile(tgt), tgt == 'A' ? rule.EndpointASchema : rule.EndpointBSchema, tgt == 'A' ? rule.EndpointATable : rule.EndpointBTable, ct);
        cp.AppliedPosition = JsonSerializer.SerializeToDocument(new { source_rows = res.Read, target_rows = tgtCount, failed = res.Failed });
        cp.LastSuccessAt = DateTimeOffset.UtcNow; cp.Status = res.Failed == 0 ? "OK" : "PARTIAL";
        rule.InitialSyncStatus = res.Failed == 0 ? "COMPLETED" : "COMPLETED_WITH_ERRORS";
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return res;
    }
}
