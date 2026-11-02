using System.Text.Json;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Pipeline;
using DBSync.SyncEngine.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DBSync.SyncEngine.Incremental;

public sealed class TableResult
{
    public long Read, Inserted, Updated, Deleted, Skipped, Failed, Conflicts;
}

/// <summary>
/// Sinkronisasi terjadwal berbasis perbandingan tiga arah:
/// baseline (hash terakhir yang disepakati) vs hash sisi A vs hash sisi B.
/// - hanya A berubah  -> A ke B;  hanya B berubah -> B ke A;
/// - keduanya berubah dan berbeda -> konflik.
/// Baseline juga mencegah "echo" (perubahan yang baru kita tulis tidak dikirim balik) sehingga loop A-B-A tidak terjadi.
/// DELETE terdeteksi karena baris ada di baseline tetapi hilang dari salah satu sisi.
/// </summary>
public sealed class IncrementalSyncService
{
    private sealed record Snap(string Hash, Dictionary<string, object?>? Row);

    private readonly DbSyncContext _db;
    private readonly ChangeApplier _applier;
    private readonly ILogger<IncrementalSyncService> _log;

    public IncrementalSyncService(DbSyncContext db, ChangeApplier applier, ILogger<IncrementalSyncService> log)
    { _db = db; _applier = applier; _log = log; }

