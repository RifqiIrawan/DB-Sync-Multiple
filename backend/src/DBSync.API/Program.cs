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
builder.Services.AddHostedService<MonitorBroadcaster>();

var origins = cfg.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:3000" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddHealthChecks().AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

var app = builder.Build();

app.UseMiddleware<ErrorMiddleware>();
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseCors();
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
