using System.Text.Json;

namespace DBSync.SyncEngine.Cdc;

public sealed record CdcRecord(
    string Op, string Database, string? Schema, string Table,
    Dictionary<string, object?>? Before, Dictionary<string, object?>? After, long TsMs, string? Lsn)
{
    /// <summary>Nama schema yang dipakai DBSync: MySQL memakai nama database, vendor lain memakai schema.</summary>
    public string EffectiveSchema => string.IsNullOrEmpty(Schema) ? Database : Schema!;
}

/// <summary>Mengubah envelope JSON Debezium (schemas.enable=true) ke model internal DBSync.</summary>
public static class DebeziumEventNormalizer
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static CdcRecord? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement payload = root.TryGetProperty("payload", out var p) ? p : root;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("op", out var opEl)) return null;
        var op = opEl.GetString() ?? "";
        if (!payload.TryGetProperty("source", out var src)) return null;

        var before = payload.TryGetProperty("before", out var b) && b.ValueKind == JsonValueKind.Object ? Decode(b, FieldSchemas(root, "before")) : null;
        var after = payload.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Object ? Decode(a, FieldSchemas(root, "after")) : null;
        long ts = payload.TryGetProperty("ts_ms", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
        string? lsn = src.TryGetProperty("lsn", out var l) && l.ValueKind != JsonValueKind.Null ? l.ToString()
                    : src.TryGetProperty("pos", out var ps) ? ps.ToString() : src.TryGetProperty("commit_lsn", out var cl) ? cl.ToString() : null;
        return new CdcRecord(op, Str(src, "db") ?? "", Str(src, "schema"), Str(src, "table") ?? "", before, after, ts, lsn);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, (string Type, string? Name)> FieldSchemas(JsonElement root, string field)
    {
        var d = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("schema", out var sch) || !sch.TryGetProperty("fields", out var fields)) return d;
        foreach (var f in fields.EnumerateArray())
        {
            if (Str(f, "field") != field || !f.TryGetProperty("fields", out var cols)) continue;
            foreach (var c in cols.EnumerateArray())
                d[Str(c, "field") ?? ""] = (Str(c, "type") ?? "", Str(c, "name"));
        }
        return d;
    }

    private static Dictionary<string, object?> Decode(JsonElement row, Dictionary<string, (string Type, string? Name)> schema)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in row.EnumerateObject())
        {
            schema.TryGetValue(prop.Name, out var s);
            d[prop.Name] = DecodeValue(prop.Value, s.Type, s.Name);
        }
        return d;
    }

    private static object? DecodeValue(JsonElement v, string? type, string? name)
    {
        if (v.ValueKind == JsonValueKind.Null) return null;
        switch (name)
        {
            case "io.debezium.time.Date": case "org.apache.kafka.connect.data.Date": return Epoch.AddDays(v.GetInt64());
            case "io.debezium.time.Timestamp": case "org.apache.kafka.connect.data.Timestamp": return Epoch.AddMilliseconds(v.GetInt64());
            case "io.debezium.time.MicroTimestamp": return Epoch.AddTicks(v.GetInt64() * 10);
            case "io.debezium.time.NanoTimestamp": return Epoch.AddTicks(v.GetInt64() / 100);
            case "io.debezium.time.ZonedTimestamp": return DateTimeOffset.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
            case "io.debezium.time.Time": case "org.apache.kafka.connect.data.Time": return TimeSpan.FromMilliseconds(v.GetInt64());
            case "io.debezium.time.MicroTime": return TimeSpan.FromTicks(v.GetInt64() * 10);
            case "io.debezium.time.NanoTime": return TimeSpan.FromTicks(v.GetInt64() / 100);
            case "io.debezium.time.ZonedTime": return v.GetString();
            case "io.debezium.time.Year": return v.GetInt32();
        }
        return v.ValueKind switch
        {
            JsonValueKind.String when type == "bytes" => Convert.FromBase64String(v.GetString()!),
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (object)v.GetDecimal(),
            _ => v.GetRawText()
        };
    }
}
