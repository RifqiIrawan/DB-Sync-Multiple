using DBSync.API.Common;
using DBSync.Application;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBSync.API.Controllers;

public record CreateUserRequest(string Username, string Email, string Password, string[] Roles);
public record UpdateUserRequest(string? Email, bool? IsActive, string[]? Roles, string? NewPassword);

[Route("api/v1/users"), Authorize(Policy = Policies.SuperAdmin)]
public class UsersController : ApiBase
{
    private readonly DbSyncContext _db;
    private readonly IPasswordHasher _hasher;
    public UsersController(DbSyncContext db, IPasswordHasher hasher) { _db = db; _hasher = hasher; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Success(await _db.Users.AsNoTracking().OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.Email, u.IsActive, u.CreatedAt, u.LastLoginAt, roles = u.UserRoles.Select(r => r.Role!.Name) }).ToListAsync(ct));

    [HttpGet("/api/v1/roles")]
    public async Task<IActionResult> RolesList(CancellationToken ct) =>
        Success(await _db.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => new { r.Id, r.Name, r.Description }).ToListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username)) throw new ApiException(400, "REQUIRED", "Username wajib.", "username");
        if (req.Password.Length < 10) throw new ApiException(400, "WEAK_PASSWORD", "Password minimal 10 karakter.", "password");
        if (await _db.Users.AnyAsync(u => u.Username == req.Username, ct)) throw new ApiException(409, "DUPLICATE", "Username sudah dipakai.", "username");
        var roles = await _db.Roles.Where(r => req.Roles.Contains(r.Name)).ToListAsync(ct);
        if (roles.Count == 0 || roles.Count != req.Roles.Distinct().Count()) throw new ApiException(400, "INVALID_ROLE", "Role tidak valid.", "roles");
        var u = new User { Username = req.Username.Trim(), Email = req.Email, PasswordHash = _hasher.Hash(req.Password) };
        foreach (var r in roles) u.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = r.Id });
        _db.Users.Add(u); await _db.SaveChangesAsync(ct);
        await Log("CREATE_USER", "User", u.Id.ToString(), null, new { u.Username, u.Email, roles = req.Roles });
        return Created201(new { u.Id, u.Username });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest req, CancellationToken ct)
    {
        var u = await _db.Users.Include(x => x.UserRoles).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("User tidak ditemukan.");
        if (id == UserId && req.IsActive == false) throw new ApiException(400, "SELF_DISABLE", "Tidak dapat menonaktifkan akun sendiri.");
        var before = new { u.Email, u.IsActive, roles = u.UserRoles.Select(r => r.RoleId) };
        if (req.Email != null) u.Email = req.Email;
        if (req.IsActive != null) u.IsActive = req.IsActive.Value;
        if (!string.IsNullOrEmpty(req.NewPassword))
        {
            if (req.NewPassword.Length < 10) throw new ApiException(400, "WEAK_PASSWORD", "Password minimal 10 karakter.", "newPassword");
            u.PasswordHash = _hasher.Hash(req.NewPassword);
        }
        if (req.Roles != null)
        {
            var roles = await _db.Roles.Where(r => req.Roles.Contains(r.Name)).ToListAsync(ct);
            if (roles.Count == 0) throw new ApiException(400, "INVALID_ROLE", "Minimal satu role valid.", "roles");
            _db.UserRoles.RemoveRange(u.UserRoles);
            foreach (var r in roles) _db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = r.Id });
        }
        await _db.SaveChangesAsync(ct);
        await Log("UPDATE_USER", "User", id.ToString(), before, new { req.Email, req.IsActive, req.Roles });
        return Success(null, "User diperbarui");
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (id == UserId) throw new ApiException(400, "SELF_DELETE", "Tidak dapat menghapus akun sendiri.");
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("User tidak ditemukan.");
        _db.Users.Remove(u); await _db.SaveChangesAsync(ct);
        await Log("DELETE_USER", "User", id.ToString(), new { u.Username });
        return Success(null, "User dihapus");
    }
}