    public async Task<TableResult> SyncAsync(RuleContext ctx, Guid runId, Func<CancellationToken, Task>? progress, CancellationToken ct)
    {
        var res = new TableResult();
        var rule = ctx.Rule; var group = ctx.Group;
        var baseline = await RowStateStore.LoadAsync(_db, rule.Id, ct);

        var snapA = await ScanAsync(ctx, 'A', baseline, res, ct);
        var snapB = await ScanAsync(ctx, 'B', baseline, res, ct);
        if (progress != null) await progress(ct);

        // event bermasalah sebelumnya (retry / dead letter)
        var open = await _db.SyncEvents.Where(e => e.TableRuleId == rule.Id && (e.Status == EventStatus.Retry || e.Status == EventStatus.DeadLetter))
            .ToListAsync(ct);
        var openByPk = open.GroupBy(e => e.PkKey).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CreatedAt).First());
        var pendingConflicts = await _db.SyncConflicts.Where(c => c.TableRuleId == rule.Id && c.Status == "PENDING").ToListAsync(ct);
        var conflictByPk = pendingConflicts.ToDictionary(c => c.PkKey);

        var toB = new List<PendingChange>(); var toA = new List<PendingChange>();
        var baseUpserts = new Dictionary<string, string>(); var baseDeletes = new HashSet<string>();
        var now = DateTimeOffset.UtcNow;

        foreach (var pk in snapA.Keys.Union(snapB.Keys).Union(baseline.Keys))
        {
            snapA.TryGetValue(pk, out var a); snapB.TryGetValue(pk, out var b);
            baseline.TryGetValue(pk, out var baseRaw);
            var baseH = baseRaw == RowStateStore.Deleted ? null : baseRaw;
            var ha = a?.Hash; var hb = b?.Hash;

            if (openByPk.TryGetValue(pk, out var oev) && (oev.Status == EventStatus.DeadLetter || (oev.NextRetryAt != null && oev.NextRetryAt > now)))
            { res.Skipped++; continue; }

            if (ha == null && hb == null)
            {
                if (baseRaw != null && baseRaw != RowStateStore.Deleted) baseDeletes.Add(pk);
                continue;
            }

            bool changedA = ha != baseH && ctx.CanAToB;
            bool changedB = hb != baseH && ctx.CanBToA;

            if (ha == hb) { if (ha != baseH) baseUpserts[pk] = ha!; continue; } // sudah sama; selaraskan baseline
            if (!changedA && !changedB) continue;

            char? winner = null;
            if (changedA && changedB)
            {
                winner = ResolveAutomatically(ctx, a, b);
                if (winner == null)
                {
                    RecordConflict(ctx, pk, a, b, baseH, conflictByPk, res);
                    continue;
                }
            }
            else winner = changedA ? 'A' : 'B';

            var src = winner.Value; var tgt = RuleContext.Other(src);
            var srcSnap = src == 'A' ? a : b; var tgtSnap = src == 'A' ? b : a;

            if (srcSnap == null)
            {
                // penghapusan di sumber
                if (tgtSnap == null) { baseDeletes.Add(pk); continue; }
                if (!rule.SyncDelete) continue;
                var keyRow = tgtSnap.Row ?? await FetchRowAsync(ctx, tgt, tgtSnap, pk, ct);
                if (keyRow == null) continue;
                var op = ctx.BuildDelete(keyRow, tgt);
                Add(tgt == 'B' ? toB : toA, new PendingChange(src, tgt, Ops.Delete, pk, keyRow, null, op));
            }
            else
            {
                bool exists = tgtSnap != null;
                if (exists && !rule.SyncUpdate) continue;
                if (!exists && !rule.SyncInsert) continue;
                var row = srcSnap.Row!;
                var op = ctx.BuildUpsert(row, tgt);
                Add(tgt == 'B' ? toB : toA, new PendingChange(src, tgt, exists ? Ops.Update : Ops.Insert, pk, row, srcSnap.Hash, op));
            }
        }

        await FlushConflictsAsync(ct);
        await ExecuteAsync(ctx, runId, 'B', toB, openByPk, baseUpserts, baseDeletes, res, ct);
        await ExecuteAsync(ctx, runId, 'A', toA, openByPk, baseUpserts, baseDeletes, res, ct);

        await RowStateStore.UpsertAsync(_db, rule.Id, baseUpserts.Select(k => (k.Key, k.Value)).ToList(), ct);
        await RowStateStore.DeleteAsync(_db, rule.Id, baseDeletes, ct);
        await UpdateCheckpointAsync(ctx, res, ct);
        if (progress != null) await progress(ct);
        return res;

        static void Add(List<PendingChange> l, PendingChange c) => l.Add(c);
    }

    private async Task<Dictionary<string, Snap>> ScanAsync(RuleContext ctx, char side, Dictionary<string, string> baseline, TableResult res, CancellationToken ct)
    {
        var d = new Dictionary<string, Snap>();
        await foreach (var raw in ctx.Conn(side).ReadRowsAsync(ctx.Profile(side), ctx.BuildRead(side), ct))
        {
            var logical = ctx.ToLogical(raw, side);
            var pk = ctx.PkKey(logical); var h = ctx.RowHash(logical);
            res.Read++;
            var same = baseline.TryGetValue(pk, out var bh) && bh == h;
            d[pk] = new Snap(h, same ? null : logical);   // baris tak berubah tidak perlu disimpan penuh
        }
        return d;
    }

    private async Task<Dictionary<string, object?>?> FetchRowAsync(RuleContext ctx, char side, Snap s, string pk, CancellationToken ct)
    {
        // baris yang tidak berubah tidak disimpan penuh; ambil ulang berdasarkan PK
        var keys = JsonSerializer.Deserialize<string[]>(pk)!;
        var pkCols = ctx.Pk;
        var vals = pkCols.Select((c, i) => (object?)Values.Coerce(keys[i], ctx.Col(c, side)!)).ToList();
        await foreach (var r in ctx.Conn(side).ReadRowsAsync(ctx.Profile(side), ctx.BuildRead(side, keyEquals: vals), ct))
            return ctx.ToLogical(r, side);
        return null;
    }

    private char? ResolveAutomatically(RuleContext ctx, Snap? a, Snap? b)
    {
        switch (ctx.Group.ConflictStrategy)
        {
            case ConflictStrategies.APriority: return 'A';
            case ConflictStrategies.BPriority: return 'B';
            case ConflictStrategies.LastWriteWins:
                var col = ctx.Rule.UpdatedAtColumn;
                if (string.IsNullOrEmpty(col) || a?.Row == null || b?.Row == null) return null; // metadata waktu tidak tersedia -> manual
                var mc = ctx.Columns.FirstOrDefault(c => c.AName.Equals(col, StringComparison.OrdinalIgnoreCase));
                if (mc == null) return null;
                var va = a.Row[mc.AName]; var vb = b.Row[mc.AName];
                if (va is IComparable ca && vb is not null && va.GetType() == vb.GetType())
                { var cmp = ca.CompareTo(vb); if (cmp > 0) return 'A'; if (cmp < 0) return 'B'; }
                return null;
            default: return null;
        }
    }

    private void RecordConflict(RuleContext ctx, string pk, Snap? a, Snap? b, string? baseH, Dictionary<string, SyncConflict> existing, TableResult res)
    {
        var type = a == null || b == null ? "DELETE_UPDATE" : baseH == null ? "BOTH_INSERTED" : "BOTH_UPDATED";
        JsonDocument? ja = a?.Row == null ? null : ctx.RowJson(a.Row);
        JsonDocument? jb = b?.Row == null ? null : ctx.RowJson(b.Row);
        if (existing.TryGetValue(pk, out var c))
        { c.EndpointAValue = ja; c.EndpointBValue = jb; c.ConflictType = type; return; }
        var logical = (a?.Row ?? b?.Row)!;
        var nc = new SyncConflict
        {
            SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, PkKey = pk, PrimaryKey = ctx.PkJson(logical),
            EndpointAValue = ja, EndpointBValue = jb, ConflictType = type,
            EndpointAVersion = JsonSerializer.SerializeToDocument(new { hash = a?.Hash }),
            EndpointBVersion = JsonSerializer.SerializeToDocument(new { hash = b?.Hash }),
        };
        _db.SyncConflicts.Add(nc); existing[pk] = nc; res.Conflicts++;
        _db.SyncEvents.Add(new SyncEvent
        {
            SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, PkKey = pk, PrimaryKey = nc.PrimaryKey, Direction = "BIDIRECTIONAL",
            Operation = Ops.Update, Status = EventStatus.Conflict, ErrorCode = "CONFLICT", ErrorMessage = type, Payload = ja,
        });
    }

    private Task FlushConflictsAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    private async Task ExecuteAsync(RuleContext ctx, Guid runId, char target, List<PendingChange> changes, Dictionary<string, SyncEvent> openByPk,
        Dictionary<string, string> baseUpserts, HashSet<string> baseDeletes, TableResult res, CancellationToken ct)
    {
        if (changes.Count == 0) return;
        var outcomes = await _applier.ApplyAsync(ctx, target, changes, ctx.Group.BatchSize, ctx.Group.MaxRetries, ct);
        foreach (var o in outcomes)
        {
            var c = o.Change;
            SyncEvent ev;
            if (!openByPk.TryGetValue(c.PkKey, out ev!) || ev.Direction != (c.Source == 'A' ? "A_TO_B" : "B_TO_A"))
            {
                ev = EventRecorder.Create(ctx, runId, c, c.Op.Values == null ? null : ctx.RowJson(c.Logical));
                _db.SyncEvents.Add(ev);
            }
            else { ev.RunId = runId; ev.Operation = c.Operation; }
            EventRecorder.UpdateFromOutcome(ev, o.Error, ctx.Group.MaxRetries);

            if (o.Error == null)
            {
                if (c.Operation == Ops.Delete) { baseDeletes.Add(c.PkKey); baseUpserts.Remove(c.PkKey); res.Deleted++; }
                else
                {
                    baseUpserts[c.PkKey] = c.NewHash!; baseDeletes.Remove(c.PkKey);
                    if (c.Operation == Ops.Insert) res.Inserted++; else res.Updated++;
                }
            }
            else
            {
                res.Failed++;
                _log.LogWarning("Gagal apply {Op} {Table} pk={Pk}: {Code} {Msg}", c.Operation, ctx.Label, c.PkKey, o.Error.Code, o.Error.Message);
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task UpdateCheckpointAsync(RuleContext ctx, TableResult res, CancellationToken ct)
    {
        foreach (var dir in new[] { "A_TO_B", "B_TO_A" })
        {
            var cp = await _db.SyncCheckpoints.FirstOrDefaultAsync(x => x.TableRuleId == ctx.Rule.Id && x.Direction == dir, ct);
            if (cp == null)
            { cp = new SyncCheckpoint { SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, Direction = dir }; _db.SyncCheckpoints.Add(cp); }
            var pos = JsonSerializer.SerializeToDocument(new { at = DateTimeOffset.UtcNow, mechanism = "snapshot-hash" });
            cp.CapturePosition = pos; cp.DurablePosition = pos;
            if (res.Failed == 0) { cp.AppliedPosition = pos; cp.LastSuccessAt = DateTimeOffset.UtcNow; cp.Status = "OK"; }
            else cp.Status = "PARTIAL";
            cp.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}
