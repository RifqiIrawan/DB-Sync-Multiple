using System.Text.Json;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using DBSync.SyncEngine.Incremental;
using DBSync.SyncEngine.InitialSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DBSync.SyncEngine.Queue;

public sealed class RunExecutor
{
    private readonly IServiceScopeFactory _sf;
    private readonly ILogger<RunExecutor> _log;
    public RunExecutor(IServiceScopeFactory sf, ILogger<RunExecutor> log) { _sf = sf; _log = log; }

    public async Task ExecuteAsync(Guid runId, string workerId, long fencingToken, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var ct = cts.Token;
        bool cancelledByUser = false;

        // loop lease + cancel watcher
        var watcher = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    using var s = _sf.CreateScope();
                    var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
                    var n = await db.Database.ExecuteSqlRawAsync(
                        "UPDATE sync_runs SET lease_expires_at = now() + interval '60 seconds' WHERE id = {0} AND locked_by = {1} AND fencing_token = {2}",
                        new object[] { runId, workerId, fencingToken }, CancellationToken.None);
                    if (n == 0) { _log.LogWarning("Lease run {Run} hilang; membatalkan eksekusi lokal.", runId); cts.Cancel(); break; }
                    var req = await db.SyncRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.CancelRequested).FirstOrDefaultAsync(CancellationToken.None);
                    if (req) { cancelledByUser = true; cts.Cancel(); break; }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "Watcher run {Run} error", runId); }
            }
        });

        string finalStatus = RunStatus.Success; string? error = null;
        var totals = new TableResult();
        try
        {
            using var scope = _sf.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DbSyncContext>();
            var run = await db.SyncRuns.Include(r => r.SyncGroup).FirstAsync(r => r.Id == runId, ct);
            var group = run.SyncGroup!;
            var rules = await db.SyncTableRules.AsNoTracking().Where(r => r.SyncGroupId == group.Id && r.IsEnabled).ToListAsync(ct);

            InitialSyncOptions? initial = null;
            if (run.RunType == RunTypes.Initial)
            {
                char src = 'A'; bool restart = false; HashSet<Guid>? only = null;
                if (run.Options != null)
                {
                    var o = run.Options.RootElement;
                    if (o.TryGetProperty("sourceRole", out var sr) && sr.GetString() is { Length: > 0 } s0) src = char.ToUpperInvariant(s0[0]);
                    if (o.TryGetProperty("restart", out var rs) && rs.ValueKind == JsonValueKind.True) restart = true;
                    if (o.TryGetProperty("tableRuleIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                        only = ids.EnumerateArray().Select(x => x.GetGuid()).ToHashSet();
                }
                else if (group.SyncDirection == Directions.BToA) src = 'B';
                if (only is { Count: > 0 }) rules = rules.Where(r => only.Contains(r.Id)).ToList();
                initial = new InitialSyncOptions(src, restart);
            }

            // konteks seluruh aturan (untuk urutan dependensi FK)
            var contexts = new List<(SyncTableRule Rule, RuleContext? Ctx, SyncRunItem Item, string? LoadError)>();
            foreach (var rule in rules)
            {
                var item = new SyncRunItem { SyncRunId = run.Id, TableRuleId = rule.Id, TableLabel = $"{rule.EndpointASchema}.{rule.EndpointATable} ⇄ {rule.EndpointBSchema}.{rule.EndpointBTable}", Status = RunStatus.Queued };
                db.SyncRunItems.Add(item);
                RuleContext? ctx = null; string? err = null;
                try
                {
                    ctx = await scope.ServiceProvider.GetRequiredService<IRuleContextFactory>().CreateAsync(rule.Id, ct);
                    if (initial == null && !rule.InitialSyncStatus.StartsWith("COMPLETED"))
                    { item.Status = "SKIPPED"; item.ErrorMessage = "Initial sync belum dijalankan untuk tabel ini."; item.FinishedAt = DateTimeOffset.UtcNow; ctx = null; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { err = ex.Message; item.Status = RunStatus.Failed; item.ErrorMessage = err; item.FinishedAt = DateTimeOffset.UtcNow; }
                contexts.Add((rule, ctx, item, err));
            }
            await db.SaveChangesAsync(ct);

            foreach (var wave in BuildWaves(contexts.Where(c => c.Ctx != null).Select(c => (c.Rule, c.Ctx!, c.Item)).ToList()))
            {
                var sem = new SemaphoreSlim(Math.Max(1, group.MaxParallelTables));
                var tasks = wave.Select(async w =>
                {
                    await sem.WaitAsync(ct);
                    try { return await RunTableAsync(w.Rule.Id, w.Item.Id, runId, initial, ct); }
                    finally { sem.Release(); }
                }).ToList();
                var results = await Task.WhenAll(tasks);
                foreach (var r in results) { totals.Read += r.Read; totals.Inserted += r.Inserted; totals.Updated += r.Updated; totals.Deleted += r.Deleted; totals.Skipped += r.Skipped; totals.Failed += r.Failed; totals.Conflicts += r.Conflicts; }
            }

            await db.Entry(run).ReloadAsync(CancellationToken.None);
            var items = await db.SyncRunItems.AsNoTracking().Where(i => i.SyncRunId == runId).ToListAsync(CancellationToken.None);
            var failedItems = items.Count(i => i.Status == RunStatus.Failed);
            finalStatus = failedItems == 0 && totals.Failed == 0 ? RunStatus.Success
                : failedItems == items.Count(i => i.Status != "SKIPPED") && items.Any(i => i.Status != "SKIPPED") ? RunStatus.Failed : RunStatus.PartialSuccess;
            if (failedItems > 0) error = string.Join("; ", items.Where(i => i.Status == RunStatus.Failed).Select(i => $"{i.TableLabel}: {i.ErrorMessage}").Take(5));
            else if (totals.Failed > 0) error = $"{totals.Failed} perubahan gagal; lihat Failed Events / Dead Letter.";
        }
        catch (OperationCanceledException)
        {
            if (stopping.IsCancellationRequested) { await RequeueAsync(runId, workerId, fencingToken); cts.Cancel(); await SafeWait(watcher); return; }
            finalStatus = cancelledByUser ? RunStatus.Cancelled : RunStatus.Failed;
            if (!cancelledByUser) error = "Lease hilang atau eksekusi dibatalkan.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run {Run} gagal", runId);
            finalStatus = RunStatus.Failed; error = ex.Message;
        }

        cts.Cancel(); await SafeWait(watcher);
        await FinalizeAsync(runId, workerId, fencingToken, finalStatus, error, totals);
    }

    private static async Task SafeWait(Task t) { try { await t; } catch { } }

    private async Task<TableResult> RunTableAsync(Guid ruleId, Guid itemId, Guid runId, InitialSyncOptions? initial, CancellationToken ct)
    {
        using var scope = _sf.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DbSyncContext>();
        var item = await db.SyncRunItems.FirstAsync(i => i.Id == itemId, ct);
        item.Status = RunStatus.Running; item.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        try
        {
            var ctx = await sp.GetRequiredService<IRuleContextFactory>().CreateAsync(ruleId, ct);
            Func<CancellationToken, Task> progress = async c => await db.SaveChangesAsync(c);
            TableResult r;
            if (initial != null) r = await sp.GetRequiredService<InitialSyncService>().RunAsync(ctx, item, initial, progress, ct);
            else
            {
                r = await sp.GetRequiredService<IncrementalSyncService>().SyncAsync(ctx, runId, progress, ct);
                item.RecordsRead = r.Read; item.RecordsWritten = r.Inserted + r.Updated + r.Deleted; item.RecordsSkipped = r.Skipped; item.RecordsFailed = r.Failed;
            }
            item.Status = r.Failed > 0 ? RunStatus.PartialSuccess : RunStatus.Success;
            item.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return r;
        }
        catch (OperationCanceledException)
        {
            item.Status = RunStatus.Cancelled; item.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tabel {Rule} gagal pada run {Run}", ruleId, runId);
            item.Status = RunStatus.Failed; item.ErrorMessage = ex.Message; item.FinishedAt = DateTimeOffset.UtcNow;
            if (initial != null)
            {
                var rule = await db.SyncTableRules.FirstAsync(r => r.Id == ruleId, CancellationToken.None);
                if (rule.InitialSyncStatus == "RUNNING" && ex is ConnectorException { Class: ErrorClass.Permanent or ErrorClass.Configuration }) rule.InitialSyncStatus = "FAILED";
            }
            await db.SaveChangesAsync(CancellationToken.None);
            return new TableResult();
        }
    }

    /// <summary>Urutkan tabel menurut dependensi foreign key (induk lebih dulu); tabel setingkat berjalan paralel.</summary>
    private static List<List<(SyncTableRule Rule, RuleContext Ctx, SyncRunItem Item)>> BuildWaves(List<(SyncTableRule Rule, RuleContext Ctx, SyncRunItem Item)> all)
    {
        var byTable = all.ToDictionary(x => $"{x.Rule.EndpointASchema}.{x.Rule.EndpointATable}".ToLowerInvariant());
        var level = new Dictionary<string, int>();
        int Level(string key, HashSet<string> stack)
        {
            if (level.TryGetValue(key, out var l)) return l;
            if (!stack.Add(key)) return 0; // siklus FK -> abaikan
            var ctx = byTable[key].Ctx; int max = -1;
            foreach (var fk in ctx.SchemaA.ForeignKeys)
            {
                var rk = $"{fk.RefSchema}.{fk.RefTable}".ToLowerInvariant();
                if (rk != key && byTable.ContainsKey(rk)) max = Math.Max(max, Level(rk, stack));
            }
            stack.Remove(key);
            return level[key] = max + 1;
        }
        foreach (var k in byTable.Keys) Level(k, new HashSet<string>());
        return all.GroupBy(x => level[$"{x.Rule.EndpointASchema}.{x.Rule.EndpointATable}".ToLowerInvariant()]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
    }

    private async Task RequeueAsync(Guid runId, string workerId, long token)
    {
        using var s = _sf.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
        await db.Database.ExecuteSqlRawAsync("UPDATE sync_runs SET status='QUEUED', locked_by=NULL, lease_expires_at=NULL WHERE id={0} AND locked_by={1} AND fencing_token={2}", new object[] { runId, workerId, token });
    }

    private async Task FinalizeAsync(Guid runId, string workerId, long token, string status, string? error, TableResult totals)
    {
        using var s = _sf.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DbSyncContext>();
        var run = await db.SyncRuns.FirstAsync(r => r.Id == runId);
        if (run.FencingToken != token || run.LockedBy != workerId) { _log.LogWarning("Run {Run} sudah dimiliki worker lain; finalisasi dilewati.", runId); return; }
        run.Status = status; run.ErrorSummary = error; run.FinishedAt = DateTimeOffset.UtcNow; run.LockedBy = null; run.LeaseExpiresAt = null;
        run.RecordsRead = totals.Read; run.RecordsInserted = totals.Inserted; run.RecordsUpdated = totals.Updated; run.RecordsDeleted = totals.Deleted;
        run.RecordsSkipped = totals.Skipped; run.RecordsFailed = totals.Failed; run.ConflictsDetected = totals.Conflicts;
        var rids = await db.SyncRunItems.Where(i => i.SyncRunId == runId).Select(i => i.RecordsRead).SumAsync();
        if (run.RunType == RunTypes.Initial) { run.RecordsRead = rids; }
        await db.SaveChangesAsync();
    }
}
