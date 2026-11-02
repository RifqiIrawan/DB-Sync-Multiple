using System.Text.Json;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Pipeline;
using DBSync.SyncEngine.Queue;
using Microsoft.EntityFrameworkCore;

namespace DBSync.SyncEngine.Conflict;

public sealed class ConflictService
{
    private readonly DbSyncContext _db;
    private readonly IRuleContextFactory _factory;
    private readonly ChangeApplier _applier;
    public ConflictService(DbSyncContext db, IRuleContextFactory factory, ChangeApplier applier) { _db = db; _factory = factory; _applier = applier; }

    /// <summary>Resolusi manual: winner 'A' atau 'B'. Versi pemenang disebarkan ke sisi lain (termasuk penghapusan).</summary>
    public async Task<SyncConflict> ResolveAsync(Guid conflictId, char winner, Guid? userId, CancellationToken ct)
    {
        var conflict = await _db.SyncConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct)
            ?? throw new KeyNotFoundException("Konflik tidak ditemukan.");
        if (conflict.Status != "PENDING") throw new InvalidOperationException("Konflik sudah diselesaikan.");
        var ctx = await _factory.CreateAsync(conflict.TableRuleId, ct);

        char src = winner, tgt = RuleContext.Other(src);
        var keyStrings = JsonSerializer.Deserialize<string[]>(conflict.PkKey)!;
        var keyVals = ctx.Pk.Select((c, i) => (object?)Values.Coerce(keyStrings[i], ctx.Col(c, src)!)).ToList();

        Dictionary<string, object?>? srcRow = null;
        await foreach (var r in ctx.Conn(src).ReadRowsAsync(ctx.Profile(src), ctx.BuildRead(src, keyEquals: keyVals), ct)) { srcRow = ctx.ToLogical(r, src); break; }

        PendingChange change;
        string? newHash = null;
        if (srcRow != null)
        {
            // pastikan status keberadaan di target
            var tgtKeyVals = ctx.Pk.Select((c, i) => (object?)Values.Coerce(keyStrings[i], ctx.Col(c, tgt)!)).ToList();
            bool exists = false;
            await foreach (var _ in ctx.Conn(tgt).ReadRowsAsync(ctx.Profile(tgt), ctx.BuildRead(tgt, keyEquals: tgtKeyVals), ct)) { exists = true; break; }
            newHash = ctx.RowHash(srcRow);
            change = new PendingChange(src, tgt, exists ? Ops.Update : Ops.Insert, conflict.PkKey, srcRow, newHash, ctx.BuildUpsert(srcRow, tgt));
        }
        else
        {
            var keyRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ctx.Pk.Count; i++) keyRow[ctx.Pk[i].AName] = keyStrings[i];
            change = new PendingChange(src, tgt, Ops.Delete, conflict.PkKey, keyRow, null, ctx.BuildDelete(keyRow, tgt));
        }

        var outcome = (await _applier.ApplyAsync(ctx, tgt, new[] { change }, 1, ctx.Group.MaxRetries, ct)).Single();
        if (outcome.Error != null) throw outcome.Error;

        if (newHash != null) await RowStateStore.UpsertAsync(_db, ctx.Rule.Id, new[] { (conflict.PkKey, newHash) }, ct);
        else await RowStateStore.DeleteAsync(_db, ctx.Rule.Id, new[] { conflict.PkKey }, ct);

        conflict.Status = "RESOLVED"; conflict.ResolutionStrategy = $"MANUAL_{winner}_WINS";
        conflict.ResolvedValue = srcRow == null ? null : ctx.RowJson(srcRow);
        conflict.ResolvedBy = userId; conflict.ResolvedAt = DateTimeOffset.UtcNow;

        var ev = EventRecorder.Create(ctx, null, change, srcRow == null ? null : ctx.RowJson(srcRow));
        ev.Status = EventStatus.Success; ev.ProcessedAt = DateTimeOffset.UtcNow;
        _db.SyncEvents.Add(ev);
        // event konflik terkait ditandai selesai
        foreach (var e in await _db.SyncEvents.Where(e => e.TableRuleId == ctx.Rule.Id && e.PkKey == conflict.PkKey && e.Status == EventStatus.Conflict).ToListAsync(ct))
        { e.Status = EventStatus.Success; e.ProcessedAt = DateTimeOffset.UtcNow; }
        await _db.SaveChangesAsync(ct);
        return conflict;
    }
}
