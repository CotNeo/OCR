using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaxCertificate.Application.Interfaces;

namespace TaxCertificate.Api.Services;

/// <summary>Reports the API as degraded when the local OCR service has no model loaded.</summary>
public sealed class OcrServiceHealthCheck : IHealthCheck
{
    private readonly IOcrClient _ocrClient;

    public OcrServiceHealthCheck(IOcrClient ocrClient) => _ocrClient = ocrClient;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _ocrClient.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

            // The two failure modes are worth distinguishing: "not started" is an operator
            // action, "started but no model" usually means a failed model download.
            return status switch
            {
                { IsHealthy: true } => HealthCheckResult.Healthy("OCR service is ready."),
                { Reachable: false } => HealthCheckResult.Unhealthy("OCR service is unreachable."),
                _ => HealthCheckResult.Unhealthy("OCR service is up but reports no loaded model."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Message only, never the stack: health output can be exposed publicly.
            return HealthCheckResult.Unhealthy("OCR service is unreachable.", ex);
        }
    }
}
