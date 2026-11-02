using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DBSync.Connectors.Abstractions;

namespace DBSync.SyncEngine.Pipeline;

/// <summary>Normalisasi (untuk hash/perbandingan) dan koersi (untuk menulis) nilai lintas vendor.</summary>
public static class Values
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Normalize(object? v, string logicalType)
    {
        if (v is null || v is DBNull) return "\u0000NULL";
        switch (v)
        {
            case bool b: return b ? "1" : "0";
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return logicalType == LogicalTypes.Boolean ? (Convert.ToDecimal(v, Inv) != 0 ? "1" : "0") : Convert.ToString(v, Inv)!;
            case decimal d: return NormDecimal(d);
            case double db: return double.IsFinite(db) ? NormDecimal(SafeDecimal(db)) : db.ToString(Inv);
            case float f: return double.IsFinite(f) ? NormDecimal(SafeDecimal(f)) : f.ToString(Inv);
            case DateTime dt: return logicalType == LogicalTypes.Date ? dt.ToString("yyyy-MM-dd", Inv) : FormatTs(logicalType == LogicalTypes.TimestampTz && dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt);
            case DateTimeOffset dto: return FormatTs(dto.UtcDateTime);
            case DateOnly dOnly: return dOnly.ToString("yyyy-MM-dd", Inv);
            case TimeSpan ts: return ts.ToString(@"hh\:mm\:ss\.fff", Inv);
            case TimeOnly to: return to.ToString("HH:mm:ss.fff", Inv);
            case Guid g: return g.ToString("D").ToLowerInvariant();
            case byte[] bytes: return "b64:" + Convert.ToBase64String(SHA256.HashData(bytes));
            case string s:
                if (logicalType == LogicalTypes.Json) return CanonJson(s);
                if (logicalType == LogicalTypes.Boolean) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" ? "1" : "0";
                if (logicalType == LogicalTypes.Uuid && Guid.TryParse(s, out var gg)) return gg.ToString("D").ToLowerInvariant();
                return s.TrimEnd(' ');
            case JsonElement je: return CanonJson(je.GetRawText());
            default: return Convert.ToString(v, Inv) ?? "";
        }
    }

    private static decimal SafeDecimal(double d) { try { return (decimal)Math.Round(d, 12); } catch { return 0m; } }
    private static string NormDecimal(decimal d)
    {
        var s = d.ToString("0.#############################", Inv);
        return s == "-0" ? "0" : s;
    }
    private static string FormatTs(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);

    private static string CanonJson(string s)
    {
        try { var n = JsonNode.Parse(s); return Sort(n)?.ToJsonString() ?? "null"; }
        catch { return s; }
    }
    private static JsonNode? Sort(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => KeyValuePair.Create(k.Key, Sort(k.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
        _ => n?.DeepClone()
    };

    public static string Hash(IEnumerable<string> normalized)
    {
        var sb = new StringBuilder();
        foreach (var n in normalized) sb.Append(n.Length).Append(':').Append(n).Append('\u001f');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    /// <summary>Ubah nilai ke tipe CLR yang sesuai untuk kolom target.</summary>
    public static object? Coerce(object? v, ColumnInfo target)
    {
        if (v is null || v is DBNull) return null;
        try
        {
            if (v is JsonElement je) v = FromJson(je);
            if (v is null) return null;
            switch (target.LogicalType)
            {
                case LogicalTypes.Integer:
                    return v switch
                    {
                        bool b => b ? 1 : 0,
                        string s => long.Parse(s, Inv),
                        decimal d => decimal.ToInt64(d),
                        double db => (long)db,
                        ulong ul => ul <= long.MaxValue ? (object)(long)ul : (decimal)ul,
                        _ => v
                    };
                case LogicalTypes.Decimal:
                    return v switch { decimal => v, string s => decimal.Parse(s, Inv), bool b => b ? 1m : 0m, _ => Convert.ToDecimal(v, Inv) };
                case LogicalTypes.Float:
                    return v is string fs ? double.Parse(fs, Inv) : Convert.ToDouble(v, Inv);
                case LogicalTypes.Boolean:
                    return v switch
                    {
                        bool => v,
                        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("t", StringComparison.OrdinalIgnoreCase),
                        byte[] bytes => bytes.Any(x => x != 0),
                        _ => Convert.ToDecimal(v, Inv) != 0
                    };
                case LogicalTypes.Text:
                    return v switch
                    {
                        string => v,
                        Guid g => g.ToString(),
                        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Inv),
                        bool b => b ? "true" : "false",
                        byte[] by => Convert.ToBase64String(by),
                        _ => Convert.ToString(v, Inv)
                    };
                case LogicalTypes.Date:
                    return v switch { DateTime d => d.Date, DateOnly d => d.ToDateTime(TimeOnly.MinValue), DateTimeOffset o => o.UtcDateTime.Date, string s => DateTime.Parse(s, Inv).Date, _ => v };
                case LogicalTypes.Time:
                    return v switch { TimeSpan => v, TimeOnly t => t.ToTimeSpan(), DateTime d => d.TimeOfDay, string s => TimeSpan.Parse(s, Inv), _ => v };
                case LogicalTypes.Timestamp:
                case LogicalTypes.TimestampTz:
                    {
                        DateTime dt = v switch
                        {
                            DateTime d => d,
                            DateTimeOffset o => o.UtcDateTime,
                            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                            string s => DateTimeOffset.Parse(s, Inv, DateTimeStyles.AssumeUniversal).UtcDateTime,
                            _ => Convert.ToDateTime(v, Inv)
                        };
                        if (target.LogicalType == LogicalTypes.TimestampTz) return dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                        return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                    }
                case LogicalTypes.Binary:
                    return v switch { byte[] => v, string s => Convert.FromBase64String(s), _ => v };
                case LogicalTypes.Json:
                    return v is string ? v : JsonSerializer.Serialize(v);
                case LogicalTypes.Uuid:
                    return v switch { Guid => v, string s => Guid.Parse(s), byte[] { Length: 16 } by => new Guid(by), _ => v };
                default:
                    return v;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new ConnectorException(ErrorClass.Permanent, "TYPE_CONVERSION",
                $"Gagal mengonversi nilai '{v}' ({v.GetType().Name}) ke kolom {target.Name} ({target.NativeType}): {ex.Message}", ex);
        }
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDecimal(),
        _ => e.GetRawText()
    };

    /// <summary>Ubah nilai menjadi bentuk yang aman untuk JSON (payload event, UI).</summary>
    public static object? ToJsonSafe(object? v) => v switch
    {
        null => null,
        byte[] b => "base64:" + Convert.ToBase64String(b),
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", Inv),
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        TimeSpan t => t.ToString(),
        Guid g => g.ToString(),
        decimal or double or float or long or int or short or sbyte or byte or ushort or uint or ulong or bool or string => v,
        _ => v.ToString()
    };
}
