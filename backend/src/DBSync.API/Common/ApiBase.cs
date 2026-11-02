using System.Security.Claims;
using DBSync.Application;
using DBSync.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DBSync.API.Common;

public sealed record ApiError(string Code, string? Field, string Message);

public static class Policies
{
    public const string Read = "Read";             // semua role terautentikasi
    public const string Operate = "Operate";       // Operator ke atas
    public const string Manage = "Manage";         // Administrator ke atas
    public const string Audit = "Audit";           // Auditor, Administrator, SuperAdmin
    public const string SuperAdmin = "SuperAdmin";

    public static void Register(AuthorizationOptions o)
    {
        o.AddPolicy(Read, p => p.RequireAuthenticatedUser());
        o.AddPolicy(Operate, p => p.RequireRole(Roles.SuperAdmin, Roles.Administrator, Roles.Operator));
        o.AddPolicy(Manage, p => p.RequireRole(Roles.SuperAdmin, Roles.Administrator));
        o.AddPolicy(Audit, p => p.RequireRole(Roles.SuperAdmin, Roles.Administrator, Roles.Auditor));
        o.AddPolicy(SuperAdmin, p => p.RequireRole(Roles.SuperAdmin));
    }
}

public class ApiException : Exception
{
    public int Status { get; }
    public List<ApiError> Errors { get; }
    public ApiException(int status, string code, string message, string? field = null) : base(message)
    { Status = status; Errors = new() { new ApiError(code, field, message) }; }
    public ApiException(int status, string message, List<ApiError> errors) : base(message) { Status = status; Errors = errors; }
}

[ApiController]
[Authorize(Policy = Policies.Read)]
public abstract class ApiBase : ControllerBase
{
    protected string TraceId => HttpContext.TraceIdentifier;
    protected Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;
    protected string? Username => User.FindFirstValue(ClaimTypes.Name);
    protected string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    protected IActionResult Success(object? data = null, string message = "OK") =>
        Ok(new { success = true, message, data, traceId = TraceId });

    protected IActionResult Accepted202(object data, string message = "Operation accepted") =>
        StatusCode(202, new { success = true, message, data, traceId = TraceId });

    protected IActionResult Created201(object data, string message = "Created") =>
        StatusCode(201, new { success = true, message, data, traceId = TraceId });

    protected IActionResult Paged<T>(IReadOnlyList<T> items, int total, int page, int pageSize) =>
        Success(new { items, total, page, pageSize });

    protected static (int Page, int Size) Page(int? page, int? size) => (Math.Max(1, page ?? 1), Math.Clamp(size ?? 25, 1, 200));

    protected IAuditService Audit => HttpContext.RequestServices.GetRequiredService<IAuditService>();
    protected Task Log(string action, string resourceType, string? id, object? before = null, object? after = null) =>
        Audit.WriteAsync(UserId, Username, action, resourceType, id, before, after, Ip);
}
