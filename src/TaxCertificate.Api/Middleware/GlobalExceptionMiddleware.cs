using System.Text.Json;
using TaxCertificate.Application.Dtos;

namespace TaxCertificate.Api.Middleware;

/// <summary>
/// Last line of defence: converts any unhandled exception into the standard error envelope.
/// The response never carries an exception message, type or stack trace; the full detail goes
/// to the log with the request id so it stays correlatable.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away. Nothing to report, and writing a body would throw.
            _logger.LogInformation("Request {TraceId} aborted by the client", context.TraceIdentifier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception for request {TraceId}", context.TraceIdentifier);

            if (context.Response.HasStarted)
            {
                // Headers are already on the wire; the connection must simply be dropped.
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var payload = AnalysisResult.Failure(
                ErrorCodes.InternalError, "Beklenmeyen bir hata oluştu.");

            await context.Response
                .WriteAsync(JsonSerializer.Serialize(payload, JsonOptions))
                .ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
