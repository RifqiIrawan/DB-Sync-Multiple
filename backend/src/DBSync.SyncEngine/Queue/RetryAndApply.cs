using System.Text.Json;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DBSync.SyncEngine.Queue;

public static class Backoff
{
    private static readonly Random Rng = new();

    /// <summary>Exponential backoff + jitter.</summary>
    public static TimeSpan Delay(int attempt, double baseSeconds = 2, double maxSeconds = 3600)
    {
        var exp = Math.Min(maxSeconds, baseSeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        double jitter;
        lock (Rng) jitter = Rng.NextDouble() * exp * 0.25;
        return TimeSpan.FromSeconds(exp + jitter);
    }
}

public sealed record PendingChange(
    char Source, char Target, string Operation, string PkKey, Dictionary<string, object?> Logical,
    string? NewHash, ChangeOperation Op);

public sealed record ApplyOutcome(PendingChange Change, ConnectorException? Error);

/// <summary>Menulis perubahan ke target: batch dalam satu transaksi, fallback per-baris untuk mengisolasi kegagalan.</summary>
public sealed class ChangeApplier
{
    private readonly ILogger<ChangeApplier> _log;
    public ChangeApplier(ILogger<ChangeApplier> log) => _log = log;

    public async Task<List<ApplyOutcome>> ApplyAsync(RuleContext ctx, char target, IReadOnlyList<PendingChange> changes, int batchSize, int maxTransientRetries, CancellationToken ct)
    {
        var results = new List<ApplyOutcome>(changes.Count);
        var conn = ctx.Conn(target); var profile = ctx.Profile(target);
        foreach (var batch in changes.Chunk(Math.Max(1, batchSize)))
        {
            ct.ThrowIfCancellationRequested();
            if (await TryBatchAsync(conn, profile, batch, maxTransientRetries, ct))
            {
                results.AddRange(batch.Select(c => new ApplyOutcome(c, null)));
                continue;
            }
            // isolasi: satu per satu
            foreach (var ch in batch)
            {
                ConnectorException? err = null;
                try { await WithRetryAsync(() => conn.ApplyChangesAsync(profile, new[] { ch.Op }, ct), maxTransientRetries, ct); }
                catch (ConnectorException ex) { err = ex; }
                catch (Exception ex) when (ex is not OperationCanceledException) { err = new ConnectorException(ErrorClass.Permanent, "UNEXPECTED", ex.Message, ex); }
                results.Add(new ApplyOutcome(ch, err));
            }
        }
        return results;
    }

    private async Task<bool> TryBatchAsync(IDatabaseConnector conn, ConnectionProfile p, PendingChange[] batch, int retries, CancellationToken ct)
    {
        try
        {
            await WithRetryAsync(() => conn.ApplyChangesAsync(p, batch.Select(b => b.Op).ToList(), ct), retries, ct);
            return true;
        }
        catch (ConnectorException ex)
        {
            _log.LogWarning("Batch {Count} baris gagal ({Code}); fallback per-baris: {Msg}", batch.Length, ex.Code, ex.Message);
            return false;
        }
    }

    public static async Task WithRetryAsync<T>(Func<Task<T>> action, int maxRetries, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { await action(); return; }
            catch (ConnectorException ex) when (ex.Class == ErrorClass.Transient && attempt <= maxRetries)
            {
                await Task.Delay(Backoff.Delay(attempt, 1, 30), ct);
            }
        }
    }
}

public static class EventRecorder
{
    public static SyncEvent Create(RuleContext ctx, Guid? runId, PendingChange c, JsonDocument? payload) => new()
    {
        SyncGroupId = ctx.Group.Id, TableRuleId = ctx.Rule.Id, RunId = runId, SourceRole = c.Source.ToString(),
        Direction = c.Source == 'A' ? "A_TO_B" : "B_TO_A", Operation = c.Operation, PkKey = c.PkKey,
        PrimaryKey = ctx.PkJson(c.Logical), Payload = payload,
    };

    /// <summary>Terapkan hasil apply ke event (status, retry, dead-letter) mengikuti state machine.</summary>
    public static void UpdateFromOutcome(SyncEvent ev, ConnectorException? err, int maxRetries)
    {
        void Set(string st) { ev.Status = st; }
        ev.ProcessedAt = DateTimeOffset.UtcNow;
        if (err is null)
        {
            Set(EventStatus.Success); ev.ErrorCode = null; ev.ErrorMessage = null; ev.NextRetryAt = null; return;
        }
        ev.ErrorCode = err.Code; ev.ErrorMessage = Truncate(err.Message, 2000);
        ev.RetryCount++;
        if (err.Class != ErrorClass.Transient || ev.RetryCount > maxRetries)
        {
            Set(EventStatus.DeadLetter); ev.NextRetryAt = null;
        }
        else
        {
            Set(EventStatus.Retry);
            ev.NextRetryAt = DateTimeOffset.UtcNow + Backoff.Delay(ev.RetryCount, 30);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

