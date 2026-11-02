using DBSync.Connectors.Abstractions;
using DBSync.Domain.Entities;

namespace DBSync.SyncEngine.Validation;

public sealed record ValidationIssue(string Severity, string Code, string Message, string? Field = null);

public sealed record ValidationReport(string Status, List<ValidationIssue> Issues)
{
    public static ValidationReport From(List<ValidationIssue> issues) =>
        new(issues.Any(i => i.Severity == "error") ? "INVALID" : issues.Any(i => i.Severity == "warning") ? "WARNING" : "VALID", issues);
}

public static class ColumnMappingBuilder
{
    /// <summary>Petakan kolom bernama sama (case-insensitive). Primary key ditandai dari sisi A.</summary>
    public static List<SyncColumnMapping> AutoMap(TableSchema a, TableSchema b)
    {
        var list = new List<SyncColumnMapping>();
        foreach (var ca in a.Columns.OrderBy(c => c.Ordinal))
        {
            var cb = b.Find(ca.Name);
            if (cb is null) continue;
            list.Add(new SyncColumnMapping { EndpointAColumn = ca.Name, EndpointBColumn = cb.Name, IsPrimaryKey = ca.IsPrimaryKey && cb.IsPrimaryKey });
        }
        return list;
    }
}

public static class RuleValidator
{
    private static string Group(string t) => t switch
    {
        LogicalTypes.Integer or LogicalTypes.Decimal or LogicalTypes.Float or LogicalTypes.Boolean => "numeric",
        LogicalTypes.Date or LogicalTypes.Time or LogicalTypes.Timestamp or LogicalTypes.TimestampTz => "temporal",
        LogicalTypes.Binary => "binary",
        LogicalTypes.Uuid => "uuid",
        LogicalTypes.Json => "json",
        LogicalTypes.Text => "text",
        _ => "other"
    };

