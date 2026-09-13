using System.Diagnostics;

namespace SeoLoodoi.Api.Middleware;

/// <summary>
/// One structured access log per request — method, path, status, duration,
/// and trace id. Bodies and headers are deliberately never read so secrets
/// (Authorization, API keys) can never leak into the logs. 5xx responses log
/// at Error and 4xx at Warning so they stand out in log alerting.
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var status = context.Response.StatusCode;
            var level = status >= StatusCodes.Status500ServiceUnavailable
                ? LogLevel.Error
                : status >= StatusCodes.Status400BadRequest ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "HTTP {Method} {Path} responded {Status} in {ElapsedMs} ms; trace={TraceId}",
                context.Request.Method, context.Request.Path.Value, status, stopwatch.ElapsedMilliseconds, context.TraceIdentifier);
        }
    }
}
