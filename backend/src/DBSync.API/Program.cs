using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBSync.API.Common;
using DBSync.API.Hubs;
using DBSync.Application;
using DBSync.Connectors.Abstractions;
using DBSync.Connectors.MySql;
using DBSync.Connectors.PostgreSql;
using DBSync.Connectors.SqlServer;
using DBSync.Infrastructure;
using DBSync.Scheduler;
using DBSync.SyncEngine;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddDbSyncInfrastructure(cfg);
builder.Services.AddSingleton<IDatabaseConnector, MySqlDatabaseConnector>();
builder.Services.AddSingleton<IDatabaseConnector, PostgreSqlDatabaseConnector>();
builder.Services.AddSingleton<IDatabaseConnector, SqlServerDatabaseConnector>();
builder.Services.AddDbSyncEngine();

// Worker di dalam proses API (praktis untuk development). Produksi: jalankan DBSync.Worker terpisah & set Worker:Embedded=false.
if (cfg.GetValue("Worker:Embedded", false))
    builder.Services.AddDbSyncWorkers().AddDbSyncScheduler();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ctx =>
{
    var errs = ctx.ModelState.Where(m => m.Value!.Errors.Count > 0)
        .SelectMany(m => m.Value!.Errors.Select(e => new ApiError("INVALID_REQUEST", m.Key, string.IsNullOrEmpty(e.ErrorMessage) ? "Nilai tidak valid" : e.ErrorMessage))).ToList();
    return new Microsoft.AspNetCore.Mvc.ObjectResult(new { success = false, message = "Validation failed", errors = errs, traceId = ctx.HttpContext.TraceIdentifier }) { StatusCode = 400 };
});

var jwt = cfg.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey wajib diisi minimal 32 karakter (lihat PANDUAN_SETUP.md).");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
    };
    o.Events = new JwtBearerEvents
    {
        // SignalR mengirim token via query string
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization(Policies.Register);
builder.Services.AddSignalR();
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// Batasi percobaan login/refresh per IP (mencegah brute force / credential stuffing).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetSlidingWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new SlidingWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    o.OnRejected = async (c, ct) =>
    {
        c.HttpContext.Response.Headers.RetryAfter = "60";
        c.HttpContext.Response.ContentType = "application/json";
        await c.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            success = false, message = "Terlalu banyak percobaan. Coba lagi dalam 1 menit.",
            errors = new[] { new { code = "RATE_LIMITED", field = (string?)null, message = "Terlalu banyak percobaan login." } },
            traceId = c.HttpContext.TraceIdentifier
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);
    };
});
builder.Services.AddHostedService<MonitorBroadcaster>();

var origins = cfg.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:3000" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddHealthChecks().AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

var app = builder.Build();

app.UseMiddleware<ErrorMiddleware>();
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
if (!app.Environment.IsDevelopment()) app.UseHsts();
// header keamanan untuk semua respons API
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        h["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    });
    await next();
});
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<MonitorHub>("/hubs/monitor");
app.MapHealthChecks("/health");
app.MapGet("/health/db", async (DbSyncContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503)).AllowAnonymous();

// siapkan database konfigurasi + seed role & admin awal
for (int attempt = 1; ; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().RunAsync();
        break;
    }
    catch (Exception ex) when (attempt < 10)
    {
        app.Logger.LogWarning("Database konfigurasi belum siap ({Msg}); coba lagi {A}/10", ex.Message, attempt);
        await Task.Delay(3000);
    }
}

app.Run();

public partial class Program { }
