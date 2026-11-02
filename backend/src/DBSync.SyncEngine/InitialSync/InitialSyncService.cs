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

/// <param name="ConflictMode">OVERWRITE = baris di tujuan dengan PK sama ditimpa; SKIP_EXISTING = baris yang sudah ada di tujuan TIDAK disentuh (yang isinya berbeda dibiarkan, akan muncul sebagai konflik pada sinkronisasi dua arah).</param>
/// <param name="Merge">Gabungkan dua arah: baris yang hanya ada di A disalin ke B, yang hanya ada di B disalin ke A, identik = sinkron, ID sama isi beda = konflik (tidak ada yang ditimpa).</param>
/// <param name="DifferentPolicy">Khusus Merge. Untuk baris yang ID-nya sama tetapi isinya berbeda: CONFLICT (putuskan manual) | FILL_EMPTY (isi kolom kosong dari sisi lain) | A_WINS | B_WINS.</param>
/// <param name="FillFallback">Untuk FILL_EMPTY: bila ada kolom yang sama-sama terisi tapi beda: CONFLICT | A_WINS | B_WINS.</param>
public sealed record InitialSyncOptions(char SourceRole = 'A', bool Restart = false, string ConflictMode = "OVERWRITE", bool Merge = false,
    string DifferentPolicy = "CONFLICT", string FillFallback = "CONFLICT");

/// <summary>Satu baris berbeda: kolom mana yang tidak sama, dan apakah hanya "kosong vs terisi" (bisa diisi otomatis).</summary>
public sealed record ColumnDiff(string Column, object? A, object? B);
public sealed record RowDiff(string PkKey, System.Text.Json.JsonDocument Pk, List<ColumnDiff> Diffs, bool Fillable);

public sealed class DiffResult
{
    public long SourceRows, TargetRows, Identical;
    public Dictionary<string, Dictionary<string, object?>> OnlyA = new(), OnlyB = new();
    public List<(string Pk, Dictionary<string, object?> A, Dictionary<string, object?> B)> Different = new();
    public bool RowsKept;
}

