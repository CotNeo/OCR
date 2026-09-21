using System.Net;
using Microsoft.Extensions.Options;
using Polly;
using TaxCertificate.Infrastructure.Ocr;

namespace TaxCertificate.Api.Configuration;

public static class OcrHttpClientExtensions
{
    /// <summary>
    /// Adds a deliberately narrow retry policy for the OCR client.
    /// <para>
    /// OCR inference is CPU-bound and takes seconds, so a retry storm is the fastest way to
    /// exhaust an 8 GB M1. Hence: a low cap (default 1 attempt), a linear delay rather than a
    /// long exponential backoff, and three explicit exclusions.
    /// </para>
    /// <list type="bullet">
    ///   <item><b>503</b> is not retried. The service returns it for SERVER_BUSY, meaning the
    ///   queue is already full - retrying immediately only deepens the queue.</item>
    ///   <item><b>4xx</b> is not retried: a rejected file type or oversized upload will be
    ///   rejected again.</item>
    ///   <item><b>Client-side timeouts</b> are not retried. HttpClient.Timeout surfaces as a
    ///   cancelled task, and repeating a request that already burned the full timeout would
    ///   double the wall clock for no benefit.</item>
    /// </list>
    /// Note the method name is intentionally distinct from Polly's own
    /// <c>AddPolicyHandler</c>: an extension with an identical signature in this class would
    /// shadow the library method and recurse into itself.
    /// </summary>
    public static IHttpClientBuilder AddOcrRetryPolicy(this IHttpClientBuilder builder)
        => builder.AddPolicyHandler((serviceProvider, request) =>
        {
            // Health probes must fail fast. They run on a timer, and a retry with its delay
            // would make every probe against a down service take seconds instead of
            // milliseconds - turning a liveness signal into a latency source.
            if (request.RequestUri is not null &&
                request.RequestUri.AbsolutePath.EndsWith("/health", StringComparison.Ordinal))
            {
                return Policy.NoOpAsync<HttpResponseMessage>();
            }

            var options = serviceProvider.GetRequiredService<IOptions<OcrOptions>>().Value;
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("TaxCertificate.Api.OcrRetry");

            var retries = Math.Clamp(options.MaxRetries, 0, 3);
            var baseDelay = TimeSpan.FromMilliseconds(Math.Max(0, options.RetryBaseDelayMilliseconds));

            if (retries == 0)
            {
                return Policy.NoOpAsync<HttpResponseMessage>();
            }

            return Policy<HttpResponseMessage>
                // Connection refused / reset: the service may still be starting up.
                .Handle<HttpRequestException>()
                .OrResult(response =>
                    response.StatusCode == HttpStatusCode.RequestTimeout ||
                    ((int)response.StatusCode >= 500 &&
                     response.StatusCode != HttpStatusCode.ServiceUnavailable))
                .WaitAndRetryAsync(
                    retries,
                    attempt => baseDelay * attempt,
                    (outcome, delay, attempt, _) => logger.LogWarning(
                        "Retrying OCR request (attempt {Attempt}/{MaxAttempts}) after {DelayMs} ms: {Reason}",
                        attempt, retries, delay.TotalMilliseconds,
                        outcome.Exception?.GetType().Name
                        ?? ((int?)outcome.Result?.StatusCode)?.ToString()
                        ?? "unknown"));
        });
}
