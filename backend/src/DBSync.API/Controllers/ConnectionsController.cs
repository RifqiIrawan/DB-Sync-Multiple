using System.Text.Json;
using DBSync.API.Common;
using DBSync.Application;
using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBSync.API.Controllers;

public record ConnectionRequest(string ConnectionName, string DbType, string Host, int Port, string DatabaseName, string Username,
    string? Password, bool SslEnabled, Dictionary<string, string>? ConnectionOptions);

[Route("api/v1/connections")]
public class ConnectionsController : ApiBase
{
    private readonly DbSyncContext _db;
    private readonly ISecretProtector _protector;
    private readonly IConnectorRegistry _registry;
    private readonly IConnectionProfileFactory _profiles;

    public ConnectionsController(DbSyncContext db, ISecretProtector protector, IConnectorRegistry registry, IConnectionProfileFactory profiles)
    { _db = db; _protector = protector; _registry = registry; _profiles = profiles; }

    private static object Dto(DbConnection c, int usedBy = 0) => new
    {
        c.Id, c.ConnectionName, c.DbType, c.Host, c.Port, c.DatabaseName, c.Username, c.SslEnabled, c.ConnectionOptions,
        c.Status, c.LastTestMessage, c.LastTestedAt, c.CreatedAt, c.UpdatedAt, usedByGroups = usedBy, hasPassword = true
    };

    private static void Validate(ConnectionRequest r, bool requirePassword)
    {
        var errs = new List<ApiError>();
        if (string.IsNullOrWhiteSpace(r.ConnectionName)) errs.Add(new("REQUIRED", "connectionName", "Nama koneksi wajib."));
        if (!DbTypes.All.Contains(r.DbType?.ToUpperInvariant())) errs.Add(new("INVALID_DB_TYPE", "dbType", "dbType harus MYSQL, POSTGRESQL, atau SQLSERVER."));
        if (string.IsNullOrWhiteSpace(r.Host)) errs.Add(new("REQUIRED", "host", "Host wajib."));
        if (r.Port is < 1 or > 65535) errs.Add(new("INVALID_PORT", "port", "Port tidak valid."));
        if (string.IsNullOrWhiteSpace(r.DatabaseName)) errs.Add(new("REQUIRED", "databaseName", "Nama database wajib."));
        if (string.IsNullOrWhiteSpace(r.Username)) errs.Add(new("REQUIRED", "username", "Username wajib."));
        if (requirePassword && string.IsNullOrEmpty(r.Password)) errs.Add(new("REQUIRED", "password", "Password wajib."));
        if (errs.Count > 0) throw new ApiException(400, "Validation failed", errs);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? dbType, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, s) = Page(page, pageSize);
        var q = _db.DbConnections.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(c => EF.Functions.ILike(c.ConnectionName, $"%{search}%") || EF.Functions.ILike(c.Host, $"%{search}%"));
        if (!string.IsNullOrWhiteSpace(dbType)) q = q.Where(c => c.DbType == dbType.ToUpper());
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(c => c.ConnectionName).Skip((p - 1) * s).Take(s).ToListAsync(ct);
        var used = await _db.SyncEndpoints.GroupBy(e => e.ConnectionId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return Paged(items.Select(c => Dto(c, used.GetValueOrDefault(c.Id))).ToList(), total, p, s);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await _db.DbConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Koneksi tidak ditemukan.");
        return Success(Dto(c, await _db.SyncEndpoints.CountAsync(e => e.ConnectionId == id, ct)));
    }

