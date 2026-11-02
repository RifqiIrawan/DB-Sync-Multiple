using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DBSync.API.Common;
using DBSync.Application;
using DBSync.Domain;
using DBSync.Domain.Entities;
using DBSync.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DBSync.API.Controllers;

public record LoginRequest(string Username, string Password);
public record RefreshRequest(string RefreshToken);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

[Route("api/v1/auth")]
public class AuthController : ApiBase
{
    private readonly DbSyncContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly JwtOptions _jwt;

    public AuthController(DbSyncContext db, IPasswordHasher hasher, IOptions<JwtOptions> jwt) { _db = db; _hasher = hasher; _jwt = jwt.Value; }

    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await _db.Users.Include(u => u.UserRoles).ThenInclude(r => r.Role).FirstOrDefaultAsync(u => u.Username == req.Username, ct);
        if (user == null || !user.IsActive || !_hasher.Verify(req.Password, user.PasswordHash))
        {
            await Audit.WriteAsync(user?.Id, req.Username, "LOGIN_FAILED", "User", user?.Id.ToString(), ip: Ip, ct: ct);
            throw new ApiException(401, "INVALID_CREDENTIALS", "Username atau password salah.");
        }
        user.LastLoginAt = DateTimeOffset.UtcNow;
        var tokens = await IssueAsync(user, ct);
        await Audit.WriteAsync(user.Id, user.Username, "LOGIN", "User", user.Id.ToString(), ip: Ip, ct: ct);
        return Success(tokens);
    }

    [AllowAnonymous, HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest req, CancellationToken ct)
    {
        var hash = Hash(req.RefreshToken);
        var rt = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (rt == null || rt.RevokedAt != null || rt.ExpiresAt < DateTimeOffset.UtcNow) throw new ApiException(401, "INVALID_REFRESH", "Refresh token tidak valid.");
        var user = await _db.Users.Include(u => u.UserRoles).ThenInclude(r => r.Role).FirstAsync(u => u.Id == rt.UserId, ct);
        if (!user.IsActive) throw new ApiException(401, "USER_INACTIVE", "User nonaktif.");
        rt.RevokedAt = DateTimeOffset.UtcNow; // rotasi
        return Success(await IssueAsync(user, ct));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest req, CancellationToken ct)
    {
        var hash = Hash(req.RefreshToken);
        var rt = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash && r.UserId == UserId, ct);
        if (rt != null) { rt.RevokedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
        await Log("LOGOUT", "User", UserId?.ToString());
        return Success(null, "Logout berhasil");
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var u = await _db.Users.Include(x => x.UserRoles).ThenInclude(r => r.Role).FirstAsync(x => x.Id == UserId, ct);
        return Success(new { u.Id, u.Username, u.Email, roles = u.UserRoles.Select(r => r.Role!.Name) });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req, CancellationToken ct)
    {
        var u = await _db.Users.FirstAsync(x => x.Id == UserId, ct);
        if (!_hasher.Verify(req.CurrentPassword, u.PasswordHash)) throw new ApiException(400, "WRONG_PASSWORD", "Password saat ini salah.", "currentPassword");
        if (req.NewPassword.Length < 10) throw new ApiException(400, "WEAK_PASSWORD", "Password baru minimal 10 karakter.", "newPassword");
        u.PasswordHash = _hasher.Hash(req.NewPassword);
        await _db.RefreshTokens.Where(r => r.UserId == u.Id && r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow), ct);
        await _db.SaveChangesAsync(ct);
        await Log("CHANGE_PASSWORD", "User", u.Id.ToString());
        return Success(null, "Password diubah");
    }

    private async Task<object> IssueAsync(User user, CancellationToken ct)
    {
        var roles = user.UserRoles.Select(r => r.Role!.Name).ToList();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username), new(ClaimTypes.Email, user.Email) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var expires = DateTime.UtcNow.AddMinutes(_jwt.AccessTokenMinutes);
        var token = new JwtSecurityToken(_jwt.Issuer, _jwt.Audience, claims, expires: expires, signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        _db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = Hash(refresh), ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenDays) });
        await _db.SaveChangesAsync(ct);
        return new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresAt = expires, refreshToken = refresh, user = new { user.Id, user.Username, user.Email, roles } };
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