    public static ValidationReport Validate(TableSchema a, TableSchema b, IReadOnlyList<SyncColumnMapping> mappings)
    {
        var issues = new List<ValidationIssue>();
        void Err(string code, string msg, string? f = null) => issues.Add(new("error", code, msg, f));
        void Warn(string code, string msg, string? f = null) => issues.Add(new("warning", code, msg, f));

        if (a.PrimaryKey.Count == 0) Err("PRIMARY_KEY_REQUIRED", $"Tabel A {a.Schema}.{a.Name} tidak memiliki primary key.", "table");
        if (b.PrimaryKey.Count == 0) Err("PRIMARY_KEY_REQUIRED", $"Tabel B {b.Schema}.{b.Name} tidak memiliki primary key.", "table");

        var enabled = mappings.Where(m => m.IsEnabled).ToList();
        if (enabled.Count == 0) Err("NO_COLUMNS", "Tidak ada kolom yang dipetakan.", "columns");

        var pkMapped = enabled.Where(m => m.IsPrimaryKey).Select(m => m.EndpointAColumn.ToLowerInvariant()).ToHashSet();
        var pkA = a.PrimaryKey.Select(c => c.Name.ToLowerInvariant()).ToHashSet();
        if (pkMapped.Count == 0 && pkA.Count > 0) Err("PRIMARY_KEY_NOT_MAPPED", "Primary key tidak termasuk dalam pemetaan kolom.", "columns");
        else if (pkA.Count > 0 && !pkMapped.SetEquals(pkA)) Err("PRIMARY_KEY_MISMATCH", "Primary key terpetakan harus sama dengan primary key tabel A (termasuk composite key).", "columns");
        var pkBMapped = enabled.Where(m => m.IsPrimaryKey).Select(m => m.EndpointBColumn.ToLowerInvariant()).ToHashSet();
        if (b.PrimaryKey.Count > 0 && !pkBMapped.SetEquals(b.PrimaryKey.Select(c => c.Name.ToLowerInvariant())))
            Err("PRIMARY_KEY_MISMATCH_B", "Primary key terpetakan harus sama dengan primary key tabel B.", "columns");

        foreach (var m in enabled)
        {
            var ca = a.Find(m.EndpointAColumn); var cb = b.Find(m.EndpointBColumn);
            if (ca is null) { Err("COLUMN_NOT_FOUND", $"Kolom A '{m.EndpointAColumn}' tidak ditemukan.", m.EndpointAColumn); continue; }
            if (cb is null) { Err("COLUMN_NOT_FOUND", $"Kolom B '{m.EndpointBColumn}' tidak ditemukan.", m.EndpointBColumn); continue; }

            if (ca.LogicalType == LogicalTypes.Other)
                Err("UNSUPPORTED_TYPE", $"Tipe '{ca.NativeType}' pada A.{ca.Name} belum didukung (ENUM/SET/ARRAY/spatial/XML dll.). Nonaktifkan kolom atau ubah tipe.", ca.Name);
            if (cb.LogicalType == LogicalTypes.Other)
                Err("UNSUPPORTED_TYPE", $"Tipe '{cb.NativeType}' pada B.{cb.Name} belum didukung.", cb.Name);
            if (ca.LogicalType == LogicalTypes.Other || cb.LogicalType == LogicalTypes.Other) continue;

            var ga = Group(ca.LogicalType); var gb = Group(cb.LogicalType);
            if (ga != gb && !(gb == "text" || ga == "text"))
                Err("TYPE_INCOMPATIBLE", $"{ca.Name}: {ca.NativeType} ({ga}) tidak kompatibel dengan {cb.NativeType} ({gb}).", ca.Name);
            else if (ga != gb)
                Warn("TYPE_LOSSY", $"{ca.Name}: konversi {ca.NativeType} ↔ {cb.NativeType} dilakukan via teks; periksa format.", ca.Name);

            if (ca.LogicalType == LogicalTypes.Decimal && cb.LogicalType == LogicalTypes.Decimal && (ca.Scale ?? 0) > (cb.Scale ?? 0))
                Warn("PRECISION_LOSS", $"{ca.Name}: scale A ({ca.Scale}) > scale B ({cb.Scale}); presisi dapat hilang.", ca.Name);
            if (ca.LogicalType == LogicalTypes.Text && cb.LogicalType == LogicalTypes.Text && ca.MaxLength is > 0 && cb.MaxLength is > 0 && ca.MaxLength > cb.MaxLength)
                Warn("LENGTH_TRUNCATION", $"{ca.Name}: panjang A ({ca.MaxLength}) > B ({cb.MaxLength}).", ca.Name);
            if (ca.IsUnsigned && cb.LogicalType == LogicalTypes.Integer)
                Warn("UNSIGNED", $"{ca.Name}: unsigned integer pada A dapat melebihi rentang B.", ca.Name);
            if (ca.IsNullable && !cb.IsNullable)
                Warn("NULLABILITY", $"{ca.Name}: nullable di A tetapi NOT NULL di B.", ca.Name);
            if (ca.IsGenerated || cb.IsGenerated)
                Warn("GENERATED_COLUMN", $"{ca.Name}: kolom generated/computed tidak akan ditulis ke sisi yang mengelolanya sendiri.", ca.Name);
            if ((ca.LogicalType == LogicalTypes.Timestamp || ca.LogicalType == LogicalTypes.TimestampTz) && ca.LogicalType != cb.LogicalType)
                Warn("TIMEZONE", $"{ca.Name}: timestamp dengan/tanpa zona waktu berbeda antara A dan B.", ca.Name);
        }

        foreach (var fk in a.ForeignKeys)
            Warn("FOREIGN_KEY", $"A memiliki foreign key {fk.Name} → {fk.RefSchema}.{fk.RefTable}; tabel induk harus ikut disinkronkan (urutan dependensi diatur otomatis).");
        foreach (var c in b.Columns.Where(c => c.IsIdentity))
            if (enabled.Any(m => m.EndpointBColumn.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                Warn("IDENTITY_PRESERVE", $"B.{c.Name} adalah identity/auto-increment; nilai dari sumber dipertahankan (IDENTITY_INSERT/OVERRIDING). Sequence tidak otomatis di-advance.", c.Name);
        return ValidationReport.From(issues);
    }
}
