using System.Text.Json;
using DBSync.Connectors.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DBSync.API.Common;

/// <summary>Mengubah exception menjadi respons error standar {success:false, message, errors[], traceId}. Tidak membocorkan stack trace/secret.</summary>
public sealed class ErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorMiddleware> _log;
    public ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log) { _next = next; _log = log; }

    public async Task Invoke(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            int status; string message; List<ApiError> errors;
            switch (ex)
            {
                case ApiException a: status = a.Status; message = a.Message; errors = a.Errors; break;
                case KeyNotFoundException k: status = 404; message = k.Message; errors = new() { new("NOT_FOUND", null, k.Message) }; break;
                case ConnectorException c: status = 422; message = "Operasi database gagal"; errors = new() { new(c.Code, null, c.Message) }; break;
                case InvalidOperationException io: status = 409; message = io.Message; errors = new() { new("INVALID_OPERATION", null, io.Message) }; break;
                case DbUpdateException du:
                    _log.LogError(du, "DB update error");
                    status = 409; message = "Konflik data pada penyimpanan"; errors = new() { new("DB_CONFLICT", null, du.InnerException?.Message ?? du.Message) }; break;
                case OperationCanceledException: status = 499; message = "Dibatalkan"; errors = new() { new("CANCELLED", null, "Request dibatalkan") }; break;
                default:
                    _log.LogError(ex, "Unhandled error {Trace}", ctx.TraceIdentifier);
                    status = 500; message = "Terjadi kesalahan internal"; errors = new() { new("INTERNAL_ERROR", null, "Hubungi administrator dengan traceId.") }; break;
            }
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message, errors, traceId = ctx.TraceIdentifier }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
}