    [HttpPost, Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Create(ConnectionRequest r, CancellationToken ct)
    {
        Validate(r, true);
        var c = new DbConnection
        {
            ConnectionName = r.ConnectionName.Trim(), DbType = r.DbType.ToUpperInvariant(), Host = r.Host.Trim(), Port = r.Port, DatabaseName = r.DatabaseName.Trim(),
            Username = r.Username, SslEnabled = r.SslEnabled, ConnectionOptions = r.ConnectionOptions == null ? null : Json.ToDoc(r.ConnectionOptions),
        };
        c.Secret = new ConnectionSecret { ConnectionId = c.Id, EncryptedPassword = _protector.Protect(r.Password!), EncryptionKeyVersion = _protector.CurrentKeyVersion };
        _db.DbConnections.Add(c);
        await _db.SaveChangesAsync(ct);
        await Log("CREATE_CONNECTION", "DbConnection", c.Id.ToString(), null, new { c.ConnectionName, c.DbType, c.Host, c.Port, c.DatabaseName, c.Username, password = r.Password });
        return Created201(Dto(c));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Update(Guid id, ConnectionRequest r, CancellationToken ct)
    {
        Validate(r, false);
        var c = await _db.DbConnections.Include(x => x.Secret).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Koneksi tidak ditemukan.");
        var before = new { c.ConnectionName, c.DbType, c.Host, c.Port, c.DatabaseName, c.Username, c.SslEnabled };
        bool inUse = await _db.SyncEndpoints.AnyAsync(e => e.ConnectionId == id && e.SyncGroup!.Status == GroupStatus.Active, ct);
        if (inUse && (c.DbType != r.DbType.ToUpperInvariant())) throw new ApiException(409, "IN_USE", "Tipe database tidak dapat diubah saat dipakai group aktif.");
        c.ConnectionName = r.ConnectionName.Trim(); c.DbType = r.DbType.ToUpperInvariant(); c.Host = r.Host.Trim(); c.Port = r.Port;
        c.DatabaseName = r.DatabaseName.Trim(); c.Username = r.Username; c.SslEnabled = r.SslEnabled;
        c.ConnectionOptions = r.ConnectionOptions == null ? null : Json.ToDoc(r.ConnectionOptions);
        c.UpdatedAt = DateTimeOffset.UtcNow; c.Status = "UNKNOWN";
        if (!string.IsNullOrEmpty(r.Password))
        {
            c.Secret!.EncryptedPassword = _protector.Protect(r.Password); c.Secret.EncryptionKeyVersion = _protector.CurrentKeyVersion; c.Secret.RotatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        await Log("UPDATE_CONNECTION", "DbConnection", id.ToString(), before, new { r.ConnectionName, r.DbType, r.Host, r.Port, r.DatabaseName, r.Username, passwordChanged = !string.IsNullOrEmpty(r.Password) });
        return Success(Dto(c));
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var c = await _db.DbConnections.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Koneksi tidak ditemukan.");
        if (await _db.SyncEndpoints.AnyAsync(e => e.ConnectionId == id, ct)) throw new ApiException(409, "IN_USE", "Koneksi sedang dipakai synchronization group.");
        _db.DbConnections.Remove(c); await _db.SaveChangesAsync(ct);
        await Log("DELETE_CONNECTION", "DbConnection", id.ToString(), new { c.ConnectionName });
        return Success(null, "Koneksi dihapus");
    }

    /// <summary>Tes koneksi sebelum disimpan (untuk wizard Add Connection).</summary>
    [HttpPost("test"), Authorize(Policy = Policies.Manage)]
    public async Task<IActionResult> TestRaw(ConnectionRequest r, CancellationToken ct)
    {
        Validate(r, true);
        var profile = _profiles.FromRaw(r.DbType.ToUpperInvariant(), r.Host, r.Port, r.DatabaseName, r.Username, r.Password!, r.SslEnabled, r.ConnectionOptions);
        return Success(await _registry.Get(profile.DbType).TestConnectionAsync(profile, ct));
    }

    [HttpPost("{id:guid}/test"), Authorize(Policy = Policies.Operate)]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var c = await _db.DbConnections.Include(x => x.Secret).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Koneksi tidak ditemukan.");
        var res = await _registry.Get(c.DbType).TestConnectionAsync(_profiles.Create(c), ct);
        c.Status = res.Success ? "CONNECTED" : "DISCONNECTED"; c.LastTestedAt = DateTimeOffset.UtcNow; c.LastTestMessage = res.Message.Length > 500 ? res.Message[..500] : res.Message;
        await _db.SaveChangesAsync(ct);
        return Success(res, res.Success ? "Koneksi berhasil" : "Koneksi gagal");
    }

    [HttpGet("{id:guid}/schemas")]
    public async Task<IActionResult> Schemas(Guid id, CancellationToken ct)
    {
        var p = await _profiles.CreateAsync(id, 60, ct);
        return Success(await _registry.Get(p.DbType).GetSchemasAsync(p, ct));
    }

    [HttpGet("{id:guid}/tables")]
    public async Task<IActionResult> Tables(Guid id, [FromQuery] string? schema, CancellationToken ct)
    {
        var p = await _profiles.CreateAsync(id, 60, ct);
        return Success(await _registry.Get(p.DbType).GetTablesAsync(p, schema, ct));
    }

    [HttpGet("{id:guid}/tables/{table}/schema")]
    public async Task<IActionResult> TableSchema(Guid id, string table, [FromQuery] string? schema, CancellationToken ct)
    {
        var p = await _profiles.CreateAsync(id, 60, ct);
        var c = _registry.Get(p.DbType);
        schema ??= DefaultSchema(p);
        return Success(await c.GetTableSchemaAsync(p, schema, table, ct));
    }

    public static string DefaultSchema(ConnectionProfile p) => p.DbType switch { DbTypes.MySql => p.Database, DbTypes.PostgreSql => "public", _ => "dbo" };
}