/// <summary>Hasil pratinjau: perbandingan baris sumber vs tujuan sebelum initial sync (tanpa menulis apa pun).</summary>
public sealed record InitialPreview(long SourceRows, long TargetRows, long OnlyInSource, long OnlyInTarget, long Identical, long Different, long? Fillable = null);

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
    private readonly IncrementalSyncService _incremental;
    public InitialSyncService(DbSyncContext db, ChangeApplier applier, IncrementalSyncService incremental, ILogger<InitialSyncService> log)
    { _db = db; _applier = applier; _incremental = incremental; _log = log; }

    /// <summary>Initial sync dua arah: satu siklus perbandingan tiga arah dengan baseline kosong, lalu tabel ditandai selesai.</summary>
    private async Task<TableResult> MergeAsync(RuleContext ctx, SyncRunItem item, InitialSyncOptions opt, Func<CancellationToken, Task> progress, CancellationToken ct)
    {
        if (!ctx.CanAToB || !ctx.CanBToA)
            throw new ConnectorException(ErrorClass.Configuration, "DIRECTION", "Penggabungan dua arah membutuhkan arah dua arah pada grup dan tabel (A→B dan B→A aktif).");
        var rule = await _db.SyncTableRules.FirstAsync(r => r.Id == ctx.Rule.Id, ct);
        rule.InitialSyncStatus = "RUNNING";
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM sync_row_states WHERE table_rule_id = {0}", new object[] { rule.Id }, ct);
        await _db.SyncEvents.Where(e => e.TableRuleId == rule.Id && (e.Status == EventStatus.DeadLetter || e.Status == EventStatus.Retry))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Skipped).SetProperty(e => e.ErrorMessage, "Digantikan oleh initial sync ulang"), ct);
        await _db.SaveChangesAsync(ct);

        // 1) baris yang berbeda diselesaikan sesuai model pilihan (isi kolom kosong / pakai A / pakai B); sisanya tetap jadi konflik
        var (resolved, resolveFailed) = await PreResolveAsync(ctx, item, opt, ct);
        // 2) baris yang hanya ada di satu sisi disalin ke sisi lain, yang identik ditandai sinkron
        var r = await _incremental.SyncAsync(ctx, item.SyncRunId, progress, ct);
        r.Updated += resolved; r.Failed += resolveFailed;
        item.RecordsRead = r.Read; item.RecordsWritten = r.Inserted + r.Updated + r.Deleted; item.RecordsSkipped = r.Skipped; item.RecordsFailed = r.Failed;
        if (r.Conflicts > 0)
            item.ErrorMessage = $"{r.Conflicts:N0} baris bentrok (ID sama, isi berbeda) — tidak ada yang ditimpa. Putuskan di menu Konflik.";
        rule.InitialSyncStatus = r.Failed == 0 ? "COMPLETED" : "COMPLETED_WITH_ERRORS";
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<TableResult> RunAsync(RuleContext ctx, SyncRunItem item, InitialSyncOptions opt, Func<CancellationToken, Task> progress, CancellationToken ct)
    {
        if (opt.Merge) return await MergeAsync(ctx, item, opt, progress, ct);
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
            // percobaan ulang dari awal: event gagal dari percobaan lama tidak relevan lagi (dan akan menghalangi baris itu disinkronkan)
            await _db.SyncEvents.Where(e => e.TableRuleId == rule.Id && (e.Status == EventStatus.DeadLetter || e.Status == EventStatus.Retry))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Skipped).SetProperty(e => e.ErrorMessage, "Digantikan oleh initial sync ulang"), ct);
        }

        Dictionary<string, string>? tgtHashes = opt.ConflictMode == "SKIP_EXISTING" ? await ScanHashesAsync(ctx, tgt, ct) : null;
        long skippedDifferent = 0, skippedSame = 0;

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

            var toWrite = rows;
            if (tgtHashes != null)
            {
                // mode aman: baris yang sudah ada di tujuan tidak ditimpa
                toWrite = new List<Dictionary<string, object?>>(); var inSync = new List<(string, string)>();
                foreach (var l in rows)
                {
                    var k = ctx.PkKey(l);
                    if (!tgtHashes.TryGetValue(k, out var th)) toWrite.Add(l);
                    else if (th == ctx.RowHash(l)) inSync.Add((k, th));
                    else skippedDifferent++;
                }
                await RowStateStore.UpsertAsync(_db, rule.Id, inSync, ct);
                skippedSame += inSync.Count;
                res.Skipped = skippedSame + skippedDifferent;
                item.RecordsSkipped = res.Skipped;
            }
            var changes = toWrite.Select(l => new PendingChange(src, tgt, Ops.Insert, ctx.PkKey(l), l, ctx.RowHash(l), ctx.BuildUpsert(l, tgt))).ToList();
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

        if (skippedDifferent > 0)
            item.ErrorMessage = $"{skippedDifferent:N0} baris dilewati karena ID-nya sudah ada di tujuan dengan isi BERBEDA (tidak ditimpa). Selesaikan lewat menu Konflik atau pilih mode timpa.";

        // validasi sederhana: jumlah baris target >= jumlah sumber yang ditulis
        var tgtCount = await ctx.Conn(tgt).CountRowsAsync(ctx.Profile(tgt), tgt == 'A' ? rule.EndpointASchema : rule.EndpointBSchema, tgt == 'A' ? rule.EndpointATable : rule.EndpointBTable, ct);
        cp.AppliedPosition = JsonSerializer.SerializeToDocument(new { source_rows = res.Read, target_rows = tgtCount, failed = res.Failed });
        cp.LastSuccessAt = DateTimeOffset.UtcNow; cp.Status = res.Failed == 0 ? "OK" : "PARTIAL";
        rule.InitialSyncStatus = res.Failed == 0 ? "COMPLETED" : "COMPLETED_WITH_ERRORS";
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return res;
    }

    private static async Task<Dictionary<string, string>> ScanHashesAsync(RuleContext ctx, char side, CancellationToken ct)
    {
        var d = new Dictionary<string, string>();
        await foreach (var raw in ctx.Conn(side).ReadRowsAsync(ctx.Profile(side), ctx.BuildRead(side), ct))
        { var l = ctx.ToLogical(raw, side); d[ctx.PkKey(l)] = ctx.RowHash(l); }
        return d;
    }

    private const int MaxKeptRows = 100_000;

    /// <summary>
    /// Klasifikasi A vs B: hanya di A, hanya di B, identik, berbeda. Hash disimpan untuk semua baris; baris penuh hanya untuk yang
    /// TIDAK identik (hemat memori). Bila baris yang harus disimpan melebihi batas, RowsKept=false (hanya hitungan).
    /// </summary>
    public async Task<DiffResult> ClassifyAsync(RuleContext ctx, bool keepRows, CancellationToken ct)
    {
        var r = new DiffResult { RowsKept = keepRows };
        var hashB = await ScanHashesAsync(ctx, 'B', ct);
        var hashA = new Dictionary<string, string>(); var diffA = new Dictionary<string, Dictionary<string, object?>>();
        long kept = 0;
        await foreach (var raw in ctx.ConnA.ReadRowsAsync(ctx.ProfileA, ctx.BuildRead('A'), ct))
        {
            var l = ctx.ToLogical(raw, 'A'); var pk = ctx.PkKey(l); var h = ctx.RowHash(l); hashA[pk] = h; r.SourceRows++;
            if (!hashB.TryGetValue(pk, out var hb)) { r.OnlyA[pk] = keepRows ? l : null!; if (keepRows && ++kept > MaxKeptRows) { r.RowsKept = keepRows = false; } }
            else if (hb == h) r.Identical++;
            else if (keepRows) { diffA[pk] = l; if (++kept > MaxKeptRows) { r.RowsKept = keepRows = false; } }
            else diffA[pk] = null!;
        }
        r.TargetRows = hashB.Count;
        await foreach (var raw in ctx.ConnB.ReadRowsAsync(ctx.ProfileB, ctx.BuildRead('B'), ct))
        {
            var l = ctx.ToLogical(raw, 'B'); var pk = ctx.PkKey(l);
            if (!hashA.TryGetValue(pk, out var ha)) { r.OnlyB[pk] = keepRows ? l : null!; if (keepRows && ++kept > MaxKeptRows) { r.RowsKept = keepRows = false; } }
            else if (ha != ctx.RowHash(l) && diffA.TryGetValue(pk, out var a)) r.Different.Add((pk, a, keepRows ? l : null!));
        }
        if (!r.RowsKept) { r.OnlyA = r.OnlyA.ToDictionary(k => k.Key, _ => (Dictionary<string, object?>)null!); }
        return r;
    }

    private static bool Empty(object? v) => v is null || (v is string s && s.Length == 0);

    /// <summary>Kolom yang nilainya tidak sama (non-PK, non-generated). Fillable = setiap perbedaan hanya "kosong vs terisi".</summary>
    public static (List<ColumnDiff> Diffs, bool Fillable, Dictionary<string, object?> Merged) Compare(RuleContext ctx, Dictionary<string, object?> a, Dictionary<string, object?> b)
    {
        var diffs = new List<ColumnDiff>(); bool fillable = true; var merged = new Dictionary<string, object?>(a, StringComparer.OrdinalIgnoreCase);
        foreach (var c in ctx.Columns)
        {
            if (c.IsPk || c.A!.IsGenerated || c.B!.IsGenerated) continue;
            var av = a[c.AName]; var bv = b[c.AName];
            bool fx = RuleContext.IsFixedChar(c);
            if (Values.Normalize(av ?? c.Fill, c.Canon, fx) == Values.Normalize(bv ?? c.Fill, c.Canon, fx)) continue;
            diffs.Add(new ColumnDiff(c.AName, Values.ToJsonSafe(av), Values.ToJsonSafe(bv)));
            if (Empty(av) && !Empty(bv)) merged[c.AName] = bv;
            else if (Empty(bv) && !Empty(av)) merged[c.AName] = av;
            else fillable = false;
        }
        return (diffs, fillable && diffs.Count > 0, merged);
    }

    /// <summary>Bandingkan sumber dan tujuan tanpa menulis apa pun: baris baru, identik, berbeda (dan berapa yang bisa saling mengisi).</summary>
    public async Task<InitialPreview> PreviewAsync(RuleContext ctx, char src, CancellationToken ct)
    {
        var d = await ClassifyAsync(ctx, true, ct);
        long fillable = 0;
        if (d.RowsKept) foreach (var (_, a, b) in d.Different) if (Compare(ctx, a, b).Fillable) fillable++;
        // dari sudut pandang "sumber" yang dipilih; Merge memakai A sebagai acuan simetris
        long onlySrc = src == 'A' ? d.OnlyA.Count : d.OnlyB.Count, onlyTgt = src == 'A' ? d.OnlyB.Count : d.OnlyA.Count;
        long srcRows = src == 'A' ? d.SourceRows : d.TargetRows, tgtRows = src == 'A' ? d.TargetRows : d.SourceRows;
        return new InitialPreview(srcRows, tgtRows, onlySrc, onlyTgt, d.Identical, d.Different.Count, d.RowsKept ? fillable : null);
    }

    /// <summary>Daftar contoh baris per kategori untuk diperiksa pengguna (read-only).</summary>
    public async Task<object> CompareDetailAsync(RuleContext ctx, string kind, int limit, CancellationToken ct)
    {
        var d = await ClassifyAsync(ctx, true, ct);
        if (!d.RowsKept) throw new ConnectorException(ErrorClass.Configuration, "TOO_LARGE", $"Terlalu banyak baris berbeda (> {MaxKeptRows:N0}) untuk ditampilkan rinci. Gunakan ringkasan jumlah saja.");
        limit = Math.Clamp(limit, 1, 200);
        switch (kind)
        {
            case "ONLY_A": return new { total = d.OnlyA.Count, items = d.OnlyA.Take(limit).Select(x => new { pk = ctx.PkJson(x.Value), row = ctx.RowJson(x.Value) }).ToList() };
            case "ONLY_B": return new { total = d.OnlyB.Count, items = d.OnlyB.Take(limit).Select(x => new { pk = ctx.PkJson(x.Value), row = ctx.RowJson(x.Value) }).ToList() };
            case "DIFFERENT":
                return new
                {
                    total = d.Different.Count,
                    items = d.Different.Take(limit).Select(x => { var c = Compare(ctx, x.A, x.B); return new { pk = ctx.PkJson(x.A), diffs = c.Diffs, fillable = c.Fillable }; }).ToList()
                };
            default: throw new ConnectorException(ErrorClass.Configuration, "INVALID_KIND", "kind harus ONLY_A, ONLY_B, atau DIFFERENT.");
        }
    }

    /// <summary>
    /// Sebelum penggabungan: selesaikan baris yang berbeda sesuai model pilihan pengguna dan tulis hasilnya ke KEDUA sisi.
    /// Baris yang tidak terselesaikan (mis. FILL_EMPTY dengan kolom bentrok + fallback CONFLICT) dibiarkan menjadi konflik manual.
    /// </summary>
    private async Task<(int Resolved, int Failed)> PreResolveAsync(RuleContext ctx, SyncRunItem item, InitialSyncOptions opt, CancellationToken ct)
    {
        if (opt.DifferentPolicy == "CONFLICT") return (0, 0);
        var d = await ClassifyAsync(ctx, true, ct);
        if (!d.RowsKept) throw new ConnectorException(ErrorClass.Configuration, "TOO_LARGE", "Terlalu banyak baris berbeda untuk diselesaikan otomatis; gunakan mode konflik manual.");
        var toA = new List<PendingChange>(); var toB = new List<PendingChange>(); var needs = new Dictionary<string, (int Need, int Ok, string Hash)>();
        foreach (var (pk, a, b) in d.Different)
        {
            Dictionary<string, object?>? final = opt.DifferentPolicy switch { "A_WINS" => a, "B_WINS" => b, _ => null };
            if (opt.DifferentPolicy == "FILL_EMPTY")
            {
                var c = Compare(ctx, a, b);
                final = c.Fillable ? c.Merged : opt.FillFallback switch { "A_WINS" => a, "B_WINS" => b, _ => null };
            }
            if (final == null) continue;
            var h = ctx.RowHash(final); int need = 0;
            if (ctx.RowHash(a) != h) { toA.Add(new PendingChange('B', 'A', Ops.Update, pk, final, h, ctx.BuildUpsert(final, 'A'))); need++; }
            if (ctx.RowHash(b) != h) { toB.Add(new PendingChange('A', 'B', Ops.Update, pk, final, h, ctx.BuildUpsert(final, 'B'))); need++; }
            needs[pk] = (need, 0, h);
        }
        int failed = 0;
        foreach (var (side, list) in new[] { ('A', toA), ('B', toB) })
        {
            if (list.Count == 0) continue;
            var outcomes = await _applier.ApplyAsync(ctx, side, list, ctx.Group.BatchSize, ctx.Group.MaxRetries, ct);
            foreach (var o in outcomes)
            {
                var ev = EventRecorder.Create(ctx, item.SyncRunId, o.Change, ctx.RowJson(o.Change.Logical));
                EventRecorder.UpdateFromOutcome(ev, o.Error, 0); _db.SyncEvents.Add(ev);
                var n = needs[o.Change.PkKey];
                if (o.Error == null) needs[o.Change.PkKey] = (n.Need, n.Ok + 1, n.Hash); else failed++;
            }
        }
        var okRows = needs.Where(kv => kv.Value.Ok >= kv.Value.Need).Select(kv => (kv.Key, kv.Value.Hash)).ToList();
        await RowStateStore.UpsertAsync(_db, ctx.Rule.Id, okRows, ct);
        await _db.SaveChangesAsync(ct);
        return (okRows.Count, failed);
    }
}
